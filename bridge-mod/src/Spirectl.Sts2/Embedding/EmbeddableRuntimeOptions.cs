namespace Spirectl.Sts2.Embedding;

public sealed record EmbeddableRuntimeOptions(
    bool LiveSts2HostSupported = false,
    string? LiveSts2HostUnsupportedReason = "This runtime was not created from a live STS2 host adapter.");
