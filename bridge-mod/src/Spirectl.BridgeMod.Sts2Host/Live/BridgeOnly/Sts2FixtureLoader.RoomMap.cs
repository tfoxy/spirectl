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

namespace Spirectl.Sts2.Live;


public sealed partial class Sts2FixtureLoader
{
    private sealed partial class RoomMapFixtureFamily
    {
        private async Task<FixtureLoadResult> LoadMapAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            var (context, invalid) = await _createRunContext(request, fixture, Sts2SupportedScreenIds.MapScreenId);
            if (context is null)
            {
                return invalid!;
            }

            // Seed the current map point so ActFloor and the top-bar room icon resolve (the
            // fixture never navigates a generated map). Unlike the rewards loader, keep the
            // map screen open — the map screen is the intended view here. When the fixture authors
            // mapRoom.travelToRow, auto-walk a real connected path that deep instead, so
            // run.visitedMapCoords covers a walked route (the traveled-path coloring then renders).
            // See PrepareRestoreMapPointHistory / ApplyAuthoredMapProgression.
            // Author the start (row-0) node kind. "ancient" = the Neow node; anything else = a Monster
            // combat node (a profile's first run). Drive the game's StartedWithNeow flag to match so its own
            // map setup never re-stamps the start node Monster (RunManager only does so when !StartedWithNeow
            // && act 0). NAncientMapPoint draws the act's own Ancient model, so no per-node event is needed.
            var startsWithAncient = string.Equals(fixture.Room.FirstNode, "ancient", StringComparison.OrdinalIgnoreCase);

            if (fixture.Room.TravelPath is { Count: > 0 } travelPath)
            {
                // A typed traveled path fully controls the visited nodes' types (incl. revealed `?`); its
                // first entry sets the start node, so honor that for the StartedWithNeow flag too.
                if (TryParseTravelPathEntry(travelPath[0], out var firstPointType, out _))
                {
                    startsWithAncient = firstPointType == MapPointType.Ancient;
                }

                context.RunState.ExtraFields.StartedWithNeow = startsWithAncient;
                if (ApplyAuthoredMapTravelPath(request, context.RunState, travelPath) is { } invalidPath)
                {
                    return invalidPath;
                }
            }
            else
            {
                context.RunState.ExtraFields.StartedWithNeow = startsWithAncient;
                var startPointType = startsWithAncient ? MapPointType.Ancient : MapPointType.Monster;
                var startRoomType = startsWithAncient ? RoomType.Event : RoomType.Monster;

                if (fixture.Room.TravelToRow is { } travelToRow && travelToRow > 0)
                {
                    if (ApplyAuthoredMapProgression(request, context.RunState, startPointType, startRoomType, travelToRow) is { } progressionInvalid)
                    {
                        return progressionInvalid;
                    }
                }
                else
                {
                    PrepareRestoreMapPointHistory(
                        context.RunState,
                        startPointType,
                        startRoomType,
                        roomModelId: null,
                        fixture.Run.Floor);
                }
            }

            await context.RunManager.EnterRoom(new MapRoom());

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into the map screen.");

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
                recipeReport: BuildRecipeReport(fixture, [], [], [], [], BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded map fixture '{fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadRewardsAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            // Reset any stale non-host captured rewards from a prior load before (re)generating.
            Sts2RewardCaptureRegistry.ClearAll();

            // Couch-coop: more than one authored seat. The local "me" seat gets the native rewards screen;
            // each non-host host-local seat gets a generated RewardsSet that Sts2RewardsCaptureHooks captures
            // and surfaces (no native screen for them, exactly like live couch play).
            if (fixture.Players.Count > 1)
            {
                return await LoadCoopRewardsAsync(request, fixture);
            }

            var (context, invalid) = await _createRunContext(request, fixture, Sts2SupportedScreenIds.RewardsScreenId);
            if (context is null)
            {
                return invalid!;
            }

            // Resolve the synthetic post-combat room type the authored rewards follow.
            // It drives the top-bar room icon and, more importantly, becomes the `room`
            // argument Hook.ModifyRewards passes to granted relics' TryModifyRewards, so
            // reward-modifying relics (AmethystAubergine/BlackStar/WhiteStar/PrayerWheel)
            // actually fire against the authored rewards. Defaults to Elite, which fires
            // the most add-reward relics.
            var rewardsSpec = fixture.Rewards;
            if (!TryResolveRewardsRoomType(request, rewardsSpec?.RoomType, out var roomType, out var roomTypeInvalid))
            {
                return roomTypeInvalid!;
            }

            if (!TryResolveRewardsEncounter(request, rewardsSpec?.EncounterId, roomType, out var encounter, out var encounterInvalid))
            {
                return encounterInvalid!;
            }

            var mapPointType = ResolveCombatMapPointType(roomType);

            // Seed the current map point so the top-bar room icon resolves and ActFloor is
            // set (the fixture never navigates a generated map). See PrepareRestoreMapPointHistory.
            PrepareRestoreMapPointHistory(
                context.RunState,
                mapPointType,
                roomType,
                roomModelId: null,
                fixture.Run.Floor);

            await context.RunManager.EnterRoom(new MapRoom());

            // EnterRoom(MapRoom) opens NMapScreen, and the game resolves the map before the
            // overlay stack while it's open, so the rewards overlay pushed below would never
            // be the visible/focused screen. Close the map immediately (no animation, mirroring
            // CloseLeftoverInspectRelicOverlay) before pushing the rewards overlay.
            NMapScreen.Instance?.Close(animateOut: false);

            // Grant authored relics before generating rewards so their reward hooks are
            // registered when RewardsSet.GenerateWithoutOffering runs Hook.ModifyRewards.
            if (fixture.Players.Any(player => player.RelicIds is { Count: > 0 } || player.DeckCards is { Count: > 0 }))
            {
                var livePlayersByFixtureId = new Dictionary<string, Player>
                {
                    [fixture.Players[0].Id] = context.Player,
                };
                if (await ApplyAuthoredPlayerGrantsAsync(request, fixture, context.RunState, livePlayersByFixtureId) is { } grantInvalid)
                {
                    return grantInvalid;
                }
            }

            // A finished CombatRoom of the resolved type. Not entered (the player stays
            // on the rewards overlay over the closed map) — it exists purely as the room
            // context RewardsSet.EmptyForRoom passes to the relic reward hooks.
            var combatRoom = AbstractRoom.FromSerializable(
                new SerializableRoom
                {
                    RoomType = roomType,
                    EncounterId = encounter.Id,
                    IsPreFinished = true,
                    GoldProportion = 0f,
                },
                context.RunState);
            if (combatRoom is null)
            {
                return RuntimeFailure(
                    request,
                    "run.players[].overlays[].rewards.roomType",
                    $"Could not materialize a {roomType} reward room for encounter '{encounter.Id}'.");
            }

            var (rewards, rewardsInvalid) = BuildAuthoredRewards(request, rewardsSpec, context.Player);
            if (rewards is null)
            {
                return rewardsInvalid!;
            }

            var finalRewards = new RewardsSet(context.Player)
                .EmptyForRoom(combatRoom)
                .WithCustomRewards(rewards);
            await finalRewards.GenerateWithoutOffering();
            NRewardsScreen.ShowScreen(finalRewards, isTerminal: false, context.RunState);

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into the rewards screen.");

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
                recipeReport: BuildRecipeReport(fixture, [], [], [], [], BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded rewards fixture '{fixture.Name}'."),
                ]);
        }

        // Run-terminal combat defeat: stand up a finished single-player run, attach a
        // combat-death RunHistory, and push the real game-over overlay via the game's own
        // NRun.ShowGameOverScreen (which closes the capstone and pushes NGameOverScreen
        // onto NOverlayStack). The dead local player is expressed with creature.currentHp: 0.
        private async Task<FixtureLoadResult> LoadGameOverAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            var gameOver = fixture.GameOver;
            if (gameOver is null)
            {
                return InvalidFixture(
                    request,
                    "run.gameOver",
                    "missing",
                    "run.gameOver is required to load the game-over screen.");
            }

            if (gameOver.Win)
            {
                return InvalidFixture(
                    request,
                    "run.gameOver.win",
                    "true",
                    "Only combat defeat (win: false) is supported by the live game-over loader.");
            }

            if (string.IsNullOrWhiteSpace(gameOver.KilledByEncounter))
            {
                return InvalidFixture(
                    request,
                    "run.gameOver.killedByEncounter",
                    "missing",
                    "Name the combat encounter that defeated the party (e.g. NIBBITS_NORMAL).");
            }

            if (!Sts2ModelResolver.TryResolveFixtureEncounter(gameOver.KilledByEncounter, out var encounter))
            {
                return InvalidFixture(
                    request,
                    "run.gameOver.killedByEncounter",
                    gameOver.KilledByEncounter,
                    "Use an encounter id exposed by the installed STS2 content, such as NIBBITS_NORMAL.");
            }

            if (!encounter.RoomType.IsCombatRoom())
            {
                return InvalidFixture(
                    request,
                    "run.gameOver.killedByEncounter",
                    gameOver.KilledByEncounter,
                    "killedByEncounter must resolve to a combat encounter (combat death).");
            }

            var (context, invalid) = await _createRunContext(request, fixture, Sts2SupportedScreenIds.GameOverScreenId);
            if (context is null)
            {
                return invalid!;
            }

            // Seed the current map point so the finished run carries a combat room at the
            // authored floor (RunManager.ToSave / RunHistory read MapPointHistory, and the
            // top-bar icon resolves from CurrentMapPoint.PointType). See PrepareRestoreMapPointHistory.
            var mapPointType = ResolveCombatMapPointType(encounter.RoomType);
            PrepareRestoreMapPointHistory(
                context.RunState,
                mapPointType,
                encounter.RoomType,
                roomModelId: encounter.Id,
                fixture.Run.Floor);

            // EnterRoom(MapRoom) opens NMapScreen, which the game resolves ahead of the
            // overlay stack while open. Close it (no animation) so the pushed game-over
            // overlay is the visible/focused screen. Mirrors LoadRewardsFixture.
            await context.RunManager.EnterRoom(new MapRoom());
            NMapScreen.Instance?.Close(animateOut: false);

            // Build the combat-death history BEFORE showing the screen: NGameOverScreen._Ready
            // reads RunManager.Instance.History synchronously to pick the loss banner/quote and
            // derive the GameOverType. KilledByEncounter != none + Win == false => CombatDeath.
            // Construct it directly (deterministic encounter; no SaveManager.SaveRunHistory disk
            // write) rather than via RunHistoryUtilities.CreateRunHistoryEntry.
            var serializableRun = context.RunManager.ToSave(null);
            context.RunManager.History = new RunHistory
            {
                Win = false,
                WasAbandoned = false,
                KilledByEncounter = encounter.Id,
                Ascension = context.RunState.AscensionLevel,
                MapPointHistory = serializableRun.MapPointHistory,
            };

            if (NRun.Instance is not { } run)
            {
                return RuntimeFailure(
                    request,
                    "run",
                    "NRun.Instance was null while preparing the game-over screen.");
            }

            run.ShowGameOverScreen(serializableRun);

            await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into the game-over (defeat) screen.");

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
                recipeReport: BuildRecipeReport(fixture, [], [], [], [], BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded combat-defeat fixture '{fixture.Name}'."),
                ]);
        }

        // Couch-coop rewards: a multi-seat run where the local "me" seat opens the native rewards screen and
        // each non-host host-local seat gets a generated RewardsSet (captured + surfaced by
        // Sts2RewardsCaptureHooks, since the host never builds reward buttons for non-local players). Every
        // seat receives its own copy of the authored rewards spec. Mirrors LoadRewardsFixture's setup.
        private async Task<FixtureLoadResult> LoadCoopRewardsAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            var (context, invalid) = await _createHostRunContext(request, fixture, Sts2SupportedScreenIds.RewardsScreenId);
            if (context is null)
            {
                return invalid!;
            }

            var rewardsSpec = fixture.Rewards;
            if (!TryResolveRewardsRoomType(request, rewardsSpec?.RoomType, out var roomType, out var roomTypeInvalid))
            {
                return roomTypeInvalid!;
            }

            if (!TryResolveRewardsEncounter(request, rewardsSpec?.EncounterId, roomType, out var encounter, out var encounterInvalid))
            {
                return encounterInvalid!;
            }

            var mapPointType = ResolveCombatMapPointType(roomType);
            PrepareRestoreMapPointHistory(context.RunState, mapPointType, roomType, roomModelId: null, fixture.Run.Floor);
            await context.RunManager.EnterRoom(new MapRoom());
            NMapScreen.Instance?.Close(animateOut: false);

            if (fixture.Players.Any(player => player.RelicIds is { Count: > 0 } || player.DeckCards is { Count: > 0 }))
            {
                var livePlayersByFixtureId = context.Players.ToDictionary(player => player.FixtureId, player => player.Player);
                if (await ApplyAuthoredPlayerGrantsAsync(request, fixture, context.RunState, livePlayersByFixtureId) is { } grantInvalid)
                {
                    return grantInvalid;
                }
            }

            var combatRoom = AbstractRoom.FromSerializable(
                new SerializableRoom
                {
                    RoomType = roomType,
                    EncounterId = encounter.Id,
                    IsPreFinished = true,
                    GoldProportion = 0f,
                },
                context.RunState);
            if (combatRoom is null)
            {
                return RuntimeFailure(
                    request,
                    "run.players[].overlays[].rewards.roomType",
                    $"Could not materialize a {roomType} reward room for encounter '{encounter.Id}'.");
            }

            foreach (var seat in context.Players)
            {
                // True remote peers resolve rewards through their own client; only the local seat + host-local
                // (couch) seats are generated on this engine.
                if (!seat.IsLocal && !seat.IsHostLocalSeat)
                {
                    continue;
                }

                var (rewards, rewardsInvalid) = BuildAuthoredRewards(request, rewardsSpec, seat.Player);
                if (rewards is null)
                {
                    return rewardsInvalid!;
                }

                var finalRewards = new RewardsSet(seat.Player)
                    .EmptyForRoom(combatRoom)
                    .WithCustomRewards(rewards);
                await finalRewards.GenerateWithoutOffering();

                // Every couch seat is locally controlled (IsLocal == true); the LocalContext "me" seat is the
                // primary one (index 0, IsHostLocalSeat == false) and keeps the live NRewardsScreen — state
                // reads its buttons. The host-local extra seats were captured by Sts2RewardsCaptureHooks during
                // GenerateWithoutOffering above (no native screen, exactly like live couch play).
                if (!seat.IsHostLocalSeat)
                {
                    NRewardsScreen.ShowScreen(finalRewards, isTerminal: false, context.RunState);
                }
            }

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded coop rewards fixture '{fixture.Name}' across {context.Players.Count} seats.");

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
                recipeReport: BuildRecipeReport(fixture, [], [], [], [], BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded coop rewards fixture '{fixture.Name}'."),
                ]);
        }

        private static bool TryResolveRewardsRoomType(
            FixtureLoadRequestSnapshot request,
            string? authoredRoomType,
            out RoomType roomType,
            out FixtureLoadResult? invalid)
        {
            invalid = null;
            // Elite fires the most add-reward relics (AmethystAubergine + BlackStar +
            // WhiteStar) alongside the room-agnostic card-level relics.
            if (string.IsNullOrWhiteSpace(authoredRoomType))
            {
                roomType = RoomType.Elite;
                return true;
            }

            switch (authoredRoomType.Trim().ToLowerInvariant())
            {
                case "monster":
                    roomType = RoomType.Monster;
                    return true;
                case "elite":
                    roomType = RoomType.Elite;
                    return true;
                case "boss":
                    roomType = RoomType.Boss;
                    return true;
                default:
                    roomType = RoomType.Elite;
                    invalid = InvalidFixture(
                        request,
                        "run.players[].overlays[].rewards.roomType",
                        authoredRoomType,
                        "Use roomType: monster, elite, or boss.");
                    return false;
            }
        }

        private static bool TryResolveRewardsEncounter(
            FixtureLoadRequestSnapshot request,
            string? authoredEncounterId,
            RoomType roomType,
            out EncounterModel encounter,
            out FixtureLoadResult? invalid)
        {
            invalid = null;
            if (!string.IsNullOrWhiteSpace(authoredEncounterId))
            {
                if (!Sts2ModelResolver.TryResolveFixtureEncounter(authoredEncounterId, out encounter))
                {
                    encounter = null!;
                    invalid = InvalidFixture(
                        request,
                        "run.players[].overlays[].rewards.encounterId",
                        authoredEncounterId,
                        "Use an encounter id from the installed STS2 content.");
                    return false;
                }

                return true;
            }

            // Default to the first installed encounter of the resolved room type so the
            // CombatRoom materializes without the author naming an encounter.
            var resolved = ModelDb.AllEncounters.FirstOrDefault(candidate => candidate.RoomType == roomType);
            if (resolved is null)
            {
                encounter = null!;
                invalid = RuntimeFailure(
                    request,
                    "run.players[].overlays[].rewards.roomType",
                    $"No installed {roomType} encounter is available to seed the reward room; set rewards.encounterId explicitly.");
                return false;
            }

            encounter = resolved;
            return true;
        }

        // Builds the authored reward list. Returns (null, invalid) when an authored id
        // does not resolve. An empty/absent item list falls back to the legacy single
        // gold reward so the checked-in basic-rewards fixture keeps loading.
        private (List<Reward>? Rewards, FixtureLoadResult? Invalid) BuildAuthoredRewards(
            FixtureLoadRequestSnapshot request,
            FixtureRewards? spec,
            Player player)
        {
            var rewards = new List<Reward>();
            if (spec is null || spec.Items.Count == 0)
            {
                rewards.Add(new GoldReward(30, player));
                return (rewards, null);
            }

            for (var index = 0; index < spec.Items.Count; index++)
            {
                var (reward, invalid) = BuildAuthoredReward(
                    request,
                    spec.Items[index],
                    spec.RoomType,
                    player,
                    $"run.players[].overlays[].rewards.items[{index}]");
                if (reward is null)
                {
                    return (null, invalid);
                }

                rewards.Add(reward);
            }

            return (rewards, null);
        }

        private (Reward? Reward, FixtureLoadResult? Invalid) BuildAuthoredReward(
            FixtureLoadRequestSnapshot request,
            FixtureRewardItem item,
            string? overlayRoomType,
            Player player,
            string field)
        {
            if (item.Gold is { } gold)
            {
                return (new GoldReward((int)gold, player), null);
            }

            if (item.Card is { } card)
            {
                // Both forms resolve a room type for the base CardCreationOptions: the
                // authored card.roomType, else the overlay room type (default elite).
                if (!TryResolveRewardsRoomType(request, card.RoomType ?? overlayRoomType, out var poolRoomType, out var poolInvalid))
                {
                    return (null, poolInvalid);
                }

                if (card.ModelIds.Count > 0)
                {
                    var cards = ResolveCardModels(request, $"{field}.card.modelIds", card.ModelIds, allowEmpty: false, out var cardInvalid);
                    if (cardInvalid is not null)
                    {
                        return (null, cardInvalid);
                    }

                    // Offer exactly the authored cards (any rarity, e.g. include a rare
                    // card model for a "card with rare rarity" reward). The live CardReward
                    // dropped its specific-cards constructor, so replicate its manual-set
                    // path: build the run-bound card results and flip _cardsWereManuallySet
                    // so Populate offers them verbatim while still running the card-reward
                    // relic hooks (eggs/enchants) over them.
                    var offered = cards!
                        .Select(model => new CardCreationResult(player.RunState.CreateCard(model, player)))
                        .ToList();
                    var manualReward = new CardReward(CardCreationOptions.ForRoom(player, poolRoomType), offered.Count, player);
                    if (!Sts2LiveIntrospection.TrySetMemberValue(manualReward, "_cards", offered)
                        || !Sts2LiveIntrospection.TrySetMemberValue(manualReward, "_cardsWereManuallySet", true))
                    {
                        return (null, RuntimeFailure(request, $"{field}.card.modelIds", "Could not set the authored card-reward options on the live CardReward."));
                    }

                    ApplyCardRewardSkippable(manualReward, card.CanSkip);
                    return (manualReward, null);
                }

                // Pooled choice: room pool + option count (defaults to 3, mirroring the
                // game's standard combat card reward).
                var count = card.Count is { } authoredCount and > 0 ? authoredCount : 3;
                var pooledReward = new CardReward(CardCreationOptions.ForRoom(player, poolRoomType), count, player);
                ApplyCardRewardSkippable(pooledReward, card.CanSkip);
                return (pooledReward, null);
            }

            if (item.SpecialCardModelId is { } specialCardId)
            {
                if (!Sts2ModelResolver.TryResolveFixtureCard(specialCardId, out var specialCard))
                {
                    return (null, InvalidFixture(request, $"{field}.specialCard.modelId", specialCardId, "Use a card id from the installed STS2 content."));
                }

                // SpecialCardReward holds a run-bound card instance (game call sites use
                // RunState.CreateCard / a deck version), so mint one rather than a bare
                // mutable model.
                return (new SpecialCardReward(player.RunState.CreateCard(specialCard, player), player), null);
            }

            if (item.Potion is { } potion)
            {
                if (string.IsNullOrWhiteSpace(potion.ModelId))
                {
                    return (new PotionReward(player), null);
                }

                if (!Sts2ModelResolver.TryResolveFixturePotion(potion.ModelId, out var potionModel))
                {
                    return (null, InvalidFixture(request, $"{field}.potion.modelId", potion.ModelId, "Use a potion id from the installed STS2 content."));
                }

                return (new PotionReward(potionModel.ToMutable(), player), null);
            }

            if (item.Relic is { } relic)
            {
                if (!string.IsNullOrWhiteSpace(relic.ModelId))
                {
                    if (!Sts2ModelResolver.TryResolveFixtureRelic(relic.ModelId, out var relicModel))
                    {
                        return (null, InvalidFixture(request, $"{field}.relic.modelId", relic.ModelId, "Use a relic id from the installed STS2 content."));
                    }

                    return (new RelicReward(relicModel.ToMutable(), player), null);
                }

                if (!string.IsNullOrWhiteSpace(relic.Rarity))
                {
                    if (!Enum.TryParse<RelicRarity>(relic.Rarity, ignoreCase: true, out var rarity)
                        || rarity == RelicRarity.None)
                    {
                        return (null, InvalidFixture(request, $"{field}.relic.rarity", relic.Rarity, "Use a relic rarity: common, uncommon, rare, shop, event, or ancient."));
                    }

                    return (new RelicReward(rarity, player), null);
                }

                return (new RelicReward(player), null);
            }

            if (item.CardRemoval)
            {
                return (new CardRemovalReward(player), null);
            }

            if (item.Linked is { } linkedItems)
            {
                var nested = new List<Reward>();
                for (var index = 0; index < linkedItems.Count; index++)
                {
                    var (childReward, childInvalid) = BuildAuthoredReward(
                        request,
                        linkedItems[index],
                        overlayRoomType,
                        player,
                        $"{field}.linked.items[{index}]");
                    if (childReward is null)
                    {
                        return (null, childInvalid);
                    }

                    nested.Add(childReward);
                }

                return (new LinkedRewardSet(nested, player), null);
            }

            return (null, InvalidFixture(request, field, "{}", "Each reward item must author exactly one of gold, card, specialCard, potion, relic, cardRemoval, or linked."));
        }

        // CardReward.CanSkip is init-only; reflect to honor an authored canSkip:false so
        // the Skip alternative is dropped (the game caps card-reward alternatives at 2).
        private static void ApplyCardRewardSkippable(CardReward reward, bool? canSkip)
        {
            if (canSkip is false)
            {
                Sts2LiveIntrospection.TrySetMemberValue(reward, "CanSkip", false);
            }
        }

        private async Task<FixtureLoadResult> LoadRestSiteAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            // Rest-site fixtures are single-player by default, but support a host-local
            // multiplayer context (>1 player) so the multiplayer-only MEND option appears
            // (its gate is RunState.Players.Count > 1). Mirrors LoadEventRoomFixture.
            RunManager runManager;
            RunState runState;
            Player localPlayer;
            IReadOnlyDictionary<string, Player> livePlayersByFixtureId;
            if (fixture.Players.Count > 1)
            {
                if (!string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
                {
                    return InvalidFixture(request, "run.currentRoom.combat", fixture.Room.EncounterId, "Rest-site fixtures do not author a combat encounter room.");
                }

                if (fixture.Lobby is not null)
                {
                    return InvalidFixture(request, "characterSelect", fixture.Lobby.Kind, "Rest-site fixtures do not use the characterSelect lobby recipe.");
                }

                var (hostLocalContext, hostLocalInvalid) = await _createHostRunContext(request, fixture, "rest-site");
                if (hostLocalContext is null)
                {
                    return hostLocalInvalid!;
                }

                runManager = hostLocalContext.RunManager;
                runState = hostLocalContext.RunState;
                livePlayersByFixtureId = hostLocalContext.Players.ToDictionary(player => player.FixtureId, player => player.Player);
                localPlayer = (hostLocalContext.Players.FirstOrDefault(player => player.FixtureId == fixture.Perspective.PlayerId)
                    ?? hostLocalContext.Players.FirstOrDefault(player => player.IsLocal)
                    ?? hostLocalContext.Players[0]).Player;
            }
            else
            {
                var (context, invalid) = await _createRunContext(request, fixture, Sts2SupportedScreenIds.RestSiteRoomScreenId);
                if (context is null)
                {
                    return invalid!;
                }

                runManager = context.RunManager;
                runState = context.RunState;
                livePlayersByFixtureId = new Dictionary<string, Player> { [fixture.Players[0].Id] = context.Player };
                localPlayer = context.Player;
            }

            var restSiteRoom = new RestSiteRoom();
            // Seed the current map point so the top-bar room icon resolves (the fixture
            // never navigates a generated map). See PrepareRestoreMapPointHistory.
            PrepareRestoreMapPointHistory(
                runState,
                MapPointType.RestSite,
                restSiteRoom.RoomType,
                roomModelId: null,
                fixture.Run.Floor);
            await runManager.EnterRoom(restSiteRoom);

            // Grant authored relics + extra deck cards AFTER entering the room — the live
            // obtain commands (RelicCmd.Obtain / CardPileCmd.Add) need an active room/action
            // context to complete. EnterRoom already generated the per-player options
            // (HEAL/SMITH/MEND), so regenerate them via BeginRestSite once the relic/card
            // rest-site hooks are in place (SHOVEL->DIG, MEAT_CLEAVER->COOK, GIRYA->LIFT,
            // PAELS_GROWTH->CLONE, BYRDONIS_EGG->HATCH).
            var grantsApplied = fixture.Players.Any(player => player.RelicIds is { Count: > 0 } || player.DeckCards is { Count: > 0 });
            var extraOptionIds = fixture.RestSite?.ExtraOptionIds;
            if (grantsApplied || extraOptionIds is { Count: > 0 })
            {
                if (grantsApplied
                    && await ApplyAuthoredPlayerGrantsAsync(request, fixture, runState, livePlayersByFixtureId) is { } grantInvalid)
                {
                    return grantInvalid;
                }

                if (Sts2LiveIntrospection.GetMemberValue(runManager, "RestSiteSynchronizer") is { } restSiteSynchronizer)
                {
                    if (grantsApplied)
                    {
                        // Regenerate the per-player options so the granted relics'/cards'
                        // rest-site hooks contribute (SHOVEL->DIG, MEAT_CLEAVER->COOK, GIRYA->LIFT).
                        Sts2LiveIntrospection.InvokeMethod(restSiteSynchronizer, "BeginRestSite");
                    }

                    if (extraOptionIds is { Count: > 0 }
                        && InjectExtraRestSiteOptions(request, restSiteSynchronizer, localPlayer, extraOptionIds) is { } injectInvalid)
                    {
                        return injectInvalid;
                    }

                    // The live NRestSiteRoom built its option buttons during _Ready()
                    // (HEAL/SMITH/MEND), before the authored relics/extra options reached the
                    // synchronizer. NRestSiteRoom.Options is a live proxy over the synchronizer's
                    // per-player list, so rebuild the button nodes now that BeginRestSite()/
                    // InjectExtraRestSiteOptions have added the relic-granted (SHOVEL->DIG,
                    // MEAT_CLEAVER->COOK, GIRYA->LIFT) and force-injected (CLONE/HATCH) options —
                    // otherwise they appear in state but never as live campfire buttons.
                    var restSiteNode = Sts2LiveIntrospection.ResolveCurrentScreenObject();
                    if (restSiteNode is not null
                        && Sts2LiveIntrospection.IsType(restSiteNode, Sts2RestSiteScreenInspector.RestSiteRoomType))
                    {
                        Sts2LiveIntrospection.TryInvokeParameterlessMethod(restSiteNode, "UpdateRestSiteOptions");
                    }
                }

                await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
            }

            // Compose the deck-card-selection overlay (the smith/upgrade dialog) on top
            // of the campfire room when the fixture authors one — faithful to picking
            // SMITH at a rest site. Shares the overlay-composition path with the event
            // loader (see ComposeDeckSelectionOverlay).
            if (ComposeDeckSelectionOverlay(request, fixture, localPlayer, runState, "rest-site") is { } restSiteOverlayInvalid)
            {
                return restSiteOverlayInvalid;
            }

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into the rest site.");

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
                recipeReport: BuildRecipeReport(fixture, AppliedSelectionFields(fixture.Selection), [], UnsupportedJsonFields("run.players[].overlays[]", fixture.Selection?.ExtraFields), [], BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded rest-site fixture '{fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadEventRoomAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            if (!string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.combat",
                    fixture.Room.EncounterId,
                    "Remove room; event-room fixtures do not use room.encounterId.");
            }

            if (fixture.Lobby is not null)
            {
                return InvalidFixture(
                    request,
                    "characterSelect",
                    fixture.Lobby.Kind,
                    "Remove lobby; event-room fixtures do not use the lobby recipe section.");
            }

            if (fixture.EventRoom is null)
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.event",
                    string.Empty,
                    "Provide eventRoom for event-room fixtures.");
            }

            if (!Sts2ModelResolver.TryResolveFixtureEvent(fixture.EventRoom.EventId, out var eventModel))
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.event.canonicalEventModelId",
                    fixture.EventRoom.EventId,
                    "Use an event id from installed STS2 events or ancients.");
            }

            var isAncientEvent = IsAncientEventModel(eventModel);
            if (!isAncientEvent && !string.IsNullOrWhiteSpace(fixture.EventRoom.AncientDialogueId))
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.event.playerStates[].ancient.view.visibleDialogue.dialogueId",
                    fixture.EventRoom.AncientDialogueId,
                    "Use eventRoom.ancientDialogueId only with ancient event fixtures.");
            }

            RunManager runManager;
            RunState runState;
            Player localPlayer;
            IReadOnlyDictionary<string, Player> livePlayersByFixtureId;
            IReadOnlyList<HostLocalFixturePlayerRecipe> hostLocalPlayers = [];
            FixtureLoadResult? invalid;
            if (fixture.Players.Count > 1)
            {
                var (hostLocalContext, hostLocalInvalid) = await _createHostRunContext(request, fixture, "event-room");
                if (hostLocalContext is null)
                {
                    return hostLocalInvalid!;
                }

                runManager = hostLocalContext.RunManager;
                runState = hostLocalContext.RunState;
                hostLocalPlayers = hostLocalContext.Players;
                livePlayersByFixtureId = hostLocalContext.Players.ToDictionary(player => player.FixtureId, player => player.Player);
                localPlayer = (hostLocalContext.Players.FirstOrDefault(player => player.FixtureId == fixture.Perspective.PlayerId)
                    ?? hostLocalContext.Players.FirstOrDefault(player => player.IsLocal)
                    ?? hostLocalContext.Players[0]).Player;
            }
            else
            {
                var (context, singlePlayerInvalid) = await _createSinglePlayerRunContext(request, fixture);
                if (context is null)
                {
                    return singlePlayerInvalid!;
                }

                runManager = context.RunManager;
                runState = context.RunState;
                livePlayersByFixtureId = new Dictionary<string, Player> { [fixture.Players[0].Id] = context.Player };
                localPlayer = context.Player;
            }

            var room = new EventRoom(eventModel);
            if (isAncientEvent)
            {
                PrepareAncientEventRestoreMapPoint(runState, eventModel, fixture.Run.Floor);
                await EnterMapPointInternalCompat(runManager, fixture.Run.Floor, MapPointType.Ancient, room);
            }
            else
            {
                await runManager.EnterRoom(room);
            }

            FixtureRecipeFieldReport? ancientDialogueReport = null;
            if (isAncientEvent)
            {
                var ancientDialogueResult = await _applyAncientDialogue(request, fixture, eventModel);
                if (!ancientDialogueResult.Success)
                {
                    return ancientDialogueResult.Invalid!;
                }

                ancientDialogueReport = ancientDialogueResult.Report;
            }

            if (!_validateEventRoomRecipe(request, fixture.EventRoom, fixture.Perspective.PlayerId, out invalid))
            {
                return invalid!;
            }

            FixtureRecipeFieldReport? crystalSphereChosenOptionReport = null;
            IReadOnlyList<FixtureRecipeFieldReport> crystalSphereStateReports = [];
            if (Sts2SupportedScreenIds.IsCrystalSphereScreenType(fixture.Screen))
            {
                var chosenOptionResult = await _applyCrystalSphereChosenOption(request, fixture, eventModel);
                if (!chosenOptionResult.Success)
                {
                    return chosenOptionResult.Invalid!;
                }

                crystalSphereChosenOptionReport = chosenOptionResult.Report;

                var crystalSphereStateResult = await _applyCrystalSphereState(request, fixture);
                if (!crystalSphereStateResult.Success)
                {
                    return crystalSphereStateResult.Invalid!;
                }

                crystalSphereStateReports = crystalSphereStateResult.Reports;
            }

            // Grant authored relics/deck cards AFTER entering the room + applying the ancient
            // dialogue + validating the recipe, so an upon-pickup relic (e.g. PRECARIOUS_SHEARS,
            // the Neow ancient relic option) fires AfterObtained() against the live event room:
            // its real CardSelectCmd.FromDeckForRemoval dialog opens on top, reproducing the
            // "remove two cards" checkpoint with the relic already in the bar. The grant
            // fire-and-forgets the upon-pickup obtain so the open dialog does not hang the load;
            // the warmup frames let the dialog mount before the screen is located.
            if (fixture.Players.Any(player => player.RelicIds is { Count: > 0 } || player.DeckCards is { Count: > 0 }))
            {
                if (await ApplyAuthoredPlayerGrantsAsync(request, fixture, runState, livePlayersByFixtureId) is { } grantInvalid)
                {
                    return grantInvalid;
                }

                await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
            }

            // Compose the deck-card-selection overlay (the enchant/upgrade dialog) on
            // top of the event room when the fixture authors one — faithful to picking
            // an enchant option such as Field of Man-Sized Holes -> ENTER_YOUR_HOLE
            // (PerfectFit). Runs AFTER the deck grants so appended cards appear in the
            // grid. Shares the composition path with the rest-site loader.
            if (ComposeDeckSelectionOverlay(request, fixture, localPlayer, runState, "event") is { } eventOverlayInvalid)
            {
                return eventOverlayInvalid;
            }

            var locatedScreen = _screenLocator.Locate();
            IReadOnlyList<FixtureRecipeFieldReport> ancientDialogueAppliedReports = ancientDialogueReport is { ReasonCode: "applied-authored-field" } appliedAncientDialogueReport
                ? [appliedAncientDialogueReport]
                : [];
            IReadOnlyList<FixtureRecipeFieldReport> ancientDialogueInferredReports = ancientDialogueReport is { ReasonCode: "inferred-deterministic-fixture-field" } inferredAncientDialogueReport
                ? [inferredAncientDialogueReport]
                : [];
            IReadOnlyList<FixtureRecipeFieldReport> crystalSphereChosenOptionReports = crystalSphereChosenOptionReport is null
                ? []
                : [crystalSphereChosenOptionReport];
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
                    [
                        .. (fixture.Players.Count > 1
                        ? AppliedEventRoomFields(fixture, hostLocalPlayers, eventModel, ancientDialogueAppliedReports)
                        : AppliedEventRoomFields(fixture, [], eventModel, ancientDialogueAppliedReports)),
                    .. crystalSphereChosenOptionReports,
                    .. crystalSphereStateReports,
                    .. AppliedSelectionFields(fixture.Selection),
                    ],
                    ancientDialogueInferredReports,
                    [
                        .. UnsupportedEventRoomFields(fixture.EventRoom),
                    .. UnsupportedJsonFields("run.players[].overlays[]", fixture.Selection?.ExtraFields),
                    ],
                    [],
                    BuildBridgeValidation("passed", "run.currentRoom.event.playerStates[].options", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded event-room fixture '{fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadTreasureFamilyAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            FixtureLoadResult? invalid;
            RunManager runManager;
            RunState runState;
            if (fixture.Players.Count > 1)
            {
                // Multiplayer treasure-room fixtures use the host-local couch-coop run
                // context, mirroring the event-room path. room/lobby remain unused here.
                if (!string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
                {
                    return InvalidFixture(
                        request,
                        "run.currentRoom.combat",
                        fixture.Room.EncounterId,
                        $"Remove room; {fixture.Screen} fixtures do not use room.encounterId.");
                }

                if (fixture.Lobby is not null)
                {
                    return InvalidFixture(
                        request,
                        "characterSelect",
                        fixture.Lobby.Kind,
                        $"Remove lobby; {fixture.Screen} fixtures do not use the lobby recipe section.");
                }

                var (hostLocalContext, hostLocalInvalid) = await _createHostRunContext(request, fixture, "treasure-room");
                if (hostLocalContext is null)
                {
                    return hostLocalInvalid!;
                }

                runManager = hostLocalContext.RunManager;
                runState = hostLocalContext.RunState;
            }
            else
            {
                var (context, singlePlayerInvalid) = await _createRunContext(request, fixture, fixture.Screen);
                if (context is null)
                {
                    return singlePlayerInvalid!;
                }

                runManager = context.RunManager;
                runState = context.RunState;
            }

            if (fixture.TreasureRoom is null)
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.treasure",
                    string.Empty,
                    "Provide treasureRoom for treasure-room and relic-selection fixtures.");
            }

            if (Sts2SupportedScreenIds.IsRelicSelectionScreenType(fixture.Screen))
            {
                var relics = ResolveRelicModels(request, "run.currentRoom.treasure.currentRelics", fixture.TreasureRoom.RelicIds, allowEmpty: false, out invalid);
                if (invalid is not null)
                {
                    return invalid;
                }

                ShowRelicSelection(relics!);
            }
            else
            {
                var treasureRoom = new TreasureRoom(fixture.Run.Act - 1);
                // Seed the current map point so the top-bar room icon resolves.
                PrepareRestoreMapPointHistory(
                    runState,
                    MapPointType.Treasure,
                    treasureRoom.RoomType,
                    roomModelId: null,
                    fixture.Run.Floor);
                await runManager.EnterRoom(treasureRoom);

                if (string.Equals(fixture.TreasureRoom.ChestState, "opened", StringComparison.Ordinal))
                {
                    if (!_applyOpenedTreasureRoomRecipe(request, fixture, runManager, runState, out invalid))
                    {
                        return invalid!;
                    }
                }
            }

            var locatedScreen = _screenLocator.Locate();
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
                recipeReport: BuildRecipeReport(fixture, AppliedTreasureFields(fixture), [], UnsupportedTreasureFields(fixture), [], BuildBridgeValidation("passed", "run.currentRoom.treasure", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded {fixture.Screen} fixture '{fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadShopAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            var (context, invalid) = await _createRunContext(request, fixture, Sts2SupportedScreenIds.ShopScreenId);
            if (context is null)
            {
                return invalid!;
            }

            var merchantRoom = new MerchantRoom();
            // Seed the current map point so the top-bar room icon resolves.
            PrepareRestoreMapPointHistory(
                context.RunState,
                MapPointType.Shop,
                merchantRoom.RoomType,
                roomModelId: null,
                fixture.Run.Floor);
            await context.RunManager.EnterRoom(merchantRoom);

            if (fixture.Shop is not null)
            {
                if (fixture.Shop.Gold is int authoredGold)
                {
                    context.Player.Gold = authoredGold;
                }

                if (!_applyShopRecipe(request, context.Player, fixture.Shop, out invalid))
                {
                    return invalid!;
                }
            }

            NMerchantRoom.Instance?.OpenInventory();
            await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);

            var locatedScreen = _screenLocator.Locate();
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
                    AppliedShopFields(fixture.Shop),
                    InferredGeneratedShopFields(fixture.Shop?.HasInventory != true),
                    UnsupportedJsonFields("run.currentRoom.shop", fixture.Shop?.ExtraFields),
                    [],
                    BuildBridgeValidation("passed", "shop", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded shop fixture '{fixture.Name}'."),
                ]);
        }

    }
}
