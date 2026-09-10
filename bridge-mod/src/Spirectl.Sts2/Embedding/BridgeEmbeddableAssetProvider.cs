using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Protocol;
using System.Text;

namespace Spirectl.Sts2.Embedding;

public sealed class BridgeEmbeddableAssetProvider(IAssetExtractor extractor) : ISpirectlAssetProvider
{
    public EmbeddableAssetResult GetAsset(EmbeddableAssetRequest request)
    {
        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? $"asset:{Guid.NewGuid():N}"
            : request.RequestId!;
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return Failure("invalid-asset-key", "Asset key must be non-empty.", "key", request.Key ?? string.Empty, requestId);
        }

        var resolved = ResolveKey(request.Key);
        var operation = extractor.Extract(new AssetExtractRequestSnapshot(
            requestId,
            resolved.SourceRoot,
            resolved.SourcePath,
            resolved.LoadPath,
            NormalizeFormat(request.Format),
            request.Timeout,
            RenderWidth: request.RenderWidth,
            RenderHeight: request.RenderHeight,
            CompositionSelector: request.CompositionSelector,
            ImageQuality: request.ImageQuality,
            ImageOpaque: request.ImageOpaque,
            EventBackgroundFrame: request.EventBackgroundFrame));

        return MapResult(request.Key, operation, resolved.SourceRoot, resolved.SourcePath, resolved.LoadPath, NormalizeFormat(request.Format));
    }

    public EmbeddableAssetBatchResult GetAssets(EmbeddableAssetBatchRequest request)
    {
        var results = new List<EmbeddableAssetResult>(request.Requests.Count);
        foreach (var asset in request.Requests)
        {
            var result = GetAsset(asset);
            results.Add(result);
            if (request.FailFast && !result.Success)
            {
                foreach (var skipped in request.Requests.Skip(results.Count))
                {
                    results.Add(new EmbeddableAssetResult(
                        false,
                        null,
                        new EmbeddableAssetError(
                            "asset-request-skipped",
                            "Request skipped because fail-fast stopped the batch.",
                            "key",
                            skipped.Key,
                            [new EmbeddableAssetNotice("asset-request-skipped", "info", "Fail-fast skipped this request.")])));
                }

                var failFastStatus = results.Any(static item => item.Success) ? "partial" : "failed";
                return new EmbeddableAssetBatchResult(failFastStatus, results);
            }
        }

        var status = results.All(static result => result.Success)
            ? "ok"
            : results.Any(static result => result.Success) ? "partial" : "failed";
        return new EmbeddableAssetBatchResult(status, results);
    }

    private static EmbeddableAssetResult Failure(string code, string message, string field, string value, string requestId)
        => new(false, null, new EmbeddableAssetError(code, message, field, value, [
            new EmbeddableAssetNotice("embeddable_asset_request", "error", message, Path: field),
            new EmbeddableAssetNotice("embeddable_asset_request_id", "info", $"requestId={requestId}")
        ]));

    public static string NormalizeFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? "auto" : format.Trim().ToLowerInvariant();

    public static string MapFailureCode(AssetExtractFailureCode code)
        => code switch
        {
            AssetExtractFailureCode.NotImplemented => "not-implemented",
            AssetExtractFailureCode.BridgeNotAttached => "bridge-not-attached",
            AssetExtractFailureCode.RuntimeFailure => "runtime-failure",
            _ => "asset-extract-failed",
        };

    public static EmbeddableAssetError MapFailure(
        string key,
        AssetExtractFailure failure,
        string detailNoticeCode = "asset-extract-detail")
        => new(
            FailureCodeWithStructuredDetail(failure),
            failure.Message,
            "key",
            key,
            failure.Details.Select(detail =>
                new EmbeddableAssetNotice(
                    detailNoticeCode,
                    "error",
                    FormatDetailNoticeMessage(detail),
                    detail.Field)).ToArray());

    private static string FailureCodeWithStructuredDetail(AssetExtractFailure failure)
        => failure.Details.Any(detail => string.Equals(detail.Field, "font-bytes-unavailable", StringComparison.Ordinal))
            ? "font-bytes-unavailable"
            : MapFailureCode(failure.Code);

    public static EmbeddableAssetResult MapResult(
        string key,
        AssetExtractOperationResult operation,
        string fallbackSourceRoot = "virtual",
        string fallbackSourcePath = "",
        string fallbackLoadPath = "",
        string fallbackFormat = "auto")
    {
        if (operation.Error is not null)
        {
            return new EmbeddableAssetResult(false, null, MapFailure(key, operation.Error));
        }

        var format = NormalizeFormat(string.IsNullOrWhiteSpace(operation.Format) ? fallbackFormat : operation.Format);
        var frames = operation.Frames.Select(frame => new EmbeddableAssetFrame(
            frame.Index,
            NormalizeFormat(frame.Format),
            frame.ContentType,
            frame.Width,
            frame.Height,
            frame.Contents,
            frame.DurationMs,
            frame.OffsetX,
            frame.OffsetY,
            frame.CanvasWidth,
            frame.CanvasHeight)).ToArray();

        var contents = operation.ArtifactKind == AssetExtractArtifactKind.Timeline && operation.Contents.Length == 0
            ? BuildTimelineManifestBytes(format, operation.Frames.Count)
            : operation.Contents;

        var payload = new EmbeddableAssetPayload(
            operation.RequestId,
            key,
            operation.ArtifactKind.ToString().ToLowerInvariant(),
            format,
            operation.ArtifactKind == AssetExtractArtifactKind.Timeline
            && (string.IsNullOrWhiteSpace(operation.ContentType) || operation.ContentType == "application/octet-stream")
                ? "application/json"
                : operation.ContentType,
            operation.Width,
            operation.Height,
            contents,
            frames,
            new EmbeddableAssetProvenance(
                string.IsNullOrWhiteSpace(operation.Provenance.SourceRoot) ? fallbackSourceRoot : operation.Provenance.SourceRoot,
                string.IsNullOrWhiteSpace(operation.Provenance.SourcePath) ? fallbackSourcePath : operation.Provenance.SourcePath,
                string.IsNullOrWhiteSpace(operation.Provenance.LoadPath) ? fallbackLoadPath : operation.Provenance.LoadPath,
                string.IsNullOrWhiteSpace(operation.Provenance.RenderMode) ? operation.RenderMode : operation.Provenance.RenderMode,
                string.IsNullOrWhiteSpace(operation.Provenance.SourceKind) ? operation.Source.ToString().ToLowerInvariant() : operation.Provenance.SourceKind),
            operation.Notices.Select(notice => new EmbeddableAssetNotice(notice.Code, notice.Severity, notice.Message, notice.Path))
                .Concat(operation.Notes.Select(note => new EmbeddableAssetNotice("asset_extract_note", "info", note)))
                .ToArray())
        {
            DurationMs = operation.DurationMs,
            ExtractionMs = operation.ExtractionMs,
            ClipLocalX = operation.ClipPlacement?.LocalX ?? 0,
            ClipLocalY = operation.ClipPlacement?.LocalY ?? 0,
            ClipLocalWidth = operation.ClipPlacement?.LocalWidth ?? 0,
            ClipLocalHeight = operation.ClipPlacement?.LocalHeight ?? 0,
        };

        return new EmbeddableAssetResult(true, payload, null);
    }

    private static ResolvedAssetKey ResolveKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedAssetKey("resources", trimmed, trimmed);
        }
        if (trimmed.StartsWith("model://", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedAssetKey("model", trimmed, trimmed);
        }

        if (trimmed.StartsWith("composed://", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedAssetKey("composed", trimmed, trimmed);
        }

        return new ResolvedAssetKey("virtual", trimmed, trimmed);
    }

    private static byte[] BuildTimelineManifestBytes(string format, int frameCount)
        => Encoding.UTF8.GetBytes($"{{\"artifactKind\":\"timeline\",\"format\":\"{format}\",\"frameCount\":{frameCount}}}");

    private static string FormatDetailNoticeMessage(AssetExtractDetail detail)
    {
        var message = string.IsNullOrWhiteSpace(detail.Note) ? "Asset extraction failed." : detail.Note;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.Field))
        {
            parts.Add($"field={detail.Field}");
        }

        if (!string.IsNullOrWhiteSpace(detail.Value))
        {
            parts.Add($"value={detail.Value}");
        }

        if (detail.Diagnostic is { Count: > 0 })
        {
            parts.Add($"diagnostic={string.Join(",", detail.Diagnostic.Keys.Order(StringComparer.Ordinal))}");
        }

        return parts.Count == 0 ? message : $"{message} ({string.Join("; ", parts)})";
    }

    private sealed record ResolvedAssetKey(string SourceRoot, string SourcePath, string LoadPath);
}
