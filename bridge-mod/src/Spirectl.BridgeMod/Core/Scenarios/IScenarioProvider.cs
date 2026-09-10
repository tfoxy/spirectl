using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Restore;

namespace Spirectl.Sts2.Core.Scenarios;


public interface IScenarioProvider
{
    ScenarioCaptureResultSnapshot Capture(ScenarioCaptureRequestSnapshot request);

    ScenarioRestoreResultSnapshot Restore(ScenarioRestoreRequestSnapshot request);
}

public sealed record ScenarioCaptureRequestSnapshot(
    string RequestId,
    bool IncludeExact,
    string? PlayerId);

public sealed record ScenarioRestoreRequestSnapshot(
    string RequestId,
    ScenarioDocumentSnapshot? Scenario,
    byte[]? ExactBundle,
    string? ExactBundleContentType,
    bool AllowSparseFallback,
    bool AllowDegradedLocalMultiplayer = false);

public sealed record ScenarioCaptureResultSnapshot(
    string RequestId,
    ScenarioDocumentSnapshot? Scenario,
    DataSourceKind Source,
    bool Provisional,
    ScenarioExactBundlePayloadSnapshot? ExactBundlePayload,
    ScenarioFailure? Error)
{
    public static ScenarioCaptureResultSnapshot Success(
        string requestId,
        ScenarioDocumentSnapshot scenario,
        DataSourceKind source,
        bool provisional,
        ScenarioExactBundlePayloadSnapshot? exactBundlePayload)
        => new(requestId, scenario, source, provisional, exactBundlePayload, Error: null);

    public static ScenarioCaptureResultSnapshot Failure(
        string requestId,
        DataSourceKind source,
        bool provisional,
        ScenarioFailureCode code,
        string message,
        IReadOnlyList<ScenarioFailureDetail> details)
        => new(requestId, Scenario: null, source, provisional, ExactBundlePayload: null, new ScenarioFailure(code, message, details));
}

public sealed record ScenarioRestoreResultSnapshot(
    string RequestId,
    ScenarioRestoreQuality Quality,
    ScenarioScreenSnapshot? Screen,
    ScenarioPerspectiveSnapshot? ResolvedPerspective,
    IReadOnlyList<ScenarioNoticeSnapshot> Notices,
    bool ExactBundleUsed,
    bool SparseFallbackUsed,
    IReadOnlyList<ScenarioCompatibilityNoteSnapshot> CompatibilityNotes,
    RestoreVerificationSnapshot? Verification,
    ScenarioFailure? Error,
    MultiplayerRestoreResultSnapshot? MultiplayerRestore = null)
{
    public static ScenarioRestoreResultSnapshot Success(
        string requestId,
        ScenarioRestoreQuality quality,
        ScenarioScreenSnapshot screen,
        ScenarioPerspectiveSnapshot resolvedPerspective,
        IReadOnlyList<ScenarioNoticeSnapshot> notices,
        bool exactBundleUsed,
        bool sparseFallbackUsed,
        IReadOnlyList<ScenarioCompatibilityNoteSnapshot> compatibilityNotes,
        RestoreVerificationSnapshot? verification = null,
        MultiplayerRestoreResultSnapshot? multiplayerRestore = null)
        => new(requestId, quality, screen, resolvedPerspective, notices, exactBundleUsed, sparseFallbackUsed, compatibilityNotes, verification, Error: null, MultiplayerRestore: multiplayerRestore);

    public static ScenarioRestoreResultSnapshot Failure(
        string requestId,
        ScenarioRestoreQuality quality,
        ScenarioFailureCode code,
        string message,
        IReadOnlyList<ScenarioFailureDetail> details)
        => new(requestId, quality, Screen: null, ResolvedPerspective: null, Notices: [], ExactBundleUsed: false, SparseFallbackUsed: false, CompatibilityNotes: [], Verification: null, new ScenarioFailure(code, message, details));
}

public sealed record ScenarioDocumentSnapshot(
    string SchemaVersion,
    string Name,
    string Description,
    string CreatedAt,
    ScenarioSourceSnapshot Source,
    ScenarioRestoreSnapshot Restore,
    ScenarioRunSnapshot? Run,
    string ScreenStateJson,
    IReadOnlyList<ScenarioNoticeSnapshot> Notices,
    MultiplayerRestoreSnapshot? Multiplayer = null);

public sealed record ScenarioSourceSnapshot(
    string GameVersion,
    string BridgeVersion,
    string SpirectlVersion,
    ScenarioScreenSnapshot Screen,
    ScenarioPerspectiveSnapshot? Perspective);

public sealed record ScenarioRestoreSnapshot(
    ScenarioRestoreMode Mode,
    ScenarioRestoreQuality Quality,
    ScenarioExactBundleMetadataSnapshot? ExactBundle,
    IReadOnlyList<ScenarioCompatibilityNoteSnapshot> CompatibilityNotes,
    IReadOnlyList<RestoreFieldReportSnapshot> FieldReports);

public sealed record ScenarioRunSnapshot(
    string Seed,
    int Act,
    int Floor,
    int Ascension,
    IReadOnlyList<ScenarioPlayerSnapshot> Players);

public sealed record ScenarioPlayerSnapshot(
    string Id,
    string Character,
    bool IsLocal,
    bool IsHost,
    bool IsRemote);

public sealed record ScenarioScreenSnapshot(string Type, string Title, string ScreenInstanceId);

public sealed record ScenarioPerspectiveSnapshot(string Scope, string PlayerId);

public sealed record ScenarioExactBundleMetadataSnapshot(
    string Path,
    string FormatVersion,
    string Sha256,
    ulong SizeBytes,
    bool VersionSensitive,
    string ContentType = "");

public sealed record ScenarioExactBundlePayloadSnapshot(
    string Path,
    string FormatVersion,
    string ContentType,
    byte[] Data);

public sealed record ScenarioCompatibilityNoteSnapshot(
    string Code,
    string Message,
    string Field);

public sealed record ScenarioNoticeSnapshot(
    string Code,
    string Message,
    bool Provisional = false);

public enum ScenarioRestoreMode
{
    Unspecified,
    Sparse,
    Exact,
    Hybrid,
}

public enum ScenarioRestoreQuality
{
    Unspecified,
    Exact,
    Partial,
    Unsupported,
    Degraded,
}

public enum ScenarioFailureCode
{
    NotImplemented,
    InvalidRequest,
    InvalidAction,
    RuntimeFailure,
}

public sealed record ScenarioFailure(
    ScenarioFailureCode Code,
    string Message,
    IReadOnlyList<ScenarioFailureDetail> Details);

public sealed record ScenarioFailureDetail(
    string Field,
    string Value,
    string Note);
