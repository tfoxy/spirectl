using Spirectl.Sts2;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Stage-A1 embedder contract pins: the CompositionSelector grammar (Sts2CombatBackgroundLayerSelection),
// the additive RenderWidth/RenderHeight/CompositionSelector request/snapshot fields (absent stays null —
// defaults regression pin), and the BridgeEmbeddableAssetProvider request->snapshot threading.
public sealed class Sts2CombatBackgroundLayerSelectionTests
{
    private const string Layer00C = "res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.tscn";
    private const string Layer01B = "res://scenes/backgrounds/test_bg/layers/test_bg_bg_01_b.tscn";
    private const string ForegroundC = "res://scenes/backgrounds/test_bg/layers/test_bg_fg_c.tscn";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectorParseTreatsNullAndWhitespaceAsAbsent(string? selector)
    {
        var parsed = Sts2CombatBackgroundLayerSelection.Parse(selector);

        Assert.False(parsed.IsExplicit);
        Assert.Null(parsed.Error);
        Assert.Empty(parsed.LayerPaths);
    }

    [Fact]
    public void SelectorParsePinsCommaSeparatedResPathGrammar()
    {
        var parsed = Sts2CombatBackgroundLayerSelection.Parse(
            $" {Layer00C} ,{Layer01B},, {ForegroundC}");

        Assert.True(parsed.IsExplicit);
        Assert.Null(parsed.Error);
        Assert.Equal([Layer00C, Layer01B, ForegroundC], parsed.LayerPaths);
    }

    [Fact]
    public void SelectorParseNormalizesBackslashesAndCollapsesDuplicates()
    {
        var parsed = Sts2CombatBackgroundLayerSelection.Parse(
            $"{Layer00C.Replace('/', '\\')},{Layer00C}");

        Assert.True(parsed.IsExplicit);
        Assert.Equal([Layer00C], parsed.LayerPaths);
    }

    [Theory]
    [InlineData(",,,")]
    [InlineData("scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.tscn")]
    [InlineData("res://scenes/backgrounds/test_bg/layers/test_bg_bg_00_c.png")]
    public void SelectorParseRejectsMalformedSelectors(string selector)
    {
        var parsed = Sts2CombatBackgroundLayerSelection.Parse(selector);

        Assert.False(parsed.IsExplicit);
        Assert.NotNull(parsed.Error);
        Assert.Empty(parsed.LayerPaths);
    }

    [Fact]
    public void FindUnhonoredLayerPathsIsEmptyWhenThePlanSelectedEveryRequestedPath()
    {
        var unhonored = Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(
            [Layer00C, ForegroundC],
            [Layer00C, Layer01B, ForegroundC, null, string.Empty]);

        Assert.Empty(unhonored);
    }

    [Fact]
    public void FindUnhonoredLayerPathsReportsRequestedPathsThePlanDidNotSelect()
    {
        var unhonored = Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(
            [Layer00C, Layer01B, ForegroundC],
            [Layer00C]);

        Assert.Equal([Layer01B, ForegroundC], unhonored);
    }

    [Fact]
    public void EmbeddableAssetRequestDefaultsKeepRenderSizeAndSelectorAbsent()
    {
        var request = new EmbeddableAssetRequest("composed://combat-background/test_bg/image");

        Assert.Null(request.RenderWidth);
        Assert.Null(request.RenderHeight);
        Assert.Null(request.CompositionSelector);
    }

    [Fact]
    public void AssetExtractRequestSnapshotDefaultsKeepRenderSizeAndSelectorAbsent()
    {
        var snapshot = new AssetExtractRequestSnapshot(
            "req-1",
            "composed",
            "composed://combat-background/test_bg/image",
            "composed://combat-background/test_bg/image",
            "png");

        Assert.Null(snapshot.RenderWidth);
        Assert.Null(snapshot.RenderHeight);
        Assert.Null(snapshot.CompositionSelector);
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderThreadsRenderSizeAndSelectorIntoSnapshot()
    {
        var extract = new CapturingExtractProvider();
        var provider = new BridgeEmbeddableAssetProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/test_bg/image",
            "png",
            "cbg-size",
            RenderWidth: 2520,
            RenderHeight: 1080,
            CompositionSelector: $"{Layer00C},{ForegroundC}"));

        Assert.True(result.Success);
        Assert.NotNull(extract.LastRequest);
        Assert.Equal(2520, extract.LastRequest!.RenderWidth);
        Assert.Equal(1080, extract.LastRequest.RenderHeight);
        Assert.Equal($"{Layer00C},{ForegroundC}", extract.LastRequest.CompositionSelector);
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderKeepsRenderSizeAndSelectorNullWhenAbsent()
    {
        var extract = new CapturingExtractProvider();
        var provider = new BridgeEmbeddableAssetProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/test_bg/image",
            "png",
            "cbg-default"));

        Assert.True(result.Success);
        Assert.NotNull(extract.LastRequest);
        Assert.Null(extract.LastRequest!.RenderWidth);
        Assert.Null(extract.LastRequest.RenderHeight);
        Assert.Null(extract.LastRequest.CompositionSelector);
    }

    private static BridgeRuntime CreateRuntime(IAssetExtractProvider extractProvider)
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractProvider = extractProvider,
            BridgeHost = scaffold.BridgeHost,
        });
    }

    private sealed class CapturingExtractProvider : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        public AssetExtractRequestSnapshot? LastRequest { get; private set; }

        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            LastRequest = request;
            return AssetExtractOperationResult.Success(
                request.RequestId,
                DataSourceKind.Live,
                false,
                request.OutputFormat,
                1,
                1,
                [1, 2, 3],
                "extract",
                []);
        }

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
            => AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                false,
                AssetExtractFailureCode.NotImplemented,
                "not implemented",
                []);
    }
}
