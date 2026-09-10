using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler
{
    internal static ActionExecutionResult ExecuteMainMenuChoice(
        SemanticActionRequest request,
        ScreenLocatorResult screen,
        object? screenObject,
        ILogStream logStream)
    {
        var choiceId = request.ChoiceId?.Trim() ?? string.Empty;
        if (!Sts2ActionCatalog.IsExecutableChoiceId(screen.ScreenType, choiceId))
        {
            return ChoiceActionUnavailable(
                request.Kind,
                "choose requires a visible choice id from the current screen.",
                "choice_id",
                choiceId,
                $"Choice '{choiceId}' is not executable on screen '{screen.ScreenType}' in this spec.");
        }

        var inspection = Sts2MainMenuStartRunHooks.Inspect(screenObject);
        if (!inspection.HasCallableHook || !Sts2MainMenuStartRunHooks.TryInvoke(screenObject, inspection))
        {
            logStream.Write(
                BridgeLogLevel.Error,
                "bridge.action",
                $"No callable start-run hook was found for choice '{choiceId}'. Active screen class: {inspection.ActiveScreenClassName}. Resolved hook path: {inspection.ResolvedHookPath ?? "none"}. Checked probe paths: {FormatCheckedProbePaths(inspection)}. Present candidates: {FormatPresentCandidates(inspection)}.");
            return ActionExecutionResult.Failure(
                kind: request.Kind,
                code: ActionFailureCode.MissingHook,
                message: "Could not execute the requested visible choice through the active live hooks.",
                details: MainMenuStartRunFailureDetails(choiceId, inspection),
                screen: screen.ScreenType,
                checkedHookPaths: inspection.CheckedProbePaths,
                fieldDiagnostics: MainMenuStartRunFieldDiagnostics(inspection));
        }

        logStream.Write(
            BridgeLogLevel.Info,
            "bridge.action",
            $"Executed choose '{choiceId}'.");
        return ActionExecutionResult.Success(
            actionInstanceId: $"action:choose:{request.RequestId}",
            kind: request.Kind,
            message: $"Executed choice '{choiceId}'.");
    }

    private static bool TryInvokeEndTurn(Player? player)
    {
        var combatManager = CombatManager.Instance;
        if (combatManager is null)
        {
            return false;
        }

        // Primary path: the multiplayer-aware ready-to-end-turn signal the live
        // end-turn button uses (NEndTurnButton enqueues an EndPlayerTurnAction ->
        // PlayerCmd.EndTurn(player, canBackOut: true) -> CombatManager.SetReadyToEndTurn).
        // canBackOut MUST be true to mirror the game: the seat becomes ready and the
        // turn only resolves once every living player is ready, so the player can still
        // back out via cancel-end-turn while allies are going. canBackOut:false is the
        // irrevocable-commit path that discards the hand immediately.
        if (player is not null)
        {
            try
            {
                combatManager.SetReadyToEndTurn(player, canBackOut: true);
                return true;
            }
            catch
            {
                // Fall through to legacy reflection-based probes.
            }
        }

        if (Sts2LiveIntrospection.TryInvokeParameterlessMethod(combatManager, "RequestEndTurn", "TryEndTurn", "EndTurn", "OnEndTurnPressed"))
        {
            return true;
        }

        var turnController = Sts2LiveIntrospection.GetMemberValue(combatManager, "TurnController")
            ?? Sts2LiveIntrospection.GetMemberValue(combatManager, "TurnManager");
        if (Sts2LiveIntrospection.TryInvokeParameterlessMethod(turnController, "RequestEndTurn", "TryEndTurn", "EndTurn"))
        {
            return true;
        }

        return false;
    }

    private static bool TryInvokeCancelEndTurn(Player? player)
    {
        var combatManager = CombatManager.Instance;
        if (combatManager is null)
        {
            return false;
        }

        // Inverse of TryInvokeEndTurn: the live button enqueues an UndoEndPlayerTurnAction
        // which calls CombatManager.UndoReadyToEndTurn(player), clearing the ready state and
        // un-darkening the hand while allies are still taking their turn.
        if (player is not null)
        {
            try
            {
                combatManager.UndoReadyToEndTurn(player);
                return true;
            }
            catch
            {
                // Fall through to reflection-based probes for differing game builds.
            }

            foreach (var methodName in new[] { "UndoReadyToEndTurn", "CancelEndTurn", "CancelReadyToEndTurn", "UnreadyToEndTurn" })
            {
                if (Sts2LiveIntrospection.TryInvokeMethod(combatManager, methodName, player))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ResolvedCombatCard? ResolveLocalHandCard(Player player, string playerId, string requestedCardId)
    {
        foreach (var (card, index) in (player.PlayerCombatState?.Hand?.Cards ?? []).Select((card, index) => (card, index)))
        {
            var stableCardId = Sts2CombatIds.CardId(card, playerId, index);
            if (!string.Equals(stableCardId, requestedCardId, StringComparison.Ordinal))
            {
                continue;
            }

            return new ResolvedCombatCard(card, stableCardId, ResolveCardName(card));
        }

        return null;
    }

    private static ResolvedCombatPotion? ResolveLocalPotion(Player player, string playerId, string requestedPotionId)
    {
        var trimmed = requestedPotionId.Trim();
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotIndex)
            || slotIndex < 0)
        {
            const string stablePrefix = "potion:";
            if (!trimmed.StartsWith(stablePrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var parts = trimmed.Split(':');
            if (parts.Length < 4
                || !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out slotIndex)
                || slotIndex < 0)
            {
                return null;
            }
        }

        var potion = player.GetPotionAtSlotIndex(slotIndex);
        if (potion is null)
        {
            return null;
        }

        var stablePotionId = Sts2CombatIds.PotionId(potion, playerId, slotIndex);
        if (trimmed.StartsWith("potion:", StringComparison.Ordinal)
            && !string.Equals(trimmed, stablePotionId, StringComparison.Ordinal)
            && !string.Equals(trimmed, StatePotionId(potion, playerId, slotIndex), StringComparison.Ordinal))
        {
            return null;
        }

        return new ResolvedCombatPotion(potion, stablePotionId, ResolvePotionName(potion), slotIndex);
    }

    private static string StatePotionId(PotionModel potion, string playerId, int slotIndex)
        => $"potion:{playerId}:{slotIndex}:{Sts2StateProvider.ResolveModelId(potion) ?? potion.GetType().Name}";

    private static IReadOnlyList<string> ResolveTargetIds(CardModel card, CombatState combatState)
    {
        // Enemy-targeting cards aim at enemies; ally/player-targeting cards (AnyAlly/AnyPlayer,
        // e.g. Believe in You) aim at ally creatures — the game's own CanPlayTargeting validator
        // decides per creature, so AnyAlly self-exclusion and AnyPlayer inclusion fall out for
        // free, and an enemy-only card returns false for every ally (and vice versa). Creature
        // ids are creature:{CombatId} (globally unique), so concatenating the two sides is safe.
        var enemyIds = combatState.Enemies
            .Select((enemy, index) => new { Creature = enemy, Id = Sts2CombatIds.CreatureId(enemy, index) })
            .Where(entry => entry.Creature.IsAlive && card.CanPlayTargeting(entry.Creature))
            .Select(entry => entry.Id);
        var allyIds = combatState.Allies
            .Select((ally, index) => new { Creature = ally, Id = Sts2CombatIds.CreatureId(ally, index) })
            .Where(entry => entry.Creature.IsAlive && card.CanPlayTargeting(entry.Creature))
            .Select(entry => entry.Id);
        return enemyIds.Concat(allyIds).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static ResolvedCombatTarget? ResolveCombatTarget(CombatState combatState, string? requestedTargetId)
    {
        var creature = ResolveCreatureByCombatId(combatState, requestedTargetId);
        if (creature is null)
        {
            return null;
        }

        var requested = requestedTargetId!.Trim();
        var name = creature.Monster?.GetType().Name
            ?? creature.Player?.Character?.GetType().Name
            ?? requested;
        return new ResolvedCombatTarget(creature, requested, name);
    }

    internal static Creature? ResolveCreatureByCombatId(CombatState combatState, string? requestedTargetId)
    {
        if (string.IsNullOrWhiteSpace(requestedTargetId))
        {
            return null;
        }

        var trimmed = requestedTargetId.Trim();
        const string prefix = "creature:";
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)
            || !uint.TryParse(trimmed[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var combatId))
        {
            return null;
        }

        return combatState.GetCreature(combatId);
    }

    private static IReadOnlyList<string> ResolvePotionTargetIds(object potion, string playerId, CombatState combatState)
    {
        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        return targetType switch
        {
            "Self" => combatState.Allies
                .Where(creature => creature.Player is not null && IsPlayerId(creature.Player!, playerId))
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index))
                .ToArray(),
            "AnyEnemy" => combatState.Enemies
                .Select((enemy, index) => new { enemy, id = Sts2CombatIds.CreatureId(enemy, index) })
                .Where(entry => entry.enemy.IsAlive && SafeBool(() => ((dynamic)potion).CanPlayTargeting(entry.enemy), fallback: true))
                .Select(entry => entry.id)
                .ToArray(),
            // AnyPlayer throw potions (Energy, Lucky Tonic, …) can land on ANY living player,
            // including the thrower (NTargetManager: IsPlayer && !IsDead).
            "AnyPlayer" => combatState.Allies
                .Where(creature => creature.Player is not null && creature.IsAlive)
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            // AnyAlly EXCLUDES the thrower (NTargetManager: !LocalContext.IsMe). No shipped potion
            // is AnyAlly today, but keep the rule faithful so a future one targets only teammates.
            "AnyAlly" => combatState.Allies
                .Where(creature => creature.Player is not null && creature.IsAlive && !IsPlayerId(creature.Player!, playerId))
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            _ => [],
        };
    }

    private static bool IsPlayerId(Player player, string playerId)
        => string.Equals(Sts2CombatIds.PlayerId(player), playerId, StringComparison.Ordinal);

    private static bool PotionNeedsTarget(object potion)
    {
        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        return targetType is "Self" or "AnyEnemy" or "AnyPlayer" or "AnyAlly";
    }

    private static bool PotionCanBeUsedManually(object potion)
    {
        var usage = Sts2LiveIntrospection.GetMemberValue(potion, "Usage")?.ToString();
        return !string.Equals(usage, "Automatic", StringComparison.Ordinal);
    }

    private static ResolvedCombatTarget? ResolvePotionTarget(CombatState combatState, string? requestedTargetId)
        // Potion targets (enemy, ally, or self) are all addressed by creature:{CombatId}; a single
        // CombatState.GetCreature lookup resolves them uniformly.
        => ResolveCombatTarget(combatState, requestedTargetId);

    private static string ResolveCardName(CardModel card)
    {
        return Sts2LiveIntrospection.GetMemberValue(card, "Title")?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(card, "DisplayName")?.ToString()
            ?? card.GetType().Name;
    }

    private static string ResolvePotionName(PotionModel potion)
    {
        return Sts2LiveIntrospection.GetMemberValue(potion, "Title")?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(potion, "DisplayName")?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(potion, "Id"), "Entry")?.ToString()
            ?? potion.GetType().Name;
    }

    private static bool SafeBool(Func<bool> getter, bool fallback = false)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static string? ResolveLocalPlayerId()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState is null)
            {
                return null;
            }

            var player = LocalContext.GetMe(runState);
            return player is null
                ? null
                : Sts2LiveIntrospection.GetMemberValue(player, "NetId")?.ToString() is { Length: > 0 } netId
                    ? $"p:{netId}"
                    : $"p:{player.GetHashCode()}";
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetReady(StartRunLobby lobby, bool ready)
    {
        lobby.SetReady(ready);
        return true;
    }

    private static bool TrySetReady(LoadRunLobby lobby, bool ready)
    {
        lobby.SetReady(ready);
        return true;
    }

    private static bool TryInvokeCardSelectionAlternative(
        object screenObject,
        ResolvedCardSelectionChoice choice)
    {
        if (choice.Alternative is null
            || !Sts2LiveIntrospection.TryInvokeMethod(
                screenObject,
                "OnAlternateRewardSelected",
                Sts2LiveIntrospection.GetMemberValue(choice.Alternative, "AfterSelected")!))
        {
            return false;
        }

        if (Sts2LiveIntrospection.GetMemberValue(choice.Alternative, "OnSelect") is not Func<Task> onSelect)
        {
            return false;
        }

        TaskHelper.RunSafely(onSelect());
        return true;
    }

    private static bool TryExecuteBundleSelectionChoice(
        object screenObject,
        ResolvedCardSelectionChoice choice)
    {
        if (!Sts2LiveIntrospection.TryInvokeMethod(screenObject, "OnBundleClicked", choice.Target))
        {
            return false;
        }

        var confirmButton = Sts2LiveIntrospection.GetMemberValue(screenObject, "_previewConfirmButton")
            ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "PreviewConfirmButton");
        return confirmButton is not null
            && Sts2LiveIntrospection.TryInvokeMethod(screenObject, "ConfirmSelection", confirmButton);
    }

    private bool TryResolveCombatContext(string? requestedPlayerId, [NotNullWhen(true)] out CombatActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var combatManager = CombatManager.Instance;
        if (screen.ScreenType != "combat" || combatManager is null || !combatManager.IsInProgress)
        {
            context = null;
            return false;
        }

        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState is null)
            {
                context = null;
                return false;
            }

            var localPlayer = LocalContext.GetMe(runState);
            if (localPlayer is null)
            {
                context = null;
                return false;
            }

            var localPlayerId = Sts2CombatIds.PlayerId(localPlayer);
            var hostPlayerId = ResolveRunHostPlayerId(localPlayerId);
            var playersById = ((IEnumerable<Player>)runState.Players)
                .ToDictionary(Sts2CombatIds.PlayerId, StringComparer.Ordinal);
            var hostLocalPlayerIds = playersById
                .Where(entry => IsHostLocalPlayerId(entry.Key))
                .Select(entry => entry.Key)
                .ToArray();
            var requestedPlayerIsHostLocal = hostLocalPlayerIds.Contains(requestedPlayerId ?? string.Empty, StringComparer.Ordinal);
            var requestedPlayerIsLocal = string.Equals(requestedPlayerId, localPlayerId, StringComparison.Ordinal);
            var actionOwnerPlayerId = !string.IsNullOrWhiteSpace(requestedPlayerId)
                && playersById.ContainsKey(requestedPlayerId)
                && (requestedPlayerIsLocal || requestedPlayerIsHostLocal)
                ? requestedPlayerId
                : localPlayerId;
            var actionOwnerPlayer = playersById.GetValueOrDefault(actionOwnerPlayerId) ?? localPlayer;

            context = new CombatActionContext(
                Screen: screen,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                HostLocalPlayerIds: hostLocalPlayerIds,
                ActionOwnerPlayerId: actionOwnerPlayerId,
                ActionOwnerPlayer: actionOwnerPlayer,
                IsActionOwnerHostLocalSeat: hostLocalPlayerIds.Contains(actionOwnerPlayerId, StringComparer.Ordinal),
                LocalPlayer: localPlayer,
                CombatState: combatManager.DebugOnlyGetState()!);
            return true;
        }
        catch
        {
            context = null;
            return false;
        }
    }

    private bool TryResolveMapContext([NotNullWhen(true)] out MapActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var mapScreen = NMapScreen.Instance;
        // The browser renders the map as an OVERLAY over the run scene, so the located screen is the
        // room ("run"), never the map screen — requiring IsMapScreenType here made select-map-node
        // permanently unavailable to browser play. Resolve the context whenever the live map is open;
        // CanSelectMapNode (IsTravelEnabled + travelable) is the real gate on whether a vote is allowed.
        if (mapScreen is null || !mapScreen.IsOpen)
        {
            context = null;
            return false;
        }

        var nodesById = Sts2MapScreenInspector.ResolveNodes(mapScreen)
            .ToDictionary(node => node.Snapshot.Id, StringComparer.Ordinal);
        var localPlayerId = ResolveLocalPlayerId();
        var choicesById = Sts2MapScreenInspector.ResolveFlowChoices(mapScreen, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);

        context = new MapActionContext(
            Screen: screen,
            LocalPlayerId: localPlayerId,
            MapScreen: mapScreen,
            NodesById: nodesById,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveRewardContext([NotNullWhen(true)] out RewardActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var localPlayerId = ResolveLocalPlayerId();
        if (!Sts2SupportedScreenIds.IsRewardsScreenType(screen.ScreenType)
            || screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen"))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2RewardScreenInspector.ResolveChoices(screenObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new RewardActionContext(
            Screen: screen,
            ScreenObject: screenObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveEventRoomContext([NotNullWhen(true)] out EventRoomActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var roomObject = Sts2EventRoomScreenInspector.ResolveActiveRoom();
        var localPlayerId = ResolveLocalPlayerId();
        if (!Sts2SupportedScreenIds.IsEventRoomScreenType(screen.ScreenType)
            || roomObject is null
            || !Sts2LiveIntrospection.IsType(roomObject, Sts2EventRoomScreenInspector.EventRoomType))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2EventRoomScreenInspector.ResolveChoices(roomObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new EventRoomActionContext(
            Screen: screen,
            RoomObject: roomObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveTreasureRoomContext([NotNullWhen(true)] out TreasureRoomActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var roomObject = Sts2TreasureRoomScreenInspector.ResolveActiveRoom(screenObject);
        var relicCollection = Sts2TreasureRoomScreenInspector.ResolveActiveRelicCollection(screenObject, roomObject);
        var localPlayerId = ResolveLocalPlayerId();
        // Browser mode renders the treasure room as an overlay on the run screen, so the located
        // screen type is "run", not a treasure-room screen. Gate on the actual live treasure room /
        // relic collection being resolvable instead of the screen-type family (mirrors the map-context
        // relaxation); the per-choice IsExecutable checks below still gate what can be pressed.
        if (roomObject is null && relicCollection is null)
        {
            context = null;
            return false;
        }

        var choicesById = Sts2TreasureRoomScreenInspector.ResolveChoices(roomObject, screenObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new TreasureRoomActionContext(
            Screen: screen,
            RoomObject: roomObject,
            RelicCollection: relicCollection,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveRestSiteContext([NotNullWhen(true)] out RestSiteActionContext? context)
    {
        var screen = _screenLocator.Locate();
        // Browser mode renders the rest site as an overlay on the run screen, so the located screen
        // object is the run screen, not the rest-site room. Resolve the room from the screen object when
        // it IS the rest site, else search the run tree downward (mirrors the treasure-room resolution);
        // gate on the room being resolvable rather than the screen-type family, with per-option
        // IsExecutable checks still gating presses.
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var roomObject = Sts2LiveIntrospection.IsType(screenObject, Sts2RestSiteScreenInspector.RestSiteRoomType)
            ? screenObject
            : Sts2LiveIntrospection.FindRunTreeNodeOfType(Sts2RestSiteScreenInspector.RestSiteRoomType);
        var localPlayerId = ResolveLocalPlayerId();
        if (roomObject is null
            || !Sts2LiveIntrospection.IsType(roomObject, Sts2RestSiteScreenInspector.RestSiteRoomType))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2RestSiteScreenInspector.ResolveChoices(roomObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new RestSiteActionContext(
            Screen: screen,
            RoomObject: roomObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveShopContext([NotNullWhen(true)] out ShopActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2ShopScreenInspector.ResolveActiveShopScreenObject();
        var localPlayerId = ResolveLocalPlayerId();
        var inventory = Sts2LiveIntrospection.GetMemberValue(screenObject, "Inventory");
        if (!Sts2SupportedScreenIds.IsShopScreenType(screen.ScreenType)
            || screenObject is null
            || inventory is null
            || !Sts2ShopScreenInspector.IsShopScreen(screenObject))
        {
            // Screen-independent fallback: in a merchant room whose inventory SCREEN isn't open (couch-coop
            // never force-opens it), the MerchantInventory MODEL still lives on the room and the purchase
            // (entry.OnTryPurchaseWrapper) is screen-independent — resolve + buy off the model directly so a
            // seat can buy without us forcing the shop overlay open. Mirrors the per-player reward commit.
            return TryResolveShopContextFromModel(screen, localPlayerId, out context);
        }

        // Key by the live screen-slot ids (authoritative: these carry the leave/close flow choice with a
        // real back-button Control and per-slot player ownership, and match availableActions[].shopItemId).
        var choicesById = new Dictionary<string, ResolvedShopChoice>(StringComparer.Ordinal);
        foreach (var choice in Sts2ShopScreenInspector.ResolveChoices(screenObject, localPlayerId))
        {
            choicesById[choice.Snapshot.Id] = choice;
        }

        // Also alias each purchasable choice by its canonical inventory-model id (shop:{collection}:{index}),
        // which is what the browser sends (it binds run.currentRoom.shop.inventory.*Entries[].id). add-if-absent
        // so the real back-button flow choice (shop:leave) is never overwritten by the synthetic one. These
        // choices read entries directly off this same `inventory`, so OnTryPurchaseWrapper(inventory, …) is valid.
        foreach (var choice in Sts2ShopScreenInspector.ResolveInventoryChoices(inventory, localPlayerId))
        {
            choicesById.TryAdd(choice.Snapshot.Id, choice);
        }

        context = new ShopActionContext(
            Screen: screen,
            ScreenObject: screenObject,
            LocalPlayerId: localPlayerId,
            Inventory: inventory,
            ChoicesById: choicesById);
        return true;
    }

    // Build a shop context from the live MerchantInventory MODEL (NMerchantRoom.Instance.Room.GetLocalInventory())
    // when no shop SCREEN is active. The buy path (ResolvedShopChoice.Entry.OnTryPurchaseWrapper) reads the
    // entries straight off this model and is screen-independent, so a seat can buy without the inventory
    // overlay being open. Only entry (purchase) choices are minted here; the screen-bound leave/close flow
    // choices have their own handlers (leave-shop / proceed-merchant-room).
    private bool TryResolveShopContextFromModel(
        ScreenLocatorResult screen,
        string? localPlayerId,
        [NotNullWhen(true)] out ShopActionContext? context)
    {
        var room = NMerchantRoom.Instance;
        if (room?.Room?.GetLocalInventory() is not { } inventory)
        {
            context = null;
            return false;
        }

        var choicesById = new Dictionary<string, ResolvedShopChoice>(StringComparer.Ordinal);
        foreach (var choice in Sts2ShopScreenInspector.ResolveInventoryChoices(inventory, localPlayerId))
        {
            choicesById.TryAdd(choice.Snapshot.Id, choice);
        }

        context = new ShopActionContext(
            Screen: screen,
            ScreenObject: room,
            LocalPlayerId: localPlayerId,
            Inventory: inventory,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveCardSelectionContext([NotNullWhen(true)] out CardSelectionActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = NOverlayStack.Instance?.Peek() ?? Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var localPlayerId = ResolveLocalPlayerId();
        if (!Sts2SupportedScreenIds.IsCardSelectionFamily(screen.ScreenType)
            || screenObject is null
            || !Sts2CardSelectionScreenInspector.IsSupportedScreen(screenObject))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2CardSelectionScreenInspector.ResolveChoices(screenObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new CardSelectionActionContext(
            Screen: screen,
            ScreenObject: screenObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveCrystalSphereContext([NotNullWhen(true)] out CrystalSphereActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = NOverlayStack.Instance?.Peek() ?? Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var localPlayerId = ResolveLocalPlayerId();
        if (!Sts2SupportedScreenIds.IsCrystalSphereScreenType(screen.ScreenType)
            || screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"))
        {
            context = null;
            return false;
        }

        var choicesById = Sts2CrystalSphereScreenInspector.ResolveChoices(screenObject, localPlayerId)
            .ToDictionary(choice => choice.Snapshot.Id, StringComparer.Ordinal);
        context = new CrystalSphereActionContext(
            Screen: screen,
            ScreenObject: screenObject,
            LocalPlayerId: localPlayerId,
            ChoicesById: choicesById);
        return true;
    }

    private bool TryResolveLobbyContext([NotNullWhen(true)] out LobbyActionContext? context)
    {
        var screen = _screenLocator.Locate();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (!Sts2SupportedScreenIds.IsLobbyScreenType(screen.ScreenType) || screenObject is null)
        {
            context = null;
            return false;
        }

        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject)
            && Sts2LiveIntrospection.GetMemberValue(screenObject, "Lobby") is StartRunLobby startRunLobby)
        {
            var localPlayerId = startRunLobby.LocalPlayer is LobbyPlayer localPlayer
                ? $"p:{localPlayer.id}"
                : $"p:{startRunLobby.NetService.NetId}";
            var hostPlayerId = ResolveLobbyHostPlayerId(startRunLobby.NetService, localPlayerId);
            var players = startRunLobby.Players
                .Select(player =>
                {
                    var isLocal = player.id == startRunLobby.NetService.NetId;
                    var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.id);
                    return new LobbyPlayerSnapshot(
                        Id: $"p:{player.id}",
                        Status: player.isReady ? "ready" : "not-ready",
                        Name: null,
                        SelectedCharacterId: player.character?.Id.Entry,
                        IsReady: player.isReady,
                        SlotId: player.slotId,
                        IsLocal: isLocal,
                        IsHost: string.Equals($"p:{player.id}", hostPlayerId, StringComparison.Ordinal),
                        IsHostLocalSeat: isHostLocalSeat,
                        IsRemote: !isLocal && !isHostLocalSeat);
                })
                .ToArray();
            var availableCharacters = ResolveSelectableCharacters(screenObject);
            var lobby = new LobbyStateSnapshot(
                LobbyId: "start-run",
                Phase: startRunLobby.IsAboutToBeginGame() ? "starting" : "selecting",
                Players: players,
                AvailableCharacters: availableCharacters,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                LocalPlayerRole: startRunLobby.NetService.Type == NetGameType.Host ? "host" : "client",
                PlayersById: players.ToDictionary(player => player.Id, StringComparer.Ordinal),
                AvailableCharactersById: availableCharacters.ToDictionary(character => character.Id, StringComparer.Ordinal));
            context = new LobbyActionContext(screen, screenObject, lobby, startRunLobby, null);
            return true;
        }

        if (Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screenObject)
            && (Sts2LiveIntrospection.GetMemberValue(screenObject, "_runLobby") as LoadRunLobby
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "RunLobby") as LoadRunLobby) is LoadRunLobby loadRunLobby)
        {
            var localPlayerId = $"p:{loadRunLobby.NetService.NetId}";
            var hostPlayerId = ResolveLobbyHostPlayerId(loadRunLobby.NetService, localPlayerId);
            var players = (loadRunLobby.Run?.Players ?? [])
                .Select((player, index) =>
                {
                    var isLocal = player.NetId == loadRunLobby.NetService.NetId;
                    var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.NetId);
                    return new LobbyPlayerSnapshot(
                        Id: $"p:{player.NetId}",
                        Status: loadRunLobby.IsPlayerReady(player.NetId) ? "ready" : "not-ready",
                        Name: null,
                        SelectedCharacterId: player.CharacterId?.Entry,
                        IsReady: loadRunLobby.IsPlayerReady(player.NetId),
                        SlotId: index,
                        IsLocal: isLocal,
                        IsHost: string.Equals($"p:{player.NetId}", hostPlayerId, StringComparison.Ordinal),
                        IsHostLocalSeat: isHostLocalSeat,
                        IsRemote: !isLocal && !isHostLocalSeat);
                })
                .ToArray();
            var availableCharacters = ResolveSelectableCharacters(screenObject);
            var lobby = new LobbyStateSnapshot(
                LobbyId: "load-run",
                Phase: players.Any(player => player.IsReady) ? "confirming" : "waiting",
                Players: players,
                AvailableCharacters: availableCharacters,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                LocalPlayerRole: loadRunLobby.NetService.Type == NetGameType.Host ? "host" : "client",
                PlayersById: players.ToDictionary(player => player.Id, StringComparer.Ordinal),
                AvailableCharactersById: availableCharacters.ToDictionary(character => character.Id, StringComparer.Ordinal));
            context = new LobbyActionContext(screen, screenObject, lobby, null, loadRunLobby);
            return true;
        }

        context = null;
        return false;
    }

    private static IReadOnlyList<LobbyCharacterSnapshot> ResolveSelectableCharacters(object screenObject)
    {
        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Godot.Node
            ?? screenObject as Godot.Node;
        if (root is null)
        {
            return [];
        }

        return Sts2TreeSearch.FindDescendants(
                root,
                static node => node.GetChildren().OfType<Godot.Node>(),
                static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
            .Select(child => new
            {
                IsRandom = Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool isRandom && isRandom,
                IsLocked = Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool isLocked && isLocked,
                Character = Sts2LiveIntrospection.GetMemberValue(child, "Character") as CharacterModel,
            })
            .Where(entry => entry.IsRandom || entry.Character is not null)
            .Select(entry => new LobbyCharacterSnapshot(
                Id: entry.IsRandom ? "RANDOM_CHARACTER" : entry.Character!.Id.Entry,
                Name: entry.IsRandom ? "Random Character" : entry.Character!.CharacterSelectTitle,
                IsUnlocked: !entry.IsLocked))
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static string? ResolveLobbyHostPlayerId(object? netService, string? localPlayerId)
        => Sts2LobbyHostResolver.Resolve(netService, localPlayerId);

    private static string? ResolveRunHostPlayerId(string? localPlayerId)
        => Sts2LobbyHostResolver.ResolveRunHostPlayerId(RunManager.Instance?.NetService, localPlayerId);

    private static bool IsHostLocalPlayerId(string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !playerId.StartsWith("p:", StringComparison.Ordinal))
        {
            return false;
        }

        return ulong.TryParse(playerId[2..], out var netId)
            && Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId);
    }

    private static bool BuildOwnershipFailure(
        SemanticActionRequest request,
        ActionOwnershipContext context,
        out ActionExecutionResult failure)
    {
        var requestedPlayerId = string.IsNullOrWhiteSpace(context.RequestedPlayerId)
            ? context.ResolvedOwnerPlayerId
            : context.RequestedPlayerId;
        var localRole = ResolveLocalRole(context.LocalPlayerId, context.HostPlayerId);
        var isHostLocalOwner = context.HostLocalPlayerIds.Contains(requestedPlayerId ?? string.Empty, StringComparer.Ordinal);
        var remoteOrchestration = isHostLocalOwner
            ? OwnershipMetadata.HostLocalSeat()
            : OwnershipMetadata.LocalOnlyDegraded();

        if (string.IsNullOrWhiteSpace(requestedPlayerId))
        {
            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.UnsupportedPerspective,
                "unsupported-perspective",
                "The local bridge could not resolve which player owns this semantic action.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context.ResolvedOwnerPlayerId)
            && !string.Equals(requestedPlayerId, context.ResolvedOwnerPlayerId, StringComparison.Ordinal))
        {
            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.WrongPlayer,
                "wrong-player",
                "The requested player does not own the visible control or action surface.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(context.LocalPlayerId)
            && !string.Equals(requestedPlayerId, context.LocalPlayerId, StringComparison.Ordinal))
        {
            if (isHostLocalOwner)
            {
                failure = null!;
                return false;
            }

            failure = BuildOwnershipFailure(
                request,
                context,
                requestedPlayerId,
                localRole,
                remoteOrchestration,
                ActionFailureCode.UnsupportedPerspective,
                "unsupported-perspective",
                "This local bridge can execute only actions owned by its local player; remote-owned semantic actions require an explicitly configured remote client bridge.");
            return true;
        }

        failure = null!;
        return false;
    }

    private static ActionExecutionResult BuildOwnershipFailure(
        SemanticActionRequest request,
        ActionOwnershipContext context,
        string? requestedPlayerId,
        MultiplayerRoleSnapshot localRole,
        RemoteClientOrchestrationCapabilitySnapshot remoteOrchestration,
        ActionFailureCode code,
        string reasonCode,
        string note)
    {
        return ActionExecutionResult.Failure(
            kind: request.Kind,
            code: code,
            message: code == ActionFailureCode.WrongPlayer
                ? $"{context.Action} cannot execute for a player that does not own the current action surface."
                : $"{context.Action} cannot execute from the current local bridge perspective.",
            details:
            [
                new ActionFailureDetail(
                    Field: "player_id",
                    Value: requestedPlayerId ?? string.Empty,
                    Note: $"{note} reasonCode={reasonCode}; remoteOrchestration={remoteOrchestration.Id}.",
                    ReasonCode: code,
                    Screen: context.Screen,
                    PlayerId: requestedPlayerId,
                    RequestedPlayerId: requestedPlayerId,
                    ResolvedOwnerPlayerId: context.ResolvedOwnerPlayerId,
                    LocalPlayerId: context.LocalPlayerId,
                    HostPlayerId: context.HostPlayerId,
                    LocalRole: localRole,
                    Action: context.Action,
                    RemoteOrchestration: remoteOrchestration),
            ],
            screen: context.Screen,
            playerId: requestedPlayerId,
            requestedPlayerId: requestedPlayerId,
            resolvedOwnerPlayerId: context.ResolvedOwnerPlayerId,
            localPlayerId: context.LocalPlayerId,
            hostPlayerId: context.HostPlayerId,
            localRole: localRole,
            action: context.Action,
            remoteOrchestration: remoteOrchestration);
    }

    private static MultiplayerRoleSnapshot ResolveLocalRole(string? localPlayerId, string? hostPlayerId)
    {
        if (string.IsNullOrWhiteSpace(localPlayerId))
        {
            return MultiplayerRoleSnapshot.Unspecified;
        }

        return string.Equals(localPlayerId, hostPlayerId, StringComparison.Ordinal)
            ? MultiplayerRoleSnapshot.Host
            : MultiplayerRoleSnapshot.Local;
    }

    private static SelectCharacterTarget? ResolveSelectableCharacterTarget(object screenObject, string characterId)
    {
        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Godot.Node
            ?? screenObject as Godot.Node;
        if (root is null)
        {
            return null;
        }

        foreach (var child in Sts2TreeSearch.FindDescendants(
                     root,
                     static node => node.GetChildren().OfType<Godot.Node>(),
                     static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton")))
        {
            if (Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool isRandom && isRandom)
            {
                if (IsRandomCharacterId(characterId))
                {
                    return new SelectCharacterTarget(child, null);
                }

                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool isLocked && isLocked)
            {
                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "Character") is not CharacterModel character)
            {
                continue;
            }

            if (string.Equals(character.Id.Entry, characterId, StringComparison.Ordinal))
            {
                return new SelectCharacterTarget(child, character);
            }
        }

        return null;
    }

    private static CharacterModel? ResolveDefaultLobbyCharacter(object screenObject, LobbyPlayer localPlayer)
    {
        if (localPlayer.character is not null)
        {
            return localPlayer.character;
        }

        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Godot.Node
            ?? screenObject as Godot.Node;
        if (root is not null)
        {
            foreach (var child in Sts2TreeSearch.FindDescendants(
                         root,
                         static node => node.GetChildren().OfType<Godot.Node>(),
                         static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton")))
            {
                if (Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool isRandom && isRandom)
                {
                    continue;
                }

                if (Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool isLocked && isLocked)
                {
                    continue;
                }

                if (Sts2LiveIntrospection.GetMemberValue(child, "Character") is CharacterModel character)
                {
                    return character;
                }
            }
        }

        return ModelDb.AllCharacters.FirstOrDefault();
    }

    private static void RefreshSyntheticStartRunLobbyPlayerUi(object screenObject, LobbyPlayer player)
    {
        var remotePlayerContainer = Sts2LiveIntrospection.GetMemberValue(screenObject, "_remotePlayerContainer");
        if (RemotePlayerContainerHasPlayer(remotePlayerContainer, player.id))
        {
            Sts2LiveIntrospection.TryInvokeMethod(screenObject, "PlayerChanged", player, false);
        }
        else
        {
            Sts2LiveIntrospection.TryInvokeMethod(screenObject, "PlayerConnected", player);
        }

        // The synthetic seat's display name lives only in Sts2HostLocalSeatRegistry; the live widget
        // (NRemoteLobbyPlayer) sets its nameplate from PlatformUtil.GetPlayerNameRaw(netId), which for a
        // host-local synthetic netId has no platform identity and returns the netId as text (e.g. "2").
        // Set the nameplate label directly so the lobby shows the typed name. Re-applied on every
        // per-seat refresh so it survives later PlayerChanged updates.
        ApplySyntheticNameplate(remotePlayerContainer, player.id);
    }

    private static void ApplySyntheticNameplate(object? remotePlayerContainer, ulong playerId)
    {
        var syntheticName = Sts2HostLocalSeatRegistry.ResolveSyntheticName(playerId);
        if (string.IsNullOrWhiteSpace(syntheticName)
            || Sts2LiveIntrospection.GetMemberValue(remotePlayerContainer, "_nodes") is not System.Collections.IEnumerable nodes)
        {
            return;
        }

        foreach (var node in nodes)
        {
            if (Sts2LiveIntrospection.GetMemberValue(node, "PlayerId") is ulong nodePlayerId
                && nodePlayerId == playerId)
            {
                var nameplateLabel = Sts2LiveIntrospection.GetMemberValue(node, "_nameplateLabel");
                Sts2LiveIntrospection.TryInvokeMethod(nameplateLabel, "SetTextAutoSize", syntheticName);
                return;
            }
        }
    }

    private static bool RemotePlayerContainerHasPlayer(object? remotePlayerContainer, ulong playerId)
    {
        if (Sts2LiveIntrospection.GetMemberValue(remotePlayerContainer, "_nodes") is not System.Collections.IEnumerable nodes)
        {
            return false;
        }

        foreach (var node in nodes)
        {
            if (Sts2LiveIntrospection.GetMemberValue(node, "PlayerId") is ulong nodePlayerId
                && nodePlayerId == playerId)
            {
                return true;
            }
        }

        return false;
    }

    private static ulong NextSyntheticNetId(IEnumerable<LobbyPlayer> players)
    {
        var used = players.Select(player => player.id).ToHashSet();
        var candidate = used.Count == 0 ? 1uL : used.Max() + 1uL;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static int NextLobbySlotId(IEnumerable<LobbyPlayer> players)
    {
        var used = players.Select(player => player.slotId).ToHashSet();
        var candidate = used.Count == 0 ? 0 : used.Max() + 1;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static bool TryParseLobbyPlayerId(string? playerId, out ulong netId)
    {
        netId = 0;
        return !string.IsNullOrWhiteSpace(playerId)
               && playerId.StartsWith("p:", StringComparison.Ordinal)
               && ulong.TryParse(playerId[2..], out netId)
               && netId > 0;
    }

}
