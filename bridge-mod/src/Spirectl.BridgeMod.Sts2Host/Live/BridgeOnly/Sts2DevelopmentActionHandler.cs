using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2.Live;

// Debug/test-only semantic actions used by the browser auto-player (an algorithmic AutoSlay
// equivalent driven through the UI by Playwright). They are not catalog-rendered; the couch-coop
// frontend exposes them behind ?debug=1 buttons a test clicks like a user. Both run on the Godot
// main thread via the dispatcher in Sts2ActionHandler.Execute.
public sealed class Sts2DevelopmentActionHandler : IActionHandler
{
    private readonly IActionHandler _reusable;

    public Sts2DevelopmentActionHandler(IActionHandler reusable)
    {
        _reusable = reusable;
    }

    public ActionExecutionResult Execute(SemanticActionRequest request)
        => Sts2MainThreadDispatcher.Invoke(() => ExecuteOnMainThread(request));

    private ActionExecutionResult ExecuteOnMainThread(SemanticActionRequest request)
        => request.Kind switch
        {
            SemanticActionKind.DebugApplyGodMode => ExecuteDebugApplyGodMode(request),
            SemanticActionKind.DebugSetSeed => ExecuteDebugSetSeed(request),
            SemanticActionKind.DebugStartRun => ExecuteDebugStartRun(request),
            SemanticActionKind.DebugAdvance => ExecuteDebugAdvance(request),
            SemanticActionKind.DebugPlayCombat => ExecuteDebugPlayCombat(request),
            SemanticActionKind.DebugTravel => ExecuteDebugTravel(request),
            SemanticActionKind.DebugResolveEvent => ExecuteDebugResolveEvent(request),
            SemanticActionKind.DebugOpenShop => ExecuteDebugOpenShop(request),
            SemanticActionKind.DebugUsePotions => ExecuteDebugUsePotions(request),
            SemanticActionKind.DebugSpeedFast => ExecuteDebugSpeedFast(request),
            SemanticActionKind.Heal => ExecuteHeal(request),
            _ => ActionExecutionResult.Failure(request.Kind, ActionFailureCode.InvalidAction, $"Unsupported development action '{request.Kind}'."),
        };
    // Survival cheat for the browser auto-player: +9999 Buffer (negate incoming hits) and +9999
    // Regen (heal) so a random card-player can never die and thus reach deep content. We deliberately
    // do NOT grant Strength: a one-shot Strength ended every combat after a single attack card, which
    // defeats the goal of a realistic, multi-turn run that exercises the full energy/card-play loop.
    // Without Strength the player still survives forever but must actually grind each fight down by
    // playing cards across turns. Applied to the local player's current combat creature; the
    // auto-player re-clicks it at the start of each combat (creature is per-combat).
    private ActionExecutionResult ExecuteDebugApplyGodMode(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-apply-god-mode requires a run in progress.");
        }

        var me = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        var creature = me?.Creature;
        if (creature is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-apply-god-mode requires an active combat creature; retry once combat has started.");
        }

        TaskHelper.RunSafely(ApplyGodMode(me!));
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "God mode applied to the local player: +9999 Buffer, +9999 Regen (no Strength), free energy (kept at 5).");
    }

    // Dev-only host-side heal (`sts2 dev heal`): a live-rescue devtool, NOT a legal
    // player action (it is never advertised in availableActions). Directly mutates any creature's
    // HP through the game's own CreatureCmd on the main thread, so it applies immediately even
    // during the enemy turn (unlike the game's networked `heal` console command, which queues to
    // the next play phase). Semantics inherited from the game:
    // - CreatureCmd.Heal clamps to max HP and REVIVES a dead creature (HealInternal + revive anim).
    // - --full uses CreatureCmd.SetCurrentHp(max), which also revives.
    // MP caveat: host-direct mutation can desync real remote ENet clients; couch co-op's norm of
    // one real player + synthetic seats is fine.
    private ActionExecutionResult ExecuteHeal(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "heal requires a run in progress.");
        }

        var full = false;
        decimal amount = 0m;
        if (request.Values is not null)
        {
            if (request.Values.TryGetValue("full", out var fullRaw))
            {
                full = string.Equals(fullRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }

            if (request.Values.TryGetValue("amount", out var amountRaw))
            {
                decimal.TryParse(amountRaw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out amount);
            }
        }

        if (!full && amount <= 0m)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "heal requires a positive 'amount' value or full=true.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "amount",
                        Value: amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Note: "Pass --amount <hp> or --full."),
                ]);
        }

        Creature? creature;
        if (!string.IsNullOrWhiteSpace(request.TargetId))
        {
            // Explicit creature:<combatId> targeting reaches allies AND enemies via
            // CombatState.GetCreature — the same ids `sts2 state` shows.
            var combatState = CombatManager.Instance?.IsInProgress == true
                ? CombatManager.Instance.DebugOnlyGetState()
                : null;
            if (combatState is null)
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: "heal with an explicit target requires an active combat (creature:<combatId> ids are combat-scoped).");
            }

            creature = Sts2ActionHandler.ResolveCreatureByCombatId(combatState, request.TargetId);
            if (creature is null)
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.StaleId,
                    message: $"heal target '{request.TargetId}' did not resolve to a creature in the current combat.",
                    details:
                    [
                        new ActionFailureDetail(
                            Field: "target_id",
                            Value: request.TargetId ?? string.Empty,
                            Note: "Use a creature:<combatId> id from state.run.currentRoom.combat.combatState (enemies[].id / players[].creature.id)."),
                    ]);
            }
        }
        else
        {
            // Default target: the acting seat's player creature.
            var me = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
            creature = me?.Creature;
            if (creature is null)
            {
                return ActionExecutionResult.Failure(
                    kind: request.Kind,
                    code: ActionFailureCode.InvalidAction,
                    message: "heal found no player creature for the acting seat; pass an explicit creature:<combatId> target.");
            }
        }

        var label = creature.Monster?.GetType().Name
            ?? creature.Player?.Character?.GetType().Name
            ?? "creature";
        var before = creature.CurrentHp;
        var max = creature.MaxHp;
        var wasDead = creature.IsDead;

        // Fire-and-forget on the main thread (Debug.cs precedent): CreatureCmd awaits VFX/hook
        // waits internally, and blocking the action RPC on those would stall the bridge. The HP
        // write itself (HealInternal/SetCurrentHpInternal) happens synchronously inside the call.
        TaskHelper.RunSafely(full
            ? CreatureCmd.SetCurrentHp(creature, max)
            : CreatureCmd.Heal(creature, amount));

        var effect = full
            ? $"set to full HP ({max})"
            : $"healed by {amount} (clamped to max {max})";
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            $"{label}: {effect}; was {before}/{max}{(wasDead ? ", revived from dead" : string.Empty)}.");
    }

    // Per-phase game speed. Global TimeScale >1x during the PLAYER turn both stalls the action queue
    // (cards enqueue but never resolve) and multiplies CPU, so player turns + transitions run at 1x
    // (ResetGameSpeed). But the ENEMY turn is a big animation sink with NO bot queue-input, so it's
    // safe to fast-forward there (debug-speed-fast → EnemyTurnSpeed). The bot sets fast right after
    // end-turn and ResetGameSpeed runs again at the next player turn (god-mode) / on travel.
    private const double EnemyTurnSpeed = 5.0;

    private static void ResetGameSpeed()
    {
        Engine.TimeScale = 1.0;
    }

    // Fast-forward the (bot-idle) enemy turn. Fired by the auto-player right after it ends its turn.
    private ActionExecutionResult ExecuteDebugSpeedFast(SemanticActionRequest request)
    {
        Engine.TimeScale = EnemyTurnSpeed;
        return ActionExecutionResult.Success(request.RequestId, request.Kind, $"Game speed set to {EnemyTurnSpeed}x.");
    }

    private static async Task ApplyGodMode(Player player)
    {
        var creature = player.Creature;
        var choiceContext = new BlockingPlayerChoiceContext();
        ResetGameSpeed();
        // Buffer + Regen only (no Strength) — survival without trivialising combat. See note above.
        await PowerCmd.Apply<BufferPower>(choiceContext, creature, 9999m, creature, null, silent: false);
        await PowerCmd.Apply<RegenPower>(choiceContext, creature, 9999m, creature, null, silent: false);
        await CounterInsatiableSandpit(creature);
        // Free-energy cheat so the auto-player plays its WHOLE hand each turn, including high-cost /
        // 5-cost cards (otherwise the most expensive cards never get played).
        EnsureFreeEnergy(player);
        DisableEndTurnLongPress();
    }

    // The free-energy cheat leaves energy unspent every turn, so ending the turn would require the
    // long-press-to-confirm gesture (NEndTurnButton holds for 0.5s when you still have playable cards)
    // — which the browser bot can't perform with a click, leaving it stuck on "cancel-end-turn". Turn
    // the long-press preference off so a single end-turn click ends the turn (CallReleaseLogic) directly.
    private static void DisableEndTurnLongPress()
    {
        if (SaveManager.Instance?.PrefsSave is { } prefs)
        {
            prefs.IsLongPressEnabled = false;
        }
    }

    // "No energy used when playing cards": give the local player a large energy pool at the START of each
    // turn (god-mode runs every player turn) so the auto-player plays its WHOLE hand, including high-cost
    // / 5-cost cards. Set once per turn — deliberately NOT via a per-play EnergyChanged hook: restoring
    // energy synchronously inside the card-play action queue disrupted card resolution (cards registered
    // as "played" but dealt no damage, so combats never ended and the browser wedged), and it doubled the
    // per-play state broadcasts to the browser. A flat large pool per turn avoids both while still
    // affording the whole hand (25 covers any realistic hand, e.g. five 5-cost cards).
    private const int FreeEnergyAmount = 25;

    private static void EnsureFreeEnergy(Player player)
    {
        if (player.MaxEnergy < FreeEnergyAmount)
        {
            player.MaxEnergy = FreeEnergyAmount;
        }

        var combat = player.PlayerCombatState;
        if (combat is not null && combat.Energy < FreeEnergyAmount)
        {
            combat.Energy = FreeEnergyAmount;
        }
    }

    // THE_INSATIABLE (Act-2 boss) applies a SandpitPower that lives on the BOSS but targets the
    // player; it decrements by 1 each enemy turn and, when it hits 0, is removed and FORCE-kills the
    // player (CreatureCmd.Kill force:true — bypasses Buffer/HP entirely). The intended counter is to
    // play the FranticEscape status cards (+1 each). The auto-player tops the amount up ONLY when it's
    // about to empty (< 2) — a just-in-time rescue mirroring the survival card, leaving the fight
    // tense rather than trivialising it. No-op in any combat without a sandpit. Must NOT remove the
    // power (removal triggers the kill) — only increase its amount.
    private static async Task CounterInsatiableSandpit(Creature creature)
    {
        try
        {
            if (CombatManager.Instance?.IsInProgress != true)
            {
                return;
            }

            foreach (var enemy in CombatManager.Instance.DebugOnlyGetState().Enemies.ToList())
            {
                foreach (var sandpit in enemy.Powers.OfType<SandpitPower>().ToList())
                {
                    if (sandpit.Target == creature && sandpit.Amount < 2)
                    {
                        await PowerCmd.ModifyAmount(new BlockingPlayerChoiceContext(), sandpit, 5m, enemy, null, silent: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[AUTOPLAY] sandpit counter failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // Uses every usable potion the local player holds, mirroring AutoSlay's UseAllPotions. The
    // browser can throw AnyEnemy potions (it has an enemy targeting reticle), but ally/self/AoE
    // potions (AnyPlayer/AnyAlly/AllEnemies/...) have no wired browser gesture, so the auto-player
    // calls this to actually consume them. Each potion is enqueued with a sensible target by its
    // TargetType (random enemy / the player creature / none).
    private ActionExecutionResult ExecuteDebugUsePotions(SemanticActionRequest request)
    {
        if (CombatManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-use-potions requires an active combat.");
        }

        var me = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        var creature = me?.Creature;
        if (me is null || creature is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-use-potions requires an active combat creature.");
        }

        TaskHelper.RunSafely(UseAllPotionsAsync(me, creature));
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Using all usable potions.");
    }

    private static async Task UseAllPotionsAsync(Player me, Creature creature)
    {
        foreach (var potion in me.Potions.ToList())
        {
            if (CombatManager.Instance?.IsInProgress != true || !Sts2CombatFacts.IsPlayPhase(me))
            {
                break;
            }

            try
            {
                if (potion.IsQueued)
                {
                    continue;
                }

                var target = GetPotionTarget(potion, creature.CombatState as CombatState);
                // Single-target potions with no valid target can't be used; skip them.
                if (target is null && IsSingleTargetPotion(potion.TargetType))
                {
                    continue;
                }

                GD.Print($"[AUTOPLAY] potion: using {potion.Id} (targetType={potion.TargetType})");
                potion.EnqueueManualUse(target);
                await Task.Delay(400);
            }
            catch (Exception ex)
            {
                GD.Print($"[AUTOPLAY] potion: '{potion.Id}' could not be used ({ex.GetType().Name}: {ex.Message})");
            }
        }
    }

    private static bool IsSingleTargetPotion(TargetType targetType)
        => targetType is TargetType.AnyEnemy
            or TargetType.RandomEnemy
            or TargetType.AnyPlayer
            or TargetType.AnyAlly
            or TargetType.Self;

    private static Creature? GetPotionTarget(PotionModel potion, CombatState? combatState)
    {
        if (combatState is null)
        {
            return null;
        }

        return potion.TargetType switch
        {
            TargetType.AnyEnemy or TargetType.RandomEnemy => combatState.HittableEnemies?.FirstOrDefault(),
            TargetType.AnyPlayer or TargetType.AnyAlly or TargetType.Self
                => combatState.PlayerCreatures?.FirstOrDefault(c => c.IsAlive),
            _ => null,
        };
    }

    // Sets the deterministic seed override (NGame.DebugSeedOverride), which the run reads when it
    // begins (mirrors AutoSlayer line 148). Must be set BEFORE the run starts.
    private ActionExecutionResult ExecuteDebugSetSeed(SemanticActionRequest request)
    {
        string? seed = null;
        if (request.Values is not null && request.Values.TryGetValue("seed", out var value))
        {
            seed = value?.Trim();
        }

        if (string.IsNullOrWhiteSpace(seed))
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-set-seed requires a non-empty 'seed' value.",
                details:
                [
                    new ActionFailureDetail(
                        Field: "seed",
                        Value: string.Empty,
                        Note: "Provide { seed: \"<value>\" } in the action args."),
                ]);
        }

        var game = NGame.Instance;
        if (game is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-set-seed requires an initialized game (NGame.Instance is null).");
        }

        game.DebugSeedOverride = seed;
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            $"Debug seed override set to '{seed}'. Start a new run for it to take effect.");
    }

    // Bootstraps a controllable run from the main menu, which the browser cannot drive (the main menu
    // is host-local and not in the presentation catalog). Starts a SINGLEPLAYER standard run: the
    // auto-player plays solo, so singleplayer avoids co-op's uncontrolled host seat (which would stall
    // every per-player decision). Mirrors AutoSlayer.PlayMainMenuAsync. Runs as a background task.
    private ActionExecutionResult ExecuteDebugStartRun(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress == true)
        {
            return ActionExecutionResult.Success(request.RequestId, request.Kind, "A run is already in progress; nothing to start.");
        }

        string? seed = null;
        if (request.Values is not null && request.Values.TryGetValue("seed", out var value))
        {
            seed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        string? character = null;
        if (request.Values is not null && request.Values.TryGetValue("character", out var characterValue))
        {
            character = string.IsNullOrWhiteSpace(characterValue) ? null : characterValue.Trim();
        }

        TaskHelper.RunSafely(StartSingleplayerRunAsync(seed, character));
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            character is null
                ? (seed is null
                    ? "Starting a standard singleplayer run from the main menu."
                    : $"Starting a standard singleplayer run (seed '{seed}').")
                : $"Starting a standard singleplayer run (character '{character}'{(seed is null ? "" : $", seed '{seed}'")}).");
    }

    // Reliable "leave the current room / advance" for the auto-player when a room's own UI control is
    // non-functional in the browser (e.g. a finished Neow event whose proceed button is a phantom with
    // no live binding because the EventContainer scene is unobservable). NEventRoom.Proceed() opens the
    // travelable map — the same callback the in-game PROCEED option fires.
    private ActionExecutionResult ExecuteDebugAdvance(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-advance requires a run in progress.");
        }

        TaskHelper.RunSafely(NEventRoom.Proceed());
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Advanced: opened the travelable map.");
    }

    // Auto-plays the whole combat for the local player, mirroring AutoSlay's CombatRoomHandler: apply
    // survival powers, then each turn play every playable card (attacks auto-targeted at a random
    // hittable enemy) and end the turn, until combat is over. Browser combat lacks card-targeting
    // wiring (hand cards only "select"; enemies aren't clickable targets), so this is the reliable way
    // to clear combats. Runs as a background task (spans many frames).
    private ActionExecutionResult ExecuteDebugPlayCombat(SemanticActionRequest request)
    {
        if (CombatManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-play-combat requires an active combat.");
        }

        // Re-entrancy guard: the driver re-fires debug-play-combat every poll while a combat is in
        // progress. Without this, a combat that does not end promptly (see the force-win fallback in
        // PlayCombatAsync) spawns overlapping auto-players that race on the same combat state.
        if (Interlocked.CompareExchange(ref _playingCombat, 1, 0) != 0)
        {
            return ActionExecutionResult.Success(
                request.RequestId,
                request.Kind,
                "Combat auto-play already in progress.");
        }

        TaskHelper.RunSafely(PlayCombatGuardedAsync());
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Auto-playing the combat to completion.");
    }

    private static int _playingCombat;

    private static async Task PlayCombatGuardedAsync()
    {
        try
        {
            ResetGameSpeed();
            await PlayCombatAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _playingCombat, 0);
        }
    }

    private static async Task PlayCombatAsync()
    {
        var ct = CancellationToken.None;
        var player = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        var creature = player?.Creature;
        if (player is null || creature is null)
        {
            return;
        }

        // Survival cheat so the bot never dies while grinding the combat down (Buffer + Regen, NO
        // Strength — see ApplyGodMode). This whole host-side auto-play is now only a stall fallback;
        // the driver plays combats through the real browser UI (select card -> target).
        var choiceContext = new BlockingPlayerChoiceContext();
        await PowerCmd.Apply<BufferPower>(choiceContext, creature, 9999m, creature, null, silent: false);
        await PowerCmd.Apply<RegenPower>(choiceContext, creature, 9999m, creature, null, silent: false);

        // Play cards to win, mirroring AutoSlay. Card play transitions the combat -> reward UI cleanly,
        // so it's preferred. It struggles only when CardCmd.AutoPlay hangs in the headless host (cards
        // that await a player choice, or specific enemies like the Act-2 Decimillipede): each AutoPlay
        // is bounded by a per-card timeout AND the whole card-play phase by an overall timeout, so a
        // hang-heavy fight can't burn the entire watchdog budget before the force-win fallback runs.
        async Task PlayCardsAsync()
        {
            var turn = 0;
            while (CombatManager.Instance.IsInProgress && turn < 20)
            {
                turn++;
                await WaitHelper.Until(
                    () => Sts2CombatFacts.IsPlayPhase(player) || !CombatManager.Instance.IsInProgress,
                    ct,
                    TimeSpan.FromSeconds(6L),
                    "Combat play phase did not start.");
                if (!CombatManager.Instance.IsInProgress)
                {
                    break;
                }

                var attempted = new HashSet<CardModel>();
                var played = 0;
                while (played < 50 && Sts2CombatFacts.IsPlayPhase(player))
                {
                    var pile = PileType.Hand.GetPile(player);
                    var playable = pile.Cards.Where(c => c.CanPlay(out _, out _) && !attempted.Contains(c)).ToList();
                    if (playable.Count == 0)
                    {
                        break;
                    }

                    var card = playable[0];
                    attempted.Add(card);
                    try
                    {
                        await CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), card, GetRandomTarget(card))
                            .WaitAsync(TimeSpan.FromSeconds(3L), ct);
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[AUTOPLAY] combat: card '{card.Id}' did not resolve ({ex.GetType().Name}); skipping");
                    }

                    played++;
                    await Task.Delay(100, ct);
                }

                if (CombatManager.Instance.IsInProgress && Sts2CombatFacts.IsPlayPhase(player))
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                }
            }
        }

        try
        {
            await PlayCardsAsync().WaitAsync(TimeSpan.FromSeconds(45L), ct);
        }
        catch (Exception ex)
        {
            GD.Print($"[AUTOPLAY] combat: card play stopped ({ex.GetType().Name}: {ex.Message}); trying force-win");
        }

        // Force-win fallback for fights card play could not finish: kill the survivors, then END THE
        // TURN so the game runs its normal end-of-turn win -> reward resolution and the browser surfaces
        // the reward-claim controls (a mid-turn force-kill alone leaves stale combat controls in the DOM
        // and the bot can't claim/leave). Mirrors the game's "win" console command + a turn end.
        if (CombatManager.Instance.IsInProgress)
        {
            GD.Print("[AUTOPLAY] combat: forcing win (killing remaining enemies)");
            for (var pass = 0; pass < 12 && CombatManager.Instance.IsInProgress; pass++)
            {
                foreach (var enemy in CombatManager.Instance.DebugOnlyGetState().Enemies.ToList())
                {
                    enemy.RemoveAllPowersInternalExcept();
                    await CreatureCmd.Kill(enemy);
                }

                if (CombatManager.Instance.IsInProgress && Sts2CombatFacts.IsPlayPhase(player))
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                }

                await Task.Delay(500, ct);
                await CombatManager.Instance.CheckWinCondition();
            }

            GD.Print($"[AUTOPLAY] combat: force-win done (inProgress={CombatManager.Instance.IsInProgress})");
        }
    }

    private static Creature? GetRandomTarget(CardModel card)
    {
        if (card.TargetType != TargetType.AnyEnemy)
        {
            return null;
        }

        var enemies = card.CombatState?.HittableEnemies?.ToList();
        return enemies is { Count: > 0 } ? enemies[0] : null;
    }

    // Opens the merchant inventory for the current shop room. The browser hides the in-scene
    // MerchantButton (the only control that normally calls OpenInventory), so without this the shop
    // never opens and its buy/remove controls never surface. NMerchantRoom.OpenInventory() is exactly
    // what OnMerchantOpened fires when a player clicks the merchant — same path a human takes.
    private ActionExecutionResult ExecuteDebugOpenShop(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-open-shop requires a run in progress.");
        }

        var room = NMerchantRoom.Instance;
        if (room is null)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-open-shop requires the current room to be a merchant room.");
        }

        room.OpenInventory();
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Opened the merchant inventory.");
    }

    // Reliable map travel: opens the travelable map and clicks the next node (first row-0 node, or the
    // first child of the last visited node), mirroring AutoSlay's MapScreenHandler. Browser map nodes
    // aren't reliably exposed as clickable data-actions (esp. after combat), so this navigates for the
    // auto-player. Runs as a background task.
    private ActionExecutionResult ExecuteDebugTravel(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-travel requires a run in progress.");
        }

        // Re-entrancy guard: the driver re-fires debug-travel every few polls (1.5s debounce) while it
        // sees a non-combat run scene with no other progress action. A single travel — especially the
        // act transition (EnterNextAct + map rebuild + entry-enable wait) — can take well over that, so
        // without this guard a second TravelAsync starts mid-transition. Two concurrent EnterNextAct /
        // map-open / node-click sequences race on the same map screen and hang the main thread, which is
        // then terminated for unresponsiveness (the Act-1 -> Act-2 death). Only one travel runs at a time.
        if (Interlocked.CompareExchange(ref _traveling, 1, 0) != 0)
        {
            return ActionExecutionResult.Success(
                request.RequestId,
                request.Kind,
                "Travel already in progress.");
        }

        TaskHelper.RunSafely(TravelGuardedAsync(request));
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Traveling to the next map node.");
    }

    private static int _traveling;

    private async Task TravelGuardedAsync(SemanticActionRequest request)
    {
        try
        {
            ResetGameSpeed();
            // Treasure rooms have no clickable chest/relic/proceed controls surfaced to the browser,
            // so the driver can only fall through to debug-travel here. Resolve the chest first (the
            // relic is cosmetic under god-mode); otherwise the map opens over an unresolved room and
            // the run stalls (e.g. the forced Act-2 floor-9 chest).
            await ResolveTreasureRoomIfPresentAsync(request);
            await TravelAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _traveling, 0);
        }
    }

    // Resolves a current treasure room by driving the same hooks as the open-chest / proceed semantic
    // actions: close the map overlay (its presence blocks the treasure controls), open the chest, let
    // the (single) relic auto-resolve, then proceed (which opens the map for the next node). No-op when
    // the current room is not a treasure room.
    private async Task ResolveTreasureRoomIfPresentAsync(SemanticActionRequest request)
    {
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var runNode = root.GetNodeOrNull<NRun>("/root/Game/RootSceneContainer/Run");
        if (runNode is null)
        {
            return;
        }

        var treasureRoom = UiHelper.FindAll<NTreasureRoom>(runNode).FirstOrDefault();
        if (treasureRoom is null)
        {
            return;
        }

        // Skip once the chest is already claimed, so a lingering treasure-room node doesn't get
        // re-resolved on every travel poll (that caused an infinite open-chest/proceed loop).
        if (Traverse.Create(treasureRoom).Field("_hasRelicBeenClaimed").GetValue<bool>())
        {
            return;
        }

        if (NMapScreen.Instance is { IsOpen: true })
        {
            NMapScreen.Instance.Close(animateOut: false);
            await Task.Delay(400);
        }

        GD.Print("[AUTOPLAY] treasure: opening chest");
        _reusable.Execute(request with { Kind = SemanticActionKind.OpenChest });
        await Task.Delay(1500);

        // The chest's OpenChest() awaits the relic being picked (a click on the relic holder in the UI),
        // which never happens headless — so pick the single relic programmatically. Without this the
        // chest never finishes, canProceed stays false, and the bot loops re-opening it.
        try
        {
            var relicCollection = Traverse.Create(treasureRoom).Field("_relicCollection").GetValue();
            var holder = relicCollection is null
                ? null
                : Traverse.Create(relicCollection).Property("SingleplayerRelicHolder").GetValue();
            if (relicCollection is not null && holder is not null)
            {
                GD.Print("[AUTOPLAY] treasure: picking relic");
                Traverse.Create(relicCollection).Method("PickRelic", holder).GetValue();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[AUTOPLAY] treasure: relic pick failed ({ex.GetType().Name}: {ex.Message})");
        }

        GD.Print("[AUTOPLAY] treasure: proceeding");
        _reusable.Execute(request with { Kind = SemanticActionKind.ProceedTreasureRoom });
        await Task.Delay(800);
    }

    private static async Task TravelAsync()
    {
        var ct = CancellationToken.None;
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var runNode = root.GetNodeOrNull<NRun>("/root/Game/RootSceneContainer/Run");
        if (runNode is null || NMapScreen.Instance is null)
        {
            return;
        }

        NMapScreen.Instance.SetTravelEnabled(enabled: true);
        NMapScreen.Instance.Open();

        var mapScreen = runNode.GlobalUi.MapScreen;
        await WaitHelper.Until(() => mapScreen.IsVisibleInTree(), ct, TimeSpan.FromSeconds(10L), "Map screen did not become visible.");

        var points = UiHelper.FindAll<NMapPoint>(mapScreen);
        var runState = RunManager.Instance.DebugOnlyGetState();

        // Candidate next nodes: row-0 nodes at run start, otherwise the children of the last visited node.
        List<NMapPoint> candidates;
        if (runState.VisitedMapCoords.Count == 0)
        {
            candidates = points.Where(mp => mp.Point.coord.row == 0).ToList();
        }
        else
        {
            var lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
            var lastPoint = points.FirstOrDefault(mp => mp.Point.coord.Equals(lastCoord));
            candidates = lastPoint?.Point.Children
                .Select(c => points.FirstOrDefault(mp => mp.Point.coord.Equals(c.coord)))
                .Where(mp => mp is not null)
                .Select(mp => mp!)
                .ToList() ?? new List<NMapPoint>();
        }

        // Prefer the node types the bot reliably clears (Monster/Elite/RestSite/Shop). Avoid:
        // - Ancient/Unknown events (Ancient can crash the browser stack; Unknown is unpredictable),
        // - Treasure rooms: their chest/relic controls aren't surfaced as clickable browser actions,
        //   so the auto-player can't resolve them (the relic is cosmetic anyway under god-mode).
        // Fall back to any candidate only when no safer node is reachable.
        NMapPoint? PickPreferred(IEnumerable<NMapPoint> pool)
        {
            var list = pool.ToList();
            return list.FirstOrDefault(mp => mp.Point.PointType != MapPointType.Ancient
                    && mp.Point.PointType != MapPointType.Unknown
                    && mp.Point.PointType != MapPointType.Treasure)
                ?? list.FirstOrDefault();
        }

        GD.Print($"[AUTOPLAY] travel: visited={runState.VisitedMapCoords.Count} candidates=[{string.Join(",", candidates.Select(c => c.Point.PointType))}]");
        var nextRoom = PickPreferred(candidates);
        GD.Print($"[AUTOPLAY] travel: chose {(nextRoom is null ? "null(->EnterNextAct)" : nextRoom.Point.PointType.ToString())}");

        if (nextRoom is null)
        {
            // No next node in the current act's map (the act boss is defeated) — advance to the next
            // act and pick a starting (bottom-row) node of the new map.
            await RunManager.Instance.EnterNextAct();
            await Task.Delay(800, ct);
            NMapScreen.Instance.SetTravelEnabled(enabled: true);
            NMapScreen.Instance.Open();
            await WaitHelper.Until(() => mapScreen.IsVisibleInTree(), ct, TimeSpan.FromSeconds(10L), "Next-act map did not become visible.");

            // Give every bottom-row entry node time to enable so event-avoidance has the full set to
            // choose from (picking too early can force us onto an Ancient entry node that crashes).
            for (var wait = 0; wait < 12; wait++)
            {
                points = UiHelper.FindAll<NMapPoint>(mapScreen);
                var minRow = points.Count > 0 ? points.Min(mp => mp.Point.coord.row) : 0;
                var entryEnabled = points.Where(mp => mp.Point.coord.row == minRow && mp.IsEnabled).ToList();
                GD.Print($"[AUTOPLAY] next-act: wait={wait} minRow={minRow} entries=[{string.Join(",", points.Where(mp => mp.Point.coord.row == minRow).Select(mp => $"{mp.Point.PointType}:{(mp.IsEnabled ? "en" : "dis")}"))}]");
                var safe = PickPreferred(entryEnabled.Where(mp => mp.Point.PointType != MapPointType.Ancient && mp.Point.PointType != MapPointType.Unknown).ToList());
                if (safe is not null)
                {
                    nextRoom = safe;
                    break;
                }

                await Task.Delay(500, ct);
            }

            if (nextRoom is null)
            {
                // No safe entry surfaced — fall back to any enabled entry/node (may be an event).
                points = UiHelper.FindAll<NMapPoint>(mapScreen);
                var minRow = points.Count > 0 ? points.Min(mp => mp.Point.coord.row) : 0;
                nextRoom = PickPreferred(points.Where(mp => mp.Point.coord.row == minRow && mp.IsEnabled))
                    ?? PickPreferred(points.Where(mp => mp.IsEnabled));
                GD.Print($"[AUTOPLAY] next-act: fallback chose {(nextRoom is null ? "null" : nextRoom.Point.PointType.ToString())}");
            }
            else
            {
                GD.Print($"[AUTOPLAY] next-act: chose {nextRoom.Point.PointType}");
            }

            if (nextRoom is null)
            {
                return;
            }
        }

        await WaitHelper.Until(() => nextRoom.IsEnabled, ct, TimeSpan.FromSeconds(10L), "Next map point did not become enabled.");
        await UiHelper.Click(nextRoom);

        // Close the travel map after committing to the node. Otherwise the map overlay can linger into
        // the next room — e.g. a combat starts with the map still open, which then covers the
        // post-combat reward controls and stalls the run.
        await Task.Delay(500, ct);
        if (NMapScreen.Instance is { IsOpen: true })
        {
            NMapScreen.Instance.Close(animateOut: false);
        }
    }

    // Resolves the current event room by clicking its real option buttons until it finishes, mirroring
    // AutoSlay's EventRoomHandler. Some events (e.g. Ancient events like Tezcatara) don't resolve via
    // the browser's select-event-option, so this drives the actual NEventOptionButton controls. Handles
    // multi-page events and event-spawned combat. Runs as a background task.
    private ActionExecutionResult ExecuteDebugResolveEvent(SemanticActionRequest request)
    {
        if (RunManager.Instance?.IsInProgress != true)
        {
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.InvalidAction,
                message: "debug-resolve-event requires a run in progress.");
        }

        // Re-entrancy guard: the driver clicks debug-resolve-event every poll while an event room is
        // visible. Without this, each click spawns another ResolveEventAsync; dozens of concurrent
        // resolvers race on the same event model (ChooseLocalOption / Proceed / overlay drain) and can
        // hang the game's main thread, which then gets terminated for unresponsiveness. Only one
        // resolver runs at a time.
        if (Interlocked.CompareExchange(ref _resolvingEvent, 1, 0) != 0)
        {
            return ActionExecutionResult.Success(
                request.RequestId,
                request.Kind,
                "Event resolution already in progress.");
        }

        TaskHelper.RunSafely(ResolveEventGuardedAsync());
        return ActionExecutionResult.Success(
            request.RequestId,
            request.Kind,
            "Resolving the current event.");
    }

    private static int _resolvingEvent;

    private static async Task ResolveEventGuardedAsync()
    {
        try
        {
            ResetGameSpeed();
            await ResolveEventAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _resolvingEvent, 0);
        }
    }

    private static async Task ResolveEventAsync()
    {
        var ct = CancellationToken.None;
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        const string eventRoomPath = "/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom";

        for (var i = 0; i < 60; i++)
        {
            var eventRoom = root.GetNodeOrNull(eventRoomPath);
            if (eventRoom is null)
            {
                // The event ended, or it spawned a combat. If a combat is active, clear it, then the
                // event may resume (loop) or be finished.
                if (CombatManager.Instance?.IsInProgress == true)
                {
                    GD.Print("[AUTOPLAY] resolve-event: event spawned combat, clearing");
                    await PlayCombatAsync();
                    await Task.Delay(600, ct);
                    continue;
                }

                GD.Print($"[AUTOPLAY] resolve-event: event room gone, done (iter {i})");
                return;
            }

            // Resolve at the MODEL layer via the EventSynchronizer rather than clicking the
            // NEventOptionButton controls. For Ancient events (Tezcatara, ...) the option button builds
            // a relic-icon TextureRect (NEventOptionButton: SetTexture(Option.Relic.Icon)) which crashes
            // the headless game; choosing an option by index never constructs that UI. This mirrors what
            // OptionButtonClicked does (RunManager.EventSynchronizer.ChooseLocalOption).
            var sync = RunManager.Instance?.EventSynchronizer;
            EventModel? ev = null;
            try { ev = sync?.GetLocalEvent(); }
            catch { ev = null; }

            if (sync is null || ev is null)
            {
                await Task.Delay(400, ct);
                continue;
            }

            var options = ev.CurrentOptions;
            var unlocked = options is null
                ? new List<(EventOption opt, int index)>()
                : options.Select((o, idx) => (opt: o, index: idx)).Where(t => !t.opt.IsLocked).ToList();

            // Finished, or nothing left but proceed: leave the room via the map.
            var nonProceed = unlocked.Where(t => !t.opt.IsProceed).ToList();
            if (ev.IsFinished || unlocked.Count == 0 || nonProceed.Count == 0)
            {
                GD.Print($"[AUTOPLAY] resolve-event: finished={ev.IsFinished} unlocked={unlocked.Count} -> proceeding (iter {i})");
                await NEventRoom.Proceed();
                await Task.Delay(500, ct);
                return;
            }

            // Prefer non-lethal options.
            var nonLethal = nonProceed
                .Where(t => t.opt.WillKillPlayer is null || ev.Owner is null || !t.opt.WillKillPlayer(ev.Owner))
                .ToList();
            var pool = nonLethal.Count > 0 ? nonLethal : nonProceed;
            var choice = pool[0];

            // The Act-3 finale (THE_ARCHITECT) is a dialogue ancient: each "Continuar" advances a line,
            // and the final non-proceed option (textKey "PROCEED") calls WinRun() which ends the run via
            // the act-change transition. Choosing it is the win — do it once and STOP. Re-resolving after
            // it spawns hundreds of phantom overlays and wedges the run.
            var isWinProceed = string.Equals(choice.opt.TextKey, "PROCEED", StringComparison.OrdinalIgnoreCase);

            GD.Print($"[AUTOPLAY] resolve-event: choosing index {choice.index} '{choice.opt.Title.GetFormattedText()}' textKey={choice.opt.TextKey} (iter {i}){(isWinProceed ? " [WIN]" : "")}");
            sync.ChooseLocalOption(choice.index);

            if (isWinProceed)
            {
                GD.Print("[AUTOPLAY] resolve-event: chose finale PROCEED (WinRun) -> done");
                await Task.Delay(1500, ct);
                return;
            }

            // Dialogue advances on an animation timer; wait long enough for the line to advance before
            // re-reading, otherwise we re-pick the same stale option and pile up concurrent advances.
            await Task.Delay(2500, ct);

            // Choosing an option can open an overlay (e.g. Tezcatara opens a card upgrade/remove
            // selection) or start a combat. Drain overlays so the event can finish.
            await DrainEventOverlaysAsync(ct);
        }

        GD.Print("[AUTOPLAY] resolve-event: hit iteration limit");
    }

    // Auto-completes any overlay screen opened during an event (card selection / reward picks) by
    // selecting the required number of cards then confirming. Mirrors AutoSlay's DrainOverlayScreens
    // for the common case of "select N cards" overlays that Tezcatara-style relics trigger.
    private static async Task DrainEventOverlaysAsync(CancellationToken ct)
    {
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        for (var guard = 0; guard < 20; guard++)
        {
            var stack = NOverlayStack.Instance;
            if (stack is null || stack.ScreenCount == 0)
            {
                return;
            }

            GD.Print($"[AUTOPLAY] drain-overlay: {stack.ScreenCount} screen(s) open");

            // Select offered cards (FindAll<NCardSelectButton>-style controls) then confirm.
            var cards = UiHelper.FindAll<NClickableControl>(stack)
                .Where(c => c.IsEnabled && c.Visible && c.Name.ToString().Contains("Card"))
                .ToList();
            var confirm = UiHelper.FindFirst<NButton>(stack);
            var clickedSomething = false;
            foreach (var card in cards.Take(8))
            {
                await UiHelper.Click(card);
                await Task.Delay(200, ct);
                clickedSomething = true;
                if (NOverlayStack.Instance is null || NOverlayStack.Instance.ScreenCount == 0)
                {
                    return;
                }
            }

            // Try to confirm/close the overlay.
            var confirmBtn = UiHelper.FindAll<NButton>(stack)
                .FirstOrDefault(b => b.IsEnabled && b.Visible
                    && (b.Name.ToString().Contains("Confirm") || b.Name.ToString().Contains("Proceed") || b.Name.ToString().Contains("Done") || b.Name.ToString().Contains("Skip")));
            if (confirmBtn is not null)
            {
                GD.Print($"[AUTOPLAY] drain-overlay: confirming via '{confirmBtn.Name}'");
                await UiHelper.Click(confirmBtn);
                clickedSomething = true;
            }

            if (!clickedSomething)
            {
                GD.Print("[AUTOPLAY] drain-overlay: nothing clickable, giving up");
                return;
            }

            await Task.Delay(500, ct);
        }
    }

    private static async Task StartSingleplayerRunAsync(string? seed, string? character = null)
    {
        var ct = CancellationToken.None;
        ResetGameSpeed();
        SaveManager.Instance.SetFtuesEnabled(enabled: false);
        DisableEndTurnLongPress();

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var mainMenu = await WaitHelper.ForNode<Control>(root, "/root/Game/RootSceneContainer/MainMenu", ct, TimeSpan.FromSeconds(30L));

        var singleplayer = mainMenu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton");
        await UiHelper.Click(singleplayer);

        Control? charSelect = null;
        NButton? standard = null;
        await WaitHelper.Until(
            () =>
            {
                charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
                standard = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
                return (charSelect?.Visible ?? false) || (standard?.Visible ?? false);
            },
            ct,
            TimeSpan.FromSeconds(10L),
            "Neither CharacterSelectScreen nor SingleplayerSubmenu became visible");

        if ((standard?.Visible ?? false) && !(charSelect?.Visible ?? false))
        {
            await UiHelper.Click(standard!);
            await WaitHelper.Until(
                () => mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.Visible ?? false,
                ct,
                TimeSpan.FromSeconds(10L),
                "CharacterSelectScreen did not become visible");
            charSelect = mainMenu.GetNode<Control>("Submenus/CharacterSelectScreen");
        }

        var buttonContainer = charSelect!.GetNode("CharSelectButtons/ButtonContainer");
        var characterButtons = UiHelper.FindAll<NCharacterSelectButton>(buttonContainer);
        foreach (var button in characterButtons)
        {
            button.UnlockIfPossible();
        }

        var unlocked = characterButtons.Where(b => !b.IsLocked).ToList();
        // Honor an explicit character request (e.g. "SILENT") by matching the button's CharacterModel id
        // (normalized so "silent"/"Silent"/"SILENT" all match); otherwise default to the first unlocked.
        NCharacterSelectButton? pick = null;
        if (!string.IsNullOrWhiteSpace(character))
        {
            pick = (unlocked.Count > 0 ? unlocked : characterButtons)
                .FirstOrDefault(b => b.Character is { } model
                    && Sts2ModelResolver.MatchesFixtureIdAlias(model.Id.Entry, character));
            if (pick is null)
            {
                GD.Print($"[AUTOPLAY] start-run: requested character '{character}' not found/unlocked; using default.");
            }
        }

        pick ??= unlocked.Count > 0 ? unlocked[0] : characterButtons.FirstOrDefault();
        pick?.Select();
        await Task.Delay(100, ct);

        // Apply the seed AFTER character-select init (which resets DebugSeedOverride) and right before
        // confirming, so the starting run actually consumes it.
        if (seed is not null && NGame.Instance is not null)
        {
            NGame.Instance.DebugSeedOverride = seed;
        }

        var confirm = await WaitHelper.ForNode<NButton>(mainMenu, "Submenus/CharacterSelectScreen/ConfirmButton", ct);
        await UiHelper.Click(confirm);
    }
}
