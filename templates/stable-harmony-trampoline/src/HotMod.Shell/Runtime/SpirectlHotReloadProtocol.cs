using System.Text.Json.Serialization;

namespace HotMod.Shell.Runtime;

public static class SpirectlHotReloadProtocol
{
    public static string DescribeSpirectlHotReloadStatusJson()
    {
        return HotRuntime.Current.DescribeSpirectlHotReloadStatusJson();
    }

    public static Task<string> RequestSpirectlHotReloadJsonAsync(string requestJson)
    {
        return HotRuntime.Current.RequestSpirectlHotReloadJsonAsync(requestJson);
    }
}

public sealed record SpirectlHotReloadProtocolInfo(string Id, int Version);

public sealed record SpirectlHotReloadStatus(
    SpirectlHotReloadProtocolInfo Protocol,
    string ShellModId,
    int ActiveGeneration,
    string ExpectedLogicArtifactPath,
    int ContractVersion,
    bool ReloadInProgress,
    ReloadReport? LastReloadReport,
    bool RestartRequired);

public sealed record SpirectlHotReloadRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("shellModId")] string ShellModId,
    [property: JsonPropertyName("logicArtifactPath")] string LogicArtifactPath,
    [property: JsonPropertyName("expectedContractVersion")] int ExpectedContractVersion,
    [property: JsonPropertyName("waitForCompletion")] bool WaitForCompletion,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs);

public sealed record SpirectlHotReloadResponse(
    bool Accepted,
    SpirectlHotReloadStatus Status,
    ReloadReport? Report,
    IReadOnlyList<SpirectlHotReloadNotice> Notices);

public sealed record SpirectlHotReloadNotice(string Code, string Message);
