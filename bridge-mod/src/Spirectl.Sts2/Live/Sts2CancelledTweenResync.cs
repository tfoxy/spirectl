namespace Spirectl.Sts2.Live;

// Producer belt-and-braces for the rest-site refocus/first-click "description stuck invisible" defect — PURE and
// Godot-free (like Sts2ReparentEmit / Sts2RegionEmitCap) so it is unit-testable without the Godot-coupled watcher.
//
// Background. When a faded rest-site option is re-focused, the game arms a fade-IN tween on its description's opacity
// (0→1). If the unfocus completes and KILLS that fade-IN before its DEFERRED Finalize runs, two things go wrong on
// the producer side:
//   1. the killed record is marked Cancelled, so EmitHints is skipped — the client never gets the fade-IN hint; and
//   2. the opacity-suppression window was opened DURING Finalize (ResolveTweenEndpoint), which never ran — so
//      CancelTweenSuppression's "collapse an OPEN window to the just-elapsed sentinel" path is a NO-OP (there is no
//      window to collapse), and no forced settle re-emit happens.
// The producer then just streams the resting alpha as an ordinary volatile delta. On the client, the hide-latch armed
// by the EARLIER fade-OUT mis-clamps that plain resting-alpha write to 0 (it is value-identical to the pre-hide
// flash), so the description stays invisible. The client fix (held-restore short expiry + sweep self-heal) is the
// PRIMARY cure; this producer fix is the belt-and-braces: force ONE settled opacity re-emit of the node's LIVE alpha
// on the next capture tick — even with no window open — so the client always gets a fresh, un-suppressed alpha write.
//
// Scope: only a killed tween that carried a VISIBLE-target opacity step (a fade-IN, endAlpha > ~0.01). A killed
// fade-OUT (endAlpha ≈ 0) is left alone — the node is going hidden, so there is no restore to un-stick and forcing a
// re-emit would just re-ship the ≈0 the hide already covers.
//
// DEFAULT ON; SPIRECTL_SCENE_WATCH_CANCELLED_RISE_RESYNC=0 seeds it OFF (byte-identical to today: a killed tween only
// collapses an already-open suppression window, never forces a from-cold re-emit). Read on the game main thread (the
// Harmony Kill/Stop postfix, same thread as the capture loop), so the field writes race nothing.
internal static class Sts2CancelledTweenResync
{
    // The end alpha at/below which a killed opacity step is a fade-OUT and needs no resync (the node is hiding).
    internal const double VisibleEndAlphaEps = 0.01;

    // True when a killed tween should force ONE settled opacity re-emit of its target nodes' live alpha next tick.
    // Only for a killed tween that carried a VISIBLE-target opacity step (a fade-IN); a killed fade-OUT is left alone.
    internal static bool ShouldForceOpacityResync(bool enabled, bool hasOpacityStep, bool endAlphaVisible)
        => enabled && hasOpacityStep && endAlphaVisible;
}
