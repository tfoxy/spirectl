using System.Text.Json;
using System.Text.Json.Serialization;
using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The card-flight WIRE RECORD's optional tail. One array now carries two different card animations — the
// discard→draw shuffle sweep (`Kind` null) and the hand→discard fly of the real played card (`Kind` "discard") —
// which is only safe because the two discriminating fields are OPTIONAL: a shuffle entry has to serialize to
// exactly the bytes it did before they existed, or every already-deployed client pays (in wire size and in
// re-validation) for a flight it does not replay. These tests pin that byte-level compatibility in both
// directions and prove the discard tail survives a round trip.
public sealed class Sts2CardFlightHintDeltaTests
{
    // The mirror wire's own serializer settings (camelCase names, omit-when-null): the omit-when-null policy is
    // what turns "the field defaults to null" into "the field is absent from the payload".
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The exact payload a shuffle flight produced before the tail was appended.
    private const string ShuffleJson =
        """{"targetId":"101","trailId":"202","start":[1,2],"end":[3,4],"control":[5,6],"basis":[1,0,0,1],"speed0":1.2,"accel":2.25,"duration":1.5,"scale0":1,"windowMs":3800}""";

    private static CardFlightHintDelta Shuffle() => new(
        TargetId: "101",
        TrailId: "202",
        Start: [1, 2],
        End: [3, 4],
        Control: [5, 6],
        Basis: [1, 0, 0, 1],
        Speed0: 1.2,
        Accel: 2.25,
        Duration: 1.5,
        Scale0: 1,
        WindowMs: 3800);

    [Fact]
    public void ShuffleEntry_SerializesToTheSameBytesAsBeforeTheTailWasAppended()
    {
        // Constructed exactly as every existing call site does — positionally, with no tail argument.
        Assert.Equal(ShuffleJson, JsonSerializer.Serialize(Shuffle(), WireOptions));
    }

    [Fact]
    public void ShuffleEntry_OmitsBothTailFieldsEntirely()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Shuffle(), WireOptions));
        Assert.False(doc.RootElement.TryGetProperty("kind", out _));
        Assert.False(doc.RootElement.TryGetProperty("rot0", out _));
    }

    [Fact]
    public void PayloadWithoutTheTail_DeserializesAsAShuffleFlight()
    {
        // The other direction of the same compatibility claim: a payload minted by a producer that predates the
        // tail must read back as the shuffle kind, never as a half-populated discard.
        var flight = JsonSerializer.Deserialize<CardFlightHintDelta>(ShuffleJson, WireOptions);
        Assert.NotNull(flight);
        Assert.Null(flight!.Kind);
        Assert.Null(flight.Rot0);
    }

    [Fact]
    public void DiscardEntry_RoundTripsItsKindAndRotationSeed()
    {
        var sent = Shuffle() with { Kind = "discard", Rot0 = -0.3141592653589793 };

        var json = JsonSerializer.Serialize(sent, WireOptions);
        Assert.Contains("\"kind\":\"discard\"", json);
        Assert.Contains("\"rot0\":", json);

        var received = JsonSerializer.Deserialize<CardFlightHintDelta>(json, WireOptions);
        Assert.NotNull(received);
        Assert.Equal("discard", received!.Kind);
        Assert.Equal(sent.Rot0, received.Rot0);
        // A zero seed is a real pose (an upright card), not an absent one, so it must still travel.
        var upright = JsonSerializer.Deserialize<CardFlightHintDelta>(
            JsonSerializer.Serialize(sent with { Rot0 = 0 }, WireOptions), WireOptions);
        Assert.Equal(0d, upright!.Rot0);

        // Nothing the shuffle replay depends on moved or changed meaning.
        Assert.Equal("101", received.TargetId);
        Assert.Equal("202", received.TrailId);
        Assert.Equal([1, 2], received.Start);
        Assert.Equal([3, 4], received.End);
        Assert.Equal([5, 6], received.Control);
        Assert.Equal([1, 0, 0, 1], received.Basis);
        Assert.Equal(1.2, received.Speed0);
        Assert.Equal(2.25, received.Accel);
        Assert.Equal(1.5, received.Duration);
        Assert.Equal(1d, received.Scale0);
        Assert.Equal(3800d, received.WindowMs);
    }
}
