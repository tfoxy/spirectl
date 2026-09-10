using Spirectl.Sts2.Core.Map;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Locks the map-drawing coordinate transform: read (NetToContent) and write (ContentToNet) are
// exact inverses for ANY content affine, so the calibrated constants can be tuned live without
// read/write ever drifting. Also pins that, with the identity content affine, NetToContent equals
// the game's FromNetPosition (net.x*960 + Size.X*0.5, net.y*Size.Y) — the inverse of the game's
// ToNetPosition that produced the serialized points.
public sealed class Sts2MapDrawingTransformTests
{
    [Theory]
    [InlineData(0.0, 0.0, 1920.0, 1080.0)]
    [InlineData(566.0, 480.0, 1920.0, 1080.0)]
    [InlineData(-300.5, 50.25, 1280.0, 720.0)]
    [InlineData(1900.0, 1075.0, 1920.0, 1080.0)]
    public void ContentNetRoundTripsExactly(double contentX, double contentY, double sizeX, double sizeY)
    {
        var (netX, netY) = Sts2MapDrawingTransform.ContentToNet(contentX, contentY, sizeX, sizeY);
        var (backX, backY) = Sts2MapDrawingTransform.NetToContent(netX, netY, sizeX, sizeY);

        Assert.Equal(contentX, backX, 6);
        Assert.Equal(contentY, backY, 6);
    }

    [Fact]
    public void NetToContentAppliesFromNetPositionThenTheCalibratedAffine()
    {
        const double sizeX = 1920.0;
        const double sizeY = 1080.0;

        var (x, y) = Sts2MapDrawingTransform.NetToContent(0.25, 0.5, sizeX, sizeY);

        // First the game's FromNetPosition (net -> Line2D point), then the calibrated content affine.
        var localX = 0.25 * 960.0 + sizeX * 0.5;
        var localY = 0.5 * sizeY;
        Assert.Equal(localX * Sts2MapDrawingTransform.ContentScaleX + Sts2MapDrawingTransform.ContentOffsetX, x, 6);
        Assert.Equal(localY * Sts2MapDrawingTransform.ContentScaleY + Sts2MapDrawingTransform.ContentOffsetY, y, 6);
    }

    // The write path round-trips through the GAME's behaviour: ContentToLocal gives the BeginLineLocal
    // input, the game stores it halved (AddPoint(pos*0.5)) and serializes ToNetPosition of that; reading
    // it back through NetToContent must return the original content point. Locks read/write inverse so a
    // drawn stroke lands where the renderer expects.
    [Theory]
    [InlineData(566.0, 480.0, 1920.0, 1080.0)]
    [InlineData(100.0, 1300.0, 1920.0, 1080.0)]
    [InlineData(-250.0, 42.0, 1280.0, 720.0)]
    public void ContentToLocalRoundTripsThroughTheGamePipeline(
        double contentX,
        double contentY,
        double sizeX,
        double sizeY)
    {
        // 1) content -> BeginLineLocal input.
        var (posX, posY) = Sts2MapDrawingTransform.ContentToLocal(contentX, contentY);
        // 2) the game stores each point halved.
        var linePointX = posX * 0.5;
        var linePointY = posY * 0.5;
        // 3) the game serializes ToNetPosition(linePoint) = ((x - Size.X*0.5)/960, y/Size.Y).
        var netX = (linePointX - sizeX * 0.5) / 960.0;
        var netY = linePointY / sizeY;
        // 4) reading it back must return the original content point.
        var (backX, backY) = Sts2MapDrawingTransform.NetToContent(netX, netY, sizeX, sizeY);

        Assert.Equal(contentX, backX, 6);
        Assert.Equal(contentY, backY, 6);
    }
}
