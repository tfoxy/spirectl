using System.Text;
using System.Text.Json;
using Spirectl.Sts2.Embedding;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The state watch hub's change detector. Two properties matter and are asserted separately:
//  1. it really is XXH64 - pinned against the algorithm's PUBLISHED reference vectors, not against whatever this
//     implementation happens to produce, so a transcription slip in the stripe/tail/avalanche arms is caught;
//  2. equal documents fingerprint equal and different documents fingerprint differently, which is the only
//     property the hub actually relies on.
public sealed class EmbeddableStateFingerprintTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private sealed record Doc(string Name, int Value, IReadOnlyList<string> Tags);

    // Published XXH64 (seed 0) vectors. These are the algorithm's, not this code's.
    [Theory]
    [InlineData("", 0xEF46DB3751D8E999UL)]
    [InlineData("abc", 0x44BC2CF5AD770999UL)]
    [InlineData("Nobody inspects the spammish repetition", 0xFBCEA83C8A378BF1UL)]
    public void Hash64MatchesPublishedXxHash64Vectors(string input, ulong expected)
        => Assert.Equal(expected, EmbeddableStateFingerprint.Hash64(Encoding.UTF8.GetBytes(input)));

    // Exercises every arm: under 32 bytes (tail only), the 32-byte stripe loop, and each of the 8/4/1-byte tails.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(1000)]
    public void Hash64IsStableAndLengthSensitive(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        var first = EmbeddableStateFingerprint.Hash64(data);
        Assert.Equal(first, EmbeddableStateFingerprint.Hash64(data));

        // One flipped bit anywhere must move the hash.
        data[length - 1] ^= 0x01;
        Assert.NotEqual(first, EmbeddableStateFingerprint.Hash64(data));
    }

    [Fact]
    public void FormatIsPrefixedFixedWidthLowercaseHex()
    {
        Assert.Equal("xxh64:0123456789abcdef", EmbeddableStateFingerprint.Format(0x0123456789abcdefUL));
        Assert.Equal("xxh64:000000000000002a", EmbeddableStateFingerprint.Format(42));
    }

    [Fact]
    public void EqualDocumentsFingerprintEqual()
    {
        var a = new Doc("combat", 7, ["x", "y"]);
        var b = new Doc("combat", 7, ["x", "y"]);

        Assert.Equal(
            EmbeddableStateFingerprint.Compute(a, WebOptions),
            EmbeddableStateFingerprint.Compute(b, WebOptions));
    }

    [Theory]
    [InlineData("map", 7)]
    [InlineData("combat", 8)]
    public void DifferentDocumentsFingerprintDifferently(string name, int value)
    {
        var baseline = EmbeddableStateFingerprint.Compute(new Doc("combat", 7, ["x", "y"]), WebOptions);
        var other = EmbeddableStateFingerprint.Compute(new Doc(name, value, ["x", "y"]), WebOptions);

        Assert.NotEqual(baseline, other);
        Assert.StartsWith(EmbeddableStateFingerprint.Prefix, other, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedCollectionOrderIsPartOfTheDocument()
    {
        var ordered = EmbeddableStateFingerprint.Compute(new Doc("combat", 7, ["x", "y"]), WebOptions);
        var reordered = EmbeddableStateFingerprint.Compute(new Doc("combat", 7, ["y", "x"]), WebOptions);

        Assert.NotEqual(ordered, reordered);
    }

    // The pooled, thread-static buffer is reused across calls; a large document must not poison the next one.
    [Fact]
    public void PooledBufferIsResetBetweenComputations()
    {
        var big = new Doc("big", 1, [.. Enumerable.Range(0, 20_000).Select(i => $"tag-{i}")]);
        var small = new Doc("small", 1, ["a"]);

        var smallBefore = EmbeddableStateFingerprint.Compute(small, WebOptions);
        _ = EmbeddableStateFingerprint.Compute(big, WebOptions);
        var smallAfter = EmbeddableStateFingerprint.Compute(small, WebOptions);

        Assert.Equal(smallBefore, smallAfter);
    }

    [Fact]
    public void StringOverloadMatchesHashingTheSameUtf8Bytes()
    {
        const string Value = "current-state-error boom";
        Assert.Equal(
            EmbeddableStateFingerprint.Format(EmbeddableStateFingerprint.Hash64(Encoding.UTF8.GetBytes(Value))),
            EmbeddableStateFingerprint.Compute(Value));
    }

    // The over-512-byte arm of the string overload rents from the pool instead of using the stack buffer.
    [Fact]
    public void StringOverloadHandlesInputsLargerThanTheStackBuffer()
    {
        var value = new string('z', 5000);
        Assert.Equal(
            EmbeddableStateFingerprint.Format(EmbeddableStateFingerprint.Hash64(Encoding.UTF8.GetBytes(value))),
            EmbeddableStateFingerprint.Compute(value));
    }
}
