using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Fixture-authored transient combat VFX (e.g. a cardUpgrade preview for STONE_CRACKER),
/// injected into the combat snapshot so the renderer can be validated statically without
/// catching a live transient.
///
/// The bridge normally NEVER populates <c>StateCombatState.transient_effects</c> — a host
/// synthesizes those by time-bounding the <c>WatchCombatEvents</c> stream. This registry is
/// the fixture-only exception: the loader records authored effects here and
/// <c>Sts2StateProvider.ResolveCombatState</c> emits them. Reset on every combat load so a
/// prior fixture's effects never leak into the next snapshot.
/// </summary>
internal static class Sts2AuthoredTransientEffectsRegistry
{
    private static readonly object Gate = new();
    private static readonly List<StateCombatTransientEffectSnapshot> Effects = [];

    public static void Reset()
    {
        lock (Gate)
        {
            Effects.Clear();
        }
    }

    public static void Add(StateCombatTransientEffectSnapshot effect)
    {
        lock (Gate)
        {
            Effects.Add(effect);
        }
    }

    public static IReadOnlyList<StateCombatTransientEffectSnapshot> Snapshot()
    {
        lock (Gate)
        {
            return Effects.Count == 0
                ? Array.Empty<StateCombatTransientEffectSnapshot>()
                : Effects.ToArray();
        }
    }
}
