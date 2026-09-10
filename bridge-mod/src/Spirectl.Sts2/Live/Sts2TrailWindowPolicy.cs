namespace Spirectl.Sts2.Live;

// R14 — the Godot-free rule deciding whether a card flight opens the SELF-ONLY suppression window on its trail
// VFX root (the window that stops the root re-sending its own transform for the flight's analytic lifetime).
//
// Two levers and the emit mode decide it:
//
//   * `trailSelfSuppress` is the feature's own kill switch. Off ⇒ never open, in either emit mode.
//
//   * GLOBAL emit mode opens the window whenever the feature is on. Every node's transform is streamed
//     self-contained there, so withholding one node's writes cannot disturb any other node.
//
//   * LOCAL emit mode opens it only when `localModeLever` is also on. A consumer in local mode composes a node's
//     transform down its emitted parent chain, so it has to be able to place the frozen root itself — from the
//     declarative flight hint — for the descendants underneath to land where the game puts them. Consumers that
//     cannot do that must keep receiving the root's stream, so this stays OFF by default and is turned on per
//     process only once every attached viewer is known to handle it (the CouchCoop server couples the lever to a
//     unanimous viewer capability vote).
//
// Split out of the watcher so the whole truth table is unit-testable offline: the call site is a `_Ready` hook deep
// inside the live host, which no offline test can reach.
internal static class Sts2TrailWindowPolicy
{
    internal static bool ShouldOpenTrailSelfWindow(bool trailSelfSuppress, bool emitLocalTransforms, bool localModeLever)
        => trailSelfSuppress && (!emitLocalTransforms || localModeLever);
}
