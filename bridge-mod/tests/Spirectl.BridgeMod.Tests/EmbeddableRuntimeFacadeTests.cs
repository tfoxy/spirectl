using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Fixtures;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.Scenarios;
using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EmbeddableRuntimeFacadeTests
{

    [Fact]
    public void FacadeExposesEmbeddableAssetProvider()
    {
        ISpirectlRuntime runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MultiplayerLobbySnapshotWithGeometry()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider());

        Assert.NotNull(runtime.Assets);
        Assert.IsType<PlaceholderEmbeddableAssetProvider>(runtime.Assets);
    }

    [Fact]
    public void CapabilitiesDescribeEmbeddedRuntimeWithoutTransportHost()
    {
        var runtime = EmbeddedRuntimeTestFactory.Create();

        var capabilities = runtime.GetCapabilities();

        Assert.Equal(StateSnapshot.CurrentSchemaVersion, capabilities.SchemaVersion);
        Assert.Equal("embedded", capabilities.TransportKind);
        Assert.Contains(capabilities.Capabilities, capability => capability.Id == "state" && capability.Supported);
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "current-state"
            && capability.Supported
            && capability.Summary.Contains("Direct current semantic observation", StringComparison.Ordinal));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "state-subscriptions"
            && capability.Supported
            && capability.Summary.Contains("Reactive embedded semantic state subscriptions", StringComparison.Ordinal));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "live-sts2-host"
            && !capability.Supported
            && !string.IsNullOrWhiteSpace(capability.UnsupportedReason));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "semantic-actions"
            && !capability.Supported
            && capability.UnsupportedReason is not null);
        Assert.DoesNotContain(capabilities.Capabilities, capability =>
            capability.Id.StartsWith("presentation-", StringComparison.Ordinal));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "game-models"
            && capability.Supported
            && capability.Summary.Contains("Immutable game model metadata", StringComparison.Ordinal));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "game-reference"
            && capability.Supported
            && capability.Summary.Contains("Topic-addressed game reference data", StringComparison.Ordinal));
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "asset-extraction"
            && !capability.Supported
            && capability.UnsupportedReason is not null);
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "spine-catalog"
            && !capability.Supported
            && capability.UnsupportedReason is not null);
        // An embedder guards on this id BEFORE calling BakeSpineGeoClip, so it has to be advertised even when the
        // scaffold cannot serve it — an unadvertised capability reads as "no such capability" and refuses every
        // bake, which is indistinguishable from the bake being broken.
        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "spine-geoclip-bake"
            && !capability.Supported
            && capability.UnsupportedReason is not null);
        Assert.Empty(capabilities.SupportedActions);
    }

    [Fact]
    public void CapabilitiesAdvertiseAnimationHints()
    {
        var runtime = EmbeddedRuntimeTestFactory.Create();

        var capabilities = runtime.GetCapabilities();

        Assert.Contains(capabilities.Capabilities, capability =>
            capability.Id == "animation-hints"
            && capability.Supported
            && capability.Summary.Contains("Godot-tween timing hints", StringComparison.Ordinal));
    }

    [Fact]
    public void SubscribeAnimationHintsDeliversLiveHintsAndStopsAfterDispose()
    {
        EmbeddableAnimationHintHub.Shared.Reset();
        try
        {
            ISpirectlRuntime runtime = EmbeddedRuntimeTestFactory.Create();
            var received = new List<TweenAnimationHint>();
            var subscription = runtime.SubscribeAnimationHints(
                new AnimationHintSubscriptionRequest(),
                received.Add);

            EmbeddableAnimationHintHub.Shared.Publish(new TweenAnimationHint(
                "combat/combat_screen", "PlayerHud", "modulate:a", To: "0.5", DurationMs: 120, Trans: "Quad", Ease: "Out"));

            Assert.Single(received);
            Assert.Equal("combat/combat_screen", received[0].Scene);
            Assert.Equal("modulate:a", received[0].Property);
            Assert.Equal("0.5", received[0].To);
            Assert.Equal(120, received[0].DurationMs);
            Assert.Equal("Quad", received[0].Trans);

            subscription.Dispose();
            EmbeddableAnimationHintHub.Shared.Publish(new TweenAnimationHint(
                "combat/combat_screen", "PlayerHud", "scale", To: null, DurationMs: 80, Trans: null, Ease: null));
            Assert.Single(received); // no delivery after dispose
        }
        finally
        {
            EmbeddableAnimationHintHub.Shared.Reset();
        }
    }

    [Fact]
    public void GetModelsUsesBridgeRuntimeModelCatalogProvider()
    {
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new FixedModelCatalogProvider());

        var result = runtime.GetModels(new ModelCatalogRequestSnapshot("characters", ["silent", "ironclad"]));

        Assert.Equal(ModelCatalogStatus.Ok, result.Status);
        Assert.Equal("characters", result.Family);
        Assert.Equal(["THE_SILENT", "IRONCLAD"], [.. result.Models.Select(model => model.Id)]);
    }

    [Fact]
    public void FacadeGetReferenceUsesBridgeRuntimeReferenceDataProvider()
    {
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            referenceDataProvider: new FixedReferenceDataProvider());

        var result = runtime.GetReference(new ReferenceRequestSnapshot("randomCharacter", []));

        Assert.Null(result.Error);
        Assert.Equal(ReferenceStatus.Ok, result.Status);
        var snapshot = Assert.IsType<RandomCharacterSnapshot>(result.Payload);
        Assert.Equal(RandomCharacterFacts.Id, snapshot.Character.Id);
    }

    [Fact]
    public void GetModelsPassesLanguageToModelCatalogProvider()
    {
        var provider = new CapturingModelCatalogProvider();
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: provider);

        var result = runtime.GetModels(new ModelCatalogRequestSnapshot("characters", ["silent", "ironclad"], "esp"));

        Assert.Equal("esp", provider.LastRequest?.Language);
        Assert.Equal("esp", result.Language);
    }

    [Fact]
    public void ProtocolAdapterMapsModelCatalogResponse()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new FixedModelCatalogProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-1",
            Family = "characters",
            Ids = { "ironclad" },
            Language = "esp",
        });

        var success = Assert.IsType<Spirectl.Proto.V0.ModelCatalogResponse>(response.ResultCase switch
        {
            Spirectl.Proto.V0.ModelCatalogResult.ResultOneofCase.Success => response.Success,
            _ => null,
        });
        Assert.Equal("models-1", success.RequestId);
        Assert.Equal(Spirectl.Proto.V0.ModelCatalogStatus.Ok, success.Status);
        Assert.Equal("esp", success.Language);
        var character = Assert.Single(success.Models).Character;
        Assert.Equal("IRONCLAD", character.Id);
        Assert.Equal("The Bulwark", character.Title);
        Assert.Equal("EmberHeart", Assert.Single(character.StartingRelics));
        Assert.Equal("model://characters/ironclad/visuals", character.VisualsAssetKey);
        Assert.Equal("model://characters/ironclad/icon", character.IconAssetKey);
        Assert.Equal("model://characters/ironclad/iconOutline", character.IconOutlineAssetKey);
        Assert.Equal("model://characters/ironclad/energyCounter", character.EnergyCounterAssetKey);
        Assert.Equal("model://characters/ironclad/characterSelectBgSpineStill", character.CharacterSelectBgSpineStillAssetKey);
        Assert.Equal("model://characters/ironclad/mapMarker", character.MapMarkerAssetKey);
        Assert.Equal("res://images/characters/ironclad/icon.png", character.IconPath);
        Assert.Equal("res://images/characters/ironclad/icon_outline.png", character.IconOutlinePath);
        Assert.Equal("res://scenes/screens/char_select/char_select_bg_ironclad.tscn", character.CharacterSelectBgPath);
        Assert.Equal("res://images/characters/ironclad/map_marker.png", character.MapMarkerPath);
    }

    [Fact]
    public void ProtocolAdapterMapsModelCatalogLocalizationRefs()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new LocRefModelCatalogProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-loc-ref-1",
            Family = "relics",
            Ids = { "burning-blood" },
        });

        var relic = Assert.Single(response.Success.Models).Relic;
        Assert.Equal("", response.Success.Language);
        Assert.Equal("", relic.Title);
        Assert.Equal("relics", relic.TitleLoc.Table);
        Assert.Equal("EmberHeart.title", relic.TitleLoc.Key);
        Assert.Equal("relics", relic.DescriptionLoc.Table);
        Assert.Equal("EmberHeart.description", relic.DescriptionLoc.Key);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void LiveModelCatalogProviderRejectsUnsupportedLanguageBeforeLoadingModels()
    {
        var provider = new Sts2ModelCatalogProvider();

        var result = provider.GetModels(new ModelCatalogRequestSnapshot("characters", [], "not-a-language"));

        Assert.NotNull(result.Error);
        Assert.Equal("unsupported-language", result.Error.Code);
        Assert.Equal(ModelCatalogStatus.Unavailable, result.Status);
        Assert.Contains(result.Notices, notice =>
            notice.Code == "unsupported-language"
            && notice.Path == "language");
    }
#endif

    [Fact]
    public void ProtocolAdapterMapsRelicModelCatalogResourcePaths()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new FixedModelCatalogProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-relics-1",
            Family = "relics",
            Ids = { "burning-blood" },
        });

        var success = Assert.IsType<Spirectl.Proto.V0.ModelCatalogResponse>(response.ResultCase switch
        {
            Spirectl.Proto.V0.ModelCatalogResult.ResultOneofCase.Success => response.Success,
            _ => null,
        });
        var relic = Assert.Single(success.Models).Relic;
        Assert.Equal("EmberHeart", relic.Id);
        Assert.Equal("model://relics/burning-blood/icon", relic.IconAssetKey);
        Assert.Equal("model://relics/burning-blood/iconOutline", relic.IconOutlineAssetKey);
        Assert.Equal("model://relics/burning-blood/bigIcon", relic.BigIconAssetKey);
        Assert.Equal("res://images/relics/burning_blood.png", relic.IconPath);
        Assert.Equal("res://images/relics/burning_blood_outline.png", relic.IconOutlinePath);
        Assert.Equal("res://images/relics/burning_blood_big.png", relic.BigIconPath);
    }

    [Fact]
    public void ProtocolAdapterMapsExpandedModelCatalogFamilies()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new FixedModelCatalogProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var card = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-cards-1",
            Family = "cards",
            Ids = { "strike-ironclad" },
        }).Success.Models).Card;
        Assert.Equal("StrikeIronclad", card.Id);
        Assert.Equal("model://cards/strike-ironclad/image", card.ImageAssetKey);
        Assert.Equal("Attack", card.Type);
        Assert.True(card.Upgradable);
        Assert.Equal(6, card.DynamicVars["damage"]);
        Assert.Equal(9, card.Upgrade.DynamicVars["damage"]);

        var potion = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-potions-1",
            Family = "potions",
            Ids = { "fire-potion" },
        }).Success.Models).Potion;
        Assert.Equal("FirePotion", potion.Id);
        Assert.Equal("model://potions/fire-potion/icon", potion.IconAssetKey);

        var ancient = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-ancients-1",
            Family = "ancients",
            Ids = { "neow" },
        }).Success.Models).Event;
        Assert.Equal("Neow", ancient.Id);
        Assert.Equal("ancient", ancient.Kind);
        Assert.Equal("model://events/neow/mapIcon", ancient.MapIconAssetKey);

        var act = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-acts-1",
            Family = "acts",
            Ids = { "overgrowth" },
        }).Success.Models).Act;
        Assert.Equal("Overgrowth", act.Id);
        Assert.Equal("model://acts/overgrowth/backgroundScene", act.BackgroundSceneAssetKey);
    }

    [Fact]
    public void ProtocolAdapterMapsAdditionalModelCatalogFamilies()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            modelCatalogProvider: new AdditionalModelCatalogProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var monster = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-monsters-1",
            Family = "monsters",
            Ids = { "jaw-worm" },
        }).Success.Models).Monster;
        Assert.Equal("JawWorm", monster.Id);
        Assert.Equal("model://monsters/jaw-worm/visuals", monster.VisualsAssetKey);
        Assert.Equal(44, monster.MaxInitialHp);

        var encounter = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-encounters-1",
            Family = "encounters",
            Ids = { "jaw-worm-weak" },
        }).Success.Models).Encounter;
        Assert.Equal("JawWormWeak", encounter.Id);
        Assert.Equal("JawWorm", Assert.Single(encounter.MonsterIds));
        Assert.Equal("M", Assert.Single(encounter.MonstersWithSlots).Slot);

        var power = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-powers-1",
            Family = "powers",
            Ids = { "strength" },
        }).Success.Models).Power;
        Assert.Equal("Resolve", power.Id);
        Assert.Equal("Buff", power.Type);

        var orb = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-orbs-1",
            Family = "orbs",
            Ids = { "lightning" },
        }).Success.Models).Orb;
        Assert.Equal("Lightning", orb.Id);
        Assert.Equal("model://orbs/lightning/sprite", orb.SpriteAssetKey);

        var affliction = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-afflictions-1",
            Family = "afflictions",
            Ids = { "hexed" },
        }).Success.Models).Affliction;
        Assert.Equal("Hexed", affliction.Id);
        Assert.Equal("model://afflictions/hexed/overlay", affliction.OverlayAssetKey);

        var enchantment = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-enchantments-1",
            Family = "enchantments",
            Ids = { "innate" },
        }).Success.Models).Enchantment;
        Assert.Equal("Innate", enchantment.Id);
        Assert.True(enchantment.PreviewOutsideOfCombat);

        var pool = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-card-pools-1",
            Family = "card-pools",
            Ids = { "ironclad-card-pool" },
        }).Success.Models).CardPool;
        Assert.Equal("IroncladCardPool", pool.Id);
        Assert.Equal("StrikeIronclad", Assert.Single(pool.CardIds));

        var relicPool = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-relic-pools-1",
            Family = "relic-pools",
            Ids = { "ironclad-relic-pool" },
        }).Success.Models).RelicPool;
        Assert.Equal("IroncladRelicPool", relicPool.Id);
        Assert.Equal("EmberHeart", Assert.Single(relicPool.RelicIds));

        var potionPool = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-potion-pools-1",
            Family = "potion-pools",
            Ids = { "shared-potion-pool" },
        }).Success.Models).PotionPool;
        Assert.Equal("SharedPotionPool", potionPool.Id);
        Assert.Equal("FirePotion", Assert.Single(potionPool.PotionIds));

        var modifier = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-modifiers-1",
            Family = "modifiers",
            Ids = { "big-game-hunter" },
        }).Success.Models).Modifier;
        Assert.Equal("BigGameHunter", modifier.Id);
        Assert.Equal("good", modifier.Polarity);

        var achievement = Assert.Single(adapter.HandleGetModels(new Spirectl.Proto.V0.ModelCatalogRequest
        {
            RequestId = "models-achievements-1",
            Family = "achievements",
            Ids = { "first-win" },
        }).Success.Models).Achievement;
        Assert.Equal("FirstWin", achievement.Id);
        Assert.Equal("MegaCrit.Sts2.Core.Models.Achievements.FirstWin", achievement.TypeName);
    }


    [Fact]
    public void DispatcherLivenessClassifiesFocusedRecentDrainAsRunning()
    {
        var now = DateTimeOffset.UtcNow;

        var liveness = MainThreadDispatcherStatus.ClassifyLiveness(
            hasCapturedContext: true,
            isApplicationFocused: true,
            lastQueueDrainAtUtc: now - TimeSpan.FromMilliseconds(100),
            queueDepth: 1,
            now,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal("running", liveness);
    }

    [Fact]
    public void DispatcherLivenessClassifiesUnfocusedAsBackgrounded()
    {
        var now = DateTimeOffset.UtcNow;

        var liveness = MainThreadDispatcherStatus.ClassifyLiveness(
            hasCapturedContext: true,
            isApplicationFocused: false,
            lastQueueDrainAtUtc: now,
            queueDepth: 0,
            now,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal("backgrounded", liveness);
    }

    [Fact]
    public void DispatcherLivenessClassifiesUnknownFocusStaleDrainWithQueuedWorkAsStalled()
    {
        var now = DateTimeOffset.UtcNow;

        var liveness = MainThreadDispatcherStatus.ClassifyLiveness(
            hasCapturedContext: true,
            isApplicationFocused: null,
            lastQueueDrainAtUtc: now - TimeSpan.FromSeconds(1),
            queueDepth: 1,
            now,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal("stalled", liveness);
    }

    [Fact]
    public void GetCurrentStateFailureReturnsStructuredErrorAndHealth()
    {
        // The observation comes from the state PROVIDER, so that is what has to throw to exercise this path.
        // The extractor is left throwing as a negative control: if it were still on the observation path the
        // message assertion below would read "state extractor failed" instead.
        var runtime = FacadeWith(
            stateExtractor: new ThrowingStateExtractor(),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: new ThrowingStateProvider());

        var result = runtime.GetCurrentState(new CurrentStateRequest());

        Assert.False(result.Success);
        Assert.Null(result.State);
        Assert.NotNull(result.Error);
        Assert.Equal("runtime-state-failed", result.Error.Code);
        Assert.Contains("state provider failed", result.Error.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Health);
        Assert.Equal(Environment.CurrentManagedThreadId, result.Health.CurrentThreadId);
    }

    [Fact]
    public void GetCurrentStateFailureClassifiesBackgroundedDispatcherAsRetryable()
    {
        var context = new TestDispatcherContext(
            queueDepth: 0,
            lastDrainAtUtc: DateTimeOffset.UtcNow,
            isApplicationFocused: false);
        Sts2MainThreadDispatcher.Capture(context);
        try
        {
            var runtime = FacadeWith(
                stateExtractor: new ThrowingStateExtractor(),
                actionHandler: new PlaceholderActionHandler(),
                assetExtractProvider: new ThrowingAssetExtractProvider());

            var result = runtime.GetCurrentState(new CurrentStateRequest());

            Assert.False(result.Success);
            Assert.Null(result.State);
            Assert.Equal("current-observation-backgrounded", result.Error?.Code);
            Assert.True(result.Error?.Retryable);
            Assert.Equal("dispatcherLiveness", result.Error?.Field);
            Assert.Equal("backgrounded", result.Error?.Value);
            Assert.False(result.Health?.Healthy);
            Assert.True(result.Health?.RetryAllowed);
            Assert.Equal("retryable", result.Health?.Status);
            Assert.Equal("backgrounded", result.Health?.DispatcherLiveness);
            Assert.False(result.Health?.IsApplicationFocused);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
        }
    }

    [Fact]
    public void GetCurrentStateFailureClassifiesStalledDispatcherAsRetryable()
    {
        var context = new TestDispatcherContext(
            queueDepth: 1,
            lastDrainAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2),
            isApplicationFocused: true);
        Sts2MainThreadDispatcher.Capture(context);
        try
        {
            var runtime = FacadeWith(
                stateExtractor: new NullStateExtractor(),
                actionHandler: new PlaceholderActionHandler(),
                assetExtractProvider: new ThrowingAssetExtractProvider());

            var result = runtime.GetCurrentState(new CurrentStateRequest());

            Assert.False(result.Success);
            Assert.Null(result.State);
            Assert.Equal("current-observation-dispatcher-stalled", result.Error?.Code);
            Assert.True(result.Error?.Retryable);
            Assert.Equal("dispatcherLiveness", result.Error?.Field);
            Assert.Equal("stalled", result.Error?.Value);
            Assert.False(result.Health?.Healthy);
            Assert.True(result.Health?.RetryAllowed);
            Assert.Equal("retryable", result.Health?.Status);
            Assert.Equal("stalled", result.Health?.DispatcherLiveness);
            Assert.True(result.Health?.IsApplicationFocused);
            Assert.Equal(1, result.Health?.DispatcherQueueDepth);
        }
        finally
        {
            Sts2MainThreadDispatcher.ResetForTests();
        }
    }

    [Fact]
    public void GetCurrentStateNullSnapshotReturnsStateUnavailableWithHealth()
    {
        var runtime = FacadeWith(
            stateExtractor: new NullStateExtractor(),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: new NullStateProvider());

        var result = runtime.GetCurrentState(new CurrentStateRequest());

        Assert.False(result.Success);
        Assert.Null(result.State);
        Assert.Equal("state-unavailable", result.Error?.Code);
        Assert.NotNull(result.Health);
    }

    [Fact]
    public void GetCurrentStateWithoutAStateProviderReportsUnavailableInsteadOfAMainMenu()
    {
        // A runtime composed without a state provider cannot observe anything. It used to answer with a
        // FABRICATED snapshot — root scene "screens/main_menu", no character select, no run — which is
        // byte-identical to what a live provider reports at a real title screen, so a composition failure was
        // indistinguishable from an idle game. Consumers concluded "not in a lobby, not in a run" for ever.
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider());

        var result = runtime.GetCurrentState(new CurrentStateRequest());

        Assert.False(result.Success);
        Assert.Null(result.State);
        Assert.Equal("state-unavailable", result.Error?.Code);
        Assert.NotNull(result.Health);
    }


    [Fact]
    public async Task SubscribeCurrentStateEmitsInitialAndChangedEventsFromDispatcherTicks()
    {
        // The watch hub dedups on the observed snapshot's fingerprint, which varies with its Language, so
        // driving the provider's Language is enough to exercise the initial/changed path.
        var provider = new MutableStateProvider(MainMenuStateSnapshot("eng"));
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshotWithActions()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: provider);
        var events = new List<CurrentStateWatchEvent>();
        var initial = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = runtime.SubscribeCurrentState(
            new CurrentStateSubscriptionRequest(MinCaptureInterval: TimeSpan.Zero),
            evt =>
            {
                lock (events)
                {
                    events.Add(evt);
                }

                if (evt.Type == CurrentStateWatchEventType.Initial)
                {
                    initial.TrySetResult(evt);
                }
                else if (evt.Type == CurrentStateWatchEventType.Changed)
                {
                    changed.TrySetResult(evt);
                }
            });

        var initialEvent = await initial.Task.WaitAsync(TimeSpan.FromSeconds(1));
        provider.Snapshot = provider.Snapshot with { Language = "esp" };
        Sts2MainThreadDispatcher.NotifyMainThreadTick();
        var changedEvent = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CurrentStateWatchEventType.Initial, initialEvent.Type);
        Assert.Equal("eng", initialEvent.State?.Language);
        Assert.Equal(1UL, initialEvent.Sequence);
        Assert.Equal(1UL, initialEvent.SemanticRevision);
        Assert.Equal(CurrentStateWatchEventType.Changed, changedEvent.Type);
        Assert.Equal("esp", changedEvent.State?.Language);
        Assert.Equal(2UL, changedEvent.Sequence);
        Assert.NotEqual(initialEvent.SemanticFingerprint, changedEvent.SemanticFingerprint);

        subscription.Dispose();
        provider.Snapshot = provider.Snapshot with { Language = "fra" };
        Sts2MainThreadDispatcher.NotifyMainThreadTick();
        await Task.Delay(100);
        lock (events)
        {
            Assert.Equal(2, events.Count);
        }
    }

    [Fact]
    public async Task SubscribeCurrentStatePassesRequestedPerspectiveToObservation()
    {
        // The watch capture must observe through the subscription's perspective, matching the one-shot
        // GetCurrentState path. The state provider is the only thing that observes, so assert the requested
        // perspective reaches IT — resolved, not the raw selection.
        var provider = new MutableStateProvider(MainMenuStateSnapshot());
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: provider);
        var initial = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = runtime.SubscribeCurrentState(
            new CurrentStateSubscriptionRequest(
                Perspective: new PerspectiveSelection(PlayerScope.Local, "p2"),
                MinCaptureInterval: TimeSpan.Zero),
            evt =>
            {
                if (evt.Type == CurrentStateWatchEventType.Initial)
                {
                    initial.TrySetResult(evt);
                }
            });

        await initial.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("p2", provider.LastPerspective?.PlayerId);
        Assert.Equal(PlayerScope.Local, provider.LastPerspective?.Scope);
        Assert.False(provider.LastPerspective?.UsesDefault);
    }

    [Fact]
    public async Task WatchCurrentStateAsyncYieldsSubscriptionEvents()
    {
        var provider = new MutableStateProvider(MainMenuStateSnapshot("eng"));
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshotWithActions()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = runtime
            .WatchCurrentStateAsync(
                new CurrentStateSubscriptionRequest(MinCaptureInterval: TimeSpan.Zero),
                cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await watcher.MoveNextAsync());
        var initial = watcher.Current;
        provider.Snapshot = provider.Snapshot with { Language = "esp" };
        Sts2MainThreadDispatcher.NotifyMainThreadTick();
        Assert.True(await watcher.MoveNextAsync());
        var changed = watcher.Current;

        Assert.Equal(CurrentStateWatchEventType.Initial, initial.Type);
        Assert.Equal("eng", initial.State?.Language);
        Assert.Equal(CurrentStateWatchEventType.Changed, changed.Type);
        Assert.Equal("esp", changed.State?.Language);
    }

    [Fact]
    public async Task SubscribeCurrentStateCanSuppressInitialButStillBaselineChanges()
    {
        // The observed snapshot's fingerprint varies with its Language, so driving the provider's Language
        // exercises the baseline-change path without an initial emission.
        var provider = new MutableStateProvider(MainMenuStateSnapshot("eng"));
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshotWithActions()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: provider);
        var changed = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = runtime.SubscribeCurrentState(
            new CurrentStateSubscriptionRequest(EmitInitial: false, MinCaptureInterval: TimeSpan.Zero),
            evt => changed.TrySetResult(evt));

        await WaitForConditionAsync(() => provider.ObserveCount > 0, TimeSpan.FromSeconds(1));
        provider.Snapshot = provider.Snapshot with { Language = "esp" };
        Sts2MainThreadDispatcher.NotifyMainThreadTick();
        var changedEvent = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CurrentStateWatchEventType.Changed, changedEvent.Type);
        Assert.NotNull(changedEvent.State);
        Assert.Equal(1UL, changedEvent.Sequence);
        Assert.Equal(1UL, changedEvent.SemanticRevision);
    }

    [Fact]
    public async Task SubscribeCurrentStateRefreshesAfterAcceptedActionsOnly()
    {
        // The observed snapshot's fingerprint varies with its Language, so driving the provider's Language
        // exercises the post-action refresh path.
        var provider = new MutableStateProvider(MainMenuStateSnapshot("eng"));
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshotWithActions()),
            actionHandler: new CapturingActionHandler(),
            assetExtractProvider: new ThrowingAssetExtractProvider(),
            stateProvider: provider);
        var initial = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<CurrentStateWatchEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = runtime.SubscribeCurrentState(
            new CurrentStateSubscriptionRequest(MinCaptureInterval: TimeSpan.Zero),
            evt =>
            {
                if (evt.Type == CurrentStateWatchEventType.Initial)
                {
                    initial.TrySetResult(evt);
                }
                else if (evt.Type == CurrentStateWatchEventType.Changed)
                {
                    changed.TrySetResult(evt);
                }
            });

        await initial.Task.WaitAsync(TimeSpan.FromSeconds(1));
        provider.Snapshot = provider.Snapshot with { Language = "esp" };
        var failed = runtime.ExecuteAction(new EmbeddableActionRequest("invalid", SemanticActionKind.Choose));
        await Task.Delay(100);
        Assert.False(failed.Success);
        Assert.False(changed.Task.IsCompleted);

        var accepted = runtime.ExecuteAction(new EmbeddableActionRequest(
            "valid",
            SemanticActionKind.Choose,
            ChoiceId: "choice:start-run"));
        var changedEvent = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(accepted.Success);
        Assert.NotNull(changedEvent.State);
    }


#if false


#endif











#if false
#endif



    [Fact]
    public void ExecuteActionConvertsChooseRequestToSemanticAction()
    {
        var actionHandler = new CapturingActionHandler();
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: actionHandler,
            assetExtractProvider: new PlaceholderAssetExtractProvider());

        var result = runtime.ExecuteAction(new EmbeddableActionRequest(
            "act-choose",
            SemanticActionKind.Choose,
            ChoiceId: "choice:start-run"));

        Assert.True(result.Success);
        Assert.NotNull(result.Result);
        Assert.Equal(SemanticActionKind.Choose, actionHandler.LastRequest?.Kind);
        Assert.Equal("choice:start-run", actionHandler.LastRequest?.ChoiceId);
        Assert.Equal(SemanticActionKind.Choose, result.Result.Kind);
    }

    [Fact]
    public void ExecuteActionReturnsInvalidActionForMissingPayload()
    {
        var runtime = EmbeddedRuntimeTestFactory.Create();

        var result = runtime.ExecuteAction(new EmbeddableActionRequest("act-invalid", SemanticActionKind.Choose));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("InvalidAction", result.Error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetProviderResolvesSceneKeyAndMapsPlaceholderRuntimeToUnsupported()
    {
        // Consumers go through the canonical Assets.GetAsset(key) entrypoint (the runtime
        // resolves the opaque key into the low-level extraction request); there is no
        // public ExtractAsset on the embeddable contract.
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new FixedAssetExtractProvider());

        var result = runtime.Assets.GetAsset(new EmbeddableAssetRequest(
            "res://ui/shared/HandPanel.tscn", "png", "asset-fixed"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("asset-fixed", result.Payload!.RequestId);
        Assert.Equal("png", result.Payload.Format);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, result.Payload.Contents);
        Assert.Equal("res://ui/shared/HandPanel.tscn", result.Payload.Provenance.LoadPath);

        // A runtime without a live asset provider surfaces a structured consumer error.
        var placeholder = EmbeddedRuntimeTestFactory.Create().Assets;
        var unsupported = placeholder.GetAsset(new EmbeddableAssetRequest(
            "res://ui/shared/HandPanel.tscn", "png", "asset-placeholder"));

        Assert.False(unsupported.Success);
        Assert.NotNull(unsupported.Error);
        Assert.Equal("unsupported-asset-provider", unsupported.Error!.Code);
    }

    [Fact]
    public void EmbeddableAssetProviderMapsSuccessPayloadAndBatchStatus()
    {
        var runtime = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new FixedAssetExtractProvider());

        var single = runtime.Assets.GetAsset(new EmbeddableAssetRequest("composed://combat-background/overgrowth/image", "png", "asset-provider-1"));
        Assert.True(single.Success);
        Assert.NotNull(single.Payload);
        Assert.Equal("raster", single.Payload!.ArtifactKind);
        Assert.Equal("image/png", single.Payload!.ContentType);
        Assert.Equal("composed://combat-background/overgrowth/image", single.Payload.Key);
        Assert.Equal("composed://combat-background/overgrowth/image", single.Payload.Provenance.LoadPath);

        var batch = runtime.Assets.GetAssets(new EmbeddableAssetBatchRequest([
            new EmbeddableAssetRequest("composed://combat-background/overgrowth/image", "png", "ok-1"),
            new EmbeddableAssetRequest(" ", "png", "bad-1")
        ]));

        Assert.Equal("partial", batch.Status);
        Assert.Equal(2, batch.Results.Count);
        Assert.True(batch.Results[0].Success);
        Assert.False(batch.Results[1].Success);
    }

    [Fact]
    public void EmbeddableAssetProviderResolvesEventBackgroundAliasAndFailFastBatchStopsEarly()
    {
        var capturingProvider = new CapturingAssetExtractProvider();
        var provider = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: capturingProvider).Assets;

        var single = provider.GetAsset(new EmbeddableAssetRequest("res://scenes/events/background_scenes/the_city.tscn", "png", "evt-1"));
        Assert.True(single.Success);

        Assert.Equal("res://scenes/events/background_scenes/the_city.tscn", capturingProvider.Requests[0].LoadPath);

        var batch = provider.GetAssets(new EmbeddableAssetBatchRequest([
            new EmbeddableAssetRequest(" ", "png", "bad-early"),
            new EmbeddableAssetRequest("combat-background:overgrowth", "png", "never-runs")
        ], FailFast: true));

        Assert.Equal("failed", batch.Status);
        Assert.Equal(2, batch.Results.Count);
        Assert.False(batch.Results[0].Success);
        Assert.False(batch.Results[1].Success);
        Assert.Equal("asset-request-skipped", batch.Results[1].Error?.Code);
    }

    [Fact]
    public void EmbeddableAssetProviderTimelinePayloadHasManifestAndPreservesFrames()
    {
        var provider = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new FixedTimelineExtractProvider()).Assets;

        var result = provider.GetAsset(new EmbeddableAssetRequest("composed://combat-background/overgrowth/image", "auto", "tl-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("timeline", result.Payload!.ArtifactKind);
        Assert.NotEmpty(result.Payload.Contents);
        Assert.Equal("application/json", result.Payload.ContentType);
        Assert.Single(result.Payload.Frames);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Payload.Frames[0].Contents);
    }

    [Fact]
    public void EmbeddableAssetProviderPreservesFontPayloadMetadata()
    {
        var provider = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new FixedFontExtractProvider()).Assets;

        var result = provider.GetAsset(new EmbeddableAssetRequest("res://fonts/title.woff2", "auto", "font-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("font", result.Payload!.ArtifactKind);
        Assert.Equal("woff2", result.Payload.Format);
        Assert.Equal("font/woff2", result.Payload.ContentType);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, result.Payload.Contents);
        Assert.Equal("res://fonts/title.woff2", result.Payload.Provenance.LoadPath);
    }

    [Fact]
    public void EmbeddableAssetProviderResKeyUsesResourcesSourceRoot()
    {
        var capturingProvider = new CapturingAssetExtractProvider();
        var provider = FacadeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: capturingProvider).Assets;

        var result = provider.GetAsset(new EmbeddableAssetRequest("res://ui/shared/HandPanel.tscn", "png", "res-1"));
        Assert.True(result.Success);
        Assert.Equal("resources", capturingProvider.Requests[0].SourceRoot);
        Assert.Equal("resources", result.Payload!.Provenance.SourceRoot);
    }

    // The facade deliberately no longer accepts BridgeRuntime. These test seams compose only the
    // ten embedded ports, so a bridge-only service cannot slip back into the shared constructor.
    private static SpirectlRuntimeFacade FacadeWith(
        IGameStateExtractor stateExtractor,
        IActionHandler actionHandler,
        IAssetExtractProvider assetExtractProvider,
        IModelCatalogProvider? modelCatalogProvider = null,
        IReferenceDataProvider? referenceDataProvider = null,
        IStateProvider? stateProvider = null)
    {
        return SpirectlRuntimeFacade.FromFactory(
            stateExtractor,
            actionHandler,
            new InMemoryLogStream(),
            new DefaultPerspectiveProvider(),
            assetExtractProvider,
            assetExtractProvider,
            assetExtractProvider,
            assetExtractProvider,
            assetExtractProvider,
            modelCatalogProvider ?? new PlaceholderModelCatalogProvider(),
            referenceDataProvider ?? new PlaceholderReferenceDataProvider(),
            new PlaceholderRuntimeSceneWatcher(),
            // Left null by default: a facade with no state provider is a composition that cannot observe the
            // game, and GetCurrentState must say so rather than invent an answer. Tests that need an observation
            // pass one explicitly.
            stateProvider);
    }

    private static BridgeRuntime RuntimeWith(
        IGameStateExtractor stateExtractor,
        IActionHandler actionHandler,
        IAssetExtractProvider assetExtractProvider,
        IScreenshotProvider? screenshotProvider = null,
        IModelCatalogProvider? modelCatalogProvider = null,
        IReferenceDataProvider? referenceDataProvider = null)
        => BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = stateExtractor,
            ActionHandler = actionHandler,
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            FixtureLoader = new PlaceholderFixtureLoader(),
            ScreenshotProvider = screenshotProvider ?? new PlaceholderScreenshotProvider(),
            AssetExtractProvider = assetExtractProvider,
            DebugControl = new PlaceholderDebugControl(),
            RuntimeSceneProvider = new PlaceholderRuntimeSceneProvider(),
            LifecycleControl = new PlaceholderLifecycleControl(),
            ScenarioProvider = new PlaceholderScenarioProvider(),
            RecordedFixtureProvider = new PlaceholderRecordedFixtureProvider(),
            BridgeHost = new FixedBridgeHost(new BridgeHostStatus("embedded", true, "Embeddable runtime facade is available in-process.")),
            ModelCatalogProvider = modelCatalogProvider,
            ReferenceDataProvider = referenceDataProvider,
        });

    /// <summary>A live provider's reading of a main menu — the observation the facade now only ever RELAYS.</summary>
    private static StateSnapshot MainMenuStateSnapshot(string? language = "eng")
        => new(StateSnapshot.CurrentSchemaVersion, language, "screens/main_menu", null, null);

    private static GameStateSnapshot MainMenuSnapshot()
        => new(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-live",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "main-menu",
            ScreenTitle: "Main Menu",
            ScreenInstanceId: "screen:main-menu:live",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
            Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
            Lobby: null,
            Run: null,
            Combat: null,
            Choices: [],
            AvailableActions: [],
            Notices: [],
            Debug: null);

    private static GameStateSnapshot MainMenuSnapshotWithActions()
        => MainMenuSnapshot() with
        {
            Choices =
            [
                new ChoiceSnapshot("menu:start-run", "Start Run", "menu", Provisional: false, PreferredAction: "choose"),
            ],
            AvailableActions =
            [
                new AvailableActionSnapshot(
                    "action:menu:start-run",
                    SemanticActionKind.Choose,
                    "Choose the visible Start Run option.",
                    "sts2 act choose --choice menu:start-run",
                    Provisional: false,
                    new ActionArgumentsSnapshot(null, null, null, "menu:start-run", null, null)),
            ],
        };

    private static GameStateSnapshot MultiplayerLobbySnapshot()
    {
        var host = new LobbyPlayerSnapshot("p1", "selecting", "Ironclad", "ironclad", false, 0, IsLocal: true, IsHost: true, IsRemote: false);
        var remote = new LobbyPlayerSnapshot("p2", "selecting", "Silent", null, false, 1, IsLocal: false, IsHost: false, IsRemote: true);
        var ironclad = new LobbyCharacterSnapshot(
            "ironclad",
            "Ironclad",
            true,
            PortraitAssetKey: "model://characters/ironclad/characterSelectIcon",
            SelectBackgroundAssetKey: "res://scenes/screens/char_select/char_select_bg_ironclad.tscn");
        var silent = new LobbyCharacterSnapshot(
            "silent",
            "Silent",
            true,
            PortraitAssetKey: "model://characters/silent/characterSelectIcon",
            SelectBackgroundAssetKey: "res://scenes/screens/char_select/char_select_bg_silent.tscn");
        return MainMenuSnapshot() with
        {
            ScreenType = "Screens.CharacterSelect.NCharacterSelectScreen",
            ScreenTitle = "Start Run",
            ScreenInstanceId = "screen:Screens.CharacterSelect.NCharacterSelectScreen:live",
            Menu = null,
            ResolvedPerspective = new PlayerPerspective(PlayerScope.Local, "p1", UsesDefault: false),
            Lobby = new LobbyStateSnapshot(
                "start-run",
                "selecting",
                [host, remote],
                [ironclad, silent],
                LocalPlayerId: "p1",
                HostPlayerId: "p1",
                LocalPlayerRole: "host",
                PlayersById: new Dictionary<string, LobbyPlayerSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = host,
                    ["p2"] = remote,
                },
                AvailableCharactersById: new Dictionary<string, LobbyCharacterSnapshot>(StringComparer.Ordinal)
                {
                    ["ironclad"] = ironclad,
                    ["silent"] = silent,
                }),
            MultiplayerLobby = new MultiplayerLobbyStateSnapshot(
                [
                    new VisibleItemStateSnapshot("lobby:player:p1", "Ironclad", OwnerPlayerId: "p1", PlayerId: "p1", IsLocal: true, IsHost: true),
                    new VisibleItemStateSnapshot("lobby:player:p2", "Silent", OwnerPlayerId: "p2", PlayerId: "p2", IsRemote: true),
                ],
                [
                    new VisibleActionReferenceSnapshot(
                        "action:lobby:p1:ready",
                        "Ready",
                        true,
                        OwnerPlayerId: "p1",
                        ActionKind: SemanticActionKind.Ready,
                        Arguments: new ActionArgumentsSnapshot("p1", null, null, null, null, null)),
                    new VisibleActionReferenceSnapshot(
                        "action:lobby:p1:unready",
                        "Unready",
                        true,
                        OwnerPlayerId: "p1",
                        ActionKind: SemanticActionKind.Unready,
                        Arguments: new ActionArgumentsSnapshot("p1", null, null, null, null, null)),
                    new VisibleActionReferenceSnapshot(
                        "action:lobby:p1:select-silent",
                        "Select Silent",
                        true,
                        OwnerPlayerId: "p1",
                        ActionKind: SemanticActionKind.SelectCharacter,
                        Arguments: new ActionArgumentsSnapshot("p1", null, null, null, "silent", null)),
                ],
                LocalPlayerId: "p1",
                HostPlayerId: "p1",
                Perspective: "local:p1",
                IsLocal: true,
                IsHost: true),
            Choices =
            [
                new ChoiceSnapshot("lobby:character:silent", "Silent", "lobby-character", Provisional: false, OwnerPlayerId: "p1", PreferredAction: "select-character"),
            ],
            AvailableActions =
            [
                new AvailableActionSnapshot(
                    "action:lobby:p1:ready",
                    SemanticActionKind.Ready,
                    "Ready Ironclad.",
                    "sts2 act ready --player-id p1",
                    Provisional: false,
                    new ActionArgumentsSnapshot("p1", null, null, null, null, null),
                    OwnerPlayerId: "p1"),
                new AvailableActionSnapshot(
                    "action:lobby:p1:unready",
                    SemanticActionKind.Unready,
                    "Unready Ironclad.",
                    "sts2 act unready --player-id p1",
                    Provisional: false,
                    new ActionArgumentsSnapshot("p1", null, null, null, null, null),
                    OwnerPlayerId: "p1"),
                new AvailableActionSnapshot(
                    "action:lobby:p1:select-silent",
                    SemanticActionKind.SelectCharacter,
                    "Select Silent.",
                    "sts2 act select-character --character silent --player-id p1",
                    Provisional: false,
                    new ActionArgumentsSnapshot("p1", null, null, null, "silent", null),
                    OwnerPlayerId: "p1"),
            ],
        };
    }

    private static GameStateSnapshot MultiplayerLobbySnapshotWithGeometry()
    {
        var state = MultiplayerLobbySnapshot();
        var lobby = state.Lobby!;
        var rects = new Dictionary<string, LobbyPresentationRectSnapshot>(StringComparer.Ordinal)
        {
            ["background"] = LobbyRect("background", 0, 0, 1280, 720),
            ["character-list"] = LobbyRect("character-list", 100, 400, 170, 90) with
            {
                NodeType = "Godot.HBoxContainer",
                Anchors = new LobbyPresentationAnchorsSnapshot(0.5, 0.5, 0.5, 0.5),
                ObservedGapX = 10,
            },
            ["remote-player-container"] = LobbyRect("remote-player-container", 36, 45, 518, 354) with
            {
                NodeType = "MegaCrit.Sts2.Core.Nodes.Multiplayer.NRemoteLobbyPlayerContainer",
            },
            ["remote-player-list"] = LobbyRect("remote-player-list", 36, 45, 518, 354) with
            {
                NodeType = "Godot.FlowContainer",
            },
            ["status:solo-message"] = LobbyRect("status:solo-message", 36, 44, 393, 39.3958) with
            {
                NodeType = "MegaCrit.Sts2.addons.mega_text.MegaLabel",
            },
            ["selected-character"] = LobbyRect("selected-character", 700, 120, 220, 320),
            ["selected-character:stats-panel"] = LobbyRect("selected-character:stats-panel", 520, 240, 180, 120),
            ["player:p1"] = LobbyRect("player:p1", 40, 80, 170, 56),
            ["player:p2"] = LobbyRect("player:p2", 40, 150, 170, 56),
            ["character:ironclad"] = LobbyRect("character:ironclad", 100, 400, 80, 90),
            ["character:ironclad:portrait"] = LobbyRect("character:ironclad:portrait", 110, 410, 60, 55),
            ["character:ironclad:player-marker:p1"] = LobbyRect("character:ironclad:player-marker:p1", 125, 474, 24, 24),
            ["character:silent"] = LobbyRect("character:silent", 190, 400, 80, 90),
            ["character:silent:portrait"] = LobbyRect("character:silent:portrait", 200, 410, 60, 55),
            ["control:ready"] = LobbyRect("control:ready", 840, 490, 120, 52),
            ["control:unready"] = LobbyRect("control:unready", 840, 490, 120, 52),
            ["control:back"] = LobbyRect("control:back", 30, 490, 110, 52),
        };
        rects["character-list"] = rects["character-list"] with
        {
            Children =
            [
                LobbyChild("character:ironclad", "ironclad", rects["character:ironclad"], 0),
                LobbyChild("character:silent", "silent", rects["character:silent"], 1),
            ],
        };
        rects["remote-player-container"] = rects["remote-player-container"] with
        {
            Children =
            [
                LobbyChild("status:solo-message", null, rects["status:solo-message"], 0),
                LobbyChild("player:p1", "p1", rects["player:p1"], 1),
                LobbyChild("player:p2", "p2", rects["player:p2"], 2),
            ],
        };
        rects["remote-player-list"] = rects["remote-player-list"] with
        {
            Children =
            [
                LobbyChild("status:solo-message", null, rects["status:solo-message"], 0),
                LobbyChild("player:p1", "p1", rects["player:p1"], 1),
                LobbyChild("player:p2", "p2", rects["player:p2"], 2),
            ],
        };
        return state with
        {
            Lobby = lobby with
            {
                PresentationGeometry = new LobbyPresentationGeometrySnapshot(rects, []),
            },
        };
    }

    private static LobbyPresentationRectSnapshot LobbyRect(
        string key,
        double x,
        double y,
        double width,
        double height)
        => new(
            key,
            new PresentationRectSnapshot(x, y, width, height),
            "EmbeddableRuntimeFacadeTests",
            $"/root/{key}",
            key,
            [key],
            key,
            "live-control");

    private static LobbyPresentationChildLayoutSnapshot LobbyChild(
        string key,
        string? stateId,
        LobbyPresentationRectSnapshot rect,
        uint order)
        => new(key, stateId, rect.NodePath, rect.Rect, order, new Dictionary<string, string>());

    private static GameStateSnapshot CombatSnapshot()
    {
        var p1Run = new PlayerStateSnapshot("p1", "ironclad", 70, 80, IsLocal: true, IsHost: true);
        var p2Run = new PlayerStateSnapshot("p2", "silent", 64, 70, IsHostLocalSeat: true);
        var p1Combat = new CombatPlayerStateSnapshot("p1", "ironclad", 70, 80, 3, 2, 3, [], IsLocal: true, IsHost: true);
        var p2Card = new CardStateSnapshot("c_p2_1", "Jab", 1, "p2", true, null, ["e_1"], false, ModelId: "strike_r");
        var p2Combat = new CombatPlayerStateSnapshot("p2", "silent", 64, 70, 0, 3, 3, [p2Card], IsHostLocalSeat: true);
        var enemy = new EnemyStateSnapshot(
            "e_1",
            "Gnash Grub",
            40,
            "attack",
            40,
            0,
            true,
            [new EnemyIntentSnapshot("attack", 9, 1, 9, "Attack")],
            ModelId: "jaw-worm");

        return MainMenuSnapshot() with
        {
            ScreenType = "combat",
            ScreenTitle = "Combat",
            ScreenInstanceId = "screen:combat:live",
            Menu = null,
            ResolvedPerspective = new PlayerPerspective(PlayerScope.Local, "p2", UsesDefault: false),
            Run = new RunStateSnapshot(
                "seed",
                3,
                1,
                [p1Run, p2Run],
                new Dictionary<string, PlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = p1Run,
                    ["p2"] = p2Run,
                },
                EncounterId: "jaw-worm",
                EncounterLabel: "Gnash Grub"),
            Combat = new CombatStateSnapshot(
                2,
                "p2",
                true,
                [p2Card],
                [p1Combat, p2Combat],
                [enemy],
                new Dictionary<string, CombatPlayerStateSnapshot>(StringComparer.Ordinal)
                {
                    ["p1"] = p1Combat,
                    ["p2"] = p2Combat,
                },
                EncounterId: "jaw-worm",
                EncounterLabel: "Gnash Grub"),
            AvailableActions =
            [
                new AvailableActionSnapshot(
                    "action:p2:play-card:c_p2_1:e_1",
                    SemanticActionKind.PlayCard,
                    "Play Strike.",
                    "sts2 act play-card --player-id p2 --card c_p2_1 --target e_1",
                    Provisional: false,
                    new ActionArgumentsSnapshot("p2", "c_p2_1", "e_1", null, null, null),
                    OwnerPlayerId: "p2"),
                new AvailableActionSnapshot(
                    "action:p2:end-turn",
                    SemanticActionKind.EndTurn,
                    "End turn.",
                    "sts2 act end-turn --player-id p2",
                    Provisional: false,
                    new ActionArgumentsSnapshot("p2", null, null, null, null, null),
                    OwnerPlayerId: "p2"),
            ],
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - startedAt > timeout)
            {
                throw new TimeoutException("Timed out waiting for test condition.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FixedSnapshotExtractor(GameStateSnapshot snapshot) : IGameStateExtractor
    {
        public GameStateQuery? LastQuery { get; private set; }
        public int CallCount { get; private set; }

        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            LastQuery = query;
            CallCount++;
            return snapshot with { ResolvedPerspective = perspective };
        }
    }

    private sealed class TransitioningSnapshotExtractor(params GameStateSnapshot[] snapshots) : IGameStateExtractor
    {
        public int CallCount { get; private set; }

        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            _ = query;
            var index = Math.Min(CallCount, snapshots.Length - 1);
            CallCount++;
            return snapshots[index] with { ResolvedPerspective = perspective };
        }
    }

    private sealed class ThrowingStateExtractor : IGameStateExtractor
    {
        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            _ = query;
            _ = perspective;
            throw new InvalidOperationException("state extractor failed");
        }
    }

    private sealed class NullStateExtractor : IGameStateExtractor
    {
        public int CallCount { get; private set; }

        public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
        {
            _ = query;
            _ = perspective;
            CallCount++;
            return null!;
        }
    }

    /// <summary>The observation the facade reads; its Language is what the watch hub's fingerprint varies with.</summary>
    private sealed class MutableStateProvider(StateSnapshot snapshot) : IStateProvider
    {
        public StateSnapshot Snapshot { get; set; } = snapshot;

        public PlayerPerspective? LastPerspective { get; private set; }

        public int ObserveCount { get; private set; }

        public StateSnapshot Observe(PlayerPerspective perspective)
        {
            LastPerspective = perspective;
            ObserveCount++;
            return Snapshot;
        }
    }

    private sealed class ThrowingStateProvider : IStateProvider
    {
        public StateSnapshot Observe(PlayerPerspective perspective)
        {
            _ = perspective;
            throw new InvalidOperationException("state provider failed");
        }
    }

    private sealed class NullStateProvider : IStateProvider
    {
        public StateSnapshot Observe(PlayerPerspective perspective)
        {
            _ = perspective;
            return null!;
        }
    }

    private sealed class CapturingActionHandler : IActionHandler
    {
        public SemanticActionRequest? LastRequest { get; private set; }

        public ActionExecutionResult Execute(SemanticActionRequest request)
        {
            LastRequest = request;
            return ActionExecutionResult.Success("action:choose:captured", request.Kind, "captured");
        }
    }

    private sealed class FixedAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
            => AssetExtractOperationResult.Success(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                request.OutputFormat,
                width: 2,
                height: 1,
                contents: [0x89, 0x50, 0x4e, 0x47],
                renderMode: "flattened-first-frame",
                notes: []);

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                AssetExtractFailureCode.NotImplemented,
                "asset composition explanation is not implemented by this fixed test provider.",
                []);
    }

    private sealed class ThrowingScreenshotProvider : IScreenshotProvider
    {
        public ScreenshotCaptureResult Capture(ScreenshotCaptureRequest request)
            => throw new InvalidOperationException("State observation must not capture screenshots.");
    }

    private sealed class ThrowingAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
            => throw new InvalidOperationException("State observation must not extract assets.");

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => throw new InvalidOperationException("State observation must not explain assets.");
    }


    private sealed class FixedBridgeHost(BridgeHostStatus status) : IBridgeHost
    {
        public BridgeHostStatus DescribeStatus() => status;
    }

    private sealed class CapturingAssetExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public List<AssetExtractRequestSnapshot> Requests { get; } = [];

        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            Requests.Add(request);
            return AssetExtractOperationResult.Success(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                request.OutputFormat,
                width: 1,
                height: 1,
                contents: [0x89, 0x50, 0x4e, 0x47],
                renderMode: "flattened-first-frame",
                notes: []);
        }

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                provisional: false,
                AssetExtractFailureCode.NotImplemented,
                "explain not needed in this test provider.",
                []);
    }

    private sealed class FixedTimelineExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
            => AssetExtractOperationResult.SuccessTimeline(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "auto",
                1,
                1,
                "timeline",
                16,
                [new AssetExtractFrame(0, "png", "image/png", 1, 1, [1, 2, 3, 4], 16)],
                []);

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(request.RequestId, DataSourceKind.Live, false, AssetExtractFailureCode.NotImplemented, "not implemented", []);
    }

    private sealed class FixedFontExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
            => AssetExtractOperationResult.SuccessFont(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "woff2",
                "font/woff2",
                [0, 1, 2, 3],
                "raw-font-file",
                []);

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(request.RequestId, DataSourceKind.Live, false, AssetExtractFailureCode.NotImplemented, "not implemented", []);
    }

    private sealed class AdditionalModelCatalogProvider : IModelCatalogProvider
    {
        public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
        {
            var models = new Dictionary<string, Dictionary<string, GameModelSnapshot>>(StringComparer.Ordinal)
            {
                ["monsters"] = new(StringComparer.Ordinal)
                {
                    ["jaw-worm"] = new MonsterGameModelSnapshot(
                        "JawWorm", "MegaCrit.Sts2.Core.Models.Monsters.JawWorm", 1, 10, true,
                        Title: "Gnash Grub",
                        MinInitialHp: 40,
                        MaxInitialHp: 44,
                        MoveNames: ["Gnash"],
                        AssetPaths: ["res://scenes/monsters/jaw_worm.tscn"],
                        VisualsAssetKey: "model://monsters/jaw-worm/visuals",
                        VisualsPath: "res://scenes/monsters/jaw_worm.tscn",
                        BestiaryAttackAnimId: "attack",
                        CanChangeScale: true,
                        IsHealthBarVisible: true,
                        DeathAnimLengthOverride: 0,
                        HasDeathAnimLengthOverride: false,
                        HasDeathSfx: true,
                        DeathSfx: "event:/sfx/monster/jaw_worm_death",
                        HasHurtSfx: true,
                        HurtSfx: "event:/sfx/monster/jaw_worm_hurt",
                        TakeDamageSfx: "event:/sfx/combat/damage_fleshy",
                        TakeDamageSfxType: "Fleshy",
                        ShouldFadeAfterDeath: true,
                        ShouldDisappearFromDoom: true,
                        HpBarSizeReduction: 0,
                        ExtraDeathVfxPadding: new ModelVector2Snapshot(4, 8)),
                },
                ["encounters"] = new(StringComparer.Ordinal)
                {
                    ["jaw-worm-weak"] = new EncounterGameModelSnapshot(
                        "JawWormWeak", "MegaCrit.Sts2.Core.Models.Encounters.JawWormWeak", 1, 20, true,
                        Title: "Gnash Grub",
                        RoomType: "Monster",
                        IsWeak: true,
                        IsDebugEncounter: false,
                        MonsterIds: ["JawWorm"],
                        MonstersWithSlots: [new EncounterMonsterSlotSnapshot("JawWorm", "M")],
                        Slots: ["M"],
                        Tags: ["Weak"],
                        MinGoldReward: 10,
                        MaxGoldReward: 15,
                        ShouldGiveRewards: true,
                        HasBgm: false,
                        CustomBgm: null,
                        HasAmbientSfx: false,
                        AmbientSfx: null,
                        HasScene: true,
                        SceneAssetKey: "model://encounters/jaw-worm-weak/scene",
                        ScenePath: "res://scenes/encounters/jaw_worm_weak.tscn",
                        BossNodePath: null,
                        MapNodeAssetPaths: ["res://images/map/monster.png"],
                        ExtraAssetPaths: [],
                        CustomRewardDescription: null,
                        FullyCenterPlayers: false,
                        CameraOffset: new ModelVector2Snapshot(0, -12),
                        CameraScaling: 1),
                },
                ["powers"] = new(StringComparer.Ordinal)
                {
                    ["strength"] = new PowerGameModelSnapshot(
                        "Resolve", "MegaCrit.Sts2.Core.Models.Powers.Strength", 2, 1, true,
                        Title: "Resolve",
                        Description: "Increase attack damage.",
                        SmartDescription: "Gain Strength.",
                        RemoteDescription: null,
                        Type: "Buff",
                        StackType: "Amount",
                        Amount: 0,
                        DisplayAmount: 0,
                        AmountOnTurnStart: 0,
                        AllowNegative: true,
                        IsVisible: true,
                        IsInstanced: false,
                        HasSmartDescription: true,
                        HasRemoteDescription: false,
                        ShouldPlayVfx: true,
                        ShouldScaleInMultiplayer: true,
                        AmountLabelColor: "rgb(255 255 255)",
                        IconAssetKey: "model://powers/strength/icon",
                        IconPath: "res://images/powers/strength.png",
                        PackedIconPath: "res://images/powers/strength.png",
                        BigIconAssetKey: "model://powers/strength/bigIcon",
                        ResolvedBigIconPath: "res://images/powers/strength_big.png"),
                },
                ["orbs"] = new(StringComparer.Ordinal)
                {
                    ["lightning"] = new OrbGameModelSnapshot(
                        "Lightning", "MegaCrit.Sts2.Core.Models.Orbs.Lightning", 3, 1, true,
                        Title: "Lightning",
                        Description: "Passive: Deal damage to a random enemy.",
                        SmartDescription: "Deal lightning damage.",
                        HasSmartDescription: true,
                        PassiveVal: 3,
                        EvokeVal: 8,
                        DarkenedColor: "rgb(90 90 150)",
                        AssetPaths: ["res://images/orbs/lightning.png"],
                        IconAssetKey: "model://orbs/lightning/icon",
                        IconPath: "res://images/orbs/lightning_icon.png",
                        SpriteAssetKey: "model://orbs/lightning/sprite",
                        SpritePath: "res://images/orbs/lightning.png"),
                },
                ["afflictions"] = new(StringComparer.Ordinal)
                {
                    ["hexed"] = new AfflictionGameModelSnapshot(
                        "Hexed", "MegaCrit.Sts2.Core.Models.Afflictions.Hexed", 4, 1, true,
                        Title: "Hexed",
                        Description: "This card is afflicted.",
                        ExtraCardText: "Hexed",
                        Amount: 1,
                        IsStackable: false,
                        CanAfflictUnplayableCards: false,
                        HasExtraCardText: true,
                        HasOverlay: true,
                        OverlayAssetKey: "model://afflictions/hexed/overlay",
                        OverlayPath: "res://scenes/cards/afflictions/hexed_overlay.tscn"),
                },
                ["enchantments"] = new(StringComparer.Ordinal)
                {
                    ["innate"] = new EnchantmentGameModelSnapshot(
                        "Innate", "MegaCrit.Sts2.Core.Models.Enchantments.Innate", 5, 1, true,
                        Title: "Innate",
                        Description: "Starts in your opening hand.",
                        ExtraCardText: "Innate",
                        Amount: 1,
                        DisplayAmount: 1,
                        ShowAmount: false,
                        Status: "Permanent",
                        IsStackable: false,
                        HasExtraCardText: true,
                        PreviewOutsideOfCombat: true,
                        ShouldGlowGold: true,
                        ShouldGlowRed: false,
                        ShouldStartAtBottomOfDrawPile: false,
                        IconAssetKey: "model://enchantments/innate/icon",
                        IconPath: "res://images/enchantments/innate.png",
                        IntendedIconPath: "res://images/enchantments/innate.png",
                        MissingIconPath: "res://images/enchantments/missing.png"),
                },
                ["card-pools"] = new(StringComparer.Ordinal)
                {
                    ["ironclad-card-pool"] = new CardPoolGameModelSnapshot(
                        "IroncladCardPool", "MegaCrit.Sts2.Core.Models.CardPools.IroncladCardPool", 6, 1, false,
                        Title: "Ironclad",
                        CardIds: ["StrikeIronclad"],
                        IsColorless: false,
                        EnergyColorName: "Red",
                        EnergyOutlineColor: "rgb(92 28 26)",
                        DeckEntryCardColor: "rgb(221 64 56)",
                        EnergyIconAssetKey: "model://card-pools/ironclad-card-pool/energyIcon",
                        EnergyIconPath: "res://images/cards/energy_red.png",
                        FrameMaterialAssetKey: "model://card-pools/ironclad-card-pool/frameMaterial",
                        FrameMaterialPath: "res://materials/cards/ironclad_frame.tres",
                        CardFrameMaterialPath: "res://materials/cards/ironclad_card_frame.tres"),
                },
                ["relic-pools"] = new(StringComparer.Ordinal)
                {
                    ["ironclad-relic-pool"] = new RelicPoolGameModelSnapshot(
                        "IroncladRelicPool", "MegaCrit.Sts2.Core.Models.RelicPools.IroncladRelicPool", 7, 1, false,
                        RelicIds: ["EmberHeart"],
                        EnergyColorName: "Red",
                        LabOutlineColor: "rgb(221 64 56)"),
                },
                ["potion-pools"] = new(StringComparer.Ordinal)
                {
                    ["shared-potion-pool"] = new PotionPoolGameModelSnapshot(
                        "SharedPotionPool", "MegaCrit.Sts2.Core.Models.PotionPools.SharedPotionPool", 8, 1, false,
                        PotionIds: ["FirePotion"],
                        EnergyColorName: "Shared",
                        LabOutlineColor: "rgb(255 255 255)"),
                },
                ["modifiers"] = new(StringComparer.Ordinal)
                {
                    ["big-game-hunter"] = new ModifierGameModelSnapshot(
                        "BigGameHunter", "MegaCrit.Sts2.Core.Models.Modifiers.BigGameHunter", 9, 1, true,
                        Title: "Big Game Hunter",
                        Description: "Elites are more rewarding.",
                        NeowOptionTitle: "Big Game Hunter",
                        NeowOptionDescription: "Hunt stronger foes.",
                        ClearsPlayerDeck: false,
                        Polarity: "good",
                        MutuallyExclusiveModifierIds: ["SmallGameHunter"],
                        IconAssetKey: "model://modifiers/big-game-hunter/icon",
                        IconPath: "res://images/modifiers/big_game_hunter.png"),
                },
                ["achievements"] = new(StringComparer.Ordinal)
                {
                    ["first-win"] = new AchievementGameModelSnapshot(
                        "FirstWin", "MegaCrit.Sts2.Core.Models.Achievements.FirstWin", 10, 1, false),
                },
            };

            var familyModels = models[request.Family];
            return ModelCatalogOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                request.Family,
                language: "eng",
                ModelCatalogStatus.Ok,
                [.. request.Ids.Select(id => familyModels[id])],
                [],
                []);
        }
    }

    private sealed class CapturingModelCatalogProvider : IModelCatalogProvider
    {
        public ModelCatalogRequestSnapshot? LastRequest { get; private set; }

        public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
        {
            LastRequest = request;
            return ModelCatalogOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                request.Family,
                request.Language ?? "eng",
                ModelCatalogStatus.Ok,
                [],
                [],
                []);
        }
    }

    private sealed class LocRefModelCatalogProvider : IModelCatalogProvider
    {
        public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
        {
            var relic = new RelicGameModelSnapshot(
                "EmberHeart",
                Title: null,
                Flavor: null,
                Description: null,
                IconPath: "res://images/relics/burning_blood.png",
                IconOutlinePath: "res://images/relics/burning_blood_outline.png",
                BigIconPath: "res://images/relics/burning_blood_big.png",
                Rarity: "Starter",
                IconAssetKey: "model://relics/burning-blood/icon",
                IconOutlineAssetKey: "model://relics/burning-blood/iconOutline",
                BigIconAssetKey: "model://relics/burning-blood/bigIcon",
                PoolId: "IroncladRelicPool",
                IsTradable: false,
                IsAllowedInShops: true,
                HasUponPickupEffect: false,
                SpawnsPets: false,
                AddsPet: false,
                IsStackable: false,
                MerchantCost: 999999999,
                ShowCounter: false,
                FlashSfx: "event:/sfx/ui/relic_activate_general")
            {
                LocalizationRefs = new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal)
                {
                    ["title"] = new("relics", "EmberHeart.title"),
                    ["description"] = new("relics", "EmberHeart.description"),
                },
            };

            return ModelCatalogOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                request.Family,
                language: null,
                ModelCatalogStatus.Ok,
                [relic],
                [],
                []);
        }
    }

    private sealed class FixedModelCatalogProvider : IModelCatalogProvider
    {
        public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
        {
            var characterModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["silent"] = new CharacterGameModelSnapshot(
                    "THE_SILENT",
                    Title: "The Silent",
                    NameColor: "rgb(78 190 92)",
                    StartingHp: 70,
                    StartingGold: 99,
                    MaxEnergy: 3,
                    EnergyLabelOutlineColor: "rgb(24 82 35)",
                    BaseOrbSlotCount: 0,
                    ShouldAlwaysShowStarCounter: false,
                    StartingRelics: ["RingOfTheSnake"],
                    CharacterSelectTitle: "The Silent",
                    CharacterSelectDesc: "A deadly huntress from the foglands.",
                    UnlockText: null,
                    DialogueColor: "rgb(78 190 92)",
                    SpeechBubbleColor: "rgb(79 210 99)",
                    MapDrawingColor: "rgb(78 190 92)",
                    VisualsAssetKey: "model://characters/silent/visuals",
                    IconAssetKey: "model://characters/silent/icon",
                    IconOutlineAssetKey: "model://characters/silent/iconOutline",
                    EnergyCounterAssetKey: "model://characters/silent/energyCounter",
                    MerchantAnimAssetKey: "model://characters/silent/merchantAnim",
                    RestSiteAnimAssetKey: "model://characters/silent/restSiteAnim",
                    CharacterSelectBgAssetKey: "model://characters/silent/characterSelectBg",
                    CharacterSelectBgSpineStillAssetKey: "model://characters/silent/characterSelectBgSpineStill",
                    CharacterSelectIconAssetKey: "model://characters/silent/characterSelectIcon",
                    CharacterSelectLockedIconAssetKey: "model://characters/silent/characterSelectLockedIcon",
                    MapMarkerAssetKey: "model://characters/silent/mapMarker",
                    IconPath: "res://images/characters/silent/icon.png",
                    IconOutlinePath: "res://images/characters/silent/icon_outline.png",
                    EnergyCounterPath: "res://scenes/character_energy/silent_energy_counter.tscn",
                    MerchantAnimPath: "res://scenes/character_animations/silent_merchant.tscn",
                    RestSiteAnimPath: "res://scenes/character_animations/silent_rest_site.tscn",
                    CharacterSelectBgPath: "res://scenes/screens/char_select/char_select_bg_silent.tscn",
                    CharacterSelectIconPath: "res://images/characters/silent/select_icon.png",
                    CharacterSelectLockedIconPath: "res://images/characters/silent/select_locked.png",
                    MapMarkerPath: "res://images/characters/silent/map_marker.png"),
                ["ironclad"] = new CharacterGameModelSnapshot(
                    "IRONCLAD",
                    Title: "The Bulwark",
                    NameColor: "rgb(221 64 56)",
                    StartingHp: 80,
                    StartingGold: 99,
                    MaxEnergy: 3,
                    EnergyLabelOutlineColor: "rgb(92 28 26)",
                    BaseOrbSlotCount: 0,
                    ShouldAlwaysShowStarCounter: false,
                    StartingRelics: ["EmberHeart"],
                    CharacterSelectTitle: "The Bulwark",
                    CharacterSelectDesc: "A veteran of the frontier wars.",
                    UnlockText: null,
                    DialogueColor: "rgb(221 64 56)",
                    SpeechBubbleColor: "rgb(238 74 74)",
                    MapDrawingColor: "rgb(221 64 56)",
                    VisualsAssetKey: "model://characters/ironclad/visuals",
                    IconAssetKey: "model://characters/ironclad/icon",
                    IconOutlineAssetKey: "model://characters/ironclad/iconOutline",
                    EnergyCounterAssetKey: "model://characters/ironclad/energyCounter",
                    MerchantAnimAssetKey: "model://characters/ironclad/merchantAnim",
                    RestSiteAnimAssetKey: "model://characters/ironclad/restSiteAnim",
                    CharacterSelectBgAssetKey: "model://characters/ironclad/characterSelectBg",
                    CharacterSelectBgSpineStillAssetKey: "model://characters/ironclad/characterSelectBgSpineStill",
                    CharacterSelectIconAssetKey: "model://characters/ironclad/characterSelectIcon",
                    CharacterSelectLockedIconAssetKey: "model://characters/ironclad/characterSelectLockedIcon",
                    MapMarkerAssetKey: "model://characters/ironclad/mapMarker",
                    IconPath: "res://images/characters/ironclad/icon.png",
                    IconOutlinePath: "res://images/characters/ironclad/icon_outline.png",
                    EnergyCounterPath: "res://scenes/character_energy/ironclad_energy_counter.tscn",
                    MerchantAnimPath: "res://scenes/character_animations/ironclad_merchant.tscn",
                    RestSiteAnimPath: "res://scenes/character_animations/ironclad_rest_site.tscn",
                    CharacterSelectBgPath: "res://scenes/screens/char_select/char_select_bg_ironclad.tscn",
                    CharacterSelectIconPath: "res://images/characters/ironclad/select_icon.png",
                    CharacterSelectLockedIconPath: "res://images/characters/ironclad/select_locked.png",
                    MapMarkerPath: "res://images/characters/ironclad/map_marker.png"),
            };
            var relicModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["burning-blood"] = new RelicGameModelSnapshot(
                    "EmberHeart",
                    Title: "Ember Heart",
                    Flavor: "Your body's own blood burns with an undying rage.",
                    Description: "When a battle ends, restore 6 HP.",
                    IconPath: "res://images/relics/burning_blood.png",
                    IconOutlinePath: "res://images/relics/burning_blood_outline.png",
                    BigIconPath: "res://images/relics/burning_blood_big.png",
                    Rarity: "Starter",
                    IconAssetKey: "model://relics/burning-blood/icon",
                    IconOutlineAssetKey: "model://relics/burning-blood/iconOutline",
                    BigIconAssetKey: "model://relics/burning-blood/bigIcon",
                    PoolId: "IroncladRelicPool",
                    IsTradable: false,
                    IsAllowedInShops: true,
                    HasUponPickupEffect: false,
                    SpawnsPets: false,
                    AddsPet: false,
                    IsStackable: false,
                    MerchantCost: 999999999,
                    ShowCounter: false,
                    FlashSfx: "event:/sfx/ui/relic_activate_general"),
            };
            var cardModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["strike-ironclad"] = new CardGameModelSnapshot(
                    "StrikeIronclad",
                    Title: "Jab",
                    Description: "Land 6 harm.",
                    Type: "Attack",
                    Rarity: "Basic",
                    TargetType: "AnyEnemy",
                    PoolId: "IroncladCardPool",
                    VisualPoolId: "IroncladCardPool",
                    EnergyCost: 1,
                    IsEnergyXCost: false,
                    StarCost: -1,
                    IsStarXCost: false,
                    ReplayCount: 0,
                    Keywords: [],
                    Tags: ["Jab"],
                    MaxUpgradeLevel: 1,
                    Upgradable: true,
                    UpgradePreviewDescription: "Land 9 harm.",
                    CanBeGeneratedInCombat: true,
                    CanBeGeneratedByModifiers: true,
                    MultiplayerConstraint: "None",
                    ShouldShowInCardLibrary: true,
                    GainsBlock: false,
                    OrbEvokeType: "None",
                    HasBuiltInOverlay: false,
                    ImageAssetKey: "model://cards/strike-ironclad/image",
                    ImagePath: "res://images/cards/strike_ironclad.tres",
                    BetaImagePath: "res://images/cards/beta/strike_ironclad.tres",
                    OverlayAssetKey: "model://cards/strike-ironclad/overlay",
                    OverlayPath: "res://scenes/cards/overlays/strike_ironclad.tscn",
                    DynamicVars: new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["damage"] = 6,
                    },
                    Upgrade: new CardGameModelUpgradeSnapshot(
                        DynamicVars: new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["damage"] = 9,
                        })),
            };
            var potionModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["fire-potion"] = new PotionGameModelSnapshot(
                    "FirePotion",
                    Title: "Fire Potion",
                    Description: "Land 20 harm.",
                    SelectionScreenPrompt: "Choose a target.",
                    Rarity: "Common",
                    Usage: "CombatOnly",
                    TargetType: "AnyEnemy",
                    PoolId: "SharedPotionPool",
                    CanBeGeneratedInCombat: true,
                    PassesCustomUsabilityCheck: true,
                    IconAssetKey: "model://potions/fire-potion/icon",
                    IconPath: "res://images/potions/fire_potion.tres",
                    OutlineAssetKey: "model://potions/fire-potion/outline",
                    OutlinePath: "res://images/potions/fire_potion_outline.tres"),
            };
            var eventModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["room-full-of-cheese"] = new EventGameModelSnapshot(
                    "RoomFullOfCheese",
                    Kind: "event",
                    Title: "Room Full of Cheese",
                    InitialDescription: "A strange scent fills the room.",
                    LayoutType: "Default",
                    IsShared: false,
                    IsDeterministic: true,
                    HasVfx: false,
                    CanonicalEncounterId: null,
                    GameInfoOptions: ["Take the cheese."],
                    BackgroundSceneAssetKey: "model://events/room-full-of-cheese/backgroundScene",
                    BackgroundScenePath: "res://scenes/events/background_scenes/roomfullofcheese.tscn",
                    BackgroundSpineStillAssetKey: "model://events/room-full-of-cheese/backgroundSpineStill",
                    BackgroundSpineStillPath: "res://scenes/events/background_scenes/roomfullofcheese.tscn",
                    InitialPortraitAssetKey: "model://events/room-full-of-cheese/initialPortrait",
                    InitialPortraitPath: "res://images/events/roomfullofcheese.png",
                    VfxAssetKey: "model://events/room-full-of-cheese/vfx",
                    VfxPath: null,
                    Epithet: null,
                    DialogueColor: null,
                    ButtonColor: "rgba(255 255 255 / 0.9)",
                    AmbientBgm: null,
                    HasAmbientBgm: false,
                    AnyCharacterDialogueBlacklistIds: [],
                    MapIconAssetKey: null,
                    MapIconPath: null,
                    MapIconOutlineAssetKey: null,
                    MapIconOutlinePath: null,
                    RunHistoryIconAssetKey: null,
                    RunHistoryIconPath: null,
                    RunHistoryIconOutlineAssetKey: null,
                    RunHistoryIconOutlinePath: null),
                ["neow"] = new EventGameModelSnapshot(
                    "Neow",
                    Kind: "ancient",
                    Title: "Neow",
                    InitialDescription: "Choose a blessing.",
                    LayoutType: "Ancient",
                    IsShared: false,
                    IsDeterministic: true,
                    HasVfx: false,
                    CanonicalEncounterId: null,
                    GameInfoOptions: ["Obtain a blessing."],
                    BackgroundSceneAssetKey: "model://events/neow/backgroundScene",
                    BackgroundScenePath: "res://scenes/events/background_scenes/neow.tscn",
                    BackgroundSpineStillAssetKey: "model://events/neow/backgroundSpineStill",
                    BackgroundSpineStillPath: "res://scenes/events/background_scenes/neow.tscn",
                    InitialPortraitAssetKey: "model://events/neow/initialPortrait",
                    InitialPortraitPath: "res://images/events/neow.png",
                    VfxAssetKey: "model://events/neow/vfx",
                    VfxPath: null,
                    Epithet: "The Ancient of Resurrection",
                    DialogueColor: "rgb(40 69 79)",
                    ButtonColor: "rgba(0 0 0 / 0.35)",
                    AmbientBgm: null,
                    HasAmbientBgm: false,
                    AnyCharacterDialogueBlacklistIds: [],
                    MapIconAssetKey: "model://events/neow/mapIcon",
                    MapIconPath: "res://images/packed/map/ancients/ancient_node_neow.png",
                    MapIconOutlineAssetKey: "model://events/neow/mapIconOutline",
                    MapIconOutlinePath: "res://images/packed/map/ancients/ancient_node_neow_outline.png",
                    RunHistoryIconAssetKey: "model://events/neow/runHistoryIcon",
                    RunHistoryIconPath: "res://images/ui/run_history/neow.png",
                    RunHistoryIconOutlineAssetKey: "model://events/neow/runHistoryIconOutline",
                    RunHistoryIconOutlinePath: "res://images/ui/run_history/neow_outline.png"),
            };
            var actModels = new Dictionary<string, GameModelSnapshot>(StringComparer.Ordinal)
            {
                ["overgrowth"] = new ActGameModelSnapshot(
                    "Overgrowth",
                    Title: "The Overgrowth",
                    DefaultOrder: 1,
                    RoomCount: 15,
                    MultiplayerRoomCount: 14,
                    FloorCount: 17,
                    MultiplayerFloorCount: 16,
                    BossEncounterIds: ["VantomBoss"],
                    EventIds: ["RoomFullOfCheese"],
                    AncientEventIds: ["Neow"],
                    WeakEncounterIds: ["SlimesWeak"],
                    RegularEncounterIds: ["SlimesNormal"],
                    EliteEncounterIds: ["ByrdonisElite"],
                    MonsterIds: ["Cultist"],
                    BgMusicOptions: ["event:/music/act1_a1_v1"],
                    MusicBankPaths: ["res://banks/desktop/act1_a1.bank"],
                    AmbientSfx: "event:/sfx/ambience/act1_ambience",
                    ChestOpenSfx: "event:/sfx/ui/treasure/treasure_act1",
                    MapTraveledColor: "rgb(40 35 29)",
                    MapUntraveledColor: "rgb(135 114 86)",
                    MapBgColor: "rgb(167 138 103)",
                    BackgroundSceneAssetKey: "model://acts/overgrowth/backgroundScene",
                    BackgroundScenePath: "res://scenes/backgrounds/overgrowth/overgrowth_background.tscn",
                    RestSiteBackgroundAssetKey: "model://acts/overgrowth/restSiteBackground",
                    RestSiteBackgroundPath: "res://scenes/rest_site/overgrowth_rest_site.tscn",
                    MapTopBgAssetKey: "model://acts/overgrowth/mapTopBg",
                    MapTopBgPath: "res://images/packed/map/map_bgs/overgrowth/map_top_overgrowth.png",
                    MapMidBgAssetKey: "model://acts/overgrowth/mapMidBg",
                    MapMidBgPath: "res://images/packed/map/map_bgs/overgrowth/map_middle_overgrowth.png",
                    MapBotBgAssetKey: "model://acts/overgrowth/mapBotBg",
                    MapBotBgPath: "res://images/packed/map/map_bgs/overgrowth/map_bottom_overgrowth.png",
                    ChestSpineAssetKey: "model://acts/overgrowth/chestSpine",
                    ChestSpineResourcePath: "res://animations/backgrounds/treasure_room/chest_room_act_1_skel_data.tres"),
            };
            var models = request.Family switch
            {
                "relics" => relicModels,
                "cards" => cardModels,
                "potions" => potionModels,
                "events" => eventModels,
                "ancients" => eventModels
                    .Where(entry => entry.Value is EventGameModelSnapshot { Kind: "ancient" })
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                "acts" => actModels,
                _ => characterModels,
            };

            return ModelCatalogOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                request.Family,
                language: request.Language ?? "eng",
                ModelCatalogStatus.Ok,
                [.. request.Ids.Select(id => models[id])],
                [],
                []);
        }
    }



    private sealed class TestDispatcherContext(
        int queueDepth,
        DateTimeOffset? lastDrainAtUtc,
        bool? isApplicationFocused) : SynchronizationContext, IMainThreadDispatcherDiagnostics
    {
        public int QueueDepth { get; } = queueDepth;

        public DateTimeOffset? LastDrainAtUtc { get; } = lastDrainAtUtc;

        public string? DispatcherNote => null;

        public bool? IsApplicationFocused { get; } = isApplicationFocused;

        public DateTimeOffset? LastFocusChangedAtUtc { get; } = DateTimeOffset.UtcNow;
    }

    [Fact]
    public void ProtocolAdapterMapsReferenceColors()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            referenceDataProvider: new FixedReferenceDataProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetReference(new Spirectl.Proto.V0.ReferenceRequest
        {
            RequestId = "reference-colors-1",
            Topic = "colors",
            Keys = { "screenBackdrop", "missing" },
        });

        Assert.Equal(Spirectl.Proto.V0.ReferenceResult.ResultOneofCase.Success, response.ResultCase);
        var success = response.Success;
        Assert.Equal("reference-colors-1", success.RequestId);
        Assert.Equal("colors", success.Topic);
        Assert.Equal(Spirectl.Proto.V0.ReferenceStatus.Partial, success.Status);
        Assert.Equal(new[] { "missing" }, success.MissingKeys);
        Assert.Equal(Spirectl.Proto.V0.ReferenceResponse.PayloadOneofCase.Colors, success.PayloadCase);
        var color = Assert.Single(success.Colors.Colors);
        Assert.Equal("screenBackdrop", color.Name);
        Assert.Equal("000000cc", color.Hex);
        Assert.Equal(0.8f, color.A, 3);
    }

    [Fact]
    public void ProtocolAdapterMapsReferenceVersion()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            referenceDataProvider: new FixedReferenceDataProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetReference(new Spirectl.Proto.V0.ReferenceRequest
        {
            RequestId = "reference-version-1",
            Topic = "version",
        });

        Assert.Equal(Spirectl.Proto.V0.ReferenceResult.ResultOneofCase.Success, response.ResultCase);
        var version = response.Success.Version;
        Assert.Equal(Spirectl.Proto.V0.ReferenceStatus.Ok, response.Success.Status);
        Assert.Equal("v0.103.2", version.Version);
        Assert.Equal("2026.04.16", version.VersionDate);
        Assert.Equal("abc123", version.Commit);
        Assert.Equal(42, version.MainAssemblyHash);
        Assert.NotNull(version.Modding);
        Assert.True(version.Modding.IsRunningModded);
        Assert.Equal(2, version.Modding.LoadedModCount);
        Assert.Equal(3, version.Modding.TotalModCount);
    }

    [Fact]
    public void ProtocolAdapterMapsReferenceRandomCharacter()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider(),
            referenceDataProvider: new FixedReferenceDataProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetReference(new Spirectl.Proto.V0.ReferenceRequest
        {
            RequestId = "reference-random-character-1",
            Topic = "randomCharacter",
        });

        Assert.Equal(Spirectl.Proto.V0.ReferenceResult.ResultOneofCase.Success, response.ResultCase);
        var success = response.Success;
        Assert.Equal("randomCharacter", success.Topic);
        Assert.Equal(Spirectl.Proto.V0.ReferenceStatus.Ok, success.Status);
        Assert.Equal(Spirectl.Proto.V0.ReferenceResponse.PayloadOneofCase.RandomCharacter, success.PayloadCase);
        var character = success.RandomCharacter;
        Assert.Equal(RandomCharacterFacts.Id, character.Id);
        Assert.Equal(RandomCharacterFacts.PortraitAssetKey, character.CharacterSelectIconAssetKey);
        Assert.Equal(RandomCharacterFacts.LockedIconAssetKey, character.CharacterSelectLockedIconAssetKey);
        Assert.NotEqual(character.CharacterSelectIconAssetKey, character.CharacterSelectLockedIconAssetKey);
        Assert.Equal(RandomCharacterFacts.SelectBackgroundAssetKey, character.CharacterSelectBgPath);
        Assert.Equal(RandomCharacterFacts.LocTable, character.CharacterSelectTitleLoc.Table);
        Assert.Equal(RandomCharacterFacts.NameKey, character.CharacterSelectTitleLoc.Key);
    }

    [Fact]
    public void ProtocolAdapterReportsUnavailableReferenceWithoutProvider()
    {
        var runtime = RuntimeWith(
            stateExtractor: new FixedSnapshotExtractor(MainMenuSnapshot()),
            actionHandler: new PlaceholderActionHandler(),
            assetExtractProvider: new PlaceholderAssetExtractProvider());
        var adapter = new BridgeRuntimeProtocolAdapter(runtime);

        var response = adapter.HandleGetReference(new Spirectl.Proto.V0.ReferenceRequest
        {
            RequestId = "reference-unavailable-1",
            Topic = "colors",
        });

        Assert.Equal(Spirectl.Proto.V0.ReferenceResult.ResultOneofCase.Error, response.ResultCase);
    }

    private sealed class FixedReferenceDataProvider : IReferenceDataProvider
    {
        public ReferenceOperationResult GetReference(ReferenceRequestSnapshot request)
        {
            switch (request.Topic)
            {
                case "colors":
                {
                    var all = new List<GameColorSnapshot>
                    {
                        new("aqua", "2aebbe", 0.164f, 0.921f, 0.745f, 1f),
                        new("screenBackdrop", "000000cc", 0f, 0f, 0f, 0.8f),
                    };
                    IReadOnlyList<string> missing = [];
                    IReadOnlyList<GameColorSnapshot> selected = all;
                    if (request.Keys.Count > 0)
                    {
                        var miss = new List<string>();
                        var chosen = new List<GameColorSnapshot>();
                        foreach (var key in request.Keys)
                        {
                            GameColorSnapshot? found = null;
                            foreach (var color in all)
                            {
                                if (string.Equals(color.Name, key, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = color;
                                    break;
                                }
                            }

                            if (found is not null)
                            {
                                chosen.Add(found);
                            }
                            else
                            {
                                miss.Add(key);
                            }
                        }

                        selected = chosen;
                        missing = miss;
                    }

                    return ReferenceOperationResult.Success(
                        DataSourceKind.Live,
                        provisional: false,
                        topic: "colors",
                        status: missing.Count > 0 ? ReferenceStatus.Partial : ReferenceStatus.Ok,
                        payload: new GameColorsSnapshot(selected),
                        missingKeys: missing,
                        notices: []);
                }

                case "version":
                    return ReferenceOperationResult.Success(
                        DataSourceKind.Live,
                        provisional: false,
                        topic: "version",
                        status: ReferenceStatus.Ok,
                        payload: new GameVersionInfoSnapshot(
                            "v0.103.2",
                            "2026.04.16",
                            "abc123",
                            "main",
                            42,
                            new ModdingSummarySnapshot(true, 2, 3)),
                        missingKeys: [],
                        notices: []);

                case "randomCharacter":
                {
                    var character = new CharacterGameModelSnapshot(
                        Id: RandomCharacterFacts.Id,
                        Title: null,
                        NameColor: null,
                        StartingHp: 0,
                        StartingGold: 0,
                        MaxEnergy: 0,
                        EnergyLabelOutlineColor: null,
                        BaseOrbSlotCount: 0,
                        ShouldAlwaysShowStarCounter: false,
                        StartingRelics: [],
                        CharacterSelectTitle: null,
                        CharacterSelectDesc: null,
                        UnlockText: null,
                        DialogueColor: null,
                        SpeechBubbleColor: null,
                        MapDrawingColor: null,
                        VisualsAssetKey: null,
                        IconAssetKey: null,
                        IconOutlineAssetKey: null,
                        EnergyCounterAssetKey: null,
                        MerchantAnimAssetKey: null,
                        RestSiteAnimAssetKey: null,
                        CharacterSelectBgAssetKey: null,
                        CharacterSelectBgSpineStillAssetKey: null,
                        CharacterSelectIconAssetKey: RandomCharacterFacts.PortraitAssetKey,
                        CharacterSelectLockedIconAssetKey: RandomCharacterFacts.LockedIconAssetKey,
                        MapMarkerAssetKey: null,
                        IconPath: null,
                        IconOutlinePath: null,
                        EnergyCounterPath: null,
                        MerchantAnimPath: null,
                        RestSiteAnimPath: null,
                        CharacterSelectBgPath: RandomCharacterFacts.SelectBackgroundAssetKey,
                        CharacterSelectIconPath: null,
                        CharacterSelectLockedIconPath: null,
                        MapMarkerPath: null)
                    {
                        LocalizationRefs = new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal)
                        {
                            ["title"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.NameKey),
                            ["characterSelectTitle"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.NameKey),
                            ["characterSelectDesc"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.DescriptionKey),
                        },
                    };
                    return ReferenceOperationResult.Success(
                        DataSourceKind.Live,
                        provisional: false,
                        topic: "randomCharacter",
                        status: ReferenceStatus.Ok,
                        payload: new RandomCharacterSnapshot(character),
                        missingKeys: [],
                        notices: []);
                }

                default:
                    return ReferenceOperationResult.Failure(
                        DataSourceKind.Live,
                        provisional: false,
                        topic: request.Topic,
                        status: ReferenceStatus.UnsupportedTopic,
                        code: "reference-unsupported-topic",
                        message: "Unsupported reference topic.",
                        notices: []);
            }
        }
    }

}
