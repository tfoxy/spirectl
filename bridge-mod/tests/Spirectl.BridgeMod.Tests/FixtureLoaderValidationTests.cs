#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Live;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class FixtureLoaderValidationTests
{
    [Fact]
    public void ExistingMainMenuWithSubmenuStackIsReusable()
    {
        Assert.True(Sts2FixtureLoader.HasReusableMainMenuSubmenuStack(
            new MainMenuLike(submenuStack: new object())));
    }

    [Fact]
    public void MissingMainMenuSubmenuStackIsNotReusable()
    {
        Assert.False(Sts2FixtureLoader.HasReusableMainMenuSubmenuStack(null));
        Assert.False(Sts2FixtureLoader.HasReusableMainMenuSubmenuStack(
            new MainMenuLike(submenuStack: null)));
        Assert.False(Sts2FixtureLoader.HasReusableMainMenuSubmenuStack(
            new object()));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host_standard")]
    [InlineData("host_daily")]
    [InlineData("host_custom")]
    public void CommandLineFastMpHostValuesAreRecognized(string value)
    {
        Assert.True(Sts2FixtureLoader.IsCommandLineFastMpHostValue(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("join")]
    [InlineData("load")]
    [InlineData("host-standard")]
    public void NonHostFastMpValuesAreIgnored(string? value)
    {
        Assert.False(Sts2FixtureLoader.IsCommandLineFastMpHostValue(value));
    }

    [Theory]
    [InlineData("Screens.CharacterSelect.NCharacterSelectScreen", false)]
    [InlineData("Screens.CharacterSelect.NMultiplayerLoadGameScreen", false)]
    [InlineData("combat", true)]
    [InlineData("Rooms.NMapScreen", true)]
    public void CrossFamilyLoadsReleaseLobbyOwnershipBeforeRealization(string screen, bool expected)
    {
        Assert.Equal(expected, Sts2FixtureLoader.RequiresLobbyCleanup(screen));
    }

    [Theory]
    [InlineData("MenuLobbyFixtureFamily", "LoadMainMenuAsync", "LoadLobbyAsync")]
    [InlineData("CombatSelectionFixtureFamily", "LoadCombatAsync", "LoadSelectionAsync", "LoadOverlayAsync")]
    [InlineData("RoomMapFixtureFamily", "LoadMapAsync", "LoadRewardsAsync", "LoadGameOverAsync", "LoadRestSiteAsync", "LoadEventRoomAsync", "LoadTreasureFamilyAsync", "LoadShopAsync")]
    public void FixtureFamiliesOwnTheirScreenRealizers(string familyName, params string[] realizerNames)
    {
        var family = typeof(Sts2FixtureLoader).GetNestedType(familyName, BindingFlags.NonPublic);
        Assert.NotNull(family);

        foreach (var realizerName in realizerNames)
        {
            Assert.NotNull(family!.GetMethod(realizerName, BindingFlags.Instance | BindingFlags.NonPublic));
        }

        var constructor = Assert.Single(family!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.DoesNotContain(constructor.GetParameters(), parameter =>
            parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<,,>)
            && parameter.ParameterType.GenericTypeArguments[0] == typeof(FixtureLoadRequestSnapshot)
            && parameter.ParameterType.GenericTypeArguments[1].Name == "FixtureDocument");
    }

    [Fact]
    public void StableScreenCandidateIgnoresMissingScreen()
    {
        Assert.False(Sts2FixtureLoader.IsStableScreenIdCandidate(null, "Screens.CharacterSelect.NCharacterSelectScreen"));
    }

    [Fact]
    public async Task FixtureRenderFrameWaitAllowsZeroFrameNoOp()
    {
        await Sts2FixtureLoader.AwaitFixtureRenderFramesAsync(0);
    }

    [Theory]
    [InlineData(true, true, 5, true, true, true, false, false, false, true)]
    [InlineData(true, true, 0, true, true, true, false, false, false, false)]
    [InlineData(true, true, 5, false, true, true, false, false, false, false)]
    [InlineData(true, true, 5, true, false, true, false, false, false, false)]
    [InlineData(true, true, 5, true, true, false, false, false, false, false)]
    [InlineData(true, true, 5, true, true, true, true, false, false, false)]
    [InlineData(true, true, 5, true, true, true, false, true, false, false)]
    [InlineData(true, true, 5, true, true, true, false, false, true, false)]
    [InlineData(false, true, 5, true, true, true, false, false, false, false)]
    public void OpeningCombatDealRequiresIdleActionQueueAfterTheHandArrives(
        bool hasCombatState,
        bool hasCombatPiles,
        int handCardCount,
        bool isAnyPlayerInPlayPhase,
        bool isPlayerCombatSide,
        bool hasActionQueue,
        bool isActionExecutorRunning,
        bool hasRunningAction,
        bool hasReadyAction,
        bool expected)
    {
        Assert.Equal(
            expected,
            Sts2FixtureLoader.IsOpeningCombatDealSettled(
                hasCombatState,
                hasCombatPiles,
                handCardCount,
                isAnyPlayerInPlayPhase,
                isPlayerCombatSide,
                hasActionQueue,
                isActionExecutorRunning,
                hasRunningAction,
                hasReadyAction));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void OpeningCombatDealRequiresIdleQueueAcrossAProcessFrame(int consecutiveIdleObservations, bool expected)
    {
        Assert.Equal(expected, Sts2FixtureLoader.HasStableOpeningCombatDealIdleObservation(consecutiveIdleObservations));
    }

    [Fact]
    public void GeneratedShopInventoryReportIsInferredWhenShopRecipeIsOmitted()
    {
        var fields = Sts2FixtureLoader.InferredGeneratedShopFields(usesGeneratedInventory: true);

        var field = Assert.Single(fields);
        Assert.Equal("run.currentRoom.shop.inventory", field.FieldPath);
        Assert.Equal("generated", field.ValueSummary);
        Assert.Equal("inferred-runtime-shop-inventory", field.ReasonCode);
    }

    [Fact]
    public void GeneratedShopInventoryReportIsOmittedForAuthoredShopRecipe()
    {
        Assert.Empty(Sts2FixtureLoader.InferredGeneratedShopFields(usesGeneratedInventory: false));
    }

    [Fact]
    public void FixtureModelResolverMatchesStrippedTheAssetKeyAliases()
    {
        Assert.True(Sts2ModelResolver.MatchesFixtureIdAlias("THE_ABACUS", "abacus"));
        Assert.True(Sts2ModelResolver.MatchesFixtureIdAlias("THE_ABACUS", "the-abacus"));
        Assert.False(Sts2ModelResolver.MatchesFixtureIdAlias("THE_ABACUS", "boot"));
    }

    [Fact]
    public void StaleLobbyRetryRecognizesDisposedGodotObjectExceptions()
    {
        var disposed = new ObjectDisposedException("MegaCrit.Sts2.addons.mega_text.MegaLabel");
        var reflected = new TargetInvocationException(disposed);

        Assert.Same(disposed, Sts2FixtureLoader.GetStaleGodotObjectException(reflected));
        Assert.True(Sts2FixtureLoader.ShouldRetryStaleLobbyMaterialization(reflected, attempt: 0, maxAttempts: 2));
        Assert.False(Sts2FixtureLoader.ShouldRetryStaleLobbyMaterialization(reflected, attempt: 1, maxAttempts: 2));
    }

    [Fact]
    public void StaleLobbyRetryIgnoresNonDisposalExceptions()
    {
        Assert.Null(Sts2FixtureLoader.GetStaleGodotObjectException(new InvalidOperationException("not stale")));
        Assert.False(Sts2FixtureLoader.ShouldRetryStaleLobbyMaterialization(new InvalidOperationException("not stale"), attempt: 0, maxAttempts: 2));
    }

    [Fact]
    public void CombatPlayerResolutionAcceptsCanonicalHostOwnedAndHostLocalSeats()
    {
        var loaderType = typeof(Sts2FixtureLoader);
        var documentType = loaderType.GetNestedType("FixtureWireDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var wireFixture = JsonSerializer.Deserialize(
            """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "combat",
              "name": "two-ironclad-nibbits-weak-combat",
              "run": {
                "seed": "fixture-two-ironclad-nibbits-weak-combat",
                "currentActIndex": 0,
                "actFloor": 1,
                "view": { "playerId": "p:1" },
                "players": [
                  { "id": "p:1", "characterId": "IRONCLAD", "creature": { "currentHp": 80, "maxHp": 80 }, "isLocal": true, "isHostLocalSeat": false, "slotId": 0 },
                  { "id": "p:2", "characterId": "IRONCLAD", "creature": { "currentHp": 80, "maxHp": 80 }, "isLocal": true, "isHostLocalSeat": true, "slotId": 1 }
                ],
                "currentRoom": { "combat": { "encounterId": "NIBBITS_WEAK" } }
              }
            }
            """,
            documentType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(wireFixture);

        var projectMethod = documentType!.GetMethod("ToRecipeDocument", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(projectMethod);
        var fixture = projectMethod!.Invoke(wireFixture, []);
        Assert.NotNull(fixture);

        var method = loaderType.GetMethod("ValidateHostLocalPlayerOwnership", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method.Invoke(null, [
            new FixtureLoadRequestSnapshot(
                RequestId: "fixture-1",
                SchemaVersion: "spirectl.fixture/v0",
                FixtureName: "two-ironclad-nibbits-weak-combat",
                SourcePath: "/repo/fixtures/two-ironclad-nibbits-weak-combat.sts2.fixture.yaml",
                FixtureJson: "{}"),
            fixture,
            "combat",
        ]);

        Assert.Null(result);
    }

    [Fact]
    public void CrystalSphereStateProjectsIntoFixtureRecipe()
    {
        var loaderType = typeof(Sts2FixtureLoader);
        var documentType = loaderType.GetNestedType("FixtureWireDocument", BindingFlags.NonPublic);
        Assert.NotNull(documentType);

        var wireFixture = JsonSerializer.Deserialize(
            """
            {
              "schemaVersion": "spirectl.fixture/v0",
              "screen": "Events.Custom.CrystalSphere.NCrystalSphereScreen",
              "name": "event-crystal-sphere-grid-revealed",
              "run": {
                "seed": "event-crystal-sphere-grid",
                "view": { "playerId": "p:1" },
                "players": [
                  { "id": "p:1", "characterId": "IRONCLAD" }
                ],
                "currentRoom": {
                  "event": {
                    "canonicalEventModelId": "CRYSTAL_SPHERE",
                    "playerStates": [
                      {
                        "playerId": "p:1",
                        "options": [
                          { "textKey": "CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN", "wasChosen": true }
                        ],
                        "crystalSphere": {
                          "selectedTool": "big",
                          "divinationsRemaining": 2,
                          "cells": [
                            { "id": "crystal-sphere:cell:3:2", "x": 3, "y": 2, "isHidden": false }
                          ]
                        }
                      }
                    ]
                  }
                }
              }
            }
            """,
            documentType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(wireFixture);

        var projectMethod = documentType!.GetMethod("ToRecipeDocument", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(projectMethod);
        var fixture = projectMethod!.Invoke(wireFixture, []);
        Assert.NotNull(fixture);

        var eventRoom = fixture!.GetType().GetProperty("EventRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(fixture);
        Assert.NotNull(eventRoom);
        var crystalSphere = eventRoom!.GetType().GetProperty("CrystalSphere", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(eventRoom);
        Assert.NotNull(crystalSphere);

        Assert.Equal("big", crystalSphere!.GetType().GetProperty("SelectedTool")!.GetValue(crystalSphere));
        Assert.Equal(2, crystalSphere.GetType().GetProperty("DivinationsRemaining")!.GetValue(crystalSphere));
        var cells = Assert.IsAssignableFrom<System.Collections.IList>(crystalSphere.GetType().GetProperty("Cells")!.GetValue(crystalSphere));
        var cell = Assert.Single(cells.Cast<object>());
        Assert.Equal("crystal-sphere:cell:3:2", cell.GetType().GetProperty("Id")!.GetValue(cell));
        Assert.Equal(3, cell.GetType().GetProperty("X")!.GetValue(cell));
        Assert.Equal(2, cell.GetType().GetProperty("Y")!.GetValue(cell));
        Assert.Equal(false, cell.GetType().GetProperty("IsHidden")!.GetValue(cell));
    }

    [Fact]
    public void FixtureModelResolutionRequiresExactGameIds()
    {
        Assert.True(Sts2ModelResolver.MatchesExactFixtureModelId("NIBBITS_WEAK", "NIBBITS_WEAK"));
        Assert.False(Sts2ModelResolver.MatchesExactFixtureModelId("NIBBITS_WEAK", "nibbits-weak"));
        Assert.False(Sts2ModelResolver.MatchesExactFixtureModelId("NIBBITS_NORMAL", "  NIBBITS_NORMAL  "));

        Assert.True(Sts2ModelResolver.MatchesExactFixtureModelId("IRONCLAD", "IRONCLAD"));
        Assert.False(Sts2ModelResolver.MatchesExactFixtureModelId("IRONCLAD", "ironclad"));
        Assert.False(Sts2ModelResolver.MatchesExactFixtureModelId("IRONCLAD", "  IRONCLAD  "));
    }

    private sealed class MainMenuLike(object? submenuStack)
    {
        public object? SubmenuStack { get; } = submenuStack;
    }
}
#endif
