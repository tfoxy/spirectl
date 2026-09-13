using System.Text;

namespace Spirectl.Sts2.Core.Logging;

/// <summary>Reads a bounded append-only slice of a Godot log without requiring exclusive file access.</summary>
public sealed class GodotLogReader
{
    public const int HardMaximumBytes = 64 * 1024;
    public GodotLogReadResult Read(GodotLogReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var path = Path.GetFullPath(request.Path);
            var maxBytes = Math.Clamp(request.MaxBytes <= 0 ? HardMaximumBytes : request.MaxBytes, 1, HardMaximumBytes);
            var afterOffset = Math.Max(0, request.Checkpoint?.ByteOffset ?? request.AfterByteOffset);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return GodotLogReadResult.ForUnavailable(path, afterOffset);
            }

            var identity = GodotLogCheckpoint.From(info, afterOffset);
            var rotated = afterOffset > info.Length || request.Checkpoint is { } prior && !prior.Matches(identity);
            var start = rotated ? 0 : afterOffset;
            var skippedPrefix = request.ReadTail && info.Length - start > maxBytes;
            if (skippedPrefix) start = info.Length - maxBytes;
            var available = info.Length - start;
            var wanted = (int)Math.Min(available, maxBytes);
            var bytes = new byte[wanted];
            var read = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(start, SeekOrigin.Begin);
                while (read < wanted)
                {
                    var count = stream.Read(bytes, read, wanted - read);
                    if (count == 0) break;
                    read += count;
                }
            }

            var content = bytes.AsSpan(0, read);
            if (skippedPrefix)
            {
                var leadingContinuationBytes = SkipLeadingUtf8ContinuationBytes(content);
                start += leadingContinuationBytes;
                content = content[leadingContinuationBytes..];
            }

            var complete = CompleteUtf8Length(content);
            var lastNewline = content[..complete].LastIndexOf((byte)'\n');
            // Do not advance past a line a concurrent writer may still be appending. The next read repeats
            // that tail once the writer commits its newline, which keeps an error block intact.
            if (lastNewline < 0 && (read < available || content.Length == maxBytes))
            {
                // A line longer than the bounded slice must not permanently pin the cursor. Decode the valid
                // prefix and advance; its continuation is less useful than a stalled diagnostics feed.
                complete = content.Length;
            }
            else if (lastNewline != complete - 1)
            {
                complete = Math.Max(0, lastNewline + 1);
            }

            var truncated = skippedPrefix || available > read || complete != content.Length;
            return new GodotLogReadResult(
                path,
                ParseEntries(content[..complete], start, request.CheckpointMarker),
                start + complete,
                Unavailable: false,
                Rotated: rotated,
                Truncated: truncated,
                Checkpoint: GodotLogCheckpoint.From(info, start + complete));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return GodotLogReadResult.ForUnavailable(request.Path, Math.Max(0, request.AfterByteOffset));
        }
    }

    private static IReadOnlyList<GodotLogEntry> ParseEntries(ReadOnlySpan<byte> bytes, long startOffset, string? checkpointMarker)
    {
        var entries = new List<GodotLogEntry>();
        var offset = startOffset;
        GodotLogEntryBuilder? current = null;
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var lineByteLength = newline < 0 ? bytes.Length : newline;
            var rawLine = bytes[..lineByteLength];
            var byteCount = lineByteLength + (newline < 0 ? 0 : 1);
            if (byteCount == 0) break;

            var line = Encoding.UTF8.GetString(rawLine).TrimEnd('\r');
            var isContinuation = current is not null && (line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith("at ", StringComparison.Ordinal));
            if (current is not null && isContinuation)
            {
                current.Append(line, offset + byteCount);
            }
            else
            {
                if (current is not null) entries.Add(current.Build());
                current = new GodotLogEntryBuilder(
                    offset,
                    offset + byteCount,
                    line,
                    IsErrorLine(line),
                    !string.IsNullOrEmpty(checkpointMarker) && line.Contains(checkpointMarker, StringComparison.Ordinal));
            }

            offset += byteCount;
            bytes = newline < 0 ? [] : bytes[(newline + 1)..];
        }

        if (current is not null) entries.Add(current.Build());
        return entries;
    }

    private static int SkipLeadingUtf8ContinuationBytes(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length && (bytes[index] & 0b1100_0000) == 0b1000_0000)
        {
            index++;
        }

        return index;
    }

    private static bool IsErrorLine(string line)
        => line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("SCRIPT ERROR", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("E ", StringComparison.Ordinal)
            || line.Contains("Exception", StringComparison.Ordinal);

    private static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var utf8 = new UTF8Encoding(false, true);
        for (var length = bytes.Length; length >= Math.Max(0, bytes.Length - 3); length--)
        {
            try
            {
                _ = utf8.GetString(bytes[..length]);
                return length;
            }
            catch (DecoderFallbackException)
            {
            }
        }

        // Invalid bytes in an otherwise complete log line are rendered with replacement characters by the normal
        // decoder. Only an incomplete trailing rune must be retained for the next incremental read.
        return bytes.Length;
    }

    private sealed class GodotLogEntryBuilder(long startByteOffset, long endByteOffset, string text, bool isError, bool isCheckpoint)
    {
        private long _endByteOffset = endByteOffset;
        private string _text = text;

        public void Append(string line, long endByteOffset)
        {
            _text += "\n" + line;
            _endByteOffset = endByteOffset;
        }

        public GodotLogEntry Build() => new(startByteOffset, _endByteOffset, _text, isError, isCheckpoint);
    }
}

public sealed record GodotLogReadRequest(string Path, long AfterByteOffset = 0, int MaxBytes = 64 * 1024, string? CheckpointMarker = null, GodotLogCheckpoint? Checkpoint = null, bool ReadTail = false);

/// <summary>Stable-enough file identity for incremental reads across replacement and rotation.</summary>
public sealed record GodotLogCheckpoint(long ByteOffset, long Length, DateTimeOffset LastWriteUtc,
    DateTimeOffset? CreatedUtc = null, string? Prefix = null, string? Boundary = null)
{
    public static GodotLogCheckpoint From(FileInfo info, long byteOffset)
    {
        using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var prefix = ReadSample(stream, 0, (int)Math.Min(64, stream.Length));
        var end = Math.Min(byteOffset, stream.Length);
        var boundary = ReadSample(stream, Math.Max(0, end - 64), (int)Math.Min(64, end));
        return new(byteOffset, stream.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, prefix, boundary);
    }

    public bool Matches(GodotLogCheckpoint current)
        => current.Length >= ByteOffset && current.LastWriteUtc >= LastWriteUtc
            && (Prefix is null || current.Prefix?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            && (Boundary is null || Boundary == current.Boundary);

    private static string ReadSample(FileStream stream, long offset, int count)
    {
        stream.Position = offset;
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var length = stream.Read(bytes, read, count - read);
            if (length == 0) break;
            read += length;
        }
        return Convert.ToHexString(bytes.AsSpan(0, read));
    }
}

public sealed record GodotLogEntry(long StartByteOffset, long EndByteOffset, string Text, bool IsError, bool IsCheckpoint);

public sealed record GodotLogReadResult(
    string Path,
    IReadOnlyList<GodotLogEntry> Entries,
    long NextByteOffset,
    bool Unavailable,
    bool Rotated,
    bool Truncated,
    GodotLogCheckpoint? Checkpoint = null)
{
    internal static GodotLogReadResult ForUnavailable(string path, long nextByteOffset)
        => new(path, [], nextByteOffset, Unavailable: true, Rotated: false, Truncated: false);
}
