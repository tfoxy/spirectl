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

// The last three fields say which STS2 game build this payload was compiled for. Empty means "this
// payload cannot claim one" — a released payload compiles against the declaration-only reference SDK,
// which has no release_info.json and no game hash, so it can only claim its lane. See BridgeBuildInfo.
public sealed record BridgeBuildIdentitySnapshot(
    string BridgeSemVer,
    string BridgeVersion,
    string AssemblyInformationalVersion,
    string BuiltAtUtc,
    string Sts2ApiLane,
    string BuiltAgainstGameVersion,
    string BuiltAgainstMainAssemblyHash);

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
