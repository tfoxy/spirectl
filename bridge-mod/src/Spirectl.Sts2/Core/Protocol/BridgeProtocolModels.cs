using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.Protocol;

public enum DataSourceKind
{
    Stub,
    Live,
}

public enum RuntimeAttachmentState
{
    Stubbed,
    Detached,
    Attached,
}

public enum MultiplayerRoleSnapshot
{
    Unspecified,
    Local,
    Host,
    Remote,
    HostLocalSeat,
}

public enum RemoteClientOrchestrationStateSnapshot
{
    Unspecified,
    Unavailable,
    LocalOnlyDegraded,
    HostMediated,
    ConfiguredClient,
    Unsupported,
    HostLocalSeat,
}

public sealed record RemoteClientOrchestrationCapabilitySnapshot(
    string Id,
    RemoteClientOrchestrationStateSnapshot State,
    string Summary,
    bool Provisional);
