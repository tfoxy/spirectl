namespace Spirectl.Sts2.Embedding;

public sealed class PlaceholderEmbeddableAssetProvider : ISpirectlAssetProvider
{
    public EmbeddableAssetResult GetAsset(EmbeddableAssetRequest request)
        => new(false, null, new EmbeddableAssetError(
            "unsupported-asset-provider",
            "The embeddable runtime does not have a live asset provider.",
            "key",
            request.Key,
            [new EmbeddableAssetNotice("asset-provider-unavailable", "info", "Provide a host runtime with asset extraction support.")]));

    public EmbeddableAssetBatchResult GetAssets(EmbeddableAssetBatchRequest request)
        => new("failed", request.Requests.Select(GetAsset).ToArray());
}
