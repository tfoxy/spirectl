using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Proto.V0;
using Google.Protobuf;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

extern alias GodotLive;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
extern alias Sts2Live;
#endif

using GodotLive::Godot;
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
using Sts2Live::MegaCrit.Sts2.Core.Entities.Players;
using Sts2Live::MegaCrit.Sts2.Core.Map;
using Sts2Live::MegaCrit.Sts2.Core.Models;
using Sts2Live::MegaCrit.Sts2.Core.Models.Cards.Mocks;
using Sts2Live::MegaCrit.Sts2.Core.Rooms;
using Sts2Live::MegaCrit.Sts2.Core.Runs;
using Sts2Live::MegaCrit.Sts2.Core.Runs.History;
#endif

public sealed partial class Sts2HostTests
{
#if ENABLE_STS2_LIVE_HOST && ENABLE_STALE_STS2_SCREEN_FIXTURE_TESTS
    [Fact]
    public void FixtureModelResolverNormalizesFixtureIds()
    {
        Assert.Equal("nibbits-normal", Sts2ModelResolver.NormalizeFixtureId("NIBBITS_NORMAL"));
        Assert.Equal("ironclad", Sts2ModelResolver.NormalizeFixtureId("Ironclad"));
    }

    [Fact]
    public void FixtureModelResolverParsesLobbyPlayerIds()
    {
        Assert.True(Sts2ModelResolver.TryResolveLobbyPlayerId("p:1", out var localPlayerId));
        Assert.Equal(1uL, localPlayerId);

        Assert.True(Sts2ModelResolver.TryResolveLobbyPlayerId("p:42", out var remotePlayerId));
        Assert.Equal(42uL, remotePlayerId);

        Assert.False(Sts2ModelResolver.TryResolveLobbyPlayerId("p1", out _));
        Assert.False(Sts2ModelResolver.TryResolveLobbyPlayerId("p:0", out _));
    }

    [Fact]
    public void FixtureModelResolverKeepsNibbitsWeakDistinctFromNibbitsNormal()
    {
        Assert.False(string.Equals(
            Sts2ModelResolver.NormalizeFixtureId("nibbits-weak"),
            Sts2ModelResolver.NormalizeFixtureId("nibbits-normal"),
            StringComparison.Ordinal));

        Assert.False(Sts2ModelResolver.TryResolveFixtureEncounter("nibbits-weak", out _));
        Assert.True(Sts2ModelResolver.TryResolveFixtureEncounter("NIBBITS_WEAK", out var weak));
        Assert.Equal("nibbits-weak", Sts2ModelResolver.NormalizeFixtureId(weak.Id.Entry));

        if (Sts2ModelResolver.TryResolveFixtureEncounter("NIBBITS_NORMAL", out var normal))
        {
            Assert.NotEqual(normal.Id.Entry, weak.Id.Entry);
        }
    }

    [Fact]
    public void FixtureModelResolverResolvesNeowThroughAncients()
    {
        Assert.False(Sts2ModelResolver.TryResolveFixtureEvent("neow", out _));
        Assert.True(Sts2ModelResolver.TryResolveFixtureEvent("NEOW", out var model));
        Assert.Equal("neow", Sts2ModelResolver.NormalizeFixtureId(model.Id.Entry));
        Assert.Contains(ModelDb.AllAncients, candidate => ReferenceEquals(candidate, model));
    }

    [Fact]
    public void FixtureLoaderCanonicalCombatDocumentAcceptsAscensionLevel()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "combat",
          "name": "basic-combat",
          "perspective": { "playerId": "p:1" },
          "run": { "act": 1, "floor": 1, "seed": "fixture-basic-combat", "ascensionLevel": 3 },
          "players": [{ "id": "p:1", "character": "IRONCLAD", "hp": 67, "maxHp": 80, "isHostLocalSeat": true }],
          "room": { "encounterId": "NIBBITS_NORMAL" }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var run = documentType!.GetProperty("Run", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.Equal(3, run?.GetType().GetProperty("AscensionLevel", BindingFlags.Instance | BindingFlags.Public)?.GetValue(run));
    }

    [Fact]
    public void FixtureLoaderCanonicalEventRoomDocumentAcceptsAncientDialogueId()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "schemaVersion": "spirectl.fixture/v0",
          "screen": "Rooms.NEventRoom",
          "name": "initial-neow",
          "run": {
            "seed": "fixture-initial-neow",
            "view": { "playerId": "p:1" },
            "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
            "currentRoom": {
              "event": {
                "canonicalEventModelId": "NEOW",
                "playerStates": [
                  { "playerId": "p:1", "ancient": { "view": { "visibleDialogue": { "dialogueId": "NEOW.talk.ANY.4" } } } }
                ]
              }
            }
          }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var eventRoom = documentType!.GetProperty("EventRoom", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.Equal("NEOW.talk.ANY.4", eventRoom?.GetType().GetProperty("AncientDialogueId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(eventRoom));
    }

    [Fact]
    public void FixtureLoaderResolvesAuthoredAncientDialogueId()
    {
        var (success, dialogueId, authored, failureNote) = ResolveFixtureAncientDialogue("""
        {
          "schemaVersion": "spirectl.fixture/v0",
          "screen": "Rooms.NEventRoom",
          "name": "initial-neow",
          "run": {
            "seed": "fixture-initial-neow",
            "view": { "playerId": "p:1" },
            "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
            "currentRoom": {
              "event": {
                "canonicalEventModelId": "NEOW",
                "playerStates": [
                  { "playerId": "p:1", "ancient": { "view": { "visibleDialogue": { "dialogueId": "NEOW.talk.ANY.4" } } } }
                ]
              }
            }
          }
        }
        """);

        Assert.True(success, failureNote);
        Assert.True(authored);
        Assert.Equal("NEOW.talk.ANY.4", dialogueId);
    }

    [Fact]
    public void FixtureLoaderRecognizesDerivedAncientEventModels()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var method = loaderType?.GetMethod("IsAncientEventModel", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True(Sts2ModelResolver.TryResolveFixtureEvent("NEOW", out var model));

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, [model])));
    }

    [Fact]
    public void FixtureLoaderInfersDeterministicAncientDialogueId()
    {
        var (success, dialogueId, authored, failureNote) = ResolveFixtureAncientDialogue("""
        {
          "schemaVersion": "spirectl.fixture/v0",
          "screen": "Rooms.NEventRoom",
          "name": "initial-neow",
          "run": {
            "seed": "fixture-initial-neow",
            "view": { "playerId": "p:1" },
            "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
            "currentRoom": {
              "event": { "canonicalEventModelId": "NEOW" }
            }
          }
        }
        """);

        Assert.True(success, failureNote);
        Assert.False(authored);
        Assert.Equal("NEOW.talk.ANY.4", dialogueId);
    }

    [Fact]
    public void FixtureLoaderFallsBackForStaleAncientDialogueVariant()
    {
        var (success, dialogueId, authored, failureNote) = ResolveFixtureAncientDialogue("""
        {
          "schemaVersion": "spirectl.fixture/v0",
          "screen": "Rooms.NEventRoom",
          "name": "initial-neow",
          "run": {
            "seed": "fixture-initial-neow",
            "view": { "playerId": "p:1" },
            "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
            "currentRoom": {
              "event": {
                "canonicalEventModelId": "NEOW",
                "playerStates": [
                  { "playerId": "p:1", "ancient": { "view": { "visibleDialogue": { "dialogueId": "NEOW.talk.ANY.4" } } } }
                ]
              }
            }
          }
        }
        """);

        Assert.True(success, failureNote);
        Assert.False(authored);
        Assert.Equal("NEOW.talk.ANY.4", dialogueId);
    }

    [Fact]
    public void FixtureLoaderRejectsUnknownAncientDialogueId()
    {
        var (success, dialogueId, _, failureNote) = ResolveFixtureAncientDialogue("""
        {
          "schemaVersion": "spirectl.fixture/v0",
          "screen": "Rooms.NEventRoom",
          "name": "initial-neow",
          "run": {
            "seed": "fixture-initial-neow",
            "view": { "playerId": "p:1" },
            "players": [{ "id": "p:1", "characterId": "IRONCLAD" }],
            "currentRoom": {
              "event": {
                "canonicalEventModelId": "NEOW",
                "playerStates": [
                  { "playerId": "p:1", "ancient": { "view": { "visibleDialogue": { "dialogueId": "NEOW.talk.UNKNOWN.999" } } } }
                ]
              }
            }
          }
        }
        """);

        Assert.False(success);
        Assert.Equal("NEOW.talk.UNKNOWN.999", dialogueId);
        Assert.Contains("ancient dialogue id", failureNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FixtureLoaderCanonicalCombatDocumentAcceptsTwoHostLocalIronclads()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "combat",
          "name": "two-ironclad-nibbits-weak",
          "perspective": { "playerId": "p:1" },
          "run": { "act": 1, "floor": 1, "seed": "fixture-two-ironclad-nibbits-weak" },
          "players": [
            { "id": "p:1", "character": "IRONCLAD", "hp": 67, "maxHp": 80, "isHostLocalSeat": true, "slotId": 0 },
            { "id": "p:2", "character": "IRONCLAD", "hp": 52, "maxHp": 75, "isHostLocalSeat": true, "slotId": 1 }
          ],
          "room": { "encounterId": "NIBBITS_WEAK" }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var room = documentType!.GetProperty("Room", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.Equal("NIBBITS_WEAK", room?.GetType().GetProperty("EncounterId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(room));

        var players = Assert.IsAssignableFrom<System.Collections.IList>(
            documentType.GetProperty("Players", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document));
        Assert.Equal(2, players.Count);
        Assert.All(players.Cast<object>(), player =>
        {
            Assert.Equal("IRONCLAD", player.GetType().GetProperty("Character", BindingFlags.Instance | BindingFlags.Public)?.GetValue(player));
            Assert.Equal(true, player.GetType().GetProperty("IsHostLocalSeat", BindingFlags.Instance | BindingFlags.Public)?.GetValue(player));
        });
        Assert.Equal(67, players[0]!.GetType().GetProperty("Hp", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[0]));
        Assert.Equal(80, players[0]!.GetType().GetProperty("MaxHp", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[0]));
        Assert.Equal(52, players[1]!.GetType().GetProperty("Hp", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[1]));
        Assert.Equal(75, players[1]!.GetType().GetProperty("MaxHp", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[1]));
    }

    [Fact]
    public void FixtureLoaderCanonicalCombatDocumentAcceptsPlayerPotionIds()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "combat",
          "name": "combat-potion-target",
          "perspective": { "playerId": "p:1" },
          "run": { "act": 1, "floor": 1, "seed": "fixture-combat-potion-target" },
          "players": [
            { "id": "p:1", "character": "IRONCLAD", "potionIds": ["FIRE_POTION", "POTION_OF_BINDING"], "isHostLocalSeat": true, "slotId": 0 },
            { "id": "p:2", "character": "IRONCLAD", "isHostLocalSeat": true, "slotId": 1 }
          ],
          "room": { "encounterId": "NIBBITS_WEAK" }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var players = Assert.IsAssignableFrom<System.Collections.IList>(
            documentType!.GetProperty("Players", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document));
        var potionIds = Assert.IsAssignableFrom<System.Collections.IList>(
            players[0]!.GetType().GetProperty("PotionIds", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[0]));
        Assert.Equal(2, potionIds.Count);
        Assert.Equal("FIRE_POTION", potionIds[0]);
        Assert.Equal("POTION_OF_BINDING", potionIds[1]);
        Assert.Null(players[1]!.GetType().GetProperty("PotionIds", BindingFlags.Instance | BindingFlags.Public)?.GetValue(players[1]));
    }

    [Fact]
    public void FixtureLoaderCanonicalLobbyDocumentAcceptsLobbyRecipeFields()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "Screens.CharacterSelect.NCharacterSelectScreen",
          "name": "basic-lobby",
          "perspective": { "playerId": "p:1" },
          "run": { "act": 1, "floor": 2, "seed": "fixture-basic-lobby" },
          "players": [{ "id": "p:1", "character": "IRONCLAD", "isLocal": true, "slotId": 0 }],
          "lobby": {
            "kind": "start-run",
            "hostPlayerId": "p:1",
            "availableCharacters": ["IRONCLAD", "SILENT"]
          }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var lobby = documentType!.GetProperty("Lobby", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(lobby);
        Assert.Equal("start-run", lobby?.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public)?.GetValue(lobby));
    }

    [Fact]
    public void FixtureLoaderCanonicalMainMenuDocumentAcceptsMissingRoomAndLobby()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "main-menu",
          "name": "basic-main-menu",
          "perspective": { "playerId": "p1" },
          "run": { "act": 1, "floor": 1, "seed": "fixture-basic-main-menu" },
          "players": [{ "id": "p1", "character": "IRONCLAD" }]
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var room = documentType!.GetProperty("Room", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        var lobby = documentType!.GetProperty("Lobby", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(room);
        Assert.Null(lobby);
    }

    [Fact]
    public void FixtureLoaderCanonicalMapDocumentAcceptsMissingRoomAndLobby()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "map",
          "name": "basic-map",
          "perspective": { "playerId": "p1" },
          "run": { "act": 1, "floor": 3, "seed": "fixture-basic-map" },
          "players": [{ "id": "p1", "character": "IRONCLAD" }]
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var room = documentType!.GetProperty("Room", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        var lobby = documentType!.GetProperty("Lobby", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(room);
        Assert.Null(lobby);
    }

    [Fact]
    public void FixtureLoaderCanonicalRewardsDocumentAcceptsMissingRoomAndLobby()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "rewards",
          "name": "basic-rewards",
          "perspective": { "playerId": "p1" },
          "run": { "act": 1, "floor": 3, "seed": "fixture-basic-rewards" },
          "players": [{ "id": "p1", "character": "IRONCLAD" }]
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var room = documentType!.GetProperty("Room", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        var lobby = documentType!.GetProperty("Lobby", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(room);
        Assert.Null(lobby);
    }

    [Fact]
    public void FixtureLoaderCanonicalRestSiteDocumentAcceptsMissingRoomAndLobby()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "rest-site",
          "name": "basic-rest-site",
          "perspective": { "playerId": "p1" },
          "run": { "act": 1, "floor": 3, "seed": "fixture-basic-rest-site" },
          "players": [{ "id": "p1", "character": "IRONCLAD" }]
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var room = documentType!.GetProperty("Room", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        var lobby = documentType!.GetProperty("Lobby", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(room);
        Assert.Null(lobby);
    }

    [Fact]
    public void FixtureLoaderCanonicalCardOverlayDocumentAcceptsOverlayRecipe()
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var payload = """
        {
          "screen": "passive-card-overlay",
          "name": "basic-passive-card-overlay",
          "perspective": { "playerId": "p1" },
          "run": { "act": 1, "floor": 5, "seed": "fixture-basic-passive-card-overlay" },
          "players": [{ "id": "p1", "character": "IRONCLAD" }],
          "overlay": {
            "family": "passive-card-overlay",
            "policy": "passive",
            "sourceScreen": "shop",
            "cards": ["zap"],
            "hiddenControlState": true
          }
        }
        """;
        var document = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(document);

        var overlay = documentType!.GetProperty("Overlay", BindingFlags.Instance | BindingFlags.Public)?.GetValue(document);
        Assert.NotNull(overlay);
        Assert.Equal("passive", overlay?.GetType().GetProperty("Policy", BindingFlags.Instance | BindingFlags.Public)?.GetValue(overlay));
        var extraFields = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            overlay?.GetType().GetProperty("ExtraFields", BindingFlags.Instance | BindingFlags.Public)?.GetValue(overlay));
        Assert.True(extraFields.Contains("hiddenControlState"));
    }

    [Fact]
    public void FixtureLoaderRejectsMainMenuRecipeWithEncounterRoom()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "basic-main-menu",
            SourcePath: "/repo/fixtures/basic-main-menu.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "main-menu",
              "name": "basic-main-menu",
              "rootScene": "main-menu",
              "run": {
                "seed": "fixture-basic-main-menu",
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "IRONCLAD" }],
                "currentRoom": { "combat": { "encounterId": "NIBBITS_NORMAL" } }
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("run.currentRoom.combat", result.Error.Details[0].Field);
    }

    [Fact]
    public void FixtureLoaderRejectsLobbyRecipeWithoutLobbySection()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "basic-lobby",
            SourcePath: "/repo/fixtures/basic-lobby.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.CharacterSelect.NCharacterSelectScreen",
              "name": "basic-lobby",
              "run": {
                "seed": "fixture-basic-lobby",
                "view": { "playerId": "p:1" },
                "players": [{ "id": "p:1", "characterId": "IRONCLAD" }]
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("characterSelect", result.Error.Details[0].Field);
    }

    [Fact]
    public void FixtureLoaderRejectsLobbyRecipeWithUnparseableLocalPlayerId()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "basic-lobby",
            SourcePath: "/repo/fixtures/basic-lobby.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Screens.CharacterSelect.NCharacterSelectScreen",
              "name": "basic-lobby",
              "characterSelect": {
                "kind": "start-run",
                "view": { "playerId": "p1" },
                "lobby": {
                  "localPlayerId": "p1",
                  "hostPlayerId": "p1",
                  "seed": "fixture-basic-lobby",
                  "players": [{ "id": "p1", "characterId": "IRONCLAD", "slotId": 0 }]
                }
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("characterSelect.lobby.players[0].id", result.Error.Details[0].Field);
    }

    [Fact]
    public void FixtureLoaderRejectsOverlayRecipeWithUnknownCardId()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "bad-card-overlay",
            SourcePath: "/repo/fixtures/bad-card-overlay.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "card-overlay",
              "name": "bad-card-overlay",
              "run": {
                "seed": "fixture-bad-card-overlay",
                "view": { "playerId": "p1" },
                "players": [
                  {
                    "id": "p1",
                    "characterId": "IRONCLAD",
                    "overlays": [
                      {
                        "cardOverlay": {
                          "policy": "blocking",
                          "sourceScreen": "map",
                          "cards": [{ "modelId": "not-a-card" }]
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("run.players[].overlays[].cardOverlay.cards[0]", result.Error.Details[0].Field);
    }

    [Fact]
    public void FixtureLoaderRejectsCombatRecipeWithUnknownEncounterBeforeRunLaunch()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "bad-combat",
            SourcePath: "/repo/fixtures/bad-combat.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "combat",
              "name": "bad-combat",
              "run": {
                "seed": "fixture-bad-combat",
                "view": { "playerId": "p:1" },
                "players": [
                  { "id": "p:1", "characterId": "IRONCLAD", "isHostLocalSeat": true },
                  { "id": "p:2", "characterId": "IRONCLAD", "isHostLocalSeat": true }
                ],
                "currentRoom": { "combat": { "encounterId": "not-an-encounter" } }
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("run.currentRoom.combat.encounterId", result.Error.Details[0].Field);
        Assert.Equal("not-an-encounter", result.Error.Details[0].Value);
    }

    [Fact]
    public void FixtureLoaderRejectsCombatRecipeWithUnknownPlayerPotionBeforeRunLaunch()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "bad-combat-potion",
            SourcePath: "/repo/fixtures/bad-combat-potion.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "combat",
              "name": "bad-combat-potion",
              "run": {
                "seed": "fixture-bad-combat-potion",
                "view": { "playerId": "p:1" },
                "players": [
                  { "id": "p:1", "characterId": "IRONCLAD", "potions": [{ "modelId": "not-a-potion" }], "isHostLocalSeat": true },
                  { "id": "p:2", "characterId": "IRONCLAD", "isHostLocalSeat": true }
                ],
                "currentRoom": { "combat": { "encounterId": "NIBBITS_WEAK" } }
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("run.players[0].potions[0]", result.Error.Details[0].Field);
        Assert.Equal("not-a-potion", result.Error.Details[0].Value);
    }

    [Fact]
    public void FixtureLoaderRejectsEventRoomRecipeWithUnknownEventBeforeRunLaunch()
    {
        var loader = new Sts2FixtureLoader(new Sts2ScreenLocator(), new InMemoryLogStream(source: Spirectl.Sts2.Core.Protocol.DataSourceKind.Live, provisional: false));

        var result = loader.Load(new Spirectl.Sts2.Core.Fixtures.FixtureLoadRequestSnapshot(
            RequestId: "fixture-1",
            SchemaVersion: "spirectl.fixture/v0",
            FixtureName: "bad-event-room",
            SourcePath: "/repo/fixtures/bad-event-room.sts2.fixture.yaml",
            FixtureJson: """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "event-room",
              "name": "bad-event-room",
              "run": {
                "seed": "fixture-bad-event-room",
                "view": { "playerId": "p1" },
                "players": [{ "id": "p1", "characterId": "IRONCLAD" }],
                "currentRoom": { "event": { "canonicalEventModelId": "not-an-event" } }
              }
            }
            """));

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Fixtures.FixtureLoadFailureCode.InvalidFixture, result.Error.Code);
        Assert.Equal("run.currentRoom.event.canonicalEventModelId", result.Error.Details[0].Field);
        Assert.Equal("not-an-event", result.Error.Details[0].Value);
        Assert.Equal("Use an event id from installed STS2 events or ancients.", result.Error.Details[0].Note);
    }

    [Fact]
    public void FixtureLoaderSeedsCombatRestoreMapPointHistory()
    {
        var runState = CreateMinimalRunState();

        var method = typeof(Sts2FixtureLoader).GetMethod(
            "PrepareCombatRestoreMapPointHistory",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        method!.Invoke(null, [runState, MapPointType.Unknown, RoomType.Monster, null, 3]);

        Assert.Equal(3, runState.ActFloor);
        var entry = Assert.Single(runState.MapPointHistory[runState.CurrentActIndex]);
        Assert.Equal(MapPointType.Unknown, entry.MapPointType);
        var room = Assert.Single(entry.Rooms);
        Assert.Equal(RoomType.Monster, room.RoomType);
        Assert.Null(room.ModelId);
    }

    private static (bool Success, string DialogueId, bool Authored, string FailureNote) ResolveFixtureAncientDialogue(string payload)
    {
        var loaderType = typeof(Sts2BridgeRuntimeFactory).Assembly.GetType("Spirectl.Sts2.Live.Sts2FixtureLoader");
        var documentType = loaderType?.GetNestedType("FixtureWireDocument", BindingFlags.NonPublic);
        var method = loaderType?.GetMethod("TryResolveFixtureAncientDialogue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(documentType);
        Assert.NotNull(method);
        Assert.True(Sts2ModelResolver.TryResolveFixtureEvent("NEOW", out var model));

        var wireDocument = System.Text.Json.JsonSerializer.Deserialize(
            payload,
            documentType!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.NotNull(wireDocument);
        var document = documentType!
            .GetMethod("ToRecipeDocument", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(wireDocument, []);
        Assert.NotNull(document);

        object?[] arguments = [model, document, null, string.Empty, false, string.Empty];
        var success = Assert.IsType<bool>(method!.Invoke(null, arguments));
        return (success, Assert.IsType<string>(arguments[3]), Assert.IsType<bool>(arguments[4]), Assert.IsType<string>(arguments[5]));
    }

    private static RunState CreateMinimalRunState()
    {
        var runState = (RunState)FormatterServices.GetUninitializedObject(typeof(RunState));
        typeof(RunState).GetField("_players", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runState, new List<Player>());
        typeof(RunState).GetField("_mapPointHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runState, new List<List<MapPointHistoryEntry>>());
        return runState;
    }

    [Fact]
    public void FixtureModelResolverExposesResolutionEntryPoints()
    {
        var resolverType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Sts2ModelResolver");
        Assert.NotNull(resolverType);

        var resolveCharacter = resolverType!.GetMethod("ResolveCharacter", BindingFlags.Public | BindingFlags.Static);
        var resolveEncounter = resolverType.GetMethod("ResolveEncounter", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(resolveCharacter);
        Assert.NotNull(resolveEncounter);
        Assert.NotNull(resolverType.GetMethod("TryResolveCharacter", BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(resolverType.GetMethod("TryResolveEncounter", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void EncounterVisualHookInvocationSupportsArmSideEnumAndDurationArguments()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryInvokeEncounterVisualHook",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(object), typeof(string)],
            null);
        Assert.NotNull(method);
        var target = new FakeKaiserVisualHooks();

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, [target, "PlayHurtAnim(left)"])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, [target, "PlayRightSideChargeUpAnim"])));

        Assert.Equal(FakeKaiserVisualHooks.ArmSide.Left, target.HurtSide);
        Assert.Equal(0.7f, target.ChargeDuration);
    }

    [Fact]
    public void AssetExtractProviderExportsSpriteFramesAsTimeline()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var spriteFrames = new SpriteFrames();
        spriteFrames.AddAnimation("idle");
        spriteFrames.AddFrame("idle", SolidTexture([255, 0, 0, 255]), 0.12f);
        spriteFrames.AddFrame("idle", SolidTexture([0, 255, 0, 255]), 0.08f);

        var result = InvokeProviderExtract(
            provider,
            "ExtractSpriteFrames",
            spriteFrames,
            AssetRequest(sourcePath: "res://ui/shared/anim_frames.tres"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractArtifactKind.Timeline, result.Response.ArtifactKind);
        Assert.Equal("timeline-frames", result.Response.RenderMode);
        Assert.Equal(2, result.Response.Frames.Count);
        Assert.Equal(200, result.Response.DurationMs);
        Assert.Equal(120, result.Response.Frames[0].DurationMs);
        Assert.Equal(80, result.Response.Frames[1].DurationMs);
        Assert.Equal("Rendered SpriteFrames animation 'idle' as a timeline export.", result.Response.Notes[0]);
    }

    [Fact]
    public void AssetExtractProviderAcceptsOnlyBrowserFontMagicBytes()
    {
        Assert.True(DetectBrowserFontFormat([(byte)'w', (byte)'O', (byte)'F', (byte)'2', 0], out var woff2Format));
        Assert.Equal("woff2", woff2Format);

        Assert.True(DetectBrowserFontFormat([0x00, 0x01, 0x00, 0x00, 0], out var ttfFormat));
        Assert.Equal("ttf", ttfFormat);

        Assert.False(DetectBrowserFontFormat([(byte)'R', (byte)'S', (byte)'R', (byte)'C', 0], out _));
        // The compressed Godot resource container (`RSCC`) is what a `res://….ttf`
        // path yields in an exported game; it must be rejected so the extractor
        // falls back to the loaded FontFile's embedded data instead.
        Assert.False(DetectBrowserFontFormat([(byte)'R', (byte)'S', (byte)'C', (byte)'C', 0x02], out _));
    }

    [Fact]
    public void AssetExtractProviderStyleBoxTextureReturnsStructuredViewportFailureWithoutRootViewport()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var styleBox = new StyleBoxTexture
        {
            Texture = SolidTexture([12, 34, 56, 255]),
        };

        var result = InvokeProviderExtract(
            provider,
            "ExtractStyleBoxTexture",
            styleBox,
            AssetRequest(sourcePath: "res://ui/shared/panel_style.tres"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("viewport", result.Response.Error.Details[0].Field);
        Assert.Equal("root", result.Response.Error.Details[0].Value);
        Assert.Equal(
            "Engine.GetMainLoop() did not expose a root viewport for scene rendering.",
            result.Response.Error.Details[0].Note);
    }

    [Fact]
    public void AssetExtractProviderThemeReturnsStructuredViewportFailureWithoutRootViewport()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var theme = new Theme();

        var result = InvokeProviderExtract(
            provider,
            "ExtractTheme",
            theme,
            AssetRequest(sourcePath: "res://ui/shared/sample_theme.tres"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("viewport", result.Response.Error.Details[0].Field);
        Assert.Equal("root", result.Response.Error.Details[0].Value);
    }

    [Fact]
    public void AssetExtractProviderCanvasItemMaterialReturnsStructuredViewportFailureWithoutRootViewport()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var material = new CanvasItemMaterial();

        var result = InvokeProviderExtract(
            provider,
            "ExtractCanvasItemMaterial",
            material,
            AssetRequest(sourcePath: "res://ui/shared/sample_material.tres"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("viewport", result.Response.Error.Details[0].Field);
        Assert.Equal("root", result.Response.Error.Details[0].Value);
    }

    [Fact]
    public void AssetExtractProviderTileSetWithoutDeterministicTilesReturnsTypeSpecificFailure()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var tileSet = new TileSet();

        var result = InvokeProviderExtract(
            provider,
            "ExtractTileSet",
            tileSet,
            AssetRequest(sourcePath: "res://ui/shared/sample_tileset.tres"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("resource_type", result.Response.Error.Details[0].Field);
        Assert.Contains("TileSet", result.Response.Error.Details[0].Value);
        Assert.Contains("deterministic atlas or scene tile", result.Response.Error.Details[0].Note);
    }

    [Fact]
    public void AssetExtractProviderConfiguresSpinePreviewFromSceneMetadata()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var spine = new FakeSpineSprite
        {
            PreviewAnimation = "hiss",
        };

        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryConfigureSpineFirstFramePreview",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(object), typeof(string).MakeByRefType()],
            null);
        Assert.NotNull(method);

        var args = new object?[] { spine, null };
        var configured = Assert.IsType<bool>(method!.Invoke(provider, args));

        Assert.True(configured);
        Assert.Equal("hiss", Assert.IsType<string>(args[1]));
        Assert.True(spine.PreviewFrame);
        Assert.Equal(0d, spine.PreviewTime);
        Assert.Equal(["hiss"], spine.SetAnimationCalls);
    }

    [Fact]
    public void AssetExtractProviderFallsBackToIdleLoopForSpinePreview()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var spine = new FakeSpineSprite();

        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryConfigureSpineFirstFramePreview",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(object), typeof(string).MakeByRefType()],
            null);
        Assert.NotNull(method);

        var args = new object?[] { spine, null };
        var configured = Assert.IsType<bool>(method!.Invoke(provider, args));

        Assert.True(configured);
        Assert.Equal("idle_loop", Assert.IsType<string>(args[1]));
        Assert.True(spine.PreviewFrame);
        Assert.Equal(0d, spine.PreviewTime);
        Assert.Equal(["idle_loop"], spine.SetAnimationCalls);
    }

    [Fact]
    public void EncounterVisualNonCatalogRemovalPrunesUnselectedSiblings()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "RemoveNonCatalogNodes",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var root = new Node { Name = "root" };
        var catalog = new Node { Name = "catalog" };
        var selected = new Node { Name = "selected" };
        var selectedChild = new Node { Name = "selected-child" };
        var sibling = new Node { Name = "sibling" };

        root.AddChild(catalog);
        catalog.AddChild(selected);
        selected.AddChild(selectedChild);
        catalog.AddChild(sibling);

        var package = new Sts2EncounterVisualPackageDefinition(
            PackageId: "fake-package",
            DisplayName: "Fake Package",
            Camera: null,
            LogicalActors: [],
            VisualParts:
            [
                new Sts2EncounterVisualPartDefinition(
                    PartId: "selected",
                    DisplayName: "Selected",
                    Selector: "selected",
                    RenderTargets: []),
            ],
            VisualStates: [],
            VisualTransitions: []);

        var visibleParts = new HashSet<string> { "selected" };
        var notes = new List<string>();
        var removedCount = Assert.IsType<int>(method!.Invoke(null, [catalog, package, visibleParts, notes]));

        Assert.Equal(1, removedCount);
        Assert.Same(catalog, selected.GetParent());
        Assert.Same(selected, selectedChild.GetParent());
        Assert.Null(sibling.GetParent());
        Assert.DoesNotContain(catalog.GetChildren().OfType<Node>(), node => node.Name == "sibling");
    }

    [Fact]
    public void AssetExtractProviderRecognizesStandaloneSkeletonDataPaths()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "LooksLikeStandaloneSkeletonData",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, [AssetRequest("res://monsters/nibbit/nibbit_skel_data.tres")])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, [AssetRequest("res://characters/ironclad/ironclad_skel.tres")])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, [AssetRequest("res://characters/defect/defect_skeleton.tres")])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null, [AssetRequest("res://monsters/nibbit/nibbit_scene.tscn")])));
    }

    [Fact]
    public void AssetExtractProviderRecognizesCharacterVisualQueries()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryParseCharacterVisualRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var acceptedArgs = new object?[] { VirtualAssetRequest("model://characters/ironclad/visuals"), null };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, acceptedArgs)));
        Assert.NotNull(acceptedArgs[1]);
        var parsed = acceptedArgs[1]!;
        Assert.Equal("ironclad", parsed.GetType().GetProperty("CharacterId")!.GetValue(parsed));
        Assert.Equal("visuals", parsed.GetType().GetProperty("Variant")!.GetValue(parsed));

        foreach (var rejected in new[]
        {
            "character:ironclad:battlefield",
            "model:characters:ironclad:visuals",
            "model:character::visuals",
            "model:character:ironclad:",
            "model://characters/ironclad/characterSelectBg",
        })
        {
            var rejectedArgs = new object?[] { VirtualAssetRequest(rejected), null };
            Assert.False(Assert.IsType<bool>(method.Invoke(null, rejectedArgs)));
            Assert.Null(rejectedArgs[1]);
        }
    }

    [Fact]
    public void AssetExtractProviderResolvesCharacterSelectBackgroundAsModelResource()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryResolveModelResourceRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[] { VirtualAssetRequest("model://characters/ironclad/characterSelectBg"), null };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, args)));
        var resolved = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot>(args[1]);

        Assert.Equal("model", resolved.SourceRoot);
        Assert.Equal("res://scenes/screens/char_select/char_select_bg_ironclad.tscn", resolved.SourcePath);
        Assert.Equal("res://scenes/screens/char_select/char_select_bg_ironclad.tscn", resolved.LoadPath);
    }

    [Fact]
    public void AssetExtractProviderResolvesMonsterAndEventKeysAsModelResources()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryResolveModelResourceRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        if (!Sts2ModelResolver.TryResolveMonster("nibbit", out _)
            || !Sts2ModelResolver.TryResolveEvent("fake-merchant", out _))
        {
            return;
        }

        var monsterArgs = new object?[] { VirtualAssetRequest("model://monsters/nibbit/visuals"), null };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, monsterArgs)));
        var monsterResolved = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot>(monsterArgs[1]);
        Assert.Equal("model", monsterResolved.SourceRoot);
        Assert.StartsWith("res://", monsterResolved.LoadPath, StringComparison.Ordinal);

        var eventBackgroundArgs = new object?[] { VirtualAssetRequest("model://events/fake-merchant/backgroundScene"), null };
        Assert.True(Assert.IsType<bool>(method.Invoke(null, eventBackgroundArgs)));
        var eventBackgroundResolved = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot>(eventBackgroundArgs[1]);
        Assert.Equal("model", eventBackgroundResolved.SourceRoot);
        Assert.StartsWith("res://", eventBackgroundResolved.LoadPath, StringComparison.Ordinal);
        Assert.EndsWith(".tscn", eventBackgroundResolved.LoadPath, StringComparison.Ordinal);

        var eventPortraitArgs = new object?[] { VirtualAssetRequest("model://events/fake-merchant/initialPortrait"), null };
        Assert.True(Assert.IsType<bool>(method.Invoke(null, eventPortraitArgs)));
        var eventPortraitResolved = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot>(eventPortraitArgs[1]);
        Assert.Equal("model", eventPortraitResolved.SourceRoot);
        Assert.StartsWith("res://", eventPortraitResolved.LoadPath, StringComparison.Ordinal);
        Assert.EndsWith(".png", eventPortraitResolved.LoadPath, StringComparison.Ordinal);

        var mergedArgs = new object?[] { VirtualAssetRequest("model://events/fake-merchant/background"), null };
        Assert.False(Assert.IsType<bool>(method.Invoke(null, mergedArgs)));
        Assert.Null(mergedArgs[1]);
    }

    [Fact]
    public void AssetExtractProviderRejectsUnsupportedCharacterVisualVariant()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());

        var result = InvokeProviderMainExtractAsync(
            provider,
            VirtualAssetRequest("model://characters/ironclad/portrait"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("character_variant", result.Response.Error.Details[0].Field);
        Assert.Equal("portrait", result.Response.Error.Details[0].Value);
        Assert.Contains("Only model://characters/<id>/icon", result.Response.Error.Details[0].Note);
    }

    [Fact]
    public void AssetExtractProviderRejectsUnknownCharacterIdWithModelDbDetail()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());

        var result = InvokeProviderMainExtractAsync(
            provider,
            VirtualAssetRequest("character:missing:battlefield"));

        Assert.False(result.Success);
        Assert.NotNull(result.Response.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Response.Error.Code);
        Assert.Equal("character_id", result.Response.Error.Details[0].Field);
        Assert.Equal("missing", result.Response.Error.Details[0].Value);
        Assert.Contains("ModelDb.AllCharacters", result.Response.Error.Details[0].Note);
    }

    [Fact]
    public void AssetExtractProviderInvokesCreateVisualsForCharacterVisualNodes()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryInvokeCreateVisuals",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var factory = new FakeCharacterModel("ironclad");
        var args = new object?[] { factory, null };
        var created = Assert.IsType<bool>(method!.Invoke(null, args));

        Assert.True(created);
        Assert.IsType<TestSpineSprite>(Assert.IsAssignableFrom<Node>(args[1]));
    }

    [Fact]
    public void AssetExtractProviderCharacterVisualNotesNameModelFallbackAndVirtualCaveat()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryCreateCharacterVisualNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var model = new FakeCharacterModel("ironclad");
        var args = new object?[] { model, null, null };
        var created = Assert.IsType<bool>(method!.Invoke(provider, args));

        Assert.True(created);
        var notes = Assert.IsAssignableFrom<IReadOnlyList<string>>(args[2]);
        Assert.Contains(notes, note => note.Contains("CharacterModel.CreateVisuals()", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("not a packed creature_visuals scene", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetExtractProviderPrefersLiveAllyCharacterVisuals()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryCreateLiveAllyCharacterVisualNode",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(object), typeof(IEnumerable<object>), typeof(Node).MakeByRefType(), typeof(IReadOnlyList<string>).MakeByRefType()],
            null);
        Assert.NotNull(method);

        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var model = new FakeCharacterModel("ironclad");
        var ally = new FakeAlly(model);
        var args = new object?[] { model, new object[] { ally }, null, null };
        var created = Assert.IsType<bool>(method!.Invoke(provider, args));

        Assert.True(created);
        Assert.Same(ally.Visuals, Assert.IsAssignableFrom<Node>(args[2]));
        var notes = Assert.IsAssignableFrom<IReadOnlyList<string>>(args[3]);
        Assert.Contains(notes, note => note.Contains("live combat ally CreateVisuals()", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetExtractProviderResolvesStandaloneSkeletonPreviewDefaults()
    {
        var resource = new TestSkeletonDataResource
        {
            AnimationNames = ["attack", "idle_loop"],
            SkinNames = ["variant", "default"],
        };
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "ResolveStandaloneSkeletonPreviewDefaults",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var defaults = method!.Invoke(null, [resource]);
        Assert.NotNull(defaults);

        Assert.Equal("idle_loop", defaults.GetType().GetProperty("AnimationName")!.GetValue(defaults));
        Assert.Equal("default", defaults.GetType().GetProperty("SkinName")!.GetValue(defaults));
        Assert.Equal(Vector2.One, defaults.GetType().GetProperty("Scale")!.GetValue(defaults));
        Assert.True(Assert.IsType<Rect2>(defaults.GetType().GetProperty("Bounds")!.GetValue(defaults)).Size.X > 0);
    }

    [Fact]
    public void AssetExtractProviderCreatesStandaloneSkeletonPreviewNodeWithHonestDefaults()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryCreateStandaloneSkeletonPreviewNode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var resource = new TestSkeletonDataResource
        {
            AnimationNames = ["idle_loop"],
            SkinNames = ["default"],
        };
        var args = new object?[] { resource, AssetRequest("res://monsters/nibbit/nibbit_skel_data.tres"), null, null, null };

        var created = Assert.IsType<bool>(method!.Invoke(provider, args));

        Assert.True(created);
        var node = Assert.IsAssignableFrom<Node2D>(args[2]);
        Assert.IsType<TestSpineSprite>(node);
        Assert.NotNull(args[3]);
        var defaults = args[3]!;
        var notes = Assert.IsAssignableFrom<IReadOnlyList<string>>(args[4]);
        Assert.Contains(notes, note => note.Contains("synthetic Spine preview node", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, note => note.Contains("composed scenes remain the preferred exact in-game visual path", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(resource, ((TestSpineSprite)node).SkeletonDataRes);
        Assert.Equal("idle_loop", ((TestSpineSprite)node).PreviewAnimation);
        Assert.Equal("default", ((TestSpineSprite)node).PreviewSkin);
        Assert.Equal(Vector2.One, node.Scale);
        Assert.Equal("idle_loop", defaults.GetType().GetProperty("AnimationName")!.GetValue(defaults));
    }

    [Fact]
    public void AssetExtractProviderStandaloneSkeletonFailureNamesMissingPreviewNodeType()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "ExtractStandaloneSkeletonDataAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task<Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult>>(
            method!.Invoke(provider, [new Resource(), AssetRequest("res://monsters/nibbit/nibbit_skel_data.tres")]));
        var result = task.GetAwaiter().GetResult();

        Assert.NotNull(result.Error);
        Assert.Equal(Spirectl.Sts2.Core.Artifacts.AssetExtractFailureCode.RuntimeFailure, result.Error.Code);
        Assert.Equal("resource_type", result.Error.Details[0].Field);
        Assert.Contains("standalone skeleton data", result.Error.Details[0].Note);
        Assert.Contains("preview node", result.Error.Details[0].Note);
    }

    [Fact]
    public void AssetExtractProviderComposedSpineSceneRenderModeStaysSceneSpecific()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var spine = new TestSpineSprite
        {
            PreviewAnimation = "idle_loop",
        };
        var scene = new PackedScene();
        Assert.Equal(Error.Ok, scene.Pack(spine));

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractPackedSceneAsync",
            scene,
            AssetRequest(sourcePath: "res://monsters/nibbit/nibbit.tscn"));

        if (result.Success)
        {
            Assert.Equal("flattened-spine-first-frame", result.Response.RenderMode);
        }
        else
        {
            Assert.NotEqual("flattened-spine-skeleton-resource-preview", result.Response.RenderMode);
        }
    }

    [Fact]
    public void AssetExtractProviderTreatsFullyTransparentImagesAsInvisible()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "HasVisiblePixels",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(byte[])],
            null);
        Assert.NotNull(method);

        Assert.False(Assert.IsType<bool>(method!.Invoke(null, [new byte[] { 0, 0, 0, 0 }])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, [new byte[] { 0, 0, 0, 255 }])));
    }

    [Fact]
    public void AssetExtractProviderFramesSpineScenesFromAuthoredBounds()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "FrameFromBounds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var frame = method!.Invoke(null, [new Rect2(-130, -138, 260, 138)]);
        Assert.NotNull(frame);

        var viewportSize = Assert.IsType<Vector2I>(frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        Assert.Equal(new Vector2I(388, 266), viewportSize);
        Assert.Equal(new Vector2(194, 202), nodePosition);
    }

    [Fact]
    public void AssetExtractProviderFramesEventBackgroundScenesAsFullScene()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryResolveEventBackgroundFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[]
        {
            AssetRequest("res://scenes/events/background_scenes/orobas.tscn"),
            new Vector2I(1920, 1080),
            null,
        };

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, args)));
        Assert.NotNull(args[2]);
        var frame = args[2]!;
        var viewportSize = Assert.IsType<Vector2I>(frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        var nodeScale = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        var pivotOffset = Assert.IsType<Vector2>(frame.GetType().GetProperty("PivotOffset")!.GetValue(frame));
        var controlSize = Assert.IsType<Vector2>(frame.GetType().GetProperty("ControlSize")!.GetValue(frame));

        Assert.Equal(new Vector2I(1920, 1080), viewportSize);
        Assert.Equal(new Vector2(105.60002f, 40), nodePosition);
        Assert.Equal(new Vector2(0.89f, 0.89f), nodeScale);
        Assert.Equal(Vector2.Zero, pivotOffset);
        Assert.Equal(new Vector2(1920, 1080), controlSize);
    }

    [Fact]
    public void AssetExtractProviderRecognizesCombatBackgroundScenePaths()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "IsCombatBackgroundScenePath",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, ["res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"])));
        Assert.True(Assert.IsType<bool>(method.Invoke(null, ["res://scenes/backgrounds/OverGrowth/OverGrowth_background.tscn"])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null, ["res://scenes/events/background_scenes/neow.tscn"])));
        Assert.False(Assert.IsType<bool>(method.Invoke(null, ["res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"])));
    }

    [Fact]
    public void AssetExtractProviderFramesCombatBackgroundScenesLikeRuntimeBgContainer()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryResolveCombatBackgroundFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        AssertCombatBackgroundFrame(
            method!,
            new Vector2I(1920, 1080),
            new Vector2(983, 540));
        AssertCombatBackgroundFrame(
            method,
            new Vector2I(1440, 810),
            new Vector2(743, 405));
    }

    private static void AssertCombatBackgroundFrame(
        MethodInfo method,
        Vector2I rootViewportSize,
        Vector2 expectedNodePosition)
    {
        var args = new object?[]
        {
            AssetRequest("res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"),
            rootViewportSize,
            null,
        };

        Assert.True(Assert.IsType<bool>(method.Invoke(null, args)));
        Assert.NotNull(args[2]);
        var frame = args[2]!;
        var viewportSize = Assert.IsType<Vector2I>(frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        var nodeScale = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        var controlSize = Assert.IsType<Vector2>(frame.GetType().GetProperty("ControlSize")!.GetValue(frame));
        var preserveControlSize = Assert.IsType<bool>(frame.GetType().GetProperty("PreserveControlSize")!.GetValue(frame));

        Assert.Equal(rootViewportSize, viewportSize);
        Assert.Equal(expectedNodePosition, nodePosition);
        Assert.Equal(new Vector2(0.9f, 0.9f), nodeScale);
        Assert.Equal(Vector2.Zero, controlSize);
        Assert.True(preserveControlSize);
    }

    [Fact]
    public void AssetExtractProviderFramesEncounterScenesWithEncounterCamera()
    {
        var camera = new Spirectl.Sts2.Core.Artifacts.AssetEncounterCameraSnapshot(
            0.75d,
            new Spirectl.Sts2.Core.Artifacts.AssetVector2Snapshot(0, 35),
            "EncounterModel.GetCameraScaling/GetCameraOffset",
            "KaiserCrabBoss.GetCameraScaling;KaiserCrabBoss.GetCameraOffset");
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "EncounterViewportFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var frame = method!.Invoke(null, [camera]);
        Assert.NotNull(frame);
        var viewportSize = Assert.IsType<Vector2I>(frame!.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        var nodeScale = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        var controlSize = Assert.IsType<Vector2>(frame.GetType().GetProperty("ControlSize")!.GetValue(frame));
        var preserveControlSize = Assert.IsType<bool>(frame.GetType().GetProperty("PreserveControlSize")!.GetValue(frame));

        Assert.Equal(new Vector2I(1920, 1080), viewportSize);
        Assert.Equal(new Vector2(960, 575), nodePosition);
        Assert.Equal(new Vector2(0.75f, 0.75f), nodeScale);
        Assert.Equal(new Vector2(1920, 1080), controlSize);
        Assert.True(preserveControlSize);
    }

    [Fact]
    public void AssetExtractProviderExplainsUnknownEncounterWithStructuredFallback()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());

        var result = provider.Explain(new Spirectl.Sts2.Core.Artifacts.AssetExplainRequestSnapshot(
            RequestId: "explain-test",
            SourceRoot: "virtual",
            SourcePath: "composed://encounters/unknown_encounter/scene-package",
            LoadPath: "composed://encounters/unknown_encounter/scene-package"));

        Assert.Null(result.Error);
        Assert.Equal("encounter-scene-package", result.ExplanationKind);
        Assert.NotNull(result.EncounterScenePackage);
        Assert.Equal("unknown_encounter", result.EncounterScenePackage!.EncounterId);
        Assert.Contains(
            result.EncounterScenePackage.Notices,
            notice => notice.Code == "encounter-visual-package-unsupported");
        Assert.Contains(
            result.EncounterScenePackage.Notices,
            notice => notice.Code == "encounter-camera-unsupported-fallback");
        Assert.Equal("unsupported-fallback", result.EncounterScenePackage.Camera.Source);
    }

    [Fact]
    public void AssetExtractProviderDistinguishesEncounterOverlayFromPartTargets()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryParseEncounterRenderTargetRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var overlayArgs = new object?[]
        {
            VirtualAssetRequest("encounter:kaiser_crab_boss:visual-state:rocket-charge-up:overlay:image"),
            null,
        };
        Assert.True(Assert.IsType<bool>(method!.Invoke(null, overlayArgs)));
        var overlay = overlayArgs[1]!;
        Assert.Equal("overlay", overlay.GetType().GetProperty("Kind")!.GetValue(overlay));
        Assert.Null(overlay.GetType().GetProperty("PartId")!.GetValue(overlay));

        var partArgs = new object?[]
        {
            VirtualAssetRequest("encounter:kaiser_crab_boss:visual-part:rocket:state:rocket-charge-up:image"),
            null,
        };
        Assert.True(Assert.IsType<bool>(method.Invoke(null, partArgs)));
        var part = partArgs[1]!;
        Assert.Equal("part", part.GetType().GetProperty("Kind")!.GetValue(part));
        Assert.Equal("rocket", part.GetType().GetProperty("PartId")!.GetValue(part));
    }

    [Fact]
    public void AssetExtractProviderRemovesCatalogSpecialVisualRootForEncounterBackgrounds()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        Assert.True(catalog.TryGetPackage("kaiser_crab_boss", out var package));
        var root = new Node();
        var specialVisualRoot = new EncounterSpecialVisualRoot { Name = "KaiserCrab" };
        var leftArm = specialVisualRoot.LeftArm;
        var rightArm = specialVisualRoot.RightArm;
        var body = specialVisualRoot.AnimController;
        specialVisualRoot.AddChild(leftArm);
        specialVisualRoot.AddChild(rightArm);
        specialVisualRoot.AddChild(body);
        root.AddChild(specialVisualRoot);

        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "RemoveEncounterSpecialVisualNodes",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var notes = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [root, package]));

        Assert.DoesNotContain(root.GetChildren().OfType<Node>(), node => node.Name == "KaiserCrab");
        Assert.Contains(notes, note => note.Contains("Removed 1 encounter special visual node", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("_leftArm", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("_rightArm", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("_animController", StringComparison.Ordinal));
        root.QueueFree();
    }

    [Fact]
    public void AssetExtractProviderSelectorDiagnosticsIncludeCandidatesResolvedNodeAndBounds()
    {
        var root = new Node { Name = "Root" };
        var specialVisualRoot = new EncounterSpecialVisualRoot { Name = "KaiserCrab" };
        var rightArm = specialVisualRoot.RightArm;
        rightArm.AddChild(new Control
        {
            Name = "Bounds",
            Position = new Vector2(10, 20),
            Size = new Vector2(30, 40),
        });
        specialVisualRoot.AddChild(rightArm);
        root.AddChild(specialVisualRoot);

        var method = typeof(Sts2AssetExtractProvider).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "TryResolveEncounterVisualPart"
                && candidate.GetParameters().Length == 5);
        var diagnostic = method.Invoke(null, [root, "_rightArm", "rightArm", "rocket-charge-up", "right-arm-overlay"]);
        Assert.NotNull(diagnostic);

        var snapshot = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetEncounterSelectorDiagnosticSnapshot>(
            diagnostic!.GetType().GetMethod("ToSnapshot")!.Invoke(diagnostic, []));
        Assert.Equal("resolved", snapshot.Status);
        Assert.Equal("_rightArm", snapshot.Selector);
        Assert.Contains("rightArm", snapshot.NormalizedSelectors);
        Assert.Contains(snapshot.Candidates, candidate =>
            candidate.Path.Contains("KaiserCrab", StringComparison.Ordinal)
            && candidate.Source == "field"
            && candidate.Status == "resolved");
        Assert.NotNull(snapshot.ResolvedNode);
        Assert.Equal("RightArmNode", snapshot.ResolvedNode!.Name);
        Assert.Equal("Node", snapshot.ResolvedNode.Type);
        Assert.NotNull(snapshot.LocalBounds);
        Assert.Equal(30, snapshot.LocalBounds!.Width);
        Assert.NotNull(snapshot.VisibleBounds);
        Assert.Equal("rocket-charge-up", snapshot.TargetStateId);
        Assert.Equal("right-arm-overlay", snapshot.RenderTargetId);

        root.QueueFree();
    }

    private sealed class EncounterSpecialVisualRoot : Node
    {
#pragma warning disable IDE0051 // Private members are resolved reflectively by the production selector path.
        private readonly Node _leftArm = new() { Name = "LeftArmNode" };
        private readonly Node _rightArm = new() { Name = "RightArmNode" };
        private readonly Node _animController = new() { Name = "BodyAnimController" };
#pragma warning restore IDE0051

        public Node LeftArm => _leftArm;

        public Node RightArm => _rightArm;

        public Node AnimController => _animController;
    }

    [Fact]
    public void AssetExtractProviderRecognizesCombatBackgroundAliasRequests()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryParseCombatBackgroundAliasRequest",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object?[]
        {
            AssetRequest(
                sourcePath: "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
                loadPath: "combat-background:OverGrowth:image"),
            null,
        };

        Assert.True(Assert.IsType<bool>(method!.Invoke(null, args)));
        Assert.NotNull(args[1]);
        var alias = args[1]!;
        Assert.Equal("overgrowth", alias.GetType().GetProperty("BackgroundId")!.GetValue(alias));
        Assert.Equal(
            "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
            alias.GetType().GetProperty("RootScenePath")!.GetValue(alias));

        var exactArgs = new object?[]
        {
            AssetRequest("res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"),
            null,
        };
        Assert.False(Assert.IsType<bool>(method.Invoke(null, exactArgs)));
    }

    [Fact]
    public void AssetExtractProviderPlansDeterministicCombatBackgroundAliasLayersFromDiscoveredPaths()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "BuildCombatBackgroundLayerPlans",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        string[] placeholders = ["Layer_00", "Layer_01", "Layer_02", "Foreground"];
        string[] discoveredLayerPaths =
        [
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_b.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_02_b.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_fg_c.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_fg_a.tscn",
        ];

        var plans = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            method!.Invoke(null, [placeholders, discoveredLayerPaths]));
        var paths = plans
            .Cast<object>()
            .Select(plan => plan.GetType().GetProperty("LayerPath")!.GetValue(plan))
            .Cast<string>()
            .ToList();

        Assert.Equal(
            [
                "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
                "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_b.tscn",
                "res://scenes/backgrounds/test_bg/layers/test_bg_bg_02_b.tscn",
                "res://scenes/backgrounds/test_bg/layers/test_bg_fg_a.tscn",
            ],
            paths);
    }

    [Fact]
    public void AssetExtractProviderBuildsExplainableCombatBackgroundLayerGroups()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "BuildCombatBackgroundCompositionPlan",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        string[] placeholders = ["Layer_00", "Layer_01", "Foreground"];
        string[] discoveredLayerPaths =
        [
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_b.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_fg_a.tscn",
        ];

        var plan = method!.Invoke(null, ["test_bg", placeholders, discoveredLayerPaths]);
        var groups = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            plan!.GetType().GetProperty("LayerGroups")!.GetValue(plan));
        var selected = groups.Cast<object>().ToList();

        Assert.Equal(3, selected.Count);
        Assert.Equal("Layer_00", selected[0].GetType().GetProperty("Placeholder")!.GetValue(selected[0]));
        Assert.Equal(
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
            selected[0].GetType().GetProperty("SelectedPath")!.GetValue(selected[0]));
        Assert.Equal(
            "deterministic-first-sorted",
            selected[0].GetType().GetProperty("SelectionSource")!.GetValue(selected[0]));
    }

    [Fact]
    public void AssetExtractProviderCompositionPlanWarnsForMissingPlaceholderLayer()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "BuildCombatBackgroundCompositionPlan",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var plan = method!.Invoke(null, ["test_bg", new[] { "Layer_00", "Layer_02" }, new[]
        {
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
        }]);
        var warnings = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            plan!.GetType().GetProperty("Warnings")!.GetValue(plan));

        Assert.Contains(warnings.Cast<object>(), warning =>
            (string)warning.GetType().GetProperty("Code")!.GetValue(warning)! == "missing-layer");
    }

    [Fact]
    public void AssetExtractProviderIgnoresDiscoveredCombatBackgroundLayersWithoutRootPlaceholders()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "BuildCombatBackgroundLayerPlans",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        string[] placeholders = ["Layer_00"];
        string[] discoveredLayerPaths =
        [
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_a.tscn",
            "res://scenes/backgrounds/test_bg/layers/test_bg_fg_a.tscn",
        ];
        var plans = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            method!.Invoke(null, [placeholders, discoveredLayerPaths]));

        var paths = plans
            .Cast<object>()
            .Select(plan => plan.GetType().GetProperty("LayerPath")!.GetValue(plan))
            .Cast<string>()
            .ToList();
        Assert.Equal(["res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_a.tscn"], paths);
    }

    [Fact]
    public void AssetExtractProviderDoesNotTrimComposedCombatBackgroundsByDefault()
    {
        var shouldTrimMethod = typeof(Sts2AssetExtractProvider).GetMethod(
            "ShouldTrimTransparentBounds",
            BindingFlags.Static | BindingFlags.NonPublic);
        var paddingMethod = typeof(Sts2AssetExtractProvider).GetMethod(
            "ResolveTransparentCropPadding",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(shouldTrimMethod);
        Assert.NotNull(paddingMethod);

        Assert.False(Assert.IsType<bool>(shouldTrimMethod!.Invoke(
            null,
            ["flattened-combat-background-composed", false])));
        Assert.True(Assert.IsType<bool>(shouldTrimMethod.Invoke(
            null,
            ["flattened-combat-background-composed", true])));
        Assert.Equal(0, Assert.IsType<int>(paddingMethod!.Invoke(
            null,
            ["flattened-combat-background-composed"])));
        Assert.False(Assert.IsType<bool>(shouldTrimMethod.Invoke(
            null,
            ["flattened-combat-background-scene", false])));
    }

    [Fact]
    public void AssetExtractProviderComputesVisiblePixelBoundsAndTransparencyRatio()
    {
        var boundsMethod = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryFindVisiblePixelBounds",
            BindingFlags.Static | BindingFlags.NonPublic);
        var ratioMethod = typeof(Sts2AssetExtractProvider).GetMethod(
            "ComputeTransparentPixelRatio",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(boundsMethod);
        Assert.NotNull(ratioMethod);

        byte[] rgba =
        [
            0, 0, 0, 0, 255, 0, 0, 255,
            0, 0, 0, 0, 0, 0, 255, 255,
        ];
        object?[] args = [rgba, 2, 2, 0, null];

        Assert.True(Assert.IsType<bool>(boundsMethod!.Invoke(null, args)));
        var rect = Assert.IsType<Rect2I>(args[4]);
        Assert.Equal(1, rect.Position.X);
        Assert.Equal(0.5d, Assert.IsType<double>(ratioMethod!.Invoke(null, [rgba, 2, 2])), precision: 3);
    }

    [Fact]
    public void AssetExtractProviderComputesRgbEvidenceFromCapturedRgbaBytes()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "ComputeRgbEvidence",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        byte[] rgba =
        [
            0, 0, 0, 0, 255, 0, 0, 0,
            0, 0, 0, 255, 0, 0, 5, 0,
        ];

        var evidence = Assert.NotNull(method!.Invoke(null, [rgba, 2, 2]));
        var count = evidence.GetType().GetProperty("NonzeroPixelCount")!.GetValue(evidence);
        var ratio = evidence.GetType().GetProperty("NonzeroPixelRatio")!.GetValue(evidence);

        Assert.Equal(2, Assert.IsType<int>(count));
        Assert.Equal(0.5d, Assert.IsType<double>(ratio), precision: 3);
    }

    [Fact]
    public void AssetExtractProviderDisablesParticlesForDeterministicCombatBackgroundAliases()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryStabilizeCombatBackgroundParticleNode",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var particles = new TestParticles2DObject
        {
            Emitting = true,
        };

        var stabilized = Assert.IsType<bool>(method!.Invoke(null, [particles]));

        Assert.True(stabilized);
        Assert.False(particles.Emitting);
    }

    [Fact]
    public void AssetExtractProviderExportsAtlasTextureRegionWithVisiblePixels()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var source = Image.Create(4, 4, false, Image.Format.Rgba8);
        source.Fill(new Color(0, 0, 0, 0));
        source.SetPixel(2, 1, new Color(1, 0, 0, 1));
        source.SetPixel(3, 1, new Color(0, 1, 0, 1));
        var atlas = new AtlasTexture
        {
            Atlas = ImageTexture.CreateFromImage(source),
            Region = new Rect2(2, 1, 2, 1),
        };

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractTextureAsync",
            atlas,
            AssetRequest(sourcePath: "res://images/atlases/card_atlas.sprites/ironclad/bash.tres"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("flattened-atlas-texture", result.Response.RenderMode);
        Assert.Equal(2, result.Response.Width);
        Assert.Equal(1, result.Response.Height);
        Assert.True(HasEncodedVisiblePixels(result.Response.Contents));
    }

    [Fact]
    public void GodotResourceProducerEmitsAtlasPageSizeForAtlasTexture()
    {
        // The AtlasTexture resource DOCUMENT must carry the underlying atlas PAGE size so the web
        // client can scale a cropped region into a layout box (CSS background offsets) instead of
        // fetching a per-sprite cropped image. Emitted as `{type:"Vector2",args:[w,h]}` (asVector2).
        var source = Image.Create(4, 4, false, Image.Format.Rgba8);
        source.Fill(new Color(0, 0, 0, 0));
        var atlas = new AtlasTexture
        {
            Atlas = ImageTexture.CreateFromImage(source),
            Region = new Rect2(2, 1, 2, 1),
            Margin = new Rect2(0, 0, 0, 0),
        };

        var dto = Sts2GodotResourceProducer.BuildDto(atlas);

        Assert.Equal("AtlasTexture", dto.Type);
        var atlasSize = Assert.IsType<Dictionary<string, object?>>(dto.Properties["atlas_size"]);
        Assert.Equal("Vector2", atlasSize["type"]);
        var args = Assert.IsType<object?[]>(atlasSize["args"]);
        Assert.Equal(4.0, Assert.IsType<double>(args[0]));
        Assert.Equal(4.0, Assert.IsType<double>(args[1]));
    }

    [Fact]
    public void AssetExtractProviderRejectsTransparentAtlasTextureRegion()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var source = Image.Create(4, 4, false, Image.Format.Rgba8);
        source.Fill(new Color(0, 0, 0, 0));
        var atlas = new AtlasTexture
        {
            Atlas = ImageTexture.CreateFromImage(source),
            Region = new Rect2(1, 1, 2, 2),
        };

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractTextureAsync",
            atlas,
            AssetRequest(sourcePath: "res://images/atlases/card_atlas.sprites/ironclad/bash.tres"));

        Assert.False(result.Success);
        Assert.Equal("texture", result.Response.Error?.Details[0].Field);
        Assert.Contains("SubViewport fallback", result.ErrorMessage);
        Assert.Contains("only fully transparent pixels", result.ErrorMessage);
    }

    [Fact]
    public void AssetExtractProviderExportsSimpleTextureRectSceneWithoutViewport()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var sceneRoot = new TextureRect
        {
            Texture = SolidTexture([255, 64, 0, 255]),
            Size = new Vector2(1, 1),
        };
        var scene = new PackedScene();
        Assert.Equal(Error.Ok, scene.Pack(sceneRoot));

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractPackedSceneAsync",
            scene,
            AssetRequest(sourcePath: "res://scenes/ui/character_icons/ironclad_icon.tscn"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("flattened-scene-texture", result.Response.RenderMode);
        Assert.Equal(1, result.Response.Width);
        Assert.Equal(1, result.Response.Height);
        Assert.True(HasEncodedVisiblePixels(result.Response.Contents));
    }

    // (The char-select bg spine-still extractor `ExtractCharacterSelectBgSpineStillAsync` is retained and
    // reached via the spine://…&still=1 lane now; its former model://…characterSelectBgSpineStill dispatch +
    // this dispatch-specific test were removed with the model:// SpineStill cleanup. The extractor itself is
    // unchanged and covered live.)

    [Fact]
    public void AssetExtractProviderExportsCombatBackgroundLayerStack()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var root = new Node2D { Name = "OvergrowthBackground" };
        root.AddChild(new Sprite2D
        {
            Name = "overgrowth_bg_00_a",
            Texture = SolidTexture([16, 96, 48, 255]),
            Position = Vector2.Zero,
        });
        root.AddChild(new Sprite2D
        {
            Name = "overgrowth_bg_01_a",
            Texture = SolidTexture([96, 128, 48, 192]),
            Position = new Vector2(1, 0),
        });
        var scene = new PackedScene();
        Assert.Equal(Error.Ok, scene.Pack(root));

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractPackedSceneAsync",
            scene,
            AssetRequest(sourcePath: "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("flattened-combat-background-scene", result.Response.RenderMode);
        Assert.True(result.Response.Width > 0);
        Assert.True(result.Response.Height > 0);
        Assert.True(HasEncodedVisiblePixels(result.Response.Contents));
        Assert.Contains(
            result.Response.Notes,
            note => note.Contains("literal combat background root scene", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssetExtractProviderKeepsIndividualCombatLayerScenesAsSimpleTexturePreviews()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var sceneRoot = new Sprite2D
        {
            Texture = SolidTexture([32, 80, 120, 255]),
        };
        var scene = new PackedScene();
        Assert.Equal(Error.Ok, scene.Pack(sceneRoot));

        var result = InvokeProviderExtractAsync(
            provider,
            "ExtractPackedSceneAsync",
            scene,
            AssetRequest(sourcePath: "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_a.tscn"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("flattened-scene-texture", result.Response.RenderMode);
        Assert.True(HasEncodedVisiblePixels(result.Response.Contents));
    }

    [Fact]
    public void AssetExtractProviderSynthesizesAlphaForVfxPreviewPixels()
    {
        var image = Image.Create(2, 1, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(1, 0.25f, 0, 0));
        image.SetPixel(1, 0, new Color(0, 0, 0, 0));

        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "NormalizePreviewAlphaFromColor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var normalized = Assert.IsType<Image>(method!.Invoke(null, [image]));

        Assert.True(normalized.GetPixel(0, 0).A > 0);
        Assert.Equal(0, normalized.GetPixel(1, 0).A);
    }

    [Fact]
    public void AssetExtractProviderEncodesLoadedImageResources()
    {
        var provider = new Sts2AssetExtractProvider(new InMemoryLogStream());
        var image = Image.Create(2, 1, false, Image.Format.Rgba8);
        image.SetPixel(0, 0, new Color(1, 0, 0, 1));
        image.SetPixel(1, 0, new Color(0, 1, 0, 1));
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "EncodeImageResult",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var response = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult>(
            method!.Invoke(provider, [
                image,
                AssetRequest("res://images/packed/common_ui/cursor_default.png"),
                "flattened-static-image",
                Array.Empty<string>()
            ]));

        Assert.Null(response.Error);
        Assert.Equal("png", response.Response.Format);
        Assert.Equal("image/png", response.Response.ContentType);
        Assert.Equal("flattened-static-image", response.Response.RenderMode);
        Assert.True(HasEncodedVisiblePixels(response.Response.Contents));
    }

    private static ImageTexture SolidTexture(byte[] rgba)
    {
        var image = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, rgba);
        return ImageTexture.CreateFromImage(image);
    }

    private static bool HasEncodedVisiblePixels(byte[] contents)
    {
        var image = new Image();
        Assert.Equal(Error.Ok, image.LoadPngFromBuffer(contents));
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "HasVisiblePixels",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(Image)],
            null);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [image]));
    }

    private static bool EncodedImageContainsRgba(byte[] contents, byte red, byte green, byte blue, byte alpha)
    {
        var image = new Image();
        Assert.Equal(Error.Ok, image.LoadPngFromBuffer(contents));
        var targetRed = red / 255f;
        var targetGreen = green / 255f;
        var targetBlue = blue / 255f;
        var targetAlpha = alpha / 255f;
        for (var y = 0; y < image.GetHeight(); y += 1)
        {
            for (var x = 0; x < image.GetWidth(); x += 1)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - targetRed) < 0.001f
                    && Math.Abs(pixel.G - targetGreen) < 0.001f
                    && Math.Abs(pixel.B - targetBlue) < 0.001f
                    && Math.Abs(pixel.A - targetAlpha) < 0.001f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool DetectBrowserFontFormat(byte[] bytes, out string format)
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "TryDetectBrowserFontFormat",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(byte[]), typeof(string).MakeByRefType()],
            null);
        Assert.NotNull(method);

        var args = new object?[] { bytes, null };
        var detected = Assert.IsType<bool>(method!.Invoke(null, args));
        format = Assert.IsType<string>(args[1]);
        return detected;
    }

    private static Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot AssetRequest(
        string sourcePath,
        string? loadPath = null)
    {
        return new Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot(
            RequestId: "asset-test",
            SourceRoot: "resources",
            SourcePath: sourcePath,
            LoadPath: loadPath ?? sourcePath,
            OutputFormat: "png");
    }

    private static Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot VirtualAssetRequest(string query)
    {
        return new Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot(
            RequestId: "asset-test",
            SourceRoot: "virtual",
            SourcePath: query,
            LoadPath: query,
            OutputFormat: "png");
    }

    private static (bool Success, string? ErrorMessage, Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult Response) InvokeProviderMainExtractAsync(
        Sts2AssetExtractProvider provider,
        Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot request)
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "ExtractOnMainThreadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task<Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult>>(
            method!.Invoke(provider, [request]));
        var response = task.GetAwaiter().GetResult();
        var success = response.Error is null;
        var errorMessage = success ? null : response.Error!.Details.FirstOrDefault().Note ?? response.Error.Message;
        return (success, errorMessage, response);
    }

    private static (bool Success, string? ErrorMessage, Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult Response) InvokeProviderExtract(
        Sts2AssetExtractProvider provider,
        string methodName,
        Resource resource,
        Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot request)
    {
        var method = FindProviderResourceMethod(methodName, resource.GetType());
        Assert.NotNull(method);

        var response = Assert.IsType<Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult>(
            method!.Invoke(provider, [resource, request]));
        var success = response.Error is null;
        var errorMessage = success ? null : response.Error!.Details.FirstOrDefault().Note ?? response.Error.Message;
        return (success, errorMessage, response);
    }

    private static (bool Success, string? ErrorMessage, Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult Response) InvokeProviderExtractAsync(
        Sts2AssetExtractProvider provider,
        string methodName,
        Resource resource,
        Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot request)
    {
        var method = FindProviderResourceMethod(methodName, resource.GetType());
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task<Spirectl.Sts2.Core.Artifacts.AssetExtractOperationResult>>(
            method!.Invoke(provider, [resource, request]));
        var response = task.GetAwaiter().GetResult();
        var success = response.Error is null;
        var errorMessage = success ? null : response.Error!.Details.FirstOrDefault().Note ?? response.Error.Message;
        return (success, errorMessage, response);
    }

    private static MethodInfo? FindProviderResourceMethod(string methodName, Type resourceType)
    {
        return typeof(Sts2AssetExtractProvider)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsAssignableFrom(resourceType)
                    && parameters[1].ParameterType == typeof(Spirectl.Sts2.Core.Artifacts.AssetExtractRequestSnapshot);
            });
    }

    private sealed class FakeSpineSprite
    {
        public string PreviewAnimation { get; set; } = string.Empty;
        public bool PreviewFrame { get; set; }
        public double PreviewTime { get; set; } = -1d;
        public object SkeletonDataRes { get; set; } = new();
        public List<string> SetAnimationCalls { get; } = [];

        public void SetAnimation(string animation)
        {
            SetAnimationCalls.Add(animation);
        }
    }

    private sealed class TestSkeletonDataResource : Resource
    {
        public string[] AnimationNames { get; set; } = [];
        public string[] SkinNames { get; set; } = [];
    }

    private sealed class TestSpineSprite : Node2D
    {
        public object? SkeletonDataRes { get; set; }
        public string PreviewAnimation { get; set; } = string.Empty;
        public string PreviewSkin { get; set; } = string.Empty;
        public bool PreviewFrame { get; set; }
        public double PreviewTime { get; set; } = -1d;
        public List<string> SetAnimationCalls { get; } = [];
        public List<string> SetSkinCalls { get; } = [];

        public void SetAnimation(string animation) => SetAnimationCalls.Add(animation);

        public void SetSkin(string skin) => SetSkinCalls.Add(skin);
    }

    private sealed class FakeCharacterModel(string id)
    {
        public FakeModelId Id { get; } = new(id);

        public TestSpineSprite CreateVisuals() => UninitializedTestSpineSprite();
    }

    private sealed class FakeAlly(FakeCharacterModel character)
    {
        public FakePlayer Player { get; } = new(character);
        public TestSpineSprite Visuals { get; } = UninitializedTestSpineSprite();

        public TestSpineSprite CreateVisuals() => Visuals;
    }

    private sealed class FakePlayer(FakeCharacterModel character)
    {
        public FakeCharacterModel Character { get; } = character;
    }

    private sealed class FakeModelId(string entry)
    {
        public string Entry { get; } = entry;
    }

    private sealed class FakeKaiserVisualHooks
    {
        public enum ArmSide
        {
            Left,
            Right,
        }

        public ArmSide? HurtSide { get; private set; }

        public float? ChargeDuration { get; private set; }

        public void PlayHurtAnim(ArmSide side)
        {
            HurtSide = side;
        }

        public Task PlayRightSideChargeUpAnim(float duration)
        {
            ChargeDuration = duration;
            return Task.CompletedTask;
        }
    }

    private static TestSpineSprite UninitializedTestSpineSprite()
    {
        return (TestSpineSprite)FormatterServices.GetUninitializedObject(typeof(TestSpineSprite));
    }
#endif

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void AssetExtractProviderCharacterBattlefieldRenderEnablesAlphaNormalization()
    {
        var normalizeField = typeof(Sts2AssetExtractProvider).GetField(
            "CharacterBattlefieldNormalizePreviewAlpha",
            BindingFlags.Static | BindingFlags.NonPublic);
        var noteField = typeof(Sts2AssetExtractProvider).GetField(
            "CharacterBattlefieldAlphaNormalizationNote",
            BindingFlags.Static | BindingFlags.NonPublic);
        var disableProcessField = typeof(Sts2AssetExtractProvider).GetField(
            "CharacterBattlefieldDisableProcessDuringCapture",
            BindingFlags.Static | BindingFlags.NonPublic);
        var characterSelectBgSpineStillNormalizeField = typeof(Sts2AssetExtractProvider).GetField(
            "CharacterSelectBgSpineStillNormalizePreviewAlpha",
            BindingFlags.Static | BindingFlags.NonPublic);
        var characterSelectBgSpineStillNoteField = typeof(Sts2AssetExtractProvider).GetField(
            "CharacterSelectBgSpineStillAlphaNormalizationNote",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(normalizeField);
        Assert.NotNull(noteField);
        Assert.NotNull(disableProcessField);
        Assert.NotNull(characterSelectBgSpineStillNormalizeField);
        Assert.NotNull(characterSelectBgSpineStillNoteField);
        Assert.True(Assert.IsType<bool>(normalizeField!.GetRawConstantValue()));
        Assert.False(Assert.IsType<bool>(disableProcessField!.GetRawConstantValue()));
        Assert.True(Assert.IsType<bool>(characterSelectBgSpineStillNormalizeField!.GetRawConstantValue()));
        Assert.Contains(
            "alpha normalization",
            Assert.IsType<string>(noteField!.GetRawConstantValue()),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "alpha normalization",
            Assert.IsType<string>(characterSelectBgSpineStillNoteField!.GetRawConstantValue()),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssetExtractProviderFramesEventBackgroundSpineStillWithBottomOverscan()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "SpineStillOverscanFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // The Neow cave layers' authored rect: -330..2252 x -49..1172. It overscans
        // the 1920x1080 viewport on every edge, including the bottom (1172 > 1080), so
        // the waterfall Spine still captured over it is not clipped at the viewport.
        var caveExtent = new Rect2(-330, -49, 2582, 1221);
        var frame = method!.Invoke(null, [caveExtent]);
        Assert.NotNull(frame);

        var viewportSize = Assert.IsType<Vector2I>(frame!.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        var nodeScale = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodeScale")!.GetValue(frame));

        // The SubViewport spans the full overscan extent with no container scale baked
        // in (the catalog applies the 0.89 AncientBgContainer scale when mounting).
        Assert.Equal(new Vector2I(2582, 1221), viewportSize);
        Assert.Equal(Vector2.One, nodeScale);
        // The render node is shifted so the extent's top-left maps to the origin.
        Assert.Equal(new Vector2(330, 49), nodePosition);
        // The captured frame reaches below the viewport bottom: scene y = 1080 (viewport
        // bottom) maps to 1129 in the capture, still inside the 1221 capture height.
        Assert.True(nodePosition.Y + 1080f < viewportSize.Y);
    }

    [Fact]
    public void AssetExtractProviderOverscansCharacterSelectBackgroundBeyondTheRootViewport()
    {
        var method = typeof(Sts2AssetExtractProvider).GetMethod(
            "CharacterSelectBackgroundOverscanExtent",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var extent = Assert.IsType<Rect2>(method!.Invoke(null, [new Vector2I(1920, 1080)]));

        // The live `AnimatedBg` container reports exactly this rect through
        // `dev scene tree --computed-transform` at a 1920x1080 root viewport:
        // globalRect position (-516,-140), size 2816x1320 (offset -388,-80; size 2560x1200;
        // scale 1.1 about pivot 1280,600). The synthetic render root reproduces it 1:1, so a
        // capture over this rect is the same picture the game itself composes.
        Assert.Equal(-516f, extent.Position.X, 3);
        Assert.Equal(-140f, extent.Position.Y, 3);
        Assert.Equal(2816f, extent.Size.X, 3);
        Assert.Equal(1320f, extent.Size.Y, 3);

        // THE POINT: it overscans the root viewport on every edge, so the raster contains the
        // side art a wider-than-16:9 mirror stage re-centres onto (the pre-fix capture was the
        // bare 0..1920 x 0..1080 viewport and physically held no such pixels).
        Assert.True(extent.Position.X < 0f);
        Assert.True(extent.Position.Y < 0f);
        Assert.True(extent.Position.X + extent.Size.X > 1920f);
        Assert.True(extent.Position.Y + extent.Size.Y > 1080f);

        // And the overscan frame built from it shifts the render root so the extent's
        // top-left maps to the capture origin — the offset the placement composition adds back.
        var frameMethod = typeof(Sts2AssetExtractProvider).GetMethod(
            "SpineStillOverscanFrame",
            BindingFlags.Static | BindingFlags.NonPublic);
        var frame = frameMethod!.Invoke(null, [extent]);
        var viewportSize = Assert.IsType<Vector2I>(frame!.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        var nodePosition = Assert.IsType<Vector2>(frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        Assert.Equal(new Vector2I(2816, 1320), viewportSize);
        Assert.Equal(516f, nodePosition.X, 3);
        Assert.Equal(140f, nodePosition.Y, 3);
    }
#endif
}
