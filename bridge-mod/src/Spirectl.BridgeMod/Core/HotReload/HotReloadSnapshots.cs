using System.Text.Json.Serialization;

namespace Spirectl.Sts2.Core.HotReload;


public sealed record HotReloadStatusRequestSnapshot(
    string RequestId,
    string ProjectId,
    string ShellModId);

public sealed record HotReloadRequestSnapshot(
    string RequestId,
    string ProjectId,
    string ShellModId,
    string LogicArtifactPath,
    uint ExpectedContractVersion,
    bool WaitForCompletion,
    uint TimeoutMs);

public sealed record HotReloadOperationResult(
    HotReloadShellStatusSnapshot? Status,
    HotReloadReportSnapshot? Report,
    bool Accepted,
    IReadOnlyList<HotReloadNoticeSnapshot> Notices,
    HotReloadFailureSnapshot? Error)
{
    public static HotReloadOperationResult Success(
        HotReloadShellStatusSnapshot status,
        HotReloadReportSnapshot? report,
        bool accepted,
        IReadOnlyList<HotReloadNoticeSnapshot> notices)
        => new(status, report, accepted, notices, Error: null);

    public static HotReloadOperationResult Failure(
        string code,
        string message,
        IReadOnlyList<HotReloadNoticeSnapshot>? notices = null,
        HotReloadShellStatusSnapshot? status = null,
        HotReloadReportSnapshot? report = null)
        => new(status, report, Accepted: false, notices ?? [], new HotReloadFailureSnapshot(code, message));
}

public sealed record HotReloadStatusResultSnapshot(
    HotReloadShellStatusSnapshot? Status,
    IReadOnlyList<HotReloadNoticeSnapshot> Notices,
    HotReloadFailureSnapshot? Error)
{
    public static HotReloadStatusResultSnapshot Success(
        HotReloadShellStatusSnapshot status,
        IReadOnlyList<HotReloadNoticeSnapshot> notices)
        => new(status, notices, Error: null);

    public static HotReloadStatusResultSnapshot Failure(string code, string message)
        => new(Status: null, Notices: [], new HotReloadFailureSnapshot(code, message));
}

public sealed record HotReloadFailureSnapshot(string Code, string Message);

public sealed record HotReloadProtocolSnapshot(string Id, uint Version);

public sealed record HotReloadShellStatusSnapshot(
    bool Supported,
    HotReloadProtocolSnapshot? Protocol,
    string ShellModId,
    uint ShellProtocolVersion,
    uint ActiveGeneration,
    string ExpectedLogicArtifactPath,
    uint ContractVersion,
    bool ReloadInProgress,
    HotReloadReportSnapshot? LastReloadReport,
    bool RestartRequired,
    IReadOnlyList<HotReloadNoticeSnapshot> Notices);

public sealed record HotReloadReportSnapshot(
    string Status,
    uint Generation,
    string RequestedAt,
    string SourceAssemblyPath,
    string ShadowAssemblyPath,
    uint ContractVersion,
    string LogicAssemblyName,
    string EntryType,
    uint PreviousGeneration,
    bool PreviousRemainsActive,
    bool PreviousDisposed,
    bool PreviousUnloadRequested,
    bool PreviousCollected,
    ulong DurationMs,
    HotReloadErrorSnapshot? Error,
    IReadOnlyList<HotReloadWarningSnapshot> Warnings);

public sealed record HotReloadErrorSnapshot(
    string Code,
    string Phase,
    string Message,
    string ExceptionType,
    string ExceptionMessage,
    bool RestartRequired);

public sealed record HotReloadWarningSnapshot(string Code, string Phase, string Message);

public sealed record HotReloadNoticeSnapshot(string Code, string Message);

internal sealed record HotReloadShellStatusJson(
    HotReloadProtocolSnapshot? Protocol,
    string? ShellModId,
    uint ActiveGeneration,
    string? ExpectedLogicArtifactPath,
    uint ContractVersion,
    bool ReloadInProgress,
    HotReloadReportSnapshot? LastReloadReport,
    bool RestartRequired);

internal sealed record HotReloadShellResponseJson(
    bool Accepted,
    HotReloadShellStatusJson? Status,
    HotReloadReportSnapshot? Report,
    IReadOnlyList<HotReloadNoticeSnapshot>? Notices);

internal sealed record HotReloadShellRequestJson(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("shellModId")] string ShellModId,
    [property: JsonPropertyName("logicArtifactPath")] string LogicArtifactPath,
    [property: JsonPropertyName("expectedContractVersion")] uint ExpectedContractVersion,
    [property: JsonPropertyName("waitForCompletion")] bool WaitForCompletion,
    [property: JsonPropertyName("timeoutMs")] uint TimeoutMs);
