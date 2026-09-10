using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Reference;

// Provider for "game reference data": values that are constant at runtime but
// are not game models (cards, relics, …). Examples: the StsColors palette and
// the game version / modding metadata shown in the in-game DebugInfo node.
//
// The surface is topic-addressed and extensible: a new topic adds a new
// ReferencePayloadSnapshot subtype and a dispatch arm; existing topics are
// unaffected.
public interface IReferenceDataProvider
{
    ReferenceOperationResult GetReference(ReferenceRequestSnapshot request)
        => ReferenceOperationResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            topic: request.Topic,
            status: ReferenceStatus.Unavailable,
            code: "reference-unavailable",
            message: "Live game reference data requires the live STS2 bridge host.",
            notices:
            [
                new ReferenceNoticeSnapshot(
                    "reference-unavailable",
                    "warning",
                    "This runtime does not expose live game reference data.",
                    "reference"),
            ]);
}

public sealed record ReferenceRequestSnapshot(string Topic, IReadOnlyList<string> Keys);

public enum ReferenceStatus
{
    Ok,
    Partial,
    UnsupportedTopic,
    Unavailable,
}

public sealed record ReferenceOperationResult(
    DataSourceKind Source,
    bool Provisional,
    string Topic,
    ReferenceStatus Status,
    ReferencePayloadSnapshot? Payload,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<ReferenceNoticeSnapshot> Notices,
    ReferenceFailureSnapshot? Error)
{
    public static ReferenceOperationResult Success(
        DataSourceKind source,
        bool provisional,
        string topic,
        ReferenceStatus status,
        ReferencePayloadSnapshot? payload,
        IReadOnlyList<string> missingKeys,
        IReadOnlyList<ReferenceNoticeSnapshot> notices)
        => new(source, provisional, topic, status, payload, missingKeys, notices, null);

    public static ReferenceOperationResult Failure(
        DataSourceKind source,
        bool provisional,
        string topic,
        ReferenceStatus status,
        string code,
        string message,
        IReadOnlyList<ReferenceNoticeSnapshot> notices)
        => new(source, provisional, topic, status, null, [], notices, new ReferenceFailureSnapshot(code, message));
}

public sealed record ReferenceFailureSnapshot(string Code, string Message);

public sealed record ReferenceNoticeSnapshot(string Code, string Severity, string Message, string? Path = null);

// One subtype per topic. Extend additively; never reuse the proto field numbers
// that mirror these in reference.proto.
public abstract record ReferencePayloadSnapshot;

// --- topic: colors ---

public sealed record GameColorsSnapshot(IReadOnlyList<GameColorSnapshot> Colors) : ReferencePayloadSnapshot;

public sealed record GameColorSnapshot(string Name, string Hex, float R, float G, float B, float A);

// --- topic: version ---

public sealed record GameVersionInfoSnapshot(
    string Version,
    string VersionDate,
    string Commit,
    string Branch,
    int MainAssemblyHash,
    ModdingSummarySnapshot Modding) : ReferencePayloadSnapshot;

public sealed record ModdingSummarySnapshot(bool IsRunningModded, int LoadedModCount, int TotalModCount);

// --- topic: randomCharacter ---

// The synthetic "Random Character" lobby entry (RandomCharacterFacts.Id), reusing the SAME
// CharacterGameModelSnapshot shape as the "characters" model-catalog family so hosts can seed
// it into their character model cache uniformly. It is not a real game model (excluded from
// ModelDb.AllCharacters), so it is exposed here instead of through GetModels.
public sealed record RandomCharacterSnapshot(CharacterGameModelSnapshot Character) : ReferencePayloadSnapshot;
