using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class NarrowAssetConsumerTests
{
    [Fact]
    public void ExtractionOnlyRuntimeDoesNotAdvertiseUnprovidedSpineOperations()
    {
        var extractor = new ExtractOnly();
        using var facade = EmbeddedRuntimeTestFactory.CreateExtractionOnly(extractor);
        var capabilities = facade.GetCapabilities().Capabilities.ToDictionary(item => item.Id);
        Assert.True(capabilities[EmbeddableCapabilityIds.AssetExtraction].Supported);
        Assert.False(capabilities[EmbeddableCapabilityIds.SpineCatalog].Supported);
        Assert.False(capabilities[EmbeddableCapabilityIds.SpineGeoClipBake].Supported);
        facade.Assets.GetAsset(new EmbeddableAssetRequest("res://test.png", "png", "narrow-extract"));
        Assert.Equal("narrow-extract", extractor.Request?.RequestId);
    }

    [Fact]
    public void AssetAdapterNeedsOnlyExtractionAndPreservesFailureMapping()
    {
        var extractor = new ExtractOnly();
        var provider = new BridgeEmbeddableAssetProvider(extractor);
        var result = provider.GetAsset(new EmbeddableAssetRequest("res://test.png", "png", "adapter"));
        Assert.NotNull(extractor.Request);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("key", result.Error.Field);
        Assert.Equal("res://test.png", result.Error.Value);
    }

    // Intentionally cannot explain, enumerate, bake, or observe state.
    private sealed class ExtractOnly : IAssetExtractor
    {
        public AssetExtractRequestSnapshot? Request;
        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            Request = request;
            return new PlaceholderAssetExtractProvider().Extract(request);
        }
    }
}
