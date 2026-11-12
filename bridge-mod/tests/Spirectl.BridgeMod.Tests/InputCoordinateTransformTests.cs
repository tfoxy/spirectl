using Spirectl.Sts2.Core.Actions;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class InputCoordinateTransformTests
{
    [Theory]
    [InlineData(1, 1, 0, 0, 960, 540)]
    [InlineData(0.6666666666666666, 0.6666666666666666, 0, 0, 640, 360)]
    [InlineData(0.5, 0.75, 160, 30, 640, 435)]
    public void MapsBothDirectionsIncludingLetterbox(double sx, double sy, double ox, double oy, double wx, double wy)
    {
        var transform = new InputCoordinateTransform(sx, sy, ox, oy);
        var window = transform.CanvasToWindow(960, 540);
        Assert.Equal(wx, window.X, 6);
        Assert.Equal(wy, window.Y, 6);
        var canvas = transform.WindowToCanvas(window.X, window.Y);
        Assert.Equal(960, canvas.X, 6);
        Assert.Equal(540, canvas.Y, 6);
    }

    [Theory]
    [InlineData(MouseCoordinateSpaceKind.Unspecified, 420, 230)]
    [InlineData(MouseCoordinateSpaceKind.Canvas, 420, 230)]
    [InlineData(MouseCoordinateSpaceKind.Window, 640, 360)]
    public void WindowSpaceSkipsCanvasTransform(MouseCoordinateSpaceKind space, double x, double y)
    {
        var transform = new InputCoordinateTransform(0.5, 0.5, 100, 50);
        Assert.Equal((x, y), transform.ToWindow(640, 360, space));
    }

    [Fact]
    public void UnknownSpaceFailsClosed()
        => Assert.Throws<ArgumentOutOfRangeException>(() => InputCoordinateTransform.Identity.ToWindow(1, 2, (MouseCoordinateSpaceKind)99));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DroppedMotionPressOrReleaseFailsGesture(int droppedEdge)
    {
        var edge = 0;
        var buttonCalls = 0;
        Assert.False(MouseClickInjection.TryInject(
            () => edge++ != droppedEdge,
            _ => { buttonCalls++; return edge++ != droppedEdge; }, null, 1));
        Assert.Equal(droppedEdge, buttonCalls);
    }

    [Fact]
    public void CancellationDuringPressSurfacesFailureAndStopsRelease()
    {
        var lifetime = new InjectedInputLifetime();
        var buttons = 0;
        Assert.False(MouseClickInjection.TryInject(
            () => lifetime.Run(0, () => { }),
            pressed => lifetime.Send(0, "mouse:left", pressed,
                () => { buttons++; lifetime.Cancel(() => new Scope(), () => { }); }, () => { }), null, 1));
        Assert.Equal(1, buttons);
    }

    [Fact]
    public void FullClickPreservesMotionPressReleaseOrder()
    {
        var edges = new List<string>();
        Assert.True(MouseClickInjection.TryInject(
            () => { edges.Add("motion"); return true; },
            pressed => { edges.Add(pressed ? "press" : "release"); return true; }, null, 1));
        Assert.Equal(new[] { "motion", "press", "release" }, edges);
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() { }
    }
}
