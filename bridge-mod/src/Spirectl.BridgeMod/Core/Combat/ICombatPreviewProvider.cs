using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Combat;


// Combat damage/block PREVIEW oracle: the game's own computed numbers for each playable
// hand card against each candidate target. Used to validate the client's status-modifier
// formula against game truth (NOT consumed by the renderer). The live implementation calls
// the game's damage/block funnel (Hook.ModifyDamage / Hook.ModifyBlock) directly so every
// power, relic, and enchantment is reflected exactly as in-game. Non-live runtimes report
// unavailable.
public interface ICombatPreviewProvider
{
    CombatPreviewOperationResult GetCombatPreview(CombatPreviewRequestSnapshot request)
        => CombatPreviewOperationResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: "combat-preview-unavailable",
            message: "Live combat damage/block preview requires the live STS2 bridge host.");
}

public sealed record CombatPreviewRequestSnapshot(string? PlayerId = null);

/// <summary>Bridge endpoint failure details for a combat-preview request.</summary>
public sealed record CombatPreviewFailureSnapshot(string Code, string Message);

public sealed record CombatPreviewOperationResult(
    DataSourceKind Source,
    bool Provisional,
    bool CombatActive,
    IReadOnlyDictionary<string, CardPreviewSnapshot> Cards,
    CombatPreviewFailureSnapshot? Error)
{
    public static CombatPreviewOperationResult Success(
        DataSourceKind source,
        bool provisional,
        bool combatActive,
        IReadOnlyDictionary<string, CardPreviewSnapshot> cards)
        => new(source, provisional, combatActive, cards, null);

    public static CombatPreviewOperationResult Failure(
        DataSourceKind source,
        bool provisional,
        string code,
        string message)
        => new(
            source,
            provisional,
            CombatActive: false,
            new Dictionary<string, CardPreviewSnapshot>(StringComparer.Ordinal),
            new CombatPreviewFailureSnapshot(code, message));
}

// The game-computed preview numbers for a single hand card. All values are final
// post-modifier integers.
