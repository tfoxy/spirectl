namespace Spirectl.Sts2.Live;

internal sealed class Sts2BridgeSpineDiagnostics : ISts2SpineDiagnostics
{
    public bool Enabled => Sts2SpineDebug.Enabled;
    public void Log(string message) => Sts2SpineDebug.Log(message);
}
