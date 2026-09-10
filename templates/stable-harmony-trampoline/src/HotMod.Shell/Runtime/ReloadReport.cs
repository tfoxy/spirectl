using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotMod.Shell.Runtime;

public static class ReloadJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T? Deserialize<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

public enum ReloadStatus
{
    Loaded,
    Failed
}

public enum ReloadErrorCode
{
    ReloadSourceMissing,
    ReloadSourceLockedOrIncomplete,
    ReloadBusy,
    ReloadContractMissing,
    ReloadContractVersionMismatch,
    ReloadEntryTypeMissing,
    ReloadEntryTypeAmbiguous,
    ReloadActivationFailed,
    ReloadInitializationFailed,
    ReloadPreviousDisposeFailed,
    ReloadUnloadNotCollected,
    ReloadPatchOwnerMissing,
    ReloadHookSignatureChanged,
    ReloadRestartRequired
}

public enum ReloadPhase
{
    Source,
    ShadowCopy,
    Load,
    EntryType,
    Activation,
    Contract,
    Initialization,
    Swap,
    Dispose,
    Unload,
    Patch
}

public sealed record ReloadReport(
    ReloadStatus Status,
    int? Generation,
    DateTimeOffset RequestedAt,
    string? SourceAssemblyPath,
    string? ShadowAssemblyPath,
    int? ContractVersion,
    string? LogicAssemblyName,
    string? EntryType,
    int? PreviousGeneration,
    bool PreviousRemainsActive,
    bool? PreviousDisposed,
    bool? PreviousUnloadRequested,
    bool? PreviousCollected,
    long DurationMs,
    ReloadError? Error,
    IReadOnlyList<ReloadWarning> Warnings)
{
    public static ReloadReport Loaded(
        int generation,
        DateTimeOffset requestedAt,
        string sourceAssemblyPath,
        string shadowAssemblyPath,
        int contractVersion,
        string logicAssemblyName,
        string entryType,
        int? previousGeneration,
        bool? previousDisposed,
        bool? previousUnloadRequested,
        bool? previousCollected,
        long durationMs,
        IReadOnlyList<ReloadWarning>? warnings = null)
    {
        return new ReloadReport(
            ReloadStatus.Loaded,
            generation,
            requestedAt,
            sourceAssemblyPath,
            shadowAssemblyPath,
            contractVersion,
            logicAssemblyName,
            entryType,
            previousGeneration,
            PreviousRemainsActive: false,
            previousDisposed,
            previousUnloadRequested,
            previousCollected,
            durationMs,
            Error: null,
            warnings ?? Array.Empty<ReloadWarning>());
    }

    public static ReloadReport Failed(
        DateTimeOffset requestedAt,
        string? sourceAssemblyPath,
        string? shadowAssemblyPath,
        int? previousGeneration,
        long durationMs,
        ReloadError error,
        IReadOnlyList<ReloadWarning>? warnings = null)
    {
        return new ReloadReport(
            ReloadStatus.Failed,
            Generation: null,
            requestedAt,
            sourceAssemblyPath,
            shadowAssemblyPath,
            ContractVersion: null,
            LogicAssemblyName: null,
            EntryType: null,
            previousGeneration,
            PreviousRemainsActive: true,
            PreviousDisposed: null,
            PreviousUnloadRequested: null,
            PreviousCollected: null,
            durationMs,
            error,
            warnings ?? Array.Empty<ReloadWarning>());
    }

    public static ReloadReport Busy(
        DateTimeOffset requestedAt,
        string? sourceAssemblyPath,
        int? previousGeneration,
        long durationMs)
    {
        return Failed(
            requestedAt,
            sourceAssemblyPath,
            shadowAssemblyPath: null,
            previousGeneration,
            durationMs,
            new ReloadError(
                ReloadErrorCode.ReloadBusy,
                ReloadPhase.Load,
                "A reload is already running.",
                ExceptionType: null,
                ExceptionMessage: null,
                RestartRequired: false));
    }
}

public sealed record ReloadError(
    ReloadErrorCode Code,
    ReloadPhase Phase,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage,
    bool RestartRequired);

public sealed record ReloadWarning(
    ReloadErrorCode Code,
    ReloadPhase Phase,
    string Message);
