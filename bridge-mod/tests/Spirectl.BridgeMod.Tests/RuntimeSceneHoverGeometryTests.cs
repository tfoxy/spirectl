using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// The reachability rules behind `dev scene hover`. A node path resolves wherever the node has been
/// scrolled to, so the old "centre of the global rect" answer could name a coordinate that clicks a
/// different row — or nothing at all. These cases pin what is refused and what is not.
/// </summary>
public sealed class RuntimeSceneHoverGeometryTests
{
    private static readonly RuntimeSceneHoverClipSnapshot Viewport = Clip(
        "/root",
        RuntimeSceneHoverClipReasons.Viewport,
        0,
        0,
        1920,
        1080);

    [Fact]
    public void AnOnScreenControlKeepsTheExactCentreItAlwaysHad()
    {
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(100, 200, 300, 80), [Viewport]);

        Assert.True(plan.Reachable);
        Assert.True(plan.Visibility.FullyVisible);
        Assert.Empty(plan.Visibility.ClippedBy);
        Assert.NotNull(plan.HoverPosition);
        Assert.Equal(250, plan.HoverPosition.X);
        Assert.Equal(240, plan.HoverPosition.Y);
    }

    [Fact]
    public void AControlInsideItsScrollContainerIsStillFullyVisible()
    {
        var scroll = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 100, 640, 414);
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, 150, 640, 108), [scroll, Viewport]);

        Assert.True(plan.Reachable);
        Assert.True(plan.Visibility.FullyVisible);
        Assert.Equal(204, plan.HoverPosition!.Y);
    }

    [Fact]
    public void ARowScrolledAboveItsContainerIsRefusedAndNamesTheContainer()
    {
        // The connection panel's case: the row's own rect resolves fine, ~3400px above the list.
        var scroll = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 100, 640, 414);
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, -3348, 640, 108), [scroll, Viewport]);

        Assert.False(plan.Reachable);
        Assert.Null(plan.HoverPosition);
        Assert.Null(plan.Visibility.VisibleRect);
        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal("/root/Panel/List", blocker.NodePath);

        var message = RuntimeSceneHoverGeometry.DescribeRefusal("/root/Panel/List/Row0", plan);
        Assert.Contains("/root/Panel/List/Row0", message, StringComparison.Ordinal);
        Assert.Contains("scroll-container", message, StringComparison.Ordinal);
        Assert.Contains("--ensure-visible", message, StringComparison.Ordinal);

        var details = RuntimeSceneHoverGeometry.RefusalDetails("/root/Panel/List/Row0", plan);
        Assert.Contains(details, detail => detail.Field == "clipped_by" && detail.Value == "/root/Panel/List");
        Assert.Contains(details, detail => detail.Field == "remedy" && detail.Value == "--ensure-visible");
    }

    [Fact]
    public void AControlDraggedOffTheViewportIsRefusedAndNamesTheViewport()
    {
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(2100, 200, 300, 80), [Viewport]);

        Assert.False(plan.Reachable);
        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal(RuntimeSceneHoverClipReasons.Viewport, blocker.Reason);
    }

    [Fact]
    public void APartlyVisibleControlIsHoveredOnThePartThatIsActuallyOnScreen()
    {
        // Half a row poking out of the bottom of its list: reachable, but the rect's own centre is
        // below the clip, so hovering it would land outside the container.
        var scroll = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 100, 640, 400);
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, 420, 640, 200), [scroll, Viewport]);

        Assert.True(plan.Reachable);
        Assert.False(plan.Visibility.FullyVisible);
        Assert.Empty(plan.Blockers);
        Assert.Equal(460, plan.HoverPosition!.Y);
        Assert.Equal(80, plan.Visibility.VisibleRect!.Size.Y);
        var clipped = Assert.Single(plan.Visibility.ClippedBy);
        Assert.Equal("/root/Panel/List", clipped.NodePath);
    }

    [Fact]
    public void ASubPixelSliverCountsAsUnreachable()
    {
        var scroll = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 100, 640, 400);
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, 499.6, 640, 108), [scroll, Viewport]);

        Assert.False(plan.Reachable);
    }

    [Fact]
    public void EachClipIsJudgedAgainstTheControlSoTheReportDoesNotDependOnOrdering()
    {
        // Two nested clips, one cutting the control's top and one its bottom: both must be listed,
        // even though after the first intersection the second no longer changes the running region
        // by as much.
        var outer = Clip("/root/Panel", RuntimeSceneHoverClipReasons.ClipContents, 0, 0, 640, 300);
        var inner = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 270, 640, 330);
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, 250, 640, 108), [inner, outer, Viewport]);

        Assert.True(plan.Reachable);
        Assert.Equal(2, plan.Visibility.ClippedBy.Count);
        Assert.Equal(30, plan.Visibility.VisibleRect!.Size.Y);
        Assert.Equal(285, plan.HoverPosition!.Y);
    }

    [Fact]
    public void ScrollsAreCarriedThroughOntoTheVisibilityReport()
    {
        var scrolled = new[]
        {
            new RuntimeSceneHoverScrollSnapshot("/root/Panel/List", 0, 3400, 0, 0),
        };
        var plan = RuntimeSceneHoverGeometry.Plan(Rect(0, 150, 640, 108), [Viewport], scrolled);

        var scroll = Assert.Single(plan.Visibility.Scrolled);
        Assert.Equal(3400, scroll.PreviousVertical);
        Assert.Equal(0, scroll.Vertical);
    }

    [Fact]
    public void IsFullyVisibleAgreesWithThePlanItIsDerivedFrom()
    {
        var scroll = Clip("/root/Panel/List", RuntimeSceneHoverClipReasons.ScrollContainer, 0, 100, 640, 414);

        Assert.True(RuntimeSceneHoverGeometry.IsFullyVisible(Rect(0, 150, 640, 108), [scroll, Viewport]));
        Assert.False(RuntimeSceneHoverGeometry.IsFullyVisible(Rect(0, 480, 640, 108), [scroll, Viewport]));
    }

    private static RuntimeSceneRect2Snapshot Rect(double x, double y, double width, double height)
        => new(new RuntimeSceneVector2Snapshot(x, y), new RuntimeSceneVector2Snapshot(width, height));

    private static RuntimeSceneHoverClipSnapshot Clip(
        string nodePath,
        string reason,
        double x,
        double y,
        double width,
        double height)
        => new(nodePath, "Godot.Control", reason, Rect(x, y, width, height));
}
