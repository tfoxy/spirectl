namespace Spirectl.Sts2.Embedding;

public sealed record EmbeddableRuntimeOptions(
    bool LiveSts2HostSupported = false,
    string? LiveSts2HostUnsupportedReason = LiveSts2HostUnsupportedReasons.NotALiveHostAdapter);
