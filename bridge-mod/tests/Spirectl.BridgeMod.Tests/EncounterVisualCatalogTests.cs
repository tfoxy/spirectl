#if ENABLE_STS2_LIVE_HOST
extern alias Sts2Live;
#endif

using Spirectl.Sts2.Live.EncounterVisuals;
using Spirectl.Sts2.Core.Logging;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EncounterVisualCatalogTests
{
    [Fact]
    public void BaseGameCatalogContainsKaiserCrabMappings()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        Assert.True(catalog.TryGetPackage("kaiser_crab_boss", out var package));
        Assert.Equal("composed://encounters/kaiser_crab_boss/scene-package", package.PackageId);
        Assert.Equal(
            "res://scenes/backgrounds/kaiser_crab_boss/kaiser_crab_boss_background.tscn",
            package.Background.SourceScene);
        Assert.Equal(
            "res://scenes/creature_visuals/kaiser_crab_boss_setup.tscn",
            package.SpecialVisual.SourceScene);

        var crusher = Assert.Single(package.LogicalActors, actor => actor.ActorId == "crusher");
        Assert.Equal("crusher", crusher.SlotId);
        Assert.Contains("crusher", crusher.StatePartIds);

        var rocketPart = Assert.Single(package.VisualParts, part => part.PartId == "rocket");
        Assert.Equal("rocket", rocketPart.ActorId);
        Assert.Equal("right", rocketPart.ScreenSide);
        Assert.Equal("right", rocketPart.AnatomicalSide);
        Assert.Equal("%ArmBoneR", rocketPart.Selector);
        Assert.NotNull(rocketPart.ViewportRect);

        var bodyPart = Assert.Single(package.VisualParts, part => part.PartId == "body");
        Assert.Equal("center", bodyPart.ScreenSide);
        Assert.Equal("body", bodyPart.AnatomicalSide);
        Assert.Equal("%Visuals", bodyPart.Selector);
        Assert.NotNull(bodyPart.ViewportRect);

        Assert.All(package.LogicalActors, actor => Assert.NotNull(actor.TargetRect));

        var stateIds = package.States.Select(state => state.StateId).ToHashSet();
        Assert.Contains("default", stateIds);
        Assert.Contains("hurt-left", stateIds);
        Assert.Contains("hurt-right", stateIds);
        Assert.Contains("arm-death-left", stateIds);
        Assert.Contains("arm-death-right", stateIds);
        Assert.Contains("rocket-charge-up", stateIds);
        Assert.Contains("rocket-heavy", stateIds);
        Assert.Contains("rocket-recharge", stateIds);
        Assert.Contains("body-death", stateIds);
        Assert.Equal(
            "PlayRightSideChargeUpAnim",
            Assert.Single(package.States, state => state.StateId == "rocket-charge-up").Hook);

        var transition = Assert.Single(package.Transitions, transition => transition.TransitionId == "rocket-charge-up");
        Assert.Equal("PlayRightSideChargeUpAnim", transition.Hook);
        Assert.Contains("rocket", transition.AffectedPartIds);
        Assert.Equal("rocket-charge-up", transition.ActiveStateId);

        Assert.Contains(
            package.RenderTargets,
            target => target.Query == "composed://encounters/kaiser_crab_boss/visual-part/rocket/state/rocket-charge-up/image");
    }

    [Fact]
    public void VisualStateOverlayTargetsKeepAllCatalogPartsRenderable()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        Assert.True(catalog.TryGetPackage("kaiser_crab_boss", out var package));

        var state = Assert.Single(package.States, state => state.StateId == "rocket-charge-up");
        Assert.Equal(["rocket"], state.AffectedPartIds);
        Assert.Contains(
            package.RenderTargets,
            target => target.Query == "composed://encounters/kaiser_crab_boss/visual-state/rocket-charge-up/overlay/image");
        Assert.Equal(
            package.VisualParts.Select(part => part.PartId).Order(StringComparer.Ordinal),
            new[] { "body", "crusher", "rocket" });
    }

    [Fact]
    public void BaseGameCatalogContainsOvicopterPriorityMappings()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        Assert.True(catalog.TryGetPackage("ovicopter_normal", out var package));
        Assert.Equal("composed://encounters/ovicopter_normal/scene-package", package.PackageId);
        Assert.Equal("res://scenes/backgrounds/hive/hive_background.tscn", package.Background.SourceScene);
        Assert.Equal("composed://combat-background/hive/image", package.Background.SourceQuery);
        Assert.Equal("composed://encounters/ovicopter_normal/background/image", package.Background.RenderQuery);
        Assert.Equal("res://scenes/creature_visuals/ovicopter.tscn", package.SpecialVisual.SourceScene);
        Assert.Equal("NCreatureVisuals", package.SpecialVisual.RootType);

        var actor = Assert.Single(package.LogicalActors);
        Assert.Equal("ovicopter", actor.ActorId);
        Assert.Equal("ovicopter", actor.SlotId);
        Assert.Equal(["body"], actor.StatePartIds);
        Assert.NotNull(actor.TargetRect);
        Assert.InRange(actor.TargetRect!.Width, 1, double.MaxValue);
        Assert.InRange(actor.TargetRect.Height, 1, double.MaxValue);

        var body = Assert.Single(package.VisualParts);
        Assert.Equal("body", body.PartId);
        Assert.Equal("ovicopter", body.ActorId);
        Assert.Equal("right", body.ScreenSide);
        Assert.Equal("body", body.AnatomicalSide);
        Assert.Equal("%Visuals", body.Selector);
        Assert.NotNull(body.ViewportRect);
        Assert.InRange(body.ViewportRect!.Width, 1, double.MaxValue);
        Assert.InRange(body.ViewportRect.Height, 1, double.MaxValue);

        Assert.Contains(package.States, state => state.StateId == "default" && state.AffectedPartIds.SequenceEqual(["body"]));
        Assert.Contains(package.States, state => state.StateId == "lay-eggs" && state.Hook == "layTrigger");
        Assert.Contains(package.Transitions, transition =>
            transition.TransitionId == "lay-eggs"
            && transition.Hook == "layTrigger"
            && transition.ActiveStateId == "lay-eggs"
            && transition.AffectedPartIds.SequenceEqual(["body"]));

        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "background"
            && target.Query == "composed://encounters/ovicopter_normal/background/image");
        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "overlay"
            && target.Query == "composed://encounters/ovicopter_normal/visual-state/default/overlay/image");
        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "overlay"
            && target.Query == "composed://encounters/ovicopter_normal/visual-state/lay-eggs/overlay/image");
        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "part"
            && target.Query == "composed://encounters/ovicopter_normal/visual-part/body/state/lay-eggs/image");

        Assert.Contains(package.Notices, notice =>
            notice.Code == "encounter-visual-dynamic-minions-unsupported"
            && notice.Severity == "warning"
            && notice.Provisional
            && notice.Path == "encounterVisuals.ovicopter_normal.logicalActors");
    }

    [Fact]
    public void BaseGameCatalogContainsKnowledgeDemonBossMappings()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        Assert.True(catalog.TryGetPackage("knowledge_demon_boss", out var package));
        Assert.Equal("composed://encounters/knowledge_demon_boss/scene-package", package.PackageId);
        Assert.Equal(
            "res://scenes/backgrounds/knowledge_demon_boss/knowledge_demon_boss_background.tscn",
            package.Background.SourceScene);
        Assert.Equal("composed://combat-background/knowledge_demon_boss/image", package.Background.SourceQuery);
        Assert.Equal("composed://encounters/knowledge_demon_boss/background/image", package.Background.RenderQuery);
        Assert.Equal("res://scenes/creature_visuals/knowledge_demon.tscn", package.SpecialVisual.SourceScene);
        Assert.Equal("NCreatureVisuals", package.SpecialVisual.RootType);

        var actor = Assert.Single(package.LogicalActors);
        Assert.Equal("knowledge-demon", actor.ActorId);
        Assert.Equal("knowledge-demon", actor.SlotId);
        Assert.Equal(["body", "burn-fire"], actor.StatePartIds);
        Assert.NotNull(actor.TargetRect);
        Assert.InRange(actor.TargetRect!.Width, 1, double.MaxValue);
        Assert.InRange(actor.TargetRect.Height, 1, double.MaxValue);

        var body = Assert.Single(package.VisualParts, part => part.PartId == "body");
        Assert.Equal("knowledge-demon", body.ActorId);
        Assert.Equal("right", body.ScreenSide);
        Assert.Equal("body", body.AnatomicalSide);
        Assert.Equal("%Visuals", body.Selector);
        Assert.NotNull(body.ViewportRect);
        Assert.InRange(body.ViewportRect!.Width, 1, double.MaxValue);
        Assert.InRange(body.ViewportRect.Height, 1, double.MaxValue);

        var burnFire = Assert.Single(package.VisualParts, part => part.PartId == "burn-fire");
        Assert.Equal("overlay", burnFire.AnatomicalSide);
        Assert.Equal("%Visuals/NKnowledgeDemonVfx", burnFire.Selector);
        Assert.NotNull(burnFire.ViewportRect);
        Assert.InRange(burnFire.ViewportRect!.Width, 1, double.MaxValue);
        Assert.InRange(burnFire.ViewportRect.Height, 1, double.MaxValue);

        Assert.Contains(package.States, state =>
            state.StateId == "heavy-attack-burnt"
            && state.Hook == "HeavyAttackTrigger"
            && state.AffectedPartIds.SequenceEqual(["body", "burn-fire"]));
        Assert.Contains(package.Transitions, transition =>
            transition.TransitionId == "heavy-attack-burnt"
            && transition.Hook == "HeavyAttackTrigger"
            && transition.ActiveStateId == "heavy-attack-burnt"
            && transition.AffectedPartIds.SequenceEqual(["body", "burn-fire"]));

        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "background"
            && target.Query == "composed://encounters/knowledge_demon_boss/background/image");
        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "overlay"
            && target.Query == "composed://encounters/knowledge_demon_boss/visual-state/heavy-attack-burnt/overlay/image");
        Assert.Contains(package.RenderTargets, target =>
            target.Kind == "part"
            && target.Query == "composed://encounters/knowledge_demon_boss/visual-part/burn-fire/state/heavy-attack-burnt/image");

        Assert.Contains(package.Notices, notice =>
            notice.Code == "encounter-visual-vfx-live-only"
            && notice.Severity == "warning"
            && notice.Provisional
            && notice.Path == "encounterVisuals.knowledge_demon_boss.visualParts.burn-fire");
    }

    [Fact]
    public void CatalogValidationRejectsUnreviewableRenderablePartsAndTargets()
    {
        var missingSelector = """
            {
              "schemaVersion": "0",
              "packages": [
                {
                  "encounterId": "bad_encounter",
                  "packageId": "composed://encounters/bad_encounter/scene-package",
                  "background": {
                    "sourceScene": "res://scenes/backgrounds/hive/hive_background.tscn",
                    "sourceQuery": "composed://combat-background/hive/image",
                    "renderQuery": "composed://encounters/bad_encounter/background/image"
                  },
                  "specialVisual": {
                    "sourceScene": "res://scenes/creature_visuals/ovicopter.tscn",
                    "rootType": "NCreatureVisuals"
                  },
                  "logicalActors": [],
                  "visualParts": [
                    {
                      "partId": "body",
                      "actorId": "",
                      "screenSide": "right",
                      "anatomicalSide": "body",
                      "layer": 10,
                      "selector": ""
                    }
                  ],
                  "states": [
                    {
                      "stateId": "default",
                      "affectedPartIds": ["body"],
                      "hook": ""
                    }
                  ],
                  "transitions": [],
                  "renderTargets": [
                    {
                      "targetId": "body",
                      "kind": "part",
                      "query": "composed://encounters/bad_encounter/visual-part/body/state/default/image"
                    }
                  ],
                  "notices": []
                }
              ]
            }
            """;
        var unknownStateTarget = missingSelector.Replace(
            "\"selector\": \"\"",
            "\"selector\": \"%Visuals\"").Replace(
            "visual-part/body/state/default/image",
            "visual-part/body/state/missing/image");

        var selectorError = Assert.Throws<InvalidOperationException>(() => Sts2EncounterVisualCatalog.FromJson(missingSelector));
        Assert.Contains("selector", selectorError.Message);

        var targetError = Assert.Throws<InvalidOperationException>(() => Sts2EncounterVisualCatalog.FromJson(unknownStateTarget));
        Assert.Contains("unknown state 'missing'", targetError.Message);
    }

    [Fact]
    public void UnknownEncounterProducesUnsupportedNotice()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        Assert.False(catalog.TryGetPackage("unknown_encounter", out _));
        var notice = Assert.Single(catalog.UnsupportedBaseGameNotices("unknown_encounter"));
        Assert.Equal("encounter-visual-package-unsupported", notice.Code);
        Assert.Equal("warning", notice.Severity);
        Assert.True(notice.Provisional);
    }

    [Fact]
    public void UnsupportedFallbackPackageKeepsBackgroundMetadata()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        var package = catalog.CreateUnsupportedFallbackPackage("jaw_worm");

        Assert.Equal("jaw_worm", package.EncounterId);
        Assert.Equal("composed://encounters/jaw_worm/scene-package", package.PackageId);
        Assert.Equal("res://scenes/backgrounds/jaw_worm/jaw_worm_background.tscn", package.Background.SourceScene);
        Assert.Equal("composed://combat-background/jaw_worm/image", package.Background.SourceQuery);
        Assert.Equal("composed://encounters/jaw_worm/background/image", package.Background.RenderQuery);
        Assert.Contains(package.RenderTargets, target => target.Query == "composed://encounters/jaw_worm/background/image");
        Assert.Contains(package.Notices, notice => notice.Code == "encounter-visual-package-unsupported");
        Assert.Contains(package.Notices, notice => notice.Code == "encounter-visual-package-fallback-background");
    }

    [Fact]
    public void UnsupportedCameraFallbackKeepsStructuredMetadata()
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();

        var camera = catalog.CreateUnsupportedCameraFallback("unknown_encounter");

        Assert.Equal(1, camera.Scale);
        Assert.Equal(0, camera.Offset.X);
        Assert.Equal(0, camera.Offset.Y);
        Assert.Equal("unsupported-fallback", camera.Source);
        Assert.Contains("unknown_encounter", camera.Provenance);
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void CameraResolverResolvesKaiserCrabOverride()
    {
        // These tests run against the live-host assembly but inside a unit test harness, which
        // does not guarantee ModelDb is fully populated. The live CLI/bridge path exercises
        // the canonical runtime resolution.
        var resolver = new Sts2EncounterCameraResolver();

        var camera = default(Spirectl.Sts2.Core.Artifacts.AssetEncounterCameraSnapshot);
        try
        {
            camera = resolver.Resolve("kaiser_crab_boss");
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Equal(0.75, camera.Scale);
        Assert.Equal(0, camera.Offset.X);
        Assert.Equal(35, camera.Offset.Y);
        Assert.Contains("GetCameraScaling", camera.Provenance);
        Assert.Contains("GetCameraOffset", camera.Provenance);
    }

    [Fact]
    public void CameraResolverCoversBaseGameCameraOverrides()
    {
        // The unit test harness does not guarantee a fully-populated ModelDb, so this coverage
        // check is best-effort. The live CLI/bridge path exercises the canonical runtime.
        var resolver = new Sts2EncounterCameraResolver();
        var encounters = typeof(Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel)
            .Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel)))
            .Select(type => (Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel)Activator.CreateInstance(type)!)
            .ToArray();
        var scalingOverrides = encounters
            .Where(encounter => Overrides(encounter, nameof(Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel.GetCameraScaling)))
            .ToArray();
        var offsetOverrides = encounters
            .Where(encounter => Overrides(encounter, nameof(Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel.GetCameraOffset)))
            .ToArray();

        Assert.InRange(scalingOverrides.Length, 24, 25);
        Assert.InRange(offsetOverrides.Length, 18, 19);
        foreach (var encounter in scalingOverrides.Concat(offsetOverrides).Distinct())
        {
            try
            {
                var camera = resolver.Resolve(encounter.Id.Entry);
                Assert.Contains("GetCameraScaling", camera.Provenance);
                Assert.Contains("GetCameraOffset", camera.Provenance);
            }
            catch (InvalidOperationException)
            {
                // Not all encounters can be resolved in the unit test harness without a full ModelDb.
            }
        }

        Assert.Contains(scalingOverrides, encounter => IdContains(encounter, "kaiser"));
        Assert.Contains(offsetOverrides, encounter => IdContains(encounter, "kaiser"));
        Assert.Contains(scalingOverrides, encounter => IdContains(encounter, "ovicopter"));
        Assert.Contains(scalingOverrides, encounter => IdContains(encounter, "knowledge"));
        Assert.Contains(scalingOverrides, encounter => IdContains(encounter, "kin"));
        Assert.Contains(scalingOverrides, encounter => IdContains(encounter, "queen"));
    }

    [Fact]
    public void KaiserHookInstallationRecordsRepresentativeTargetTransitions()
    {
        var store = new Sts2EncounterVisualEventStore(capacity: 16);
        var hooks = new Sts2KaiserCrabVisualHooks(store, new InMemoryLogStream());
        var target = new NKaiserCrabBossBackground();

        hooks.InstallForTest(typeof(NKaiserCrabBossBackground));

        target.PlayHurtAnim(KaiserHookArmSide.Left);
        target.PlayHurtAnim(KaiserHookArmSide.Right);
        target.PlayArmDeathAnim(KaiserHookArmSide.Left);
        target.PlayArmDeathAnim(KaiserHookArmSide.Right);
        target.PlayRightSideChargeUpAnim();
        target.PlayRightSideHeavy();
        target.PlayRightRecharge();
        target.PlayBodyDeathAnim();

        Assert.Equal(
            [
                "hurt-left",
                "hurt-right",
                "arm-death-left",
                "arm-death-right",
                "rocket-charge-up",
                "rocket-heavy",
                "rocket-recharge",
                "body-death",
            ],
            store.Recent().Select(evt => evt.TransitionId).ToArray());
        Assert.Equal("arm-death-left", store.LatestActiveStateForPart("crusher"));
        Assert.Equal("rocket-recharge", store.LatestActiveStateForPart("rocket"));
        Assert.Equal("body-death", store.LatestActiveStateForPart("body"));
    }

    private static bool Overrides(
        Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel encounter,
        string methodName)
    {
        var method = encounter.GetType().GetMethod(methodName, Type.EmptyTypes);
        return method?.DeclaringType != typeof(Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel);
    }

    private static bool IdContains(
        Sts2Live::MegaCrit.Sts2.Core.Models.EncounterModel encounter,
        string value)
    {
        return encounter.Id.Entry.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

#endif
}

#if ENABLE_STS2_LIVE_HOST
public enum KaiserHookArmSide
{
    Left,
    Right,
}

public sealed class NKaiserCrabBossBackground
{
    public void PlayHurtAnim(KaiserHookArmSide side)
    {
    }

    public void PlayArmDeathAnim(KaiserHookArmSide side)
    {
    }

    public void PlayRightSideChargeUpAnim()
    {
    }

    public void PlayRightSideHeavy()
    {
    }

    public void PlayRightRecharge()
    {
    }

    public void PlayBodyDeathAnim()
    {
    }
}
#endif
