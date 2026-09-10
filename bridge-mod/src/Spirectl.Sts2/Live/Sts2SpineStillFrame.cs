using System;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Godot-free choice of WHICH animation time a single-frame Spine bake samples. Pure, so it is offline
/// unit-testable like <see cref="Sts2SpineDefaults"/> / <see cref="Sts2SpineEventBackgroundClip"/>.
/// </summary>
/// <remarks>
/// <para>
/// A clip collapses to ONE frame when the client asks for <c>&amp;still=1</c>, when the subject is a genuine
/// full-screen background, or (round-8 item 14) when the host DEGRADES a bake under instance oversubscription.
/// That frame used to be t=0 — the pose the skeleton happens to hold at the start of the clip, which for most
/// one-shots is the wind-up/entry pose (frequently near-empty: a creature spawning in, a fully-transparent first
/// frame). The MID frame is the representative pose of an animation, so that is the default.
/// </para>
/// <para>
/// The exception is a TERMINAL animation (die/death/dead/defeat): its meaningful resting state is the END pose —
/// the corpse. Freezing a death animation to its middle shows the creature mid-collapse forever.
/// </para>
/// </remarks>
internal static class Sts2SpineStillFrame
{
    // Token prefixes that mark a terminal (one-way) animation. Matched per NAME TOKEN, not as a bare substring, so
    // an unrelated clip that merely contains the letters (e.g. "audience_idle") is never treated as terminal.
    private static readonly string[] TerminalPrefixes = ["die", "death", "dead", "defeat"];

    private static readonly char[] TokenSeparators = ['_', '-', '.', ' ', '/'];

    // ── Which branch chose the time ──────────────────────────────────────────────────────────────────
    //
    // These are REPORTED (the geoclip bake report's `sampleTimeSource`), so a reader of an artifact can tell a
    // pose that was asked for from one the heuristic guessed, and a corpse pose from a mid-clip pose, without
    // re-deriving the rule. They are constants rather than inline strings for the same reason the bake report's
    // rejection reasons are: a renamed token silently empties a field somebody is grading.

    /// <summary>The caller's <c>&amp;t=</c> won.</summary>
    public const string SampleSourceRequested = "requested";

    /// <summary>A terminal (die/defeat) animation: its LAST frame, the corpse.</summary>
    public const string SampleSourceTerminalEnd = "terminal-end";

    /// <summary>The default: the mid point of the clip.</summary>
    public const string SampleSourceMid = "mid";

    /// <summary>A zero-length / NaN / infinite duration: t=0 is the only sample that exists.</summary>
    public const string SampleSourceDegenerate = "degenerate";

    /// <summary>A chosen sample time together with WHICH rule chose it.</summary>
    public readonly record struct SpineStillSample(float Seconds, string Source);

    /// <summary>True when <paramref name="animationName"/> reads as a death/defeat animation.</summary>
    public static bool IsTerminalAnimation(string? animationName)
    {
        if (string.IsNullOrWhiteSpace(animationName))
        {
            return false;
        }

        foreach (var token in animationName.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var prefix in TerminalPrefixes)
            {
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The animation time (seconds) a single-frame bake of <paramref name="animationName"/> should sample:
    /// the caller's <paramref name="requestedSeconds"/> when supplied, else the MID point of the clip, or its
    /// LAST frame for a terminal (die/defeat) animation. Returns 0 for a zero-length / degenerate duration (the
    /// only sample that exists).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="requestedSeconds"/> (the <c>&amp;t=</c> selector) exists because the mid/end heuristic only
    /// guesses where an animation "rests" — but a track the GAME has explicitly PAUSED has an authoritative resting
    /// time, and it is not the middle. The treasure chest is the canonical case: its sole clip is named "animation"
    /// (the lid opening) and the room freezes it with <c>SetTimeScale(0)</c> at t=0 for a CLOSED chest, so the
    /// mid-clip default renders a half-open lid on a chest nobody has touched. The producer already streams
    /// <c>spinePaused</c> + <c>spineTrackTime</c>; this lets a client ask for exactly that frame.
    /// </para>
    /// <para>
    /// Clamped into [0, duration]: a client's track time is wall-clock derived and may overshoot a clip's end by a
    /// frame, and a negative/NaN/infinite request is ignored entirely (falls back to the heuristic) so a garbled
    /// query can never address anything outside the clip.
    /// </para>
    /// </remarks>
    public static float ChooseSampleTime(string? animationName, float durationSeconds, float? requestedSeconds = null)
        => ChooseSample(animationName, durationSeconds, requestedSeconds).Seconds;

    /// <summary>
    /// <see cref="ChooseSampleTime"/> plus WHICH of its branches produced the answer
    /// (<see cref="SampleSourceRequested"/> / <see cref="SampleSourceTerminalEnd"/> / <see cref="SampleSourceMid"/>
    /// / <see cref="SampleSourceDegenerate"/>).
    /// </summary>
    /// <remarks>
    /// The two are ONE function, not a value and a second function that re-derives which branch it must have
    /// taken. A separate classifier is free to drift from the chooser — and it would drift silently, because a
    /// mislabelled source still carries a plausible time. <see cref="ChooseSampleTime"/> is the projection.
    /// </remarks>
    public static SpineStillSample ChooseSample(
        string? animationName,
        float durationSeconds,
        float? requestedSeconds = null)
    {
        if (!(durationSeconds > 0f) || float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds))
        {
            return new SpineStillSample(0f, SampleSourceDegenerate);
        }

        if (requestedSeconds is { } requested
            && requested >= 0f
            && !float.IsNaN(requested)
            && !float.IsInfinity(requested))
        {
            return new SpineStillSample(Math.Min(requested, durationSeconds), SampleSourceRequested);
        }

        return IsTerminalAnimation(animationName)
            ? new SpineStillSample(durationSeconds, SampleSourceTerminalEnd)
            : new SpineStillSample(durationSeconds * 0.5f, SampleSourceMid);
    }
}
