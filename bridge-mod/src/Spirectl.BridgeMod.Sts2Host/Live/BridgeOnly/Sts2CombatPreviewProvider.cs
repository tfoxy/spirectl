using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Combat;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Live combat damage/block PREVIEW oracle — the game's OWN post-modifier numbers for each
/// hand card, used to validate the client's status-modifier formula (NOT consumed by the
/// renderer). For each playable hand card it calls the game's damage/block funnel
/// (<see cref="Hook.ModifyDamage"/> / <see cref="Hook.ModifyBlock"/>) directly, exactly as
/// the game's own preview does (mirroring <c>CalculatedDamageVar.UpdateCardPreview</c> and
/// <c>CalculatedBlockVar.UpdateCardPreview</c> with <c>runGlobalHooks: true</c>), so every
/// power, relic, and enchantment is reflected as in-game. Calling the funnel and reading its
/// return sidesteps the headless dead-end where <c>PreviewValue</c> isn't populated by the
/// normal UI-driven preview trigger.
///
/// The displayed integer is <c>(int)</c> the funnel result — truncation toward zero, matching
/// the game's <c>DynamicVar.ToHighlightedString</c> (<c>(int)PreviewValue</c>); the funnels
/// already clamp to <c>Math.Max(0m, …)</c>, so this equals <c>floor</c> for non-negative values.
///
/// Keys use the same stable ids state advertises (<see cref="Sts2CombatIds.CardId"/>,
/// <see cref="Sts2CombatIds.CreatureId"/>) so the offline harness can join oracle ↔ state.
/// Runs on the game's main thread (BridgeRuntime wraps the call in MainThreadInvoker).
/// </summary>
internal sealed class Sts2CombatPreviewProvider(ILogStream logStream) : ICombatPreviewProvider
{
    private readonly ILogStream _logStream = logStream;

    public CombatPreviewOperationResult GetCombatPreview(CombatPreviewRequestSnapshot request)
    {
        var combatManager = CombatManager.Instance;
        if (combatManager is null || !combatManager.IsInProgress)
        {
            // Not an error: the preview is simply unavailable outside live combat.
            return CombatPreviewOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                combatActive: false,
                new Dictionary<string, CardPreviewSnapshot>(StringComparer.Ordinal));
        }

        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState is null)
        {
            return CombatPreviewOperationResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                "combat-preview-no-run",
                "No active run state is available for combat preview.");
        }

        Player? player;
        if (request.PlayerId is { Length: > 0 } requestedId)
        {
            player = ((IEnumerable<Player>)runState.Players)
                .FirstOrDefault(candidate =>
                    string.Equals(Sts2CombatIds.PlayerId(candidate), requestedId, StringComparison.Ordinal));
        }
        else
        {
            player = LocalContext.GetMe(runState);
        }

        if (player is null)
        {
            return CombatPreviewOperationResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                "combat-preview-no-player",
                request.PlayerId is { Length: > 0 } id
                    ? $"No combat player matches id '{id}'."
                    : "No local combat player is available for combat preview.");
        }

        var playerId = Sts2CombatIds.PlayerId(player);
        var hittableEnemies = Sts2CombatPreviewCore.HittableEnemies();
        var hand = (player.PlayerCombatState?.Hand?.Cards ?? Enumerable.Empty<CardModel>()).ToList();

        _logStream.Write(
            BridgeLogLevel.Debug,
            "bridge.combat.preview",
            $"combat-preview: player={playerId} hand={hand.Count} hittableEnemies={hittableEnemies.Count} playPhase={Sts2CombatFacts.IsPlayPhase(player)}");

        var cards = new Dictionary<string, CardPreviewSnapshot>(StringComparer.Ordinal);
        var index = 0;
        foreach (var card in hand)
        {
            var cardIndex = index++;
            if (card is null)
            {
                continue;
            }

            try
            {
                var snapshot = Sts2CombatPreviewCore.ComputeCardPreview(card, hittableEnemies);
                if (snapshot is not null)
                {
                    cards[Sts2CombatIds.CardId(card, playerId, cardIndex)] = snapshot;
                }
            }
            catch (Exception ex)
            {
                // A single card's preview must never fail the whole oracle; the game's own
                // preview is likewise best-effort per card. Surface the cause for diagnosis.
                _logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.preview",
                    $"combat-preview: card[{cardIndex}] {card.GetType().Name} skipped — {ex.GetType().Name}: {ex.Message}");
            }
        }

        return CombatPreviewOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            combatActive: true,
            cards);
    }
}
