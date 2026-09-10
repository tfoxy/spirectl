using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.Protocol;

public sealed record BridgeCapabilitySnapshot(
    string Id,
    string Summary,
    bool Provisional,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record BridgeHandshakeRequest(
    string CliVersion,
    string RequestedSchemaVersion,
    string Mode,
    string TransportKind);

public sealed record BridgeBuildIdentitySnapshot(
    string BridgeSemVer,
    string BridgeVersion,
    string AssemblyInformationalVersion,
    string BuiltAtUtc);

public sealed record BridgeHandshakeSnapshot(
    string SchemaVersion,
    string GameVersion,
    string BridgeVersion,
    BridgeBuildIdentitySnapshot BuildIdentity,
    string TransportKind,
    RuntimeAttachmentState AttachmentState,
    DataSourceKind Source,
    bool Provisional,
    PlayerPerspective DefaultPerspective,
    IReadOnlyList<BridgeCapabilitySnapshot> Capabilities,
    IReadOnlyList<ActionDescriptorSnapshot> SupportedActions);

public sealed record BridgeTransportSnapshot(
    string TransportKind,
    RuntimeAttachmentState AttachmentState,
    DataSourceKind Source,
    bool Provisional);
