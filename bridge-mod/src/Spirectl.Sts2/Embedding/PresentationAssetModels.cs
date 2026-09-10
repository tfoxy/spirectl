namespace Spirectl.Sts2.Embedding;

public sealed record PresentationAssetBatchRequest(
    IReadOnlyList<string> Keys,
    string Format = "auto",
    bool FailFast = false);
