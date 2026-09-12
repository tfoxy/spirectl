using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.MapDrawing;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Saves.Runs;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.GameApi;

namespace Spirectl.Sts2.Live;


public sealed partial class Sts2FixtureLoader : IFixtureLoader
{
    private const int FixtureRenderWarmupFrames = 2;
    private static readonly TimeSpan FixtureRenderFrameTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Sts2ScreenLocator _screenLocator;
    private readonly ILogStream _logStream;
    private readonly Sts2SelectedCardViewState _selectedCardViewState;
    private readonly MenuLobbyFixtureFamily _menuLobbyFixtures;
    private readonly CombatSelectionFixtureFamily _combatSelectionFixtures;
    private readonly RoomMapFixtureFamily _roomMapFixtures;
    private NetHostGameService? _activeLobbyHost;
    private bool _commandLineFastMpHostSettled;

    public Sts2FixtureLoader(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream,
        Sts2SelectedCardViewState? selectedCardViewState = null)
    {
        _screenLocator = screenLocator;
        _logStream = logStream;
        _selectedCardViewState = selectedCardViewState ?? new Sts2SelectedCardViewState();
        _menuLobbyFixtures = new MenuLobbyFixtureFamily(
            _screenLocator,
            _logStream,
            TryClearSubmenuStack,
            ClearFixtureLobbyOverlays,
            TryDisconnectLobbyHost,
            RetainActiveLobbyHost,
            ReleaseActiveLobbyHost,
            ResetLobbyFixtureLifecycle,
            TryBuildCompleteLoadRunSaveAsync);
        _combatSelectionFixtures = new CombatSelectionFixtureFamily(
            _screenLocator,
            _logStream,
            _selectedCardViewState,
            ResolveRoomlessSinglePlayerContextAsync,
            CreateHostLocalRunContextAsync);
        _roomMapFixtures = new RoomMapFixtureFamily(
            _screenLocator,
            _logStream,
            ResolveRoomlessSinglePlayerContextAsync,
            CreateSinglePlayerRunContextAsync,
            CreateHostLocalRunContextAsync,
            ValidateEventRoomRecipe,
            TryApplyOpenedTreasureRoomRecipe,
            ApplyFixtureAncientDialogueAsync,
            ApplyFixtureCrystalSphereChosenOptionAsync,
            ApplyFixtureCrystalSphereStateAsync,
            ApplyShopRecipe);
    }

    public FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
    {
        return LoadAsync(request).GetAwaiter().GetResult();
    }

    public Task<FixtureLoadResult> LoadAsync(FixtureLoadRequestSnapshot request)
    {
        return Sts2MainThreadDispatcher.InvokeAsync(() => LoadOnMainThread(request));
    }

    private async Task<FixtureLoadResult> LoadOnMainThread(FixtureLoadRequestSnapshot request)
    {
        try
        {
            var wireFixture = JsonSerializer.Deserialize<FixtureWireDocument>(request.FixtureJson, JsonOptions);
            if (wireFixture is null)
            {
                return InvalidFixture(
                    request,
                    "fixture_json",
                    request.FixtureJson,
                    "The bridge expected canonical fixture JSON from the CLI.");
            }

            if (!string.Equals(wireFixture.SchemaVersion, "spirectl.fixture/v0", StringComparison.Ordinal))
            {
                return InvalidFixture(
                    request,
                    "schemaVersion",
                    wireFixture.SchemaVersion,
                    "The bridge expects spirectl.fixture/v0 wire documents.");
            }

            var fixture = wireFixture.ToRecipeDocument();

            await WaitForCommandLineFastMpHostToSettle(request.FixtureName);

            if (RequiresLobbyCleanup(fixture.Screen))
            {
                ReleaseActiveLobbyHost();
                CleanUpActiveLobbyScreen(request.FixtureName);
            }

            if (_menuLobbyFixtures.TryCreateInput(request, fixture, out var menuLobbyInput))
            {
                return await _menuLobbyFixtures.LoadAsync(menuLobbyInput);
            }

            if (_combatSelectionFixtures.TryCreateInput(request, fixture, out var combatSelectionInput))
            {
                return await _combatSelectionFixtures.LoadAsync(combatSelectionInput);
            }

            if (_roomMapFixtures.TryCreateInput(request, fixture, out var roomMapInput))
            {
                return await _roomMapFixtures.LoadAsync(roomMapInput);
            }

            return InvalidFixture(
                request,
                "screen",
                fixture.Screen,
                $"The live fixture loader currently supports main-menu, combat, card-overlay families, and game-derived screen ids including {Sts2SupportedScreenIds.MapScreenId}, {Sts2SupportedScreenIds.RewardsScreenId}, {Sts2SupportedScreenIds.RestSiteRoomScreenId}, {Sts2SupportedScreenIds.EventRoomScreenId}, {Sts2SupportedScreenIds.CrystalSphereScreenId}, {Sts2SupportedScreenIds.TreasureRoomScreenId}, {Sts2SupportedScreenIds.RelicSelectionScreenId}, {Sts2SupportedScreenIds.ShopScreenId}, card-selection families, and {Sts2SupportedScreenIds.StartRunLobbyScreenId}/{Sts2SupportedScreenIds.LoadRunLobbyScreenId} recipes only.");
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.fixture",
                $"Fixture load '{request.FixtureName}' failed: {ex}");
            return FixtureLoadResult.Failure(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                code: FixtureLoadFailureCode.RuntimeFailure,
                message: "Fixture loading failed with an unhandled runtime exception.",
                details:
                [
                    new FixtureLoadDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                ]);
        }
    }

    // The coordinator owns the lifetime-sensitive cleanup. Family loaders only receive a
    // validated typed input, so a future family cannot accidentally retain a lobby host while
    // realizing a run screen.
    internal static bool RequiresLobbyCleanup(string screen)
        => !Sts2SupportedScreenIds.IsLobbyScreenType(screen);

    private static FixtureLoadResult InvalidFixture(
        FixtureLoadRequestSnapshot request,
        string field,
        string value,
        string note)
    {
        return FixtureLoadResult.Failure(
            requestId: request.RequestId,
            fixtureName: request.FixtureName,
            sourcePath: request.SourcePath,
            source: DataSourceKind.Live,
            provisional: false,
            code: FixtureLoadFailureCode.InvalidFixture,
            message: "The live fixture loader could not realize the requested fixture.",
            details:
            [
                new FixtureLoadDetail(
                    Field: field,
                    Value: value,
                    Note: note),
            ]);
    }

    private static FixtureLoadResult RuntimeFailure(
        FixtureLoadRequestSnapshot request,
        string field,
        string note)
    {
        return FixtureLoadResult.Failure(
            requestId: request.RequestId,
            fixtureName: request.FixtureName,
            sourcePath: request.SourcePath,
            source: DataSourceKind.Live,
            provisional: false,
            code: FixtureLoadFailureCode.RuntimeFailure,
            message: "The live fixture loader could not initialize the STS2 runtime path.",
            details:
            [
                new FixtureLoadDetail(
                    Field: field,
                    Value: request.FixtureName,
                    Note: note),
            ]);
    }

    private static async Task<FixtureLoadResult?> ApplyAuthoredPotionUiAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var setup = fixture.Ui?.Potion;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.playerId",
                setup.PlayerId,
                "Point ui.potion.playerId at one authored player from players[].id.");
        }

        if (setup.SlotIndex < 0)
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.slotIndex",
                setup.SlotIndex.ToString(CultureInfo.InvariantCulture),
                "Use a zero-based potion slot index.");
        }

        var potion = player.Player.GetPotionAtSlotIndex(setup.SlotIndex);
        if (potion is null)
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.slotIndex",
                setup.SlotIndex.ToString(CultureInfo.InvariantCulture),
                "The authored player does not have a potion in this slot.");
        }

        if (!FixturePotionCanBeUsedManually(potion))
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.slotIndex",
                setup.SlotIndex.ToString(CultureInfo.InvariantCulture),
                "The selected potion is automatic and cannot open a manual use popup.");
        }

        if (!potion.PassesCustomUsabilityCheck)
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.slotIndex",
                setup.SlotIndex.ToString(CultureInfo.InvariantCulture),
                "The selected potion is not currently usable.");
        }

        var holder = ResolveTopBarPotionHolder(setup.SlotIndex, potion);
        if (holder is null)
        {
            return RuntimeFailure(
                request,
                "run.view.selectedPotion.slotIndex",
                "Could not resolve the top-bar potion holder for the authored potion.");
        }

        holder.TryGrabFocus();
        if (string.Equals(setup.Mode, "popup", StringComparison.Ordinal))
        {
            if (!Sts2LiveIntrospection.TryInvokeMethod(holder, "OpenPotionPopup"))
            {
                return RuntimeFailure(
                    request,
                    "run.view.selectedPotion.mode",
                    "The top-bar potion holder did not expose OpenPotionPopup.");
            }

            return null;
        }

        if (!string.Equals(setup.Mode, "targeting", StringComparison.Ordinal))
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.mode",
                setup.Mode,
                "Use popup or targeting.");
        }

        if (!FixturePotionNeedsTarget(potion))
        {
            return InvalidFixture(
                request,
                "run.view.selectedPotion.mode",
                setup.Mode,
                "The selected potion cannot enter target selection.");
        }

        StartPotionTargeting(holder, potion);

        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            await Task.Delay(16);
            if (Sts2TargetManagerAccess.IsInSelection)
            {
                return null;
            }
        }

        return RuntimeFailure(
            request,
            "run.view.selectedPotion.mode",
            "Timed out waiting for potion targeting to become active.");
    }

    // Pre-selects an authored opening-hand card in spirectl view state
    // (state.run.view.selectedCard), the same per-player entry the select-card
    // semantic action toggles. Selection is keyed by the STATE player id
    // ("p:{NetId}") and the card is matched by model id because runtime hand-card
    // ids are unknowable at authoring time.
    // Enters in-hand selection mode (NPlayerHand.SelectCards) for the authored
    // run.view.handSelection. The loader holds the selection task itself —
    // confirming resolves it with no further card effect — so the staged state
    // and the select/deselect/confirm-hand-selection actions behave like a
    // live card effect (e.g. Survivor's discard prompt). source is passed as
    // null (a real source would subscribe canonical-model lifecycle events);
    // the authored sourceModelId is recorded on the hook slot instead.
    private static async Task<FixtureLoadResult?> ApplyAuthoredHandSelectionAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var setup = fixture.Ui?.HandSelection;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.handSelection.playerId",
                setup.PlayerId,
                "Point run.view.playerId at one authored player from players[].id.");
        }

        CardModel? sourceModel = null;
        if (!string.IsNullOrWhiteSpace(setup.SourceModelId))
        {
            if (!Sts2ModelResolver.TryResolveFixtureCard(setup.SourceModelId!, out sourceModel))
            {
                return InvalidFixture(
                    request,
                    "run.view.handSelection.sourceModelId",
                    setup.SourceModelId!,
                    "Use a card model id from the installed STS2 content.");
            }
        }

        // The opening hand can settle a few frames after EnterMapPoint; poll
        // briefly (same bounded wait as the selected-card setup).
        NPlayerHand? hand = null;
        IReadOnlyList<CardModel>? handCards = null;
        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            hand = NPlayerHand.Instance;
            handCards = player.Player.PlayerCombatState?.Hand?.Cards;
            if (hand is not null && handCards is { Count: > 0 })
            {
                break;
            }

            await Task.Delay(16);
        }

        if (hand is null || handCards is not { Count: > 0 })
        {
            return InvalidFixture(
                request,
                "run.view.handSelection",
                "hand-unavailable",
                "The combat hand did not become available; author a combat room that deals an opening hand.");
        }

        foreach (var index in setup.SelectedCardIndexes)
        {
            if (index < 0 || index >= handCards.Count)
            {
                return InvalidFixture(
                    request,
                    "run.view.handSelection.selectedCardIndexes",
                    index.ToString(CultureInfo.InvariantCulture),
                    $"Pre-staged indexes must address the dealt hand (0..{handCards.Count - 1}).");
            }
        }

        var isUpgradeMode = string.Equals(setup.Mode, "upgrade-select", StringComparison.Ordinal);
        var mode = isUpgradeMode ? NPlayerHand.Mode.UpgradeSelect : NPlayerHand.Mode.SimpleSelect;
        var prefs = new CardSelectorPrefs(
            isUpgradeMode ? CardSelectorPrefs.UpgradeSelectionPrompt : CardSelectorPrefs.DiscardSelectionPrompt,
            setup.MinSelect,
            setup.MaxSelect);
        var selectionTask = hand.SelectCards(prefs, filter: null, source: null, mode);
        _ = selectionTask.ContinueWith(
            task =>
            {
                _ = task.Exception; // observe faults; AnimOut cancels the task
                Sts2HandSelectionHooks.SetAuthoredSource(null);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (sourceModel is not null)
        {
            Sts2HandSelectionHooks.SetAuthoredSource(sourceModel);
        }

        var selectMethod = isUpgradeMode ? "SelectCardInUpgradeMode" : "SelectCardInSimpleMode";
        foreach (var index in setup.SelectedCardIndexes)
        {
            var holder = hand.GetCardHolder(handCards[index]);
            if (holder is null
                || !Sts2LiveIntrospection.TryInvokeMethod(hand, selectMethod, holder))
            {
                return InvalidFixture(
                    request,
                    "run.view.handSelection.selectedCardIndexes",
                    index.ToString(CultureInfo.InvariantCulture),
                    "The pre-staged hand card could not be selected in the live hand UI.");
            }
        }

        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        return null;
    }

    private static async Task<FixtureLoadResult?> ApplyAuthoredSelectedCardAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players,
        Sts2SelectedCardViewState selectedCardViewState)
    {
        var setup = fixture.Ui?.SelectedCard;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.selectedCard.playerId",
                setup.PlayerId,
                "Point selectedCard.playerId at one authored player from players[].id.");
        }

        if (!Sts2ModelResolver.TryResolveFixtureCard(setup.CardModelId, out var cardModel))
        {
            return InvalidFixture(
                request,
                "run.view.selectedCard.cardModelId",
                setup.CardModelId,
                "Use a card model id from the installed STS2 content.");
        }

        var statePlayerId = Sts2CombatIds.PlayerId(player.Player);
        var targetModelId = Sts2StateProvider.ResolveModelId(cardModel);

        // The opening hand can settle a few frames after EnterMapPoint; poll
        // briefly (same bounded wait as the potion-targeting setup).
        var handDealt = false;
        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            if (player.Player.PlayerCombatState?.Hand?.Cards is { Count: > 0 })
            {
                handDealt = true;
                break;
            }

            await Task.Delay(16);
        }

        if (!handDealt)
        {
            return InvalidFixture(
                request,
                "run.view.selectedCard.cardModelId",
                setup.CardModelId,
                "The combat hand did not become available; author a combat room that deals an opening hand.");
        }

        if (TrySelectHandCardByModel(player.Player, targetModelId, statePlayerId, selectedCardViewState))
        {
            return null;
        }

        // The dealt opening hand does not include this card (e.g. WHIRLWIND is not
        // in the Ironclad starter deck, so no seed lands it in hand). Deal one into
        // the player's combat hand so ANY card can be pre-selected, not just
        // opening-hand cards. The game's own dev-console `card` command produces a
        // fully owned, UI-bound combat instance (the same path `dev console card`
        // uses), unlike run-deck grants which never reach the combat piles.
        if (!TryAddCardToHand(player.Player, cardModel))
        {
            return InvalidFixture(
                request,
                "run.view.selectedCard.cardModelId",
                setup.CardModelId,
                "Could not add this card to the authored player's combat hand.");
        }

        // The added card can take a frame to land in Hand.Cards.
        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            if (TrySelectHandCardByModel(player.Player, targetModelId, statePlayerId, selectedCardViewState))
            {
                return null;
            }

            await Task.Delay(16);
        }

        return InvalidFixture(
            request,
            "run.view.selectedCard.cardModelId",
            setup.CardModelId,
            "The card was added but did not appear in the authored player's combat hand.");
    }

    // Selects the first hand card whose resolved model id matches, recording the
    // selection in spirectl view state by its stable combat card id.
    private static bool TrySelectHandCardByModel(Player player, string targetModelId, string statePlayerId, Sts2SelectedCardViewState selectedCardViewState)
    {
        var cards = player.PlayerCombatState?.Hand?.Cards;
        if (cards is not { Count: > 0 })
        {
            return false;
        }

        foreach (var (card, index) in cards.Select((card, index) => (card, index)))
        {
            if (card is null
                || !string.Equals(Sts2StateProvider.ResolveModelId(card), targetModelId, StringComparison.Ordinal))
            {
                continue;
            }

            selectedCardViewState.Set(statePlayerId, Sts2CombatIds.CardId(card, statePlayerId, index));
            return true;
        }

        return false;
    }

    // Headless dev console used only to inject authored cards during fixture load
    // (mirrors Sts2ConsoleCommandExecutor's DevConsole). Built lazily so merely
    // referencing the loader type outside a live game never constructs it. The
    // private ProcessCommand(Player, string, string[]) overload adds the card to
    // the given player's combat hand and returns the in-game `card` command result.
    private static DevConsole? _fixtureDevConsole;
    private static MethodInfo? _devConsoleProcessCommand;

    private static bool TryAddCardToHand(Player player, CardModel cardModel)
    {
        _fixtureDevConsole ??= new DevConsole(shouldAllowDebugCommands: true);
        _devConsoleProcessCommand ??=
            typeof(DevConsole).GetMethod(
                "ProcessCommand",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(Player), typeof(string), typeof(string[])],
                modifiers: null)
            ?? throw new MissingMethodException(typeof(DevConsole).FullName, "ProcessCommand(Player?, string, string[])");

        var result = (CmdResult)_devConsoleProcessCommand.Invoke(
            _fixtureDevConsole,
            [player, "card", new[] { cardModel.Id.Entry }])!;
        if (result.task is not null)
        {
            TaskHelper.RunSafely(result.task);
        }

        return result.success;
    }

    // Puts each seat authored with `endedTurn: true` into the REAL "ended its turn,
    // waiting for allies" state via the game's own PlayerCmd.EndTurn(canBackOut: true),
    // so the live game window reflects it (Cancelar button, AnimDisable'd hand, ready
    // ticks) and `act cancel-end-turn` round-trips. This is only safe because
    // Sts2EndTurnReadinessHooks patches CombatManager.AllPlayersReadyToEndTurn — without
    // the hook, the vanilla NetGameType.Singleplayer branch returns true unconditionally
    // and the FIRST seat's ready resolves the whole turn (enemy attacks, hands discard,
    // re-deal). When the hook is unavailable, or when readying the authored seats would
    // leave no seat still playing (so the turn would legitimately resolve), fall back to
    // marking Sts2AuthoredEndedTurnRegistry, which drives spirectl's STATE projection and
    // the presentation render without touching the live turn.
    // Applies authored status effects to combat creatures: player powers
    // (run.players[].powers, e.g. WEAK_POWER/FRAIL_POWER on the attacker) and
    // per-enemy powers (run.currentRoom.combat.enemies[i].powers, e.g.
    // VULNERABLE_POWER). Goes through the game's networked PowerCmd so the full
    // modifier pipeline runs and the selected card's damage/block preview recomputes
    // exactly as it would in play — the renderer then colors the diff generically.
    private static async Task<FixtureLoadResult?> ApplyAuthoredPowersAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var authoredEnemies = fixture.Room.Enemies;
        var hasEnemyAuthoring = authoredEnemies.Any(enemy =>
            enemy.Powers.Count > 0 || enemy.CurrentHp.HasValue || enemy.MaxHp.HasValue);

        // Powers attach to combat creatures, so wait until the play phase is live with
        // its creatures spawned — the same point the selected-card preview reads from.
        for (var attempt = 0; attempt < 240; attempt += 1)
        {
            var pending = CombatManager.Instance.DebugOnlyGetState();
            if (pending is not null && pending.Creatures.Count > 0 && Sts2CombatFacts.IsAnyPlayerInPlayPhase())
            {
                break;
            }

            await Task.Delay(16);
        }

        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
        {
            return InvalidFixture(
                request,
                "run.currentRoom.combat",
                string.Empty,
                "Authored powers require an active combat, but no combat state was available after load.");
        }

        var livePlayersByFixtureId = players.ToDictionary(player => player.FixtureId, player => player.Player);
        for (var index = 0; index < fixture.Players.Count; index += 1)
        {
            var authored = fixture.Players[index];
            if (authored.Powers is not { Count: > 0 } powers)
            {
                continue;
            }

            if (!livePlayersByFixtureId.TryGetValue(authored.Id, out var player) || player.Creature is null)
            {
                continue;
            }

            if (await ApplyAuthoredPowerListAsync(request, $"run.players[{index}].powers", powers, player.Creature) is { } playerInvalid)
            {
                return playerInvalid;
            }
        }

        if (hasEnemyAuthoring)
        {
            var enemies = combatState.Enemies;
            for (var i = 0; i < authoredEnemies.Count; i += 1)
            {
                var enemy = authoredEnemies[i];
                if (enemy.Powers.Count == 0 && !enemy.CurrentHp.HasValue && !enemy.MaxHp.HasValue)
                {
                    continue;
                }

                if (i >= enemies.Count)
                {
                    return InvalidFixture(
                        request,
                        $"run.currentRoom.combat.enemies[{i}]",
                        i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "Authored enemy index exceeds the loaded encounter's enemy count; lower the index or change the encounter.");
                }

                // HP override first — set max before current so a raised max never clamps
                // the requested current, mirroring the player-creature path.
                if (enemy.MaxHp is int requestedMaxHp)
                {
                    enemies[i].SetMaxHpInternal(requestedMaxHp);
                }

                if (enemy.CurrentHp is int requestedHp)
                {
                    enemies[i].SetCurrentHpInternal(requestedHp);
                }

                if (enemy.Powers.Count > 0
                    && await ApplyAuthoredPowerListAsync(request, $"run.currentRoom.combat.enemies[{i}].powers", enemy.Powers, enemies[i]) is { } enemyInvalid)
                {
                    return enemyInvalid;
                }
            }
        }

        return null;
    }

    private static async Task<FixtureLoadResult?> ApplyAuthoredPowerListAsync(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<FixturePowerRecipe> powers,
        Creature target)
    {
        for (var index = 0; index < powers.Count; index += 1)
        {
            var entry = powers[index];
            if (!Sts2ModelResolver.TryResolveFixturePower(entry.ModelId, out var power))
            {
                return InvalidFixture(
                    request,
                    $"{field}[{index}]",
                    entry.ModelId,
                    "Use a power id from the installed STS2 content (e.g. VULNERABLE_POWER, WEAK_POWER, FRAIL_POWER).");
            }

            // A fresh ToMutable(0) instance avoids mutating the shared catalog model;
            // the authored amount is applied as the offset. Self-applied (applier =
            // target) with no card source, silent (no fly-in VFX during load).
            await PowerCmd.Apply(new BlockingPlayerChoiceContext(), power.ToMutable(0), target, entry.Amount, target, null, silent: true);
        }

        return null;
    }

    // Record fixture-authored transient combat VFX (e.g. a cardUpgrade preview) so the
    // snapshot exposes them via Sts2StateProvider.ResolveCombatState. Resets unconditionally
    // so a prior fixture's effects never leak into this load; no game mutation, no failure.
    private static void ApplyAuthoredTransientEffects(FixtureDocument fixture, ILogStream? logStream = null)
    {
        Sts2AuthoredTransientEffectsRegistry.Reset();

        foreach (var effect in fixture.Room.TransientEffects)
        {
            if (string.IsNullOrWhiteSpace(effect.Id) || string.IsNullOrWhiteSpace(effect.Kind))
            {
                continue;
            }

            Sts2AuthoredTransientEffectsRegistry.Add(new Spirectl.Sts2.Core.State.StateCombatTransientEffectSnapshot(
                Id: effect.Id,
                AnchorCreatureId: effect.AnchorCreatureId ?? string.Empty,
                Kind: effect.Kind,
                Amount: effect.Amount,
                SpawnedAtMs: effect.SpawnedAtMs,
                CardModelId: effect.CardModelId,
                CardId: effect.CardId,
                SourceRelicModelId: effect.SourceRelicModelId,
                ScenePath: effect.ScenePath));
        }
    }

    private static async Task<FixtureLoadResult?> ApplyAuthoredEndedTurnsAsync(
        FixtureLoadRequestSnapshot request,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players,
        ILogStream logStream)
    {
        Sts2AuthoredEndedTurnRegistry.Reset();

        var endedPlayers = players.Where(player => player.EndedTurn).ToArray();
        if (endedPlayers.Length == 0)
        {
            return null;
        }

        // Wait until the play phase is fully live and the opening hand has been dealt, so
        // readiness sticks and the captured combat retains the (now locked) cards the
        // presentation renders. IsPlayPhase matters twice over: StartTurn clears the ready
        // set when the player side begins, and it fires TurnStarted (which resets the end
        // turn button to its default label/state) in the same synchronous block that sets
        // IsPlayPhase — so once we observe it, ending the turn won't be visually undone.
        for (var attempt = 0; attempt < 240; attempt += 1)
        {
            if (Sts2CombatFacts.IsAnyPlayerInPlayPhase()
                && CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player
                && endedPlayers.All(player => player.Player.PlayerCombatState?.Hand?.Cards is { Count: > 0 }))
            {
                break;
            }

            await Task.Delay(16);
        }

        var combatManager = CombatManager.Instance;
        var livePlayers = combatManager.DebugOnlyGetState()?.Players ?? [];
        var endedNetIds = endedPlayers.Select(player => player.NetId).ToHashSet();

        // If no live seat would be left playing (alive, not authored-ended, not already
        // ready), readying the authored seats would resolve the whole turn — exactly what
        // an authored mid-turn fixture must not do.
        var someSeatStillPlaying = livePlayers.Any(livePlayer =>
            !endedNetIds.Contains(livePlayer.NetId)
            && livePlayer.Creature?.IsDead != true
            && !combatManager.IsPlayerReadyToEndTurn(livePlayer));

        foreach (var player in endedPlayers)
        {
            var livePlayer = livePlayers.FirstOrDefault(candidate => candidate.NetId == player.NetId);
            if (Sts2EndTurnReadinessHooks.IsInstalled && someSeatStillPlaying && livePlayer is not null)
            {
                // The game's own end-turn entry point: fires PlayerEndedTurn (button ->
                // Cancelar, hand darkens, ready ticks) and disables actions for the local
                // seat. Never retry on failure — a retry loop against an unpatched
                // readiness check is what used to end the turn repeatedly until the
                // players died.
                PlayerCmd.EndTurn(livePlayer, canBackOut: true);
                if (combatManager.IsPlayerReadyToEndTurn(livePlayer))
                {
                    continue;
                }
            }

            logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.fixture",
                $"Falling back to the authored ended-turn registry for seat '{player.FixtureId}' "
                + $"(hookInstalled={Sts2EndTurnReadinessHooks.IsInstalled}, someSeatStillPlaying={someSeatStillPlaying}, "
                + $"livePlayerResolved={livePlayer is not null}); the live turn stays untouched.");
            Sts2AuthoredEndedTurnRegistry.Mark(player.NetId);
        }

        return null;
    }

    // Opens the in-game card pile viewer (NCardPileScreen) for the authored pile by
    // releasing the combat HUD pile button (NCombatCardPile), the same hook the
    // view-{draw,discard,exhaust}-pile actions drive. Lets fixtures author the open
    // pile screen so the presentation dev server can render the faithful dialog.
    private static async Task<FixtureLoadResult?> ApplyAuthoredCardPileUiAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var setup = fixture.Ui?.CardPile;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.capstone.cardPileView.playerId",
                setup.PlayerId,
                "Point ui.cardPile.playerId at one authored player from players[].id.");
        }

        var pileNodeName = setup.Pile?.Trim().ToLowerInvariant() switch
        {
            "draw" => "DrawPile",
            "discard" => "DiscardPile",
            "exhaust" => "ExhaustPile",
            _ => null,
        };
        if (pileNodeName is null)
        {
            return InvalidFixture(
                request,
                "run.view.capstone.cardPileView.pileType",
                setup.Pile ?? string.Empty,
                "Use draw, discard, or exhaust.");
        }

        var combatUi = Sts2LiveIntrospection.GetMemberValue(NCombatRoom.Instance, "Ui") as Node
            ?? NCombatRoom.Instance;
        if (combatUi is null)
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.cardPileView",
                "ui.cardPile requires a combat fixture with the combat HUD mounted.");
        }

        var control = Sts2TreeSearch.FindDescendants(
                combatUi,
                static node => node.GetChildren().OfType<Node>(),
                node => string.Equals(node.Name.ToString(), pileNodeName, StringComparison.Ordinal))
            .OfType<Control>()
            .FirstOrDefault();
        if (control is null)
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.cardPileView.pileType",
                $"Could not locate the combat {pileNodeName} control under the combat UI.");
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(control, "OnRelease"))
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.cardPileView.pileType",
                $"The combat {pileNodeName} control did not expose a callable OnRelease hook.");
        }

        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            await Task.Delay(16);
            var capstone = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "CapstoneContainer"),
                "CurrentCapstoneScreen");
            if (capstone is not null
                && Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen"))
            {
                return null;
            }
        }

        return RuntimeFailure(
            request,
            "run.view.capstone.cardPileView.pileType",
            "Timed out waiting for the card pile screen to open.");
    }

    // Opens the master deck viewer (NDeckViewScreen) for the authored deck view by
    // releasing the run top-bar Deck button, the same hook the toggle-deck action
    // drives, then applies the authored sort keys and upgrade preview toggle so the
    // presentation dev server can render the faithful deck dialog.
    private static async Task<FixtureLoadResult?> ApplyAuthoredDeckViewUiAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var setup = fixture.Ui?.DeckView;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.capstone.deckView.playerId",
                setup.PlayerId,
                "Point ui.deckView.playerId at one authored player from players[].id.");
        }

        var topBar = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "TopBar") as Node;
        if (topBar is null)
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.deckView",
                "ui.deckView requires a run fixture with the run top bar mounted.");
        }

        var deckButton = Sts2LiveIntrospection.GetMemberValue(topBar, "Deck") as Control
            ?? ResolveDescendantControl(topBar, "Deck");
        if (deckButton is null)
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.deckView",
                "Could not locate the run top-bar Deck button.");
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(deckButton, "OnRelease"))
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.deckView",
                "The run top-bar Deck button did not expose a callable OnRelease hook.");
        }

        Node? deckViewScreen = null;
        for (var attempt = 0; attempt < 30 && deckViewScreen is null; attempt += 1)
        {
            await Task.Delay(16);
            var capstone = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "CapstoneContainer"),
                "CurrentCapstoneScreen");
            if (capstone is Node screen
                && Sts2LiveIntrospection.IsType(capstone, "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen"))
            {
                deckViewScreen = screen;
            }
        }

        if (deckViewScreen is null)
        {
            return RuntimeFailure(
                request,
                "run.view.capstone.deckView",
                "Timed out waiting for the deck view screen to open.");
        }

        foreach (var sort in setup.Sort)
        {
            var (memberName, fallbackNodeName) = sort.By?.Trim().ToLowerInvariant() switch
            {
                "obtained" => ("_obtainedSorter", "ObtainedSorter"),
                "type" => ("_typeSorter", "CardTypeSorter"),
                "cost" => ("_costSorter", "CostSorter"),
                "alphabet" => ("_alphabetSorter", "AlphabeticalSorter"),
                _ => (null, null),
            };
            if (memberName is null || fallbackNodeName is null)
            {
                return InvalidFixture(
                    request,
                    "run.view.capstone.deckView.sort[].by",
                    sort.By ?? string.Empty,
                    "Use obtained, type, cost, or alphabet.");
            }

            var sorter = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, memberName) as Control
                ?? ResolveDescendantControl(deckViewScreen, fallbackNodeName);
            if (sorter is null)
            {
                return RuntimeFailure(
                    request,
                    "run.view.capstone.deckView.sort[].by",
                    $"Could not locate the deck view {sort.By} sorter control.");
            }

            // Pressing a sorter selects its key (ascending). Re-pressing the active
            // sorter toggles the direction, so a second release reaches descending.
            var presses = string.Equals(sort.Direction?.Trim(), "descending", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            for (var press = 0; press < presses; press += 1)
            {
                if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(sorter, "OnRelease"))
                {
                    return RuntimeFailure(
                        request,
                        "run.view.capstone.deckView.sort[].by",
                        $"The deck view {sort.By} sorter did not expose a callable OnRelease hook.");
                }

                await Task.Delay(16);
            }
        }

        if (setup.ShowUpgrades)
        {
            var upgrades = Sts2LiveIntrospection.GetMemberValue(deckViewScreen, "_showUpgrades") as Control
                ?? ResolveDescendantControl(deckViewScreen, "Upgrades");
            if (upgrades is null)
            {
                return RuntimeFailure(
                    request,
                    "run.view.capstone.deckView.showUpgrades",
                    "Could not locate the deck view upgrades toggle control.");
            }

            if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(upgrades, "OnRelease"))
            {
                return RuntimeFailure(
                    request,
                    "run.view.capstone.deckView.showUpgrades",
                    "The deck view upgrades toggle did not expose a callable OnRelease hook.");
            }

            await Task.Delay(16);
        }

        return null;
    }

    private static Control? ResolveDescendantControl(Node root, string nodeName)
        => Sts2TreeSearch.FindDescendants(
                root,
                static node => node.GetChildren().OfType<Node>(),
                node => string.Equals(node.Name.ToString(), nodeName, StringComparison.Ordinal))
            .OfType<Control>()
            .FirstOrDefault();

    // Opens the relic-details overlay (NInspectRelicScreen) for the authored
    // relic via the same path the inspect-relic action drives (the relic bar's
    // browse list + NGame.GetInspectRelicScreen().Open), so fixtures can render
    // the open overlay faithfully.
    private static async Task<FixtureLoadResult?> ApplyAuthoredInspectRelicUiAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        IReadOnlyList<HostLocalFixturePlayerRecipe> players)
    {
        var setup = fixture.Ui?.InspectRelic;
        if (setup is null)
        {
            return null;
        }

        var player = players.FirstOrDefault(player => string.Equals(player.FixtureId, setup.PlayerId, StringComparison.Ordinal));
        if (player is null)
        {
            return InvalidFixture(
                request,
                "run.view.inspectRelic.playerId",
                setup.PlayerId,
                "The inspect-relic overlay belongs to the view player from players[].id.");
        }

        var inventory = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
            "RelicInventory") as MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory;
        if (inventory is null || NGame.Instance is null)
        {
            return RuntimeFailure(
                request,
                "run.view.inspectRelic",
                "ui.inspectRelic requires a run fixture with the relic bar mounted.");
        }

        // The relic-bar holders mount a few frames after the room enters.
        var relics = new List<RelicModel>();
        RelicModel? target = null;
        for (var attempt = 0; attempt < 60 && target is null; attempt += 1)
        {
            relics.Clear();
            foreach (var holder in inventory.RelicNodes)
            {
                var model = holder?.Relic?.Model;
                if (model is null)
                {
                    continue;
                }

                relics.Add(model);
                if (target is null
                    && string.Equals(model.Id.Entry, setup.RelicModelId, StringComparison.OrdinalIgnoreCase))
                {
                    target = model;
                }
            }

            if (target is null)
            {
                await Task.Delay(16);
            }
        }

        if (target is null)
        {
            return RuntimeFailure(
                request,
                "run.view.inspectRelic.relicModelId",
                $"No relic-bar relic matched '{setup.RelicModelId}'.");
        }

        NGame.Instance.GetInspectRelicScreen().Open(relics, target);

        for (var attempt = 0; attempt < 30; attempt += 1)
        {
            if (NGame.Instance.InspectRelicScreen is { Visible: true })
            {
                return null;
            }

            await Task.Delay(16);
        }

        return RuntimeFailure(
            request,
            "run.view.inspectRelic",
            "Timed out waiting for the relic-details overlay to open.");
    }

    private static NPotionHolder? ResolveTopBarPotionHolder(int slotIndex, PotionModel potion)
    {
        var holders = Sts2LiveIntrospection.GetMemberValue(
            Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(
                    Sts2LiveIntrospection.GetMemberValue(NRun.Instance, "GlobalUi"),
                    "TopBar"),
                "PotionContainer"),
            "_holders");
        var holder = EnumerateFixtureCollection(holders).ElementAtOrDefault(slotIndex) as NPotionHolder;
        if (holder?.Potion?.Model == potion)
        {
            return holder;
        }

        return EnumerateFixtureCollection(holders)
            .OfType<NPotionHolder>()
            .FirstOrDefault(candidate => candidate.Potion?.Model == potion);
    }

    private static void StartPotionTargeting(NPotionHolder holder, PotionModel potion)
    {
        RunManager.Instance?.HoveredModelTracker.OnLocalPotionSelected(potion);
        var startPosition = holder.GlobalPosition + Vector2.Right * holder.Size.X * 0.5f + Vector2.Down * 50f;
        Sts2TargetManagerAccess.InstanceOrNull?.StartTargeting(
            potion.TargetType,
            startPosition,
            TargetMode.ClickMouseToTarget,
            () => holder.Potion is null || !ReferenceEquals(holder.Potion.Model, potion),
            null);
    }

    private static IEnumerable<object?> EnumerateFixtureCollection(object? value)
        => value is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object?>()
            : [];

    private static bool FixturePotionNeedsTarget(object potion)
    {
        var targetType = Sts2LiveIntrospection.GetMemberValue(potion, "TargetType")?.ToString();
        return targetType is "Self" or "AnyEnemy" or "AnyPlayer" or "AnyAlly" or "TargetedNoCreature";
    }

    private static bool FixturePotionCanBeUsedManually(object potion)
    {
        var usage = Sts2LiveIntrospection.GetMemberValue(potion, "Usage")?.ToString();
        return !string.Equals(usage, "Automatic", StringComparison.Ordinal);
    }

    private static bool ToFixtureBool(object? value)
        => value switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };

    private static void PrepareRestoreMapPointHistory(
        RunState runState,
        MapPointType pointType,
        RoomType roomType,
        ModelId? roomModelId,
        int actFloor)
    {
        // Mirror PrepareAncientEventRestoreMapPoint: seed the current (starting)
        // map point's type and mark it visited so NTopBarRoomIcon.UpdateIcon
        // resolves the room icon from CurrentMapPoint.PointType. Without this the
        // icon stays on its authored placeholder because the fixture never
        // navigates a generated map. Used by the combat path and the roomless
        // room loaders (rest-site/shop/treasure), which otherwise leave the icon
        // blank.
        var startingMapPoint = runState.Map.StartingMapPoint;
        startingMapPoint.PointType = pointType;
        runState.AddVisitedMapCoord(startingMapPoint.coord);
        runState.ActFloor = actFloor;
        runState.AppendToMapPointHistory(pointType, roomType, roomModelId);
    }

    // Like PrepareRestoreMapPointHistory, but seeds an ARBITRARY map coord (not the start node) as the
    // current map point. Boss fixtures use this to enter on the act's terminal boss node so the
    // post-boss rewards screen sees CurrentMapCoord == the boss point and votes to advance the act.
    private static void PrepareRestoreMapPointHistoryAt(
        RunState runState,
        MapCoord coord,
        MapPointType pointType,
        RoomType roomType,
        ModelId? roomModelId,
        int actFloor)
    {
        if (runState.Map.GetPoint(coord) is { } point)
        {
            point.PointType = pointType;
        }

        runState.AddVisitedMapCoord(coord);
        runState.ActFloor = actFloor;
        runState.AppendToMapPointHistory(pointType, roomType, roomModelId);
    }

    // Auto-walks a deterministic, connected path from the start node up to travelToRow, recording each
    // node as visited (start → … → deepest reached). This populates run.visitedMapCoords with a real
    // walked route so the traveled-path coloring renders, and lands CurrentMapCoord (== last visited)
    // mid-map. MapPoint.Children is a HashSet (unordered), so each step is chosen deterministically —
    // the child whose column is closest to the current node (ties: column, then row) — for a stable,
    // roughly-central path across reloads. Returns an InvalidFixture result if the depth is unreachable.
    private static FixtureLoadResult? ApplyAuthoredMapProgression(
        FixtureLoadRequestSnapshot request,
        RunState runState,
        MapPointType startPointType,
        RoomType startRoomType,
        int travelToRow)
    {
        var current = runState.Map.StartingMapPoint;

        // Seed the start node (row 0) as the first visited coord, with the authored start kind.
        PrepareRestoreMapPointHistoryAt(
            runState,
            current.coord,
            startPointType,
            startRoomType,
            roomModelId: null,
            actFloor: current.coord.row);

        var guard = 0;
        while (current.coord.row < travelToRow)
        {
            guard++;
            var next = ChooseDeterministicChild(current);
            if (guard > 1000 || next is null)
            {
                break;
            }

            PrepareRestoreMapPointHistoryAt(
                runState,
                next.coord,
                next.PointType,
                ResolveMapPointRoomType(next.PointType),
                roomModelId: null,
                actFloor: next.coord.row);
            current = next;
        }

        if (current.coord.row < travelToRow)
        {
            return InvalidFixture(
                request,
                "run.currentRoom.mapRoom.travelToRow",
                travelToRow.ToString(CultureInfo.InvariantCulture),
                $"Could not walk a connected map path to row {travelToRow}; reached row {current.coord.row}.");
        }

        return null;
    }

    // Like ApplyAuthoredMapProgression, but STAMPS each visited node with an authored per-row type
    // (travelPath[i] = the node reached at step i, row i) instead of keeping the generated type — so a
    // fixture can showcase every node kind, including revealed `?` nodes, along the walked route. Walks
    // the same deterministic column-closest path; travelPath[0] is the start (row-0) node.
    private static FixtureLoadResult? ApplyAuthoredMapTravelPath(
        FixtureLoadRequestSnapshot request,
        RunState runState,
        IReadOnlyList<string> travelPath)
    {
        var current = runState.Map.StartingMapPoint;
        for (var step = 0; step < travelPath.Count; step++)
        {
            if (step > 0)
            {
                var next = ChooseDeterministicChild(current);
                if (next is null)
                {
                    return InvalidFixture(
                        request,
                        "run.currentRoom.mapRoom.travelPath",
                        travelPath.Count.ToString(CultureInfo.InvariantCulture),
                        $"Could not walk a connected map path {travelPath.Count} nodes deep; dead end at row {current.coord.row}.");
                }

                current = next;
            }

            if (!TryParseTravelPathEntry(travelPath[step], out var pointType, out var roomType))
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.mapRoom.travelPath",
                    travelPath[step],
                    $"Invalid travelPath entry '{travelPath[step]}'. Use a MapPointType name (Monster/Elite/Shop/Treasure/RestSite/Unknown/Ancient) or 'Unknown:<RoomType>'.");
            }

            PrepareRestoreMapPointHistoryAt(
                runState,
                current.coord,
                pointType,
                roomType,
                roomModelId: null,
                actFloor: current.coord.row);
        }

        return null;
    }

    // Parses a travelPath entry: "Monster" -> (Monster, Monster); "Unknown:Event" -> (Unknown, Event);
    // a bare "Unknown" -> (Unknown, <RoomType matching the name, else Monster>). The MapPointType keeps
    // the node's icon kind (Unknown stays a `?`), while the RoomType is recorded in the visited history
    // so the state projection can surface a revealed `?` node's category (revealedRoomType). Returns
    // false for an unparseable MapPointType.
    private static bool TryParseTravelPathEntry(string entry, out MapPointType pointType, out RoomType roomType)
    {
        pointType = MapPointType.Monster;
        roomType = RoomType.Monster;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var parts = entry.Split(':', 2);
        if (!Enum.TryParse(parts[0].Trim(), ignoreCase: true, out pointType))
        {
            return false;
        }

        roomType = parts.Length == 2 && Enum.TryParse<RoomType>(parts[1].Trim(), ignoreCase: true, out var explicitRoomType)
            ? explicitRoomType
            : ResolveMapPointRoomType(pointType);
        return true;
    }

    // Deterministic next step up the map: the child column-closest to the current node (HashSet order
    // is otherwise unstable). Returns null at a dead end (no children).
    private static MapPoint? ChooseDeterministicChild(MapPoint point) =>
        point.Children
            .OrderBy(child => Math.Abs(child.coord.col - point.coord.col))
            .ThenBy(child => child.coord.col)
            .ThenBy(child => child.coord.row)
            .FirstOrDefault();

    // Maps a map-point type to the matching room type for the visited-history record (the enum member
    // names align; anything unexpected — e.g. Unknown — records as a normal monster room).
    private static RoomType ResolveMapPointRoomType(MapPointType pointType) =>
        Enum.TryParse<RoomType>(pointType.ToString(), out var roomType)
            ? roomType
            : RoomType.Monster;

    private static bool IsBossCombatFixture(FixtureDocument fixture)
        => !string.IsNullOrWhiteSpace(fixture.Room.EncounterId)
           && Sts2ModelResolver.TryResolveFixtureEncounter(fixture.Room.EncounterId, out var encounter)
           && encounter.RoomType == RoomType.Boss;

    // For boss combat fixtures, move the run to the boss's real act index BEFORE its act assets load,
    // the map generates, and the room is built. Beating the boss then routes through the normal act
    // transition (next act, or — for the last act — the THE_ARCHITECT finale). This MUST happen during
    // run-state creation, not after the combat room exists: changing the act index later disposes the
    // just-loaded act assets out from under the live combat (NullReferenceException in
    // CombatManager.StartCombatInternal). Non-boss fixtures keep the legacy index-0 placement, so their
    // enemy scaling / render snapshots are unchanged. fixture.Run.Act is the boss's act (Sts2FixtureLoader
    // auto-fills it from the encounter), so its 0-based index is fixture.Run.Act - 1.
    private static void PositionBossActIndex(RunState runState, FixtureDocument fixture)
    {
        if (!IsBossCombatFixture(fixture))
        {
            return;
        }

        var bossActIndex = fixture.Run.Act - 1;
        if (bossActIndex >= 0 && bossActIndex < runState.Acts.Count)
        {
            runState.CurrentActIndex = bossActIndex;
        }
    }

    // Finds which act's boss pool contains the given encounter (e.g. DOORMAKER_BOSS -> Glory, index 2).
    // The returned index matches RunState.CurrentActIndex because both enumerate the acts in the same
    // order (the 3-act default list, via CreateActsForRunStateCreation).
    private static bool TryResolveBossActIndex(EncounterModel encounter, out int actIndex)
    {
        var acts = Sts2ModelResolver.CreateActsForRunStateCreation();
        for (var i = 0; i < acts.Count; i++)
        {
            if (acts[i].AllBossEncounters.Any(candidate => candidate.Id.Entry == encounter.Id.Entry))
            {
                actIndex = i;
                return true;
            }
        }

        actIndex = -1;
        return false;
    }

    // Maps a combat encounter's room type to the matching map-point type so the
    // top-bar room icon shows the correct icon (monster/elite/boss). The enum
    // member names align; anything unexpected falls back to a normal monster.
    private static MapPointType ResolveCombatMapPointType(RoomType roomType) =>
        Enum.TryParse<MapPointType>(roomType.ToString(), out var mapPointType)
            ? mapPointType
            : MapPointType.Monster;

    private static void PrepareAncientEventRestoreMapPoint(
        RunState runState,
        EventModel eventModel,
        int actFloor)
    {
        var startingMapPoint = runState.Map.StartingMapPoint;
        startingMapPoint.PointType = MapPointType.Ancient;
        runState.AddVisitedMapCoord(startingMapPoint.coord);
        runState.ActFloor = actFloor;
        runState.AppendToMapPointHistory(MapPointType.Ancient, RoomType.Event, eventModel.Id);
    }

    // NGame (and its lazily-created inspection screens) persists across fixture
    // loads; an inspect-relic overlay left open by a previous fixture would bleed
    // into the new run's state/capture. Hide it instantly (Close() only animates
    // out over ~0.35s, racing the snapshot) before standing up the new run.
    private static void CloseLeftoverInspectRelicOverlay()
    {
        if (NGame.Instance?.InspectRelicScreen is { Visible: true } leftover)
        {
            Sts2LiveIntrospection.TryInvokeMethod(leftover, "Close");
            leftover.Visible = false;
        }
    }

    private async Task<(SinglePlayerFixtureContext? Context, FixtureLoadResult? Invalid)> CreateSinglePlayerRunContextAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture)
    {
        if (!TryCreateSinglePlayerRunState(request, fixture, out var runManager, out var runState, out var player, out var invalid))
        {
            return (null, invalid);
        }

        CloseLeftoverInspectRelicOverlay();
        Sts2HostLocalSeatRegistry.ReplaceHostLocalSeats([1uL]);
        runManager!.SetUpNewSingleplayer(runState!, shouldSave: false, dailyTime: null);
        await PreloadManager.LoadRunAssets(runState!.Players.Select(playerModel => playerModel.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        runManager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        await runManager.GenerateMap();

        return (new SinglePlayerFixtureContext(runManager, runState, player!), null);
    }

    private async Task<(HostLocalFixtureContext? Context, FixtureLoadResult? Invalid)> CreateHostLocalRunContextAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenLabel)
    {
        if (!TryCreateHostLocalRunState(request, fixture, screenLabel, out var runManager, out var runState, out var players, out var invalid))
        {
            return (null, invalid);
        }

        CloseLeftoverInspectRelicOverlay();
        Sts2HostLocalSeatRegistry.ReplaceHostLocalSeats(players!.Where(player => player.IsHostLocalSeat).Select(player => player.NetId));
        runManager!.SetUpNewSingleplayer(runState!, shouldSave: false, dailyTime: null);
        await PreloadManager.LoadRunAssets(runState!.Players.Select(playerModel => playerModel.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        runManager.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        await runManager.GenerateMap();

        return (new HostLocalFixtureContext(runManager, runState, players!), null);
    }

    private async Task<(SinglePlayerFixtureContext? Context, FixtureLoadResult? Invalid)> ResolveRoomlessSinglePlayerContextAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenType)
    {
        if (!string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
        {
            return (null, InvalidFixture(
                request,
                "run.currentRoom.combat",
                fixture.Room.EncounterId,
                $"Remove room; {screenType} fixtures do not use room.encounterId."));
        }

        if (fixture.Lobby is not null)
        {
            return (null, InvalidFixture(
                request,
                "characterSelect",
                fixture.Lobby.Kind,
                $"Remove lobby; {screenType} fixtures do not use the lobby recipe section."));
        }

        return await CreateSinglePlayerRunContextAsync(request, fixture);
    }

    private bool TryCreateSinglePlayerRunState(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        out RunManager? runManager,
        out RunState? runState,
        out Player? player,
        out FixtureLoadResult? invalid)
    {
        runManager = null;
        runState = null;
        player = null;
        invalid = null;

        if (fixture.Players.Count != 1)
        {
            invalid = InvalidFixture(
                request,
                "players",
                fixture.Players.Count.ToString(),
                "The live fixture loader currently supports exactly one local player.");
            return false;
        }

        if (!Sts2ModelResolver.TryResolveFixtureAct(fixture.Run.Act, out var act))
        {
            invalid = InvalidFixture(
                request,
                "run.currentActIndex",
                fixture.Run.Act.ToString(),
                "Use an act number exposed by the installed STS2 content.");
            return false;
        }

        if (!Sts2ModelResolver.TryResolveFixtureCharacter(fixture.Players[0].Character, out var character))
        {
            invalid = InvalidFixture(
                request,
                "players[0].character",
                fixture.Players[0].Character,
                "Use a character id from the installed STS2 content, such as IRONCLAD.");
            return false;
        }

        if (fixture.Players[0].MaxHp is int maxHp && fixture.Players[0].Hp is int hp && hp > maxHp)
        {
            invalid = InvalidFixture(
                request,
                "players[0].hp",
                hp.ToString(),
                "hp cannot exceed maxHp in the live fixture loader.");
            return false;
        }

        runManager = RunManager.Instance;
        if (runManager is null)
        {
            invalid = RuntimeFailure(request, "run_manager", "RunManager.Instance was null.");
            return false;
        }

        if (runManager.IsInProgress)
        {
            runManager.CleanUp(graceful: false);
        }

        player = Player.CreateForNewRun(character, UnlockState.all, netId: 1uL);
        if (fixture.Players[0].MaxHp is int requestedMaxHp)
        {
            player.Creature.SetMaxHpInternal(requestedMaxHp);
        }

        if (fixture.Players[0].Hp is int requestedHp)
        {
            player.Creature.SetCurrentHpInternal(requestedHp);
        }

        runState = RunState.CreateForTest(
            [player],
            Sts2ModelResolver.CreateActsForRunStateCreation(),
            modifiers: [],
            ascensionLevel: fixture.Run.AscensionLevel ?? 0,
            seed: fixture.Run.Seed);
        PositionBossActIndex(runState, fixture);
        runState.SetActDebug(act);

        if (NGame.Instance is null)
        {
            invalid = RuntimeFailure(
                request,
                "game",
                "NGame.Instance was null while preparing the live fixture load.");
            return false;
        }

        return true;
    }

    private bool TryCreateHostLocalRunState(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenLabel,
        out RunManager? runManager,
        out RunState? runState,
        out IReadOnlyList<HostLocalFixturePlayerRecipe>? players,
        out FixtureLoadResult? invalid)
    {
        runManager = null;
        runState = null;
        players = null;
        invalid = null;

        if (!Sts2ModelResolver.TryResolveFixtureAct(fixture.Run.Act, out var act))
        {
            invalid = InvalidFixture(
                request,
                "run.currentActIndex",
                fixture.Run.Act.ToString(),
                "Use an act number exposed by the installed STS2 content.");
            return false;
        }

        if (!TryResolveHostLocalPlayers(request, fixture, screenLabel, out players, out invalid))
        {
            return false;
        }

        runManager = RunManager.Instance;
        if (runManager is null)
        {
            invalid = RuntimeFailure(request, "run_manager", "RunManager.Instance was null.");
            return false;
        }

        if (runManager.IsInProgress)
        {
            runManager.CleanUp(graceful: false);
        }

        runState = RunState.CreateForTest(
            players!.Select(player => player.Player).ToArray(),
            Sts2ModelResolver.CreateActsForRunStateCreation(),
            modifiers: [],
            ascensionLevel: fixture.Run.AscensionLevel ?? 0,
            seed: fixture.Run.Seed);
        PositionBossActIndex(runState, fixture);
        runState.SetActDebug(act);

        if (NGame.Instance is null)
        {
            invalid = RuntimeFailure(
                request,
                "game",
                "NGame.Instance was null while preparing the live fixture load.");
            return false;
        }

        return true;
    }

    private static async Task<NMainMenu?> EnsureMainMenu(bool forceCurrentScene = false)
    {
        try
        {
            var existingMainMenu = NGame.Instance?.MainMenu;
            if (existingMainMenu is not null
                && IsUsableGodotObject(existingMainMenu)
                && HasReusableMainMenuSubmenuStack(existingMainMenu))
            {
                if (forceCurrentScene)
                {
                    NGame.Instance?.RootSceneContainer.SetCurrentScene(existingMainMenu);
                    await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
                    await WaitForStableScreenId("main-menu");
                }

                return existingMainMenu;
            }

            return await CreateFreshMainMenu(forceCurrentScene);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<NMainMenu?> CreateFreshMainMenu(bool forceCurrentScene)
    {
        await PreloadManager.LoadCommonAndMainMenuAssets();
        var mainMenu = NMainMenu.Create(openTimeline: false);
        NGame.Instance?.RootSceneContainer.SetCurrentScene(mainMenu);
        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        if (forceCurrentScene)
        {
            await WaitForStableScreenId("main-menu");
        }

        return NGame.Instance?.MainMenu;
    }

    private bool TryClearSubmenuStack(NMainMenuSubmenuStack submenuStack, string fixtureName)
    {
        try
        {
            while (submenuStack.SubmenusOpen)
            {
                submenuStack.Pop();
            }

            return true;
        }
        catch (ObjectDisposedException ex)
        {
            _logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.fixture",
                $"Load fixture '{fixtureName}' discarded a stale main-menu submenu stack after fastmp startup disposed a submenu: {ex.ObjectName}");
            return false;
        }
    }

    private void ClearFixtureLobbyOverlays(string fixtureName)
    {
        try
        {
            var game = NGame.Instance;
            var mainMenu = game?.MainMenu;
            if (mainMenu is not null && IsUsableGodotObject(mainMenu))
            {
                mainMenu.DisableBackstopInstantly();
                if (GameApiMainMenu.BlurBackstop(mainMenu) is CanvasItem backstop && IsUsableGodotObject(backstop))
                {
                    backstop.Visible = false;
                }
            }

            if (game?.Transition is CanvasItem transition && IsUsableGodotObject(transition))
            {
                transition.Visible = false;
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logStream.Write(
                BridgeLogLevel.Warn,
                "bridge.fixture",
                $"Load fixture '{fixtureName}' could not clear a stale main-menu lobby overlay: {ex.ObjectName}");
        }
    }

    internal static bool HasReusableMainMenuSubmenuStack(object? mainMenu)
        => Sts2LiveIntrospection.GetMemberValue(mainMenu, "SubmenuStack") is not null;

    private static async Task<bool> WaitForStableScreenId(string screenType)
    {
        const int maxAttempts = 30;
        const int requiredStableAttempts = 4;
        var stableAttempts = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            var currentScreen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            if (IsStableScreenIdCandidate(currentScreen, screenType))
            {
                stableAttempts += 1;
                if (stableAttempts >= requiredStableAttempts)
                {
                    return true;
                }
            }
            else
            {
                stableAttempts = 0;
            }

            await AwaitFixtureRenderFramesAsync(1);
        }

        return false;
    }

    internal static bool IsStableScreenIdCandidate(object? currentScreen, string screenType)
        => IsUsableGodotObject(currentScreen)
            && Sts2SupportedScreenIds.TryResolve(currentScreen, out var match)
            && string.Equals(match?.ScreenType, screenType, StringComparison.Ordinal);

    internal static async Task AwaitFixtureRenderFramesAsync(int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        var game = NGame.Instance;
        var tree = game?.GetTree();
        if (game is null || tree is null)
        {
            for (var index = 0; index < frameCount; index += 1)
            {
                await Task.Delay(50);
            }

            return;
        }

        for (var index = 0; index < frameCount; index += 1)
        {
            if (!await AwaitFixtureProcessFrameAsync(game, tree))
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<bool> AwaitFixtureProcessFrameAsync(NGame game, SceneTree tree)
    {
        try
        {
            var frame = AwaitFixtureProcessFrameSignalAsync(game, tree);
            var completed = await Task.WhenAny(frame, Task.Delay(FixtureRenderFrameTimeout));
            if (completed != frame)
            {
                return false;
            }

            await frame;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task AwaitFixtureProcessFrameSignalAsync(NGame game, SceneTree tree)
        => await game.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

    internal static bool IsUsableGodotObject(object? value)
    {
        if (value is not GodotObject godotObject)
        {
            return true;
        }

        try
        {
            return GodotObject.IsInstanceValid(godotObject)
                && !godotObject.IsQueuedForDeletion();
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureUsableGodotObject(object? value, string objectName)
    {
        if (!IsUsableGodotObject(value))
        {
            throw new ObjectDisposedException(objectName);
        }
    }

    internal static ObjectDisposedException? GetStaleGodotObjectException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is ObjectDisposedException disposedException)
            {
                return disposedException;
            }

            if (current is TargetInvocationException { InnerException: null })
            {
                break;
            }
        }

        return null;
    }

    internal static bool ShouldRetryStaleLobbyMaterialization(Exception ex, int attempt, int maxAttempts)
        => attempt + 1 < maxAttempts
            && GetStaleGodotObjectException(ex) is not null;

    internal static bool IsCommandLineFastMpHostValue(string? value)
        => string.Equals(value, "host", StringComparison.Ordinal)
            || string.Equals(value, "host_standard", StringComparison.Ordinal)
            || string.Equals(value, "host_daily", StringComparison.Ordinal)
            || string.Equals(value, "host_custom", StringComparison.Ordinal);

    private async Task WaitForCommandLineFastMpHostToSettle(string fixtureName)
    {
        if (_commandLineFastMpHostSettled
            || !CommandLineHelper.TryGetValue("fastmp", out var fastMpValue)
            || !IsCommandLineFastMpHostValue(fastMpValue))
        {
            return;
        }

        const int maxAttempts = 50;
        const int delayMs = 100;
        const int minimumMainMenuGraceAttempts = 25;

        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            var currentScreen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(currentScreen)
                || Sts2SupportedScreenIds.IsLoadRunLobbyScreen(currentScreen))
            {
                _commandLineFastMpHostSettled = true;
                return;
            }

            var submenuStack = NGame.Instance?.MainMenu?.SubmenuStack;
            var topSubmenu = submenuStack?.Peek();
            if (topSubmenu is not NMultiplayerHostSubmenu
                && (submenuStack is null || !submenuStack.SubmenusOpen)
                && attempt >= minimumMainMenuGraceAttempts)
            {
                break;
            }

            await AwaitFixtureRenderFramesAsync(Math.Max(1, delayMs / 50));
        }

        _commandLineFastMpHostSettled = true;
        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.fixture",
            $"Load fixture '{fixtureName}' continued after waiting for command-line fastmp host startup to settle.");
    }

    private void RetainActiveLobbyHost(NetHostGameService netService)
    {
        var previousHost = _activeLobbyHost;
        if (!ReferenceEquals(previousHost, netService))
        {
            _activeLobbyHost = netService;
            if (previousHost is not null)
            {
                Sts2FixtureLobbyProgressScope.Clear();
                TryDisconnectLobbyHost(previousHost, "previous-fixture");
            }
        }

        GC.KeepAlive(_activeLobbyHost);
    }

    private void ReleaseActiveLobbyHost()
    {
        var host = _activeLobbyHost;
        _activeLobbyHost = null;
        Sts2FixtureLobbyProgressScope.Clear();
        if (host is not null)
        {
            TryDisconnectLobbyHost(host, "previous-fixture");
        }
    }

    private void ResetLobbyFixtureLifecycle(string fixtureName)
    {
        ReleaseActiveLobbyHost();
        CleanUpActiveLobbyScreen(fixtureName);
    }

    private void CleanUpActiveLobbyScreen(string fixtureName)
    {
        var screen = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (screen is null)
        {
            return;
        }

        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screen)
            || Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screen)
            || Sts2LiveIntrospection.GetMemberValue(screen, "Lobby") is not null)
        {
            try
            {
                Sts2LiveIntrospection.TryInvokeMethod(screen, "CleanUpLobby", true);
            }
            catch (Exception ex)
            {
                _logStream.Write(
                    BridgeLogLevel.Error,
                    "bridge.fixture",
                    $"Load fixture '{fixtureName}' failed to clean up the active lobby screen before fixture materialization: {ex}");
            }
        }
    }

    private void TryDisconnectLobbyHost(NetHostGameService netService, string fixtureName)
    {
        try
        {
            netService.Disconnect(NetError.Quit, now: true);
        }
        catch (Exception disconnectEx)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.fixture",
                $"Load fixture '{fixtureName}' failed to disconnect a fixture lobby host: {disconnectEx}");
        }
    }

    private void TryDisconnectFailedStartRunHost(NetHostGameService netService, string fixtureName)
    {
        try
        {
            netService.Disconnect(NetError.Quit, now: true);
        }
        catch (Exception disconnectEx)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.fixture",
                $"Load fixture '{fixtureName}' failed to disconnect a failed start-run host: {disconnectEx}");
        }
    }

    private void CleanupPartiallyInitializedStartRunHost(NetHostGameService netService, string fixtureName)
    {
        if (ReferenceEquals(_activeLobbyHost, netService))
        {
            _activeLobbyHost = null;
        }

        Sts2FixtureLobbyProgressScope.Clear();
        TryDisconnectLobbyHost(netService, fixtureName);
    }

    private static void ApplyStartRunLobbyRecipe(
        NCharacterSelectScreen screen,
        LobbyFixtureRecipe recipe)
    {
        var lobby = screen.Lobby;
        var localIndex = lobby.Players.FindIndex(player => player.id == recipe.LocalPlayer.NetId);
        if (localIndex < 0)
        {
            throw new InvalidOperationException("The initialized character-select screen did not expose the expected local lobby player.");
        }

        var localPlayer = lobby.Players[localIndex];
        localPlayer.slotId = recipe.LocalPlayer.SlotId;
        localPlayer.character = recipe.LocalPlayer.Character;
        localPlayer.isReady = recipe.LocalPlayer.IsReady;
        lobby.Players[localIndex] = localPlayer;

        foreach (var remotePlayer in recipe.RemotePlayers)
        {
            lobby.Players.Add(new GameLobbyPlayer
            {
                id = remotePlayer.NetId,
                slotId = remotePlayer.SlotId,
                character = remotePlayer.Character,
                unlockState = localPlayer.unlockState,
                maxMultiplayerAscensionUnlocked = localPlayer.maxMultiplayerAscensionUnlocked,
                isReady = remotePlayer.IsReady,
            });
        }
    }

    private static void ApplyFixtureLobbyCharacterLocks(
        NCharacterSelectScreen screen,
        IReadOnlySet<string> lockedCharacterIds)
    {
        EnsureUsableGodotObject(screen, "character-select screen");
        if (Sts2LiveIntrospection.GetMemberValue(screen, "_charButtonContainer") is not Godot.Node buttonContainer)
        {
            throw new InvalidOperationException("The live character-select screen did not expose its button container.");
        }
        EnsureUsableGodotObject(buttonContainer, "character button container");

        var fixtureUnlockedCharacterIds = ModelDb.AllCharacters
            .Where(character => !lockedCharacterIds.Contains(character.Id.Entry))
            .Select(character => character.Id.Entry)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var child in buttonContainer.GetChildren().OfType<Godot.Node>())
        {
            EnsureUsableGodotObject(child, "character select button");
            if (!Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
            {
                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "IsRandom") is bool random && random)
            {
                if (lockedCharacterIds.Count > 0)
                {
                    HideRandomCharacterButton(child);
                }
                else
                {
                    UnlockCharacterSelectButton(child, "RANDOM_CHARACTER");
                }

                continue;
            }

            if (Sts2LiveIntrospection.GetMemberValue(child, "Character") is not CharacterModel character)
            {
                continue;
            }

            if (lockedCharacterIds.Contains(character.Id.Entry))
            {
                ApplyFixtureLockedCharacterSelectButton(child, character);
            }
            else
            {
                UnlockCharacterSelectButton(child, character.Id.Entry);
            }
        }

        if (lockedCharacterIds.Count > 0)
        {
            Sts2FixtureLobbyProgressScope.Apply(fixtureUnlockedCharacterIds);
        }
        else
        {
            Sts2FixtureLobbyProgressScope.Clear();
        }
    }

    private static void HideRandomCharacterButton(Godot.Node button)
    {
        EnsureUsableGodotObject(button, "random character select button");
        if (button is Godot.CanvasItem canvasItem)
        {
            canvasItem.Visible = false;
        }

        Sts2LiveIntrospection.TryInvokeMethod(button, "Disable");
    }

    private static void UnlockCharacterSelectButton(
        Godot.Node button,
        string characterId)
    {
        EnsureUsableGodotObject(button, $"character select button '{characterId}'");
        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(button, "DebugUnlock"))
        {
            throw new InvalidOperationException($"The live character-select button for '{characterId}' did not expose the expected debug unlock path.");
        }
    }

    private static void ApplyFixtureLockedCharacterSelectButton(
        Godot.Node button,
        CharacterModel character)
    {
        EnsureUsableGodotObject(button, $"character select button '{character.Id.Entry}'");
        if (!Sts2LiveIntrospection.TrySetMemberValue(button, "_isLocked", true))
        {
            throw new InvalidOperationException($"The live character-select button for '{character.Id.Entry}' did not expose a lock state field.");
        }

        if (Sts2LiveIntrospection.GetMemberValue(button, "_icon") is TextureRect icon)
        {
            EnsureUsableGodotObject(icon, $"character select icon '{character.Id.Entry}'");
            icon.Texture = character.CharacterSelectLockedIcon;
        }
        else
        {
            throw new InvalidOperationException($"The live character-select button for '{character.Id.Entry}' did not expose an icon texture field.");
        }

        if (Sts2LiveIntrospection.GetMemberValue(button, "_lock") is CanvasItem lockOverlay)
        {
            EnsureUsableGodotObject(lockOverlay, $"character select lock overlay '{character.Id.Entry}'");
            lockOverlay.Visible = true;
        }
        else
        {
            throw new InvalidOperationException($"The live character-select button for '{character.Id.Entry}' did not expose a lock overlay field.");
        }
    }

    private static void FocusLocalCharacterButton(
        Node screen,
        string selectedCharacterId)
    {
        EnsureUsableGodotObject(screen, "character-select screen");
        if (Sts2LiveIntrospection.GetMemberValue(screen, "_charButtonContainer") is not Godot.Node buttonContainer)
        {
            return;
        }
        EnsureUsableGodotObject(buttonContainer, "character button container");

        foreach (var child in buttonContainer.GetChildren().OfType<Godot.Node>())
        {
            EnsureUsableGodotObject(child, "character select button");
            if (!Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
            {
                continue;
            }

            var character = Sts2LiveIntrospection.GetMemberValue(child, "Character") as CharacterModel;
            if (character is null || !string.Equals(character.Id.Entry, selectedCharacterId, StringComparison.Ordinal))
            {
                continue;
            }

            Sts2LiveIntrospection.TryInvokeParameterlessMethod(child, "Select");
            return;
        }
    }

    // Replicate NCharacterSelectScreen.OnEmbarkPressed's visible effects when a fixture marks the
    // local player ready. ApplyStartRunLobbyRecipe already set the lobby player's isReady (which drives the
    // top-left player-list check), but the screen's button/overlay state is updated IMPERATIVELY by
    // the confirm-button handler, which the loader never ran — so the back/confirm buttons and the
    // "waiting for players" overlay stayed in their not-ready layout. We deliberately do NOT call
    // StartRunLobby.SetReady here: the ready flag is already set, and SetReady would only re-send a
    // network message and re-run BeginRunIfAllPlayersReady for no additional visible change.
    private static void ApplyLocalReadyLobbyState(
        NCharacterSelectScreen screen,
        LobbyFixtureRecipe recipe)
    {
        if (!recipe.LocalPlayer.IsReady)
        {
            return;
        }

        EnsureUsableGodotObject(screen, "character-select screen");

        // Post-FTUE OnEmbarkPressed: disable the confirm + back buttons (NConfirmButton/NBackButton
        // slide them off-screen) and disable the whole character grid.
        InvokeLobbyButtonMethod(screen, "_embarkButton", "Disable");
        InvokeLobbyButtonMethod(screen, "_backButton", "Disable");

        if (Sts2LiveIntrospection.GetMemberValue(screen, "_charButtonContainer") is Godot.Node buttonContainer
            && IsUsableGodotObject(buttonContainer))
        {
            foreach (var child in buttonContainer.GetChildren().OfType<Godot.Node>())
            {
                if (IsUsableGodotObject(child)
                    && Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton"))
                {
                    Sts2LiveIntrospection.TryInvokeParameterlessMethod(child, "Disable");
                }
            }
        }

        // The loader always initializes the lobby as a multiplayer host, so the game's
        // `IsMultiplayer()` half of OnEmbarkPressed's guard is always true here; only
        // `IsAboutToBeginGame()` can vary (a fixture marking every player ready would begin the run
        // instead of waiting). When waiting, reveal the "Esperando al resto del equipo…" panel and
        // swap the back button for the unready button.
        if (!screen.Lobby.IsAboutToBeginGame())
        {
            if (Sts2LiveIntrospection.GetMemberValue(screen, "_readyAndWaitingContainer") is CanvasItem waitingPanel
                && IsUsableGodotObject(waitingPanel))
            {
                waitingPanel.Visible = true;
            }

            InvokeLobbyButtonMethod(screen, "_unreadyButton", "Enable");
        }
    }

    private static void InvokeLobbyButtonMethod(Node screen, string fieldName, string method)
    {
        if (Sts2LiveIntrospection.GetMemberValue(screen, fieldName) is Godot.Node button
            && IsUsableGodotObject(button))
        {
            Sts2LiveIntrospection.TryInvokeParameterlessMethod(button, method);
        }
    }

    private bool ValidateEventRoomRecipe(
        FixtureLoadRequestSnapshot request,
        FixtureEventRoom? eventRoom,
        string playerId,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        if (eventRoom is null || eventRoom.Options.Count == 0)
        {
            return true;
        }

        var assertedOptions = eventRoom.Options
            .Where(option => !string.IsNullOrWhiteSpace(option.Id) || !string.IsNullOrWhiteSpace(option.Label))
            .ToArray();
        if (assertedOptions.Length == 0)
        {
            return true;
        }

        var roomObject = Sts2EventRoomScreenInspector.ResolveActiveRoom();
        var choices = Sts2EventRoomScreenInspector.ResolveChoices(roomObject!, playerId);
        if (choices.Count != assertedOptions.Length)
        {
            invalid = InvalidFixture(
                request,
                "run.currentRoom.event.playerStates[].options",
                JsonSerializer.Serialize(assertedOptions),
                "The loaded event-room did not expose the authored number of visible options.");
            return false;
        }

        for (var index = 0; index < assertedOptions.Length; index += 1)
        {
            var expected = assertedOptions[index];
            var actual = choices[index].Snapshot;
            var expectedChoiceId = Sts2EventRoomIds.ChoiceId(expected.Id, index);
            if (!string.Equals(actual.Id, expectedChoiceId, StringComparison.Ordinal)
                || !string.Equals(actual.Label, expected.Label, StringComparison.Ordinal))
            {
                invalid = InvalidFixture(
                    request,
                    $"run.currentRoom.event.playerStates[].options[{index}]",
                    $"{actual.Id} / {actual.Label}",
                    $"The live event-room exposed '{actual.Id}' / '{actual.Label}' instead of the authored '{expectedChoiceId}' / '{expected.Label}'.");
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Success, FixtureRecipeFieldReport? Report, FixtureLoadResult? Invalid)> ApplyFixtureCrystalSphereChosenOptionAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        EventModel eventModel)
    {
        var chosenOptions = fixture.EventRoom?.Options
            .Where(option => option.WasChosen == true)
            .ToArray() ?? [];
        if (chosenOptions.Length != 1)
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].options[].wasChosen",
                    $"count={chosenOptions.Length}",
                    "Author exactly one Crystal Sphere event option with wasChosen: true."));
        }

        var chosen = chosenOptions[0];
        if (string.IsNullOrWhiteSpace(chosen.TextKey))
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].options[].textKey",
                    string.Empty,
                    "Crystal Sphere chosen options must use the game EventOption.TextKey."));
        }

        if (!IsCrystalSphereEventModel(eventModel))
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.canonicalEventModelId",
                    fixture.EventRoom?.EventId ?? string.Empty,
                    "Only the CRYSTAL_SPHERE event can load the Crystal Sphere grid screen."));
        }

        var mutableEvent = RunManager.Instance.EventSynchronizer.GetLocalEvent();
        if (!IsCrystalSphereEventModel(mutableEvent))
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.canonicalEventModelId",
                    fixture.EventRoom?.EventId ?? string.Empty,
                    "The live event room did not start a local Crystal Sphere event."));
        }

        var currentOptions = mutableEvent.CurrentOptions;
        var matchIndex = -1;
        EventOption? match = null;
        for (var index = 0; index < currentOptions.Count; index += 1)
        {
            var option = currentOptions[index];
            if (string.Equals(option.TextKey, chosen.TextKey, StringComparison.Ordinal))
            {
                matchIndex = index;
                match = option;
                break;
            }
        }

        if (match is null)
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].options[].textKey",
                    chosen.TextKey,
                    "The chosen Crystal Sphere option was not visible in the live initial options."));
        }

        var eventRoom = Sts2EventRoomScreenInspector.ResolveActiveRoom();
        if (eventRoom is null)
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event",
                    fixture.EventRoom?.EventId ?? string.Empty,
                    "The event room was not active before choosing the Crystal Sphere option."));
        }

        Sts2LiveIntrospection.TryInvokeMethod(eventRoom, "OptionButtonClicked", match, matchIndex);
        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        var crystalSphereScreen = await WaitForCrystalSphereScreenObject();
        if (crystalSphereScreen is null)
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "screen",
                    fixture.Screen,
                    "The chosen Crystal Sphere option did not open the Crystal Sphere grid screen."));
        }

        var choices = Sts2CrystalSphereScreenInspector.ResolveChoices(crystalSphereScreen, fixture.Perspective.PlayerId);
        if (!choices.Any(choice => choice.Kind == CrystalSphereChoiceKind.BigTool && choice.IsExecutable)
            || !choices.Any(choice => choice.Kind == CrystalSphereChoiceKind.SmallTool && choice.IsExecutable))
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].options[].wasChosen",
                    chosen.TextKey,
                    "The Crystal Sphere grid opened but did not expose executable Big and Small Divination controls."));
        }

        if (!choices.Any(choice => choice.Kind == CrystalSphereChoiceKind.Cell && choice.IsExecutable))
        {
            return (
                false,
                null,
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].options[].wasChosen",
                    chosen.TextKey,
                    "The Crystal Sphere grid opened but did not expose executable hidden cell controls."));
        }

        return (
            true,
            Report(
                "run.currentRoom.event.playerStates[].options[].wasChosen",
                chosen.TextKey,
                "applied-authored-field",
                "Selected the authored Crystal Sphere event option and opened the grid screen."),
            null);
    }

    private async Task<(bool Success, IReadOnlyList<FixtureRecipeFieldReport> Reports, FixtureLoadResult? Invalid)> ApplyFixtureCrystalSphereStateAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture)
    {
        var crystalSphere = fixture.EventRoom?.CrystalSphere;
        if (crystalSphere is null)
        {
            return (true, [], null);
        }

        var crystalSphereScreen = await WaitForCrystalSphereScreenObject();
        if (crystalSphereScreen is null)
        {
            return (
                false,
                [],
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].crystalSphere",
                    fixture.Screen,
                    "The authored Crystal Sphere state requires the Crystal Sphere grid screen to be open."));
        }

        var minigame = Sts2LiveIntrospection.GetMemberValue(crystalSphereScreen, "_entity");
        if (minigame is null)
        {
            return (
                false,
                [],
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].crystalSphere",
                    fixture.Screen,
                    "The live Crystal Sphere screen did not expose its minigame entity."));
        }

        var reports = new List<FixtureRecipeFieldReport>();
        var revealedCells = crystalSphere.Cells
            .Where(cell => !cell.IsHidden)
            .ToArray();
        for (var index = 0; index < revealedCells.Length; index += 1)
        {
            var cell = revealedCells[index];
            try
            {
                var result = Sts2LiveIntrospection.InvokeMethod(minigame, "ClearCell", cell.X, cell.Y);
                if (result is Task task)
                {
                    await task;
                }
                else if (result is null)
                {
                    return (
                        false,
                        [],
                        InvalidFixture(
                            request,
                            $"run.currentRoom.event.playerStates[].crystalSphere.cells[{index}]",
                            cell.Id,
                            "The live Crystal Sphere minigame did not expose the ClearCell(x, y) hook."));
                }
            }
            catch (Exception ex)
            {
                var reason = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
                return (
                    false,
                    [],
                    InvalidFixture(
                        request,
                        $"run.currentRoom.event.playerStates[].crystalSphere.cells[{index}]",
                        cell.Id,
                        $"Clearing the authored Crystal Sphere cell failed: {reason}"));
            }
        }

        if (revealedCells.Length > 0)
        {
            await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
            reports.Add(Report(
                "run.currentRoom.event.playerStates[].crystalSphere.cells",
                $"revealed={revealedCells.Length}",
                "applied-authored-field",
                "Cleared authored Crystal Sphere cells through the live minigame fog/reveal path."));
        }

        if (!Sts2LiveIntrospection.TrySetMemberValue(minigame, "DivinationCount", crystalSphere.DivinationsRemaining))
        {
            return (
                false,
                [],
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].crystalSphere.divinationsRemaining",
                    crystalSphere.DivinationsRemaining.ToString(CultureInfo.InvariantCulture),
                    "The live Crystal Sphere minigame did not expose a writable DivinationCount property."));
        }

        reports.Add(Report(
            "run.currentRoom.event.playerStates[].crystalSphere.divinationsRemaining",
            crystalSphere.DivinationsRemaining.ToString(CultureInfo.InvariantCulture),
            "applied-authored-field",
            "Applied the authored Crystal Sphere divination counter."));

        if (!await ApplyFixtureCrystalSphereSelectedToolAsync(fixture.Perspective.PlayerId, crystalSphereScreen, minigame, crystalSphere.SelectedTool))
        {
            return (
                false,
                [],
                InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].crystalSphere.selectedTool",
                    crystalSphere.SelectedTool,
                    "The live Crystal Sphere screen did not expose the requested selected tool hook."));
        }

        reports.Add(Report(
            "run.currentRoom.event.playerStates[].crystalSphere.selectedTool",
            crystalSphere.SelectedTool,
            "applied-authored-field",
            "Applied the authored Crystal Sphere selected tool."));

        await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
        return (true, reports, null);
    }

    private async Task<bool> ApplyFixtureCrystalSphereSelectedToolAsync(
        string playerId,
        object crystalSphereScreen,
        object minigame,
        string selectedTool)
    {
        if (string.Equals(selectedTool, "none", StringComparison.OrdinalIgnoreCase))
        {
            var currentTool = Sts2LiveIntrospection.GetMemberValue(minigame, "CrystalSphereTool");
            if (currentTool is null)
            {
                return false;
            }

            var noneTool = Enum.Parse(currentTool.GetType(), "None");
            return Sts2LiveIntrospection.TryInvokeMethod(minigame, "SetTool", noneTool);
        }

        var kind = string.Equals(selectedTool, "small", StringComparison.OrdinalIgnoreCase)
            ? CrystalSphereChoiceKind.SmallTool
            : CrystalSphereChoiceKind.BigTool;
        var choices = Sts2CrystalSphereScreenInspector.ResolveChoices(crystalSphereScreen, playerId);
        var choice = choices.FirstOrDefault(candidate => candidate.Kind == kind);
        if (choice is null)
        {
            return false;
        }

        var methodName = kind == CrystalSphereChoiceKind.SmallTool
            ? "SetSmallDivination"
            : "SetBigDivination";
        if (!Sts2LiveIntrospection.TryInvokeMethod(crystalSphereScreen, methodName, choice.Control))
        {
            return false;
        }

        await AwaitFixtureRenderFramesAsync(1);
        return true;
    }

    private static async Task<object?> WaitForCrystalSphereScreenObject()
    {
        const int maxAttempts = 30;
        for (var attempt = 0; attempt < maxAttempts; attempt += 1)
        {
            var overlay = NOverlayStack.Instance?.Peek();
            if (Sts2LiveIntrospection.IsType(
                    overlay,
                    "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen"))
            {
                return overlay;
            }

            await AwaitFixtureRenderFramesAsync(1);
        }

        return null;
    }

    private async Task<(bool Success, FixtureRecipeFieldReport? Report, FixtureLoadResult? Invalid)> ApplyFixtureAncientDialogueAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        EventModel eventModel)
    {
        if (!TryResolveFixtureAncientDialogue(
                eventModel,
                fixture,
                out var dialogue,
                out var dialogueId,
                out var authored,
                out var failureNote))
        {
            return (false, null, InvalidFixture(
                request,
                "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
                fixture.EventRoom?.AncientDialogueId ?? string.Empty,
                failureNote));
        }

        var layout = Sts2LiveIntrospection.GetMemberValue(Sts2EventRoomScreenInspector.ResolveActiveRoom(), "Layout");
        if (layout is null || !(layout.GetType().FullName?.Contains("NAncientEventLayout", StringComparison.Ordinal) ?? false))
        {
            return (false, null, InvalidFixture(
                request,
                "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
                dialogueId,
                "The loaded event-room did not expose an ancient event layout."));
        }

        var lines = Sts2LiveIntrospection.GetMemberValue(dialogue, "Lines");
        if (lines is null)
        {
            return (false, null, InvalidFixture(
                request,
                "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
                dialogueId,
                "The selected ancient dialogue did not expose dialogue lines."));
        }

        AddFixtureAncientDialogueVariables(eventModel, lines);
        Sts2LiveIntrospection.TryInvokeParameterlessMethod(layout, "ClearDialogue");
        if (!Sts2LiveIntrospection.TryInvokeMethod(layout, "SetDialogue", lines))
        {
            return (false, null, InvalidFixture(
                request,
                "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
                dialogueId,
                "The loaded ancient event layout could not accept the selected dialogue."));
        }

        await RefreshFixtureAncientLayoutAsync(layout);
        var report = Report(
            "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
            dialogueId,
            authored ? "applied-authored-field" : "inferred-deterministic-fixture-field",
            authored
                ? "Applied the authored ancient dialogue id to the live ancient event layout."
                : "Inferred a deterministic ancient dialogue id for the live ancient event layout.");
        return (true, report, null);
    }

    private static async Task RefreshFixtureAncientLayoutAsync(object layout)
    {
        await AwaitFixtureRenderFramesAsync(1);
        Sts2LiveIntrospection.TryInvokeParameterlessMethod(layout, "OnSetupComplete");
        await AwaitFixtureRenderFramesAsync(1);
    }

    private static bool TryResolveFixtureAncientDialogue(
        EventModel eventModel,
        FixtureDocument fixture,
        out object? dialogue,
        out string dialogueId,
        out bool authored,
        out string failureNote)
    {
        dialogue = null;
        dialogueId = string.Empty;
        authored = !string.IsNullOrWhiteSpace(fixture.EventRoom?.AncientDialogueId);
        failureNote = string.Empty;

        var dialogues = EnumerateFixtureAncientDialogues(eventModel).ToList();
        if (dialogues.Count == 0)
        {
            failureNote = "The ancient event model did not expose any dialogue trees.";
            return false;
        }

        if (authored)
        {
            var requested = fixture.EventRoom!.AncientDialogueId.Trim();
            var match = dialogues.FirstOrDefault(entry => string.Equals(entry.DialogueId, requested, StringComparison.OrdinalIgnoreCase));
            if (match.Dialogue is null)
            {
                match = ResolveCompatibleAncientDialogueVariant(dialogues, requested);
                if (match.Dialogue is not null)
                {
                    dialogue = match.Dialogue;
                    dialogueId = match.DialogueId;
                    authored = false;
                    return true;
                }

                failureNote = "Use an ancient dialogue id available on the selected ancient event, such as NEOW.talk.ANY.4.";
                dialogueId = requested;
                return false;
            }

            dialogue = match.Dialogue;
            dialogueId = match.DialogueId;
            return true;
        }

        var character = ResolveFixturePerspectiveCharacter(fixture);
        var eventId = eventModel.Id.Entry;
        var preferredPrefix = $"{eventId}.talk.{character}.";
        var inferred = dialogues.FirstOrDefault(entry => entry.DialogueId.StartsWith(preferredPrefix, StringComparison.OrdinalIgnoreCase));
        if (inferred.Dialogue is null)
        {
            inferred = dialogues.FirstOrDefault(entry => entry.DialogueId.StartsWith($"{eventId}.talk.firstVisitEver.", StringComparison.OrdinalIgnoreCase));
        }

        if (inferred.Dialogue is null)
        {
            inferred = dialogues.FirstOrDefault(entry => entry.DialogueId.StartsWith($"{eventId}.talk.ANY.", StringComparison.OrdinalIgnoreCase));
        }

        if (inferred.Dialogue is null)
        {
            inferred = dialogues[0];
        }

        dialogue = inferred.Dialogue;
        dialogueId = inferred.DialogueId;
        return true;
    }

    private static (string DialogueId, object Dialogue) ResolveCompatibleAncientDialogueVariant(
        IReadOnlyList<(string DialogueId, object Dialogue)> dialogues,
        string requested)
    {
        var requestedVariantSeparator = requested.LastIndexOf('.');
        if (requestedVariantSeparator <= 0
            || !int.TryParse(requested[(requestedVariantSeparator + 1)..], out _))
        {
            return default;
        }

        var requestedPrefix = requested[..(requestedVariantSeparator + 1)];
        return dialogues.FirstOrDefault(
            entry => entry.DialogueId.StartsWith(requestedPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(string DialogueId, object Dialogue)> EnumerateFixtureAncientDialogues(EventModel eventModel)
    {
        var dialogueSet = Sts2LiveIntrospection.GetMemberValue(eventModel, "DialogueSet");
        var dialogues = Sts2LiveIntrospection.InvokeMethod(dialogueSet, "GetAllDialogues") as System.Collections.IEnumerable;
        if (dialogues is null)
        {
            yield break;
        }

        foreach (var dialogue in dialogues)
        {
            if (dialogue is null)
            {
                continue;
            }

            var lines = Sts2LiveIntrospection.GetMemberValue(dialogue, "Lines") as System.Collections.IEnumerable;
            var firstLine = lines?.Cast<object?>().FirstOrDefault(line => line is not null);
            var firstLineKey = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(firstLine, "LineText"),
                "LocEntryKey")?.ToString();
            var dialogueId = ResolveAncientDialogueId(firstLineKey);
            if (dialogueId is null)
            {
                continue;
            }

            yield return (dialogueId, dialogue);
        }
    }

    private static string ResolveFixturePerspectiveCharacter(FixtureDocument fixture)
    {
        var player = fixture.Players.FirstOrDefault(player => string.Equals(player.Id, fixture.Perspective.PlayerId, StringComparison.Ordinal))
            ?? fixture.Players.FirstOrDefault();
        return string.IsNullOrWhiteSpace(player?.Character)
            ? "IRONCLAD"
            : player.Character.Trim();
    }

    private static string? ResolveAncientDialogueId(string? locKey)
    {
        if (string.IsNullOrWhiteSpace(locKey))
        {
            return null;
        }

        var key = locKey.Trim();
        var roleSeparator = key.LastIndexOf('.');
        if (roleSeparator > 0)
        {
            key = key[..roleSeparator];
        }

        var lineSeparator = key.LastIndexOf('-');
        return lineSeparator > 0 ? key[..lineSeparator] : key;
    }

    private static void AddFixtureAncientDialogueVariables(EventModel eventModel, object lines)
    {
        var runState = Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(eventModel, "Owner"), "RunState");
        var acts = Sts2LiveIntrospection.GetMemberValue(runState, "Acts") as System.Collections.IEnumerable;
        var firstAct = acts?.Cast<object?>().FirstOrDefault(act => act is not null);
        var actTitle = Sts2LiveIntrospection.GetMemberValue(firstAct, "Title");
        if (actTitle is null || lines is not System.Collections.IEnumerable enumerableLines)
        {
            return;
        }

        foreach (var line in enumerableLines)
        {
            var lineText = Sts2LiveIntrospection.GetMemberValue(line, "LineText");
            Sts2LiveIntrospection.TryInvokeMethod(lineText, "Add", "Act1Name", actTitle);
        }
    }

    private static bool IsAncientEventModel(EventModel eventModel)
    {
        for (var current = eventModel.GetType(); current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, "AncientEventModel", StringComparison.Ordinal)
                || string.Equals(current.FullName, "MegaCrit.Sts2.Core.Models.AncientEventModel", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCrystalSphereEventModel(EventModel eventModel)
        => string.Equals(
            eventModel.Id.Entry.Replace('_', '-'),
            "crystal-sphere",
            StringComparison.OrdinalIgnoreCase);

    private bool TryApplyOpenedTreasureRoomRecipe(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        RunManager runManager,
        RunState runState,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        var roomObject = Sts2TreasureRoomScreenInspector.ResolveActiveRoom(screenObject);
        var choices = Sts2TreasureRoomScreenInspector.ResolveChoices(roomObject, screenObject, fixture.Perspective.PlayerId);
        var openChestChoice = choices.FirstOrDefault(choice => choice.Kind == TreasureRoomChoiceKind.OpenChest);
        if (openChestChoice is null)
        {
            invalid = RuntimeFailure(
                request,
                "run.currentRoom.treasure.currentRelicsActive",
                "The live treasure-room did not expose an executable chest-open control.");
            return false;
        }

        var opened = Sts2LiveIntrospection.TryInvokeMethod(roomObject, "OnChestButtonReleased", openChestChoice.Control)
            || Sts2LiveIntrospection.TryInvokeParameterlessMethod(openChestChoice.Control, "OnRelease");
        if (!opened)
        {
            invalid = RuntimeFailure(
                request,
                "run.currentRoom.treasure.currentRelicsActive",
                "The live treasure-room did not expose a recognized chest-open callback.");
            return false;
        }

        // Apply authored votes before reading canProceed so they can influence the
        // derived proceed control (the synchronizer may unlock proceed once players vote).
        if (fixture.TreasureRoom?.PlayerVotes is { Count: > 0 } playerVotes)
        {
            if (!TryApplyTreasureRoomVotes(request, playerVotes, runManager, runState, out invalid))
            {
                return false;
            }
        }

        var refreshedChoices = Sts2TreasureRoomScreenInspector.ResolveChoices(
            Sts2TreasureRoomScreenInspector.ResolveActiveRoom(),
            Sts2LiveIntrospection.ResolveCurrentScreenObject(),
            fixture.Perspective.PlayerId);
        if (fixture.TreasureRoom?.CanProceed is bool expectedCanProceed)
        {
            var canProceed = refreshedChoices.Any(choice => choice.Kind == TreasureRoomChoiceKind.Proceed && choice.IsExecutable);
            if (canProceed != expectedCanProceed)
            {
                invalid = InvalidFixture(
                    request,
                    "run.currentRoom.treasure.canProceed",
                    expectedCanProceed.ToString().ToLowerInvariant(),
                    $"The live opened treasure-room exposed canProceed={canProceed.ToString().ToLowerInvariant()} instead of the authored value.");
                return false;
            }
        }

        return true;
    }

    private static bool TryApplyTreasureRoomVotes(
        FixtureLoadRequestSnapshot request,
        IReadOnlyList<FixtureTreasureRoomPlayerVote> playerVotes,
        RunManager runManager,
        RunState runState,
        out FixtureLoadResult? invalid)
    {
        invalid = null;

        var synchronizer = Sts2LiveIntrospection.GetMemberValue(runManager, "TreasureRoomRelicSynchronizer");
        if (synchronizer is null)
        {
            invalid = RuntimeFailure(
                request,
                "run.currentRoom.treasure.playerVotes",
                "The live treasure-room did not expose RunManager.TreasureRoomRelicSynchronizer to author player votes.");
            return false;
        }

        var relicCount = EnumerateLiveCollection(Sts2LiveIntrospection.GetMemberValue(synchronizer, "CurrentRelics")).Count();
        var players = EnumerateLiveCollection(Sts2LiveIntrospection.GetMemberValue(runState, "Players")).ToArray();

        foreach (var vote in playerVotes)
        {
            object? targetPlayer = null;
            for (var index = 0; index < players.Length; index++)
            {
                var player = players[index];
                if (player is null)
                {
                    continue;
                }

                if (string.Equals(ResolveFixturePlayerId(player, index), vote.PlayerId, StringComparison.Ordinal))
                {
                    targetPlayer = player;
                    break;
                }
            }

            if (targetPlayer is null)
            {
                invalid = InvalidFixture(
                    request,
                    "run.currentRoom.treasure.playerVotes[].playerId",
                    vote.PlayerId,
                    "The authored playerId does not match any player in the live run.");
                return false;
            }

            if (vote.Index is int relicIndex && (relicIndex < 0 || relicIndex >= relicCount))
            {
                invalid = InvalidFixture(
                    request,
                    "run.currentRoom.treasure.playerVotes[].index",
                    relicIndex.ToString(),
                    $"The authored relic index is out of range; the live treasure-room exposed {relicCount} relic(s).");
                return false;
            }

            // OnPicked(Player, int?) takes a nullable index; the introspection
            // helper's type filter rejects a boxed int against an int? parameter,
            // so resolve and invoke the method directly here.
            var onPicked = synchronizer
                .GetType()
                .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "OnPicked", StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 2 } parameters
                    && parameters[0].ParameterType.IsInstanceOfType(targetPlayer));
            if (onPicked is null)
            {
                invalid = RuntimeFailure(
                    request,
                    "run.currentRoom.treasure.playerVotes",
                    "The live treasure-room synchronizer did not expose an OnPicked(player, index) method to author votes.");
                return false;
            }

            try
            {
                onPicked.Invoke(synchronizer, [targetPlayer, vote.Index]);
            }
            catch (Exception ex)
            {
                invalid = RuntimeFailure(
                    request,
                    "run.currentRoom.treasure.playerVotes",
                    $"The live treasure-room synchronizer rejected an authored vote for {vote.PlayerId}: {ex.InnerException?.Message ?? ex.Message}.");
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<object?> EnumerateLiveCollection(object? value)
    {
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static string ResolveFixturePlayerId(object player, int fallbackIndex)
    {
        var netId = Sts2LiveIntrospection.GetMemberValue(player, "NetId");
        return netId is null ? $"p:unknown:{fallbackIndex}" : $"p:{netId}";
    }

    private bool ApplyShopRecipe(
        FixtureLoadRequestSnapshot request,
        Player player,
        FixtureShop? shop,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        if (shop is null)
        {
            return true;
        }

        var merchantRoom = NMerchantRoom.Instance?.Room;
        if (merchantRoom?.GetLocalInventory() is not MerchantInventory inventory)
        {
            invalid = RuntimeFailure(request, "shop", "The live merchant room did not expose an inventory to customize.");
            return false;
        }

        var cardModels = ResolveCardModels(request, "run.currentRoom.shop.inventory.characterCardEntries", shop.CardIds, allowEmpty: true, out invalid);
        if (invalid is not null)
        {
            return false;
        }

        var relicModels = ResolveRelicModels(request, "run.currentRoom.shop.inventory.relicEntries", shop.RelicIds, allowEmpty: true, out invalid);
        if (invalid is not null)
        {
            return false;
        }

        var potionModels = ResolvePotionModels(request, "run.currentRoom.shop.inventory.potionEntries", shop.PotionIds, allowEmpty: true, out invalid);
        if (invalid is not null)
        {
            return false;
        }

        ApplyShopCardEntries(inventory.CharacterCardEntries.Concat(inventory.ColorlessCardEntries).ToArray(), cardModels!, player);
        ApplyShopRelicEntries(inventory.RelicEntries, relicModels!);
        ApplyShopPotionEntries(inventory.PotionEntries, potionModels!);
        ApplyShopCardRemovalEntry(inventory, player, shop.CardRemovalAvailable);
        RefreshShopEntries(inventory);
        return true;
    }

    private static void ApplyShopCardEntries(IReadOnlyList<MerchantCardEntry> entries, IReadOnlyList<CardModel> models, Player player)
    {
        for (var index = 0; index < entries.Count; index += 1)
        {
            // Create the shop card OWNED by the shop's player (matches real MerchantInventory generation);
            // an unowned card makes the buy's CardPileCmd.Add(card, Deck) throw "has no owner". Mirrors the
            // owned-card creation used for combat/reward fixtures (RunState.CreateCard(model, player)).
            var creationResult = index < models.Count
                ? new CardCreationResult(player.RunState.CreateCard(models[index], player))
                : null;
            SetProperty(entries[index], "CreationResult", creationResult);
            if (creationResult is not null)
            {
                entries[index].CalcCost();
            }
        }
    }

    private static void ApplyShopRelicEntries(IReadOnlyList<MerchantRelicEntry> entries, IReadOnlyList<RelicModel> models)
    {
        for (var index = 0; index < entries.Count; index += 1)
        {
            var model = index < models.Count ? models[index].ToMutable() : null;
            SetProperty(entries[index], "Model", model);
            if (model is not null)
            {
                entries[index].CalcCost();
            }
        }
    }

    private static void ApplyShopPotionEntries(IReadOnlyList<MerchantPotionEntry> entries, IReadOnlyList<PotionModel> models)
    {
        for (var index = 0; index < entries.Count; index += 1)
        {
            var model = index < models.Count ? models[index].ToMutable() : null;
            SetProperty(entries[index], "Model", model);
            if (model is not null)
            {
                entries[index].CalcCost();
            }
        }
    }

    private static void ApplyShopCardRemovalEntry(MerchantInventory inventory, Player player, bool? cardRemovalAvailable)
    {
        if (cardRemovalAvailable is null)
        {
            return;
        }

        if (inventory.CardRemovalEntry is null)
        {
            SetProperty(inventory, "CardRemovalEntry", new MerchantCardRemovalEntry(player));
        }

        if (cardRemovalAvailable == false && inventory.CardRemovalEntry is not null)
        {
            inventory.CardRemovalEntry.SetUsed();
        }
    }

    private static void RefreshShopEntries(MerchantInventory inventory)
    {
        foreach (var entry in inventory.AllEntries)
        {
            entry.OnMerchantInventoryUpdated();
        }
    }

    private static IReadOnlyList<PotionModel>? ResolvePotionModels(
        FixtureLoadRequestSnapshot request,
        string field,
        IReadOnlyList<string> potionIds,
        bool allowEmpty,
        out FixtureLoadResult? invalid)
    {
        invalid = null;
        var potions = new List<PotionModel>();
        foreach (var (potionId, index) in potionIds.Select((potionId, index) => (potionId, index)))
        {
            if (!Sts2ModelResolver.TryResolveFixturePotion(potionId, out var potion))
            {
                invalid = InvalidFixture(
                    request,
                    $"{field}[{index}]",
                    potionId,
                    "Use a potion id from the installed STS2 content.");
                return null;
            }

            potions.Add(potion);
        }

        if (!allowEmpty && potions.Count == 0)
        {
            invalid = InvalidFixture(request, field, string.Empty, "Provide one or more ids for this fixture section.");
            return null;
        }

        return potions;
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName);
        property?.SetValue(target, value);
    }

    // Builds a render-complete saved run for the load-run lobby. The load-game screen renders
    // the saved run's generated map/acts, so a hand-built SerializableRun (acts with no SavedMap)
    // native-crashes it. Instead, set up and generate a real run in-memory exactly like the
    // in-run fixtures (SetUpNewSinglePlayer + GenerateMap), serialize it through the game's own
    // RunManager.ToSave, then tear the run back down. No NRun scene is launched (we stay on the
    // main menu), so GenerateMap/ToSave operate purely on RunManager.State.
    private async Task<(SerializableRun? Save, FixtureLoadResult? Invalid)> TryBuildCompleteLoadRunSaveAsync(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        LobbyFixtureRecipe recipe,
        ulong localNetId)
    {
        if (!Sts2ModelResolver.TryResolveFixtureAct(fixture.Run.Act, out var act))
        {
            return (null, InvalidFixture(
                request,
                "run.currentActIndex",
                fixture.Run.Act.ToString(),
                "Use an act number exposed by the installed STS2 content."));
        }

        var runManager = RunManager.Instance;
        if (runManager is null)
        {
            return (null, RuntimeFailure(request, "run_manager", "RunManager.Instance was null while building the load-run save."));
        }

        // The local (host) seat uses the live net id so the game's load-run save validation
        // accepts it; remote seats keep their authored stable ids.
        var players = recipe.AllPlayers
            .Select(playerRecipe => Player.CreateForNewRun(
                playerRecipe.Character,
                UnlockState.all,
                playerRecipe.IsLocal ? localNetId : playerRecipe.NetId))
            .ToArray();
        var runState = RunState.CreateForTest(
            players,
            Sts2ModelResolver.CreateActsForRunStateCreation(),
            modifiers: [],
            ascensionLevel: fixture.Run.AscensionLevel ?? 0,
            seed: fixture.Run.Seed);
        runState.SetActDebug(act);

        runManager.SetUpNewSingleplayer(runState, shouldSave: false, dailyTime: null);
        await PreloadManager.LoadRunAssets(runState.Players.Select(player => player.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        await runManager.GenerateMap();
        var save = runManager.ToSave(null);
        // No NRun was launched, so ToSave leaves MapDrawings null; substitute an empty
        // (drawing-free) value, matching what GetSerializableMapDrawings produces with no lines.
        save.MapDrawings ??= new SerializableMapDrawings();
        // Leave the generated run's State live: the caller reloads the players' CharacterSelectBg
        // assets (LoadRunAssets needs run context) right before the load-game screen instantiates
        // them, then tears the run down after InitializeAsHost.
        return (save, null);
    }

    // Tears down the temporary run generated by TryBuildCompleteLoadRunSaveAsync. The load-run
    // lobby holds its own serialized save, so once InitializeAsHost has run (or an attempt is
    // abandoned) the live run must be cleaned up — otherwise RunManager.IsInProgress would make
    // `state` report a run in progress instead of the lobby.
    private static void TearDownGeneratedRun()
    {
        if (RunManager.Instance is { IsInProgress: true } generatedRun)
        {
            generatedRun.CleanUp(graceful: false);
        }
    }

}
