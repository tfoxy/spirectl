using Spirectl.Sts2.Core.Transport;

namespace Spirectl.BridgeMod.Sts2Host;

public sealed class HostedBridgeHost : IBridgeHost
{
    private readonly object _lock = new();
    private string _transportKind;
    private bool _isRunning;
    private string _message;

    public HostedBridgeHost(string transportKind, bool isRunning, string message)
    {
        _transportKind = transportKind;
        _isRunning = isRunning;
        _message = message;
    }

    public void MarkRunning(string message)
    {
        lock (_lock)
        {
            _isRunning = true;
            _message = message;
        }
    }

    public void MarkStopped(string message)
    {
        lock (_lock)
        {
            _isRunning = false;
            _message = message;
        }
    }

    public BridgeHostStatus DescribeStatus()
    {
        lock (_lock)
        {
            return new BridgeHostStatus(_transportKind, _isRunning, _message);
        }
    }
}
