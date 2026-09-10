using System.Text.Json;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

#if ENABLE_STS2_LIVE_HOST
public sealed class RecordedFixtureProviderTests
{
    [Fact]
    public void RecordCombatProducesScreenEntryFixtureAndMetadata()
    {
        var provider = CreateProvider(Snapshot("combat"));

        var result = provider.Record(new RecordedFixtureRequestSnapshot("record-combat", "local", "p1"));

        Assert.Null(result.Error);
        Assert.Equal("record-combat", result.Success!.RequestId);
        Assert.Equal("spirectl.recorded-fixture/v0", result.Success.Metadata.SchemaVersion);
        Assert.Equal("combat", result.Success.Metadata.Screen.Type);
        Assert.Equal(RecordedFixtureRestoreQuality.Partial, result.Success.Metadata.RestoreQuality);
        Assert.Contains(result.Success.Metadata.KnownOmissions, note => note.Code == "mid-combat-deltas-omitted");
        Assert.Contains(result.Success.Metadata.KnownOmissions, note => note.Code == "rng-continuation-omitted");
        Assert.True(string.IsNullOrWhiteSpace(result.Success.FixtureYaml));

        using var document = JsonDocument.Parse(result.Success.FixtureJson);
        var root = document.RootElement;
        Assert.Equal("spirectl.fixture/v0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("recorded-current-screen", root.GetProperty("name").GetString());
        var run = root.GetProperty("run");
        Assert.Equal("p1", run.GetProperty("view").GetProperty("playerId").GetString());
        Assert.Equal("SEED", run.GetProperty("seed").GetString());
        Assert.Equal(0, run.GetProperty("currentActIndex").GetInt32());
        Assert.Equal(3, run.GetProperty("actFloor").GetInt32());
        Assert.Equal("p1", run.GetProperty("players")[0].GetProperty("id").GetString());
        Assert.Equal(
            "FuzzyWurmCrawlerWeak",
            run.GetProperty("currentRoom").GetProperty("combat").GetProperty("encounterId").GetString());
    }

    [Fact]
    public void RecordUnsupportedScreenFailsWithoutFixturePayload()
    {
        var provider = CreateProvider(Snapshot("Rooms.NEventRoom"));

        var result = provider.Record(new RecordedFixtureRequestSnapshot("record-event", "local", "p1"));

        Assert.Null(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(RecordedFixtureFailureCode.InvalidAction, result.Error.Code);
        Assert.Contains(result.Error.Details, detail => detail.Field == "screen.id" && detail.Value == "Rooms.NEventRoom");
    }

    [Fact]
    public void RecordShopWithoutRecipeDataReportsSpecificUnavailableCode()
    {
        var provider = CreateProvider(Snapshot("Screens.Shops.NMerchantInventory"));

        var result = provider.Record(new RecordedFixtureRequestSnapshot("record-shop", "local", "p1"));

        Assert.Null(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains(result.Error.Details, detail => detail.Field == "code" && detail.Value == "shop_recipe_unavailable");
    }

    [Fact]
    public void RecordMainMenuWithoutRunProducesSyntheticScreenEntryFixture()
    {
        var provider = CreateProvider(Snapshot("main-menu", includeRun: false));

        var result = provider.Record(new RecordedFixtureRequestSnapshot("record-main-menu", "local", "p1"));

        Assert.Null(result.Error);
        Assert.Equal("main-menu", result.Success!.Metadata.Screen.Type);
        Assert.Contains(result.Success.Metadata.Notices, notice => notice.Code == "main-menu-default-player");

        using var document = JsonDocument.Parse(result.Success.FixtureJson);
        var root = document.RootElement;
        Assert.Equal("spirectl.fixture/v0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("main-menu", root.GetProperty("rootScene").GetString());
        var run = root.GetProperty("run");
        Assert.Equal("p1", run.GetProperty("view").GetProperty("playerId").GetString());
        Assert.Equal(0, run.GetProperty("currentActIndex").GetInt32());
        Assert.Equal(1, run.GetProperty("actFloor").GetInt32());
        Assert.Equal("ironclad", run.GetProperty("players")[0].GetProperty("characterId").GetString());
    }

    [Theory]
    [InlineData("Screens.Map.NMapScreen")]
    [InlineData("Screens.NRewardsScreen")]
    [InlineData("Rooms.NRestSiteRoom")]
    public void RecordRunBackedScreenProducesScreenEntryFixture(string screen)
    {
        var provider = CreateProvider(Snapshot(screen));

        var result = provider.Record(new RecordedFixtureRequestSnapshot($"record-{screen}", "local", "p1"));

        Assert.Null(result.Error);
        Assert.Equal(screen, result.Success!.Metadata.Screen.Type);

        using var document = JsonDocument.Parse(result.Success.FixtureJson);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("screen", out _));
        var run = root.GetProperty("run");
        Assert.Equal("SEED", run.GetProperty("seed").GetString());
        Assert.Equal("p1", run.GetProperty("players")[0].GetProperty("id").GetString());
        if (screen == "Screens.Map.NMapScreen")
        {
            Assert.True(run.GetProperty("currentRoom").TryGetProperty("mapRoom", out _));
        }
        else if (screen == "Rooms.NRestSiteRoom")
        {
            Assert.True(run.GetProperty("currentRoom").TryGetProperty("restSite", out _));
        }
        else
        {
            Assert.True(run.GetProperty("players")[0].GetProperty("overlays")[0].TryGetProperty("rewards", out _));
        }
    }

    [Fact]
    public void RecordMultiplayerLobbyProducesLobbyFixture()
    {
        var provider = CreateProvider(Snapshot("Screens.CharacterSelect.NCharacterSelectScreen", includeLobby: true));

        var result = provider.Record(new RecordedFixtureRequestSnapshot("record-lobby", "local", "p:1"));

        Assert.Null(result.Error);
        Assert.Equal("Screens.CharacterSelect.NCharacterSelectScreen", result.Success!.Metadata.Screen.Type);

        using var document = JsonDocument.Parse(result.Success.FixtureJson);
        var root = document.RootElement;
        var characterSelect = root.GetProperty("characterSelect");
        var lobby = characterSelect.GetProperty("lobby");
        Assert.Equal("start-run", characterSelect.GetProperty("kind").GetString());
        Assert.Equal("p:1", lobby.GetProperty("localPlayerId").GetString());
        Assert.Equal("p:1", lobby.GetProperty("hostPlayerId").GetString());
        Assert.Equal("ironclad", lobby.GetProperty("players")[0].GetProperty("characterId").GetString());
    }

    private static Sts2RecordedFixtureProvider CreateProvider(GameStateSnapshot snapshot)
        => new(
            new FixedSnapshotExtractor(snapshot),
            new DefaultPerspectiveProvider(),
            new InMemoryLogStream(source: DataSourceKind.Live, provisional: false));

    private static GameStateSnapshot Snapshot(string screen, bool includeRun = true, bool includeLobby = false)
    {
        var player = new PlayerStateSnapshot("p1", "ironclad", 67, 80, IsLocal: true, IsHost: true);
        var lobbyPlayer = new LobbyPlayerSnapshot("p:1", "selecting", "Local", "ironclad", IsReady: false, SlotId: 0, IsLocal: true, IsHost: true, IsRemote: false);
        var lobbyCharacter = new LobbyCharacterSnapshot("ironclad", "Ironclad", IsUnlocked: true);
        var card = new CardStateSnapshot("c1", "Jab", 1, "p1", Playable: true, UnplayableReason: null, TargetIds: [], Upgraded: false);
        var enemy = new EnemyStateSnapshot("enemy:0:fuzzywurmcrawler", "FuzzyWurmCrawler", 55, "FIRST_ACID_GOOP", 55, 0, IsAlive: true, []);
        return new GameStateSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-test",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: screen,
            ScreenTitle: screen == "combat" ? "Combat" : screen,
            ScreenInstanceId: $"screen:{screen}:1",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
            Menu: null,
            Lobby: includeLobby
                ? new LobbyStateSnapshot(
                    "start-run",
                    "selecting",
                    [lobbyPlayer],
                    [lobbyCharacter],
                    "p:1",
                    "p:1",
                    "host",
                    new Dictionary<string, LobbyPlayerSnapshot> { ["p:1"] = lobbyPlayer },
                    new Dictionary<string, LobbyCharacterSnapshot> { ["ironclad"] = lobbyCharacter })
                : null,
            Run: includeRun
                ? new RunStateSnapshot("SEED", Floor: 3, Act: 1, [player], new Dictionary<string, PlayerStateSnapshot> { ["p1"] = player })
                : null,
            Combat: screen == "combat"
                ? new CombatStateSnapshot(
                    Turn: 1,
                    ActivePlayerId: "p1",
                    IsPlayerTurn: true,
                    Hand: [card],
                    Players: [new CombatPlayerStateSnapshot("p1", "ironclad", 67, 80, Block: 0, Energy: 3, MaxEnergy: 3, Hand: [card], IsLocal: true, IsHost: true)],
                    Enemies: [enemy],
                    PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>(),
                    EncounterId: "FuzzyWurmCrawlerWeak")
                : null,
            Choices: [],
            AvailableActions: [new AvailableActionSnapshot("action:test", SemanticActionKind.Choose, "Choose", "sts2 act choose --choice x", Provisional: false, new ActionArgumentsSnapshot("p1", null, null, "choice", null, null))],
            Notices: [],
            Debug: null);
    }

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective) => snapshot;
    }
}
#endif
