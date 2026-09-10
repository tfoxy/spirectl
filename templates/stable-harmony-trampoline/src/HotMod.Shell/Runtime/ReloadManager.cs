using System.Diagnostics;
using System.Reflection;
using HotMod.Contracts;

namespace HotMod.Shell.Runtime;

public sealed class ReloadManager
{
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromMilliseconds(250);

    private readonly IHotHost _host;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public ReloadManager(IHotHost host)
    {
        _host = host;
    }

    public async Task<ReloadOutcome> ReloadAsync(ReloadRequest request, ReloadGeneration? currentGeneration)
    {
        var requestedAt = _host.Now;
        var stopwatch = Stopwatch.StartNew();

        if (!_reloadGate.Wait(0))
        {
            return new ReloadOutcome(
                ReloadReport.Busy(requestedAt, request.SourceAssemblyPath, currentGeneration?.Generation, stopwatch.ElapsedMilliseconds),
                NewGeneration: null,
                OldGenerationToDispose: null);
        }

        try
        {
            return await ReloadCoreAsync(request, currentGeneration, requestedAt, stopwatch);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<ReloadOutcome> ReloadCoreAsync(
        ReloadRequest request,
        ReloadGeneration? currentGeneration,
        DateTimeOffset requestedAt,
        Stopwatch stopwatch)
    {
        if (!File.Exists(request.SourceAssemblyPath))
        {
            return Failure(
                requestedAt,
                request,
                shadowAssemblyPath: null,
                currentGeneration,
                stopwatch,
                ReloadErrorCode.ReloadSourceMissing,
                ReloadPhase.Source,
                $"Reload source assembly does not exist: {request.SourceAssemblyPath}");
        }

        var generation = (currentGeneration?.Generation ?? 0) + 1;
        var shadowDirectory = Path.Combine(request.ShadowRoot, $"generation-{generation}");
        var shadowAssemblyPath = Path.Combine(shadowDirectory, Path.GetFileName(request.SourceAssemblyPath));

        try
        {
            await CopyStableSourceAsync(request.SourceAssemblyPath, shadowAssemblyPath);
        }
        catch (IOException exception)
        {
            return Failure(
                requestedAt,
                request,
                shadowAssemblyPath: null,
                currentGeneration,
                stopwatch,
                ReloadErrorCode.ReloadSourceLockedOrIncomplete,
                ReloadPhase.Source,
                "Reload source assembly was locked, incomplete, or changed during the stability check.",
                exception);
        }

        PluginLoadContext? loadContext = null;
        IHotLogic? logic = null;

        try
        {
            loadContext = new PluginLoadContext(shadowAssemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
            var entryType = DiscoverEntryType(assembly, request.EntryTypeName);
            if (entryType.Error is not null)
            {
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, entryType.Error.Value.Code, entryType.Error.Value.Phase, entryType.Error.Value.Message);
            }

            try
            {
                logic = (IHotLogic?)Activator.CreateInstance(entryType.Type!);
            }
            catch (Exception exception)
            {
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadActivationFailed, ReloadPhase.Activation, "Failed to construct reloadable logic entry type.", Unwrap(exception));
            }

            if (logic is null)
            {
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadActivationFailed, ReloadPhase.Activation, "Reloadable logic entry type returned null.");
            }

            HotLogicManifest? manifest;
            try
            {
                manifest = logic.Manifest;
            }
            catch (Exception exception)
            {
                logic.Dispose();
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadContractMissing, ReloadPhase.Contract, "Reloadable logic did not expose a usable manifest.", Unwrap(exception));
            }

            if (manifest is null)
            {
                logic.Dispose();
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadContractMissing, ReloadPhase.Contract, "Reloadable logic manifest was null.");
            }

            if (manifest.ContractVersion != HotContract.ContractVersion)
            {
                logic.Dispose();
                loadContext.Unload();
                return Failure(
                    requestedAt,
                    request,
                    shadowAssemblyPath,
                    currentGeneration,
                    stopwatch,
                    ReloadErrorCode.ReloadContractVersionMismatch,
                    ReloadPhase.Contract,
                    $"{assembly.GetName().Name} was built for contract version {manifest.ContractVersion}, but the shell supports version {HotContract.ContractVersion}.");
            }

            var reloadContext = new HotReloadContext(generation, requestedAt, request.SourceAssemblyPath, shadowAssemblyPath);
            try
            {
                logic.Initialize(_host, reloadContext);
            }
            catch (Exception exception)
            {
                logic.Dispose();
                loadContext.Unload();
                return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadInitializationFailed, ReloadPhase.Initialization, "Reloadable logic initialization failed.", Unwrap(exception));
            }

            var newGeneration = new ReloadGeneration(
                generation,
                manifest,
                logic,
                loadContext,
                request.SourceAssemblyPath,
                shadowAssemblyPath,
                requestedAt);

            var report = ReloadReport.Loaded(
                generation,
                requestedAt,
                request.SourceAssemblyPath,
                shadowAssemblyPath,
                manifest.ContractVersion,
                assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(shadowAssemblyPath),
                entryType.Type!.FullName ?? entryType.Type.Name,
                currentGeneration?.Generation,
                previousDisposed: null,
                previousUnloadRequested: null,
                previousCollected: null,
                stopwatch.ElapsedMilliseconds);

            return new ReloadOutcome(report, newGeneration, currentGeneration);
        }
        catch (Exception exception)
        {
            try
            {
                logic?.Dispose();
            }
            catch
            {
            }

            loadContext?.Unload();
            return Failure(requestedAt, request, shadowAssemblyPath, currentGeneration, stopwatch, ReloadErrorCode.ReloadActivationFailed, ReloadPhase.Load, "Unexpected reload failure.", Unwrap(exception));
        }
    }

    private static async Task CopyStableSourceAsync(string sourceAssemblyPath, string shadowAssemblyPath)
    {
        var first = Sample(sourceAssemblyPath);
        await Task.Delay(StabilityDelay);
        var second = Sample(sourceAssemblyPath);
        if (first != second)
        {
            throw new IOException("Source assembly changed during stability check.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(shadowAssemblyPath)!);
        using (var source = new FileStream(sourceAssemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(shadowAssemblyPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(destination);
        }

        CopySidecarIfPresent(sourceAssemblyPath, shadowAssemblyPath, ".deps.json");
        CopySidecarIfPresent(sourceAssemblyPath, shadowAssemblyPath, ".runtimeconfig.json");
        CopySidecarIfPresent(sourceAssemblyPath, shadowAssemblyPath, ".pdb");

        var sourceDirectory = Path.GetDirectoryName(sourceAssemblyPath)!;
        var shadowDirectory = Path.GetDirectoryName(shadowAssemblyPath)!;
        var mainFileName = Path.GetFileName(sourceAssemblyPath);
        foreach (var dll in Directory.EnumerateFiles(sourceDirectory, "*.dll"))
        {
            var fileName = Path.GetFileName(dll);
            if (string.Equals(fileName, mainFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "HotMod.Contracts.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(dll, Path.Combine(shadowDirectory, fileName), overwrite: true);
        }
    }

    private static (long Length, DateTime LastWriteTimeUtc) Sample(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static void CopySidecarIfPresent(string sourceAssemblyPath, string shadowAssemblyPath, string suffix)
    {
        var source = Path.ChangeExtension(sourceAssemblyPath, null) + suffix;
        if (File.Exists(source))
        {
            File.Copy(source, Path.ChangeExtension(shadowAssemblyPath, null) + suffix, overwrite: true);
        }
    }

    private static EntryTypeResult DiscoverEntryType(Assembly assembly, string? explicitTypeName)
    {
        if (!string.IsNullOrWhiteSpace(explicitTypeName))
        {
            var type = assembly.GetType(explicitTypeName, throwOnError: false);
            if (type is null || type.IsAbstract || !typeof(IHotLogic).IsAssignableFrom(type))
            {
                return EntryTypeResult.Failure(ReloadErrorCode.ReloadEntryTypeMissing, ReloadPhase.EntryType, $"Reloadable logic entry type was not found: {explicitTypeName}");
            }

            return EntryTypeResult.Success(type);
        }

        var candidates = assembly.GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IHotLogic).IsAssignableFrom(type))
            .ToArray();

        return candidates.Length switch
        {
            1 => EntryTypeResult.Success(candidates[0]),
            0 => EntryTypeResult.Failure(ReloadErrorCode.ReloadEntryTypeMissing, ReloadPhase.EntryType, "Reloadable logic assembly does not contain a public IHotLogic entry type."),
            _ => EntryTypeResult.Failure(ReloadErrorCode.ReloadEntryTypeAmbiguous, ReloadPhase.EntryType, "Reloadable logic assembly contains multiple public IHotLogic entry types.")
        };
    }

    private static ReloadOutcome Failure(
        DateTimeOffset requestedAt,
        ReloadRequest request,
        string? shadowAssemblyPath,
        ReloadGeneration? currentGeneration,
        Stopwatch stopwatch,
        ReloadErrorCode code,
        ReloadPhase phase,
        string message,
        Exception? exception = null)
    {
        return new ReloadOutcome(
            ReloadReport.Failed(
                requestedAt,
                request.SourceAssemblyPath,
                shadowAssemblyPath,
                currentGeneration?.Generation,
                stopwatch.ElapsedMilliseconds,
                new ReloadError(
                    code,
                    phase,
                    message,
                    exception?.GetType().FullName,
                    exception?.Message,
                    RestartRequired: code is ReloadErrorCode.ReloadRestartRequired or ReloadErrorCode.ReloadHookSignatureChanged)),
            NewGeneration: null,
            OldGenerationToDispose: null);
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
    }

    private readonly record struct EntryTypeError(ReloadErrorCode Code, ReloadPhase Phase, string Message);

    private readonly record struct EntryTypeResult(Type? Type, EntryTypeError? Error)
    {
        public static EntryTypeResult Success(Type type) => new(type, Error: null);

        public static EntryTypeResult Failure(ReloadErrorCode code, ReloadPhase phase, string message)
        {
            return new EntryTypeResult(Type: null, new EntryTypeError(code, phase, message));
        }
    }
}

public sealed record ReloadRequest(
    string SourceAssemblyPath,
    string ShadowRoot,
    string? EntryTypeName,
    string Reason,
    bool VerifyUnload);

public sealed record ReloadOutcome(
    ReloadReport Report,
    ReloadGeneration? NewGeneration,
    ReloadGeneration? OldGenerationToDispose);
