using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.EncounterVisuals;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class EncounterVisualEventStoreTests
{
    [Fact]
    public void RecordsTransitionEventsWithMonotonicSequence()
    {
        var store = new Sts2EncounterVisualEventStore(capacity: 4);

        var first = store.Record(
            "composed://encounters/kaiser_crab_boss/scene-package",
            "rocket-charge-up",
            ["rocket"],
            "rocket-charge-up",
            "NKaiserCrabBossBackground.PlayRightSideChargeUpAnim");
        var second = store.Record(
            "composed://encounters/kaiser_crab_boss/scene-package",
            "rocket-heavy",
            ["rocket"],
            "rocket-heavy",
            "NKaiserCrabBossBackground.PlayRightSideHeavy");

        Assert.Equal((ulong)1, first.Sequence);
        Assert.Equal((ulong)2, second.Sequence);
        Assert.Equal("rocket-heavy", store.LatestActiveStateForPart("rocket"));
    }

    [Fact]
    public void EvictsOldestEventsAtCapacity()
    {
        var store = new Sts2EncounterVisualEventStore(capacity: 2);

        store.Record("package", "hurt-left", ["crusher"], "hurt-left", "hook");
        store.Record("package", "rocket-charge-up", ["rocket"], "rocket-charge-up", "hook");
        store.Record("package", "body-death", ["body"], "body-death", "hook");

        var recent = store.Recent();
        Assert.Equal(2, recent.Count);
        Assert.Equal("rocket-charge-up", recent[0].TransitionId);
        Assert.Equal("body-death", recent[1].TransitionId);
        Assert.Null(store.LatestActiveStateForPart("crusher"));
    }

    [Fact]
    public void FiltersRecentEventsAndActiveStateByPackage()
    {
        var store = new Sts2EncounterVisualEventStore(capacity: 4);

        store.Record("composed://encounters/other/scene-package", "rocket-heavy", ["rocket"], "rocket-heavy", "hook");
        store.Record(
            "composed://encounters/kaiser_crab_boss/scene-package",
            "rocket-charge-up",
            ["rocket"],
            "rocket-charge-up",
            "hook");

        var recent = store.Recent("composed://encounters/kaiser_crab_boss/scene-package");

        Assert.Single(recent);
        Assert.Equal("rocket-charge-up", recent[0].TransitionId);
        Assert.Equal(
            "rocket-charge-up",
            store.LatestActiveStateForPart("composed://encounters/kaiser_crab_boss/scene-package", "rocket"));
        Assert.Equal(
            "rocket-heavy",
            store.LatestActiveStateForPart("composed://encounters/other/scene-package", "rocket"));
        Assert.Null(store.LatestActiveStateForPart("composed://encounters/missing/scene-package", "rocket"));
    }

    [Fact]
    public void BuildsKaiserCombatVisualStateFromCatalogAndRecentEvents()
    {
        var providerType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        if (providerType is null)
        {
            return;
        }

        var store = new Sts2EncounterVisualEventStore(capacity: 4);
        store.Record(
            "composed://encounters/other/scene-package",
            "rocket-heavy",
            ["rocket"],
            "rocket-heavy",
            "OtherHook");
        store.Record(
            "composed://encounters/kaiser_crab_boss/scene-package",
            "rocket-charge-up",
            ["rocket"],
            "rocket-charge-up",
            "NKaiserCrabBossBackground.PlayRightSideChargeUpAnim");
        var notices = new List<StateNoticeSnapshot>();

        var state = ResolveEncounterVisuals(providerType, "kaiser_crab_boss", store, notices);

        Assert.NotNull(state);
        Assert.Equal("composed://encounters/kaiser_crab_boss/scene-package", state.PackageId);
        Assert.Equal("rocket-charge-up", state.VisualParts.Single(part => part.PartId == "rocket").ActiveStateId);
        Assert.Contains(state.VisualParts, part => part.PartId == "crusher" && part.ActorId == "crusher");
        Assert.Contains(state.VisualParts, part => part.PartId == "body" && part.ScreenSide == "center");
        Assert.Equal("rocket-charge-up", state.RecentEvents.Single().TransitionId);
        Assert.Contains(notices, notice => notice.Code == "encounter-visuals-provisional");
    }

    [Fact]
    public void OmitsUnsupportedEncounterVisualState()
    {
        var providerType = typeof(Sts2ActionCatalog).Assembly
            .GetType("Spirectl.Sts2.Live.Sts2RuntimeObservationProvider");
        if (providerType is null)
        {
            return;
        }

        var notices = new List<StateNoticeSnapshot>();

        var state = ResolveEncounterVisuals(providerType, "jaw_worm", new Sts2EncounterVisualEventStore(), notices);

        Assert.Null(state);
        var notice = Assert.Single(notices);
        Assert.Equal("encounter-visuals-unsupported", notice.Code);
        Assert.Equal("combat.encounterVisuals", notice.Path);
        Assert.Equal("unsupported", notice.Severity);
        Assert.Equal("stable", notice.Stability);
    }

    private static EncounterVisualsStateSnapshot? ResolveEncounterVisuals(
        Type providerType,
        string encounterId,
        Sts2EncounterVisualEventStore store,
        List<StateNoticeSnapshot> notices)
    {
        var method = providerType.GetMethod(
            "ResolveEncounterVisuals",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not find ResolveEncounterVisuals.");
        return (EncounterVisualsStateSnapshot?)method.Invoke(null, [encounterId, store, notices]);
    }
}
