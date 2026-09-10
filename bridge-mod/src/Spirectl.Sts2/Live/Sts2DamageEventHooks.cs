using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Embedding;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Captures the floating-damage-number stream from live combat. Postfixes the single
/// static <c>Hook.AfterDamageReceived</c> (fired once per damage instance) and publishes a
/// <c>damage</c> event to <see cref="EmbeddableCombatEventHub.Shared"/> — the value the game
/// shows (<c>DamageResult.UnblockedDamage</c> [+ <c>OverkillDamage</c> unless the target is an
/// Osty], matching <c>NDamageNumVfx.Create</c>), keyed by the renderer's creature id scheme
/// (<see cref="Sts2CombatIds.CreatureId"/> → <c>creature:{CombatId}</c>).
///
/// The hook runs on the game thread; publishing is non-blocking (a bounded-channel write per
/// subscriber). The bridge never populates <c>StateCombatState.transient_effects</c> — a host
/// synthesizes those from this stream.
/// </summary>
internal static class Sts2DamageEventHooks
{
    private static readonly object Sync = new();
    private static bool _installed;

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

            var target = typeof(Hook).GetMethod(
                nameof(Hook.AfterDamageReceived),
                BindingFlags.Public | BindingFlags.Static);
            var postfix = typeof(Sts2DamageEventHooks).GetMethod(
                nameof(AfterDamageReceivedPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target is null || postfix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.damage-events",
                    "Unable to install damage-event hook; Hook.AfterDamageReceived was not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.combat-damage-events");
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.combat.damage-events",
                    $"Skipping damage-event hook because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.combat.damage-events",
                "Installed damage-event hook; live combat damage now streams over WatchCombatEvents.");
        }
    }

    // Harmony binds these by parameter name to Hook.AfterDamageReceived's signature
    // (target, result, dealer, cardSource). Reading the input args is valid in a postfix
    // regardless of the method's async completion. Telemetry must never crash combat.
    private static void AfterDamageReceivedPostfix(
        Creature target,
        DamageResult result,
        Creature? dealer,
        CardModel? cardSource)
    {
        try
        {
            if (target is null)
            {
                return;
            }

            // The displayed number, mirroring NDamageNumVfx.Create.
            var amount = result.UnblockedDamage;
            if (target.Monster is not Osty)
            {
                amount += result.OverkillDamage;
            }

            if (amount <= 0)
            {
                return;
            }

            // Gated on a live WatchCombatEvents subscriber: with nobody watching, the id resolution below
            // and the hub's fan-out lock are pure cost on the game thread. Scoped to the PUBLISH only —
            // see the Bump below, which must keep running either way.
            if (Sts2CombatEventCapture.Active)
            {
                var targetId = Sts2CombatIds.CreatureId(target, 0);
                var dealerId = dealer is null ? null : Sts2CombatIds.CreatureId(dealer, 0);

                EmbeddableCombatEventHub.Shared.PublishDamage(
                    targetId,
                    amount,
                    dealerId,
                    // source_card_model_id is reserved for later consumers; the floating number
                    // needs only target + amount.
                    sourceCardModelId: null,
                    DateTimeOffset.UtcNow);
            }

            // Damage moves HP, which is semantic state. Wake an idle state subscription a tick early rather
            // than letting it discover this on its next idle poll (accelerator only).
            //
            // OUTSIDE the gate above, deliberately. This serves the STATE watch, which is a different
            // subscriber set — the couch mirror uses it and never subscribes to combat events — so folding
            // it into the combat-event gate would stall a connected viewer's state stream behind an idle
            // poll. It is a single interlocked increment; there is nothing to save by gating it.
            Sts2SemanticStateRevision.Bump();
        }
        catch
        {
            // A damage-number telemetry failure must never disrupt combat resolution.
        }
    }
}
