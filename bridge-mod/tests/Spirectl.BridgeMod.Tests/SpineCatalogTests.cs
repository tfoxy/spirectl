using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Protocol;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class SpineCatalogTests
{
    [Fact]
    public void NormalizerDeduplicatesFiltersHelpersAndSortsCanonicalClips()
    {
        var entries = SpineCatalogNormalizer.Normalize(
        [
            new SpineCatalogCandidate("scenes\\b.tscn", "./Visuals\\Spine", ["zeta", "_ignore/setup", "idle"]),
            new SpineCatalogCandidate("res://scenes/a.tscn", null, ["walk", "idle", "walk"]),
            new SpineCatalogCandidate("res://scenes/b.tscn", "Visuals/Spine", ["idle", "zeta"]),
        ]);

        Assert.Equal(
            [
                new SpineCatalogEntrySnapshot("res://scenes/a.tscn", null, "idle"),
                new SpineCatalogEntrySnapshot("res://scenes/a.tscn", null, "walk"),
                new SpineCatalogEntrySnapshot("res://scenes/b.tscn", "Visuals/Spine", "idle"),
                new SpineCatalogEntrySnapshot("res://scenes/b.tscn", "Visuals/Spine", "zeta"),
            ],
            entries);
    }

    [Fact]
    public void CatalogResultRetainsStructuredDiscoveryFailuresAndClipCount()
    {
        var result = SpineCatalogOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            scannedSceneCount: 4,
            spineNodeCount: 2,
            entries: [new SpineCatalogEntrySnapshot("res://a.tscn", null, "idle")],
            failures: [new SpineCatalogFailureSnapshot("res://broken.tscn", "scene-load-failed", "malformed scene")],
            notes: ["test"]);

        Assert.Equal(1, result.ClipCount);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("scene-load-failed", failure.Code);
        Assert.Equal("res://broken.tscn", failure.ResourcePath);
    }
}
