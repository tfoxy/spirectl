using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Artifacts;

public sealed class PlaceholderAssetExtractProvider : IAssetExtractProvider
{
    public SpineCatalogOperationResult CatalogSpines(SpineCatalogRequestSnapshot request)
        => SpineCatalogOperationResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            code: AssetExtractFailureCode.NotImplemented,
            message: "live Spine catalog enumeration requires the live STS2 bridge host.",
            details: [new AssetExtractDetail("capability", "spine-catalog", "Mock transport does not enumerate live Spine scenes.")]);

    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request)
        => SpineGeoClipBakeResultSnapshot.NotSupported("This asset provider does not implement live Spine geoclip baking.");

    public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
    {
        return AssetExtractOperationResult.Failure(
            requestId: request.RequestId,
            source: DataSourceKind.Stub,
            provisional: true,
            code: AssetExtractFailureCode.NotImplemented,
            message: "live asset extraction requires the live STS2 bridge host.",
            details:
            [
                new AssetExtractDetail(
                    Field: "command",
                    Value: "assets extract",
                    Note: $"Retry '{request.SourcePath}' through transport.kind=ipc after launching or attaching the live bridge."),
            ]);
    }

    public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
    {
        return AssetExplainOperationResult.Failure(
            requestId: request.RequestId,
            source: DataSourceKind.Stub,
            provisional: true,
            code: AssetExtractFailureCode.NotImplemented,
            message: "asset composition explanation requires the live STS2 bridge host.",
            details:
            [
                new AssetExtractDetail(
                    Field: "command",
                    Value: "assets explain",
                    Note: $"Mock transport cannot explain '{request.SourcePath}'; retry through transport.kind=ipc after launching or attaching the live bridge."),
            ]);
    }

    public AssetCatalogOperationResult Catalog(AssetCatalogRequestSnapshot request)
    {
        return AssetCatalogOperationResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            family: request.Family,
            code: AssetExtractFailureCode.NotImplemented,
            message: "live asset catalog enumeration requires the live STS2 bridge host.",
            details:
            [
                new AssetExtractDetail(
                    Field: "command",
                    Value: "assets catalog",
                    Note: "Mock transport cannot enumerate live ModelDb-backed asset ids; retry through transport.kind=ipc after launching or attaching the live bridge."),
            ]);
    }
}
