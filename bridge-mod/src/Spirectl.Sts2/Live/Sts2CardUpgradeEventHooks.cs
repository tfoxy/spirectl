using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Captures mid-combat card upgrades (e.g. STONE_CRACKER at room entry, Armaments) and
/// publishes a <c>cardUpgrade</c> event to <see cref="EmbeddableCombatEventHub.Shared"/>.
/// Postfixes <c>CardModel.UpgradeInternal</c> — there is no <c>Hook.*</c> for upgrades — and
/// is gated on <c>CombatManager.IsInProgress</c> so out-of-combat upgrades (rest-site smith,
/// card-reward upgrades) are not streamed.
///
/// The relic that drove the upgrade is not available at <c>UpgradeInternal</c> (it takes no
/// args), so <c>source_relic_model_id</c> is left empty by the live hook; a fixture authors it
/// directly into <c>StateCombatState.transient_effects</c>. The hook runs on the game thread;
/// publishing is non-blocking. The bridge never populates <c>transient_effects</c> — a host
/// synthesizes those from this stream.
/// </summary>
internal static class Sts2CardUpgradeEventHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

    // Snapshot/preview code calls CardModel.UpgradeInternal on THROWAWAY CLONES every capture (to
    // compute "after-upgrade" stats for the in-hand preview); those must not masquerade as live
    // mid-combat upgrades. Sts2CardStateSnapshotFactory brackets its clone-upgrade calls with
    // Begin/EndSuppress so the postfix below skips them. Snapshot capture and the postfix both run
    // synchronously on the game thread, so a [ThreadStatic] depth counter is race-free; the counter is
    // a depth (not a bool) so nested preview computations restore correctly.
    [ThreadStatic]
    private static int _suppressDepth;

    internal static void BeginSuppress() => _suppressDepth++;

    internal static void EndSuppress() => _suppressDepth--;

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);
            // Arm the subscriber gate every capture postfix in this file early-outs on.
            Sts2CombatEventCapture.EnsureArmed();

            var target = typeof(CardModel).GetMethod(
                nameof(CardModel.UpgradeInternal),
                BindingFlags.Public | BindingFlags.Instance);
            var postfix = typeof(Sts2CardUpgradeEventHooks).GetMethod(
                nameof(UpgradeInternalPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.card-upgrade-events",
                    "Unable to install card-upgrade-event hook; CardModel.UpgradeInternal was not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.combat-card-upgrade-events");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.card-upgrade-events",
                    $"Skipping card-upgrade-event hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.combat.card-upgrade-events",
                "Installed card-upgrade-event hook; live combat upgrades now stream over WatchCombatEvents.");
        }
    }

    // Harmony binds __instance to the CardModel being upgraded. Reading it in a postfix is
    // valid regardless of async completion. Telemetry must never crash combat.
    private static void UpgradeInternalPostfix(CardModel __instance)
    {
        try
        {
            if (__instance is null)
            {
                return;
            }

            // Skip the snapshot factory's own clone-upgrade computations (preview "after" stats),
            // which call UpgradeInternal every capture and would otherwise flood the stream.
            if (_suppressDepth > 0)
            {
                return;
            }

            // Only stream upgrades that happen during combat (the cardUpgrade preview is a
            // combat VFX); rest-site smith and reward upgrades go through the same method.
            if (CombatManager.Instance?.IsInProgress != true)
            {
                return;
            }

            var cardModelId = ResolveCardModelId(__instance);
            if (string.IsNullOrEmpty(cardModelId))
            {
                return;
            }

            // Gated on a live WatchCombatEvents subscriber — the id resolve and the hub's fan-out lock are
            // pure cost with nobody watching. Scoped to the PUBLISH only; the Bump below must keep running.
            if (Sts2CombatEventCapture.Active)
            {
                var cardId = Sts2CombatIds.CardId(__instance, playerId: "p:unknown", index: 0);

                EmbeddableCombatEventHub.Shared.PublishCardUpgrade(
                    cardId,
                    cardModelId!,
                    // The driving relic is not known at UpgradeInternal; fixtures author it directly.
                    sourceRelicModelId: null,
                    DateTimeOffset.UtcNow);
            }

            // The card's semantic identity changed (accelerator only).
            //
            // OUTSIDE the gate, for the reason Sts2DamageEventHooks spells out: this feeds the STATE watch,
            // a different subscriber set that the couch mirror does use.
            Sts2SemanticStateRevision.Bump();
        }
        catch
        {
            // A card-upgrade telemetry failure must never disrupt combat resolution.
        }
    }

    // Mirrors Sts2CardStateSnapshotFactory.ResolveModelId: prefer Id.Entry, then Id, then ModelId.
    private static string? ResolveCardModelId(object card)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(card, "Id");
        var entry = Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString();
        if (!string.IsNullOrWhiteSpace(entry))
        {
            return entry.Trim();
        }

        var idText = id?.ToString();
        if (!string.IsNullOrWhiteSpace(idText))
        {
            return idText.Trim();
        }

        var modelId = Sts2LiveIntrospection.GetMemberValue(card, "ModelId")?.ToString();
        return string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
    }
}
