using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Restore;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using System.Text.Json;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

#if ENABLE_STS2_LIVE_HOST
public sealed class ScenarioProviderTests
{
    [Fact]
    public void CaptureIncludesMultiplayerLobbyMetadata()
    {
        var provider = CreateProvider(LobbySnapshot());

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-1", IncludeExact: false, PlayerId: null));

        Assert.Null(result.Error);
        Assert.NotNull(result.Scenario);
        Assert.NotNull(result.Scenario.Multiplayer);
        Assert.True(result.Scenario.Multiplayer.IsMultiplayer);
        Assert.Equal(MultiplayerRestoreModeSnapshot.LobbyOnly, result.Scenario.Multiplayer.RestoreMode);
        Assert.Equal("p:100", result.Scenario.Multiplayer.LocalPlayerId);
        Assert.Equal("p:100", result.Scenario.Multiplayer.HostPlayerId);
        Assert.Equal("host", result.Scenario.Multiplayer.LocalPlayerRole);
        Assert.False(result.Scenario.Multiplayer.RequiresRemoteClients);
        Assert.False(result.Scenario.Multiplayer.DegradedLocalOnlyAvailable);
        Assert.Equal("start-run", result.Scenario.Multiplayer.Lobby!.LobbyId);
        Assert.Contains(result.Scenario.Multiplayer.Players, player =>
            player.Id == "p:200"
            && player.NetId == "200"
            && player.SlotId == 1
            && player.SelectedCharacterId == "silent"
            && player.IsReady
            && player.IsRemote);
    }

    [Fact]
    public void CaptureRejectsUnsupportedScreen()
    {
        var provider = CreateProvider(Snapshot("main-menu"));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-2", IncludeExact: false, PlayerId: null));

        Assert.NotNull(result.Error);
        Assert.Contains(result.Error.Details, detail => detail.Value == "unsupported_source_screen");
    }

    [Fact]
    public void CaptureOmitsStaticDefaultsAndIncludesSparseChoices()
    {
        var provider = CreateProvider(Snapshot("combat"));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-3", IncludeExact: false, PlayerId: null));

        Assert.Null(result.Error);
        Assert.NotNull(result.Scenario);
        Assert.Equal("spirectl.scenario/v0", result.Scenario.SchemaVersion);
        Assert.Equal(ScenarioRestoreQuality.Partial, result.Scenario.Restore.Quality);
        Assert.Null(result.ExactBundlePayload);
        using var document = JsonDocument.Parse(result.Scenario.ScreenStateJson);
        Assert.True(document.RootElement.TryGetProperty("combat", out var combat));
        Assert.True(combat.TryGetProperty("handCardIds", out var handCardIds));
        Assert.Equal("c1", handCardIds[0].GetString());
        Assert.Equal("draw-1", combat.GetProperty("drawPileCardIds")[0].GetString());
        Assert.Equal("discard-1", combat.GetProperty("discardPileCardIds")[0].GetString());
        Assert.Equal("exhaust-1", combat.GetProperty("exhaustPileCardIds")[0].GetString());
        Assert.True(combat.TryGetProperty("hand", out var hand));
        Assert.Equal(1, hand[0].GetProperty("cost").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("choices", out var choices));
        Assert.Equal("choice:combat:c1", choices[0].GetProperty("id").GetString());
    }

    [Fact]
    public void CaptureReportsCombatOmissionsHonestly()
    {
        var provider = CreateProvider(Snapshot("combat"));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-fidelity", IncludeExact: false, PlayerId: null));
        var reports = result.Scenario!.Restore.FieldReports;

        Assert.Contains(reports, report => report.Path == "schemaVersion" && report.Capture == RestoreFieldFidelity.Exact && report.Restore == RestoreFieldFidelity.Exact && report.ValidationKey);
        Assert.Contains(reports, report => report.Path == "screen.className" && report.Capture == RestoreFieldFidelity.Exact && report.Restore == RestoreFieldFidelity.Inferred);
        Assert.Contains(reports, report => report.Path == "combat.turn" && report.Capture == RestoreFieldFidelity.Exact && report.Restore == RestoreFieldFidelity.Exact && report.ValidationKey);
        Assert.Contains(reports, report => report.Path == "combat.players[].hand[]" && report.Capture == RestoreFieldFidelity.Exact && report.Restore == RestoreFieldFidelity.Partial && report.ValidationKey);
        Assert.Contains(reports, report => report.Path == "combat.drawPile" && report.Capture == RestoreFieldFidelity.Partial && report.Restore == RestoreFieldFidelity.Unsupported);
        Assert.Contains(reports, report => report.Path == "combat.rngContinuation" && report.Capture == RestoreFieldFidelity.Omitted && report.Restore == RestoreFieldFidelity.Unsupported);
        Assert.Contains(reports, report => report.Path == "privateUiInternals" && report.Capture == RestoreFieldFidelity.Omitted && report.Restore == RestoreFieldFidelity.Unsupported);
    }

    [Fact]
    public void CaptureIncludesActiveMultiplayerMetadataWithRemoteClientLimits()
    {
        var provider = CreateProvider(Snapshot("combat", players: [
            new PlayerStateSnapshot("p:100", "ironclad", 70, 80, IsLocal: true, IsHost: true),
            new PlayerStateSnapshot("p:200", "silent", 70, 70, IsRemote: true),
        ]));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-active-mp", IncludeExact: true, PlayerId: null));

        Assert.Null(result.Error);
        Assert.NotNull(result.Scenario!.Multiplayer);
        Assert.True(result.Scenario.Multiplayer.IsMultiplayer);
        Assert.Equal(MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported, result.Scenario.Multiplayer.RestoreMode);
        Assert.True(result.Scenario.Multiplayer.RequiresRemoteClients);
        Assert.True(result.Scenario.Multiplayer.DegradedLocalOnlyAvailable);
        Assert.Contains(result.Scenario.Multiplayer.Players, player => player.Id == "p:200" && player.IsRemote);
        Assert.Contains(result.Scenario.Multiplayer.Limitations, limitation => limitation.Field == "multiplayer.players[isRemote=true]" && limitation.Code == "remote-player-degraded-local");
        Assert.Contains(result.Scenario.Multiplayer.Limitations, limitation => limitation.Field == "multiplayer.remoteRuntime" && limitation.Code == "remote-clients-not-captured");
        Assert.Contains(result.Scenario.Restore.FieldReports, report => report.Path == "multiplayer.players[isRemote=true]" && report.Restore == RestoreFieldFidelity.DegradedLocalMultiplayer);
        Assert.Contains(result.Scenario.Restore.FieldReports, report => report.Path == "multiplayer.remoteRuntime" && report.Capture == RestoreFieldFidelity.Omitted && report.Restore == RestoreFieldFidelity.Unsupported);
        using var bundle = JsonDocument.Parse(result.ExactBundlePayload!.Data);
        Assert.True(bundle.RootElement.TryGetProperty("multiplayer", out var multiplayer));
        Assert.Equal("p:100", multiplayer.GetProperty("localPlayerId").GetString());
    }

    [Fact]
    public void CaptureSinglePlayerOmitsMultiplayerMetadata()
    {
        var provider = CreateProvider(Snapshot("combat"));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-single", IncludeExact: false, PlayerId: null));

        Assert.Null(result.Error);
        Assert.Null(result.Scenario!.Multiplayer);
    }

    [Fact]
    public void CaptureIncludesExactBundleOnlyWhenRequested()
    {
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"));

        var sparse = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-4a", IncludeExact: false, PlayerId: null));
        var exact = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-4b", IncludeExact: true, PlayerId: null));

        Assert.Null(sparse.ExactBundlePayload);
        Assert.NotNull(exact.ExactBundlePayload);
        Assert.Equal("spirectl.scenario.bundle/v0", exact.ExactBundlePayload.FormatVersion);
    }

    [Fact]
    public void CaptureIncludesRestoreFieldReportsInScenarioAndExactBundle()
    {
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-field-reports", IncludeExact: true, PlayerId: null));

        Assert.Contains(result.Scenario!.Restore.FieldReports, report => report.Path == "shop.purchasableItems[]");
        using var bundle = JsonDocument.Parse(result.ExactBundlePayload!.Data);
        Assert.True(bundle.RootElement.TryGetProperty("fieldReports", out var fieldReports));
        Assert.Contains(fieldReports.EnumerateArray(), report => report.GetProperty("path").GetString() == "shop.purchasableItems[]");
    }

    [Theory]
    [InlineData("Screens.Shops.NMerchantInventory", "shop")]
    [InlineData("Rooms.NEventRoom", "eventRoom")]
    [InlineData("Rooms.NTreasureRoom", "treasureRoom")]
    [InlineData("Screens.NChooseARelicSelection", "relicSelection")]
    [InlineData("Rooms.NRestSiteRoom", "restSite")]
    [InlineData("Screens.NRewardsScreen", "rewards")]
    [InlineData("Screens.Map.NMapScreen", "map")]
    [InlineData("Screens.CardSelection.NCardRewardSelectionScreen", "selection")]
    [InlineData("Screens.CardSelection.NSimpleCardSelectScreen", "selection")]
    [InlineData("Screens.CardSelection.NDeckCardSelectScreen", "selection")]
    [InlineData("Screens.CardSelection.NChooseABundleSelectionScreen", "selection")]
    public void CaptureIncludesScreenIdChoiceState(string screen, string familyKey)
    {
        var provider = CreateProvider(Snapshot(screen));

        var result = provider.Capture(new ScenarioCaptureRequestSnapshot($"capture-{screen}", IncludeExact: false, PlayerId: null));

        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.Scenario!.ScreenStateJson);
        Assert.True(document.RootElement.TryGetProperty(familyKey, out var familyState));
        Assert.Equal($"choice:{screen}:c1", familyState.GetProperty("choiceIds")[0].GetString());
        Assert.Equal("choose", familyState.GetProperty("availableActionKinds")[0].GetString());
        Assert.Contains(result.Scenario.Restore.FieldReports, report => report.Path == ExpectedTypedSectionPath(screen) && report.Capture == RestoreFieldFidelity.Exact);
        Assert.Contains(result.Scenario.Restore.FieldReports, report => report.Path == "choices[].preferredActionRef" && report.Restore == RestoreFieldFidelity.Inferred);
        Assert.Contains(result.Scenario.Restore.FieldReports, report => report.Path == "availableActions[].ownerPlayerId" && report.Restore == RestoreFieldFidelity.Partial);
    }

    [Fact]
    public void SparseRestoreUsesScreenStateForFixtureRecipe()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"), loader);
        var scenario = provider.Capture(new ScenarioCaptureRequestSnapshot("capture-shop", IncludeExact: false, PlayerId: null)).Scenario!;

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-shop", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true));

        Assert.Null(result.Error);
        Assert.Contains("\"shop\":", loader.LastFixtureJson);
        Assert.Contains("\"screen\":\"Screens.Shops.NMerchantInventory\"", loader.LastFixtureJson);
    }

    [Fact]
    public void RestoreReturnsPartialForSparse()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"), loader);
        var scenario = ScenarioDocument("Screens.Shops.NMerchantInventory");

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-1", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true));

        Assert.Null(result.Error);
        Assert.Equal(ScenarioRestoreQuality.Partial, result.Quality);
        Assert.False(result.ExactBundleUsed);
        Assert.False(result.SparseFallbackUsed);
        Assert.Equal("Screens.Shops.NMerchantInventory", loader.LastRequestScreen);
    }

    [Fact]
    public void RestoreUsesAsyncFixtureLoadPath()
    {
        var loader = new AsyncOnlyFixtureLoader();
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"), loader);
        var scenario = ScenarioDocument("Screens.Shops.NMerchantInventory");

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-async", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true));

        Assert.Null(result.Error);
        Assert.True(loader.AsyncLoadCalled);
    }

    [Fact]
    public void RestoreReturnsDegradedWhenExactFailsWithFallback()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(Snapshot("combat"), loader);
        var scenario = ScenarioDocument("combat");

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-2", scenario, ExactBundle: "not json"u8.ToArray(), ExactBundleContentType: "application/json", AllowSparseFallback: true));

        Assert.Null(result.Error);
        Assert.Equal(ScenarioRestoreQuality.Degraded, result.Quality);
        Assert.False(result.ExactBundleUsed);
        Assert.True(result.SparseFallbackUsed);
        Assert.Contains(result.Notices, notice => notice.Code == "exact-restore-failed-fallback-used");
    }

    [Fact]
    public void RestoreRoutesSaveBackedExactBundlesBeforeFixtureFallback()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(Snapshot("combat"), loader);
        var scenario = ScenarioDocument("combat") with
        {
            Restore = new ScenarioRestoreSnapshot(
                ScenarioRestoreMode.Sparse,
                ScenarioRestoreQuality.Partial,
                new ScenarioExactBundleMetadataSnapshot(
                    "current_run_mp.save",
                    "spirectl.scenario.save-run/v0",
                    Sha256: string.Empty,
                    SizeBytes: 2,
                    VersionSensitive: true,
                    ContentType: "application/vnd.spirectl.sts2-save+json"),
                CompatibilityNotes: [],
                FieldReports: []),
        };

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot(
            "restore-save",
            scenario,
            ExactBundle: "{}"u8.ToArray(),
            ExactBundleContentType: "application/vnd.spirectl.sts2-save+json",
            AllowSparseFallback: false));

        Assert.NotNull(result.Error);
        Assert.Contains(result.Error.Details, detail => detail.Value == "save_type_unavailable");
        Assert.Empty(loader.LastFixtureJson);
    }

    [Fact]
    public void RestoreLobbyUsesMultiplayerFixtureAndReportsPlaceholders()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(LobbySnapshot(), loader);
        var scenario = LobbyScenarioDocument();

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-lobby", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true));

        Assert.Null(result.Error);
        Assert.Equal(ScenarioRestoreQuality.Partial, result.Quality);
        Assert.NotNull(result.MultiplayerRestore);
        Assert.Equal(MultiplayerRestoreModeSnapshot.LobbyOnly, result.MultiplayerRestore.Mode);
        Assert.Equal("placeholder", result.MultiplayerRestore.RemotePlayerMode);
        Assert.Contains("p:200", result.MultiplayerRestore.RestoredPlayerIds);
        Assert.Contains("\"screen\":\"Screens.CharacterSelect.NCharacterSelectScreen\"", loader.LastFixtureJson);
        Assert.Contains("\"schemaVersion\":\"spirectl.fixture/v0\"", loader.LastFixtureJson);
        Assert.Contains("\"localPlayerId\":\"p:100\"", loader.LastFixtureJson);
        Assert.Contains("\"id\":\"p:200\"", loader.LastFixtureJson);
    }

    [Fact]
    public void RestoreActiveMultiplayerRequiresDegradationFlag()
    {
        var provider = CreateProvider(Snapshot("combat"));
        var scenario = ActiveMultiplayerScenarioDocument();

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-active", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true));

        Assert.NotNull(result.Error);
        Assert.Contains(result.Error.Details, detail => detail.Field == "code" && detail.Value == "degradation_flag_required");
    }

    [Fact]
    public void RestoreActiveMultiplayerWithFlagReportsDegradedLocalOnly()
    {
        var loader = new RecordingFixtureLoader(success: true);
        var provider = CreateProvider(Snapshot("combat"), loader);
        var scenario = ActiveMultiplayerScenarioDocument();

        var result = provider.Restore(new ScenarioRestoreRequestSnapshot("restore-active-flag", scenario, ExactBundle: null, ExactBundleContentType: null, AllowSparseFallback: true, AllowDegradedLocalMultiplayer: true));

        Assert.Null(result.Error);
        Assert.Equal(ScenarioRestoreQuality.Degraded, result.Quality);
        Assert.NotNull(result.MultiplayerRestore);
        Assert.Equal(MultiplayerRestoreModeSnapshot.DegradedLocalOnly, result.MultiplayerRestore.Mode);
        Assert.Equal("omitted", result.MultiplayerRestore.RemotePlayerMode);
        Assert.Equal(["p:100"], result.MultiplayerRestore.RestoredPlayerIds);
        Assert.Equal(["p:200"], result.MultiplayerRestore.OmittedRemotePlayerIds);
        Assert.Contains("\"id\":\"p:100\"", loader.LastFixtureJson);
        Assert.DoesNotContain("\"id\":\"p:200\"", loader.LastFixtureJson);
    }

    private static Sts2ScenarioProvider CreateProvider(GameStateSnapshot snapshot, IFixtureLoader? loader = null)
    {
        return new Sts2ScenarioProvider(
            new FixedSnapshotExtractor(snapshot),
            new DefaultPerspectiveProvider(),
            loader ?? new RecordingFixtureLoader(success: true),
            new InMemoryLogStream(source: DataSourceKind.Live, provisional: false));
    }

    private static GameStateSnapshot Snapshot(string screen, IReadOnlyList<PlayerStateSnapshot>? players = null)
    {
        var local = players?.FirstOrDefault(player => player.IsLocal) ?? new PlayerStateSnapshot("p1", "ironclad", 67, 80, IsLocal: true, IsHost: true);
        var runPlayers = players ?? [local];
        var card = new CardStateSnapshot("c1", "Jab", 1, "p1", Playable: true, UnplayableReason: null, TargetIds: [], Upgraded: false);
        var choice = new ChoiceSnapshot($"choice:{screen}:c1", "Jab", "card", Provisional: false, OwnerPlayerId: "p1");
        return new GameStateSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-test",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: screen,
            ScreenTitle: screen == "Screens.Shops.NMerchantInventory" ? "Shop" : screen == "combat" ? "Combat" : screen,
            ScreenInstanceId: $"screen:{screen}:1",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot("SEED", Floor: screen == "Screens.Shops.NMerchantInventory" ? 4 : 3, Act: 1, runPlayers, runPlayers.ToDictionary(player => player.Id)),
            Combat: screen == "combat"
                ? new CombatStateSnapshot(
                    Turn: 1,
                    ActivePlayerId: "p1",
                    IsPlayerTurn: true,
                    Hand: [card],
                    Players: [new CombatPlayerStateSnapshot("p1", "ironclad", 67, 80, Block: 0, Energy: 3, MaxEnergy: 3, Hand: [card], IsLocal: true, IsHost: true, DrawPileCardIds: ["draw-1"], DiscardPileCardIds: ["discard-1"], ExhaustPileCardIds: ["exhaust-1"])],
                    Enemies: [],
                    PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(),
                    DrawPileCardIds: ["draw-1"],
                    DiscardPileCardIds: ["discard-1"],
                    ExhaustPileCardIds: ["exhaust-1"])
                : null,
            Choices: [choice],
            AvailableActions: [new AvailableActionSnapshot($"action:{screen}:choose", SemanticActionKind.Choose, "Choose", "sts2 act choose --choice x", Provisional: false, new ActionArgumentsSnapshot("p1", null, null, choice.Id, null, null))],
            Notices: [],
            Debug: null);
    }

    private static string ExpectedTypedSectionPath(string screen)
        => screen switch
        {
            "Screens.Shops.NMerchantInventory" => "shop.purchasableItems[]",
            "Rooms.NEventRoom" => "eventRoom.options[]",
            "Rooms.NTreasureRoom" => "treasureRoom.relics[]",
            "Screens.NChooseARelicSelection" => "relicSelection.relics[]",
            "Rooms.NRestSiteRoom" => "restSite.controls[]",
            "Screens.NRewardsScreen" => "rewards.rewards[]",
            "Screens.Map.NMapScreen" => "map.nodes[]",
            "Screens.CardSelection.NCardRewardSelectionScreen" => "cardSelection.cards[]",
            "Screens.CardSelection.NSimpleCardSelectScreen" => "simpleCardSelection.choices[]",
            "Screens.CardSelection.NDeckCardSelectScreen" => "deckCardSelection.deckCards[]",
            "Screens.CardSelection.NChooseABundleSelectionScreen" => "bundleSelection.bundles[]",
            _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "Unexpected screen."),
        };

    private static GameStateSnapshot LobbySnapshot()
    {
        var local = new LobbyPlayerSnapshot("p:100", "not-ready", null, "ironclad", IsReady: false, SlotId: 0, IsLocal: true, IsHost: true, IsRemote: false);
        var remote = new LobbyPlayerSnapshot("p:200", "ready", null, "silent", IsReady: true, SlotId: 1, IsLocal: false, IsHost: false, IsRemote: true);
        var ironclad = new Spirectl.Sts2.Core.State.LobbyCharacterSnapshot("ironclad", "Ironclad", IsUnlocked: true);
        var silent = new Spirectl.Sts2.Core.State.LobbyCharacterSnapshot("silent", "Silent", IsUnlocked: true);
        var players = new[] { local, remote };
        var characters = new[] { ironclad, silent };
        return new GameStateSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-test",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "Screens.CharacterSelect.NCharacterSelectScreen",
            ScreenTitle: "Character Select",
            ScreenInstanceId: "screen:lobby:start-run:1",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p:100", UsesDefault: false),
            Menu: null,
            Lobby: new LobbyStateSnapshot(
                "start-run",
                "selecting",
                players,
                characters,
                LocalPlayerId: "p:100",
                HostPlayerId: "p:100",
                LocalPlayerRole: "host",
                PlayersById: players.ToDictionary(player => player.Id),
                AvailableCharactersById: characters.ToDictionary(character => character.Id)),
            Run: null,
            Combat: null,
            Choices: [new ChoiceSnapshot("lobby:ready", "Ready", "lobby-flow", Provisional: false, OwnerPlayerId: "p:100")],
            AvailableActions: [new AvailableActionSnapshot("action:lobby:ready", SemanticActionKind.Ready, "Ready", "sts2 act ready", Provisional: false, new ActionArgumentsSnapshot("p:100", null, null, null, null, null))],
            Notices: [],
            Debug: null);
    }

    private static ScenarioDocumentSnapshot ScenarioDocument(string screen)
    {
        return new ScenarioDocumentSnapshot(
            SchemaVersion: "spirectl.scenario/v0",
            Name: $"{screen}-scenario",
            Description: string.Empty,
            CreatedAt: "2026-04-24T00:00:00Z",
            Source: new ScenarioSourceSnapshot("sts2-test", BridgeBuildInfo.BridgeVersion, "0.0.0", new ScenarioScreenSnapshot(screen, screen, $"screen:{screen}:1"), new ScenarioPerspectiveSnapshot("local", "p1")),
            Restore: new ScenarioRestoreSnapshot(ScenarioRestoreMode.Sparse, ScenarioRestoreQuality.Partial, ExactBundle: null, CompatibilityNotes: [], FieldReports: []),
            Run: new ScenarioRunSnapshot("SEED", Act: 1, Floor: screen == "Screens.Shops.NMerchantInventory" ? 4 : 3, Ascension: 0, Players: [new ScenarioPlayerSnapshot("p1", "ironclad", IsLocal: true, IsHost: true, IsRemote: false)]),
            ScreenStateJson: "{}",
            Notices: []);
    }

    private static ScenarioDocumentSnapshot LobbyScenarioDocument()
        => new(
            SchemaVersion: "spirectl.scenario/v0",
            Name: "lobby-scenario",
            Description: string.Empty,
            CreatedAt: "2026-04-24T00:00:00Z",
            Source: new ScenarioSourceSnapshot("sts2-test", BridgeBuildInfo.BridgeVersion, "0.0.0", new ScenarioScreenSnapshot("Screens.CharacterSelect.NCharacterSelectScreen", "Character Select", "screen:lobby:start-run:1"), new ScenarioPerspectiveSnapshot("local", "p:100")),
            Restore: new ScenarioRestoreSnapshot(ScenarioRestoreMode.Sparse, ScenarioRestoreQuality.Partial, ExactBundle: null, CompatibilityNotes: [], FieldReports: []),
            Run: null,
            ScreenStateJson: "{}",
            Notices: [],
            Multiplayer: MultiplayerMetadata(MultiplayerRestoreModeSnapshot.LobbyOnly, requiresRemoteClients: false, degradedLocalOnlyAvailable: false));

    private static ScenarioDocumentSnapshot ActiveMultiplayerScenarioDocument()
        => new(
            SchemaVersion: "spirectl.scenario/v0",
            Name: "active-multiplayer",
            Description: string.Empty,
            CreatedAt: "2026-04-24T00:00:00Z",
            Source: new ScenarioSourceSnapshot("sts2-test", BridgeBuildInfo.BridgeVersion, "0.0.0", new ScenarioScreenSnapshot("combat", "Combat", "screen:combat:1"), new ScenarioPerspectiveSnapshot("local", "p:100")),
            Restore: new ScenarioRestoreSnapshot(ScenarioRestoreMode.Sparse, ScenarioRestoreQuality.Degraded, ExactBundle: null, CompatibilityNotes: [], FieldReports: []),
            Run: new ScenarioRunSnapshot("SEED", Act: 1, Floor: 3, Ascension: 0, Players: [
                new ScenarioPlayerSnapshot("p:100", "ironclad", IsLocal: true, IsHost: true, IsRemote: false),
                new ScenarioPlayerSnapshot("p:200", "silent", IsLocal: false, IsHost: false, IsRemote: true),
            ]),
            ScreenStateJson: "{}",
            Notices: [],
            Multiplayer: MultiplayerMetadata(MultiplayerRestoreModeSnapshot.ActiveMultiplayerUnsupported, requiresRemoteClients: true, degradedLocalOnlyAvailable: true));

    private static MultiplayerRestoreSnapshot MultiplayerMetadata(
        MultiplayerRestoreModeSnapshot mode,
        bool requiresRemoteClients,
        bool degradedLocalOnlyAvailable)
        => new(
            IsMultiplayer: true,
            RestoreMode: mode,
            LocalPlayerId: "p:100",
            HostPlayerId: "p:100",
            LocalPlayerRole: "host",
            Players: [
                new MultiplayerPlayerSnapshot("p:100", "100", SlotId: 0, DisplayName: string.Empty, SelectedCharacterId: "ironclad", IsReady: false, IsLocal: true, IsHost: true, IsRemote: false, Character: "ironclad"),
                new MultiplayerPlayerSnapshot("p:200", "200", SlotId: 1, DisplayName: string.Empty, SelectedCharacterId: "silent", IsReady: true, IsLocal: false, IsHost: false, IsRemote: true, Character: "silent"),
            ],
            Lobby: mode == MultiplayerRestoreModeSnapshot.LobbyOnly
                ? new MultiplayerLobbySnapshot("start-run", "selecting", [
                    new Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot("ironclad", "Ironclad", IsUnlocked: true),
                    new Spirectl.Sts2.Core.Restore.LobbyCharacterSnapshot("silent", "Silent", IsUnlocked: true),
                ])
                : null,
            RequiresRemoteClients: requiresRemoteClients,
            DegradedLocalOnlyAvailable: degradedLocalOnlyAvailable,
            Limitations: []);

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective) => snapshot;
    }

    private sealed class RecordingFixtureLoader(bool success) : IFixtureLoader
    {
        public string? LastRequestScreen { get; private set; }
        public string LastFixtureJson { get; private set; } = string.Empty;

        public FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
        {
            LastFixtureJson = request.FixtureJson;
            using var document = JsonDocument.Parse(request.FixtureJson);
            LastRequestScreen = document.RootElement.GetProperty("screen").GetString();
            return success
                ? FixtureLoadResult.Success(request.RequestId, request.FixtureName, request.SourcePath, DataSourceKind.Live, provisional: false, LastRequestScreen ?? string.Empty, $"screen:{LastRequestScreen}:1", new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false), [])
                : FixtureLoadResult.Failure(request.RequestId, request.FixtureName, request.SourcePath, DataSourceKind.Live, provisional: false, FixtureLoadFailureCode.RuntimeFailure, "fixture failed", []);
        }
    }

    private sealed class AsyncOnlyFixtureLoader : IFixtureLoader
    {
        public bool AsyncLoadCalled { get; private set; }

        public FixtureLoadResult Load(FixtureLoadRequestSnapshot request)
            => throw new InvalidOperationException("Scenario restore should use the async fixture load path.");

        public async Task<FixtureLoadResult> LoadAsync(FixtureLoadRequestSnapshot request)
        {
            await Task.Yield();
            AsyncLoadCalled = true;
            return FixtureLoadResult.Success(
                request.RequestId,
                request.FixtureName,
                request.SourcePath,
                DataSourceKind.Live,
                provisional: false,
                screenType: "Screens.Shops.NMerchantInventory",
                screenInstanceId: "screen:shop:1",
                resolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
                notices: [],
                screenTitle: "Shop");
        }
    }
}
#endif
