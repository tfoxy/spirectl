using Godot;

namespace Spirectl.Sts2.Live;

/// <summary>
/// The reads the two card-flight producers (<see cref="Sts2CardFlightHooks"/>, <see cref="Sts2DiscardFlightHooks"/>)
/// share. Both flights are the same effect pointed at a different mover, are authored from the same scalars, and
/// drag the same kind of trail behind them — so the property handles and the trail-stroke scan live once, here,
/// rather than being cloned into the second hook.
/// </summary>
internal static class Sts2CardFlightVfxProbe
{
    private const string TrailStrokeTypeName = "NCardTrail";

    // The flight VFXes' own script-registered field names. Read through Godot's property bridge rather than
    // System.Reflection: each class's generated GetGodotClassPropertyValue exposes every one of these, so
    // `Get(name)` is a direct, allocation-light native read on the game thread.
    internal static readonly StringName StartPosProp = "_startPos";
    internal static readonly StringName EndPosProp = "_endPos";
    internal static readonly StringName ControlPointOffsetProp = "_controlPointOffset";
    internal static readonly StringName DurationProp = "_duration";
    internal static readonly StringName SpeedProp = "_speed";
    internal static readonly StringName AccelProp = "_accel";
    internal static readonly StringName ArcDirProp = "_arcDir";

    // The trail VFX the flight creates and hands to its own parent.
    internal static readonly StringName TrailVfxProp = "_vfx";

    // The card node a discard fly MOVES (the shuffle's flier has no such handle — it moves itself).
    internal static readonly StringName CardProp = "_card";

    /// <summary>
    /// The instance id of a node property, or 0 when it is unset or already freed.
    /// </summary>
    internal static ulong ReadNodeId(Node owner, StringName property)
    {
        var node = owner.Get(property).AsGodotObject() as Node;
        return node is not null && GodotObject.IsInstanceValid(node) ? node.GetInstanceId() : 0UL;
    }

    /// <summary>
    /// The two <c>NCardTrail</c> Line2Ds (<c>Trails/OuterTrail</c>, <c>Trails/InnerTrail</c>) under a trail VFX root.
    /// Matched on the SCRIPT class name's last dot-segment, the same test the mirror client uses, so the
    /// <c>Trails</c> container and the <c>Sprites</c> particle branch can never be mistaken for a stroke. A bounded
    /// 2-level walk: the trail scene is four nodes deep and this runs once per flying card.
    /// </summary>
    internal static List<ulong> CollectTrailStrokeIds(Node trailRoot)
    {
        var ids = new List<ulong>(2);
        foreach (var child in trailRoot.GetChildren())
        {
            if (IsTrailStroke(child))
            {
                ids.Add(child.GetInstanceId());
                continue;
            }

            foreach (var grandchild in child.GetChildren())
            {
                if (IsTrailStroke(grandchild))
                {
                    ids.Add(grandchild.GetInstanceId());
                }
            }
        }

        return ids;
    }

    private static bool IsTrailStroke(Node node)
        => string.Equals(node.GetType().Name, TrailStrokeTypeName, StringComparison.Ordinal);
}
