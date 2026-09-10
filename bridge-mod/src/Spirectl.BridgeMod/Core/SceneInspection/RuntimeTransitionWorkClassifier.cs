using System;

namespace Spirectl.Sts2.Core.SceneInspection;


public static class RuntimeTransitionWorkClassifier
{
    // Transition quiescence is determined by POSITIVE "room intro complete" signals,
    // not by trying to wait for every ambient tween/animation to settle. We wait for:
    //   1. the room/scene entrance transition to finish (screen stops being black), and
    //   2. known one-shot intro animations (e.g. the combat "Your turn" banner, the
    //      ancient title+epithet) to stop showing.
    // Ambient perpetual VFX (campfire fire, merchant idle, particles) are ignored by
    // construction because they are not on this allowlist.

    // A visible intro-animation node with effective alpha below this is treated as
    // faded-out / done (covers banners that linger invisibly or loop after their intro).
    public const double VisibleAlphaEpsilon = 0.01;

    // Node type-name fragments for the one-shot intro animations we wait for. Match by
    // "contains" so the fully-qualified runtime type name still matches. Extend as needed.
    private static readonly string[] IntroAnimationNodeTypes =
    {
        "NPlayerTurnBanner",
        "NAncientNameBanner",
    };

    public static bool IsIntroAnimationNodeType(string? typeName)
    {
        foreach (var token in IntroAnimationNodeTypes)
        {
            if (ContainsTypeName(typeName, token))
            {
                return true;
            }
        }

        return false;
    }

    public static RuntimeTransitionWorkClassification ClassifyIntroAnimation(
        bool visibleInTree,
        double modulateAlpha,
        bool? introTweenRunning = null)
    {
        // A hidden banner is always done.
        if (!visibleInTree)
        {
            return RuntimeTransitionWorkClassification.Ignored("intro-animation-idle", infinite: false);
        }

        // Prefer an authoritative "is the intro tween still running" signal when the banner
        // exposes one. Some banners (e.g. NAncientNameBanner) play their intro and then
        // PERSIST on screen at full modulate alpha — only their child labels fade, and the
        // node never frees itself — so the alpha heuristic below would block forever. When
        // we can read the driving tween, trust it: showing while it runs, done once it stops.
        if (introTweenRunning.HasValue)
        {
            return introTweenRunning.Value
                ? RuntimeTransitionWorkClassification.Blocking("intro-animation-showing", infinite: false)
                : RuntimeTransitionWorkClassification.Ignored("intro-animation-idle", infinite: false);
        }

        // Fall back to the modulate-alpha heuristic for transient banners that fade their own
        // alpha back to 0 (e.g. the combat "Your turn" banner, which also frees itself).
        return modulateAlpha > VisibleAlphaEpsilon
            ? RuntimeTransitionWorkClassification.Blocking("intro-animation-showing", infinite: false)
            : RuntimeTransitionWorkClassification.Ignored("intro-animation-idle", infinite: false);
    }

    public static RuntimeTransitionWorkClassification ClassifyGameTransition(bool? inTransition)
    {
        return inTransition == true
            ? RuntimeTransitionWorkClassification.Blocking("game-transition-active", infinite: false)
            : RuntimeTransitionWorkClassification.Ignored("game-transition-inactive", infinite: false);
    }

    private static bool ContainsTypeName(string? typeName, string expectedFragment)
        => typeName?.Contains(expectedFragment, StringComparison.Ordinal) == true;
}

public readonly record struct RuntimeTransitionWorkClassification(
    bool Blocks,
    bool IgnoredInfinite,
    string Reason,
    bool? Infinite)
{
    public static RuntimeTransitionWorkClassification Blocking(string reason, bool? infinite)
        => new(Blocks: true, IgnoredInfinite: false, Reason: reason, Infinite: infinite);

    public static RuntimeTransitionWorkClassification Ignored(string reason, bool? infinite)
        => new(Blocks: false, IgnoredInfinite: infinite == true, Reason: reason, Infinite: infinite);
}
