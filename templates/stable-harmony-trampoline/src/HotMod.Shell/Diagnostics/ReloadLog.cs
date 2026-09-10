using System.Text.Json;
using HotMod.Shell.Runtime;

namespace HotMod.Shell.Diagnostics;

public static class ReloadLog
{
    public static void WriteInfo(string message, object? data = null)
    {
        Write(Console.Out, DateTimeOffset.UtcNow, "information", message, data);
    }

    public static void WriteWarning(string message, object? data = null)
    {
        Write(Console.Out, DateTimeOffset.UtcNow, "warning", message, data);
    }

    public static void WriteError(string message, object? data = null)
    {
        Write(Console.Out, DateTimeOffset.UtcNow, "error", message, data);
    }

    public static void WriteInfo(TextWriter writer, DateTimeOffset timestamp, string message, object? data = null)
    {
        Write(writer, timestamp, "information", message, data);
    }

    private static void Write(TextWriter writer, DateTimeOffset timestamp, string level, string message, object? data)
    {
        var entry = new ReloadLogEntry(timestamp, level, message, data);
        writer.WriteLine(JsonSerializer.Serialize(entry, ReloadJson.Options));
    }

    private sealed record ReloadLogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Message,
        object? Data);
}
