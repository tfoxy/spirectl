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

/// <param name="Branch">
/// The game's OWN <c>release_info.json</c> branch field, which is its release TAG — "v0.107.1", not a Steam
/// branch name. Kept as the game reports it; see <paramref name="SteamBranch"/> for the other question.
/// </param>
/// <param name="SteamBranch">
/// The Steam branch this install is MOUNTED on — "public", "public-beta" — or empty when it could not be
/// determined (no Steam, or an install outside a Steam library). Nothing in the game's own release record answers
/// this, and it is the field that separates two installs of the same product whose content differs; see
/// <see cref="Spirectl.Sts2.Live.Sts2GameBuildIdentity"/>.
/// </param>
/// <param name="SteamBuildId">Steam's build id for the mounted branch, or 0 when undeterminable.</param>
/// <param name="SteamBranchSource">
/// WHICH rung of the ladder answered — "steamworks" (Steam itself, in-process), "appmanifest" (Steam's install
/// manifest on disk), or "unknown". Reported because the rungs are not equally trustworthy and a silent
/// demotion is otherwise invisible: they agree on an ordinary install, so the only way to find out that the
/// primary one never fires is to ask which one did.
/// </param>
public sealed record GameVersionInfoSnapshot(
    string Version,
    string VersionDate,
    string Commit,
    string Branch,
    int MainAssemblyHash,
    ModdingSummarySnapshot Modding,
    string SteamBranch = "",
    int SteamBuildId = 0,
    string SteamBranchSource = "") : ReferencePayloadSnapshot;

public sealed record ModdingSummarySnapshot(bool IsRunningModded, int LoadedModCount, int TotalModCount);

// --- topic: randomCharacter ---

// The synthetic "Random Character" lobby entry (RandomCharacterFacts.Id), reusing the SAME
// CharacterGameModelSnapshot shape as the "characters" model-catalog family so hosts can seed
// it into their character model cache uniformly. It is not a real game model (excluded from
// ModelDb.AllCharacters), so it is exposed here instead of through GetModels.
public sealed record RandomCharacterSnapshot(CharacterGameModelSnapshot Character) : ReferencePayloadSnapshot;
