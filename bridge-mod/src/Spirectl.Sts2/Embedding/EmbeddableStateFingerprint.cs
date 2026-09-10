using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Spirectl.Sts2.Embedding;

/// <summary>
/// The change detector behind the semantic state watch: "is this snapshot the same one I already emitted?".
/// <para>
/// It is a STREAMING hash, not a serialize-then-digest. The value is written straight into a pooled, thread-local
/// UTF-8 buffer with <see cref="Utf8JsonWriter"/> and hashed with an inline XxHash64 — so a capture no longer
/// allocates a full JSON <see cref="string"/> (two bytes per character, immediately garbage) plus a SHA-256
/// state, several times a second, forever. The hash is non-cryptographic on purpose: nothing here authenticates
/// anything, it only has to make two different snapshots produce two different strings.
/// </para>
/// <para>
/// <b>No new package.</b> <c>System.IO.Hashing</c> would supply <c>XxHash64</c>, but this assembly ships INSIDE
/// the game process and carries zero <c>PackageReference</c>s; adding one would put a resolve/version problem
/// into a modded game's load path to save forty lines. The algorithm is the published XXH64 (seed 0), verified
/// against its reference vectors in <c>EmbeddableStateFingerprintTests</c>.
/// </para>
/// <para>
/// Values are prefixed <c>xxh64:</c>. The fingerprint travels to embedders as an opaque string
/// (<c>CurrentStateWatchEvent.SemanticFingerprint</c>) and is only ever compared for equality, so the algorithm
/// is free to change — but the prefix makes a value from this hash impossible to confuse with the bare SHA-256
/// hex the hub used before, which matters for anyone diffing two logs across the change.
/// </para>
/// </summary>
public static class EmbeddableStateFingerprint
{
    /// <summary>Algorithm tag every fingerprint from this type carries.</summary>
    public const string Prefix = "xxh64:";

    /// <summary>
    /// Buffers above this stay out of the thread-local cache. A state snapshot is normally tens of KB; a one-off
    /// giant capture should not pin megabytes on a pool thread for the life of the process.
    /// </summary>
    private const int MaxRetainedBufferBytes = 1 << 20;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
    };

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _buffer;

    [ThreadStatic]
    private static Utf8JsonWriter? _writer;

    /// <summary>
    /// Fingerprint a value by writing it as UTF-8 JSON into the pooled buffer and hashing the bytes. The
    /// serializer options decide what "the same state" means (naming policy, ignored members), exactly as they
    /// did when this was <c>JsonSerializer.Serialize</c> + SHA-256.
    /// </summary>
    public static string Compute<TValue>(TValue value, JsonSerializerOptions? options = null)
    {
        var buffer = _buffer ??= new ArrayBufferWriter<byte>(16 * 1024);
        buffer.ResetWrittenCount();

        var writer = _writer;
        if (writer is null)
        {
            writer = new Utf8JsonWriter(buffer, WriterOptions);
            _writer = writer;
        }
        else
        {
            writer.Reset(buffer);
        }

        try
        {
            JsonSerializer.Serialize(writer, value, options);
            writer.Flush();
            return Format(Hash64(buffer.WrittenSpan));
        }
        finally
        {
            // Release the reference to the (possibly huge) segment either way; a throwing serializer must not
            // leave a half-written buffer to be reused as if it were empty.
            writer.Reset();
            if (buffer.Capacity > MaxRetainedBufferBytes)
            {
                _buffer = null;
                _writer = null;
                writer.Dispose();
            }
        }
    }

    /// <summary>Fingerprint a string (the error path, where the "document" is a joined tuple, not JSON).</summary>
    public static string Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> scratch = maxBytes <= 512
            ? stackalloc byte[512]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes)).AsSpan();
        try
        {
            var written = Encoding.UTF8.GetBytes(value, scratch);
            return Format(Hash64(scratch[..written]));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Render a raw hash as the prefixed, fixed-width value that goes on the wire.</summary>
    public static string Format(ulong hash) => string.Create(
        Prefix.Length + 16,
        hash,
        static (span, value) =>
        {
            Prefix.AsSpan().CopyTo(span);
            value.TryFormat(span[Prefix.Length..], out _, "x16");
        });

    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    /// <summary>
    /// XXH64 over <paramref name="data"/>. Straight transcription of the published algorithm: four interleaved
    /// accumulators over 32-byte stripes, then the 8/4/1-byte tail, then the final avalanche.
    /// </summary>
    public static ulong Hash64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var index = 0;
        ulong hash;

        if (data.Length >= 32)
        {
            var v1 = unchecked(seed + Prime1 + Prime2);
            var v2 = unchecked(seed + Prime2);
            var v3 = seed;
            var v4 = unchecked(seed - Prime1);

            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 8)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 16)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[(index + 24)..]));
                index += 32;
            }
            while (index <= data.Length - 32);

            hash = unchecked(
                BitOperations.RotateLeft(v1, 1)
                + BitOperations.RotateLeft(v2, 7)
                + BitOperations.RotateLeft(v3, 12)
                + BitOperations.RotateLeft(v4, 18));
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = unchecked(seed + Prime5);
        }

        hash = unchecked(hash + (ulong)data.Length);

        while (index + 8 <= data.Length)
        {
            var lane = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[index..]));
            hash = unchecked((BitOperations.RotateLeft(hash ^ lane, 27) * Prime1) + Prime4);
            index += 8;
        }

        if (index + 4 <= data.Length)
        {
            hash ^= unchecked(BinaryPrimitives.ReadUInt32LittleEndian(data[index..]) * Prime1);
            hash = unchecked((BitOperations.RotateLeft(hash, 23) * Prime2) + Prime3);
            index += 4;
        }

        while (index < data.Length)
        {
            hash ^= unchecked(data[index] * Prime5);
            hash = unchecked(BitOperations.RotateLeft(hash, 11) * Prime1);
            index++;
        }

        hash ^= hash >> 33;
        hash = unchecked(hash * Prime2);
        hash ^= hash >> 29;
        hash = unchecked(hash * Prime3);
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong Round(ulong accumulator, ulong lane)
    {
        accumulator = unchecked(accumulator + (lane * Prime2));
        accumulator = BitOperations.RotateLeft(accumulator, 31);
        return unchecked(accumulator * Prime1);
    }

    private static ulong MergeRound(ulong accumulator, ulong lane)
    {
        accumulator ^= Round(0, lane);
        return unchecked((accumulator * Prime1) + Prime4);
    }
}
