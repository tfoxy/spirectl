using System.Text.Json;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;


public sealed class Sts2RecordedFixtureProvider(
    IGameStateExtractor stateExtractor,
    IPerspectiveProvider perspectiveProvider,
    ILogStream logStream) : IRecordedFixtureProvider
{
    private readonly IGameStateExtractor _stateExtractor = stateExtractor;
    private readonly IPerspectiveProvider _perspectiveProvider = perspectiveProvider;
    private readonly ILogStream _logStream = logStream;

    public RecordedFixtureResult Record(RecordedFixtureRequestSnapshot request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(() => RecordOnMainThread(request));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.fixture", $"Recorded fixture failed: {ex}");
            return RecordedFixtureResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                RecordedFixtureFailureCode.RuntimeFailure,
                ex.Message,
                [new FixtureLoadDetail("code", "recorded_fixture_runtime_failure", "Recorded fixture capture failed.")]);
        }
    }

    private RecordedFixtureResult RecordOnMainThread(RecordedFixtureRequestSnapshot request)
    {
        var snapshot = _stateExtractor.Extract(
            new GameStateQuery(new PerspectiveSelection(PlayerScope.Local, request.PlayerId), IncludeDebug: false),
            _perspectiveProvider.GetDefaultPerspective());

        if (Sts2SupportedScreenIds.IsLobbyScreenType(snapshot.ScreenType))
        {
            return RecordMultiplayerLobby(request, snapshot);
        }

        if (string.Equals(snapshot.ScreenType, "combat", StringComparison.Ordinal))
        {
            return RecordCombat(request, snapshot);
        }

        if (string.Equals(snapshot.ScreenType, "main-menu", StringComparison.Ordinal))
        {
            return RecordRunlessOrRunBackedSinglePlayer(request, snapshot, "main-menu");
        }

        if (Sts2SupportedScreenIds.IsMapScreenType(snapshot.ScreenType)
            || Sts2SupportedScreenIds.IsRewardsScreenType(snapshot.ScreenType)
            || Sts2SupportedScreenIds.IsRestSiteScreenType(snapshot.ScreenType))
        {
            return RecordRunBackedSinglePlayer(request, snapshot, snapshot.ScreenType);
        }

        if (Sts2SupportedScreenIds.IsShopScreenType(snapshot.ScreenType))
        {
            return Unsupported(request, snapshot, "shop_recipe_unavailable", "Shop recording requires authoritative shop inventory recipe data.");
        }

        return Unsupported(request, snapshot, "unsupported_screen", "M59 only records screens with authoritative fixture recipe data.");
    }

    private RecordedFixtureResult RecordCombat(RecordedFixtureRequestSnapshot request, GameStateSnapshot snapshot)
    {
        var combatPlayer = snapshot.Combat?.Players.FirstOrDefault(player => player.IsLocal);
        var runPlayer = snapshot.Run?.Players.FirstOrDefault(player => player.IsLocal)
            ?? snapshot.Run?.Players.FirstOrDefault();
        if (combatPlayer is null && runPlayer is null || snapshot.Run is null || snapshot.Combat is null)
        {
            return Unsupported(request, snapshot, "combat_recipe_unavailable", "Combat recording requires run, combat, and local player state.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Combat.EncounterId))
        {
            return Unsupported(request, snapshot, "combat_encounter_unavailable", "Combat recording requires an encounter id so fixture load can recreate the screen entry.");
        }

        var playerId = combatPlayer?.Id ?? runPlayer!.Id;
        var fixture = BaseSinglePlayerFixture(
            snapshot,
            "combat",
            playerId,
            combatPlayer?.Character ?? runPlayer!.Character,
            combatPlayer?.Hp ?? runPlayer!.Hp,
            combatPlayer?.MaxHp ?? runPlayer!.MaxHp,
            snapshot.Run);
        ((Dictionary<string, object?>)fixture["run"]!)["currentRoom"] = new Dictionary<string, object?>
        {
            ["combat"] = new Dictionary<string, object?>
            {
                ["encounterId"] = snapshot.Combat.EncounterId,
            },
        };

        return Succeeded(
            request,
            snapshot,
            fixture,
            [
                new RecordedFixtureCompatibilityNoteSnapshot(
                    "mid-combat-deltas-omitted",
                    "Played-card history, current queues, turn-local relic history, and transient combat UI state are not recorded.",
                    "combat"),
                new RecordedFixtureCompatibilityNoteSnapshot(
                    "rng-continuation-omitted",
                    "Runtime RNG continuation is not recorded.",
                    "run.rng"),
            ],
            []);
    }

    private RecordedFixtureResult RecordRunlessOrRunBackedSinglePlayer(
        RecordedFixtureRequestSnapshot request,
        GameStateSnapshot snapshot,
        string screen)
    {
        if (snapshot.Run is null || snapshot.Run.Players.Count == 0)
        {
            var fixture = BaseSinglePlayerFixture(
                snapshot,
                screen,
                "p1",
                "ironclad",
                hp: null,
                maxHp: null,
                run: null);
            return Succeeded(
                request,
                snapshot,
                fixture,
                [],
                [
                    new RecordedFixtureNoticeSnapshot(
                        "main-menu-default-player",
                        "Main-menu recording used a synthetic default player because no run/player state was available."),
                ]);
        }

        return RecordRunBackedSinglePlayer(request, snapshot, screen);
    }

    private RecordedFixtureResult RecordRunBackedSinglePlayer(
        RecordedFixtureRequestSnapshot request,
        GameStateSnapshot snapshot,
        string screen)
    {
        var player = snapshot.Run?.Players.FirstOrDefault(player => player.IsLocal)
            ?? snapshot.Run?.Players.FirstOrDefault();
        if (snapshot.Run is null || player is null)
        {
            return Unsupported(request, snapshot, $"{screen}_recipe_unavailable", $"{screen} recording requires run and local player state.");
        }

        var fixture = BaseSinglePlayerFixture(
            snapshot,
            screen,
            player.Id,
            player.Character,
            player.Hp,
            player.MaxHp,
            snapshot.Run);
        return Succeeded(request, snapshot, fixture, [], []);
    }

    private RecordedFixtureResult RecordMultiplayerLobby(RecordedFixtureRequestSnapshot request, GameStateSnapshot snapshot)
    {
        var lobby = snapshot.Lobby;
        if (lobby is null || lobby.Players.Count == 0)
        {
            return Unsupported(request, snapshot, "lobby_recipe_unavailable", "Lobby recording requires authoritative lobby player data.");
        }

        var localPlayer = lobby.Players.FirstOrDefault(player => player.IsLocal) ?? lobby.Players.First();
        var availableCharacters = lobby.AvailableCharacters
            .Where(character => character.IsUnlocked)
            .Select(character => character.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (availableCharacters.Length == 0)
        {
            return Unsupported(request, snapshot, "lobby_characters_unavailable", "Lobby recording requires visible available character ids.");
        }

        var kind = string.Equals(lobby.LobbyId, "load-run", StringComparison.Ordinal)
            ? "load-run"
            : "start-run";
        if (kind == "load-run" && lobby.Players.Any(player => !player.Id.StartsWith("p:", StringComparison.Ordinal)))
        {
            return Unsupported(request, snapshot, "load_run_lobby_ids_unavailable", "Load-run lobby recording requires stable p:<net-id> player ids.");
        }

        var run = snapshot.Run;
        var fixture = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "spirectl.fixture/v0",
            ["name"] = "recorded-current-screen",
            ["description"] = $"Recorded screen-entry fixture for {snapshot.ScreenType} captured from {snapshot.ScreenInstanceId}.",
            ["characterSelect"] = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["lobby"] = new Dictionary<string, object?>
                {
                    ["localPlayerId"] = localPlayer.Id,
                    ["hostPlayerId"] = string.IsNullOrWhiteSpace(lobby.HostPlayerId) ? localPlayer.Id : lobby.HostPlayerId,
                    ["seed"] = run is null || string.IsNullOrWhiteSpace(run.Seed) ? "recorded-current-screen" : run.Seed,
                    ["players"] = lobby.Players.Select(player => new Dictionary<string, object?>
                    {
                        ["id"] = player.Id,
                        ["characterId"] = string.IsNullOrWhiteSpace(player.SelectedCharacterId)
                            ? availableCharacters[0]
                            : player.SelectedCharacterId,
                        ["isReady"] = player.IsReady,
                        ["slotId"] = Math.Max(0, player.SlotId),
                    }).ToArray(),
                },
            },
        };

        return Succeeded(request, snapshot, fixture, [], []);
    }

    private static RecordedFixtureResult Unsupported(
        RecordedFixtureRequestSnapshot request,
        GameStateSnapshot snapshot,
        string code,
        string message)
        => RecordedFixtureResult.Failure(
            request.RequestId,
            snapshot.Source,
            snapshot.Provisional,
            RecordedFixtureFailureCode.InvalidAction,
            "The current screen cannot be recorded as a fixture recipe.",
            [
                new FixtureLoadDetail("code", code, message),
                new FixtureLoadDetail("screen.id", snapshot.ScreenType, "Unsupported for recorded fixture capture in M59."),
            ]);

    private static Dictionary<string, object?> BaseSinglePlayerFixture(
        GameStateSnapshot snapshot,
        string screen,
        string playerId,
        string character,
        int? hp,
        int? maxHp,
        RunStateSnapshot? run)
    {
        var player = new Dictionary<string, object?>
        {
            ["id"] = playerId,
            ["characterId"] = string.IsNullOrWhiteSpace(character) ? "ironclad" : character,
        };
        if (hp is not null || maxHp is not null)
        {
            var creature = new Dictionary<string, object?>();
            if (hp is not null)
            {
                creature["currentHp"] = hp.Value;
            }
            if (maxHp is not null)
            {
                creature["maxHp"] = maxHp.Value;
            }
            player["creature"] = creature;
        }

        var runFixture = RunFixture(run);
        runFixture["view"] = new Dictionary<string, object?> { ["playerId"] = playerId };
        runFixture["players"] = new[] { player };

        var fixture = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "spirectl.fixture/v0",
            ["name"] = "recorded-current-screen",
            ["description"] = $"Recorded screen-entry fixture for {screen} captured from {snapshot.ScreenInstanceId}.",
            ["run"] = runFixture,
        };

        // The current schema derives the loader recipe from structure: main-menu is an
        // explicit rootScene, rewards is a player overlay, and the remaining
        // single-player screens are currentRoom kinds (combat adds its room in
        // RecordCombat).
        if (string.Equals(screen, "main-menu", StringComparison.Ordinal))
        {
            fixture["rootScene"] = "main-menu";
        }
        else if (Sts2SupportedScreenIds.IsRewardsScreenType(screen))
        {
            player["overlays"] = new object[]
            {
                new Dictionary<string, object?> { ["rewards"] = new Dictionary<string, object?>() },
            };
        }
        else if (Sts2SupportedScreenIds.IsMapScreenType(screen))
        {
            runFixture["currentRoom"] = new Dictionary<string, object?>
            {
                ["mapRoom"] = new Dictionary<string, object?>(),
            };
        }
        else if (Sts2SupportedScreenIds.IsRestSiteScreenType(screen))
        {
            runFixture["currentRoom"] = new Dictionary<string, object?>
            {
                ["restSite"] = new Dictionary<string, object?>(),
            };
        }

        return fixture;
    }

    private static Dictionary<string, object?> RunFixture(RunStateSnapshot? run)
        => new()
        {
            ["seed"] = run is null || string.IsNullOrWhiteSpace(run.Seed) ? "recorded-current-screen" : run.Seed,
            ["currentActIndex"] = Math.Max(0, (run?.Act ?? 1) - 1),
            ["actFloor"] = Math.Max(1, run?.Floor ?? 1),
        };

    private static RecordedFixtureResult Succeeded(
        RecordedFixtureRequestSnapshot request,
        GameStateSnapshot snapshot,
        IReadOnlyDictionary<string, object?> fixture,
        IReadOnlyList<RecordedFixtureCompatibilityNoteSnapshot> knownOmissions,
        IReadOnlyList<RecordedFixtureNoticeSnapshot> notices)
    {
        var allNotices = new List<RecordedFixtureNoticeSnapshot>
        {
            new(
                "screen-entry-fixture",
                "This artifact recreates the screen-entry recipe, not the current runtime frame."),
        };
        allNotices.AddRange(notices);

        var fixtureJson = JsonSerializer.Serialize(fixture);
        var metadata = new RecordedFixtureMetadataSnapshot(
            SchemaVersion: "spirectl.recorded-fixture/v0",
            RecordedAt: DateTimeOffset.UtcNow.ToString("O"),
            Screen: new RecordedFixtureScreenSnapshot(snapshot.ScreenType, snapshot.ScreenTitle, snapshot.ScreenInstanceId),
            GameVersion: snapshot.GameVersion,
            BridgeVersion: snapshot.BridgeVersion,
            SpirectlVersion: string.Empty,
            RestoreQuality: RecordedFixtureRestoreQuality.Partial,
            KnownOmissions: knownOmissions,
            Notices: allNotices);

        return RecordedFixtureResult.Succeeded(new RecordedFixtureSuccess(
            request.RequestId,
            FixtureYaml: string.Empty,
            FixtureJson: fixtureJson,
            Metadata: metadata,
            Source: snapshot.Source,
            Provisional: snapshot.Provisional));
    }
}
