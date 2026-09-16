namespace Spirectl.Sts2.Core.SceneInspection;

/// <summary>
/// Why a rectangle governs whether a hover target is on screen. Callers branch on these, so the
/// values are part of the wire contract and must stay stable.
/// </summary>
public static class RuntimeSceneHoverClipReasons
{
    /// <summary>The visible rect of the root viewport.</summary>
    public const string Viewport = "viewport";

    /// <summary>An ancestor Control with clip_contents set.</summary>
    public const string ClipContents = "clip-contents";

    /// <summary>An ancestor ScrollContainer, which clips to its own rect.</summary>
    public const string ScrollContainer = "scroll-container";
}

/// <summary>One rectangle a hover target has to survive to be reachable by a click.</summary>
public sealed record RuntimeSceneHoverClipSnapshot(
    string NodePath,
    string NodeType,
    string Reason,
    RuntimeSceneRect2Snapshot Rect);

/// <summary>A scroll container that an ensure-visible pass moved, and by how much.</summary>
public sealed record RuntimeSceneHoverScrollSnapshot(
    string NodePath,
    double PreviousHorizontal,
    double PreviousVertical,
    double Horizontal,
    double Vertical);

public sealed record RuntimeSceneHoverVisibilitySnapshot(
    bool FullyVisible,
    RuntimeSceneRect2Snapshot ControlRect,
    RuntimeSceneRect2Snapshot? VisibleRect,
    IReadOnlyList<RuntimeSceneHoverClipSnapshot> ClippedBy,
    IReadOnlyList<RuntimeSceneHoverScrollSnapshot> Scrolled);

/// <summary>
/// The outcome of intersecting a hover target's rect with every clip that governs it: whether a
/// pointer can reach it at all, where to put the pointer, and which clips cut it away.
/// </summary>
/// <param name="Reachable">
/// False when nothing of the control survives its clips, i.e. every coordinate the old
/// centre-of-rect rule would have returned lands on whatever else is painted there.
/// </param>
/// <param name="HoverPosition">
/// Null when <paramref name="Reachable"/> is false. Otherwise the control's own centre when that
/// point is inside the surviving region (so a fully visible control keeps the exact position it
/// has always had), else the centre of the surviving region.
/// </param>
/// <param name="Blockers">
/// The clips that emptied the region, innermost first. Empty when the control is reachable, or
/// when no single clip is to blame and only their combination is — read <see cref="Visibility"/>'s
/// ClippedBy then.
/// </param>
public sealed record RuntimeSceneHoverPlan(
    bool Reachable,
    RuntimeSceneVector2Snapshot? HoverPosition,
    RuntimeSceneHoverVisibilitySnapshot Visibility,
    IReadOnlyList<RuntimeSceneHoverClipSnapshot> Blockers);

/// <summary>
/// Pure hover geometry, deliberately free of Godot types so it is unit-testable off a live host.
/// The live provider harvests the rectangles; every decision about them is made here.
/// </summary>
public static class RuntimeSceneHoverGeometry
{
    /// <summary>
    /// A surviving region thinner than this in either axis is treated as unreachable. A sub-pixel
    /// sliver is not something a click can be aimed at, and rounding it to a coordinate would put
    /// the pointer on the neighbouring row.
    /// </summary>
    public const double MinimumVisibleExtent = 1.0;

    private const double Epsilon = 0.01;

    public static RuntimeSceneHoverPlan Plan(
        RuntimeSceneRect2Snapshot controlRect,
        IReadOnlyList<RuntimeSceneHoverClipSnapshot> clips,
        IReadOnlyList<RuntimeSceneHoverScrollSnapshot>? scrolled = null)
    {
        ArgumentNullException.ThrowIfNull(controlRect);
        ArgumentNullException.ThrowIfNull(clips);

        var region = controlRect;
        var clippedBy = new List<RuntimeSceneHoverClipSnapshot>();
        var blockers = new List<RuntimeSceneHoverClipSnapshot>();

        foreach (var clip in clips)
        {
            // "Cut" is judged against the control's own rect, not the running region, so each entry
            // answers "did THIS container hide part of the target" independently of ordering.
            if (!SameRect(Intersect(controlRect, clip.Rect), controlRect))
            {
                clippedBy.Add(clip);
            }

            var next = Intersect(region, clip.Rect);
            if (!IsTooThin(region) && IsTooThin(next))
            {
                blockers.Add(clip);
            }

            region = next;
        }

        var reachable = !IsTooThin(region);
        var fullyVisible = reachable && clippedBy.Count == 0;
        var visibility = new RuntimeSceneHoverVisibilitySnapshot(
            FullyVisible: fullyVisible,
            ControlRect: controlRect,
            VisibleRect: reachable ? region : null,
            ClippedBy: clippedBy,
            Scrolled: scrolled ?? []);

        return new RuntimeSceneHoverPlan(
            Reachable: reachable,
            HoverPosition: reachable ? ChooseHoverPosition(controlRect, region) : null,
            Visibility: visibility,
            Blockers: blockers);
    }

    /// <summary>
    /// True when the control is inside every clip that governs it, i.e. an ensure-visible pass has
    /// nothing left to do.
    /// </summary>
    public static bool IsFullyVisible(
        RuntimeSceneRect2Snapshot controlRect,
        IReadOnlyList<RuntimeSceneHoverClipSnapshot> clips)
        => Plan(controlRect, clips).Visibility.FullyVisible;

    /// <summary>
    /// The refusal a caller sees when the target cannot be reached, phrased so the next command is
    /// obvious: what was refused, which container hid it, and the flag that fixes it.
    /// </summary>
    public static string DescribeRefusal(string nodePath, RuntimeSceneHoverPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var culprits = plan.Blockers.Count > 0 ? plan.Blockers : plan.Visibility.ClippedBy;
        var by = culprits.Count == 0
            ? "the current viewport"
            : string.Join(", ", culprits.Select(clip => $"'{clip.NodePath}' ({clip.Reason})"));

        return $"Live runtime scene control '{nodePath}' is outside the reachable area: "
            + $"clipped away by {by}. Hovering it would return a position that clicks whatever is "
            + "painted there instead. Re-run with --ensure-visible to scroll it into view, or "
            + "--allow-offscreen to hover it anyway.";
    }

    /// <summary>Structured companions to <see cref="DescribeRefusal"/>, for machine callers.</summary>
    public static IReadOnlyList<RuntimeSceneDetail> RefusalDetails(
        string nodePath,
        RuntimeSceneHoverPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var details = new List<RuntimeSceneDetail>
        {
            new(
                "node_path",
                nodePath,
                "The control the hover refused; it resolved, it is just not where a click can reach."),
            new(
                "control_rect",
                FormatRect(plan.Visibility.ControlRect),
                "Global rect of the requested control, in viewport coordinates."),
        };

        var culprits = plan.Blockers.Count > 0 ? plan.Blockers : plan.Visibility.ClippedBy;
        foreach (var clip in culprits)
        {
            details.Add(new RuntimeSceneDetail(
                "clipped_by",
                clip.NodePath,
                $"{clip.Reason} rect {FormatRect(clip.Rect)} does not contain the control."));
        }

        details.Add(new RuntimeSceneDetail(
            "remedy",
            "--ensure-visible",
            "Scrolls the target's ancestor scroll containers into range and re-resolves the position."));
        return details;
    }

    public static string FormatRect(RuntimeSceneRect2Snapshot rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return $"({rect.Position.X:0.##}, {rect.Position.Y:0.##}) {rect.Size.X:0.##}x{rect.Size.Y:0.##}";
    }

    private static RuntimeSceneVector2Snapshot ChooseHoverPosition(
        RuntimeSceneRect2Snapshot controlRect,
        RuntimeSceneRect2Snapshot region)
    {
        var centre = Centre(controlRect);
        return Contains(region, centre) ? centre : Centre(region);
    }

    private static RuntimeSceneVector2Snapshot Centre(RuntimeSceneRect2Snapshot rect)
        => new(rect.Position.X + (rect.Size.X / 2), rect.Position.Y + (rect.Size.Y / 2));

    private static bool Contains(RuntimeSceneRect2Snapshot rect, RuntimeSceneVector2Snapshot point)
        => point.X >= rect.Position.X
            && point.X <= rect.Position.X + rect.Size.X
            && point.Y >= rect.Position.Y
            && point.Y <= rect.Position.Y + rect.Size.Y;

    private static RuntimeSceneRect2Snapshot Intersect(
        RuntimeSceneRect2Snapshot left,
        RuntimeSceneRect2Snapshot right)
    {
        var x0 = Math.Max(left.Position.X, right.Position.X);
        var y0 = Math.Max(left.Position.Y, right.Position.Y);
        var x1 = Math.Min(left.Position.X + left.Size.X, right.Position.X + right.Size.X);
        var y1 = Math.Min(left.Position.Y + left.Size.Y, right.Position.Y + right.Size.Y);
        return new RuntimeSceneRect2Snapshot(
            Position: new RuntimeSceneVector2Snapshot(x0, y0),
            Size: new RuntimeSceneVector2Snapshot(Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)));
    }

    private static bool IsTooThin(RuntimeSceneRect2Snapshot rect)
        => rect.Size.X < MinimumVisibleExtent || rect.Size.Y < MinimumVisibleExtent;

    private static bool SameRect(RuntimeSceneRect2Snapshot left, RuntimeSceneRect2Snapshot right)
        => Math.Abs(left.Position.X - right.Position.X) <= Epsilon
            && Math.Abs(left.Position.Y - right.Position.Y) <= Epsilon
            && Math.Abs(left.Size.X - right.Size.X) <= Epsilon
            && Math.Abs(left.Size.Y - right.Size.Y) <= Epsilon;
}
