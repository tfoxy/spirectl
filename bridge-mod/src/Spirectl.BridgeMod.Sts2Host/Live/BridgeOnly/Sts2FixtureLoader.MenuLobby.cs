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
    private sealed partial class MenuLobbyFixtureFamily
    {
        private async Task<FixtureLoadResult> LoadLobbyAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            if (!TryResolveLobbyRecipe(request, fixture, out var recipe, out var invalid))
            {
                return invalid!;
            }

            var runManager = RunManager.Instance;
            if (runManager is null)
            {
                return RuntimeFailure(request, "run_manager", "RunManager.Instance was null.");
            }

            if (runManager.IsInProgress)
            {
                runManager.CleanUp(graceful: false);
            }

            _resetLobbyLifecycle(request.FixtureName);

            if (NGame.Instance is null)
            {
                return RuntimeFailure(
                    request,
                    "game",
                    "NGame.Instance was null while preparing the live CharacterSelect fixture load.");
            }

            var mainMenu = await EnsureMainMenu(forceCurrentScene: true);
            if (mainMenu is null)
            {
                return RuntimeFailure(
                    request,
                    "game.mainMenu",
                    "The live CharacterSelect fixture path could not materialize the main menu screen stack.");
            }

            var submenuStack = mainMenu.SubmenuStack;

            if (string.Equals(recipe.Kind, "load-run", StringComparison.Ordinal))
            {
                return await LoadRunLobbyAsync(request, fixture, recipe, submenuStack);
            }

            return await LoadStartRunLobbyAsync(request, fixture, recipe, submenuStack);
        }

        private async Task<FixtureLoadResult> LoadStartRunLobbyAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture,
            LobbyFixtureRecipe recipe,
            NMainMenuSubmenuStack submenuStack)
        {
            const int maxAttempts = 2;

            for (var attempt = 0; attempt < maxAttempts; attempt += 1)
            {
                if (attempt > 0)
                {
                    _logStream.Write(
                        BridgeLogLevel.Warn,
                        "bridge.fixture",
                        $"Load fixture '{request.FixtureName}' is retrying start-run lobby materialization after stale Godot UI disposal.");
                    Sts2FixtureLobbyProgressScope.Clear();

                    var freshMainMenu = await CreateFreshMainMenu(forceCurrentScene: true);
                    if (freshMainMenu is null)
                    {
                        return RuntimeFailure(
                            request,
                            "game.mainMenu",
                            "The live CharacterSelect fixture path could not recover from stale lobby UI.");
                    }

                    submenuStack = freshMainMenu.SubmenuStack;
                }
                else if (!_tryClearSubmenuStack(submenuStack, request.FixtureName))
                {
                    var freshMainMenu = await CreateFreshMainMenu(forceCurrentScene: true);
                    if (freshMainMenu is null)
                    {
                        return RuntimeFailure(
                            request,
                            "game.mainMenu",
                            "The live CharacterSelect fixture path could not recover from a stale fastmp submenu stack.");
                    }

                    submenuStack = freshMainMenu.SubmenuStack;
                }

                var (netService, hostError) = StartStartRunLobbyHost(request);
                if (hostError is not null || netService is null)
                {
                    return RuntimeFailure(
                        request,
                        "characterSelect.kind",
                        $"The live start-run lobby host could not start after {MaxStartRunHostPortAttempts} attempts: {hostError}.");
                }

                try
                {
                    var screen = submenuStack.GetSubmenuType<NCharacterSelectScreen>();
                    EnsureUsableGodotObject(screen, "character-select screen");
                    screen.InitializeMultiplayerAsHost(netService, 4);
                    EnsureUsableGodotObject(screen, "character-select screen");
                    ApplyStartRunLobbyRecipe(screen, recipe);
                    submenuStack.Push(screen);
                    _clearLobbyOverlays(request.FixtureName);
                    await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
                    if (!await WaitForStableScreenId(Sts2SupportedScreenIds.StartRunLobbyScreenId))
                    {
                        CleanupPartiallyInitializedStartRunHost(netService, request.FixtureName);
                        return RuntimeFailure(
                            request,
                            "screen",
                            "The live start-run lobby screen did not remain stable after submenu push.");
                    }

                    ApplyFixtureLobbyCharacterLocks(screen, recipe.LockedCharacterIds);
                    FocusLocalCharacterButton(screen, recipe.LocalPlayer.Character.Id.Entry);
                    ApplyLocalReadyLobbyState(screen, recipe);
                    await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
                    if (!await WaitForStableScreenId(Sts2SupportedScreenIds.StartRunLobbyScreenId))
                    {
                        CleanupPartiallyInitializedStartRunHost(netService, request.FixtureName);
                        return RuntimeFailure(
                            request,
                            "screen",
                            "The live start-run lobby screen did not remain stable after fixture UI customization.");
                    }

                    _retainLobbyHost(netService);
                    break;
                }
                catch (Exception ex) when (ShouldRetryStaleLobbyMaterialization(ex, attempt, maxAttempts))
                {
                    CleanupPartiallyInitializedStartRunHost(netService, request.FixtureName);
                    continue;
                }
                catch (Exception ex) when (GetStaleGodotObjectException(ex) is { } staleException)
                {
                    CleanupPartiallyInitializedStartRunHost(netService, request.FixtureName);
                    ExceptionDispatchInfo.Capture(staleException).Throw();
                    throw;
                }
                catch
                {
                    CleanupPartiallyInitializedStartRunHost(netService, request.FixtureName);
                    throw;
                }
            }

            var locatedScreen = _screenLocator.Locate();
            if (!Sts2SupportedScreenIds.IsStartRunLobbyScreenType(locatedScreen.ScreenType))
            {
                _releaseLobbyHost();
                return RuntimeFailure(
                    request,
                    "screen",
                    $"The live start-run lobby fixture path resolved '{locatedScreen.ScreenType}' after materialization instead of {Sts2SupportedScreenIds.StartRunLobbyScreenId}.");
            }

            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into start-run lobby '{fixture.Lobby!.Kind}'.");

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
                    Message: $"Loaded start-run lobby fixture '{fixture.Name}'."),
                new FixtureLoadNotice(
                    Code: "fixture.lobby.host-local-seat",
                    Message: "Live start-run lobby fixtures create a local multiplayer host and report authored remote players as degraded seats when no configured remote client exists."),
                new FixtureLoadNotice(
                    Code: "fixture.lobby.start-run",
                    Message: "The live loader realized the authored start-run lobby recipe."),
                ]);
        }

        private const ushort StartRunHostPortBase = 33771;
        private const int MaxStartRunHostPortAttempts = 16;

        private (NetHostGameService? Host, object? Error) StartStartRunLobbyHost(FixtureLoadRequestSnapshot request)
        {
            var netService = new NetHostGameService();
            object? hostError = null;
            for (var portOffset = 0; portOffset < MaxStartRunHostPortAttempts; portOffset += 1)
            {
                var hostPort = (ushort)(StartRunHostPortBase + portOffset);
                hostError = netService.StartENetHost(hostPort, 4);
                if (hostError is null)
                {
                    return (netService, null);
                }

                TryDisconnectFailedStartRunHost(netService, request.FixtureName);

                if (portOffset + 1 < MaxStartRunHostPortAttempts)
                {
                    netService = new NetHostGameService();
                }
            }

            return (null, hostError);
        }

        private void TryDisconnectFailedStartRunHost(NetHostGameService netService, string fixtureName)
            => _disconnectLobbyHost(netService, fixtureName);

        private void CleanupPartiallyInitializedStartRunHost(NetHostGameService netService, string fixtureName)
        {
            Sts2FixtureLobbyProgressScope.Clear();
            _disconnectLobbyHost(netService, fixtureName);
        }

        private async Task<FixtureLoadResult> LoadRunLobbyAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture,
            LobbyFixtureRecipe recipe,
            NMainMenuSubmenuStack submenuStack)
        {
            // Mirror the start-run lobby retry: a freshly materialized main-menu submenu stack
            // can be disposed out from under us before the load-run screen is pushed, so recover
            // by rebuilding the main menu and retrying once.
            const int maxAttempts = 2;

            for (var attempt = 0; attempt < maxAttempts; attempt += 1)
            {
                if (attempt > 0)
                {
                    _logStream.Write(
                        BridgeLogLevel.Warn,
                        "bridge.fixture",
                        $"Load fixture '{request.FixtureName}' is retrying load-run lobby materialization after stale Godot UI disposal.");
                    Sts2FixtureLobbyProgressScope.Clear();
                }

                var netService = new NetHostGameService();
                var hostError = netService.StartENetHost(33771, 4);
                if (hostError is not null)
                {
                    return RuntimeFailure(
                        request,
                        "characterSelect.kind",
                        $"The live load-run lobby host could not start: {hostError}.");
                }

                // Build a render-complete saved run before touching the menu. The game validates the
                // save against the live local net id (so the host must start first), and the load-run
                // screen renders the saved map/acts (so the run must be generated, not hand-built).
                var (serializableRun, saveInvalid) = await _buildLoadRunSave(request, fixture, recipe, netService.NetId);
                if (serializableRun is null)
                {
                    _disconnectLobbyHost(netService, request.FixtureName);
                    return saveInvalid!;
                }

                // Always materialize a FRESH main menu for the load-run lobby rather than reusing the
                // live one (as the caller's EnsureMainMenu does): reusing re-enters the live main-menu
                // buttons (NInvitePlayersButton "signal already connected"). A fresh NMainMenu + submenu
                // stack mirrors the proven save-restore materialization. The passed-in submenuStack is
                // intentionally discarded.
                var freshMainMenu = await CreateFreshMainMenu(forceCurrentScene: true);
                if (freshMainMenu is null)
                {
                    _disconnectLobbyHost(netService, request.FixtureName);
                    return RuntimeFailure(
                        request,
                        "game.mainMenu",
                        "The live load-run lobby fixture path could not materialize a fresh main menu.");
                }

                submenuStack = freshMainMenu.SubmenuStack;

                // CreateFreshMainMenu evicts the character assets cached during save generation, but
                // the load-game screen instantiates each player's CharacterSelectBg from that cache.
                // Reload them now (the generated run's State is still live, which LoadRunAssets needs)
                // so InitializeAsHost does not native-crash on a missing background scene.
                await PreloadManager.LoadRunAssets(recipe.AllPlayers.Select(player => player.Character));

                try
                {
                    var screen = submenuStack.GetSubmenuType<NMultiplayerLoadGameScreen>();
                    EnsureUsableGodotObject(screen, "load-run lobby screen");
                    // Do NOT call CleanUpLobby here: it runs `_runLobby.CleanUp(...)` and `_runLobby`
                    // is null on a freshly created screen (it is only set by InitializeAsHost), so the
                    // null dereference native-crashes the game. The fresh-main-menu screen has no prior
                    // lobby to clean up. Initialize first (the GetSubmenuType instance is already in the
                    // submenu container, so its @onready fields are resolved), then push to show it.
                    screen.InitializeAsHost(netService, serializableRun!);
                    EnsureUsableGodotObject(screen, "load-run lobby screen");
                    // The screen now holds the save (and has instantiated the player backgrounds), so
                    // tear down the temporary generation run; otherwise `state` would report a run in
                    // progress instead of the load-run lobby.
                    TearDownGeneratedRun();
                    submenuStack.Push(screen);
                    _clearLobbyOverlays(request.FixtureName);
                    await AwaitFixtureRenderFramesAsync(FixtureRenderWarmupFrames);
                    if (!await WaitForStableScreenId(Sts2SupportedScreenIds.LoadRunLobbyScreenId))
                    {
                        _disconnectLobbyHost(netService, request.FixtureName);
                        return RuntimeFailure(
                            request,
                            "screen",
                            "The live load-run lobby screen did not remain stable after submenu push.");
                    }

                    FocusLocalCharacterButton(screen, recipe.LocalPlayer.Character.Id.Entry);
                    _retainLobbyHost(netService);
                    break;
                }
                catch (Exception ex) when (ShouldRetryStaleLobbyMaterialization(ex, attempt, maxAttempts))
                {
                    TearDownGeneratedRun();
                    _disconnectLobbyHost(netService, request.FixtureName);
                    continue;
                }
                catch (Exception ex)
                {
                    TearDownGeneratedRun();
                    _logStream.Write(
                        BridgeLogLevel.Error,
                        "bridge.fixture",
                        $"Load fixture '{request.FixtureName}' failed during load-run lobby screen initialization: {ex}");
                    _disconnectLobbyHost(netService, request.FixtureName);
                    return RuntimeFailure(
                        request,
                        "characterSelect.kind",
                        $"The live load-run lobby fixture path failed during screen initialization: {ex.Message}");
                }
            }

            var locatedScreen = _screenLocator.Locate();
            if (!Sts2SupportedScreenIds.IsLoadRunLobbyScreenType(locatedScreen.ScreenType))
            {
                _releaseLobbyHost();
                return RuntimeFailure(
                    request,
                    "screen",
                    $"The live load-run lobby fixture path resolved '{locatedScreen.ScreenType}' after materialization instead of {Sts2SupportedScreenIds.LoadRunLobbyScreenId}.");
            }

            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into load-run lobby '{fixture.Lobby!.Kind}'.");

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
                recipeReport: BuildRecipeReport(fixture, [], [], [], recipe.RemotePlayers.Select(player => player.IsHostLocalSeat
                    ? Report(
                        $"players[{player.FixtureId}]",
                        player.Character.Id.Entry,
                        "host-local-seat",
                        "The fixture marks this extra seat as owned by the host bridge.")
                    : Report(
                    $"players[{player.FixtureId}]",
                    player.Character.Id.Entry,
                    "degraded-local-multiplayer",
                    "No configured remote bridge client exists, so the live loader preserves the remote player as lobby data without advertising remote-owned local execution.")).ToArray(), BuildBridgeValidation("passed", "screen", locatedScreen.ScreenType)),
                notices:
                [
                    new FixtureLoadNotice(
                    Code: "fixture.loaded",
                    Message: $"Loaded load-run lobby fixture '{fixture.Name}'."),
                ]);
        }

        private async Task<FixtureLoadResult> LoadMainMenuAsync(
            FixtureLoadRequestSnapshot request,
            FixtureDocument fixture)
        {
            if (!string.IsNullOrWhiteSpace(fixture.Room.EncounterId))
            {
                return InvalidFixture(
                    request,
                    "run.currentRoom.combat",
                    fixture.Room.EncounterId,
                    "Remove room; main-menu fixtures do not enter a room.");
            }

            if (fixture.Lobby is not null)
            {
                return InvalidFixture(
                    request,
                    "characterSelect",
                    fixture.Lobby.Kind,
                    "Remove lobby; main-menu fixtures only materialize the top-level menu screen.");
            }

            var runManager = RunManager.Instance;
            if (runManager is not null && runManager.IsInProgress)
            {
                runManager.CleanUp(graceful: false);
            }

            if (NGame.Instance is null)
            {
                return RuntimeFailure(
                    request,
                    "game",
                    "NGame.Instance was null while preparing the live main-menu fixture load.");
            }

            var mainMenu = await EnsureMainMenu(forceCurrentScene: true);
            if (mainMenu is null)
            {
                return RuntimeFailure(
                    request,
                    "game.mainMenu",
                    "The live main-menu fixture path could not materialize the main menu screen stack.");
            }

            var locatedScreen = _screenLocator.Locate();
            _logStream.Write(
                BridgeLogLevel.Info,
                "bridge.fixture",
                $"Loaded fixture '{fixture.Name}' into the main menu.");

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
                    Message: $"Loaded main-menu fixture '{fixture.Name}'."),
                ]);
        }

    }
}
