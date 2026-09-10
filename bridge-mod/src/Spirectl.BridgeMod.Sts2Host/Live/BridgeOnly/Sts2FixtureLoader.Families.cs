using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Spirectl.Sts2.Live;


public sealed partial class Sts2FixtureLoader
{
    // These collaborators deliberately receive typed recipe inputs and only the operation they
    // can realize. The coordinator remains responsible for canonical input validation,
    // main-thread dispatch, cross-screen cleanup, and lobby-host ownership.
    private sealed partial class MenuLobbyFixtureFamily(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream,
        Func<NMainMenuSubmenuStack, string, bool> tryClearSubmenuStack,
        Action<string> clearLobbyOverlays,
        Action<NetHostGameService, string> disconnectLobbyHost,
        Action<NetHostGameService> retainLobbyHost,
        Action releaseLobbyHost,
        Action<string> resetLobbyLifecycle,
        Func<FixtureLoadRequestSnapshot, FixtureDocument, LobbyFixtureRecipe, ulong, Task<(SerializableRun? Save, FixtureLoadResult? Invalid)>> buildLoadRunSave)
    {
        private readonly Sts2ScreenLocator _screenLocator = screenLocator;
        private readonly ILogStream _logStream = logStream;
        private readonly Func<NMainMenuSubmenuStack, string, bool> _tryClearSubmenuStack = tryClearSubmenuStack;
        private readonly Action<string> _clearLobbyOverlays = clearLobbyOverlays;
        private readonly Action<NetHostGameService, string> _disconnectLobbyHost = disconnectLobbyHost;
        private readonly Action<NetHostGameService> _retainLobbyHost = retainLobbyHost;
        private readonly Action _releaseLobbyHost = releaseLobbyHost;
        private readonly Action<string> _resetLobbyLifecycle = resetLobbyLifecycle;
        private readonly Func<FixtureLoadRequestSnapshot, FixtureDocument, LobbyFixtureRecipe, ulong, Task<(SerializableRun? Save, FixtureLoadResult? Invalid)>> _buildLoadRunSave = buildLoadRunSave;
        public bool TryCreateInput(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture,
            out MenuLobbyFixtureInput input)
        {
            if (string.Equals(fixture.Screen, "main-menu", StringComparison.Ordinal))
            {
                input = new MenuFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsLobbyScreenType(fixture.Screen))
            {
                input = new LobbyFixtureInput(request, fixture);
                return true;
            }

            input = default!;
            return false;
        }

        public Task<FixtureLoadResult> LoadAsync(MenuLobbyFixtureInput input)
            => input switch
            {
                MenuFixtureInput menu => LoadMainMenuAsync(menu.Request, menu.Fixture),
                LobbyFixtureInput lobby => LoadLobbyAsync(lobby.Request, lobby.Fixture),
                _ => throw new ArgumentOutOfRangeException(nameof(input)),
            };
    }

    private abstract record MenuLobbyFixtureInput(
        FixtureLoadRequestSnapshot Request,
        FixtureDocument Fixture);

    private sealed record MenuFixtureInput(
        FixtureLoadRequestSnapshot Request,
        FixtureDocument Fixture) : MenuLobbyFixtureInput(Request, Fixture);

    private sealed record LobbyFixtureInput(
        FixtureLoadRequestSnapshot Request,
        FixtureDocument Fixture) : MenuLobbyFixtureInput(Request, Fixture);

    private sealed class CombatSelectionFixtureFamily(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream,
        Sts2SelectedCardViewState selectedCardViewState,
        FixtureRunContextFactory createRunContext,
        HostLocalRunContextFactory createCombatRunContext)
    {
        public bool TryCreateInput(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture,
            out CombatSelectionFixtureInput input)
        {
            if (string.Equals(fixture.Screen, "combat", StringComparison.Ordinal))
            {
                input = new CombatFixtureInput(request, fixture);
                return true;
            }

            if (IsSelectionFixtureScreen(fixture.Screen))
            {
                input = new SelectionFixtureInput(request, fixture);
                return true;
            }

            if (IsOverlayFixtureScreen(fixture.Screen))
            {
                input = new OverlayFixtureInput(request, fixture);
                return true;
            }

            input = default!;
            return false;
        }

        public Task<FixtureLoadResult> LoadAsync(CombatSelectionFixtureInput input)
            => input switch
            {
                CombatFixtureInput combat => LoadCombatAsync(combat),
                SelectionFixtureInput selection => LoadSelectionAsync(selection),
                OverlayFixtureInput overlay => LoadOverlayAsync(overlay),
                _ => throw new ArgumentOutOfRangeException(nameof(input)),
            };

        private async Task<FixtureLoadResult> LoadCombatAsync(CombatFixtureInput input)
        {
            var request = input.Request;
            var fixture = input.Fixture;
            if (fixture.Lobby is not null)
            {
                return InvalidFixture(request, "characterSelect", fixture.Lobby.Kind, "Remove lobby; combat fixtures use room.encounterId instead.");
            }

            if (string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
            {
                return InvalidFixture(request, "run.currentRoom.combat.encounterId", fixture.Room.EncounterId, "Provide room.encounterId for combat fixtures.");
            }

            if (!Sts2ModelResolver.TryResolveFixtureEncounter(fixture.Room.EncounterId, out var encounter))
            {
                return InvalidFixture(request, "run.currentRoom.combat.encounterId", fixture.Room.EncounterId, "Use an encounter id from the installed STS2 content.");
            }

            var bossActIndex = -1;
            if (encounter.RoomType == RoomType.Boss && TryResolveBossActIndex(encounter, out bossActIndex))
            {
                fixture.Run.Act = bossActIndex + 1;
            }

            if (CombatManager.Instance.DebugOnlyGetState() is not null)
            {
                CombatManager.Instance.Reset(graceful: true);
            }

            var (context, invalid) = await createCombatRunContext(request, fixture, "combat");
            if (context is null)
            {
                return invalid!;
            }

            var serializedRoom = new SerializableRoom
            {
                RoomType = encounter.RoomType,
                EncounterId = encounter.Id,
                IsPreFinished = false,
                GoldProportion = 1f,
            };
            var room = AbstractRoom.FromSerializable(serializedRoom, context.RunState);
            if (room is null)
            {
                return RuntimeFailure(request, "run.currentRoom.combat.encounterId", $"Could not materialize combat room for encounter '{fixture.Room.EncounterId}'.");
            }

            var mapPointType = ResolveCombatMapPointType(room.RoomType);
            var entryFloor = fixture.Run.Floor;
            if (bossActIndex >= 0)
            {
                var bossPoint = context.RunState.Map.SecondBossMapPoint ?? context.RunState.Map.BossMapPoint;
                entryFloor = bossPoint.coord.row;
                PrepareRestoreMapPointHistoryAt(context.RunState, bossPoint.coord, mapPointType, room.RoomType, room.ModelId, entryFloor);
            }
            else
            {
                PrepareRestoreMapPointHistory(context.RunState, mapPointType, room.RoomType, room.ModelId, fixture.Run.Floor);
            }

            await EnterMapPointInternalCompat(context.RunManager, entryFloor, MapPointType.Unknown, room);

            var livePlayersByFixtureId = context.Players.ToDictionary(player => player.FixtureId, player => player.Player);
            if (fixture.Players.Any(player => player.RelicIds is { Count: > 0 } || player.DeckCards is { Count: > 0 })
                && await ApplyAuthoredPlayerGrantsAsync(request, fixture, context.RunState, livePlayersByFixtureId) is { } grantInvalid)
            {
                return grantInvalid;
            }

            if (fixture.Players.Any(player => player.OrbIds is { Count: > 0 } || player.OrbSlotCount is not null)
                && await ApplyAuthoredOrbsAsync(request, fixture, livePlayersByFixtureId) is { } orbInvalid)
            {
                return orbInvalid;
            }

            if (fixture.Players.Any(player => player.Combat is { HasAuthoredPiles: true })
                && await ApplyAuthoredCombatPilesAsync(request, fixture, livePlayersByFixtureId) is { } combatPileInvalid)
            {
                return combatPileInvalid;
            }

            if ((fixture.Players.Any(player => player.Powers is { Count: > 0 })
                    || fixture.Room.Enemies.Any(enemy => enemy.Powers.Count > 0 || enemy.CurrentHp.HasValue || enemy.MaxHp.HasValue))
                && await ApplyAuthoredPowersAsync(request, fixture, context.Players) is { } powerFailure)
            {
                return powerFailure;
            }

            if (await ApplyAuthoredPotionUiAsync(request, fixture, context.Players) is { } potionUiFailure)
            {
                return potionUiFailure;
            }

            if (await ApplyAuthoredCardPileUiAsync(request, fixture, context.Players) is { } cardPileUiFailure)
            {
                return cardPileUiFailure;
            }

            if (await ApplyAuthoredDeckViewUiAsync(request, fixture, context.Players) is { } deckViewUiFailure)
            {
                return deckViewUiFailure;
            }

            if (await ApplyAuthoredSelectedCardAsync(request, fixture, context.Players, selectedCardViewState) is { } selectedCardFailure)
            {
                return selectedCardFailure;
            }

            if (await ApplyAuthoredInspectRelicUiAsync(request, fixture, context.Players) is { } inspectRelicUiFailure)
            {
                return inspectRelicUiFailure;
            }

            if (await ApplyAuthoredEndedTurnsAsync(request, context.Players, logStream) is { } endedTurnFailure)
            {
                return endedTurnFailure;
            }

            ApplyAuthoredTransientEffects(fixture, logStream);
            if (await ApplyAuthoredHandSelectionAsync(request, fixture, context.Players) is { } handSelectionFailure)
            {
                return handSelectionFailure;
            }

            if (fixture.Selection is not null)
            {
                if (fixture.Ui?.HandSelection is not null)
                {
                    return InvalidFixture(request, "run.view.handSelection", "present", "A card-choice overlay blocks in-hand selection input; author one or the other.");
                }

                var overlayOwner = context.Players.First().Player;
                CardModel CreateOwned(CardModel card) => overlayOwner.Creature.CombatState.CreateCard(card, overlayOwner);
                switch (fixture.Selection.Screen)
                {
                    case Sts2SupportedScreenIds.SimpleCardSelectionScreenId:
                        var simpleCards = ResolveCardModels(request, "run.players[].overlays[].simpleCardSelection.cards", fixture.Selection.Cards, allowEmpty: false, out var simpleInvalid);
                        if (simpleInvalid is not null) return simpleInvalid;
                        ShowSimpleCardSelection(simpleCards!.Select(CreateOwned).ToList(), CreateSelectorPrefs(fixture.Selection));
                        break;
                    case Sts2SupportedScreenIds.BundleSelectionScreenId:
                        var bundles = ResolveBundleModels(request, fixture.Selection.Bundles, out var bundleInvalid);
                        if (bundleInvalid is not null) return bundleInvalid;
                        ShowBundleSelection(bundles!.Select(bundle => (IReadOnlyList<CardModel>)bundle.Select(CreateOwned).ToList()).ToList());
                        break;
                    default:
                        var overlayCards = ResolveCardModels(request, "run.players[].overlays[].cards", fixture.Selection.Cards, allowEmpty: false, out var selectionInvalid);
                        if (selectionInvalid is not null) return selectionInvalid;
                        ShowChooseACardSelection(overlayCards!.Select(CreateOwned).ToList(), fixture.Selection.CanSkip ?? false);
                        break;
                }

                await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
            }

            var screen = screenLocator.Locate();
            logStream.Write(BridgeLogLevel.Info, "bridge.fixture", $"Loaded fixture '{fixture.Name}' into encounter '{encounter.Id.Entry}'.");
            return FixtureLoadResult.Success(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                screenType: screen.ScreenType,
                screenTitle: screen.ScreenTitle,
                screenInstanceId: screen.ScreenInstanceId,
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, fixture.Perspective.PlayerId, UsesDefault: false),
                recipeReport: BuildRecipeReport(fixture, AppliedCombatFields(fixture, context.Players, encounter), [], [], [], BuildBridgeValidation("passed", "screen", screen.ScreenType)),
                notices: [new FixtureLoadNotice(Code: "fixture.loaded", Message: $"Loaded encounter '{encounter.Id.Entry}' for fixture '{fixture.Name}'.")]);
        }

        private async Task<FixtureLoadResult> LoadSelectionAsync(SelectionFixtureInput input)
        {
            var (context, invalid) = await createRunContext(
                input.Request,
                input.Fixture,
                input.Fixture.Screen);
            if (context is null)
            {
                return invalid!;
            }

            if (input.Fixture.Selection is null)
            {
                return InvalidFixture(
                    input.Request,
                    "run.players[].overlays[]",
                    string.Empty,
                    "Provide selection for card-selection family fixtures.");
            }

            var cards = ResolveCardModels(
                input.Request,
                "run.players[].overlays[].cards",
                input.Fixture.Selection.Cards,
                allowEmpty: true,
                out invalid);
            if (invalid is not null)
            {
                return invalid;
            }

            switch (input.Fixture.Screen)
            {
                case Sts2SupportedScreenIds.CardRewardSelectionScreenId:
                case Sts2SupportedScreenIds.ChooseACardSelectionScreenId:
                    ShowChooseACardSelection(cards!, input.Fixture.Selection.CanSkip ?? false);
                    break;
                case Sts2SupportedScreenIds.BundleSelectionScreenId:
                    {
                        var bundles = ResolveBundleModels(input.Request, input.Fixture.Selection.Bundles, out invalid);
                        if (invalid is not null)
                        {
                            return invalid;
                        }

                        ShowBundleSelection(bundles!);
                        break;
                    }
                case Sts2SupportedScreenIds.SimpleCardSelectionScreenId:
                    ShowSimpleCardSelection(cards!, CreateSelectorPrefs(input.Fixture.Selection));
                    break;
                case Sts2SupportedScreenIds.DeckCardSelectionScreenId:
                case Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId:
                case Sts2SupportedScreenIds.DeckTransformSelectionScreenId:
                case Sts2SupportedScreenIds.DeckEnchantSelectionScreenId:
                    ShowDeckCardSelection(
                        input.Fixture.Screen,
                        cards!,
                        CreateSelectorPrefs(input.Fixture.Selection),
                        context.RunState);
                    break;
                default:
                    return InvalidFixture(
                        input.Request,
                        "screen",
                        input.Fixture.Screen,
                        "Unsupported selection fixture screen.");
            }

            var locatedScreen = screenLocator.Locate();
            return FixtureLoadResult.Success(
                requestId: input.Request.RequestId,
                fixtureName: input.Request.FixtureName,
                sourcePath: input.Request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                screenType: locatedScreen.ScreenType,
                screenTitle: locatedScreen.ScreenTitle,
                screenInstanceId: locatedScreen.ScreenInstanceId,
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, input.Fixture.Perspective.PlayerId, UsesDefault: false),
                recipeReport: BuildRecipeReport(input.Fixture, AppliedSelectionFields(input.Fixture.Selection), [], UnsupportedJsonFields("run.players[].overlays[]", input.Fixture.Selection.ExtraFields), [], BuildBridgeValidation("passed", "run.players[].overlays[]", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                        Code: "fixture.loaded",
                        Message: $"Loaded {input.Fixture.Screen} fixture '{input.Fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadOverlayAsync(OverlayFixtureInput input)
        {
            var request = input.Request;
            var fixture = input.Fixture;
            if (fixture.Overlay is null)
            {
                return InvalidFixture(request, "run.players[].overlays[].cardOverlay", string.Empty, "Provide overlay for card-overlay fixtures.");
            }

            var cards = ResolveCardModels(request, "run.players[].overlays[].cardOverlay.cards", fixture.Overlay.Cards, allowEmpty: false, out var invalid);
            if (invalid is not null)
            {
                return invalid;
            }

            var sourceScreen = string.IsNullOrWhiteSpace(fixture.Overlay.SourceScreen)
                ? Sts2SupportedScreenIds.MapScreenId
                : fixture.Overlay.SourceScreen;
            var sourceFixture = new FixtureDocument
            {
                Screen = sourceScreen,
                Name = fixture.Name,
                Perspective = fixture.Perspective,
                Run = fixture.Run,
                Players = fixture.Players,
                Room = fixture.Room,
            };
            var (context, contextInvalid) = await createRunContext(request, sourceFixture, fixture.Screen);
            if (context is null)
            {
                return contextInvalid!;
            }

            if (Sts2SupportedScreenIds.IsShopScreenType(sourceScreen))
            {
                await context.RunManager.EnterRoom(new MerchantRoom());
            }
            else
            {
                await context.RunManager.EnterRoom(new MapRoom());
            }

            var locatedScreen = screenLocator.Locate();
            var unsupported = new List<FixtureRecipeFieldReport>
            {
                Report("run.players[].overlays[].cardOverlay.policy", fixture.Overlay.Policy, "unsupported-hidden-overlay-state", "The live loader records the requested overlay policy but does not patch transient overlay stack internals."),
                Report("run.players[].overlays[].cardOverlay.sourceScreen", sourceScreen, "source-screen-breadcrumb", "The live loader enters a deterministic observable source screen and reports overlay breadcrumbs instead of fabricating overlay UI state."),
            };
            unsupported.AddRange(UnsupportedJsonFields("run.players[].overlays[].cardOverlay", fixture.Overlay.ExtraFields));

            return FixtureLoadResult.Success(
                requestId: request.RequestId,
                fixtureName: request.FixtureName,
                sourcePath: request.SourcePath,
                source: DataSourceKind.Live,
                provisional: false,
                screenType: locatedScreen.ScreenType,
                screenTitle: locatedScreen.ScreenTitle,
                screenInstanceId: locatedScreen.ScreenInstanceId,
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, fixture.Perspective.PlayerId, UsesDefault: false),
                recipeReport: BuildRecipeReport(
                    fixture,
                    cards!.Select((card, index) => Report($"run.players[].overlays[].cardOverlay.cards[{index}].modelId", card.Id.Entry, "bridge-validated-card-id", "Resolved authored observable card id through the installed STS2 model database.")).ToArray(),
                    [Report("run.players[].overlays[].cardOverlay.sourceScreen", sourceScreen, "inferred-source-screen", "Entered the deterministic source screen used as the overlay breadcrumb.")],
                    unsupported,
                    [],
                    BuildBridgeValidation("partial", "cardOverlay", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                        Code: "fixture.loaded",
                        Message: $"Loaded bounded {fixture.Screen} fixture '{fixture.Name}' through source screen '{sourceScreen}'."),
                ]);
        }

    }

    private abstract record CombatSelectionFixtureInput(
        FixtureLoadRequestSnapshot Request,
        FixtureDocument Fixture);

    private delegate Task<(SinglePlayerFixtureContext? Context, FixtureLoadResult? Invalid)> FixtureRunContextFactory(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenType);

    private delegate Task<(SinglePlayerFixtureContext? Context, FixtureLoadResult? Invalid)> SinglePlayerRunContextFactory(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture);

    private delegate Task<(HostLocalFixtureContext? Context, FixtureLoadResult? Invalid)> HostLocalRunContextFactory(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        string screenType);

    private delegate bool EventRoomRecipeValidator(
        FixtureLoadRequestSnapshot request,
        FixtureEventRoom? eventRoom,
        string playerId,
        out FixtureLoadResult? invalid);

    private delegate bool OpenedTreasureRecipeApplier(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        RunManager runManager,
        RunState runState,
        out FixtureLoadResult? invalid);

    private delegate Task<(bool Success, FixtureRecipeFieldReport? Report, FixtureLoadResult? Invalid)> AncientDialogueApplier(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        EventModel eventModel);

    private delegate Task<(bool Success, FixtureRecipeFieldReport? Report, FixtureLoadResult? Invalid)> CrystalSphereOptionApplier(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture,
        EventModel eventModel);

    private delegate Task<(bool Success, IReadOnlyList<FixtureRecipeFieldReport> Reports, FixtureLoadResult? Invalid)> CrystalSphereStateApplier(
        FixtureLoadRequestSnapshot request,
        FixtureDocument fixture);

    private delegate bool ShopRecipeApplier(
        FixtureLoadRequestSnapshot request,
        Player player,
        FixtureShop? shop,
        out FixtureLoadResult? invalid);

    private sealed record CombatFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture)
        : CombatSelectionFixtureInput(Request, Fixture);

    private sealed record SelectionFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture)
        : CombatSelectionFixtureInput(Request, Fixture);

    private sealed record OverlayFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture)
        : CombatSelectionFixtureInput(Request, Fixture);

    private sealed partial class RoomMapFixtureFamily(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream,
        FixtureRunContextFactory createRunContext,
        SinglePlayerRunContextFactory createSinglePlayerRunContext,
        HostLocalRunContextFactory createHostRunContext,
        EventRoomRecipeValidator validateEventRoomRecipe,
        OpenedTreasureRecipeApplier applyOpenedTreasureRoomRecipe,
        AncientDialogueApplier applyAncientDialogue,
        CrystalSphereOptionApplier applyCrystalSphereChosenOption,
        CrystalSphereStateApplier applyCrystalSphereState,
        ShopRecipeApplier applyShopRecipe)
    {
        private readonly Sts2ScreenLocator _screenLocator = screenLocator;
        private readonly ILogStream _logStream = logStream;
        private readonly FixtureRunContextFactory _createRunContext = createRunContext;
        private readonly SinglePlayerRunContextFactory _createSinglePlayerRunContext = createSinglePlayerRunContext;
        private readonly HostLocalRunContextFactory _createHostRunContext = createHostRunContext;
        private readonly EventRoomRecipeValidator _validateEventRoomRecipe = validateEventRoomRecipe;
        private readonly OpenedTreasureRecipeApplier _applyOpenedTreasureRoomRecipe = applyOpenedTreasureRoomRecipe;
        private readonly AncientDialogueApplier _applyAncientDialogue = applyAncientDialogue;
        private readonly CrystalSphereOptionApplier _applyCrystalSphereChosenOption = applyCrystalSphereChosenOption;
        private readonly CrystalSphereStateApplier _applyCrystalSphereState = applyCrystalSphereState;
        private readonly ShopRecipeApplier _applyShopRecipe = applyShopRecipe;
        public bool TryCreateInput(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture,
            out RoomMapFixtureInput input)
        {
            if (Sts2SupportedScreenIds.IsMapScreenType(fixture.Screen))
            {
                input = new MapFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsRewardsScreenType(fixture.Screen))
            {
                input = new RewardsFixtureInput(request, fixture);
                return true;
            }

            // A game-over recipe owns the visible terminal screen regardless of the derived
            // underlying room screen.
            if (fixture.GameOver is not null)
            {
                input = new GameOverFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsRestSiteScreenType(fixture.Screen))
            {
                input = new RestSiteFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsEventRoomScreenType(fixture.Screen)
                || Sts2SupportedScreenIds.IsCrystalSphereScreenType(fixture.Screen))
            {
                input = new EventRoomFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsTreasureRoomFamily(fixture.Screen))
            {
                input = new TreasureFixtureInput(request, fixture);
                return true;
            }

            if (Sts2SupportedScreenIds.IsShopScreenType(fixture.Screen))
            {
                input = new ShopFixtureInput(request, fixture);
                return true;
            }

            input = default!;
            return false;
        }

        public Task<FixtureLoadResult> LoadAsync(RoomMapFixtureInput input)
            => input switch
            {
                MapFixtureInput map => LoadMapAsync(map.Request, map.Fixture),
                RewardsFixtureInput rewards => LoadRewardsAsync(rewards.Request, rewards.Fixture),
                GameOverFixtureInput gameOver => LoadGameOverAsync(gameOver.Request, gameOver.Fixture),
                RestSiteFixtureInput restSite => LoadRestSiteAsync(restSite.Request, restSite.Fixture),
                EventRoomFixtureInput eventRoom => LoadEventRoomAsync(eventRoom.Request, eventRoom.Fixture),
                TreasureFixtureInput treasure => LoadTreasureFamilyAsync(treasure.Request, treasure.Fixture),
                ShopFixtureInput shop => LoadShopAsync(shop.Request, shop.Fixture),
                _ => throw new ArgumentOutOfRangeException(nameof(input)),
            };
    }

    private abstract record RoomMapFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture);
    private sealed record MapFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record RewardsFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record GameOverFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record RestSiteFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record EventRoomFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record TreasureFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
    private sealed record ShopFixtureInput(FixtureLoadRequestSnapshot Request, FixtureDocument Fixture) : RoomMapFixtureInput(Request, Fixture);
}
