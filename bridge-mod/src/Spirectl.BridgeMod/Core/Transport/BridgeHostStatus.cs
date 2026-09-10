namespace Spirectl.Sts2.Core.Transport;


public sealed record BridgeHostStatus(
    string TransportKind,
    bool IsRunning,
    string Message);
