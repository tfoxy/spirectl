#if ENABLE_STS2_LIVE_HOST
extern alias GodotLive;

using GodotLive::Godot;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using System.Reflection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2CharacterSelectBackgroundRenderSizeTests
{
    private const string ScenePath = "res://scenes/screens/char_select/char_select_bg_ironclad.tscn";

    [Fact]
    public void ExplicitSizeCentersTheReferenceCompositionInTheRequestedViewport()
    {
        var request = Request(2520, 1080);
        var viewport = Assert.IsType<Vector2I>(Method("ResolveRequestedViewportSize").Invoke(null, [request]));

        AssertFrame(ResolveFrame(request, viewport), new Vector2I(2520, 1080), new Vector2(300, 0),
            Vector2.One, new Vector2(1920, 1080));
    }

    [Fact]
    public void ExplicitFrameIsRecenteredOnBothAxesWithoutChangingItsScale()
    {
        AssertFrame(ResolveFrame(Request(2520, 1440, "12,-8,0.75"), new Vector2I(2520, 1440)),
            new Vector2I(2520, 1440), new Vector2(312, 172), new Vector2(0.75f, 0.75f), new Vector2(1920, 1080));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2")]
    [InlineData("NaN,2,1")]
    [InlineData("1,2,Infinity")]
    [InlineData("1,2,0")]
    [InlineData("1,2,-1")]
    public void InvalidFrameKeepsTheDefaultComposition(string? spec)
    {
        Assert.Equal(ResolveFrame(Request(2520, 1080), new Vector2I(2520, 1080)),
            ResolveFrame(Request(2520, 1080, spec), new Vector2I(2520, 1080)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(2520, null)]
    [InlineData(null, 1080)]
    [InlineData(0, 1080)]
    [InlineData(2520, -1)]
    public void WithoutACompleteSizeTheLegacyViewportAndIdentityFrameRemain(int? width, int? height)
    {
        AssertFrame(ResolveFrame(Request(width, height, "12,-8,0.75"), new Vector2I(1600, 900)),
            new Vector2I(1600, 900), Vector2.Zero, Vector2.One, new Vector2(1600, 900));
    }

    [Fact]
    public void RepeatedRequestsResolveTheSameFrameRegardlessOfRequestId()
    {
        var request = Request(2520, 1080, "12,-8,0.75");
        Assert.Equal(ResolveFrame(request, new Vector2I(2520, 1080)),
            ResolveFrame(request with { RequestId = "second-request" }, new Vector2I(2520, 1080)));
    }

    [Fact]
    public void OtherSceneFamiliesAreNotAdmitted()
    {
        var request = Request(2520, 1080) with { SourcePath = "res://other.tscn", LoadPath = "res://other.tscn" };
        var args = new object?[] { request, new Vector2I(2520, 1080), null };
        Assert.False(Assert.IsType<bool>(Method("TryResolveCharacterSelectBackgroundFrame").Invoke(null, args)));
    }

    private static AssetExtractRequestSnapshot Request(int? width, int? height, string? frame = null)
        => new("req-frame", "resources", ScenePath, ScenePath, "png",
            RenderWidth: width, RenderHeight: height, EventBackgroundFrame: frame);

    private static MethodInfo Method(string name)
        => typeof(Sts2AssetExtractProvider).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object ResolveFrame(AssetExtractRequestSnapshot request, Vector2I viewport)
    {
        var args = new object?[] { request, viewport, null };
        Assert.True(Assert.IsType<bool>(Method("TryResolveCharacterSelectBackgroundFrame").Invoke(null, args)));
        Assert.NotNull(args[2]);
        return args[2]!;
    }

    private static void AssertFrame(object frame, Vector2I viewport, Vector2 position, Vector2 scale, Vector2 size)
    {
        Assert.Equal(viewport, frame.GetType().GetProperty("ViewportSize")!.GetValue(frame));
        Assert.Equal(position, frame.GetType().GetProperty("NodePosition")!.GetValue(frame));
        Assert.Equal(scale, frame.GetType().GetProperty("NodeScale")!.GetValue(frame));
        Assert.Equal(Vector2.Zero, frame.GetType().GetProperty("PivotOffset")!.GetValue(frame));
        Assert.Equal(size, frame.GetType().GetProperty("ControlSize")!.GetValue(frame));
    }
}
#endif
