namespace Spirectl.Sts2.Core.Transport;


public sealed class NullBridgeHost : IBridgeHost
{
    public BridgeHostStatus DescribeStatus()
    {
        return new BridgeHostStatus(
            TransportKind: "unbound",
            IsRunning: false,
            Message: "Loader integration has not been selected yet.");
    }
}
