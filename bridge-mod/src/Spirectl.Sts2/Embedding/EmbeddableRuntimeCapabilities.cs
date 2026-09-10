using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Embedding;

public sealed record EmbeddableRuntimeCapabilities(
    string SchemaVersion,
    string GameVersion,
    string BridgeVersion,
    string TransportKind,
    RuntimeAttachmentState AttachmentState,
    DataSourceKind Source,
    bool Provisional,
    IReadOnlyList<EmbeddableRuntimeCapability> Capabilities,
    IReadOnlyList<ActionDescriptorSnapshot> SupportedActions)
{
    /// <summary>
    /// The asset-payload shape version this runtime serves (<see cref="SpirectlSts2Runtime.AssetPayloadVersion"/>),
    /// surfaced on the capabilities record so an embedder can read it off the handshake it already performs.
    /// </summary>
    /// <remarks>
    /// A computed member rather than a constructor parameter, deliberately: it is a property of the ASSEMBLY, not
    /// of a particular capability probe, so there is nothing for a caller to pass and no way for a caller to pass
    /// something untrue. An embedder that caches asset bytes should include it in its cache key — a version it
    /// does not recognise means the bytes it holds were produced by a different payload shape.
    /// </remarks>
    public int AssetPayloadVersion => SpirectlSts2Runtime.AssetPayloadVersion;
}

public sealed record EmbeddableRuntimeCapability(
    string Id,
    string Summary,
    bool Supported,
    bool Provisional,
    string? UnsupportedReason);
