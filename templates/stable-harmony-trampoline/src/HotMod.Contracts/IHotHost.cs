namespace HotMod.Contracts;

public interface IHotHost
{
    DateTimeOffset Now { get; }

    void Log(HotLogLevel level, string message, IReadOnlyDictionary<string, string>? fields = null);
}
