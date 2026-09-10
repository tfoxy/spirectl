namespace Spirectl.Sts2.Core.Logging;

public interface ILogStream
{
    void Write(BridgeLogLevel level, string target, string message);

    LogReadResult Read(LogQuery query);
}
