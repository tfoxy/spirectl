#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2AssetOperationServiceTests
{
    [Fact]
    public void ExplanationServicePreservesTheOperationRequestAtItsRenderSeam()
    {
        Sts2MainThreadDispatcher.ResetForTests();
        var context = new CapturingExplanationContext();
        var service = new Sts2AssetExplanationService(new InMemoryLogStream(), context);
        var request = new AssetExplainRequestSnapshot("request-7", "virtual", "source", "load");

        var result = service.Explain(request);

        Assert.Same(request, context.Request);
        Assert.NotNull(result.Error);
        Assert.Equal("request-7", result.RequestId);
    }

    [Fact]
    public void ExplanationServiceTurnsRenderFailuresIntoTheStableLiveHostFailure()
    {
        Sts2MainThreadDispatcher.ResetForTests();
        var service = new Sts2AssetExplanationService(new InMemoryLogStream(), new ThrowingExplanationContext());

        var result = service.Explain(new AssetExplainRequestSnapshot("request-8", "virtual", "source", "load"));

        Assert.NotNull(result.Error);
        Assert.Equal(AssetExtractFailureCode.RuntimeFailure, result.Error!.Code);
        Assert.Contains(result.Error.Details, detail => detail.Field == "live_host" && detail.Value == "main-thread-explain-failed");
    }

    private sealed class CapturingExplanationContext : IAssetExplanationRenderContext
    {
        public AssetExplainRequestSnapshot? Request { get; private set; }
        public Task<AssetExplainOperationResult> ExplainOnMainThreadAsync(AssetExplainRequestSnapshot request)
        {
            Request = request;
            return Task.FromResult(AssetExplainOperationResult.Failure(request.RequestId, DataSourceKind.Live, false, AssetExtractFailureCode.NotImplemented, "expected", []));
        }
    }

    private sealed class ThrowingExplanationContext : IAssetExplanationRenderContext
    {
        public Task<AssetExplainOperationResult> ExplainOnMainThreadAsync(AssetExplainRequestSnapshot request)
            => Task.FromException<AssetExplainOperationResult>(new InvalidOperationException("render failure"));
    }
}

#endif
