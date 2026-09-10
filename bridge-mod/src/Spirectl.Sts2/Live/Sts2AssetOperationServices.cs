using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;

/// <summary>Rendering seam shared with the extractor; services own operation dispatch and contracts.</summary>
internal interface IAssetExplanationRenderContext
{
    Task<AssetExplainOperationResult> ExplainOnMainThreadAsync(AssetExplainRequestSnapshot request);
}

internal sealed class Sts2AssetExplanationService(ILogStream log, IAssetExplanationRenderContext renderContext)
    : IAssetExplainer
{
    public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request)
    {
        try { return Sts2MainThreadDispatcher.InvokeAsync(() => renderContext.ExplainOnMainThreadAsync(request)).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            log.Write(BridgeLogLevel.Error, "bridge.assets", $"Asset explain '{request.SourcePath}' failed: {ex}");
            return AssetExplainOperationResult.Failure(request.RequestId, DataSourceKind.Live, false, AssetExtractFailureCode.RuntimeFailure,
                "The live bridge failed while explaining the requested asset composition.",
                [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message), new AssetExtractDetail("live_host", "main-thread-explain-failed", "Live asset explanation must run inside the attached STS2 live host on the Godot main thread.")]);
        }
    }
}

/// <summary>Owns the blocking main-thread dispatch and failure contract for the general model catalog.</summary>
internal sealed class Sts2AssetCatalogService(ILogStream log)
    : IAssetCatalogProvider
{
    public AssetCatalogOperationResult Catalog(AssetCatalogRequestSnapshot request)
    {
        try { return Sts2MainThreadDispatcher.Invoke(() => CatalogOnMainThread(request)); }
        catch (Exception ex)
        {
            log.Write(BridgeLogLevel.Error, "bridge.assets", $"Asset catalog enumeration failed: {ex}");
            return AssetCatalogOperationResult.Failure(DataSourceKind.Live, false, request.Family, AssetExtractFailureCode.RuntimeFailure,
                "The live bridge failed while enumerating the asset catalog.",
                [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message), new AssetExtractDetail("live_host", "main-thread-catalog-failed", "Live asset catalog enumeration must run inside the attached STS2 live host on the Godot main thread.")]);
        }
    }

    private static AssetCatalogOperationResult CatalogOnMainThread(AssetCatalogRequestSnapshot request)
    {
        var family = string.IsNullOrWhiteSpace(request.Family) ? "all" : request.Family.Trim().ToLowerInvariant();
        var entries = new List<AssetCatalogEntrySnapshot>();
        AddModelEntries(entries, family, "cards", "image", "PortraitPath/PortraitPngPath/AllPortraitPaths/BetaPortraitPath", "flattened-static", ModelDb.AllCards, Sts2AssetModelResourceResolver.TryExtractCardPath);
        AddModelEntries(entries, family, "relics", "icon", "IconPath/PackedIconPath", "flattened-static", ModelDb.AllRelics, Sts2AssetModelResourceResolver.TryExtractRelicIconPath);
        AddModelEntries(entries, family, "relics", "iconOutline", "IconOutline", "flattened-relic-model-iconOutline", ModelDb.AllRelics, _ => null);
        AddModelEntries(entries, family, "relics", "bigIcon", "BigIcon", "flattened-relic-model-bigIcon", ModelDb.AllRelics, _ => null);
        AddModelEntries(entries, family, "potions", "icon", "ImagePath/PackedImagePath", "flattened-static", ModelDb.AllPotions, Sts2AssetModelResourceResolver.TryExtractPotionPath);
        AddModelEntries(entries, family, "potions", "outline", "OutlinePath", "flattened-static", ModelDb.AllPotions, model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "OutlinePath"));
        AddModelEntries(entries, family, "characters", "icon", "IconTexture", "flattened-character-model-icon", ModelDb.AllCharacters, _ => null);
        AddModelEntries(entries, family, "characters", "iconOutline", "IconOutlineTexture", "flattened-character-model-iconOutline", ModelDb.AllCharacters, _ => null);
        AddModelEntries(entries, family, "characters", "characterSelectIcon", "CharacterSelectIcon", "flattened-character-model-characterSelectIcon", ModelDb.AllCharacters, _ => null);
        AddModelEntries(entries, family, "characters", "characterSelectLockedIcon", "CharacterSelectLockedIcon", "flattened-character-model-characterSelectLockedIcon", ModelDb.AllCharacters, _ => null);
        AddModelEntries(entries, family, "characters", "mapMarker", "MapMarker", "flattened-character-model-mapMarker", ModelDb.AllCharacters, _ => null);
        AddModelEntries(entries, family, "characters", "characterSelectBg", "CharacterSelectBg", "flattened-character-select-background", ModelDb.AllCharacters, model => Sts2AssetModelResourceResolver.ResolveAssetModelId(model) is { } id ? Sts2AssetModelResourceResolver.CharacterSelectBackgroundPath(model, id) : null);
        AddModelEntries(entries, family, "characters", "energyCounter", "EnergyCounterPath", "flattened-scene", ModelDb.AllCharacters, model => model.EnergyCounterPath);
        AddModelEntries(entries, family, "characters", "merchantAnim", "MerchantAnimPath", "flattened-scene", ModelDb.AllCharacters, model => model.MerchantAnimPath);
        AddModelEntries(entries, family, "characters", "restSiteAnim", "RestSiteAnimPath", "flattened-scene", ModelDb.AllCharacters, model => model.RestSiteAnimPath);
        AddModelEntries(entries, family, "characters", "visuals", "Visuals", "flattened-character-model-battlefield", ModelDb.AllCharacters, _ => "CharacterModel.CreateVisuals()");
        AddModelEntries(entries, family, "monsters", "visuals", "VisualsPath", "flattened-scene", Sts2ModelResolver.AllMonsters(), Sts2AssetModelResourceResolver.TryExtractMonsterVisualPath);
        AddModelEntries(entries, family, "events", "backgroundScene", "BackgroundScenePath", "flattened-event-background-scene", ModelDb.AllEvents.Cast<object>().Concat(ModelDb.AllAncients.Cast<object>()), model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "BackgroundScenePath"));
        AddModelEntries(entries, family, "events", "initialPortrait", "InitialPortraitPath", "flattened-static", ModelDb.AllEvents.Cast<object>().Concat(ModelDb.AllAncients.Cast<object>()), model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "InitialPortraitPath"));
        AddModelEntries(entries, family, "events", "mapIcon", "MapIconPath", "flattened-static", ModelDb.AllAncients.Cast<object>(), model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "MapIconPath"));
        AddModelEntries(entries, family, "events", "mapIconOutline", "MapIconOutlinePath", "flattened-static", ModelDb.AllAncients.Cast<object>(), model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "MapIconOutlinePath"));
        AddModelEntries(entries, family, "events", "runHistoryIcon", "RunHistoryIcon", "flattened-static", ModelDb.AllAncients.Cast<object>(), model => Sts2AssetModelResourceResolver.TryExtractResourcePathProperty(model, "RunHistoryIcon"));
        AddModelEntries(entries, family, "events", "runHistoryIconOutline", "RunHistoryIconOutlinePath", "flattened-static", ModelDb.AllAncients.Cast<object>(), model => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "RunHistoryIconOutlinePath"));
        return AssetCatalogOperationResult.Success(DataSourceKind.Live, false, request.Family, [.. entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)], ["Concrete model-backed ids were enumerated from the live game's ModelDb catalogs."]);
    }

    private static void AddModelEntries<TModel>(List<AssetCatalogEntrySnapshot> entries, string family, string modelType, string assetKind, string modelProperty, string renderMode, IEnumerable<TModel> models, Func<TModel, string?> sourcePath)
    {
        if (family is not ("all" or "model") && !string.Equals(family, $"model:{modelType}", StringComparison.Ordinal)) return;
        foreach (var model in models)
        {
            var modelId = Sts2AssetModelResourceResolver.ResolveAssetModelId(model);
            if (string.IsNullOrWhiteSpace(modelId)) continue;
            var segment = ModelTypePathSegment(modelType);
            entries.Add(new AssetCatalogEntrySnapshot($"model://{segment}/{modelId}/{assetKind}", $"model://{segment}/<id>/{assetKind}", "model", modelType, modelId, modelProperty, sourcePath(model), renderMode, []));
        }
    }

    private static string ModelTypePathSegment(string modelType) => modelType switch { "card" or "cards" => "cards", "relic" or "relics" => "relics", "potion" or "potions" => "potions", "character" or "characters" => "characters", "monster" or "monsters" => "monsters", "event" or "events" => "events", "encounter" or "encounters" => "encounters", _ => modelType };
}
