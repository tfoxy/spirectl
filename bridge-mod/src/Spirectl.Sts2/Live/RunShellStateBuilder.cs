using System.Collections;
using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Perspective;
using System.Reflection;

namespace Spirectl.Sts2.Live;

internal sealed class RunShellStateBuilder
{
    private readonly Sts2SelectedCardViewState _selectedCardViewState;

    internal RunShellStateBuilder(Sts2SelectedCardViewState selectedCardViewState)
    {
        _selectedCardViewState = selectedCardViewState;
    }

    internal StateRunSnapshot? ResolveRun(PlayerPerspective perspective)
    {
        var manager = RunManager.Instance;
        if (manager is null || !manager.IsInProgress)
        {
            return null;
        }

        RunState? runState;
        try
        {
            runState = manager.DebugOnlyGetState();
        }
        catch
        {
            return null;
        }

        if (runState is null)
        {
            return null;
        }

        var notices = new List<StateNoticeSnapshot>();
        var netService = manager.NetService;
        var localNetId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(netService, "NetId"));
        var localPlayerId = localNetId.HasValue ? $"p:{localNetId.Value}" : null;
        var hostPlayerId = Sts2LobbyHostResolver.ResolveRunHostPlayerId(netService, localPlayerId);
        var platform = Sts2LiveIntrospection.GetMemberValue(netService, "Platform");
        var visitedMapCoords = MapStateBuilder.ResolveMapCoords(Sts2LiveIntrospection.GetMemberValue(runState, "VisitedMapCoords"));
        var currentRoom = Sts2LiveIntrospection.GetMemberValue(runState, "CurrentRoom");
        var combatState = Sts2LiveIntrospection.GetMemberValue(currentRoom, "CombatState");
        var includePlayerCombat = combatState is not null
            || (currentRoom?.GetType().FullName?.Contains("CombatRoom", StringComparison.Ordinal) ?? false);

        var viewPlayerId = string.IsNullOrWhiteSpace(perspective.PlayerId)
            ? localPlayerId
            : perspective.PlayerId;
        // Resolved ONCE per snapshot (never per player — ResolveRunPlayers is a hot path). null == connectedness
        // unknown, which Sts2RunPlayerConnectivity.IsConnected reads as "everyone is connected".
        var connectedNetIds = Sts2RunPlayerConnectivity.ResolveConnectedNetIds(
            StateProjectionValues.ResolveHostNetService(netService),
            localNetId);
        var players = CombatStateBuilder.ResolveRunPlayers(runState, localNetId, localPlayerId, hostPlayerId, platform, includePlayerCombat, combatState, connectedNetIds);
        var map = MapStateBuilder.ResolveRunMap(runState, visitedMapCoords, notices, viewPlayerId, localPlayerId);
        var room = ResolveCurrentRoom(runState, manager, notices);
        var view = ResolveRunView(runState, notices, viewPlayerId, localPlayerId);

        return new StateRunSnapshot(
            SourceType: typeof(RunState).FullName ?? nameof(RunState),
            ManagerSourceType: typeof(RunManager).FullName ?? nameof(RunManager),
            NetGameType: StateProjectionValues.ResolveNetGameType(Sts2LiveIntrospection.GetMemberValue(netService, "Type")),
            GameMode: Sts2LiveIntrospection.GetMemberValue(runState, "GameMode")?.ToString() ?? string.Empty,
            Seed: StateProjectionValues.NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(runState, "Rng"), "StringSeed")?.ToString()) ?? "unknown",
            AscensionLevel: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(runState, "AscensionLevel")),
            ActId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(runState, "Act")),
            CurrentActIndex: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(runState, "CurrentActIndex")),
            ActFloor: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(runState, "ActFloor")),
            TotalFloor: StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(runState, "TotalFloor")),
            BossEncounterId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(runState, "Act"), "BossEncounter")),
            SecondBossEncounterId: StateProjectionValues.ResolveModelId(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(runState, "Act"), "SecondBossEncounter")),
            CurrentMapCoord: MapStateBuilder.ResolveMapCoord(Sts2LiveIntrospection.GetMemberValue(runState, "CurrentMapCoord")),
            CurrentMapPointId: MapStateBuilder.ResolveMapPointId(Sts2LiveIntrospection.GetMemberValue(runState, "CurrentMapPoint")),
            VisitedMapCoords: visitedMapCoords,
            Players: players,
            Map: map,
            CurrentRoom: room,
            Notices: notices,
            View: view);
    }

    private StateRunViewSnapshot ResolveRunView(
        RunState runState,
        ICollection<StateNoticeSnapshot> notices,
        string? viewPlayerId,
        string? localPlayerId)
    {
        var viewPlayer = ResolveRunPlayerById(runState, viewPlayerId);
        if (!string.IsNullOrWhiteSpace(viewPlayerId)
            && !string.IsNullOrWhiteSpace(localPlayerId)
            && !string.Equals(viewPlayerId, localPlayerId, StringComparison.Ordinal))
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone",
                "state-run-view-capstone-local-only",
                "Run capstone view is local-client transient UI and is omitted for the requested remote presentation perspective."));
            return new StateRunViewSnapshot(
                viewPlayerId,
                Capstone: null,
                SelectedCard: viewPlayer is null ? null : ResolveSelectedCardView(viewPlayer, viewPlayerId));
        }

        var capstone = ResolveRunCapstoneView(notices, localPlayerId);
        return new StateRunViewSnapshot(
            viewPlayerId,
            capstone,
            SelectedPotion: viewPlayer is null ? null : CombatStateBuilder.ResolveSelectedPotionView(viewPlayer),
            IsInCardSelection: ResolveIsInCardSelection(),
            SelectedCard: viewPlayer is null ? null : ResolveSelectedCardView(viewPlayer, viewPlayerId),
            InspectRelic: ResolveInspectRelicView(),
            HandSelection: ResolveHandSelectionView(notices, localPlayerId),
            GameOver: ResolveGameOverView(notices));
    }

    // In-hand selection mode (NPlayerHand SimpleSelect/UpgradeSelect, e.g.
    // Survivor's "Discard 1 card."). Selected cards stay in the hand pile while
    // staged (only their UI holders move to SelectedHandCardContainer), so ids
    // here match run.players[].combat.hand.cards. Like the capstone this is
    // local-client transient UI; the caller's remote-perspective early return
    // omits it.
    internal static StateHandSelectionViewSnapshot? ResolveHandSelectionView(
        ICollection<StateNoticeSnapshot> notices,
        string? localPlayerId)
    {
        var hand = NPlayerHand.Instance;
        if (hand is null || !hand.IsInCardSelection)
        {
            return null;
        }

        var mode = hand.CurrentMode switch
        {
            NPlayerHand.Mode.SimpleSelect => "simple-select",
            NPlayerHand.Mode.UpgradeSelect => "upgrade-select",
            var other => other.ToString().ToLowerInvariant(),
        };

        var prefs = Sts2LiveIntrospection.GetMemberValue(hand, "_prefs");
        var minSelect = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(prefs, "MinSelect"));
        var maxSelect = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(prefs, "MaxSelect"));
        var requireManualConfirmation =
            Sts2LiveIntrospection.GetMemberValue(prefs, "RequireManualConfirmation") is true;

        var promptText = string.Empty;
        StateLocRefSnapshot? promptLoc = null;
        var prompt = Sts2LiveIntrospection.GetMemberValue(prefs, "Prompt");
        if (prompt is not null)
        {
            try
            {
                promptText = Sts2LiveIntrospection.InvokeMethod(prompt, "GetFormattedText") as string
                    ?? string.Empty;
            }
            catch
            {
                notices.Add(StateProjectionValues.PartialNotice(
                    "run.view.handSelection.promptText",
                    "state-run-view-hand-selection-prompt",
                    "Hand-selection prompt text could not be formatted from the live prefs."));
            }

            var promptTable = Sts2LiveIntrospection.GetMemberValue(prompt, "LocTable") as string;
            var promptKey = Sts2LiveIntrospection.GetMemberValue(prompt, "LocEntryKey") as string;
            if (!string.IsNullOrEmpty(promptTable) && !string.IsNullOrEmpty(promptKey))
            {
                promptLoc = new StateLocRefSnapshot(promptTable!, promptKey!);
            }
        }

        string? playerId = null;
        var selectedCardIds = new List<string>();
        var selectedIndex = 0;
        foreach (var model in StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(hand, "_selectedCards")))
        {
            if (model is null)
            {
                continue;
            }

            playerId ??= ResolveCardOwnerPlayerId(model);
            selectedCardIds.Add(Sts2CombatIds.CardId(model, playerId ?? localPlayerId ?? "p:unknown", selectedIndex, notices));
            selectedIndex++;
        }

        var selectableCardIds = new List<string>();
        var selectableIndex = 0;
        foreach (var holder in StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(hand, "ActiveHolders")))
        {
            var card = Sts2LiveIntrospection.GetMemberValue(holder, "CardNode");
            var model = Sts2LiveIntrospection.GetMemberValue(card, "Model");
            if (model is null)
            {
                continue;
            }

            playerId ??= ResolveCardOwnerPlayerId(model);
            selectableCardIds.Add(Sts2CombatIds.CardId(model, playerId ?? localPlayerId ?? "p:unknown", selectableIndex, notices));
            selectableIndex++;
        }

        string? sourceCardId = null;
        string? sourceModelId = null;
        var source = Sts2HandSelectionHooks.CurrentSource;
        if (source is not null)
        {
            sourceModelId = StateProjectionValues.ResolveModelId(source);
            // Only mutable (in-combat) cards have a stable combat card id;
            // authored fixture sources are canonical models and stay model-only.
            if (source is CardModel { IsMutable: true })
            {
                sourceCardId = Sts2CombatIds.CardId(source, playerId ?? localPlayerId ?? "p:unknown", 0);
            }
        }

        var isPeeking = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(hand, "PeekButton"),
            "IsPeeking") is true;

        // Upgrade-select mode (e.g. Armaments) shows a before/after preview of the
        // focused card's upgraded version. NUpgradePreview holds the original card;
        // the snapshot factory already computes the upgraded vars into NextDynamicVars,
        // so the renderer can show base + upgraded inline (same as the deck upgrade screen).
        StateCardSnapshot? previewCard = null;
        if (string.Equals(mode, "upgrade-select", StringComparison.Ordinal)
            && Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(hand, "_upgradePreview"),
                "Card") is CardModel previewModel)
        {
            previewCard = Sts2CardStateSnapshotFactory.CreateUpgradedPreview(
                previewModel,
                $"card:{playerId ?? localPlayerId ?? "p:unknown"}:hand-upgrade-preview");
        }

        return new StateHandSelectionViewSnapshot(
            PlayerId: playerId ?? localPlayerId,
            Mode: mode,
            PromptText: promptText,
            PromptLoc: promptLoc,
            MinSelect: minSelect,
            MaxSelect: maxSelect,
            RequireManualConfirmation: requireManualConfirmation,
            CanConfirm: selectedCardIds.Count >= minSelect && selectedCardIds.Count <= maxSelect,
            SelectedCardIds: selectedCardIds,
            SelectableCardIds: selectableCardIds,
            SourceCardId: sourceCardId,
            SourceModelId: sourceModelId,
            IsPeeking: isPeeking,
            PreviewCard: previewCard);
    }

    private static string? ResolveCardOwnerPlayerId(object model)
    {
        var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(model, "Owner"),
            "NetId"));
        return netId.HasValue ? $"p:{netId.Value}" : null;
    }

    // The relic-details overlay (NInspectRelicScreen) is parented under NGame's
    // InspectionContainer rather than the capstone stack, so it is resolved off
    // NGame directly. Reads the InspectRelicScreen property — NOT
    // GetInspectRelicScreen(), which lazily creates the node as a side effect.
    // Like the capstone, it is local-client transient UI (omitted for remote
    // perspectives by the caller's early return).
    private static StateInspectRelicViewSnapshot? ResolveInspectRelicView()
    {
        var screen = Sts2LiveIntrospection.GetMemberValue(NGame.Instance, "InspectRelicScreen");
        if (screen is not Control control || !control.Visible)
        {
            return null;
        }

        var relics = new List<object?>();
        foreach (var relic in StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(screen, "_relics")))
        {
            relics.Add(relic);
        }

        var index = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(screen, "_index"));
        var current = index >= 0 && index < relics.Count ? relics[index] : null;
        var modelId = StateProjectionValues.ResolveModelId(current);
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        return new StateInspectRelicViewSnapshot(
            RelicModelId: modelId,
            Index: index,
            Count: relics.Count);
    }

    // The run-terminal game-over / defeat overlay (NGameOverScreen). Unlike the
    // capstone screens it is pushed onto NOverlayStack (the capstone is closed when
    // it opens), so it is resolved off the overlay-stack peek — the same trigger
    // the screen locator uses. Local-client transient UI (omitted for remote
    // perspectives by the caller's early return). The banner string and death quote
    // are RNG-picked at runtime, so read the LIVE resolved node text rather than
    // re-deriving from loc keys. Score / killed-by are data-only (the score renders
    // in the deferred post-Continue summary panel).
    private static StateGameOverViewSnapshot? ResolveGameOverView(ICollection<StateNoticeSnapshot> notices)
    {
        var screen = NOverlayStack.Instance?.Peek();
        if (screen is null
            || !Sts2LiveIntrospection.IsType(screen, "MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen"))
        {
            return null;
        }

        var bannerLabel = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(screen, "_banner"), "label");
        var bannerText = Sts2LiveIntrospection.GetMemberValue(bannerLabel, "Text")?.ToString();
        if (string.IsNullOrEmpty(bannerText))
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.gameOver.bannerText",
                "state-game-over-banner-unavailable",
                "The game-over screen banner label text could not be resolved."));
        }

        var deathQuote = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(screen, "_deathQuote"), "Text")?.ToString();

        var history = Sts2LiveIntrospection.GetMemberValue(screen, "_history");
        var win = StateProjectionValues.ToBoolean(Sts2LiveIntrospection.GetMemberValue(history, "Win"));
        // KilledByEncounter is a bare ModelId (string in `.Entry`), not a model with
        // a nested `.Id`, so ResolveModelId can't read it. `ModelId.none` => "none".
        var killedBy = StateProjectionValues.NormalizeNullable(
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(history, "KilledByEncounter"), "Entry")?.ToString());
        var killedByEncounterId = string.Equals(killedBy, "none", StringComparison.Ordinal) ? null : killedBy;

        var score = StateProjectionValues.ToInt32(Sts2LiveIntrospection.GetMemberValue(screen, "_score"));

        return new StateGameOverViewSnapshot(
            Win: win,
            BannerText: bannerText ?? string.Empty,
            DeathQuote: deathQuote ?? string.Empty,
            Score: score,
            KilledByEncounterId: killedByEncounterId);
    }

    private static object? ResolveRunPlayerById(RunState runState, string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        var index = 0;
        foreach (var player in StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")))
        {
            if (player is null)
            {
                continue;
            }

            var netId = StateProjectionValues.ToUInt64(Sts2LiveIntrospection.GetMemberValue(player, "NetId"));
            var candidateId = netId.HasValue ? $"p:{netId.Value}" : $"p:unknown:{index}";
            if (string.Equals(candidateId, playerId, StringComparison.Ordinal))
            {
                return player;
            }

            index++;
        }

        return null;
    }

    private static bool ResolveIsInCardSelection()
    {
        var playerHand = NPlayerHand.Instance;
        if (playerHand is not null && playerHand.IsInCardSelection)
        {
            return true;
        }

        return NOverlayStack.Instance?.Peek() is ICardSelector;
    }

    private static StateRunCapstoneViewSnapshot? ResolveRunCapstoneView(ICollection<StateNoticeSnapshot> notices, string? localPlayerId)
    {
        var capstoneContainer = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "CapstoneContainer");
        var capstone = Sts2LiveIntrospection.GetMemberValue(capstoneContainer, "CurrentCapstoneScreen");
        if (capstone is null)
        {
            return null;
        }

        var sourceType = capstone.GetType().FullName ?? capstone.GetType().Name;
        if (Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NCapstoneSubmenuStack"))
        {
            return new StateRunCapstoneViewSnapshot(
                Scene: "screens/capstone_submenu_stack",
                SourceType: sourceType,
                Stack: ResolveRunCapstoneStackEntries(Sts2LiveIntrospection.GetMemberValue(capstone, "Stack"), notices));
        }

        var scene = ResolveRunCapstoneScene(capstone);
        if (scene is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.scene",
                "state-capstone-scene-unavailable",
                $"The current run capstone screen scene could not be resolved for type {sourceType}."));
            scene = string.Empty;
        }

        var deckView = Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen")
            ? ResolveDeckView(capstone, notices)
            : null;

        var cardPileView = Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen")
            ? ResolveCardPileView(capstone, localPlayerId, notices)
            : null;

        return new StateRunCapstoneViewSnapshot(
            Scene: scene,
            SourceType: sourceType,
            Stack: [],
            DeckView: deckView,
            CardPileView: cardPileView);
    }

    // The combat draw/discard/exhaust pile viewer (NCardPileScreen) exposes the
    // CardPile it is showing through its public Pile property; we normalize its
    // PileType the same way ResolveCombatCardPile does so the type matches
    // run.players[].combat.{draw,discard,exhaust}Pile. The viewer is local-player
    // UI (NCombatCardPile binds the local player), so the owner is the local seat.
    private static StateCardPileViewSnapshot? ResolveCardPileView(
        object cardPileScreen,
        string? localPlayerId,
        ICollection<StateNoticeSnapshot> notices)
    {
        var pile = Sts2LiveIntrospection.GetMemberValue(cardPileScreen, "Pile");
        if (pile is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.cardPileView",
                "state-card-pile-view-unavailable",
                "The card pile screen did not expose NCardPileScreen.Pile."));
            return null;
        }

        var type = Sts2LiveIntrospection.GetMemberValue(pile, "Type")?.ToString();
        var normalized = StateProjectionValues.NormalizeIdentifier(type);
        if (normalized is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.cardPileView.pileType",
                "state-card-pile-view-type-unavailable",
                $"The card pile screen pile type {type ?? "<null>"} could not be normalized."));
            return null;
        }

        return new StateCardPileViewSnapshot(normalized, localPlayerId);
    }

    private static StateDeckViewSnapshot ResolveDeckView(
        object deckViewScreen,
        ICollection<StateNoticeSnapshot> notices)
    {
        return new StateDeckViewSnapshot(
            ResolveDeckViewSort(deckViewScreen, notices),
            ResolveDeckViewShowUpgrades(deckViewScreen, notices));
    }

    private static IReadOnlyList<StateDeckViewSortSnapshot> ResolveDeckViewSort(
        object deckViewScreen,
        ICollection<StateNoticeSnapshot> notices)
    {
        var sortingPriority = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, "_sortingPriority");
        if (sortingPriority is not IEnumerable rawSortingPriority)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.deckView.sort",
                "state-deck-view-sort-unavailable",
                "The deck view screen did not expose NDeckViewScreen._sortingPriority."));
            return [];
        }

        var sort = new List<StateDeckViewSortSnapshot>();
        foreach (var entry in rawSortingPriority)
        {
            if (StateDeckViewSortNormalizer.TryNormalize(entry, out var normalized))
            {
                sort.Add(normalized);
                continue;
            }

            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.deckView.sort",
                "state-deck-view-sort-unsupported",
                $"The deck view sorting priority value {entry?.ToString() ?? "<null>"} is not supported."));
        }

        return sort;
    }

    private static bool ResolveDeckViewShowUpgrades(
        object deckViewScreen,
        ICollection<StateNoticeSnapshot> notices)
    {
        var grid = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, "_grid");
        var gridShowingUpgrades = Sts2LiveIntrospection.GetMemberValue(grid, "IsShowingUpgrades");
        if (gridShowingUpgrades is not null)
        {
            return StateProjectionValues.ToBoolean(gridShowingUpgrades);
        }

        var showUpgrades = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, "_showUpgrades");
        var ticked = Sts2LiveIntrospection.GetMemberValue(showUpgrades, "IsTicked");
        if (ticked is not null)
        {
            return StateProjectionValues.ToBoolean(ticked);
        }

        notices.Add(StateProjectionValues.PartialNotice(
            "run.view.capstone.deckView.showUpgrades",
            "state-deck-view-show-upgrades-unavailable",
            "The deck view screen did not expose NCardGrid.IsShowingUpgrades or the upgrades tickbox state."));
        return false;
    }

    private static IReadOnlyList<StateRunStackEntrySnapshot> ResolveRunCapstoneStackEntries(
        object? submenuStack,
        ICollection<StateNoticeSnapshot> notices)
    {
        if (submenuStack is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.stack",
                "state-capstone-stack-unavailable",
                "The current run capstone submenu stack was not available."));
            return [];
        }

        if (Sts2LiveIntrospection.GetMemberValue(submenuStack, "_submenus") is not IEnumerable rawSubmenus)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.stack",
                "state-capstone-stack-unavailable",
                "The current run capstone submenu stack did not expose NSubmenuStack._submenus."));
            return [];
        }

        return rawSubmenus
            .Cast<object?>()
            .Where(static submenu => submenu is not null)
            .Reverse()
            .Select(submenu => ResolveRunCapstoneStackEntry(submenu!, notices))
            .ToArray();
    }

    private static StateRunStackEntrySnapshot ResolveRunCapstoneStackEntry(
        object submenu,
        ICollection<StateNoticeSnapshot> notices)
    {
        var sourceType = submenu.GetType().FullName ?? submenu.GetType().Name;
        var scene = ResolveRunSubmenuScene(submenu);
        if (scene is null)
        {
            notices.Add(StateProjectionValues.PartialNotice(
                "run.view.capstone.stack",
                "state-capstone-stack-entry-scene-unavailable",
                $"A run capstone submenu scene could not be resolved for type {sourceType}."));
        }

        return new StateRunStackEntrySnapshot(scene, sourceType);
    }

    private static string? ResolveRunCapstoneScene(object capstone)
    {
        if (Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen"))
        {
            return "screens/deck_view_screen";
        }

        if (Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen"))
        {
            return "screens/card_pile_screen";
        }

        return null;
    }

    private static string? ResolveRunSubmenuScene(object submenu)
    {
        if (Sts2LiveIntrospection.IsType(submenu, "MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu.NPauseMenu"))
        {
            return "screens/pause_menu/pause_menu";
        }

        if (Sts2LiveIntrospection.IsType(submenu, "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen"))
        {
            return "screens/settings_screen";
        }

        return null;
    }

    private static StateRunCurrentRoomSnapshot? ResolveCurrentRoom(
        RunState runState,
        RunManager manager,
        ICollection<StateNoticeSnapshot> notices)
    {
        var currentRoom = Sts2LiveIntrospection.GetMemberValue(runState, "CurrentRoom");
        if (currentRoom is null)
        {
            return null;
        }

        var roomType = Sts2LiveIntrospection.GetMemberValue(currentRoom, "RoomType")?.ToString() ?? string.Empty;
        var canonicalEvent = Sts2LiveIntrospection.GetMemberValue(currentRoom, "CanonicalEvent");
        var combatState = Sts2LiveIntrospection.GetMemberValue(currentRoom, "CombatState");
        var isEventRoom = canonicalEvent is not null
            || (currentRoom.GetType().FullName?.Contains("EventRoom", StringComparison.Ordinal) ?? false);
        var isCombatRoom = combatState is not null
            || (currentRoom.GetType().FullName?.Contains("CombatRoom", StringComparison.Ordinal) ?? false);
        var isTreasureRoom = string.Equals(roomType, "Treasure", StringComparison.Ordinal)
            || (currentRoom.GetType().FullName?.Contains("TreasureRoom", StringComparison.Ordinal) ?? false);
        var isShopRoom = string.Equals(roomType, "Shop", StringComparison.Ordinal)
            || (currentRoom.GetType().FullName?.Contains("MerchantRoom", StringComparison.Ordinal) ?? false);
        var isRestSiteRoom = string.Equals(roomType, "RestSite", StringComparison.Ordinal)
            || (currentRoom.GetType().FullName?.Contains("RestSiteRoom", StringComparison.Ordinal) ?? false);
        var isMapRoom = string.Equals(roomType, "Map", StringComparison.Ordinal)
            || (currentRoom.GetType().FullName?.Contains("MapRoom", StringComparison.Ordinal) ?? false);
        if (!isEventRoom && !isCombatRoom && !isTreasureRoom && !isShopRoom && !isRestSiteRoom && !isMapRoom)
        {
            notices.Add(StateProjectionValues.PartialNotice("run.currentRoom", "state-current-room-typed-section-unavailable", $"No typed state room section is available for room type {roomType}."));
        }

        return new StateRunCurrentRoomSnapshot(
            SourceType: currentRoom.GetType().FullName ?? currentRoom.GetType().Name,
            RoomType: roomType,
            Scene: ResolveCurrentRoomScene(roomType, currentRoom),
            ModelId: StateProjectionValues.ResolveRoomModelId(currentRoom) ?? StateProjectionValues.ResolveModelId(canonicalEvent),
            Event: isEventRoom ? RoomStateBuilder.ResolveEventRoom(runState, manager, currentRoom, canonicalEvent, notices) : null,
            Combat: isCombatRoom ? CombatStateBuilder.ResolveCombatRoom(currentRoom, combatState, notices) : null,
            Treasure: isTreasureRoom ? RoomStateBuilder.ResolveTreasureRoom(runState, manager, notices) : null,
            Shop: isShopRoom ? RoomStateBuilder.ResolveShopRoom(currentRoom, notices) : null,
            RestSite: isRestSiteRoom ? RoomStateBuilder.ResolveRestSiteRoom(runState, manager, notices) : null,
            MapRoom: isMapRoom ? new StateRunMapRoomSnapshot([]) : null,
            Id: MapStateBuilder.ToNullableInt32(Sts2LiveIntrospection.GetMemberValue(currentRoom, "Id")));
    }

    private static string ResolveCurrentRoomScene(string roomType, object currentRoom)
    {
        var fullTypeName = currentRoom.GetType().FullName ?? currentRoom.GetType().Name;
        if (string.Equals(roomType, "Monster", StringComparison.Ordinal)
            || string.Equals(roomType, "Elite", StringComparison.Ordinal)
            || string.Equals(roomType, "Boss", StringComparison.Ordinal)
            || fullTypeName.Contains("CombatRoom", StringComparison.Ordinal))
        {
            return "rooms/combat_room";
        }

        if (string.Equals(roomType, "Event", StringComparison.Ordinal)
            || fullTypeName.Contains("EventRoom", StringComparison.Ordinal))
        {
            return "rooms/event_room";
        }

        if (string.Equals(roomType, "Treasure", StringComparison.Ordinal)
            || fullTypeName.Contains("TreasureRoom", StringComparison.Ordinal))
        {
            return "rooms/treasure_room";
        }

        if (string.Equals(roomType, "Shop", StringComparison.Ordinal)
            || fullTypeName.Contains("MerchantRoom", StringComparison.Ordinal))
        {
            return "rooms/merchant_room";
        }

        if (string.Equals(roomType, "RestSite", StringComparison.Ordinal)
            || fullTypeName.Contains("RestSiteRoom", StringComparison.Ordinal))
        {
            return "rooms/rest_site_room";
        }

        if (string.Equals(roomType, "Map", StringComparison.Ordinal)
            || fullTypeName.Contains("MapRoom", StringComparison.Ordinal))
        {
            return "rooms/map_room";
        }

        return "rooms/event_room";
    }

    private StateSelectedCardSnapshot? ResolveSelectedCardView(object player, string? playerId)
    {
        var selected = _selectedCardViewState.Get(playerId);
        if (selected is null)
        {
            return null;
        }

        if (!IsCardVisibleInHand(player, selected))
        {
            _selectedCardViewState.ClearIf(selected.PlayerId, selected.CardId);
            return null;
        }

        return new StateSelectedCardSnapshot(selected.CardId, selected.PlayerId);
    }

    private static bool IsCardVisibleInHand(object player, Sts2SelectedCardViewEntry selected)
    {
        try
        {
            var hand = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(player, "PlayerCombatState"),
                "Hand");
            foreach (var (card, index) in StateProjectionValues.EnumerateCollection(Sts2LiveIntrospection.GetMemberValue(hand, "Cards")).Select((card, index) => (card, index)))
            {
                if (card is null)
                {
                    continue;
                }

                var cardId = Sts2CombatIds.CardId(card, selected.PlayerId, index);
                if (string.Equals(cardId, selected.CardId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

}
