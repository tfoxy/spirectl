using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.BridgeMod.Tests;

/// <summary>Explicit narrow-port fallbacks for aggregate test doubles that only exercise extract/explain.</summary>
public abstract class AssetOperationFallbackPorts : IAssetCatalogProvider, ISpineCatalogProvider, ISpineGeoClipBaker
{
    public AssetCatalogOperationResult Catalog(AssetCatalogRequestSnapshot request)
        => AssetCatalogOperationResult.Failure(DataSourceKind.Stub, true, request.Family, AssetExtractFailureCode.NotImplemented, "catalog is not implemented by this test double.", []);

    public SpineCatalogOperationResult CatalogSpines(SpineCatalogRequestSnapshot request)
        => SpineCatalogOperationResult.Failure(DataSourceKind.Stub, true, AssetExtractFailureCode.NotImplemented, "Spine catalog is not implemented by this test double.", []);

    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request)
        => SpineGeoClipBakeResultSnapshot.Failure(AssetExtractFailureCode.NotImplemented, "Spine geoclip baking is not implemented by this test double.", []);
}
