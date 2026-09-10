using System.Text;
using System.Text.Json;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Restore;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;


public sealed class Sts2ScenarioProvider(
    IGameStateExtractor stateExtractor,
    IPerspectiveProvider perspectiveProvider,
    IFixtureLoader fixtureLoader,
    ILogStream logStream) : IScenarioProvider
{
    private const string FixtureBundleFormatVersion = "spirectl.scenario.bundle/v0";
    private const string SaveRunBundleFormatVersion = "spirectl.scenario.save-run/v0";
    private const string SaveRunBundleContentType = "application/vnd.spirectl.sts2-save+json";

    private static readonly HashSet<string> SupportedScreens = new(StringComparer.Ordinal)
    {
        "combat",
        Sts2SupportedScreenIds.ShopScreenId,
        Sts2SupportedScreenIds.FakeMerchantInventoryScreenId,
        Sts2SupportedScreenIds.MapScreenId,
        Sts2SupportedScreenIds.RewardsScreenId,
        Sts2SupportedScreenIds.RestSiteRoomScreenId,
        Sts2SupportedScreenIds.EventRoomScreenId,
        Sts2SupportedScreenIds.TreasureRoomScreenId,
        Sts2SupportedScreenIds.TreasureRoomRelicCollectionScreenId,
        Sts2SupportedScreenIds.RelicSelectionScreenId,
        Sts2SupportedScreenIds.CardRewardSelectionScreenId,
        Sts2SupportedScreenIds.ChooseACardSelectionScreenId,
        Sts2SupportedScreenIds.SimpleCardSelectionScreenId,
        Sts2SupportedScreenIds.DeckCardSelectionScreenId,
        Sts2SupportedScreenIds.DeckUpgradeSelectionScreenId,
        Sts2SupportedScreenIds.DeckTransformSelectionScreenId,
        Sts2SupportedScreenIds.DeckEnchantSelectionScreenId,
        Sts2SupportedScreenIds.BundleSelectionScreenId,
        Sts2SupportedScreenIds.StartRunLobbyScreenId,
        Sts2SupportedScreenIds.LoadRunLobbyScreenId,
    };

    private readonly IGameStateExtractor _stateExtractor = stateExtractor;
    private readonly IPerspectiveProvider _perspectiveProvider = perspectiveProvider;
    private readonly IFixtureLoader _fixtureLoader = fixtureLoader;
    private readonly ILogStream _logStream = logStream;

    public ScenarioCaptureResultSnapshot Capture(ScenarioCaptureRequestSnapshot request)
    {
        return Sts2MainThreadDispatcher.Invoke(() => CaptureOnMainThread(request));
    }

    public ScenarioRestoreResultSnapshot Restore(ScenarioRestoreRequestSnapshot request)
    {
        return Sts2MainThreadDispatcher.InvokeAsync(() => RestoreOnMainThreadAsync(request)).GetAwaiter().GetResult();
    }

    private ScenarioCaptureResultSnapshot CaptureOnMainThread(ScenarioCaptureRequestSnapshot request)
    {
        var snapshot = _stateExtractor.Extract(
            new GameStateQuery(new PerspectiveSelection(PlayerScope.Local, request.PlayerId), IncludeDebug: false),
            _perspectiveProvider.GetDefaultPerspective());

        if (!SupportedScreens.Contains(snapshot.ScreenType))
        {
            return FailureCapture(request.RequestId, ScenarioFailureCode.InvalidAction, "unsupported_source_screen", $"Scenario capture does not support screen '{snapshot.ScreenType}' in M53.");
        }

        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var name = $"{snapshot.ScreenType}-scenario";
        var fieldReports = Sts2RestoreFidelity.FieldReports(snapshot);
        var quality = Sts2RestoreFidelity.CoarseScenarioQuality(fieldReports);
        var multiplayer = Sts2MultiplayerRestore.BuildMetadata(snapshot);
        var exactBundle = request.IncludeExact
            ? BuildExactBundle(name, createdAt, snapshot, quality, fieldReports, multiplayer)
            : null;
        var compatibilityNotes = exactBundle is { FormatVersion: SaveRunBundleFormatVersion }
            ? new[]
            {
                new ScenarioCompatibilityNoteSnapshot("native-save-sidecar", "Exact export includes a native save-backed sidecar for run continuation.", "restore.exactBundle"),
            }
            :
        [
            new ScenarioCompatibilityNoteSnapshot("runtime-queues-omitted", "Hidden runtime queues were not captured.", string.Empty),
            new ScenarioCompatibilityNoteSnapshot("rng-state-omitted", "Runtime RNG internals were not captured.", string.Empty),
        ];
        var document = new ScenarioDocumentSnapshot(
            SchemaVersion: "spirectl.scenario/v0",
            Name: name,
            Description: string.Empty,
            CreatedAt: createdAt,
            Source: new ScenarioSourceSnapshot(
                snapshot.GameVersion,
                snapshot.BridgeVersion,
                SpirectlVersion: string.Empty,
                Screen: new ScenarioScreenSnapshot(snapshot.ScreenType, snapshot.ScreenTitle, snapshot.ScreenInstanceId),
                Perspective: new ScenarioPerspectiveSnapshot("local", snapshot.ResolvedPerspective.PlayerId ?? string.Empty)),
            Restore: new ScenarioRestoreSnapshot(
                ScenarioRestoreMode.Sparse,
                quality,
                ExactBundle: null,
                CompatibilityNotes: compatibilityNotes,
                FieldReports: fieldReports),
            Run: BuildRun(snapshot),
            ScreenStateJson: BuildScreenStateJson(snapshot),
            Notices: [new ScenarioNoticeSnapshot("sparse-export", "Static model defaults are referenced by stable id and not repeated.", Provisional: false)],
            Multiplayer: multiplayer);

        return ScenarioCaptureResultSnapshot.Success(
            request.RequestId,
            document,
            snapshot.Source,
            snapshot.Provisional,
            exactBundle);
    }

    private async Task<ScenarioRestoreResultSnapshot> RestoreOnMainThreadAsync(ScenarioRestoreRequestSnapshot request)
    {
        if (request.Scenario is null)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidRequest, "invalid_scenario_document", "Scenario restore requires a scenario document.");
        }

        if (request.ExactBundle is not null && request.ExactBundle.Length > 0)
        {
            if (IsSaveBackedExactBundle(request))
            {
                var saveRestore = await RestoreSaveBackedExactAsync(request);
                if (saveRestore.Error is null || !request.AllowSparseFallback)
                {
                    return saveRestore;
                }
            }

            try
            {
                using var exactDocument = JsonDocument.Parse(request.ExactBundle);
                if (exactDocument.RootElement.TryGetProperty("fixture", out var fixtureElement))
                {
                    var exactLoad = await LoadFixtureAsync(request.RequestId, request.Scenario.Name, fixtureElement.GetRawText());
                    if (exactLoad.Error is null)
                    {
                        return SuccessFromFixtureLoad(request.RequestId, exactLoad, ScenarioRestoreQuality.Partial, exactBundleUsed: true, sparseFallbackUsed: false, "exact-bundle-restore", "Scenario restore used exact bundle fixture realization.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logStream.Write(BridgeLogLevel.Info, "bridge.scenario", $"Scenario exact restore failed: {ex.Message}");
            }

            if (!request.AllowSparseFallback)
            {
                return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "bridge_restore_runtime_failure", "Scenario exact bundle restore failed and sparse fallback was not allowed.");
            }

            var sparseFallback = await LoadSparseScenarioAsync(request);
            if (sparseFallback.Error is not null)
            {
                return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "bridge_restore_runtime_failure", sparseFallback.Error.Message);
            }

            return ScenarioRestoreResultSnapshot.Success(
                request.RequestId,
                ScenarioRestoreQuality.Degraded,
                new ScenarioScreenSnapshot(sparseFallback.ScreenType, sparseFallback.ScreenTitle, sparseFallback.ScreenInstanceId),
                new ScenarioPerspectiveSnapshot("local", sparseFallback.ResolvedPerspective.PlayerId ?? string.Empty),
                [new ScenarioNoticeSnapshot("exact-restore-failed-fallback-used", "Exact bundle restore failed; sparse restore succeeded.", Provisional: false)],
                exactBundleUsed: false,
                sparseFallbackUsed: true,
                [new ScenarioCompatibilityNoteSnapshot("sparse-restore-limited", "Scenario sparse fallback used fixture-backed partial realization.", string.Empty)]);
        }

        if (request.Scenario.Multiplayer is not null)
        {
            var multiplayerResult = await RestoreMultiplayerScenarioAsync(request);
            if (multiplayerResult is not null)
            {
                return multiplayerResult;
            }
        }

        var sparseLoad = await LoadSparseScenarioAsync(request);
        if (sparseLoad.Error is not null)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "bridge_restore_runtime_failure", sparseLoad.Error.Message);
        }

        return SuccessFromFixtureLoad(request.RequestId, sparseLoad, ScenarioRestoreQuality.Partial, exactBundleUsed: false, sparseFallbackUsed: false, "fixture-backed-restore", "Scenario restore used fixture-backed partial realization.");
    }

    private async Task<ScenarioRestoreResultSnapshot?> RestoreMultiplayerScenarioAsync(ScenarioRestoreRequestSnapshot request)
    {
        var scenario = request.Scenario!;
        var multiplayer = scenario.Multiplayer!;
        if (!multiplayer.IsMultiplayer)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(multiplayer.LocalPlayerId))
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidRequest, "remote_player_identity_missing", "Multiplayer restore requires a local player id.");
        }

        if (Sts2SupportedScreenIds.IsLobbyScreenType(scenario.Source.Screen.Type)
            && multiplayer.RestoreMode == MultiplayerRestoreModeSnapshot.LobbyOnly)
        {
            var load = await LoadFixtureAsync(
                request.RequestId,
                scenario.Name,
                BuildMultiplayerLobbyFixtureJson(scenario.Name, multiplayer));
            if (load.Error is not null)
            {
                return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "lobby_materialization_failed", load.Error.Message);
            }

            return ScenarioRestoreResultSnapshot.Success(
                request.RequestId,
                ScenarioRestoreQuality.Partial,
                new ScenarioScreenSnapshot(load.ScreenType, load.ScreenTitle, load.ScreenInstanceId),
                new ScenarioPerspectiveSnapshot("local", load.ResolvedPerspective.PlayerId ?? string.Empty),
                [.. load.Notices.Select(notice => new ScenarioNoticeSnapshot(notice.Code, notice.Message, notice.Provisional))],
                exactBundleUsed: false,
                sparseFallbackUsed: false,
                [new ScenarioCompatibilityNoteSnapshot("lobby-restore", "Scenario restore materialized a multiplayer lobby fixture with remote placeholders where needed.", string.Empty)],
                multiplayerRestore: LobbyRestoreResult(multiplayer));
        }

        if (!multiplayer.RequiresRemoteClients)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "active_run_restore_unsupported", "Active multiplayer restore is unsupported for this artifact.");
        }

        if (!request.AllowDegradedLocalMultiplayer)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "degradation_flag_required", "This active multiplayer artifact can only be restored locally as a degraded single-client state. Re-run with --allow-degraded-local-multiplayer to accept omitted remote clients.");
        }

        if (!multiplayer.DegradedLocalOnlyAvailable)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "remote_clients_required", "This multiplayer artifact requires remote clients that the local bridge cannot recreate.");
        }

        var degradedLoad = await LoadFixtureAsync(
            request.RequestId,
            scenario.Name,
            BuildDegradedLocalOnlyFixtureJson(scenario.Name, scenario.Source.Screen.Type, scenario.Run, multiplayer));
        if (degradedLoad.Error is not null)
        {
            return FailureRestore(request.RequestId, ScenarioFailureCode.InvalidAction, "active_run_restore_unsupported", degradedLoad.Error.Message);
        }

        return ScenarioRestoreResultSnapshot.Success(
            request.RequestId,
            ScenarioRestoreQuality.Degraded,
            new ScenarioScreenSnapshot(degradedLoad.ScreenType, degradedLoad.ScreenTitle, degradedLoad.ScreenInstanceId),
            new ScenarioPerspectiveSnapshot("local", degradedLoad.ResolvedPerspective.PlayerId ?? string.Empty),
            [.. degradedLoad.Notices.Select(notice => new ScenarioNoticeSnapshot(notice.Code, notice.Message, notice.Provisional))],
            exactBundleUsed: false,
            sparseFallbackUsed: false,
            [new ScenarioCompatibilityNoteSnapshot("remote-clients-omitted", "Remote multiplayer clients were intentionally omitted from degraded local-only restore.", "multiplayer.remoteRuntime")],
            multiplayerRestore: DegradedRestoreResult(multiplayer));
    }

    private Task<FixtureLoadResult> LoadSparseScenarioAsync(ScenarioRestoreRequestSnapshot request)
    {
        return LoadFixtureAsync(
            request.RequestId,
            request.Scenario!.Name,
            BuildFixtureJson(request.Scenario));
    }

    private async Task<ScenarioRestoreResultSnapshot> RestoreSaveBackedExactAsync(ScenarioRestoreRequestSnapshot request)
    {
        try
        {
            var saveRunType = Type.GetType("MegaCrit.Sts2.Core.Saves.Runs.SerializableRun, sts2");
            if (saveRunType is null)
            {
                return SaveBackedRestoreFailure(request.RequestId, "save_type_unavailable", "The live STS2 SerializableRun type is unavailable.");
            }

            var serializableRun = JsonSerializer.Deserialize(request.ExactBundle!, saveRunType);
            if (serializableRun is null)
            {
                return SaveBackedRestoreFailure(request.RequestId, "save_deserialize_failed", "The save-backed exact bundle could not be deserialized as SerializableRun.");
            }

            var loadResult = await LoadRunSaveIntoLobbyAsync(request, serializableRun);
            return loadResult;
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Info, "bridge.scenario", $"Save-backed scenario restore failed: {ex.Message}");
            return SaveBackedRestoreFailure(request.RequestId, "save_restore_failed", ex.Message);
        }
    }

    private async Task<ScenarioRestoreResultSnapshot> LoadRunSaveIntoLobbyAsync(
        ScenarioRestoreRequestSnapshot request,
        object serializableRun)
    {
        var preloadType = Type.GetType("MegaCrit.Sts2.Core.Assets.PreloadManager, sts2");
        var loadAssetsTask = preloadType
            ?.GetMethod("LoadCommonAndMainMenuAssets", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null) as Task;
        if (loadAssetsTask is not null)
        {
            await loadAssetsTask;
        }

        var nGameType = Type.GetType("MegaCrit.Sts2.Core.Nodes.NGame, sts2");
        var nMainMenuType = Type.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu, sts2");
        var loadGameScreenType = Type.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerLoadGameScreen, sts2")
            ?? Type.GetType("MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen, sts2");
        var netHostType = Type.GetType("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService, sts2");
        if (nGameType is null || nMainMenuType is null || loadGameScreenType is null || netHostType is null)
        {
            return SaveBackedRestoreFailure(request.RequestId, "save_restore_runtime_unavailable", "One or more STS2 load-run lobby runtime types are unavailable.");
        }

        var nGame = nGameType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
        if (nGame is null)
        {
            return SaveBackedRestoreFailure(request.RequestId, "game_unavailable", "NGame.Instance was null while restoring a save-backed scenario.");
        }

        var mainMenu = nMainMenuType
            .GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, [false]);
        var rootSceneContainer = Sts2LiveIntrospection.GetMemberValue(nGame, "RootSceneContainer");
        Sts2LiveIntrospection.InvokeMethod(rootSceneContainer, "SetCurrentScene", mainMenu);
        mainMenu = Sts2LiveIntrospection.GetMemberValue(nGame, "MainMenu") ?? mainMenu;
        var submenuStack = Sts2LiveIntrospection.GetMemberValue(mainMenu, "SubmenuStack");
        if (submenuStack is null)
        {
            return SaveBackedRestoreFailure(request.RequestId, "main_menu_unavailable", "The save-backed restore path could not materialize the main menu submenu stack.");
        }

        while (Sts2LiveIntrospection.GetMemberValue(submenuStack, "SubmenusOpen") is true)
        {
            Sts2LiveIntrospection.InvokeMethod(submenuStack, "Pop");
        }

        var netService = Activator.CreateInstance(netHostType);
        var hostError = Sts2LiveIntrospection.InvokeMethod(netService, "StartENetHost", 33771, 4);
        if (hostError is not null)
        {
            return SaveBackedRestoreFailure(request.RequestId, "host_start_failed", $"The save-backed restore host could not start: {hostError}.");
        }

        var getSubmenu = submenuStack.GetType()
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "GetSubmenuType" && method.IsGenericMethodDefinition);
        var screen = getSubmenu?.MakeGenericMethod(loadGameScreenType).Invoke(submenuStack, null);
        if (screen is null)
        {
            return SaveBackedRestoreFailure(request.RequestId, "load_run_screen_unavailable", "The save-backed restore path could not create the load-run lobby screen.");
        }

        Sts2LiveIntrospection.TryInvokeMethod(screen, "CleanUpLobby", true);
        Sts2LiveIntrospection.InvokeMethod(screen, "InitializeAsHost", netService, serializableRun);
        Sts2LiveIntrospection.InvokeMethod(submenuStack, "Push", screen);

        var snapshot = _stateExtractor.Extract(
            new GameStateQuery(new PerspectiveSelection(PlayerScope.Local, request.Scenario?.Source.Perspective?.PlayerId), IncludeDebug: false),
            _perspectiveProvider.GetDefaultPerspective());

        return ScenarioRestoreResultSnapshot.Success(
            request.RequestId,
            ScenarioRestoreQuality.Exact,
            new ScenarioScreenSnapshot(snapshot.ScreenType, snapshot.ScreenTitle, snapshot.ScreenInstanceId),
            new ScenarioPerspectiveSnapshot("local", snapshot.ResolvedPerspective.PlayerId ?? string.Empty),
            [
                new ScenarioNoticeSnapshot("save-backed-exact-restore", "Scenario restore used a native save-backed exact bundle.", Provisional: false),
            ],
            exactBundleUsed: true,
            sparseFallbackUsed: false,
            [
                new ScenarioCompatibilityNoteSnapshot("native-save-sidecar", "Native save data was used for run continuation.", "restore.exactBundle"),
            ]);
    }

    private static bool IsSaveBackedExactBundle(ScenarioRestoreRequestSnapshot request)
        => string.Equals(request.ExactBundleContentType, SaveRunBundleContentType, StringComparison.Ordinal)
            || string.Equals(request.Scenario?.Restore.ExactBundle?.FormatVersion, SaveRunBundleFormatVersion, StringComparison.Ordinal);

    private static ScenarioRestoreResultSnapshot SaveBackedRestoreFailure(
        string requestId,
        string detailCode,
        string message)
        => FailureRestore(requestId, ScenarioFailureCode.InvalidAction, detailCode, message);

    private Task<FixtureLoadResult> LoadFixtureAsync(string requestId, string name, string fixtureJson)
    {
        return _fixtureLoader.LoadAsync(new FixtureLoadRequestSnapshot(
            requestId,
            "spirectl.fixture/v0",
            $"scenario-{name}",
            $"scenario:{name}",
            fixtureJson));
    }

    private static ScenarioRestoreResultSnapshot SuccessFromFixtureLoad(
        string requestId,
        FixtureLoadResult load,
        ScenarioRestoreQuality quality,
        bool exactBundleUsed,
        bool sparseFallbackUsed,
        string noteCode,
        string noteMessage)
    {
        return ScenarioRestoreResultSnapshot.Success(
            requestId,
            quality,
            new ScenarioScreenSnapshot(load.ScreenType, load.ScreenTitle, load.ScreenInstanceId),
            new ScenarioPerspectiveSnapshot("local", load.ResolvedPerspective.PlayerId ?? string.Empty),
            [.. load.Notices.Select(notice => new ScenarioNoticeSnapshot(notice.Code, notice.Message, notice.Provisional))],
            exactBundleUsed,
            sparseFallbackUsed,
            [new ScenarioCompatibilityNoteSnapshot(noteCode, noteMessage, string.Empty)]);
    }

    private static ScenarioRunSnapshot? BuildRun(GameStateSnapshot snapshot)
    {
        if (snapshot.Run is null)
        {
            return null;
        }

        return new ScenarioRunSnapshot(
            snapshot.Run.Seed,
            snapshot.Run.Act,
            snapshot.Run.Floor,
            Ascension: 0,
            [.. snapshot.Run.Players.Select(player => new ScenarioPlayerSnapshot(
                player.Id,
                player.Character,
                player.IsLocal,
                player.IsHost,
                player.IsRemote))]);
    }

    private static string BuildScreenStateJson(GameStateSnapshot snapshot)
    {
        return JsonSerializer.Serialize(new
        {
            run = snapshot.Run is null ? null : new
            {
                seed = snapshot.Run.Seed,
                act = snapshot.Run.Act,
                floor = snapshot.Run.Floor,
                players = snapshot.Run.Players.Select(player => new
                {
                    id = player.Id,
                    character = player.Character,
                    hp = player.Hp,
                    maxHp = player.MaxHp,
                    isLocal = player.IsLocal,
                    isHost = player.IsHost,
                    isRemote = player.IsRemote,
                }).ToArray(),
            },
            perspective = new
            {
                scope = "local",
                playerId = snapshot.ResolvedPerspective.PlayerId ?? string.Empty,
            },
            combat = snapshot.Combat is null ? null : new
            {
                activePlayerId = snapshot.Combat.ActivePlayerId ?? string.Empty,
                isPlayerTurn = snapshot.Combat.IsPlayerTurn,
                turn = snapshot.Combat.Turn,
                localPlayer = snapshot.Combat.Players.FirstOrDefault(player => player.IsLocal) is { } player
                    ? new
                    {
                        id = player.Id,
                        hp = player.Hp,
                        maxHp = player.MaxHp,
                        block = player.Block,
                        energy = player.Energy,
                        maxEnergy = player.MaxEnergy,
                    }
                    : null,
                hand = snapshot.Combat.Hand.Select(card => new
                {
                    id = card.Id,
                    name = card.Name,
                    cost = card.Cost,
                    upgraded = card.Upgraded,
                    targetIds = card.TargetIds,
                }).ToArray(),
                handCardIds = snapshot.Combat.Hand.Select(card => card.Id).ToArray(),
                drawPileCardIds = snapshot.Combat.DrawPileCardIds ?? [],
                discardPileCardIds = snapshot.Combat.DiscardPileCardIds ?? [],
                exhaustPileCardIds = snapshot.Combat.ExhaustPileCardIds ?? [],
                potions = snapshot.Combat.Potions?.Select(potion => new
                {
                    id = potion.Id,
                    name = potion.Name,
                    slotIndex = potion.SlotIndex,
                    usable = potion.Usable,
                }).ToArray() ?? [],
                potionIds = snapshot.Combat.Potions?.Select(potion => potion.Id).ToArray() ?? [],
                enemies = snapshot.Combat.Enemies.Select(enemy => new
                {
                    id = enemy.Id,
                    name = enemy.Name,
                    hp = enemy.Hp,
                    maxHp = enemy.MaxHp,
                    block = enemy.Block,
                    intent = enemy.Intent,
                    isAlive = enemy.IsAlive,
                }).ToArray(),
            },
            shop = BuildShopState(snapshot),
            eventRoom = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsEventRoomScreenType),
            treasureRoom = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsTreasureRoomScreenType),
            relicSelection = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsRelicSelectionScreenType),
            restSite = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsRestSiteScreenType),
            rewards = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsRewardsScreenType),
            map = BuildChoiceFamilyState(snapshot, Sts2SupportedScreenIds.IsMapScreenType),
            selection = IsSelectionScreen(snapshot.ScreenType)
                ? BuildChoiceFamilyState(snapshot, screenType => string.Equals(snapshot.ScreenType, screenType, StringComparison.Ordinal))
                : null,
            choices = snapshot.Choices.Select(choice => new
            {
                id = choice.Id,
                label = choice.Label,
                ownerPlayerId = choice.OwnerPlayerId ?? string.Empty,
            }).ToArray(),
            availableActions = snapshot.AvailableActions.Select(action => new
            {
                kind = ActionKindName(action.Kind),
                arguments = new
                {
                    playerId = action.Arguments?.PlayerId ?? string.Empty,
                    cardId = action.Arguments?.CardId ?? string.Empty,
                    targetId = action.Arguments?.TargetId ?? string.Empty,
                    choiceId = action.Arguments?.ChoiceId ?? string.Empty,
                    mapNodeId = action.Arguments?.MapNodeId ?? string.Empty,
                    potionId = action.Arguments?.PotionId ?? string.Empty,
                },
            }).ToArray(),
        });
    }

    private static object? BuildShopState(GameStateSnapshot snapshot)
    {
        if (!Sts2SupportedScreenIds.IsShopScreenType(snapshot.ScreenType))
        {
            return null;
        }

        return new
        {
            gold = 0,
            choiceIds = ChoiceIds(snapshot),
            choices = ChoiceSummaries(snapshot),
            availableActionKinds = AvailableActionKinds(snapshot),
        };
    }

    private static object? BuildChoiceFamilyState(GameStateSnapshot snapshot, Func<string?, bool> screenTypeMatches)
    {
        if (!screenTypeMatches(snapshot.ScreenType))
        {
            return null;
        }

        return new
        {
            choiceIds = ChoiceIds(snapshot),
            choices = ChoiceSummaries(snapshot),
            availableActionKinds = AvailableActionKinds(snapshot),
        };
    }

    private static string[] ChoiceIds(GameStateSnapshot snapshot)
        => [.. snapshot.Choices.Select(choice => choice.Id)];

    private static object[] ChoiceSummaries(GameStateSnapshot snapshot)
        => [.. snapshot.Choices.Select(choice => new
        {
            id = choice.Id,
            label = choice.Label,
            kind = choice.Kind,
            ownerPlayerId = choice.OwnerPlayerId ?? string.Empty,
        }).Cast<object>()];

    private static string[] AvailableActionKinds(GameStateSnapshot snapshot)
        => [.. snapshot.AvailableActions.Select(action => ActionKindName(action.Kind)).Distinct(StringComparer.Ordinal)];

    private static bool IsSelectionScreen(string screenType)
        => Sts2SupportedScreenIds.IsCardSelectionFamily(screenType);

    private static ScenarioExactBundlePayloadSnapshot BuildExactBundle(
        string name,
        string createdAt,
        GameStateSnapshot snapshot,
        ScenarioRestoreQuality quality,
        IReadOnlyList<RestoreFieldReportSnapshot> fieldReports,
        MultiplayerRestoreSnapshot? multiplayer)
    {
        var saveBytes = TryCaptureLoadRunSaveBytes(snapshot);
        if (saveBytes is not null)
        {
            return new ScenarioExactBundlePayloadSnapshot(
                "current_run_mp.save",
                SaveRunBundleFormatVersion,
                SaveRunBundleContentType,
                saveBytes);
        }

        return new ScenarioExactBundlePayloadSnapshot(
            "bundle.json",
            FixtureBundleFormatVersion,
            "application/json",
            Encoding.UTF8.GetBytes(BuildBundleJson(name, createdAt, snapshot, quality, fieldReports, multiplayer)));
    }

    private static byte[]? TryCaptureLoadRunSaveBytes(GameStateSnapshot snapshot)
    {
        if (!Sts2SupportedScreenIds.IsLoadRunLobbyScreenType(snapshot.ScreenType)
            || !string.Equals(snapshot.Lobby?.LobbyId, "load-run", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
            var runLobby = Sts2LiveIntrospection.GetMemberValue(screenObject, "_runLobby")
                ?? Sts2LiveIntrospection.GetMemberValue(screenObject, "RunLobby");
            var run = Sts2LiveIntrospection.GetMemberValue(runLobby, "Run");
            return run is null
                ? null
                : JsonSerializer.SerializeToUtf8Bytes(run, run.GetType());
        }
        catch
        {
            return null;
        }
    }

    private static string BuildBundleJson(
        string name,
        string createdAt,
        GameStateSnapshot snapshot,
        ScenarioRestoreQuality quality,
        IReadOnlyList<RestoreFieldReportSnapshot> fieldReports,
        MultiplayerRestoreSnapshot? multiplayer)
    {
        return JsonSerializer.Serialize(new
        {
            formatVersion = FixtureBundleFormatVersion,
            name,
            capturedAt = createdAt,
            screen = snapshot.ScreenType,
            quality = quality.ToString().ToLowerInvariant(),
            fieldReports = FieldReportsJson(fieldReports),
            multiplayer = Sts2MultiplayerRestore.Json(multiplayer),
            fixture = JsonSerializer.Deserialize<JsonElement>(BuildFixtureJson(name, snapshot)),
            screenState = JsonSerializer.Deserialize<JsonElement>(BuildScreenStateJson(snapshot)),
        });
    }

    private static string BuildFixtureJson(string name, GameStateSnapshot snapshot)
    {
        var isShop = Sts2SupportedScreenIds.IsShopScreenType(snapshot.ScreenType);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "spirectl.fixture/v0",
            name = $"scenario-{name}",
            screen = snapshot.ScreenType,
            run = new
            {
                seed = snapshot.Run?.Seed,
                currentActIndex = Math.Max(0, (snapshot.Run?.Act ?? 1) - 1),
                actFloor = Math.Max(1, snapshot.Run?.Floor ?? 1),
                view = new { playerId = snapshot.Run?.Players.FirstOrDefault()?.Id },
                players = snapshot.Run?.Players.Select(player => new
                {
                    id = player.Id,
                    characterId = player.Character,
                    creature = new { currentHp = player.Hp, maxHp = player.MaxHp },
                    gold = isShop ? 0 : (int?)null,
                }).ToArray() ?? [],
                currentRoom = isShop
                    ? new { shop = new { inventory = new { } } }
                    : null,
            },
        });
    }

    private static string BuildFixtureJson(ScenarioDocumentSnapshot scenario)
    {
        using var screenState = JsonDocument.Parse(scenario.ScreenStateJson);
        var hasShop = screenState.RootElement.TryGetProperty("shop", out var shop)
            && shop.ValueKind != JsonValueKind.Null;
        int? shopGold = hasShop && shop.TryGetProperty("gold", out var goldElement) && goldElement.ValueKind == JsonValueKind.Number
            ? goldElement.GetInt32()
            : null;

        var run = new Dictionary<string, object?>
        {
            ["seed"] = scenario.Run?.Seed,
            ["currentActIndex"] = Math.Max(0, (scenario.Run?.Act ?? 1) - 1),
            ["actFloor"] = Math.Max(1, scenario.Run?.Floor ?? 1),
            ["view"] = new Dictionary<string, object?>
            {
                ["playerId"] = scenario.Run?.Players.FirstOrDefault()?.Id,
            },
            ["players"] = scenario.Run?.Players.Select(player => new Dictionary<string, object?>
            {
                ["id"] = player.Id,
                ["characterId"] = player.Character,
                ["gold"] = shopGold,
            }).ToArray() ?? [],
        };
        if (hasShop)
        {
            run["currentRoom"] = new Dictionary<string, object?>
            {
                ["shop"] = new Dictionary<string, object?>
                {
                    ["inventory"] = new Dictionary<string, object?>(),
                },
            };
        }

        var fixture = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "spirectl.fixture/v0",
            ["name"] = $"scenario-{scenario.Name}",
            ["screen"] = scenario.Source.Screen.Type,
            ["run"] = run,
        };

        return JsonSerializer.Serialize(fixture);
    }

    private static string BuildMultiplayerLobbyFixtureJson(string name, MultiplayerRestoreSnapshot multiplayer)
    {
        var kind = multiplayer.Lobby?.LobbyId == "load-run" ? "load-run" : "start-run";
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "spirectl.fixture/v0",
            name = $"scenario-{name}",
            screen = kind == "load-run"
                ? Sts2SupportedScreenIds.LoadRunLobbyScreenId
                : Sts2SupportedScreenIds.StartRunLobbyScreenId,
            characterSelect = new
            {
                kind,
                view = new { playerId = multiplayer.LocalPlayerId },
                lobby = new
                {
                    localPlayerId = multiplayer.LocalPlayerId,
                    hostPlayerId = multiplayer.HostPlayerId,
                    players = multiplayer.Players.Select(player => new
                    {
                        id = player.Id,
                        characterId = player.SelectedCharacterId,
                        isReady = player.IsReady,
                        slotId = Math.Max(0, player.SlotId),
                    }).ToArray(),
                },
            },
        });
    }

    private static string BuildDegradedLocalOnlyFixtureJson(
        string name,
        string screen,
        ScenarioRunSnapshot? run,
        MultiplayerRestoreSnapshot multiplayer)
    {
        var localPlayer = multiplayer.Players.First(player => player.Id == multiplayer.LocalPlayerId);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "spirectl.fixture/v0",
            name = $"scenario-{name}",
            screen,
            run = new
            {
                seed = run?.Seed,
                currentActIndex = Math.Max(0, (run?.Act ?? 1) - 1),
                actFloor = Math.Max(1, run?.Floor ?? 1),
                view = new { playerId = localPlayer.Id },
                players = new[]
                {
                    new
                    {
                        id = localPlayer.Id,
                        characterId = string.IsNullOrWhiteSpace(localPlayer.Character) ? localPlayer.SelectedCharacterId : localPlayer.Character,
                    },
                },
            },
        });
    }

    private static MultiplayerRestoreResultSnapshot LobbyRestoreResult(MultiplayerRestoreSnapshot multiplayer)
        => new(
            MultiplayerRestoreModeSnapshot.LobbyOnly,
            RemotePlayerMode: multiplayer.Players.Any(player => player.IsRemote) ? "placeholder" : "none",
            multiplayer.LocalPlayerId,
            multiplayer.HostPlayerId,
            RestoredPlayerIds: [.. multiplayer.Players.Select(player => player.Id)],
            OmittedRemotePlayerIds: [],
            RequiresRemoteClients: false);

    private static MultiplayerRestoreResultSnapshot DegradedRestoreResult(MultiplayerRestoreSnapshot multiplayer)
        => new(
            MultiplayerRestoreModeSnapshot.DegradedLocalOnly,
            RemotePlayerMode: "omitted",
            multiplayer.LocalPlayerId,
            multiplayer.HostPlayerId,
            RestoredPlayerIds: [.. multiplayer.Players.Where(player => player.Id == multiplayer.LocalPlayerId).Select(player => player.Id)],
            OmittedRemotePlayerIds: [.. multiplayer.Players.Where(player => player.IsRemote).Select(player => player.Id)],
            RequiresRemoteClients: true);

    private static IReadOnlyList<object> FieldReportsJson(IReadOnlyList<RestoreFieldReportSnapshot> reports)
    {
        return [.. reports.Select(report => new
        {
            path = report.Path,
            capture = FieldFidelityName(report.Capture),
            restore = FieldFidelityName(report.Restore),
            validationKey = report.ValidationKey,
            reasonCode = report.ReasonCode,
            message = report.Message,
        }).Cast<object>()];
    }

    private static string FieldFidelityName(RestoreFieldFidelity fidelity)
    {
        return fidelity switch
        {
            RestoreFieldFidelity.Exact => "exact",
            RestoreFieldFidelity.Partial => "partial",
            RestoreFieldFidelity.Inferred => "inferred",
            RestoreFieldFidelity.Omitted => "omitted",
            RestoreFieldFidelity.Unsupported => "unsupported",
            RestoreFieldFidelity.DegradedLocalMultiplayer => "degraded-local-multiplayer",
            _ => "unspecified",
        };
    }

    private static string ActionKindName(SemanticActionKind kind)
    {
        return kind switch
        {
            SemanticActionKind.PlayCard => "play-card",
            SemanticActionKind.UsePotion => "use-potion",
            SemanticActionKind.Choose => "choose",
            SemanticActionKind.ConfirmSelection => "confirm-selection",
            SemanticActionKind.CancelSelection => "cancel-selection",
            SemanticActionKind.SelectMapNode => "select-map-node",
            SemanticActionKind.EndTurn => "end-turn",
            SemanticActionKind.CancelEndTurn => "cancel-end-turn",
            SemanticActionKind.Ready => "ready",
            SemanticActionKind.Unready => "unready",
            SemanticActionKind.SelectCharacter => "select-character",
            _ => "unspecified",
        };
    }

    private static ScenarioCaptureResultSnapshot FailureCapture(string requestId, ScenarioFailureCode code, string detailCode, string message)
    {
        return ScenarioCaptureResultSnapshot.Failure(
            requestId,
            DataSourceKind.Live,
            provisional: false,
            code,
            message,
            [new ScenarioFailureDetail("code", detailCode, message)]);
    }

    private static ScenarioRestoreResultSnapshot FailureRestore(string requestId, ScenarioFailureCode code, string detailCode, string message)
    {
        return ScenarioRestoreResultSnapshot.Failure(
            requestId,
            ScenarioRestoreQuality.Unsupported,
            code,
            message,
            [new ScenarioFailureDetail("code", detailCode, message)]);
    }
}
