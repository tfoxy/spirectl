namespace Spirectl.Sts2.Embedding;

public interface ISpirectlAssetProvider
{
    EmbeddableAssetResult GetAsset(EmbeddableAssetRequest request);

    EmbeddableAssetBatchResult GetAssets(EmbeddableAssetBatchRequest request);
}
