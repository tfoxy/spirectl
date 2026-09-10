namespace Spirectl.Sts2.Core.Transport;


public sealed class EmbeddedBridgeHost : IBridgeHost
{
    public BridgeHostStatus DescribeStatus()
    {
        return new BridgeHostStatus(
            TransportKind: "embedded",
            IsRunning: true,
            Message: "Embeddable runtime facade is available in-process.");
    }
}
