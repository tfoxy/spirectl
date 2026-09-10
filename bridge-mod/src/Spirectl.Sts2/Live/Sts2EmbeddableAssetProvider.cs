using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Embedding;
using System.Text;
using System.Text.Json;

namespace Spirectl.Sts2.Live;

public sealed class Sts2EmbeddableAssetProvider(IAssetExtractor extractor, IAssetExplainer explainer) : ISpirectlAssetProvider
{
    private readonly Sts2AssetKeyResolver _resolver = new(Sts2AssetProviderCatalog.LoadDefault());

    public EmbeddableAssetResult GetAsset(EmbeddableAssetRequest request)
    {
        var key = request.Key?.Trim() ?? string.Empty;
        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? $"asset:{Guid.NewGuid():N}"
            : request.RequestId!;
        var resolved = _resolver.Resolve(key);
        if (!resolved.IsSuccess)
        {
            var notices = new[] { new EmbeddableAssetNotice(resolved.ErrorCode, "error", resolved.ErrorMessage) };
            return new EmbeddableAssetResult(false, null, new EmbeddableAssetError(resolved.ErrorCode, resolved.ErrorMessage, "key", key, notices));
        }

        if (key.StartsWith("composed://encounters/", StringComparison.Ordinal) && key.EndsWith("/scene-package", StringComparison.Ordinal))
        {
            var explain = explainer.Explain(new AssetExplainRequestSnapshot(requestId, resolved.SourceRoot, resolved.SourcePath, resolved.LoadPath));
            if (explain.Error is not null)
            {
                return new EmbeddableAssetResult(
                    false,
                    null,
                    BridgeEmbeddableAssetProvider.MapFailure(key, explain.Error, "asset-explain-detail"));
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(explain.EncounterScenePackage);
            var sceneProvenance = new EmbeddableAssetProvenance(
                resolved.SourceRoot,
                resolved.SourcePath,
                resolved.LoadPath,
                "scene-package",
                "encounter");
            var notices = explain.EncounterScenePackage?.Notices.Select(notice =>
                new EmbeddableAssetNotice(notice.Code, notice.Severity, notice.Message, notice.Path)).ToArray() ?? [];
            return new EmbeddableAssetResult(true, new EmbeddableAssetPayload(requestId, key, "metadata", "json", "application/json", 0, 0, bytes, [], sceneProvenance, notices), null);
        }

        var normalizedFormat = BridgeEmbeddableAssetProvider.NormalizeFormat(request.Format);
        var extract = extractor.Extract(new AssetExtractRequestSnapshot(
            requestId,
            resolved.SourceRoot,
            resolved.SourcePath,
            resolved.LoadPath,
            normalizedFormat,
            RenderWidth: request.RenderWidth,
            RenderHeight: request.RenderHeight,
            CompositionSelector: request.CompositionSelector,
            ImageQuality: request.ImageQuality,
            ImageOpaque: request.ImageOpaque));
        return BridgeEmbeddableAssetProvider.MapResult(
            key,
            extract,
            resolved.SourceRoot,
            resolved.SourcePath,
            resolved.LoadPath,
            normalizedFormat);
    }

    public EmbeddableAssetBatchResult GetAssets(EmbeddableAssetBatchRequest request)
    {
        if (request.Requests.Count == 0)
        {
            return new EmbeddableAssetBatchResult("ok", []);
        }

        var results = new List<EmbeddableAssetResult>(request.Requests.Count);
        var hasSuccess = false;
        var hasFailure = false;

        for (var i = 0; i < request.Requests.Count; i++)
        {
            var result = GetAsset(request.Requests[i]);
            results.Add(result);
            if (result.Success)
            {
                hasSuccess = true;
            }
            else
            {
                hasFailure = true;
                if (request.FailFast)
                {
                    for (var j = i + 1; j < request.Requests.Count; j++)
                    {
                        var skipped = request.Requests[j];
                        var notices = new[] { new EmbeddableAssetNotice("asset-request-skipped", "info", "Skipped because fail-fast halted batch extraction.") };
                        results.Add(new EmbeddableAssetResult(false, null, new EmbeddableAssetError("asset-request-skipped", "Skipped because fail-fast halted batch extraction.", "key", skipped.Key, notices)));
                    }
                    break;
                }
            }
        }

        var status = hasFailure
            ? (hasSuccess ? "partial" : "failed")
            : "ok";
        return new EmbeddableAssetBatchResult(status, results);
    }
}
