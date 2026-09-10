using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Fixtures;


public sealed record RecordedFixtureResult(
    RecordedFixtureSuccess? Success,
    RecordedFixtureFailure? Error)
{
    public static RecordedFixtureResult Succeeded(RecordedFixtureSuccess success)
        => new(success, Error: null);

    public static RecordedFixtureResult Failure(
        string requestId,
        DataSourceKind source,
        bool provisional,
        RecordedFixtureFailureCode code,
        string message,
        IReadOnlyList<FixtureLoadDetail> details)
        => new(
            Success: null,
            Error: new RecordedFixtureFailure(requestId, source, provisional, code, message, details));
}

public sealed record RecordedFixtureSuccess(
    string RequestId,
    string FixtureYaml,
    string FixtureJson,
    RecordedFixtureMetadataSnapshot Metadata,
    DataSourceKind Source,
    bool Provisional);

public sealed record RecordedFixtureMetadataSnapshot(
    string SchemaVersion,
    string RecordedAt,
    RecordedFixtureScreenSnapshot Screen,
    string GameVersion,
    string BridgeVersion,
    string SpirectlVersion,
    RecordedFixtureRestoreQuality RestoreQuality,
    IReadOnlyList<RecordedFixtureCompatibilityNoteSnapshot> KnownOmissions,
    IReadOnlyList<RecordedFixtureNoticeSnapshot> Notices);

public sealed record RecordedFixtureScreenSnapshot(
    string Type,
    string Title,
    string ScreenInstanceId);

public sealed record RecordedFixtureCompatibilityNoteSnapshot(
    string Code,
    string Message,
    string Field);

public sealed record RecordedFixtureNoticeSnapshot(
    string Code,
    string Message,
    bool Provisional = false);

public sealed record RecordedFixtureFailure(
    string RequestId,
    DataSourceKind Source,
    bool Provisional,
    RecordedFixtureFailureCode Code,
    string Message,
    IReadOnlyList<FixtureLoadDetail> Details);

public enum RecordedFixtureRestoreQuality
{
    Unspecified,
    Exact,
    Partial,
    Unsupported,
    Degraded,
}

public enum RecordedFixtureFailureCode
{
    NotImplemented,
    InvalidAction,
    RuntimeFailure,
}
