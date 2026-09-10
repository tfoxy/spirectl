using System.Collections;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using SpirectlModels = Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live.EncounterVisuals;

namespace Spirectl.Sts2.Live;

public sealed class Sts2RuntimeObservationProvider : IRuntimeObservationProvider
{
    private readonly Sts2ScreenLocator _screenLocator;
    private readonly ILogStream _logStream;

    public Sts2RuntimeObservationProvider(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream)
    {
        _screenLocator = screenLocator;
        _logStream = logStream;
    }

    public BridgeRuntimeObservation Observe(GameStateQuery query)
    {
        try
        {
            var observation = query.Timeout is { } timeout
                ? Sts2MainThreadDispatcher.Invoke(() => ObserveOnMainThread(query), timeout)
                : Sts2MainThreadDispatcher.Invoke(() => ObserveOnMainThread(query));
            if (observation.Notices.Any(notice => notice.Code == "screen-unsupported"))
            {
                _logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.state",
                    observation.Notices.First(notice => notice.Code == "screen-unsupported").Message);
            }

            return observation;
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.state",
                $"State observation failed: {ex}");
            throw;
        }
    }

    private BridgeRuntimeObservation ObserveOnMainThread(GameStateQuery query)
    {
        var screen = _screenLocator.Locate();
        if (Sts2SupportedScreenIds.IsTreasureRoomFamily(screen.ScreenType))
        {
            return CaptureTreasureRoom(screen, query);
        }

        if (Sts2SupportedScreenIds.IsCardSelectionFamily(screen.ScreenType))
        {
            return CaptureCardSelection(screen, query);
        }

        if (Sts2SupportedScreenIds.IsLobbyScreenType(screen.ScreenType))
        {
            return CaptureLobby(screen, query);
        }

        return screen.ScreenType switch
        {
            "combat" => CaptureCombat(screen, query),
            "main-menu" => CaptureMainMenu(screen, query, Sts2LiveIntrospection.ResolveCurrentScreenObject()),
            Sts2SupportedScreenIds.MainMenuScreenId => CaptureMainMenu(screen, query, Sts2LiveIntrospection.ResolveCurrentScreenObject()),
            _ when Sts2SupportedScreenIds.IsMapScreenType(screen.ScreenType) => CaptureMap(screen, query),
            "card-overlay" => CaptureCardOverlay(screen, query),
            _ when Sts2SupportedScreenIds.IsEventRoomScreenType(screen.ScreenType) => CaptureEventRoom(screen, query),
            _ when Sts2SupportedScreenIds.IsCrystalSphereScreenType(screen.ScreenType) => CaptureCrystalSphere(screen, query),
            _ when Sts2SupportedScreenIds.IsRestSiteScreenType(screen.ScreenType) => CaptureRestSite(screen, query),
            _ when Sts2SupportedScreenIds.IsShopScreenType(screen.ScreenType) => CaptureShop(screen, query),
            _ when Sts2SupportedScreenIds.IsRewardsScreenType(screen.ScreenType) => CaptureRewards(screen, query),
            _ when Sts2SupportedScreenIds.IsGameOverScreenType(screen.ScreenType) => CaptureGameOver(screen, query),
            _ => CaptureUnsupported(screen, query),
        };
    }

    internal static BridgeRuntimeObservation CaptureMainMenu(ScreenLocatorResult screen, GameStateQuery query, object? screenObject)
    {
        var inspection = Sts2MainMenuStartRunHooks.Inspect(screenObject);
        var notices = inspection.HasCallableHook
            ? Array.Empty<StateNoticeSnapshot>()
            : [
                Sts2StateNotice.Partial(
                    "main-menu-start-run-hook-unavailable",
                    "The active main-menu screen did not expose a callable start-run hook on the checked probe paths, so Start Run remains visible in choices but unavailable in availableActions.",
                    "availableActions",
                    nameof(Sts2RuntimeObservationProvider)),
            ];

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Length > 0,
            DefaultPlayerId: null,
            Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
            Lobby: null,
            Run: null,
            Combat: null,
            Choices:
            [
                new ChoiceSnapshot(
                    "menu:start-run",
                    "Start Run",
                    "menu",
                    Provisional: true,
                    ChoiceKind: "menu-flow",
                    IntentKind: "start-run",
                    PreferredAction: "choose",
                    Description: "Start a run from the main menu.",
                    Arguments: new ActionArgumentsSnapshot(null, null, null, Sts2ActionCatalog.MainMenuStartRunChoiceId, null, null, IntentKind: "start-run"),
                    Enabled: inspection.HasCallableHook,
                    DisabledReason: inspection.HasCallableHook ? null : "missing-hook",
                    PreferredActionRef: inspection.HasCallableHook
                        ? new VisibleActionReferenceSnapshot(
                            Sts2ActionIds.MainMenuStartRun(),
                            "Choose the visible Start Run option.",
                            Enabled: true,
                            ActionKind: SemanticActionKind.Choose,
                            IntentKind: "start-run",
                            Arguments: new ActionArgumentsSnapshot(null, null, null, Sts2ActionCatalog.MainMenuStartRunChoiceId, null, null, IntentKind: "start-run"),
                            LegalityStatus: ActionLegalityKind.Legal)
                        : null,
                    LegalityStatus: inspection.HasCallableHook ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                    CheckedHookPaths: inspection.CheckedProbePaths),
            ],
            AvailableActions: inspection.HasCallableHook
                ? Sts2ActionCatalog.MainMenuActions()
                : [],
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureCardOverlay(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var overlayObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var defaultPlayerId = ResolveLocalPlayerIdOrNull();
        var inspection = Sts2CardOverlayInspector.Inspect(overlayObject, screen, defaultPlayerId, notices);
        var actions = Sts2ActionCatalog.CardOverlayActions(inspection.State, inspection.Choices);
        var enrichedNotices = AddCardOverlayScreenDiagnostics(notices, screen);

        return CreateObservation(
            screen,
            query,
            Provisional: true,
            DefaultPlayerId: defaultPlayerId,
            Menu: null,
            Lobby: null,
            Run: null,
            Combat: null,
            Choices: inspection.Choices,
            AvailableActions: actions,
            Notices: enrichedNotices.Count == 0
                ? []
                : enrichedNotices,
            CardOverlay: inspection.State,
            Debug: CreateDebug(screen, query));
    }

    private static IReadOnlyList<StateNoticeSnapshot> AddCardOverlayScreenDiagnostics(
        IReadOnlyList<StateNoticeSnapshot> notices,
        ScreenLocatorResult screen)
    {
        if (notices.Count == 0)
        {
            return notices;
        }

        var screenDiagnostics =
            $" Overlay screen diagnostics: source '{screen.Source}', raw type '{screen.ScreenRawType}', class '{screen.ScreenClassName}'.";
        return notices
            .Select(notice => notice.Message.Contains("Overlay screen diagnostics:", StringComparison.Ordinal)
                ? notice
                : notice with { Message = notice.Message + screenDiagnostics })
            .ToArray();
    }

    private static BridgeRuntimeObservation CaptureCombat(ScreenLocatorResult screen, GameStateQuery query)
    {
        var runManager = RunManager.Instance!;
        var runState = runManager.DebugOnlyGetState()!;
        var combatState = CombatManager.Instance!.DebugOnlyGetState()!;
        var notices = new List<StateNoticeSnapshot>();
        var defaultPlayerId = ResolveLocalPlayerId(runState);
        var hostPlayerId = ResolveRunHostPlayerId(defaultPlayerId);

        var runPlayers = runState.Players
            .Select(player =>
            {
                var playerId = Sts2CombatIds.PlayerId(player);
                var isLocal = string.Equals(playerId, defaultPlayerId, StringComparison.Ordinal);
                var isHostLocalSeat = IsHostLocalCombatSeat(player);
                return Sts2PresentationStateResolver.WithPlayerPresentation(
                    new PlayerStateSnapshot(
                    Id: playerId,
                    Character: ResolveCharacterId(player.Character),
                    Hp: player.Creature?.CurrentHp ?? 0,
                    MaxHp: player.Creature?.MaxHp ?? 0,
                    IsLocal: isLocal,
                    IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
                    IsRemote: !isLocal && !isHostLocalSeat,
                    IsHostLocalSeat: isHostLocalSeat),
                    player);
            })
            .ToArray();

        var combatPlayers = combatState.Allies
            .Where(creature => creature.Player is not null)
            .Select(creature =>
            {
                var player = creature.Player!;
                var playerId = Sts2CombatIds.PlayerId(player);
                var isLocal = string.Equals(playerId, defaultPlayerId, StringComparison.Ordinal);
                var isHostLocalSeat = IsHostLocalCombatSeat(player);
                var hand = (player.PlayerCombatState?.Hand?.Cards ?? [])
                    .Select((card, index) => (CardStateSnapshot)CardSnapshot((dynamic)card, playerId, combatState.Enemies, index, notices))
                    .ToArray();
                var potions = player.PotionSlots
                    .Select((potion, slotIndex) => (potion, slotIndex))
                    .Where(entry => entry.potion is not null)
                    .Select(entry => PotionSnapshot(entry.potion!, playerId, entry.slotIndex, combatState, notices))
                    .ToArray();
                var drawPileCardIds = ResolveCombatPileCardIds(player.PlayerCombatState, "DrawPile", playerId, notices);
                var discardPileCardIds = ResolveCombatPileCardIds(player.PlayerCombatState, "DiscardPile", playerId, notices);
                var exhaustPileCardIds = ResolveCombatPileCardIds(player.PlayerCombatState, "ExhaustPile", playerId, notices);
                var drawPile = Sts2PresentationStateResolver.ResolvePile(player.PlayerCombatState, "DrawPile", $"draw:{playerId}", "Draw pile", playerId, drawPileCardIds, notices);
                var discardPile = Sts2PresentationStateResolver.ResolvePile(player.PlayerCombatState, "DiscardPile", $"discard:{playerId}", "Discard pile", playerId, discardPileCardIds, notices);
                var exhaustPile = Sts2PresentationStateResolver.ResolvePile(player.PlayerCombatState, "ExhaustPile", $"exhaust:{playerId}", "Exhaust pile", playerId, exhaustPileCardIds, notices);

                return Sts2PresentationStateResolver.WithCombatPlayerPresentation(
                    new CombatPlayerStateSnapshot(
                        Id: playerId,
                        Character: ResolveCharacterId(player.Character),
                        Hp: creature.CurrentHp,
                        MaxHp: creature.MaxHp,
                        Block: creature.Block,
                        Energy: player.PlayerCombatState?.Energy ?? 0,
                        MaxEnergy: player.PlayerCombatState?.MaxEnergy ?? 0,
                        Hand: hand,
                        Potions: potions,
                        IsLocal: isLocal,
                        IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
                        IsRemote: !isLocal && !isHostLocalSeat,
                        IsHostLocalSeat: isHostLocalSeat,
                        DrawPileCardIds: drawPileCardIds,
                        DiscardPileCardIds: discardPileCardIds,
                        ExhaustPileCardIds: exhaustPileCardIds,
                        DrawPile: drawPile,
                        DiscardPile: discardPile,
                        ExhaustPile: exhaustPile,
                        CanRemovePotions: SafeBool(() => (bool)player.CanRemovePotions, fallback: true)),
                    creature,
                    player);
            })
            .ToArray();

        var isPlayerTurn = Sts2CombatFacts.IsAnyPlayerInPlayPhase(combatState.Players);
        var activePlayerId = isPlayerTurn && !string.IsNullOrWhiteSpace(defaultPlayerId)
            ? defaultPlayerId
            : combatPlayers.FirstOrDefault()?.Id ?? defaultPlayerId;
        var activeCombatPlayer = combatPlayers.FirstOrDefault(player => player.Id == activePlayerId);
        var visibleHand = activeCombatPlayer?.Hand ?? [];
        var visiblePotions = activeCombatPlayer?.Potions ?? [];
        var visibleDrawPileCardIds = activeCombatPlayer?.DrawPileCardIds ?? [];
        var visibleDiscardPileCardIds = activeCombatPlayer?.DiscardPileCardIds ?? [];
        var visibleExhaustPileCardIds = activeCombatPlayer?.ExhaustPileCardIds ?? [];
        var visibleDrawPile = activeCombatPlayer?.DrawPile;
        var visibleDiscardPile = activeCombatPlayer?.DiscardPile;
        var visibleExhaustPile = activeCombatPlayer?.ExhaustPile;

        var encounter = Sts2PresentationStateResolver.ResolveEncounter(runState);
        var encounterId = encounter.Id ?? ResolveCombatEncounterId(runState);
        var encounterVisuals = ResolveEncounterVisuals(encounterId, Sts2EncounterVisualEventStore.Shared, notices);
        var enemies = combatState.Enemies
            .Select((enemy, index) => EnemySnapshot(enemy, index, notices, combatState.Allies, combatState.Enemies, encounterVisuals))
            .ToArray();

        var combatSnapshot = new CombatStateSnapshot(
            Turn: combatState.RoundNumber,
            ActivePlayerId: activePlayerId,
            IsPlayerTurn: isPlayerTurn,
            Hand: visibleHand,
            Players: combatPlayers,
            Enemies: enemies,
            PlayersById: combatPlayers.ToDictionary(player => player.Id, StringComparer.Ordinal),
            Potions: visiblePotions,
            DrawPileCardIds: visibleDrawPileCardIds,
            DiscardPileCardIds: visibleDiscardPileCardIds,
            ExhaustPileCardIds: visibleExhaustPileCardIds,
            EncounterId: encounterId,
            EncounterLabel: encounter.Label,
            DrawPile: visibleDrawPile,
            DiscardPile: visibleDiscardPile,
            ExhaustPile: visibleExhaustPile,
            EncounterVisuals: encounterVisuals,
            SelectedPotion: ResolveSelectedPotionView(runState, activePlayerId));

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: defaultPlayerId,
            Menu: null,
            Lobby: null,
            Run: SnapshotRun(runState, runPlayers, encounter),
            Combat: combatSnapshot,
            Choices: enemies
                .Where(enemy => enemy.IsAlive)
                .Select(enemy => new ChoiceSnapshot(
                    $"target:{enemy.Id}",
                    enemy.Name,
                    "target",
                    Provisional: false,
                    OwnerPlayerId: activePlayerId,
                    ChoiceKind: "combat-target",
                    IntentKind: "select-target",
                    Perspective: ResolvePerspective(activePlayerId),
                    Description: $"Visible combat target {enemy.Name}.",
                    Arguments: new ActionArgumentsSnapshot(activePlayerId, null, enemy.Id, null, null, null, IntentKind: "select-target"),
                    Enabled: true,
                    LegalityStatus: ActionLegalityKind.Legal))
                .ToArray(),
            AvailableActions: Sts2StateProvider.ResolveHandSelectionView(notices, defaultPlayerId) is { } handSelection
                ? Sts2ActionCatalog.HandSelectionActions(handSelection)
                : Sts2ActionCatalog.CombatActions(combatSnapshot),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureLobby(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();

        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject)
            && Sts2LiveIntrospection.GetMemberValue(screenObject, "Lobby") is StartRunLobby startRunLobby)
        {
            return CaptureStartRunLobby(screen, query, screenObject, startRunLobby, notices);
        }

        if (Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screenObject))
        {
            var loadRunLobby = Sts2LiveIntrospection.GetMemberValue(screenObject, "_runLobby") as LoadRunLobby
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "RunLobby") as LoadRunLobby;
            if (loadRunLobby is not null)
            {
                return CaptureLoadRunLobby(screen, query, loadRunLobby, notices);
            }
        }

        notices.Add(Sts2StateNotice.Partial(
            "lobby-partial",
            "Live lobby extraction could not resolve the active lobby container from the current screen.",
            "lobby",
            nameof(Sts2RuntimeObservationProvider)));

        return CreateObservation(
            screen,
            query,
            Provisional: true,
            DefaultPlayerId: null,
            Menu: null,
            Lobby: new LobbyStateSnapshot(
                LobbyId: "unknown",
                Phase: "unknown",
                Players: [],
                AvailableCharacters: [],
                LocalPlayerId: null,
                HostPlayerId: null,
                LocalPlayerRole: null,
                PlayersById: new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal),
                AvailableCharactersById: new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)),
            Run: null,
            Combat: null,
            Choices: [],
            AvailableActions: [],
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureMap(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var mapScreen = NMapScreen.Instance;
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (mapScreen is null || !mapScreen.IsOpen)
        {
            notices.Add(Sts2StateNotice.Partial(
                "map-screen-unavailable",
                "The map screen was classified as active but the live NMapScreen instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "map-run-unavailable",
                "The map screen was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var nodes = mapScreen is null || !mapScreen.IsOpen
            ? []
            : Sts2MapScreenInspector.ResolveNodes(mapScreen);
        var flowChoices = mapScreen is null || !mapScreen.IsOpen
            ? []
            : Sts2MapScreenInspector.ResolveFlowChoices(mapScreen, localPlayerId);
        if (mapScreen is not null && mapScreen.IsOpen && nodes.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "map-nodes-unavailable",
                "The live map screen did not expose any map nodes through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var availableActions = Sts2ActionCatalog.MapActions(
            localPlayerId,
            nodes.Select(node => node.Snapshot).ToArray(),
            mapScreen?.IsTravelEnabled == true,
            mapScreen?.IsTraveling == true,
            flowChoices
                .Where(choice => choice.IsExecutable)
                .Select(choice => choice.Snapshot)
                .ToArray());
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "map-visible-choices-partial",
            "Visible map nodes can remain unavailable when travel is not currently enabled, travel is already in progress, or a node is not currently travelable; availableActions includes only currently travelable nodes plus executable map flow choices.",
            visibleChoiceCount: nodes.Count + flowChoices.Count,
            executableChoiceCount: availableActions.Count);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            Map: new MapStateSnapshot(
                nodes.Select((node, index) => new VisibleItemStateSnapshot(
                    node.Snapshot.Id,
                    node.Snapshot.Label,
                    Description: $"row={node.Row};col={node.Col};travelable={node.IsTravelable};order={index}",
                    OwnerPlayerId: node.OwnerPlayerId,
                    Selected: false,
                    Provisional: false,
                    ChoiceKind: "map-node",
                    IntentKind: "select-map-node",
                    Perspective: ResolvePerspective(node.OwnerPlayerId ?? localPlayerId),
                    Arguments: new ActionArgumentsSnapshot(node.OwnerPlayerId ?? localPlayerId, null, null, null, null, node.Snapshot.Id, IntentKind: "select-map-node"),
                    Enabled: node.IsTravelable && mapScreen?.IsTravelEnabled == true && mapScreen?.IsTraveling != true,
                    DisabledReason: ResolveMapNodeDisabledReason(node, mapScreen?.IsTravelEnabled == true, mapScreen?.IsTraveling == true),
                    PreferredAction: node.IsTravelable && mapScreen?.IsTravelEnabled == true && mapScreen?.IsTraveling != true
                        ? new VisibleActionReferenceSnapshot(
                            Sts2ActionIds.Intent("map", "select-map-node", node.Snapshot.Id),
                            $"Select {node.Snapshot.Label}.",
                            Enabled: true,
                            OwnerPlayerId: node.OwnerPlayerId ?? localPlayerId,
                            ActionKind: SemanticActionKind.SelectMapNode,
                            IntentKind: "select-map-node",
                            Arguments: new ActionArgumentsSnapshot(node.OwnerPlayerId ?? localPlayerId, null, null, null, null, node.Snapshot.Id, IntentKind: "select-map-node"),
                            LegalityStatus: ActionLegalityKind.Legal,
                            Perspective: ResolvePerspective(node.OwnerPlayerId ?? localPlayerId))
                        : null)).ToArray()),
            Choices: nodes
                .Select(node => new ChoiceSnapshot(
                    node.Snapshot.Id,
                    node.Snapshot.Label,
                    node.Snapshot.Kind,
                    Provisional: false,
                    OwnerPlayerId: localPlayerId,
                    ChoiceKind: "map-node",
                    IntentKind: "select-map-node",
                    Perspective: ResolvePerspective(localPlayerId),
                    PreferredAction: "select-map-node",
                    Description: $"Map node at row {node.Row}, column {node.Col}.",
                    Arguments: new ActionArgumentsSnapshot(localPlayerId, null, null, null, null, node.Snapshot.Id, IntentKind: "select-map-node"),
                    Enabled: node.IsTravelable && mapScreen?.IsTravelEnabled == true && mapScreen?.IsTraveling != true,
                    DisabledReason: ResolveMapNodeDisabledReason(node, mapScreen?.IsTravelEnabled == true, mapScreen?.IsTraveling == true),
                    PreferredActionRef: node.IsTravelable && mapScreen?.IsTravelEnabled == true && mapScreen?.IsTraveling != true
                        ? new VisibleActionReferenceSnapshot(
                            Sts2ActionIds.Intent("map", "select-map-node", node.Snapshot.Id),
                            $"Select {node.Snapshot.Label}.",
                            Enabled: true,
                            OwnerPlayerId: localPlayerId,
                            ActionKind: SemanticActionKind.SelectMapNode,
                            IntentKind: "select-map-node",
                            Arguments: new ActionArgumentsSnapshot(localPlayerId, null, null, null, null, node.Snapshot.Id, IntentKind: "select-map-node"),
                            LegalityStatus: ActionLegalityKind.Legal,
                            Perspective: ResolvePerspective(localPlayerId))
                        : null,
                    LegalityStatus: node.IsTravelable && mapScreen?.IsTravelEnabled == true && mapScreen?.IsTraveling != true
                        ? ActionLegalityKind.Legal
                        : ActionLegalityKind.Illegal))
                .Concat(flowChoices.Select(choice => choice.Snapshot with { PreferredAction = choice.PreferredAction }))
                .ToArray(),
            AvailableActions: availableActions,
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureEventRoom(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var roomObject = Sts2EventRoomScreenInspector.ResolveActiveRoom();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (roomObject is null
            || !Sts2LiveIntrospection.IsType(roomObject, Sts2EventRoomScreenInspector.EventRoomType))
        {
            notices.Add(Sts2StateNotice.Partial(
                "event-room-screen-unavailable",
                "The event room was classified as active but the live room instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "event-room-run-unavailable",
                "The event room was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var eventChoices = roomObject is null
            ? []
            : Sts2EventRoomScreenInspector.ResolveChoices(roomObject, localPlayerId, notices);
        var eventPage = roomObject is null
            ? null
            : Sts2EventRoomScreenInspector.ResolvePage(roomObject, notices);
        if (roomObject is not null && eventChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "event-room-choices-unavailable",
                "The live event room did not expose any visible event options through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var executableChoices = eventChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "event-room-visible-choices-partial",
            "Visible event-room choices can remain unavailable when an option is locked, already chosen, or disabled; only executable options appear in availableActions.",
            visibleChoiceCount: eventChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            EventRoom: new EventRoomStateSnapshot(
                eventChoices
                    .OrderBy(choice => choice.Index)
                    .Select(choice => new VisibleItemStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Description: choice.DescriptionText?.Text,
                        OwnerPlayerId: choice.Snapshot.OwnerPlayerId,
                        Selected: false,
                        Provisional: false,
                        ChoiceKind: choice.Snapshot.ChoiceKind,
                        IntentKind: choice.Snapshot.IntentKind,
                        Perspective: choice.Snapshot.Perspective,
                        Arguments: choice.Snapshot.Arguments,
                        Enabled: choice.Snapshot.Enabled,
                        DisabledReason: choice.Snapshot.DisabledReason,
                        PreferredAction: choice.Snapshot.PreferredActionRef))
                    .ToArray(),
                Page: eventPage),
            Choices: eventChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.EventRoomActions(localPlayerId, executableChoices),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureTreasureRoom(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var roomObject = Sts2TreasureRoomScreenInspector.ResolveActiveRoom(screenObject);
        var relicCollection = Sts2TreasureRoomScreenInspector.ResolveActiveRelicCollection(screenObject, roomObject);
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (roomObject is null && relicCollection is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "treasure-room-screen-unavailable",
                "The treasure room was classified as active but the live room or relic-selection instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "treasure-room-run-unavailable",
                "The treasure room was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var treasureChoices = roomObject is null && relicCollection is null
            ? []
            : Sts2TreasureRoomScreenInspector.ResolveChoices(roomObject, screenObject, localPlayerId, notices);
        if ((roomObject is not null || relicCollection is not null) && treasureChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "treasure-room-choices-unavailable",
                "The live treasure room did not expose visible chest, relic, or proceed choices through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var executableChoices = treasureChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "treasure-room-visible-choices-partial",
            "Visible treasure-room choices can remain unavailable when a chest, relic, or proceed control is visible but currently disabled; only executable choices appear in availableActions.",
            visibleChoiceCount: treasureChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            TreasureRoom: new TreasureRoomStateSnapshot(
                treasureChoices
                    .Where(choice => choice.Kind == TreasureRoomChoiceKind.Relic)
                    .Select((choice, index) => new VisibleItemStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Description: $"enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}",
                        OwnerPlayerId: choice.Snapshot.OwnerPlayerId,
                        Selected: false,
                        Provisional: false,
                        ChoiceKind: choice.Snapshot.ChoiceKind ?? choice.Snapshot.Kind,
                        IntentKind: choice.Snapshot.IntentKind,
                        Perspective: choice.Snapshot.Perspective,
                        Arguments: choice.Snapshot.Arguments,
                        Enabled: choice.IsExecutable,
                        DisabledReason: choice.Snapshot.DisabledReason,
                        PreferredAction: choice.Snapshot.PreferredActionRef))
                    .ToArray()),
            RelicSelection: new RelicSelectionStateSnapshot(
                treasureChoices
                    .Where(choice => choice.Kind == TreasureRoomChoiceKind.Relic)
                    .Select((choice, index) => new VisibleItemStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Description: $"enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}",
                        OwnerPlayerId: choice.Snapshot.OwnerPlayerId,
                        Selected: false,
                        Provisional: false,
                        ChoiceKind: choice.Snapshot.ChoiceKind ?? choice.Snapshot.Kind,
                        IntentKind: choice.Snapshot.IntentKind,
                        Perspective: choice.Snapshot.Perspective,
                        Arguments: choice.Snapshot.Arguments,
                        Enabled: choice.IsExecutable,
                        DisabledReason: choice.Snapshot.DisabledReason,
                        PreferredAction: choice.Snapshot.PreferredActionRef))
                    .ToArray()),
            Choices: treasureChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.TreasureRoomActions(localPlayerId, executableChoices),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureCrystalSphere(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = ResolveCurrentCrystalSphereScreenObject();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"))
        {
            notices.Add(Sts2StateNotice.Partial(
                "crystal-sphere-screen-unavailable",
                "The Crystal Sphere screen was classified as active but the live screen instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "crystal-sphere-run-unavailable",
                "The Crystal Sphere screen was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var crystalSphereChoices = screenObject is null
            ? []
            : Sts2CrystalSphereScreenInspector.ResolveChoices(screenObject, localPlayerId, notices);
        if (screenObject is not null && crystalSphereChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "crystal-sphere-choices-unavailable",
                "The live Crystal Sphere screen did not expose visible tool, cell, or proceed choices through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var executableChoices = crystalSphereChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            Choices: crystalSphereChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.CrystalSphereActions(localPlayerId, executableChoices),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static object? ResolveCurrentCrystalSphereScreenObject()
    {
        var overlayStack = NOverlayStack.Instance;
        var overlay = overlayStack?.Peek();
        if (Sts2LiveIntrospection.IsType(
                overlay,
                "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"))
        {
            return overlay;
        }

        if (overlayStack is not null)
        {
            var children = overlayStack.GetChildren();
            for (var index = children.Count - 1; index >= 0; index -= 1)
            {
                var child = children[index];
                if (Sts2LiveIntrospection.IsType(
                        child,
                        "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"))
                {
                    return child;
                }
            }
        }

        return Sts2LiveIntrospection.ResolveCurrentScreenObject();
    }

    private static BridgeRuntimeObservation CaptureRewards(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen"))
        {
            notices.Add(Sts2StateNotice.Partial(
                "reward-screen-unavailable",
                "The rewards screen was classified as active but the live reward screen instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "reward-run-unavailable",
                "The rewards screen was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var rewardChoices = screenObject is null
            ? []
            : Sts2RewardScreenInspector.ResolveChoices(screenObject, localPlayerId, notices);
        if (screenObject is not null && rewardChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "reward-choices-unavailable",
                "The live rewards screen did not expose any executable reward buttons through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var choiceOwnersById = rewardChoices.ToDictionary(
            choice => choice.Snapshot.Id,
            choice => choice.PlayerId,
            StringComparer.Ordinal);
        var executableChoices = rewardChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "rewards-visible-choices-partial",
            "Visible rewards choices can remain unavailable when a reward button or proceed/skip control is currently disabled; only executable choices appear in availableActions.",
            visibleChoiceCount: rewardChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            Rewards: new RewardsStateSnapshot(
                rewardChoices
                    .Select((choice, index) => new VisibleItemStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Description: choice.IsFlowChoice
                            ? $"kind={choice.RewardKind};enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}"
                            : $"kind={choice.RewardKind};rewardId={choice.RewardId ?? string.Empty};enabled={choice.IsExecutable};order={choice.DisplayIndex ?? index};preferredAction={choice.PreferredAction}",
                        OwnerPlayerId: choice.PlayerId,
                        Selected: false,
                        Provisional: false,
                        ChoiceKind: choice.Snapshot.ChoiceKind ?? choice.Snapshot.Kind,
                        IntentKind: choice.Snapshot.IntentKind,
                        Perspective: choice.Snapshot.Perspective,
                        Arguments: choice.Snapshot.Arguments,
                        Enabled: choice.IsExecutable,
                        DisabledReason: choice.Snapshot.DisabledReason,
                        PreferredAction: choice.Snapshot.PreferredActionRef))
                    .ToArray()),
            Choices: rewardChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.RewardActions(
                executableChoices,
                choiceOwnersById),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureRestSite(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (screenObject is null
            || !Sts2LiveIntrospection.IsType(screenObject, Sts2RestSiteScreenInspector.RestSiteRoomType))
        {
            notices.Add(Sts2StateNotice.Partial(
                "rest-site-screen-unavailable",
                "The rest-site room was classified as active but the live room instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "rest-site-run-unavailable",
                "The rest-site room was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var restSiteChoices = screenObject is null
            ? []
            : Sts2RestSiteScreenInspector.ResolveChoices(screenObject, localPlayerId, notices);
        if (screenObject is not null && restSiteChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "rest-site-choices-unavailable",
                "The live rest-site room did not expose any executable options through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var choiceOwnersById = restSiteChoices.ToDictionary(
            choice => choice.Snapshot.Id,
            choice => choice.PlayerId,
            StringComparer.Ordinal);
        var executableChoices = restSiteChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "rest-site-visible-choices-partial",
            "Visible rest-site choices can remain unavailable when an option is disabled; only executable options appear in availableActions.",
            visibleChoiceCount: restSiteChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            RestSite: new RestSiteStateSnapshot(
                restSiteChoices
                    .Select((choice, index) => new VisibleControlStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Enabled: choice.IsExecutable,
                        OwnerPlayerId: choice.PlayerId,
                        Description: $"enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}",
                        ChoiceKind: choice.Snapshot.ChoiceKind ?? choice.Snapshot.Kind,
                        IntentKind: choice.Snapshot.IntentKind,
                        Perspective: choice.Snapshot.Perspective,
                        Arguments: choice.Snapshot.Arguments,
                        DisabledReason: choice.Snapshot.DisabledReason,
                        PreferredAction: choice.Snapshot.PreferredActionRef))
                    .ToArray()),
            Choices: restSiteChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.RestSiteActions(executableChoices, choiceOwnersById),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureCardSelection(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);
        var isPartialCardGridScreen = Sts2CardSelectionScreenInspector.IsPartialCardGridScreen(screenObject);
        var isBundleSelectionScreen = Sts2SupportedScreenIds.IsBundleSelectionScreenType(screen.ScreenType);

        if (!Sts2CardSelectionScreenInspector.IsSupportedScreen(screenObject))
        {
            notices.Add(Sts2StateNotice.Partial(
                "card-selection-screen-unavailable",
                "The card-selection screen was classified as active but the live overlay instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "card-selection-run-unavailable",
                "The card-selection screen was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var cardChoices = screenObject is null
            ? []
            : Sts2CardSelectionScreenInspector.ResolveChoices(screenObject, localPlayerId, notices);
        if (screenObject is not null && cardChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                isBundleSelectionScreen
                    ? "bundle-selection-choices-unavailable"
                    : "card-selection-choices-unavailable",
                isBundleSelectionScreen
                    ? "The live bundle-selection overlay did not expose any visible bundle-row choices through the current hooks; the overlay may already be in unsupported preview/confirm flow."
                    : "The live card-selection screen did not expose executable card or skip choices through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var executableChoices = cardChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var confirmSelectionAvailable = isPartialCardGridScreen
            && Sts2CardSelectionScreenInspector.CanConfirmSelection(screenObject);
        var cancelSelectionAvailable = isPartialCardGridScreen
            && Sts2CardSelectionScreenInspector.CanCancelSelection(screenObject);
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            isPartialCardGridScreen || isBundleSelectionScreen
                ? $"{screen.ScreenType}-visible-choices-partial"
                : "card-selection-visible-choices-partial",
                isPartialCardGridScreen || isBundleSelectionScreen
                ? screen.ScreenType switch
                {
                    _ when Sts2SupportedScreenIds.IsSimpleCardSelectionScreenType(screen.ScreenType) =>
                        "Visible simple-card-selection cards stage selection through choose; confirm-selection and cancel-selection only appear after a selection is staged.",
                    _ when Sts2SupportedScreenIds.IsDeckCardSelectionScreenType(screen.ScreenType) =>
                        "Visible deck-card-selection cards stage selection through choose; confirm-selection and cancel-selection only appear after a selection is staged.",
                    _ when Sts2SupportedScreenIds.IsBundleSelectionScreenType(screen.ScreenType) =>
                        "Visible bundle choices can remain unavailable when the bundle row is already dismissed into preview flow; only currently executable bundle-row choices appear in availableActions.",
                    _ =>
                        "Visible card-grid choices stage selection through choose; follow-through actions appear only when the live overlay exposes them.",
                }
                : "Visible card-selection choices can remain unavailable when a card, skip button, or alternative button is currently disabled; only executable choices appear in availableActions.",
            visibleChoiceCount: cardChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        var typedItems = cardChoices
            .Select(choice => new VisibleItemStateSnapshot(
                Id: choice.Snapshot.Id,
                Label: choice.Snapshot.Label,
                Description: null,
                OwnerPlayerId: choice.Snapshot.OwnerPlayerId,
                Selected: isPartialCardGridScreen && choice.Snapshot.Kind == "card-selection-card" && !choice.IsExecutable,
                Provisional: choice.Snapshot.Provisional,
                ChoiceKind: choice.Snapshot.ChoiceKind ?? choice.Snapshot.Kind,
                IntentKind: choice.Snapshot.IntentKind,
                Perspective: choice.Snapshot.Perspective,
                Arguments: choice.Snapshot.Arguments,
                Enabled: choice.IsExecutable,
                DisabledReason: choice.Snapshot.DisabledReason,
                PreferredAction: choice.Snapshot.PreferredActionRef))
            .ToArray();

        var cardSelection = !isPartialCardGridScreen && !isBundleSelectionScreen
            ? new CardSelectionStateSnapshot(typedItems)
            : null;
        var simpleCardSelection = Sts2SupportedScreenIds.IsSimpleCardSelectionScreenType(screen.ScreenType)
            ? new SimpleCardSelectionStateSnapshot(typedItems)
            : null;
        var deckCardSelection = Sts2SupportedScreenIds.IsDeckCardSelectionScreenType(screen.ScreenType)
            ? new DeckCardSelectionStateSnapshot(typedItems)
            : null;
        var bundleSelection = isBundleSelectionScreen
            ? new BundleSelectionStateSnapshot(typedItems)
            : null;

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            CardSelection: cardSelection,
            SimpleCardSelection: simpleCardSelection,
            DeckCardSelection: deckCardSelection,
            BundleSelection: bundleSelection,
            Choices: cardChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.CardSelectionActions(
                localPlayerId,
                executableChoices,
                confirmSelectionAvailable,
                cancelSelectionAvailable),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureShop(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var screenObject = Sts2ShopScreenInspector.ResolveActiveShopScreenObject()
            ?? NMerchantRoom.Instance?.Inventory;
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (screenObject is null || !Sts2ShopScreenInspector.IsShopScreen(screenObject))
        {
            notices.Add(Sts2StateNotice.Partial(
                "shop-screen-unavailable",
                "The shop screen was classified as active but the live merchant inventory instance was unavailable.",
                "screen",
                nameof(Sts2RuntimeObservationProvider)));
        }

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "shop-run-unavailable",
                "The shop screen was visible but the active run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var shopChoices = screenObject is null
            ? []
            : Sts2ShopScreenInspector.ResolveChoices(screenObject, localPlayerId, notices);
        if (shopChoices.Count == 0)
        {
            var currentRoom = Sts2LiveIntrospection.GetMemberValue(runState, "CurrentRoom");
            var inventory = Sts2LiveIntrospection.GetMemberValue(currentRoom, "Inventory")
                ?? NMerchantRoom.Instance?.Room?.GetLocalInventory()
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "Inventory");
            shopChoices = Sts2ShopScreenInspector.ResolveInventoryChoices(inventory, localPlayerId, notices);
        }
        if (screenObject is not null && shopChoices.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "shop-choices-unavailable",
                "The live shop screen did not expose any merchant slots through the current hooks.",
                "choices",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);
        var choiceOwnersById = shopChoices.ToDictionary(
            choice => choice.Snapshot.Id,
            choice => choice.PlayerId,
            StringComparer.Ordinal);
        var executableChoices = shopChoices
            .Where(choice => choice.IsExecutable)
            .Select(choice => choice.Snapshot)
            .ToArray();
        var partialChoiceNotice = Sts2PartialChoiceNotice.TryCreate(
            "shop-visible-choices-partial",
            "Visible shop choices can remain unavailable when a slot is out of stock or the active player cannot afford it; only executable choices appear in availableActions.",
            visibleChoiceCount: shopChoices.Count,
            executableChoiceCount: executableChoices.Length);
        if (partialChoiceNotice is not null)
        {
            notices.Add(partialChoiceNotice);
        }

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            Shop: new ShopStateSnapshot(
                shopChoices
                    .Select((choice, index) => new VisibleItemStateSnapshot(
                        choice.Snapshot.Id,
                        choice.Snapshot.Label,
                        Description: choice.IsFlowChoice
                            ? $"kind={choice.SlotKind};enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}"
                            : $"kind={choice.SlotKind};modelId={choice.ItemModelId ?? string.Empty};cost={(choice.Cost.HasValue ? choice.Cost.Value.ToString() : string.Empty)};stocked={choice.IsStocked};enoughGold={choice.EnoughGold};enabled={choice.IsExecutable};order={index};preferredAction={choice.PreferredAction}",
                        OwnerPlayerId: choice.PlayerId,
                        Selected: false,
                        Provisional: false))
                    .ToArray()),
            Choices: shopChoices.Select(choice => choice.Snapshot).ToArray(),
            AvailableActions: Sts2ActionCatalog.ShopActions(executableChoices, choiceOwnersById),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureStartRunLobby(
        ScreenLocatorResult screen,
        GameStateQuery query,
        object? screenObject,
        StartRunLobby lobby,
        List<StateNoticeSnapshot> notices)
    {
        var localPlayerId = lobby.LocalPlayer is LobbyPlayer localPlayer
            ? LobbyPlayerId(localPlayer.id)
            : !string.IsNullOrWhiteSpace(lobby.NetService?.NetId.ToString())
                ? LobbyPlayerId(lobby.NetService.NetId)
                : null;
        var platform = lobby.NetService is null ? null : (object?)lobby.NetService.Platform;
        var hostPlayerId = ResolveHostPlayerId(lobby.NetService, localPlayerId, notices);

        var players = lobby.Players
            .Select(player => SnapshotStartRunPlayer(player, localPlayerId, hostPlayerId, platform, notices))
            .ToArray();
        var playersById = players.ToDictionary(player => player.Id, StringComparer.Ordinal);

        var resolvedCharacters = ResolveLobbyCharactersFromButtons(screenObject, notices);
        var availableCharacters = resolvedCharacters.Characters;
        var includeCharacterChoices = availableCharacters.Count > 0;
        if (availableCharacters.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "lobby-available-characters-modeldb-fallback",
                "Lobby character buttons were unavailable; falling back to ModelDb.AllCharacters.",
                "lobby.availableCharacters",
                nameof(Sts2RuntimeObservationProvider)));
            availableCharacters = ModelDb.AllCharacters
                .Select(character => SnapshotLobbyCharacter(character, IsUnlocked: true))
                .ToArray();
        }

        var availableCharactersById = availableCharacters
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var lobbySnapshot = new LobbyStateSnapshot(
            LobbyId: "start-run",
            Phase: lobby.IsAboutToBeginGame() ? "starting" : "selecting",
            Players: players,
            AvailableCharacters: availableCharacters,
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            LocalPlayerRole: ResolveLocalPlayerRole(lobby.NetService),
            PlayersById: playersById,
            AvailableCharactersById: availableCharactersById,
            WaitingText: ResolveLobbyWaitingText(screenObject));
        lobbySnapshot = lobbySnapshot with
        {
            PresentationGeometry = Sts2LobbyPresentationGeometryResolver.Resolve(
                screenObject,
                lobbySnapshot,
                screen,
                resolvedCharacters.CharacterButtonsById),
        };
        AddLobbyPresentationGeometryNotices(lobbySnapshot, notices);
        var choices = Sts2ActionCatalog.LobbyChoices(lobbySnapshot, includeCharacterChoices);

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: lobbySnapshot,
            Run: null,
            Combat: null,
            MultiplayerLobby: new MultiplayerLobbyStateSnapshot(
                Players: players.Select(player => new VisibleItemStateSnapshot(
                    Id: player.Id,
                    Label: player.Name ?? player.Id,
                    Description: $"status={player.Status};slot={player.SlotId};selectedCharacterId={player.SelectedCharacterId ?? string.Empty};isLocal={player.IsLocal};isHost={player.IsHost};isRemote={player.IsRemote}",
                    OwnerPlayerId: player.Id,
                    Selected: player.IsLocal,
                    Provisional: false,
                    PlayerId: player.Id,
                    IsLocal: player.IsLocal,
                    IsHost: player.IsHost,
                    IsHostLocalSeat: player.IsHostLocalSeat,
                    IsRemote: player.IsRemote,
                    HostPlayerId: hostPlayerId,
                    RemoteOrchestration: player.IsHostLocalSeat
                        ? OwnershipMetadata.HostLocalSeat()
                        : OwnershipMetadata.LocalOnlyDegraded())).ToArray(),
                Actions: Sts2ActionCatalog.LobbyActions(lobbySnapshot)
                    .Select(action => new VisibleActionReferenceSnapshot(
                        Id: action.Id,
                        Label: action.Summary,
                        Enabled: true,
                        OwnerPlayerId: action.OwnerPlayerId))
                    .ToArray()),
            Choices: choices,
            AvailableActions: Sts2ActionCatalog.LobbyActions(lobbySnapshot),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureLoadRunLobby(
        ScreenLocatorResult screen,
        GameStateQuery query,
        LoadRunLobby lobby,
        List<StateNoticeSnapshot> notices)
    {
        var localPlayerId = lobby.NetService is null ? null : LobbyPlayerId(lobby.NetService.NetId);
        var platform = lobby.NetService is null ? null : (object?)lobby.NetService.Platform;
        var hostPlayerId = ResolveHostPlayerId(lobby.NetService, localPlayerId, notices);
        var runSnapshot = ResolveLoadRunLobbyRunSnapshot(lobby.Run);
        var players = (lobby.Run?.Players ?? [])
            .Select((player, index) => SnapshotLoadRunPlayer(player, index, localPlayerId, hostPlayerId, lobby, platform, notices))
            .ToArray();
        var playersById = players.ToDictionary(player => player.Id, StringComparer.Ordinal);
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var resolvedCharacters = ResolveLobbyCharactersFromButtons(screenObject, notices);
        var availableCharacters = resolvedCharacters.Characters;
        if (availableCharacters.Count == 0)
        {
            notices.Add(Sts2StateNotice.Partial(
                "lobby-available-characters-unavailable",
                "Load-run lobbies do not expose visible character buttons through the current hooks.",
                "lobby.availableCharacters",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var lobbySnapshot = new LobbyStateSnapshot(
            LobbyId: "load-run",
            Phase: players.Any(player => player.IsReady) ? "confirming" : "waiting",
            Players: players,
            AvailableCharacters: availableCharacters,
            LocalPlayerId: localPlayerId,
            HostPlayerId: hostPlayerId,
            LocalPlayerRole: ResolveLocalPlayerRole(lobby.NetService),
            PlayersById: playersById,
            AvailableCharactersById: availableCharacters.ToDictionary(character => character.Id, StringComparer.Ordinal),
            WaitingText: ResolveLobbyWaitingText(screenObject));
        lobbySnapshot = lobbySnapshot with
        {
            PresentationGeometry = Sts2LobbyPresentationGeometryResolver.Resolve(
                screenObject,
                lobbySnapshot,
                screen,
                resolvedCharacters.CharacterButtonsById),
        };
        AddLobbyPresentationGeometryNotices(lobbySnapshot, notices);
        var choices = Sts2ActionCatalog.LobbyChoices(lobbySnapshot, includeCharacterChoices: availableCharacters.Count > 0);

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: lobbySnapshot,
            Run: runSnapshot,
            Combat: null,
            MultiplayerLobby: new MultiplayerLobbyStateSnapshot(
                Players: players.Select(player => new VisibleItemStateSnapshot(
                    Id: player.Id,
                    Label: player.Name ?? player.Id,
                    Description: $"status={player.Status};slot={player.SlotId};selectedCharacterId={player.SelectedCharacterId ?? string.Empty};isLocal={player.IsLocal};isHost={player.IsHost};isRemote={player.IsRemote}",
                    OwnerPlayerId: player.Id,
                    Selected: player.IsLocal,
                    Provisional: false,
                    PlayerId: player.Id,
                    IsLocal: player.IsLocal,
                    IsHost: player.IsHost,
                    IsHostLocalSeat: player.IsHostLocalSeat,
                    IsRemote: player.IsRemote,
                    HostPlayerId: hostPlayerId,
                    RemoteOrchestration: player.IsHostLocalSeat
                        ? OwnershipMetadata.HostLocalSeat()
                        : OwnershipMetadata.LocalOnlyDegraded())).ToArray(),
                Actions: Sts2ActionCatalog.LobbyActions(lobbySnapshot)
                    .Select(action => new VisibleActionReferenceSnapshot(
                        Id: action.Id,
                        Label: action.Summary,
                        Enabled: true,
                        OwnerPlayerId: action.OwnerPlayerId))
                    .ToArray()),
            Choices: choices,
            AvailableActions: Sts2ActionCatalog.LobbyActions(lobbySnapshot),
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static RunStateSnapshot? ResolveLoadRunLobbyRunSnapshot(object? savedRun)
        => Sts2SavedRunSnapshotResolver.Resolve(savedRun);

    private static LobbyPlayerSnapshot SnapshotStartRunPlayer(
        LobbyPlayer player,
        string? localPlayerId,
        string? hostPlayerId,
        object? platform,
        ICollection<StateNoticeSnapshot> notices)
    {
        var playerId = LobbyPlayerId(player.id);
        var isLocal = string.Equals(playerId, localPlayerId, StringComparison.Ordinal);
        var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.id);
        return new LobbyPlayerSnapshot(
            Id: playerId,
            Status: player.isReady ? "ready" : "not-ready",
            Name: Sts2LobbyNameResolver.Resolve(platform, player.id, notices),
            SelectedCharacterId: player.character?.Id.Entry,
            IsReady: player.isReady,
            SlotId: player.slotId,
            IsLocal: isLocal,
            IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
            IsHostLocalSeat: isHostLocalSeat,
            IsRemote: !isLocal && !isHostLocalSeat);
    }

    private static LobbyPlayerSnapshot SnapshotLoadRunPlayer(
        SerializablePlayer player,
        int index,
        string? localPlayerId,
        string? hostPlayerId,
        LoadRunLobby lobby,
        object? platform,
        ICollection<StateNoticeSnapshot> notices)
    {
        var playerId = LobbyPlayerId(player.NetId);
        var isReady = lobby.IsPlayerReady(player.NetId);
        var isLocal = string.Equals(playerId, localPlayerId, StringComparison.Ordinal);
        var isHostLocalSeat = Sts2HostLocalSeatRegistry.IsHostLocalSeat(player.NetId);
        return new LobbyPlayerSnapshot(
            Id: playerId,
            Status: isReady ? "ready" : "not-ready",
            Name: Sts2LobbyNameResolver.Resolve(platform, player.NetId, notices),
            SelectedCharacterId: player.CharacterId?.Entry,
            IsReady: isReady,
            SlotId: index,
            IsLocal: isLocal,
            IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
            IsHostLocalSeat: isHostLocalSeat,
            IsRemote: !isLocal && !isHostLocalSeat);
    }

    private static ResolvedLobbyCharacters ResolveLobbyCharactersFromButtons(
        object? screenObject,
        ICollection<StateNoticeSnapshot> notices)
    {
        var root = Sts2LiveIntrospection.GetMemberValue(screenObject, "_charButtonContainer") as Node
            ?? screenObject as Node;
        if (root is null)
        {
            return new ResolvedLobbyCharacters([], new Dictionary<string, Node>(StringComparer.Ordinal));
        }

        var characters = new List<LobbyCharacterSnapshot>();
        var characterButtonsById = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var child in Sts2TreeSearch.FindDescendants(
                     root,
                     static node => node.GetChildren().OfType<Node>(),
                     static node => Sts2LiveIntrospection.IsType(node, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton")))
        {
            var visibilityChecks = new List<string>();
            if (!Sts2LobbyPresentationGeometryResolver.TryControlGlobalRect(
                    child,
                    "_charButtonContainer/*",
                    visibilityChecks,
                    out _,
                    out _))
            {
                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool isRandom && isRandom)
            {
                var isRandomLocked = Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool randomLocked && randomLocked;
                var random = RandomLobbyCharacter(!isRandomLocked);
                characters.Add(random);
                characterButtonsById.TryAdd(random.Id, child);
                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "Character") is not CharacterModel character)
            {
                continue;
            }

            var isLocked = Sts2LiveIntrospection.GetMemberValue(child, "IsLocked") is bool locked && locked;
            var snapshot = SnapshotLobbyCharacter(character, !isLocked);
            characters.Add(snapshot);
            characterButtonsById.TryAdd(snapshot.Id, child);
        }

        var filtered = RemoveRandomCharacterWhenAnyVisibleCharacterIsLocked(characters
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray());
        return new ResolvedLobbyCharacters(
            filtered,
            characterButtonsById
                .Where(pair => filtered.Any(character => string.Equals(character.Id, pair.Key, StringComparison.Ordinal)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private static void AddLobbyPresentationGeometryNotices(
        LobbyStateSnapshot lobby,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (lobby.PresentationGeometry?.Missing.Count is not > 0)
        {
            return;
        }

        foreach (var missing in lobby.PresentationGeometry.Missing)
        {
            notices.Add(Sts2StateNotice.Partial(
                "lobby-presentation-geometry-missing",
                $"Lobby presentation geometry key '{missing.Key}' was not resolved from live controls: {missing.Reason}. Checked: {string.Join(", ", missing.CheckedPaths)}",
                $"lobby.presentationGeometry.{missing.Key}",
                nameof(Sts2LobbyPresentationGeometryResolver)));
        }
    }

    private static IReadOnlyList<LobbyCharacterSnapshot> RemoveRandomCharacterWhenAnyVisibleCharacterIsLocked(IReadOnlyList<LobbyCharacterSnapshot> characters)
    {
        if (!characters.Any(character => !IsRandomLobbyCharacter(character.Id) && !character.IsUnlocked))
        {
            return characters;
        }

        return characters
            .Where(character => !IsRandomLobbyCharacter(character.Id))
            .ToArray();
    }

    private static bool IsRandomLobbyCharacter(string? characterId)
        => string.Equals(characterId, SpirectlModels.RandomCharacterFacts.Id, StringComparison.OrdinalIgnoreCase);

    private static LobbyCharacterSnapshot RandomLobbyCharacter(bool isUnlocked)
        => new(
            Id: SpirectlModels.RandomCharacterFacts.Id,
            Name: ResolveLocString(SpirectlModels.RandomCharacterFacts.LocTable, SpirectlModels.RandomCharacterFacts.NameKey) ?? "Random Character",
            IsUnlocked: isUnlocked,
            NameKey: $"{SpirectlModels.RandomCharacterFacts.LocTable}.{SpirectlModels.RandomCharacterFacts.NameKey}",
            Description: ResolveLocString(SpirectlModels.RandomCharacterFacts.LocTable, SpirectlModels.RandomCharacterFacts.DescriptionKey),
            PortraitAssetKey: SpirectlModels.RandomCharacterFacts.PortraitAssetKey,
            IconAssetKey: null,
            SelectBackgroundAssetKey: SpirectlModels.RandomCharacterFacts.SelectBackgroundAssetKey);

    private static LobbyCharacterSnapshot SnapshotLobbyCharacter(CharacterModel character, bool IsUnlocked)
    {
        var id = character.Id.Entry;
        var nameKey = $"characters.{character.CharacterSelectTitle}";
        var name = ResolveLocString("characters", character.CharacterSelectTitle)
            ?? character.CharacterSelectTitle
            ?? id;
        var passive = character.StartingRelics.Count > 0 ? character.StartingRelics[0] : null;
        var passiveId = passive?.Id.Entry;
        var passiveSlug = Slug(passiveId);

        return new LobbyCharacterSnapshot(
            Id: id,
            Name: name,
            IsUnlocked: IsUnlocked,
            NameKey: nameKey,
            Description: ResolveLocString("characters", character.CharacterSelectDesc),
            StartingHp: character.StartingHp,
            StartingGold: character.StartingGold,
            PassiveId: passiveId,
            PassiveName: ResolveLocString(passive?.Title),
            PassiveDescription: ResolveLocString(passive?.DynamicDescription),
            PassiveIconAssetKey: passiveSlug is null ? null : $"model://relics/{passiveSlug}/icon",
            PortraitAssetKey: $"model://characters/{Slug(id)}/characterSelectIcon",
            IconAssetKey: $"model://characters/{Slug(id)}/icon",
            SelectBackgroundAssetKey: $"model://characters/{Slug(id)}/characterSelectBg",
            NameColor: CharacterNameColor(character));
    }

    private static string? CharacterNameColor(CharacterModel character)
        => Sts2LiveIntrospection.GetMemberValue(character, "NameColor") is Color color
            ? CssRgb(color)
            : null;

    private static string CssRgb(Color color)
        => $"rgb({ColorChannel(color.R)} {ColorChannel(color.G)} {ColorChannel(color.B)})";

    private static int ColorChannel(float value)
        => Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);

    private static string CharacterSelectBackgroundAssetKey(CharacterModel character, string id)
    {
        var configured = Sts2LiveIntrospection.GetMemberValue(character, "CharacterSelectBg")?.ToString();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = configured!.Replace('\\', '/').Trim();
            if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".escn", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
            {
                return $"res://scenes/screens/char_select/{normalized}";
            }
        }

        return $"res://scenes/screens/char_select/char_select_bg_{Slug(id).Replace('-', '_')}.tscn";
    }

    private static string? ResolveLocString(string table, string key)
    {
        try
        {
            var locStringType = Type.GetType("MegaCrit.Sts2.Core.Localization.LocString, sts2", throwOnError: false);
            if (locStringType is null)
            {
                return null;
            }

            return ResolveLocStringObject(Activator.CreateInstance(locStringType, table, key));
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocString(LocString? value)
        => ResolveLocStringObject(value);

    private static string? ResolveLobbyWaitingText(object? screenObject)
    {
        try
        {
            if (screenObject is not Node screenNode)
            {
                return null;
            }

            var soloNode = screenNode.GetNodeOrNull<Node>(new NodePath("RemotePlayerContainer/Container/SoloLabel"))
                ?? Sts2TreeSearch.FindDescendants(
                    screenNode,
                    static node => node.GetChildren().OfType<Node>(),
                    static node => string.Equals(node.Name.ToString(), "SoloLabel", StringComparison.Ordinal))
                    .FirstOrDefault();
            var soloText = ResolveTextNodeObject(soloNode);
            if (!string.IsNullOrWhiteSpace(soloText))
            {
                return soloText;
            }

            var waitingNode = screenNode.GetNodeOrNull<Node>(new NodePath("ReadyAndWaitingPanel/WaitingForPlayers"));
            return ResolveLocStringObject(waitingNode);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveTextNodeObject(object? value)
    {
        if (value is not null)
        {
            var text = Spirectl.Sts2.Common.Sts2RuntimeSceneTextDiagnostics.Describe(value)?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return ResolveLocStringObject(value);
    }

    private static string? ResolveLocStringObject(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var formatted = value.GetType().GetMethod("GetFormattedText", Type.EmptyTypes)?.Invoke(value, null)?.ToString();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }
        catch
        {
        }

        try
        {
            return NormalizeString(value.GetType().GetMethod("GetRawText", Type.EmptyTypes)?.Invoke(value, null));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeString(object? value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string Slug(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().Replace('_', '-').ToLowerInvariant();

    private static string LobbyPlayerId(ulong netId)
        => $"p:{netId}";

    private static string? ResolveLocalPlayerRole(INetGameService? netService)
    {
        if (netService is null)
        {
            return null;
        }

        return netService.Type switch
        {
            NetGameType.Host => "host",
            NetGameType.Client => "client",
            NetGameType.Singleplayer => "singleplayer",
            NetGameType.Replay => "replay",
            _ => null,
        };
    }

    private static string? ResolveHostPlayerId(
        INetGameService? netService,
        string? localPlayerId,
        ICollection<StateNoticeSnapshot> notices)
        => Sts2LobbyHostResolver.Resolve(netService, localPlayerId, notices);

    // The run-terminal defeat (game-over) overlay. Detected from the trigger — the
    // live NGameOverScreen on NOverlayStack (see Sts2ScreenLocator) — rather than
    // routed as an "unsupported" screen. Phase 1 recognizes it and still produces a
    // run snapshot (the finished run state persists; the screen holds _runState), so
    // `sts2 state` reports the game-over screen instead of screen-unsupported. The
    // structured defeat payload (banner/quote/score) is projected in Phase 2.
    private static BridgeRuntimeObservation CaptureGameOver(ScreenLocatorResult screen, GameStateQuery query)
    {
        var notices = new List<StateNoticeSnapshot>();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var localPlayerId = runState is null ? null : ResolveLocalPlayerId(runState);

        if (runState is null)
        {
            notices.Add(Sts2StateNotice.Partial(
                "game-over-run-unavailable",
                "The game-over screen was active but the finished run state could not be resolved.",
                "run",
                nameof(Sts2RuntimeObservationProvider)));
        }

        var runPlayers = runState is null ? [] : SnapshotRunPlayers(runState);

        return CreateObservation(
            screen,
            query,
            Provisional: notices.Count > 0,
            DefaultPlayerId: localPlayerId,
            Menu: null,
            Lobby: null,
            Run: runState is null
                ? null
                : SnapshotRun(runState, runPlayers),
            Combat: null,
            Choices: [],
            AvailableActions: [],
            Notices: notices,
            Debug: CreateDebug(screen, query));
    }

    private static BridgeRuntimeObservation CaptureUnsupported(ScreenLocatorResult screen, GameStateQuery query)
    {
        return CreateObservation(
            screen,
            query,
            Provisional: true,
            DefaultPlayerId: null,
            Menu: null,
            Lobby: null,
            Run: null,
            Combat: null,
            Choices: [],
            AvailableActions: [],
            Notices:
            [
                Sts2UnsupportedScreenNotice.Create(
                    screen.ScreenType,
                    screen.ScreenTitle,
                    screen.Source,
                    screen.ScreenClassName),
            ],
            Debug: CreateDebug(screen, query));
    }

    private static CardStateSnapshot CardSnapshot(dynamic card, string playerId, IEnumerable<Creature> enemies, int index, ICollection<StateNoticeSnapshot> notices)
    {
        var cardId = Sts2CombatIds.CardId((object)card, playerId, index, notices);

        var targetIds = new List<string>();
        try
        {
            if (card.TargetType.ToString() == "AnyEnemy")
            {
                var enemyIndex = 0;
                foreach (var enemy in enemies)
                {
                    if (enemy.IsAlive && card.IsValidTarget(enemy))
                    {
                        targetIds.Add(Sts2CombatIds.EnemyId(enemy, enemyIndex, notices));
                    }

                    enemyIndex++;
                }
            }
        }
        catch
        {
            // Keep target list empty when target validation is unavailable.
        }

        var playable = false;
        string? unplayableReason = null;
        try
        {
            if ((object)card is CardModel model)
            {
                var reasons = Sts2CombatFacts.ResolveUnplayableReasons(model);
                playable = reasons.Count == 0;
                unplayableReason = reasons.Count == 0 ? null : string.Join(", ", reasons);
            }
            else
            {
                playable = card.CanPlay();
            }
        }
        catch
        {
            // Leave as false/unknown if legality inspection fails.
        }

        return Sts2PresentationStateResolver.WithCardPresentation(new CardStateSnapshot(
            Id: cardId,
            Name: ((object)card).GetType().Name,
            Cost: (int)card.EnergyCost.Canonical,
            OwnerPlayerId: playerId,
            Playable: playable,
            UnplayableReason: unplayableReason,
            TargetIds: targetIds,
            Upgraded: card.CurrentUpgradeLevel > 0),
            (object)card);
    }

    private static IReadOnlyList<string> ResolveCombatPileCardIds(
        object? playerCombatState,
        string pileMemberName,
        string playerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var pile = Sts2LiveIntrospection.GetMemberValue(playerCombatState, pileMemberName);
        var cardsObject = Sts2LiveIntrospection.GetMemberValue(pile, "Cards") ?? pile;
        if (cardsObject is not IEnumerable cards || cardsObject is string)
        {
            return [];
        }

        var zone = pileMemberName.Replace("Pile", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var ids = new List<string>();
        var index = 0;
        foreach (var card in cards)
        {
            if (card is null)
            {
                continue;
            }

            ids.Add(Sts2CombatIds.CardIdInZone(card, playerId, zone, index, notices));
            index++;
        }

        return ids;
    }

    private static PotionStateSnapshot PotionSnapshot(dynamic potion, string playerId, int slotIndex, dynamic combatState, ICollection<StateNoticeSnapshot> notices)
    {
        var potionId = Sts2CombatIds.PotionId((object)potion, playerId, slotIndex);
        IReadOnlyList<string> targetIds = ResolvePotionTargetIds((object)potion, playerId, combatState, notices);
        var isQueued = SafeBool(() => (bool)potion.IsQueued);
        var passesUsability = SafeBool(() => (bool)potion.PassesCustomUsabilityCheck, fallback: true);
        var canBeUsedManually = PotionCanBeUsedManually((object)potion);
        var usable = canBeUsedManually && !isQueued && passesUsability && (!PotionNeedsTarget((object)potion) || targetIds.Count > 0);
        var unusableReason = usable
            ? null
            : !canBeUsedManually
                ? "Potion is automatic and cannot be manually used."
                : isQueued
                    ? "Potion is already queued for use."
                    : !passesUsability
                        ? "Potion is not currently usable."
                        : "Potion requires a target that is not currently available.";

        return Sts2PresentationStateResolver.WithPotionPresentation(new PotionStateSnapshot(
            Id: potionId,
            Name: ResolvePotionName((object)potion),
            OwnerPlayerId: playerId,
            SlotIndex: slotIndex,
            Usable: usable,
            UnusableReason: unusableReason,
            TargetIds: targetIds),
            (object)potion);
    }

    private static StateSelectedPotionSnapshot? ResolveSelectedPotionView(dynamic runState, string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        object? player = null;
        foreach (var candidate in EnumerateCollection(runState.Players))
        {
            if (candidate is not null && string.Equals(Sts2CombatIds.PlayerId((dynamic)candidate), playerId, StringComparison.Ordinal))
            {
                player = candidate;
                break;
            }
        }

        if (player is null)
        {
            return null;
        }

        var holder = ResolveActivePotionHolder(player);
        if (holder is null)
        {
            return null;
        }

        var potionNode = Sts2LiveIntrospection.GetMemberValue(holder, "Potion");
        var potion = Sts2LiveIntrospection.GetMemberValue(potionNode, "Model");
        if (potion is null)
        {
            return null;
        }

        var slotIndex = ResolvePotionSlotIndex(player, potion);
        if (slotIndex < 0)
        {
            return null;
        }

        var selectedPotion = ResolveSelectedPotion();
        var isTargeting = Sts2TargetManagerAccess.IsInSelection
            && ReferenceEquals(selectedPotion, potion);
        var isPopupOpen = IsLiveGodotObject(Sts2LiveIntrospection.GetMemberValue(holder, "_popup"));
        if (!isTargeting && !isPopupOpen)
        {
            return null;
        }

        return new StateSelectedPotionSnapshot(
            isTargeting ? "targeting" : "popup",
            slotIndex);
    }

    private static object? ResolveActivePotionHolder(object player)
    {
        var selectedPotion = Sts2TargetManagerAccess.IsInSelection
            ? ResolveSelectedPotion()
            : null;
        var holders = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "TopBar"),
                "PotionContainer"),
            "_holders");

        foreach (var holder in EnumerateCollection(holders))
        {
            if (holder is null)
            {
                continue;
            }

            var potionNode = Sts2LiveIntrospection.GetMemberValue(holder, "Potion");
            var potion = Sts2LiveIntrospection.GetMemberValue(potionNode, "Model");
            if (potion is null || ResolvePotionSlotIndex(player, potion) < 0)
            {
                continue;
            }

            if (ReferenceEquals(potion, selectedPotion)
                || ToBoolean(Sts2LiveIntrospection.GetMemberValue(holder, "_potionTargeting"))
                || IsLiveGodotObject(Sts2LiveIntrospection.GetMemberValue(holder, "_popup")))
            {
                return holder;
            }
        }

        return null;
    }

    private static object? ResolveSelectedPotion()
        => Sts2LiveIntrospection.GetMemberValue(RunManager.Instance?.HoveredModelTracker, "_localSelectedPotion");

    private static int ResolvePotionSlotIndex(object player, object potion)
    {
        var potionSlots = Sts2LiveIntrospection.GetMemberValue(player, "PotionSlots");
        var index = 0;
        foreach (var slotPotion in EnumerateCollection(potionSlots))
        {
            if (ReferenceEquals(slotPotion, potion))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static IEnumerable<object?> EnumerateCollection(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static bool IsLiveGodotObject(object? value)
        => value is GodotObject godotObject && GodotObject.IsInstanceValid(godotObject);

    private static bool ToBoolean(object? value)
        => value switch
        {
            bool typed => typed,
            _ => false,
        };

    private static IReadOnlyList<string> ResolvePotionTargetIds(object potion, string playerId, dynamic combatState, ICollection<StateNoticeSnapshot> notices)
    {
        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        if (string.Equals(targetType, "Self", StringComparison.Ordinal))
        {
            // Self targets are addressed by the owning player's creature id (creature:{CombatId}),
            // consistent with how enemy and ally targets are addressed.
            return ((IEnumerable<Creature>)combatState.Allies)
                .Where(creature => creature.Player is not null
                    && string.Equals(Sts2CombatIds.PlayerId(creature.Player!), playerId, StringComparison.Ordinal))
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index, notices))
                .ToArray();
        }

        if (string.Equals(targetType, "AnyEnemy", StringComparison.Ordinal))
        {
            return ((IEnumerable<Creature>)combatState.Enemies)
                .Select((enemy, index) => new { enemy, index })
                .Where(entry => entry.enemy.IsAlive && SafeBool(() => ((dynamic)potion).CanPlayTargeting(entry.enemy), fallback: true))
                .Select(entry => Sts2CombatIds.CreatureId(entry.enemy, entry.index, notices))
                .ToArray();
        }

        if (string.Equals(targetType, "AnyPlayer", StringComparison.Ordinal)
            || string.Equals(targetType, "AnyAlly", StringComparison.Ordinal))
        {
            return ((IEnumerable<Creature>)combatState.Allies)
                .Where(creature => creature.Player is not null && creature.IsAlive)
                .Select((creature, index) => Sts2CombatIds.CreatureId(creature, index, notices))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        return [];
    }

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

    private static string ResolvePotionName(object potion)
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

    private static EnemyStateSnapshot EnemySnapshot(
        Creature enemy,
        int index,
        ICollection<StateNoticeSnapshot> notices,
        IEnumerable<Creature> allies,
        IEnumerable<Creature> allEnemies,
        EncounterVisualsStateSnapshot? encounterVisuals)
    {
        var monster = enemy.Monster;
        var intents = new List<EnemyIntentSnapshot>();
        var intentSummary = "unknown";

        try
        {
            var alliesList = allies as IReadOnlyList<Creature> ?? allies.ToArray();
            var allTargets = alliesList.Concat(allEnemies).ToArray();
            var move = Sts2CombatFacts.ResolveNextMove(enemy, allTargets);
            if (move is { } resolved)
            {
                intentSummary = resolved.Id;
                var targetIds = alliesList
                    .Where(ally => ally.Player is not null)
                    .Select((ally, allyIndex) => Sts2CombatIds.CreatureId(ally, allyIndex, notices))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var fact in resolved.Intents)
                {
                    if (fact.IsAttack && fact.Intent is AttackIntent attackIntent)
                    {
                        intents.Add(Sts2PresentationStateResolver.WithIntentPresentation(new EnemyIntentSnapshot(
                            fact.Type,
                            fact.Damage,
                            fact.Hits,
                            attackIntent.GetTotalDamage(allTargets, enemy)),
                            targetIds));
                    }
                    else
                    {
                        intents.Add(Sts2PresentationStateResolver.WithIntentPresentation(new EnemyIntentSnapshot(fact.Type, 0, 0, 0), []));
                    }
                }
            }
        }
        catch
        {
            // Keep summary/intents best-effort.
        }

        var snapshot = Sts2PresentationStateResolver.WithEnemyPresentation(new EnemyStateSnapshot(
            Id: Sts2CombatIds.EnemyId(enemy, index, notices),
            Name: monster?.GetType().Name ?? "unknown",
            Hp: enemy.CurrentHp,
            Intent: intentSummary,
            MaxHp: enemy.MaxHp,
            Block: enemy.Block,
            IsAlive: enemy.IsAlive,
            Intents: intents),
            enemy,
            index);
        return ApplyEncounterVisualPackage(snapshot, encounterVisuals);
    }

    internal static EncounterVisualsStateSnapshot? ResolveEncounterVisuals(
        string? encounterId,
        Sts2EncounterVisualEventStore eventStore,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (string.IsNullOrWhiteSpace(encounterId))
        {
            return null;
        }

        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        if (!catalog.TryGetPackage(encounterId, out var package))
        {
            notices.Add(new StateNoticeSnapshot(
                "encounter-visuals-unsupported",
                "Encounter visual state is unavailable because the current encounter is not backed by catalog metadata.",
                false,
                Path: "combat.encounterVisuals",
                Severity: "unsupported",
                Source: nameof(Sts2RuntimeObservationProvider),
                Stability: "stable"));
            return null;
        }

        var recentEvents = eventStore.Recent(package.PackageId);
        var visualParts = package.VisualParts
            .Select(part => new EncounterVisualPartStateSnapshot(
                part.PartId,
                part.ActorId,
                eventStore.LatestActiveStateForPart(package.PackageId, part.PartId) ?? "default",
                part.ScreenSide,
                part.AnatomicalSide))
            .ToArray();
        var stateNotices = new[]
        {
            Sts2StateNotice.Partial(
                "encounter-visuals-provisional",
                "Encounter visual state is a provisional shared combat observation backed by base-game catalog mappings and bounded recent visual transition events.",
                "combat.encounterVisuals",
                nameof(Sts2RuntimeObservationProvider)),
        };

        foreach (var notice in stateNotices)
        {
            notices.Add(notice);
        }

        return new EncounterVisualsStateSnapshot(
            package.PackageId,
            package.EncounterId,
            Provisional: true,
            visualParts,
            recentEvents,
            stateNotices);
    }

    private static EnemyStateSnapshot ApplyEncounterVisualPackage(
        EnemyStateSnapshot enemy,
        EncounterVisualsStateSnapshot? encounterVisuals)
    {
        if (encounterVisuals is null || enemy.Visual is null)
        {
            return enemy;
        }

        var visual = enemy.Visual;
        var matchedPart = encounterVisuals.VisualParts.FirstOrDefault(part =>
            string.Equals(part.ActorId, visual.EncounterSlotId, StringComparison.Ordinal)
            || string.Equals(part.ActorId, enemy.ModelId, StringComparison.Ordinal)
            || string.Equals(part.PartId, enemy.ModelId, StringComparison.Ordinal));
        if (matchedPart is null)
        {
            return enemy;
        }

        return enemy with
        {
            Visual = visual with
            {
                EncounterSlotId = matchedPart.ActorId,
                ScreenSide = matchedPart.ScreenSide,
                EncounterVisualPackageId = encounterVisuals.PackageId,
            },
        };
    }

    private static string ResolveLocalPlayerId(dynamic runState)
    {
        try
        {
            var player = LocalContext.GetMe(runState);
            if (player is not null)
            {
                return Sts2CombatIds.PlayerId(player);
            }
        }
        catch
        {
            // Fall back to first visible player.
        }

        var firstPlayer = ((IEnumerable<dynamic>)runState.Players).FirstOrDefault();
        return firstPlayer is null ? "p:unknown" : Sts2CombatIds.PlayerId(firstPlayer);
    }

    private static bool IsHostLocalCombatSeat(object player)
    {
        var netIdValue = Sts2LiveIntrospection.GetMemberValue(player, "NetId");
        if (netIdValue is ulong netId)
        {
            return Sts2HostLocalSeatRegistry.IsHostLocalSeat(netId);
        }

        return ulong.TryParse(netIdValue?.ToString(), out var parsedNetId)
            && Sts2HostLocalSeatRegistry.IsHostLocalSeat(parsedNetId);
    }

    private static string? ResolveLocalPlayerIdOrNull()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            return runState is null ? null : ResolveLocalPlayerId(runState);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveCombatEncounterId(dynamic runState)
    {
        try
        {
            if (runState.CurrentRoom is CombatRoom combatRoom)
            {
                return combatRoom.Encounter.Id.Entry;
            }
        }
        catch
        {
            // Keep checkpoint fixture metadata best-effort.
        }

        return string.Empty;
    }

    private static IReadOnlyList<PlayerStateSnapshot> SnapshotRunPlayers(dynamic runState)
    {
        var localPlayerId = ResolveLocalPlayerId(runState);
        var hostPlayerId = ResolveRunHostPlayerId(localPlayerId);

        return ((IEnumerable<dynamic>)runState.Players)
            .Select(player =>
            {
                var playerId = Sts2CombatIds.PlayerId(player);
                return Sts2PresentationStateResolver.WithPlayerPresentation(
                    new PlayerStateSnapshot(
                        Id: playerId,
                        Character: ResolveCharacterId(player.Character),
                        Hp: player.Creature?.CurrentHp ?? 0,
                        MaxHp: player.Creature?.MaxHp ?? 0,
                        IsLocal: string.Equals(playerId, localPlayerId, StringComparison.Ordinal),
                        IsHost: string.Equals(playerId, hostPlayerId, StringComparison.Ordinal),
                        IsRemote: !string.Equals(playerId, localPlayerId, StringComparison.Ordinal)),
                    player);
            })
            .Cast<PlayerStateSnapshot>()
            .ToArray();
    }

    private static string ResolveCharacterId(dynamic? character)
    {
        if (character is null)
        {
            return "unknown";
        }

        try
        {
            string? id = character.Id?.Entry;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return Sts2ModelResolver.NormalizeFixtureId(id);
            }
        }
        catch
        {
            // Some test doubles and future runtime objects may only expose a CLR type.
        }

        return Sts2ModelResolver.NormalizeFixtureId(character.GetType().Name);
    }

    private static string? ResolveRunHostPlayerId(string? localPlayerId)
        => Sts2LobbyHostResolver.ResolveRunHostPlayerId(RunManager.Instance?.NetService, localPlayerId);

    private static BridgeRuntimeObservation CreateObservation(
        ScreenLocatorResult screen,
        GameStateQuery query,
        bool Provisional,
        string? DefaultPlayerId,
        MenuStateSnapshot? Menu,
        LobbyStateSnapshot? Lobby,
        RunStateSnapshot? Run,
        CombatStateSnapshot? Combat,
        MapStateSnapshot? Map = null,
        EventRoomStateSnapshot? EventRoom = null,
        TreasureRoomStateSnapshot? TreasureRoom = null,
        RelicSelectionStateSnapshot? RelicSelection = null,
        RestSiteStateSnapshot? RestSite = null,
        ShopStateSnapshot? Shop = null,
        RewardsStateSnapshot? Rewards = null,
        CardSelectionStateSnapshot? CardSelection = null,
        SimpleCardSelectionStateSnapshot? SimpleCardSelection = null,
        DeckCardSelectionStateSnapshot? DeckCardSelection = null,
        BundleSelectionStateSnapshot? BundleSelection = null,
        MultiplayerLobbyStateSnapshot? MultiplayerLobby = null,
        IReadOnlyList<ChoiceSnapshot>? Choices = null,
        IReadOnlyList<AvailableActionSnapshot>? AvailableActions = null,
        IReadOnlyList<StateNoticeSnapshot>? Notices = null,
        DebugStateSnapshot? Debug = null,
        CardOverlayStateSnapshot? CardOverlay = null)
        => new(
            SchemaVersion: "spirectl/v0",
            GameVersion: ResolveGameVersion(),
            BridgeVersion: ResolveBridgeVersion(),
            Source: DataSourceKind.Live,
            Provisional: Provisional,
            ScreenType: screen.ScreenType,
            ScreenTitle: screen.ScreenTitle,
            ScreenInstanceId: screen.ScreenInstanceId,
            DefaultPlayerId: DefaultPlayerId,
            Menu: Menu,
            Lobby: Lobby,
            Run: Run,
            Combat: Combat,
            Map: Map,
            EventRoom: EventRoom,
            TreasureRoom: TreasureRoom,
            RelicSelection: RelicSelection,
            RestSite: RestSite,
            Shop: Shop,
            Rewards: Rewards,
            CardSelection: CardSelection,
            SimpleCardSelection: SimpleCardSelection,
            DeckCardSelection: DeckCardSelection,
            BundleSelection: BundleSelection,
            MultiplayerLobby: MultiplayerLobby,
            Choices: Choices ?? [],
            AvailableActions: AvailableActions ?? [],
            Notices: Notices ?? [],
            Debug: Debug,
            ScreenSource: screen.Source,
            ScreenRawType: screen.ScreenRawType,
            ScreenClassName: screen.ScreenClassName,
            CardOverlay: CardOverlay,
            Language: CurrentLanguage());

    private static string? CurrentLanguage()
    {
        try
        {
            var language = LocManager.Instance?.Language;
            return string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static DebugStateSnapshot? CreateDebug(ScreenLocatorResult screen, GameStateQuery query)
        => query.IncludeDebug ? new DebugStateSnapshot([$"screen-source={screen.Source}"]) : null;

    private static string? ResolvePerspective(string? ownerPlayerId)
        => OwnershipMetadata.ResolvePerspective(ownerPlayerId);

    private static string? ResolveMapNodeDisabledReason(
        ResolvedMapNode node,
        bool isTravelEnabled,
        bool isTraveling)
    {
        if (isTraveling)
        {
            return "travel-in-progress";
        }

        if (!isTravelEnabled)
        {
            return "travel-disabled";
        }

        return node.IsTravelable ? null : "not-travelable";
    }

    private static RunStateSnapshot SnapshotRun(dynamic runState, IReadOnlyList<PlayerStateSnapshot>? players = null)
        => SnapshotRun(runState, players ?? SnapshotRunPlayers(runState), Sts2PresentationStateResolver.ResolveEncounter(runState));

    private static RunStateSnapshot SnapshotRun(
        dynamic runState,
        IReadOnlyList<PlayerStateSnapshot> players,
        (string? Id, string? Label) encounter)
        => new(
            Seed: runState.Rng?.StringSeed ?? "unknown",
            Floor: runState.TotalFloor,
            Act: runState.CurrentActIndex + 1,
            Players: players,
            PlayersById: players.ToDictionary(player => player.Id, StringComparer.Ordinal),
            ActLabel: $"Act {runState.CurrentActIndex + 1}",
            FloorLabel: $"Floor {runState.TotalFloor}",
            EncounterId: encounter.Id,
            EncounterLabel: encounter.Label,
            RoomLabel: Sts2PresentationStateResolver.ResolveRoomLabel(runState.CurrentRoom));

    private static object? GetMemberValue(object? target, string memberName)
        => Sts2LiveIntrospection.GetMemberValue(target, memberName);

    private static string ResolveGameVersion()
        => typeof(RunManager).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string ResolveBridgeVersion()
        => BridgeBuildInfo.BridgeVersion;
}
