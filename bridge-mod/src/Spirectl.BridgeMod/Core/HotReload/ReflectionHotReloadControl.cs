using System.Reflection;
using System.Text.Json;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Core.HotReload;


public sealed class ReflectionHotReloadControl : IHotReloadControl
{
    private const string ProtocolId = "spirectl.m57.hot-reload-shell";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogStream _logStream;

    public ReflectionHotReloadControl(ILogStream logStream)
    {
        _logStream = logStream;
    }

    public HotReloadStatusResultSnapshot GetStatus(HotReloadStatusRequestSnapshot request)
    {
        var shell = FindShell(request.ShellModId);
        if (shell.Error is not null)
        {
            return HotReloadStatusResultSnapshot.Success(
                UnsupportedStatus(request.ShellModId, shell.Error),
                [new HotReloadNoticeSnapshot(shell.Error.Code.Replace('_', '-'), shell.Error.Message)]);
        }

        var status = shell.Status!;
        _logStream.Write(BridgeLogLevel.Info, "bridge.hot_reload", $"status inspected for shell {status.ShellModId}");
        return HotReloadStatusResultSnapshot.Success(status, status.Notices);
    }

    public async Task<HotReloadOperationResult> RequestReloadAsync(HotReloadRequestSnapshot request)
    {
        var shell = FindShell(request.ShellModId);
        if (shell.Error is not null)
        {
            return HotReloadOperationResult.Failure(shell.Error.Code, shell.Error.Message);
        }

        _logStream.Write(BridgeLogLevel.Info, "bridge.hot_reload", $"reload requested with {request.RequestId}");
        var requestJson = JsonSerializer.Serialize(
            new HotReloadShellRequestJson(
                request.RequestId,
                request.ProjectId,
                request.ShellModId,
                request.LogicArtifactPath,
                request.ExpectedContractVersion,
                request.WaitForCompletion,
                request.TimeoutMs),
            JsonOptions);
        var responseJson = await shell.RequestMethod!.InvokeRequestAsync(requestJson).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<HotReloadShellResponseJson>(responseJson, JsonOptions);
        if (response is null)
        {
            return HotReloadOperationResult.Failure("hot_reload_shell_invalid_response", "Hot-reload shell returned an empty response.");
        }

        var status = response.Status is null ? shell.Status! : ToSnapshot(response.Status);
        var notices = response.Notices ?? [];
        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.hot_reload",
            $"reload completed with status {response.Report?.Status ?? "unknown"} generation {response.Report?.Generation ?? 0} error {response.Report?.Error?.Code ?? "none"}");
        return HotReloadOperationResult.Success(status, response.Report, response.Accepted, notices);
    }

    private static ShellLookup FindShell(string shellModId)
    {
        var matches = new List<ShellCandidate>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                var describe = type.GetMethod("DescribeSpirectlHotReloadStatusJson", BindingFlags.Public | BindingFlags.Static);
                var request = type.GetMethod("RequestSpirectlHotReloadJsonAsync", BindingFlags.Public | BindingFlags.Static);
                if (describe is null || request is null || describe.ReturnType != typeof(string))
                {
                    continue;
                }

                var statusJson = describe.Invoke(null, null) as string;
                if (string.IsNullOrWhiteSpace(statusJson))
                {
                    continue;
                }

                var status = JsonSerializer.Deserialize<HotReloadShellStatusJson>(statusJson, JsonOptions);
                if (status?.Protocol is null
                    || !string.Equals(status.Protocol.Id, ProtocolId, StringComparison.Ordinal)
                    || status.Protocol.Version != 0)
                {
                    continue;
                }

                var snapshot = ToSnapshot(status);
                if (string.Equals(snapshot.ShellModId, shellModId, StringComparison.Ordinal))
                {
                    matches.Add(new ShellCandidate(snapshot, request));
                }
            }
        }

        return matches.Count switch
        {
            0 => new ShellLookup(null, null, new HotReloadFailureSnapshot("hot_reload_shell_not_running", "No loaded M57-compatible hot-reload shell matched the requested shellModId.")),
            1 => new ShellLookup(matches[0].Status, matches[0].RequestMethod, null),
            _ => new ShellLookup(null, null, new HotReloadFailureSnapshot("hot_reload_shell_mismatch", "Multiple loaded hot-reload shells matched the requested shellModId.")),
        };
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    private static HotReloadShellStatusSnapshot ToSnapshot(HotReloadShellStatusJson status)
    {
        return new HotReloadShellStatusSnapshot(
            Supported: true,
            Protocol: status.Protocol,
            ShellModId: status.ShellModId ?? string.Empty,
            ShellProtocolVersion: status.Protocol?.Version ?? 0,
            ActiveGeneration: status.ActiveGeneration,
            ExpectedLogicArtifactPath: status.ExpectedLogicArtifactPath ?? string.Empty,
            ContractVersion: status.ContractVersion,
            ReloadInProgress: status.ReloadInProgress,
            LastReloadReport: status.LastReloadReport,
            RestartRequired: status.RestartRequired,
            Notices: []);
    }

    private static HotReloadShellStatusSnapshot UnsupportedStatus(string shellModId, HotReloadFailureSnapshot failure)
    {
        return new HotReloadShellStatusSnapshot(
            Supported: false,
            Protocol: new HotReloadProtocolSnapshot(ProtocolId, 0),
            ShellModId: shellModId,
            ShellProtocolVersion: 0,
            ActiveGeneration: 0,
            ExpectedLogicArtifactPath: string.Empty,
            ContractVersion: 0,
            ReloadInProgress: false,
            LastReloadReport: null,
            RestartRequired: false,
            Notices: [new HotReloadNoticeSnapshot(failure.Code.Replace('_', '-'), failure.Message)]);
    }

    private sealed record ShellCandidate(HotReloadShellStatusSnapshot Status, MethodInfo RequestMethod);

    private sealed record ShellLookup(
        HotReloadShellStatusSnapshot? Status,
        MethodInfo? RequestMethod,
        HotReloadFailureSnapshot? Error);
}

internal static class HotReloadReflectionMethodInfoExtensions
{
    public static async Task<string> InvokeRequestAsync(this MethodInfo method, string requestJson)
    {
        var result = method.Invoke(null, [requestJson]);
        return result switch
        {
            Task<string> task => await task.ConfigureAwait(false),
            string value => value,
            _ => throw new InvalidOperationException("Hot-reload request method returned an unsupported result type."),
        };
    }
}
