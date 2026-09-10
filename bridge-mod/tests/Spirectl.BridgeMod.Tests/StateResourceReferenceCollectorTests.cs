using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class StateResourceReferenceCollectorTests
{
    [Fact]
    public void CollectsReferencedModelsAndAssetsWithoutInliningCatalogPayloads()
    {
        var state = new GameStateSnapshot(
            SchemaVersion: "spirectl.state/v0",
            GameVersion: "sts2-test",
            BridgeVersion: "spirectl-bridge/test",
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "combat",
            ScreenTitle: "Combat",
            ScreenInstanceId: "screen:combat:test",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
            Menu: null,
            Lobby: new LobbyStateSnapshot(
                "start-run",
                "selecting",
                [new LobbyPlayerSnapshot("p1", "ready", "Host", "silent", true, 0, true, true, false)],
                [
                    new LobbyCharacterSnapshot(
                        "silent",
                        "Silent",
                        true,
                        PassiveId: "ring-of-the-snake",
                        PassiveIconAssetKey: "model://relics/ring-of-the-snake/icon",
                        PortraitAssetKey: "model://characters/silent/characterSelectIcon",
                        IconAssetKey: "model://characters/silent/icon",
                        SelectBackgroundAssetKey: "model://characters/silent/characterSelectBg"),
                ],
                "p1",
                "p1",
                "host",
                new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal),
                new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)),
            Run: new RunStateSnapshot(
                "seed",
                1,
                1,
                [
                    new PlayerStateSnapshot(
                        "p1",
                        "silent",
                        70,
                        70,
                        Relics:
                        [
                            new RelicStateSnapshot(
                                "relic:p1:ring",
                                "ring-of-the-snake",
                                "Ring of the Snake",
                                null,
                                "p1",
                                0,
                                false,
                                0,
                                null,
                                [new AssetReferenceSnapshot("icon", "model://relics/ring-of-the-snake/icon", "Ring", false)]),
                        ]),
                ],
                new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)),
            Combat: new CombatStateSnapshot(
                1,
                "p1",
                true,
                [
                    new CardStateSnapshot(
                        "card:p1:0",
                        "Jab",
                        1,
                        "p1",
                        true,
                        null,
                        ["enemy:jaw-worm"],
                        false,
                        ModelId: "strike",
                        AfflictionModelId: "BOUND",
                        AfflictionAmount: 1,
                        AssetRefs: [new AssetReferenceSnapshot("image", "model://cards/strike/image", "Jab", false)]),
                ],
                [
                    new CombatPlayerStateSnapshot(
                        "p1",
                        "silent",
                        70,
                        70,
                        0,
                        3,
                        3,
                        []),
                ],
                [
                    new EnemyStateSnapshot(
                        "enemy:jaw-worm",
                        "Gnash Grub",
                        40,
                        "attack",
                        40,
                        0,
                        true,
                        [],
                        ModelId: "jaw-worm",
                        AssetRefs: [new AssetReferenceSnapshot("visual", "model://monsters/jaw-worm/visuals", "Gnash Grub", false)]),
                ],
                new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal),
                EncounterVisuals: new EncounterVisualsStateSnapshot(
                    "composed://encounters/jaw-worm/scene-package",
                    "jaw-worm",
                    true,
                    [],
                    [])),
            Map: null,
            EventRoom: null,
            TreasureRoom: null,
            RelicSelection: null,
            RestSite: null,
            Shop: null,
            Rewards: null,
            CardSelection: null,
            SimpleCardSelection: null,
            DeckCardSelection: null,
            BundleSelection: null,
            MultiplayerLobby: null,
            Choices: [],
            AvailableActions: [],
            Notices: [],
            Debug: null);

        var references = StateResourceReferenceCollector.Collect(state);

        Assert.Equal("combat", references.ScreenId);
        Assert.Contains(references.ModelReferences, model => model is { Family: "characters", Id: "silent" });
        Assert.Contains(references.ModelReferences, model => model is { Family: "relics", Id: "ring-of-the-snake" });
        Assert.Contains(references.ModelReferences, model => model is { Family: "cards", Id: "strike" });
        Assert.Contains(references.ModelReferences, model => model is { Family: "afflictions", Id: "BOUND" });
        Assert.Contains(references.ModelReferences, model => model is { Family: "monsters", Id: "jaw-worm" });
        Assert.Contains("silent", references.ModelsByFamily["characters"]);
        Assert.Contains("BOUND", references.ModelsByFamily["afflictions"]);
        Assert.Contains(references.AssetReferences, asset => asset.Key == "model://characters/silent/characterSelectIcon");
        Assert.Contains(references.AssetReferences, asset => asset.Key == "model://cards/strike/image");
        Assert.Contains(references.AssetReferences, asset => asset.Key == "composed://encounters/jaw-worm/scene-package");
        Assert.Equal(
            references.AssetReferences.Select(asset => asset.Key).Distinct(StringComparer.Ordinal).Count(),
            references.AssetReferences.Count);
    }
}
