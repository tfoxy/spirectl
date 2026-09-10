using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spirectl.Sts2.Core.Artifacts;

namespace Spirectl.Sts2.Live;

// Godot-free core of the GEOCLIP BAKER (Sts2SpineGeoClipBaker): everything about turning a captured spine
// pose sequence into the `geoclip/0` artifact that does not need the game — the arming spec parse, the frame
// clock, the atlas page association, the src-rect derivation + UV renormalization, the rigid-vs-deforming
// track selection, and the manifest assembly itself. The Godot-typed half (RenderingServer mesh readback, the
// spine ClassDB hops, the offline SubViewport, the page
// PNG export) stays in the live-host-only glob.
//
// The seam between the two halves is GeoClipFrameSample: the baker captures one of those per sampled frame
// out of the engine, and everything after that is pure data. That is deliberate — the bake runs once, on a
// developer's machine, against a game we cannot run in CI, so as much of it as possible has to be provable
// offline.

// ── The arming spec ──────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One (scene, node, animation) bake target parsed out of SPIRECTL_SPINE_GEOCLIP_BAKE.
/// </summary>
/// <param name="PoseOnly">
/// Bake ONE frame at the pose <c>Sts2SpineStillFrame.ChooseSampleTime</c> picks, instead of the whole clip.
/// Set by <c>&amp;pose=1</c>, or implied by an explicit <c>&amp;t=</c>.
/// </param>
/// <param name="SampleTimeSeconds">
/// The explicit <c>&amp;t=</c> pose time, or null to let the mid/terminal heuristic choose.
/// </param>
internal sealed record GeoClipTarget(
    string ScenePath,
    string? NodePath,
    string? Animation,
    string Raw,
    bool PoseOnly = false,
    double? SampleTimeSeconds = null);

/// <summary>
/// One bake run's settings. Lives in the Godot-free core (rather than inside the live baker, where it started)
/// so the ON-DEMAND request lane's plan is offline-constructible and therefore offline-testable — including the
/// property that matters most about it, that it never touches <c>Sts2OneShotArmClaim</c>.
/// </summary>
/// <param name="Owner">
/// Which copy of this runtime produced the artifact. The baker has no EchoConfig, so this is carried into the
/// manifest's <c>bake</c> block instead. On the ENV lane it is the copy that WON the one-shot claim; on the
/// request lane it is simply this copy, because that lane does not claim.
/// </param>
/// <param name="PoseOnly">Run-wide default for <see cref="GeoClipTarget.PoseOnly"/>; a target may turn it on.</param>
/// <param name="SampleTimeSeconds">Run-wide default for <see cref="GeoClipTarget.SampleTimeSeconds"/>.</param>
/// <param name="DenseSweepOnly">
/// The acquisition KILL SWITCH. True disarms the walk arm (see <see cref="Sts2SpineGeoClipWalk"/>) and sweeps
/// every window densely, exactly as the baker did before the arm existed — so the two acquisitions can be A/B'd
/// against the same game session without a rebuild.
///
/// <para>Its default is <see cref="Sts2SpineGeoClipWalk.DenseSweepOnlyDefault"/> — ONE value both lanes take.
/// The env lane and the on-demand request lane used to carry their own literals and had drifted apart, so a
/// request-lane bake paid a walk that had been measured a loss and reverted, and then swept densely anyway —
/// the walk was pure cost on every one of those bakes.</para>
///
/// <para>RETRACTED: this used to quote that loss as "+227 ms byrdonis, +485 ms merchant per cold pose". Those
/// milliseconds were measured against a dense fallback of 38 579 candidates for the merchant's window A, and
/// the narrow tier now carries that same window for 10 035
/// (<c>NarrowTierAlone_FindsEveryMerchantCleanMeshFor10035ProbesInsteadOf38579</c>). A wasted walk is charged
/// against a fallback roughly a quarter the size, so the figures do not transfer and are withdrawn. The
/// probe-count facts stand and are re-measured by that test rather than quoted from a session.</para>
/// </param>
internal sealed record GeoClipConfig(
    string Owner,
    string RawSpec,
    string OutDir,
    IReadOnlyList<GeoClipTarget> Targets,
    int Fps,
    int StartDelaySeconds,
    int CandidateCap,
    int IndexSlack,
    int MaxFrames,
    int WaitSeconds,
    int? WindowBCap,
    bool StrictMeshFilter,
    bool PoseOnly = false,
    double? SampleTimeSeconds = null,
    bool DenseSweepOnly = Sts2SpineGeoClipWalk.DenseSweepOnlyDefault,
    // Atlas pages the CALLER already holds, by SHA-256 content id. A page whose id is in here is reported in the
    // manifest and NOT written; see SpineGeoClipBakeRequestSnapshot.KnownPageContentIds for the bound on what
    // that actually saves. Empty on the env lane, which has no caller to have anything.
    IReadOnlySet<string>? KnownPageContentIds = null,
    // A log-only investigation aid for validated meshes that no slot claimed. This is deliberately outside every
    // artifact/report DTO: it must not make a diagnostic toggle part of what a client is served.
    int ForeignCandidateDiagnosticsLimit = 0,
    long? MaxOutputBytes = null)
{
    internal GeoClipOutputBudget OutputBudget { get; } = new(MaxOutputBytes);
    /// <summary>Does <paramref name="target"/> bake one pose? Run-wide default OR the target's own opt-in.</summary>
    internal bool IsPoseOnly(GeoClipTarget target) => PoseOnly || target.PoseOnly;

    /// <summary>The <c>&amp;t=</c> in force for <paramref name="target"/>: its own, else the run-wide one.</summary>
    internal double? RequestedSampleTime(GeoClipTarget target) => target.SampleTimeSeconds ?? SampleTimeSeconds;
}

internal sealed class GeoClipOutputBudget(long? maximumBytes)
{
    private long _usedBytes;
    internal long? MaximumBytes { get; } = maximumBytes is < 0
        ? throw new ArgumentOutOfRangeException(nameof(maximumBytes)) : maximumBytes;
    internal long UsedBytes => _usedBytes;

    internal bool TryConsume(long bytes)
    {
        if (bytes < 0) return false;
        if (MaximumBytes is { } maximum && (_usedBytes > maximum - bytes)) return false;
        _usedBytes = checked(_usedBytes + bytes);
        return true;
    }
}

/// <summary>
/// Bounded, log-only summaries for validated mesh candidates which association left unclaimed. The data is
/// intentionally generic: a sweep-relative order plus geometry extents, never a raw RID, slot/attachment name,
/// resource path, or artifact payload. It exists to distinguish foreign geometry from an association gap without
/// changing the acceptance or refusal decision.
/// </summary>
internal static class Sts2SpineGeoClipForeignCandidateDiagnostics
{
    internal const int MaximumLimit = 8;

    internal sealed record Summary(
        int SweepOrder,
        int VertexCount,
        int IndexCount,
        string AtlasProvenance,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY,
        double MinU,
        double MinV,
        double MaxU,
        double MaxV);

    /// <summary>Resolve the environment lever. Zero is off; malformed and negative values stay safely off.</summary>
    internal static int ParseLimit(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, MaximumLimit)
            : 0;

    /// <summary>
    /// Format at most <paramref name="limit"/> summaries. An empty result means the lever is off or association
    /// left no foreign candidates, so the normal log stream is byte-for-byte unchanged.
    /// </summary>
    internal static IReadOnlyList<string> Format(IEnumerable<Summary> candidates, int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var resolvedLimit = Math.Clamp(limit, 0, MaximumLimit);
        if (resolvedLimit == 0)
        {
            return [];
        }

        return candidates
            .Take(resolvedLimit)
            .Select(candidate => string.Create(
                CultureInfo.InvariantCulture,
                $"provenance=sweep-order:{candidate.SweepOrder} geometry=vertices:{candidate.VertexCount} "
                    + $"indices:{candidate.IndexCount} atlas:{candidate.AtlasProvenance} "
                    + $"xy:[{candidate.MinX:0.###},{candidate.MinY:0.###}].."
                    + $"[{candidate.MaxX:0.###},{candidate.MaxY:0.###}] uv:[{candidate.MinU:0.###},"
                    + $"{candidate.MinV:0.###}]..[{candidate.MaxU:0.###},{candidate.MaxV:0.###}]"))
            .ToArray();
    }
}

internal static class Sts2SpineGeoClipSpec
{
    internal const int DefaultFps = 30;
    internal const int MinFps = 1;
    internal const int MaxFps = 120;

    /// <summary>
    /// Parse
    /// `&lt;sceneResPath&gt;?anim=&lt;name&gt;[&amp;node=&lt;relPath&gt;][&amp;pose=1][&amp;t=&lt;sec&gt;][;&lt;scene2&gt;?anim=...]`.
    /// A target whose animation is missing still parses (with a null <see cref="GeoClipTarget.Animation"/>) so the
    /// baker can log a named reason for it rather than the whole spec silently collapsing to nothing.
    ///
    /// <para><c>&amp;t=</c> IMPLIES <c>&amp;pose=1</c>: asking for one animation time is asking for one pose, and a
    /// whole-clip bake has no use for a single time. An explicit <c>&amp;pose=0</c> still wins, whichever order the
    /// two appear in, so the implication can be turned off without dropping the time.</para>
    ///
    /// <para>A spec that names neither key parses EXACTLY as before: whole clip, no requested time.</para>
    /// </summary>
    internal static IReadOnlyList<GeoClipTarget> ParseTargets(string? raw)
    {
        var targets = new List<GeoClipTarget>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return targets;
        }

        foreach (var chunk in raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var question = chunk.IndexOf('?');
            var scenePath = (question < 0 ? chunk : chunk[..question]).Trim();
            if (scenePath.Length == 0)
            {
                continue;
            }

            string? anim = null;
            string? node = null;
            bool? explicitPoseOnly = null;
            double? sampleTime = null;
            if (question >= 0)
            {
                foreach (var pair in chunk[(question + 1)..].Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = pair.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    var key = pair[..equals].Trim();
                    var value = pair[(equals + 1)..].Trim();
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (key.Equals("anim", StringComparison.OrdinalIgnoreCase))
                    {
                        anim = value;
                    }
                    else if (key.Equals("node", StringComparison.OrdinalIgnoreCase))
                    {
                        node = value;
                    }
                    else if (key.Equals("pose", StringComparison.OrdinalIgnoreCase))
                    {
                        explicitPoseOnly = IsTruthy(value);
                    }
                    else if (key.Equals("t", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        && parsed >= 0d
                        && !double.IsNaN(parsed)
                        && !double.IsInfinity(parsed))
                    {
                        // A garbled or negative time is DROPPED rather than clamped: the heuristic is a better
                        // answer than a number nobody meant, and the baker still reports which rule it used.
                        sampleTime = parsed;
                    }
                }
            }

            targets.Add(new GeoClipTarget(
                scenePath, node, anim, chunk, explicitPoseOnly ?? sampleTime is not null, sampleTime));
        }

        return targets;
    }

    private static bool IsTruthy(string value)
        => value is "1" or "true" or "TRUE" or "True" or "yes" or "on";

    internal static int ParseFps(string? raw, int fallback = DefaultFps)
        => int.TryParse((raw ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, MinFps, MaxFps)
            : fallback;

    /// <summary>Artifact directory name for a target: one directory per (scene, node, anim).</summary>
    internal static string DirectoryName(string scenePath, string node, string anim)
    {
        var scene = scenePath;
        var lastSlash = scene.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            scene = scene[(lastSlash + 1)..];
        }

        var dot = scene.LastIndexOf('.');
        if (dot > 0)
        {
            scene = scene[..dot];
        }

        var nodePart = node is "." or "" ? "root" : node;
        return $"{Sanitize(scene)}--{Sanitize(nodePart)}--{Sanitize(anim)}";
    }

    internal static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}

// ── The capture seam ─────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Conservative classification for a present Spine attachment. A path-control attachment has no drawable mesh;
/// every other result, including a missing or failed native read, remains drawable so the baker keeps its strict
/// refusal rather than hiding a capture failure.
/// </summary>
internal static class Sts2SpineGeoClipAttachmentClassification
{
    internal const string NoAttachment = "no-attachment";
    internal const string AttachmentReadUnavailable = "attachment-read-unavailable";
    internal const string PathConstraintTarget = "path-constraint-target";
    internal const string NativePathAttachment = "native-class:SpinePathAttachment";
    internal const string DrawingDefault = "drawing-default";

    internal sealed record Result(bool RequiresDrawing, string Source);

    internal static Result Classify(
        bool attachmentReadSucceeded,
        bool hasAttachment,
        string? attachmentWrapperClass,
        bool isPathConstraintTarget)
    {
        if (!attachmentReadSucceeded)
        {
            return new Result(RequiresDrawing: true, AttachmentReadUnavailable);
        }

        if (!hasAttachment)
        {
            return new Result(RequiresDrawing: false, NoAttachment);
        }

        // Kept for a future binding that exposes the concrete native wrapper directly. The currently installed
        // binding presents all attachment variants as SpineAttachment, so this exact check is intentionally not
        // the primary discriminator.
        if (string.Equals(attachmentWrapperClass, "SpinePathAttachment", StringComparison.Ordinal))
        {
            return new Result(RequiresDrawing: false, NativePathAttachment);
        }

        if (isPathConstraintTarget)
        {
            return new Result(RequiresDrawing: false, PathConstraintTarget);
        }

        return new Result(RequiresDrawing: true, DrawingDefault);
    }
}

/// <summary>
/// WHICH DRAWABLE SLOTS A BAKE IS GRADED ON. <see cref="Sts2SpineGeoClipAttachmentClassification"/> answers
/// "could this attachment ever produce a mesh"; this answers the narrower question "is this slot expected to
/// produce geometry at the pose(s) THIS bake actually sampled", and it differs from the first in exactly one
/// case: a slot whose colour was READ at that pose and read back with alpha 0.
///
/// <para>WHY THAT CASE IS SOUND. A slot at alpha 0 multiplies its attachment to nothing, so the renderer puts no
/// pixel on screen for it. The baker already emits such a slot as HIDDEN when it has no associated mesh, and a
/// hidden slot and a slot drawn at alpha 0 are the same picture — so refusing the whole artifact over it
/// discards a bake that is pixel-identical to the one that would have been admitted. The live case this was
/// written for is the spectral knight at <c>anim=attack_sword</c>: two slots sharing the <c>sword swish</c>
/// attachment, byte-identical uv boxes, overlapping positions and BOTH at <c>(1,1,1,0)</c> at the sampled pose
/// (measured, not inferred — archived manifest
/// <c>.sts2/research/data/geoclip-knights-20260908T0500Z/envbake-artifacts/legE-combat/spectral_knight--Visuals--attack_sword/manifest.json</c>,
/// <c>frames[0].slots["29"]</c> and <c>["30"]</c>; the same two colours are pinned as a fixture in
/// <see cref="Sts2SpineGeoClipSlotColorIdentity"/>'s tests). Nothing can separate them — not the atlas, not the
/// colour nudge, not the slot-colour tie-break — and nothing needs to, because neither draws.</para>
///
/// <para>SCOPED TO A SINGLE-POSE BAKE, AND THE SCOPE IS THE WHOLE SAFETY ARGUMENT. Read this before
/// generalising it. A standing rule from the 2026-09-06 correction contract
/// (<c>.sts2/research/data/knights-sequential-20260906T065302Z/report.md</c> §"Correction contract for the next
/// implementation") says: "Do not infer non-drawing status from a failed search, missing atlas name, zero alpha
/// or failed association." That rule is RIGHT for a multi-frame clip — a slot transparent at one sampled frame
/// may be opaque at the next, so its alpha at one sample says nothing about the clip — and it is vacuous for a
/// bake that sampled exactly ONE pose, where "at another frame" does not exist and the artifact can only ever be
/// shown at that pose. So the exception fires only when <c>poses.Count == 1</c>. A multi-frame bake keeps every
/// drawable slot expected, unchanged. DO NOT "simplify" this into the general rule; that is the thing the
/// contract forbids, and the single-pose guard is what makes this legal.</para>
///
/// <para>THE OTHER THREE CLAUSES OF THAT RULE STAND UNTOUCHED. A failed search, a missing atlas region and a
/// failed association still leave a slot fully expected — this arm never consults any of them, and cannot: it
/// runs off the pose capture alone, before association has an opinion. Alpha is the ONLY new signal.</para>
///
/// <para>AND ONLY A READ ALPHA. <see cref="SlotPose.ColorReadSucceeded"/> exists because an unreadable slot
/// colour is recorded as WHITE, which is indistinguishable from a slot that genuinely is white; an unread colour
/// therefore keeps the slot expected. The test is <c>alpha &lt;= 0</c> with NO tolerance, deliberately: a slot at
/// alpha 1/255 does draw, faintly, and an epsilon here would start deciding that faint things do not count.</para>
/// </summary>
internal static class Sts2SpineGeoClipPoseTransparency
{
    /// <summary>Kill switch. <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> expects every drawable slot again,
    /// exactly as before this arm existed — the same reading every other geoclip kill switch takes.</summary>
    internal const string ArmEnv = "SPIRECTL_SPINE_GEOCLIP_ALPHA0_SINGLE_POSE";

    /// <summary>
    /// One slot as the pose capture read it. <paramref name="Alpha"/> is the slot's own colour alpha at that
    /// pose; a capture that could not read a colour passes <c>ColorReadSucceeded: false</c> and whatever
    /// fallback it carries, which this rule then ignores.
    /// </summary>
    internal readonly record struct SlotPose(
        int Ordinal,
        bool RequiresDrawing,
        bool ColorReadSucceeded,
        double Alpha);

    /// <summary>
    /// The ordinals a bake is graded on, plus the ordinals this arm dropped and why it was allowed to. Both are
    /// reported: <see cref="TransparentOrdinals"/> is what stops a reduced <c>slotsEverVisible</c> from silently
    /// misdescribing the rig.
    /// </summary>
    internal sealed record Expectation(
        HashSet<int> Ordinals,
        IReadOnlyList<int> TransparentOrdinals,
        bool Armed,
        bool SinglePose);

    /// <summary>
    /// Every slot that shows a drawable attachment at any sampled pose, minus — on a SINGLE-pose bake only, and
    /// only while armed — those whose colour was read fully transparent at that pose.
    /// </summary>
    /// <param name="armed">
    /// Null reads <see cref="ArmEnv"/>. Passed explicitly by tests and by anything that has already resolved the
    /// lever, so the rule itself stays a pure function of its arguments.
    /// </param>
    internal static Expectation Expect(
        IReadOnlyList<IReadOnlyList<SlotPose>> poses,
        bool? armed = null)
    {
        ArgumentNullException.ThrowIfNull(poses);
        var ordinals = new HashSet<int>();
        foreach (var pose in poses)
        {
            foreach (var slot in pose)
            {
                if (slot.RequiresDrawing)
                {
                    ordinals.Add(slot.Ordinal);
                }
            }
        }

        var isArmed = armed ?? ArmedFromEnvironment();
        var singlePose = poses.Count == 1;
        if (!isArmed || !singlePose)
        {
            return new Expectation(ordinals, [], isArmed, singlePose);
        }

        var transparent = new List<int>();
        foreach (var slot in poses[0])
        {
            // ColorReadSucceeded first: an unread colour is white by fallback, and white has alpha 1, so the
            // order is belt-and-braces rather than load-bearing — but a future fallback that is not white would
            // otherwise turn a capture failure into a dropped expectation.
            if (slot.RequiresDrawing && slot.ColorReadSucceeded && slot.Alpha <= 0 && ordinals.Remove(slot.Ordinal))
            {
                transparent.Add(slot.Ordinal);
            }
        }

        transparent.Sort();
        return new Expectation(ordinals, transparent, isArmed, singlePose);
    }

    internal static bool ParseArmed(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    internal static bool ArmedFromEnvironment()
        => ParseArmed(Environment.GetEnvironmentVariable(ArmEnv));
}

/// <summary>
/// One slot at one sampled frame, as the baker read it out of the engine. <c>HasGeometry</c> is false when the
/// drawable slot's mesh read back empty (the slot is hidden, or its mesh could not be associated) — which is NOT
/// the same as <c>AttachmentName is null</c>. <see cref="RequiresDrawing"/> separately records present path-control
/// attachments, which intentionally have no mesh and must not be diagnosed as an empty drawable mesh.
/// </summary>
internal sealed record GeoClipSlotSample(
    int SlotIndex,
    string? AttachmentName,
    double[] Color,
    int BlendMode,
    bool HasGeometry,
    double[] VertsX,
    double[] VertsY,
    double[] U,
    double[] V,
    int[] Indices)
{
    internal bool RequiresDrawing { get; init; } = true;

    internal static GeoClipSlotSample Hidden(
        int slotIndex,
        double[] color,
        int blendMode,
        string? attachmentName = null,
        bool? requiresDrawing = null)
        => new(slotIndex, attachmentName, color, blendMode, HasGeometry: false, [], [], [], [], [])
        {
            RequiresDrawing = requiresDrawing ?? attachmentName is not null,
        };
}

/// <summary>One sampled frame: the skeleton's own bounds and draw order plus every slot's state.</summary>
internal sealed record GeoClipFrameSample(
    double T,
    int[] DrawOrder,
    double[] Bounds,
    IReadOnlyList<GeoClipSlotSample> Slots);

// ── The geoclip/0 artifact ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// WHERE THE CLIP SITS, stated rather than inferred: the bake's canvas, and the node-LOCAL rect that canvas
/// occupies under the live node's own transform.
///
/// <para>WHAT IT RETIRES. A geoclip's vertices are in skeleton-local units, and until now nothing in the
/// manifest said where the skeleton origin sits — so a client had to mount the geoclip behind the RASTER clip
/// for the same identity and INVERT that clip's placement to recover the mapping. That is why a live geoclip
/// could not draw until its baked clip had landed. The same three numbers the raster lane derives
/// (<c>Sts2SceneFitFrame.Place</c>) are emitted here directly, from the same function, so the mapping is exact
/// by construction rather than by agreement.</para>
///
/// <para>THE CLIENT'S ARITHMETIC, for the record: it computes <c>s = localWidth / canvasWidth</c>, which is
/// <c>1 / fitScale</c>, then <c>inverse = 1/s = fitScale</c> and <c>offsetX = -localX * inverse</c>, which is
/// the bake's own <c>nodePositionX</c>. So its fit is <c>canvasX = skelX * fitScale + nodePositionX</c> —
/// precisely what <c>Sts2SceneFitFrame.Fit</c> mapped. <c>fitScale</c> is carried too so a consumer never has to
/// recover it by division.</para>
///
/// <para>ADDITIVE AND OPTIONAL: <c>meta.schema</c> stays <c>geoclip/0</c>, and the key is omitted entirely when
/// no placement was measured, so a player that has never heard of it is still correct.</para>
/// </summary>
internal sealed record GeoClipPlacement(
    [property: JsonPropertyName("canvasWidth")] int CanvasWidth,
    [property: JsonPropertyName("canvasHeight")] int CanvasHeight,
    [property: JsonPropertyName("localX")] double LocalX,
    [property: JsonPropertyName("localY")] double LocalY,
    [property: JsonPropertyName("localWidth")] double LocalWidth,
    [property: JsonPropertyName("localHeight")] double LocalHeight,
    [property: JsonPropertyName("fitScale")] double FitScale)
{
    /// <summary>
    /// Adopt the shared placement algebra verbatim. NOT re-rounded: the raster lane's <c>clipPlacement</c> is the
    /// same float arithmetic widened into the same double fields, so the two compare EQUAL for one rig — which is
    /// the only way "the geoclip and the raster still agree about placement" can be a computed check rather than
    /// an eyeball.
    /// </summary>
    internal static GeoClipPlacement From(Sts2SceneFitFrame.FitPlacement placement)
        => new(
            placement.CanvasWidth,
            placement.CanvasHeight,
            placement.LocalX,
            placement.LocalY,
            placement.LocalWidth,
            placement.LocalHeight,
            placement.FitScale);
}

internal sealed record GeoClipMeta(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("scene")] string Scene,
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("anim")] string Anim,
    [property: JsonPropertyName("fps")] int Fps,
    [property: JsonPropertyName("frameCount")] int FrameCount,
    [property: JsonPropertyName("durationMs")] double DurationMs,
    [property: JsonPropertyName("boundsPerFrame")] IReadOnlyList<double[]> BoundsPerFrame,
    // See GeoClipPlacement: additive and optional, so it is omitted rather than written null when the bake could
    // not measure one (the same rule the `bake` report block below follows).
    [property: JsonPropertyName("placement")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GeoClipPlacement? Placement = null);

internal sealed record GeoClipPage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    // ADDITIVE and OPTIONAL, like `meta.placement` and the `bake` block: `meta.schema` stays "geoclip/0" because
    // this says nothing about how to DRAW the page. It is the lowercase SHA-256 of the page's own bytes, so an
    // adopting store can name the file it is about to publish WITHOUT re-hashing it — and so a bake can be told
    // "the caller already holds this one" and skip writing the PNG at all (see Sts2SpineGeoClipPageId).
    // Omitted when the bytes were never in hand.
    [property: JsonPropertyName("sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Sha256 = null);

internal sealed record GeoClipPart(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("slotIndex")] int SlotIndex,
    [property: JsonPropertyName("attachmentName")] string AttachmentName,
    [property: JsonPropertyName("pageId")] int PageId,
    [property: JsonPropertyName("srcRect")] int[] SrcRect,
    [property: JsonPropertyName("indices")] int[] Indices,
    [property: JsonPropertyName("uvs")] double[] Uvs,
    [property: JsonPropertyName("refVerts")] double[] RefVerts,
    [property: JsonPropertyName("refFrame")] int RefFrame,
    [property: JsonPropertyName("rigid")] bool Rigid,
    [property: JsonPropertyName("blendMode")] int BlendMode);

internal sealed record GeoClipSlotFrame(
    [property: JsonPropertyName("part")] int? Part,
    [property: JsonPropertyName("color")] double[] Color,
    [property: JsonPropertyName("xform")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Xform,
    [property: JsonPropertyName("verts")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Verts);

internal sealed record GeoClipFrame(
    [property: JsonPropertyName("t")] double T,
    [property: JsonPropertyName("drawOrder")] int[] DrawOrder,
    [property: JsonPropertyName("slots")] IReadOnlyDictionary<string, GeoClipSlotFrame> Slots);

internal sealed record GeoClipDocument(
    [property: JsonPropertyName("meta")] GeoClipMeta Meta,
    [property: JsonPropertyName("pages")] IReadOnlyList<GeoClipPage> Pages,
    [property: JsonPropertyName("parts")] IReadOnlyList<GeoClipPart> Parts,
    [property: JsonPropertyName("frames")] IReadOnlyList<GeoClipFrame> Frames,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    // ADDITIVE and OPTIONAL. `meta.schema` stays "geoclip/0": the report says nothing about how to DRAW the
    // clip, only about how completely it was acquired, so a player that has never heard of it is still correct.
    // Omitted entirely when the builder was handed no report (the harness tolerates unknown keys, but a key that
    // is sometimes null and sometimes an object is worse than one that is sometimes absent).
    [property: JsonPropertyName("bake")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GeoClipBakeReport? Bake = null);

// ── The bake report (additive; see GeoClipDocument.Bake) ─────────────────────────────────────────────

/// <summary>
/// What the WALK ARM did on one window (see <see cref="Sts2SpineGeoClipWalk"/>). Nested inside
/// <see cref="GeoClipBakeWindow"/> rather than reported alongside it, because every number here is only
/// interpretable against that window's own candidate space — and because a reader grading a bake must be able to
/// tell "the walk found all 30" from "the walk found 26 and the dense sweep found the rest".
/// </summary>
internal sealed record GeoClipBakeWalk(
    // The column scan: how many index columns were swept, and how many candidates that cost.
    [property: JsonPropertyName("columnsPlanned")] int ColumnsPlanned,
    [property: JsonPropertyName("columnsScanned")] int ColumnsScanned,
    [property: JsonPropertyName("columnsSkipped")] int ColumnsSkipped,
    [property: JsonPropertyName("anchorProbed")] long AnchorProbed,
    [property: JsonPropertyName("anchorHypotheses")] int AnchorHypotheses,
    [property: JsonPropertyName("deadAnchors")] int DeadAnchors,
    [property: JsonPropertyName("runs")] int Runs,
    // The measured progression. `strideSource` is "measured" when the ladder found a three-point arithmetic
    // progression, "assumed" when it never ran, "default" when it ran and found nothing.
    [property: JsonPropertyName("strideValidator")] uint StrideValidator,
    [property: JsonPropertyName("strideIndex")] uint StrideIndex,
    [property: JsonPropertyName("strideSource")] string StrideSource,
    [property: JsonPropertyName("strideProbed")] long StrideProbed,
    // The two walks that share each anchor: the LINE sweep (constant stride, spans index gaps) and the
    // INDEX walk (index +1 with a bounded validator reach, survives an irregular validator stride).
    [property: JsonPropertyName("lineProbed")] long LineProbed,
    [property: JsonPropertyName("lineFound")] int LineFound,
    [property: JsonPropertyName("indexWalkProbed")] long IndexWalkProbed,
    [property: JsonPropertyName("indexWalkFound")] int IndexWalkFound,
    [property: JsonPropertyName("stopUp")] string StopUp,
    [property: JsonPropertyName("stopDown")] string StopDown,
    [property: JsonPropertyName("gapSizes")] IReadOnlyList<int> GapSizes,
    [property: JsonPropertyName("indexExtentLow")] uint IndexExtentLow,
    [property: JsonPropertyName("indexExtentHigh")] uint IndexExtentHigh,
    [property: JsonPropertyName("stopReason")] string StopReason,
    [property: JsonPropertyName("budgetExhausted")] bool BudgetExhausted,
    [property: JsonPropertyName("probed")] long Probed,
    [property: JsonPropertyName("found")] int Found,
    [property: JsonPropertyName("ms")] double Ms);

/// <summary>
/// One swept RID window. The baker sweeps TWO of them (see <see cref="Sts2SpineGeoClipSweep"/>), and the
/// per-window <c>found</c> counts are the only honest proof that a bake is complete: a total says nothing about
/// WHICH window paid for it, and window A's count is the regression anchor against the Phase-1 numbers.
///
/// <para><c>probed</c> / <c>found</c> / <c>sweepMs</c> are the window's TOTALS across whichever arms ran, and
/// <c>arm</c> says which those were: <c>walk</c>, <c>dense</c>, or <c>walk-then-dense</c> when the walk
/// under-delivered and the dense sweep was armed behind it. The nested <c>walk</c> block carries the walk's own
/// half of those totals, so the two are never confused.</para>
/// </summary>
internal sealed record GeoClipBakeWindow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("validatorLow")] uint ValidatorLow,
    [property: JsonPropertyName("validatorHigh")] uint ValidatorHigh,
    [property: JsonPropertyName("indexLow")] uint IndexLow,
    [property: JsonPropertyName("indexHigh")] uint IndexHigh,
    [property: JsonPropertyName("validatorSpan")] long ValidatorSpan,
    [property: JsonPropertyName("totalCandidates")] long TotalCandidates,
    [property: JsonPropertyName("cap")] int Cap,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("probed")] long Probed,
    [property: JsonPropertyName("found")] int Found,
    [property: JsonPropertyName("sweepMs")] double SweepMs,
    [property: JsonPropertyName("recommendedCap")] long RecommendedCap,
    [property: JsonPropertyName("capSource")] string CapSource,
    [property: JsonPropertyName("capAutoRaisedFrom")] int CapAutoRaisedFrom,
    [property: JsonPropertyName("capAutoRaisedTo")] int CapAutoRaisedTo,
    [property: JsonPropertyName("rejectedByReason")] IReadOnlyDictionary<string, int> RejectedByReason,
    [property: JsonPropertyName("arm")] string Arm = Sts2SpineGeoClipWalk.ArmDense,
    // Why the walk arm did not run, or why it was not enough. Always present so a reader never has to infer it
    // from an absent key; "ok" when the walk carried the window on its own.
    [property: JsonPropertyName("armNote")] string ArmNote = "",
    [property: JsonPropertyName("walk")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GeoClipBakeWalk? Walk = null,
    // WHICH DENSE TIERS ran on this window: `narrow`, `narrow-then-wide`, `wide`, or `none` when the walk arm
    // carried it. The five numbers beside it are what a later measurement attributes the saving with — the
    // narrow tier's own band, candidates, probes and finds against the window's `totalCandidates` / `probed`.
    // Reported per window rather than per bake because the two windows narrow by very different amounts (the
    // recorded plans: window A keeps 45 of 173 index columns, window B 94 of 225).
    [property: JsonPropertyName("tier")] string Tier = Sts2SpineGeoClipSweep.TierNone,
    [property: JsonPropertyName("coreIndexLow")] uint CoreIndexLow = 0,
    [property: JsonPropertyName("coreIndexHigh")] uint CoreIndexHigh = 0,
    [property: JsonPropertyName("narrowCandidates")] long NarrowCandidates = 0,
    [property: JsonPropertyName("narrowProbed")] long NarrowProbed = 0,
    [property: JsonPropertyName("narrowFound")] int NarrowFound = 0,
    // Whether the NARROW tier's own plan hit the cap. Separate from `truncated`, which rolls up the ordinary wide
    // plan and any index-recovery strip; their exact internal plans feed the bake-level note while the wire keeps
    // its established single truncation signal.
    [property: JsonPropertyName("narrowTruncated")] bool NarrowTruncated = false)
{
    [JsonIgnore]
    internal bool? OrdinaryTruncated { get; init; }

    /// <summary>
    /// Every index-recovery strip this window swept, in the order the driver ran them (floor, then ceiling).
    /// A list rather than one strip because the recovery tier now covers the hull in BOTH directions and either
    /// half can be the one that hits its cap.
    /// </summary>
    [JsonIgnore]
    internal IReadOnlyList<GeoClipBakeIndexRecovery> IndexRecoveries { get; init; } = [];
}

internal sealed record GeoClipBakeIndexRecovery(
    Sts2SpineGeometryMath.RidCandidatePlan Plan,
    long Probed,
    int Found);

internal sealed record GeoClipBakeReport(
    [property: JsonPropertyName("bakerVersion")] string BakerVersion,
    // Which ASSEMBLY IDENTITY of this runtime produced the artifact — "Spirectl.Sts2" from the bridge mod,
    // "CouchCoop.Spirectl" from the embedded copy. A loadout can hold both, and a single env var reaches both, so
    // the manifest has to be able to say which one won the one-shot claim (see Sts2OneShotArmClaim). The baker has
    // no EchoConfig to carry it, unlike the geometry probe's report.
    [property: JsonPropertyName("owner")] string Owner,
    // POSE-ONLY: one frame, sampled at the time Sts2SpineStillFrame.ChooseSampleTime picks. It changes what the
    // three counters below MEAN — `slotsEverVisible` becomes "visible AT THIS POSE", because a slot with no
    // attachment at the sampled pose has no surface to acquire and contributes nothing — and therefore what
    // `complete` asserts. A reader must not compare a pose-only bake's counters against a whole-clip bake's.
    // It also scopes the one-pose transparency rule below (`slotsTransparentAtPose`).
    [property: JsonPropertyName("poseOnly")] bool PoseOnly,
    [property: JsonPropertyName("sampleTimeSeconds")] double SampleTimeSeconds,
    // Which rule chose that time: Sts2SpineStillFrame.SampleSource* (`requested` / `terminal-end` / `mid` /
    // `degenerate`), or `clip` when the bake was not pose-only and no single pose was selected.
    [property: JsonPropertyName("sampleTimeSource")] string SampleTimeSource,
    [property: JsonPropertyName("slots")] int Slots,
    // The slots this bake is GRADED on: `associated` and `unassociated` are its two halves, and `complete` is
    // `associated == this`. On a single-pose bake it is the drawable-attachment count MINUS the slots read fully
    // transparent at that pose — see `slotsTransparentAtPose` at the end of this block, which is what lets a
    // reader recover the raw drawable count. On a multi-frame bake nothing is subtracted.
    [property: JsonPropertyName("slotsEverVisible")] int SlotsEverVisible,
    [property: JsonPropertyName("slotsVisibleAtAcquisition")] int SlotsVisibleAtAcquisition,
    [property: JsonPropertyName("acquisitionFrame")] int AcquisitionFrame,
    [property: JsonPropertyName("windows")] IReadOnlyList<GeoClipBakeWindow> Windows,
    [property: JsonPropertyName("meshesValidated")] int MeshesValidated,
    [property: JsonPropertyName("meshFilterStrict")] bool MeshFilterStrict,
    [property: JsonPropertyName("associated")] int Associated,
    [property: JsonPropertyName("unassociated")] int Unassociated,
    [property: JsonPropertyName("byColorFlip")] int ByColorFlip,
    [property: JsonPropertyName("byColorFlipPlusAtlas")] int ByColorFlipPlusAtlas,
    [property: JsonPropertyName("byElimination")] int ByElimination,
    [property: JsonPropertyName("byAtlasRegion")] int ByAtlasRegion,
    [property: JsonPropertyName("ambiguousColorFlip")] int AmbiguousColorFlip,
    [property: JsonPropertyName("ambiguousAtlasRegion")] int AmbiguousAtlasRegion,
    // How many validated meshes inside the bracket no slot claimed. A REPORTED counter, and no longer a refusal
    // on its own: a rig mints geometry for attachments that are not drawable at the sampled pose, so leftovers
    // are ordinary. What decides admission is `claimsUnproven` beside it — see Sts2SpineGeoClipOwnership.
    [property: JsonPropertyName("foreignMeshes")] int ForeignMeshes,
    [property: JsonPropertyName("staleMeshFrames")] int StaleMeshFrames,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("atlasFallbackAvailable")] bool AtlasFallbackAvailable,
    [property: JsonPropertyName("associationPairs")] IReadOnlyList<int[]> AssociationPairs,
    // WHICH ANIMATION the slot↔mesh association was measured at. Equal to `meta.anim` on a single-animation
    // bake; on a RIG bake (N poses, one scene load) it names the pose that showed the most slots, which is the
    // one every pose's association actually comes from — so `acquisitionFrame` is read against THIS animation's
    // frames, not against `meta.anim`'s.
    [property: JsonPropertyName("acquisitionAnim")] string AcquisitionAnimation = "",
    // Whether this pose was baked in a scene load shared with its rig's other poses. False is the status quo and
    // also the per-target FALLBACK a rig bake takes when the shared association turns out not to hold.
    [property: JsonPropertyName("batched")] bool Batched = false,
    // Whether ANY window, at EITHER tier, hit its candidate cap and swept a subset of its own plan. A bake-level
    // roll-up of the per-window `truncated` / `narrowTruncated` flags, because a truncation is a reason not to
    // trust `meshesValidated` and a reader must not have to walk `windows[]` to discover one. `truncatedNote`
    // says which windows and what cap would have covered them; empty when nothing truncated.
    //
    // This is the field that closes the round's actual defect. Truncation was already reported per window, but
    // only ever WARNED about in a log line ("a slot mesh may be missing from the far end of the window") that no
    // consumer reads, and it is not hypothetical: a recorded live bake lost 13 meshes to it.
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("truncatedNote")] string TruncatedNote = "",
    // ── The ATLAS-FIRST SHADOW ARM (report-only; nothing here changed the association above) ──────────
    //
    // What association would have looked like if the atlas alone had done DISCOVERY, instead of standing behind
    // the colour-flip probe as a tie-breaker and a fallback. Measured on the same inputs, after the real
    // association has finished, and thrown away: `associationPairs` and every `by*` counter above are the real
    // arm's, unchanged. The three numbers are the whole question — how many ordinals the atlas resolved on its
    // own, and of those, how many agreed with the mesh the real arm chose. A later round decides whether the
    // colour probe (which costs most of a bake's blocking time) can be narrowed; it cannot decide that on a
    // hypothesis, and this is the measurement that replaces one.
    [property: JsonPropertyName("atlasFirstResolved")] int AtlasFirstResolved = 0,
    [property: JsonPropertyName("atlasFirstAgreed")] int AtlasFirstAgreed = 0,
    [property: JsonPropertyName("atlasFirstDisagreed")] int AtlasFirstDisagreed = 0,
    // The disagreeing ordinals and why, CAPPED — a rig with a systematically wrong atlas would otherwise put one
    // line per slot into every manifest. Omitted when nothing disagreed.
    [property: JsonPropertyName("atlasFirstDisagreements")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? AtlasFirstDisagreements = null,
    // ── …and whether it was ARMED, which changes what everything above means ─────────────────────────
    //
    // FALSE is the shadow arm: the association was measured by the colour probe and the atlas only shadowed it.
    // That is the pre-flip behaviour, now reached only by spelling GeoClipProbeSettings.AtlasFirstEnv's kill
    // switch — the record default below stays false so the report still constructs on a path that associates
    // nothing, which is a construction fallback and not a statement about how a bake is configured. TRUE, which
    // is what a default-configured bake writes, means the atlas did the DISCOVERY — every ordinal it resolved
    // uniquely was claimed before a single probe frame was awaited, and only the residue reached the probe — so
    // `byAtlasRegion` carries most of what `byColorFlip` carries when this is false. The PAIRS are meant to be
    // identical either way; a reader comparing two manifests of the same rig must read this flag before
    // concluding a method-mix difference means an association difference.
    [property: JsonPropertyName("assocAtlasFirstArmed")] bool AssocAtlasFirstArmed = false,
    // How many probe groups the armed arm had to re-run on the WHOLE swept read set. Zero is the fast path; each
    // one is a group that paid its probe frames twice, and a nonzero count is the signal that the atlas's claims
    // and the colour probe's measurement disagree about this rig somewhere. Always zero when unarmed.
    [property: JsonPropertyName("atlasFirstWidened")] int AtlasFirstWidened = 0,
    // Where the bake's wall clock went, split into the time it HELD the game's main thread and the time it merely
    // parked on engine frames. Absent when the profiler is off (SPIRECTL_RENDER_PHASE_PROFILE=0), which a reader
    // must treat as "not measured" rather than "measured zero".
    [property: JsonPropertyName("profile")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    GeoClipBakeProfile? Profile = null,
    // ── The SCENE-TREE PAUSE (see GeoClipPauseBudget; default OFF) ───────────────────────────────────
    //
    // Whether this pose was baked while the game's scene tree was PAUSED. It changes what `profile.parkedMs`
    // means — a paused tree does not run the game between the bake's awaited frames, so those milliseconds are
    // not the game's time any more — and it is the flag a reader has to check before comparing one bake's parked
    // share against another's.
    [property: JsonPropertyName("bakePaused")] bool BakePaused = false,
    // WHY the pause did or did not happen, in the lease's own words: `off (<env>)`, `disarmed (<reason>)`,
    // `already paused by the game`, `unreadable (<Exception>)`, `unwritable (<Exception>)`,
    // `rig-exempt-failed (<Exception>)`, `running -> paused`, or one of the baker's two `released (…)` notes.
    // Env var names and exception TYPE names only — no scene path, node name or animation name, because this
    // string ships inside the artifact.
    [property: JsonPropertyName("pauseNote")] string PauseNote = "",
    // How many times a paused bake's liveness assertion REFUTED the pause in this process (the rig did not
    // re-pose under it). PROCESS-WIDE and monotonic, not per-pose: a refuted pass writes no manifest at all, so a
    // per-pose counter could never carry a non-zero value. A reader seeing `pauseAssertFailed > 0` alongside
    // `bakePaused: false` is looking at an artifact re-baked after the lever disarmed itself.
    [property: JsonPropertyName("pauseAssertFailed")] int PauseAssertFailed = 0,
    // ── OWNERSHIP PROOF, scoped to THIS pose's claims (see Sts2SpineGeoClipOwnership) ────────────────
    //
    // The counters the admission rule's third arm actually grades. `claimsProven + claimsUnproven` equals
    // `associated` on any bake this runtime produced; both zero would mean a path that recorded no provenance,
    // and the rule reads that as missing evidence rather than as a clean bake. `claimProof` names the
    // provenances behind them — `atlas:uv-region-exact=37 color-flip=7` — which is the breakdown that says
    // WHICH evidence a rig's bake is resting on, and the one no earlier manifest could answer.
    [property: JsonPropertyName("claimsProven")] int ClaimsProven = 0,
    [property: JsonPropertyName("claimsUnproven")] int ClaimsUnproven = 0,
    [property: JsonPropertyName("claimProof")] string ClaimProof = "",
    // Claims the atlas narrowed to a TIE and the slot's own colour at the sampled pose then picked from (see
    // Sts2SpineGeoClipSlotColorIdentity). Belongs with the `by*` block above and is only down here because those
    // are positional; it is carried separately from `byAtlasRegion` because it answers a question the plain
    // atlas match cannot, and a reader comparing two bakes needs to see which of the two resolved a rig. The
    // five `by*` counters plus this one sum to `associated`.
    [property: JsonPropertyName("byAtlasRegionSlotColor")] int ByAtlasRegionSlotColor = 0,
    // How many slots showed a drawable attachment but were read FULLY TRANSPARENT at the one sampled pose, and
    // were therefore not expected to produce geometry (see Sts2SpineGeoClipPoseTransparency). Always 0 on a
    // multi-frame bake and whenever that arm's kill switch is down. It exists so `slotsEverVisible` above stays
    // readable: a reader recovers the raw drawable-attachment count as `slotsEverVisible + this`, and can tell a
    // rig that showed 19 slots from one that showed 21 of which 2 drew nothing.
    [property: JsonPropertyName("slotsTransparentAtPose")] int SlotsTransparentAtPose = 0);

// ── The bake's phase profile (additive; see GeoClipBakeReport.Profile) ───────────────────────────────

/// <summary>One phase of a bake. <c>Blocking</c> is a property of the CALL SITE — whether that segment held the
/// Godot main thread — not of how long it took.</summary>
internal sealed record GeoClipPhaseCost(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("ms")] double Ms,
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("blocking")] bool Blocking);

/// <summary>
/// Where one bake's wall clock went. The manifest-facing projection of
/// <see cref="Sts2RenderPhaseProfile.Snapshot"/>, so the artifact carries the breakdown without a reader having
/// to scrape a log.
///
/// <para>THE SPLIT IS THE POINT. A bake is marshalled onto the game's main thread, so its blocking segments —
/// the RID sweep's rendering-server round trips, the association's colour readbacks, the geometry readback, the
/// page encode — are a freeze the person playing sees, while its <c>await ProcessFrame</c> segments hand the
/// frame back and cost only elapsed time. One wall-clock number cannot tell those apart, and until this existed
/// the bake's blocking share had never been measured at all.</para>
///
/// <para><see cref="UnattributedMs"/> is carried rather than hidden: <see cref="TotalMs"/> is measured
/// end-to-end, so the gap between it and blocking+parked is real bake time no phase claimed. Defining the total
/// as the sum would make the instrument self-confirming.</para>
/// </summary>
internal sealed record GeoClipBakeProfile(
    [property: JsonPropertyName("totalMs")] double TotalMs,
    [property: JsonPropertyName("blockingMs")] double BlockingMs,
    [property: JsonPropertyName("parkedMs")] double ParkedMs,
    [property: JsonPropertyName("unattributedMs")] double UnattributedMs,
    [property: JsonPropertyName("framesWaited")] long FramesWaited,
    [property: JsonPropertyName("forceDraws")] long ForceDraws,
    [property: JsonPropertyName("phases")] IReadOnlyList<GeoClipPhaseCost> Phases,
    // WHICH SCHEDULE this profile was measured under, carried beside the draw count rather than left to be
    // inferred. `forceDraws` alone cannot distinguish a bake whose draw budget elided six from one whose rig
    // simply needed six fewer frames, and the two have very different blocking numbers — so a cross-round
    // comparison of `blockingMs` is only readable with this beside it. See GeoClipDrawBudget.
    //
    // LAST and DEFAULTED, out of report order, on purpose: every existing construction site — the projection
    // below and the profile/pause/core tests that build a profile by hand to grade a share or a verdict — is
    // about a fold that has no opinion about draws, and making them all restate a zero would be churn that
    // reads as if the field mattered to them. Zero is also the honest pre-lever value.
    [property: JsonPropertyName("drawsElided")] long DrawsElided = 0,
    // THE TWO DENOMINATORS. Their phases (`bakeColorRead`, `bakeSweepProbe`) already carry a total ms and a
    // `calls` count, but `calls` counts the ARM — one sweep, one probe pass — not the work inside it, so a
    // phase alone cannot answer "what did one probe cost?". The sweep is now the largest single term in the
    // rig that still loses the blocking gate, and a cost-per-probe number is what tells you whether to attack
    // the candidate count or the per-candidate cost. Same kind as the counters above: scalars about what the
    // baker did, carrying nothing about the rig.
    [property: JsonPropertyName("colorReads")] long ColorReads = 0,
    [property: JsonPropertyName("sweepProbes")] long SweepProbes = 0)
{
    /// <summary>Blocking share of the measured total. ZERO when nothing was measured — never NaN: a division
    /// that produced NaN would serialize as `null` and read downstream as "the field is missing".</summary>
    [JsonPropertyName("blockingShare")]
    public double BlockingShare => TotalMs > 0 ? BlockingMs / TotalMs : 0d;

    /// <summary>Parked share of the measured total, on the same zero-guard.</summary>
    [JsonPropertyName("parkedShare")]
    public double ParkedShare => TotalMs > 0 ? ParkedMs / TotalMs : 0d;

    /// <summary>
    /// Project a profiler snapshot. Null in, null out — an absent snapshot means the profiler was off or the
    /// stamps landed nowhere, and inventing a zeroed profile would let a reader mistake "not measured" for a
    /// bake that cost nothing.
    /// </summary>
    internal static GeoClipBakeProfile? From(Sts2RenderPhaseProfile.Snapshot? snapshot)
        => snapshot is null
            ? null
            : new GeoClipBakeProfile(
                Math.Round(snapshot.TotalMs, 3),
                Math.Round(snapshot.BlockingMs, 3),
                Math.Round(snapshot.ParkedMs, 3),
                Math.Round(snapshot.UnattributedMs, 3),
                snapshot.Counter(Sts2RenderPhaseProfile.Counter.FramesWaited),
                snapshot.Counter(Sts2RenderPhaseProfile.Counter.BakeForceDraws),
                [.. snapshot.Phases.Select(phase => new GeoClipPhaseCost(
                    phase.Phase, Math.Round(phase.Ms, 3), phase.Calls, phase.Blocking))],
                snapshot.Counter(Sts2RenderPhaseProfile.Counter.BakeDrawsElided),
                snapshot.Counter(Sts2RenderPhaseProfile.Counter.BakeColorReads),
                snapshot.Counter(Sts2RenderPhaseProfile.Counter.BakeSweepProbes));
}

/// <summary>
/// WHICH SIDE of the blocking/parked split each bake phase belongs on, as a table rather than as a convention
/// spread over a dozen call sites.
///
/// <para>The instrument's one fatal failure mode is a mislabelled call site: stamp an awaited frame as blocking
/// and the bake reports a freeze it never caused; stamp a rendering-server round trip as parked and the freeze
/// it DID cause disappears. Neither shows up as an error — both produce a plausible-looking table — so the
/// labelling is restated here and every produced profile is checked against it, instead of being trusted.</para>
/// </summary>
internal static class GeoClipPhaseDoctrine
{
    /// <summary>Phases that hand the Godot main thread back to the game: elapsed time, not a stall.</summary>
    internal static readonly IReadOnlyList<string> Parked =
    [
        Sts2RenderPhaseProfile.Phase.BakeWarmupWait,
        Sts2RenderPhaseProfile.Phase.BakePoseWait,
        Sts2RenderPhaseProfile.Phase.BakeAcquireWait,
        Sts2RenderPhaseProfile.Phase.BakeSweepWait,
        Sts2RenderPhaseProfile.Phase.BakeProbeSeekWait,
        Sts2RenderPhaseProfile.Phase.BakeProbeFrameWait,
        Sts2RenderPhaseProfile.Phase.BakeProbeRestoreWait,
    ];

    /// <summary>Phases that HOLD the main thread: the part of a bake the player would feel.</summary>
    internal static readonly IReadOnlyList<string> Blocking =
    [
        Sts2RenderPhaseProfile.Phase.BakeForceDraw,
        Sts2RenderPhaseProfile.Phase.BakeAtlasRead,
        Sts2RenderPhaseProfile.Phase.BakeSweepProbe,
        Sts2RenderPhaseProfile.Phase.BakeColorRead,
        Sts2RenderPhaseProfile.Phase.BakeGeometryRead,
        Sts2RenderPhaseProfile.Phase.BakePageCollect,
        Sts2RenderPhaseProfile.Phase.BakePageWrite,
        Sts2RenderPhaseProfile.Phase.BakeManifest,
    ];

    /// <summary>True/false for a bake phase; null for anything else — a shared phase (`sceneLoad`) or a name
    /// this table has never heard of, neither of which this doctrine is entitled to an opinion about.</summary>
    internal static bool? ExpectedBlocking(string phase)
        => Blocking.Contains(phase, StringComparer.Ordinal)
            ? true
            : Parked.Contains(phase, StringComparer.Ordinal)
                ? false
                : null;

    /// <summary>
    /// Bake phases in <paramref name="profile"/> whose recorded side disagrees with the table — i.e. call sites
    /// that stamped the wrong half of the split. Empty is the only acceptable answer; a non-empty one means the
    /// blocking share in the same report is wrong by whatever those phases cost.
    /// </summary>
    internal static IReadOnlyList<string> Violations(GeoClipBakeProfile profile)
        => [.. profile.Phases
            .Where(phase => ExpectedBlocking(phase.Phase) is { } expected && expected != phase.Blocking)
            .Select(phase => $"{phase.Phase}={(phase.Blocking ? "blocking" : "parked")}")];
}

/// <summary>
/// Whether it is worth PAUSING the game tree for the duration of a bake, decided from a measured profile alone.
///
/// <para>Pausing removes only the bake's PARKED time: those milliseconds are frames the game process spends
/// running itself while the bake waits, and a paused tree simply does not spend them. It removes none of the
/// blocking time — the sweep's round trips and the readbacks cost exactly the same on a paused tree — so the
/// parked share is the PAYOFF condition, and a bake that is nearly all blocking has nothing to win.</para>
///
/// <para>The blocking share is the separate, HARMLESS condition: a bake that already holds the main thread for
/// most of its life has frozen the game anyway, so pausing the tree on top of that takes nothing away that the
/// player still had. A short bake is harmless for the other reason — the freeze is under the threshold where
/// anyone notices — which is why either clause satisfies it.</para>
///
/// <para>BOTH are required. Worthwhile-but-not-harmless is a long bake that spends most of its time letting the
/// game run: pausing it would win real time and would also be the first thing the player notices, so it is
/// exactly the case a "would this help?" number must not be allowed to authorize on its own. Harmless-but-not-
/// worthwhile is a freeze bought for nothing.</para>
///
/// <para>A CONSEQUENCE worth knowing before the numbers arrive: the two share thresholds sum to 1.05, so a bake
/// whose shares are internally consistent cannot satisfy both on shares alone. The gate therefore opens only
/// through <see cref="SafeTotalMs"/> — a bake short enough that its freeze is beneath notice. That is the honest
/// reading of "worth pausing for AND nobody feels it", not an oversight, and it is asserted in the tests so a
/// later reader meets it as a rule rather than as a puzzling empty result.</para>
///
/// <para>NOTHING CONSUMES THIS YET. It is the gate a later workstream's arm has to pass, stated and tested here
/// so the arm is graded against a rule that existed before its numbers did.</para>
/// </summary>
internal static class GeoClipPauseVerdict
{
    /// <summary>Below this parked share there is not enough game-process time to reclaim to bother.</summary>
    internal const double MinParkedShareToArm = 0.45;

    /// <summary>At or above this blocking share the game is already frozen, so pausing costs the player nothing.</summary>
    internal const double SafeBlockingShare = 0.60;

    /// <summary>…or the whole bake is short enough that the freeze is below the noticing threshold.</summary>
    internal const double SafeTotalMs = 120d;

    internal static bool ArmingWorthwhile(GeoClipBakeProfile profile)
        => profile.ParkedShare >= MinParkedShareToArm;

    internal static bool ArmingHarmless(GeoClipBakeProfile profile)
        => profile.BlockingShare >= SafeBlockingShare || profile.TotalMs <= SafeTotalMs;

    internal static bool ArmingAllowed(GeoClipBakeProfile profile)
        => ArmingWorthwhile(profile) && ArmingHarmless(profile);
}

// ── Geometry / atlas math ────────────────────────────────────────────────────────────────────────────

internal static class Sts2SpineGeoClipMath
{
    internal const string Schema = "geoclip/0";

    /// <summary>
    /// The bake report's <c>sampleTimeSource</c> when the bake was NOT pose-only: the frame clock spanned the
    /// whole animation, so no single pose was selected and none of
    /// <c>Sts2SpineStillFrame.SampleSource*</c> applies. A fifth token rather than an empty string, so the field
    /// is always a legible answer to "which rule chose this time".
    /// </summary>
    internal const string SampleSourceWholeClip = "clip";

    /// <summary>Above this the frame clock is truncated rather than baking an unbounded artifact.</summary>
    internal const int DefaultMaxFrames = 900;

    internal const int DefaultSrcRectPadding = 1;

    internal const double DefaultContainmentTolerancePx = 1.5;

    /// <summary>
    /// The sample clock. Frames land on exact <c>i / fps</c> so a player can drive them off a fixed frame
    /// clock; the animation's END is inclusive, so a final short step is appended when the duration is not an
    /// exact multiple of the frame interval. <c>t</c> in the manifest is authoritative; <c>fps</c> is the
    /// nominal rate.
    /// </summary>
    internal static double[] FrameTimes(double durationSeconds, int fps, int maxFrames = DefaultMaxFrames)
    {
        var rate = Math.Max(1, fps);
        var cap = Math.Max(1, maxFrames);
        if (!(durationSeconds > 0) || double.IsNaN(durationSeconds))
        {
            return [0d];
        }

        var wholeSteps = (int)Math.Floor((durationSeconds * rate) + 1e-9);
        var times = new List<double>(Math.Min(cap, wholeSteps + 2));
        for (var i = 0; i <= wholeSteps && times.Count < cap; i += 1)
        {
            times.Add(Math.Min(durationSeconds, (double)i / rate));
        }

        if (times.Count == 0)
        {
            times.Add(0d);
        }

        if (times.Count < cap && durationSeconds - times[^1] > 1e-6)
        {
            times.Add(durationSeconds);
        }

        return [.. times];
    }

    internal static bool FrameTimesWereTruncated(double durationSeconds, int fps, int maxFrames = DefaultMaxFrames)
        => durationSeconds > 0 && (int)Math.Floor((durationSeconds * Math.Max(1, fps)) + 1e-9) + 1 > Math.Max(1, maxFrames);

    internal readonly record struct SrcRect(int X, int Y, int Width, int Height)
    {
        internal int[] ToArray() => [X, Y, Width, Height];

        internal bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// The page-pixel box a part is cropped from. Derived from the part's OWN uv bbox rather than the atlas
    /// region's declared bounds, because an atlas region is whitespace-stripped and may be rotated, and the
    /// mesh's uvs are the only statement of where its triangles actually sample. Rounded OUTWARD, padded, and
    /// clamped to the page, so the crop can only ever be a superset of what the part needs.
    /// </summary>
    internal static SrcRect DeriveSrcRect(
        IReadOnlyList<double> u,
        IReadOnlyList<double> v,
        int pageWidth,
        int pageHeight,
        int padPixels = DefaultSrcRectPadding)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || u.Count == 0 || v.Count == 0)
        {
            return new SrcRect(0, 0, 0, 0);
        }

        var pad = Math.Max(0, padPixels);
        double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
        var count = Math.Min(u.Count, v.Count);
        for (var i = 0; i < count; i += 1)
        {
            // A uv outside [0,1] is either a texture-wrap authoring choice or a readback artefact; either way
            // the crop must stay on the page, so clamp before mapping to pixels.
            var cu = Math.Clamp(u[i], 0d, 1d);
            var cv = Math.Clamp(v[i], 0d, 1d);
            uMin = Math.Min(uMin, cu);
            uMax = Math.Max(uMax, cu);
            vMin = Math.Min(vMin, cv);
            vMax = Math.Max(vMax, cv);
        }

        var x0 = (int)Math.Floor((uMin * pageWidth) - pad);
        var y0 = (int)Math.Floor((vMin * pageHeight) - pad);
        var x1 = (int)Math.Ceiling((uMax * pageWidth) + pad);
        var y1 = (int)Math.Ceiling((vMax * pageHeight) + pad);

        x0 = Math.Clamp(x0, 0, pageWidth);
        y0 = Math.Clamp(y0, 0, pageHeight);
        x1 = Math.Clamp(x1, 0, pageWidth);
        y1 = Math.Clamp(y1, 0, pageHeight);

        var width = Math.Max(1, x1 - x0);
        var height = Math.Max(1, y1 - y0);
        if (x0 + width > pageWidth)
        {
            x0 = Math.Max(0, pageWidth - width);
        }

        if (y0 + height > pageHeight)
        {
            y0 = Math.Max(0, pageHeight - height);
        }

        return new SrcRect(x0, y0, width, height);
    }

    /// <summary>Re-express page-normalized uvs against the part's own src rect, so a client can crop once.</summary>
    internal static (double[] U, double[] V) RenormalizeUvs(
        IReadOnlyList<double> u,
        IReadOnlyList<double> v,
        SrcRect rect,
        int pageWidth,
        int pageHeight)
    {
        var count = Math.Min(u.Count, v.Count);
        var outU = new double[count];
        var outV = new double[count];
        if (rect.IsEmpty || pageWidth <= 0 || pageHeight <= 0)
        {
            return (outU, outV);
        }

        for (var i = 0; i < count; i += 1)
        {
            outU[i] = ((u[i] * pageWidth) - rect.X) / rect.Width;
            outV[i] = ((v[i] * pageHeight) - rect.Y) / rect.Height;
        }

        return (outU, outV);
    }

    /// <summary>
    /// The frame's transform for a RIGID part: the least-squares affine that maps the part's reference
    /// vertices onto this frame's, laid out as the manifest's <c>[a,b,c,d,tx,ty]</c> — i.e.
    /// <c>x' = a*x + b*y + tx</c>, <c>y' = c*x + d*y + ty</c>.
    /// </summary>
    internal static double[] FitXform(
        IReadOnlyList<double> refX,
        IReadOnlyList<double> refY,
        IReadOnlyList<double> frameX,
        IReadOnlyList<double> frameY)
    {
        var fit = Sts2SpineGeometryMath.FitAffine2D(refX, refY, frameX, frameY);
        return [fit.A, fit.B, fit.D, fit.E, fit.C, fit.F];
    }

    internal sealed record RigidityTrack(
        bool IsRigid,
        double MaxNormalizedResidual,
        int ComparedFrames,
        bool VertexCountMismatch);

    /// <summary>
    /// Rigid-or-deforming for one PART (not one slot): the classification has to be per part, because a slot
    /// that swaps attachments swaps vertex counts too and there is no affine map across that boundary. Every
    /// frame in which the part is on screen is fitted against the part's reference pose; the WORST fit decides.
    /// </summary>
    internal static RigidityTrack ClassifyPart(
        IReadOnlyList<double> refX,
        IReadOnlyList<double> refY,
        IReadOnlyList<(IReadOnlyList<double> X, IReadOnlyList<double> Y)> frames,
        double threshold = Sts2SpineGeometryMath.DefaultRigidThreshold)
    {
        var diagonal = Sts2SpineGeometryMath.BoundingBoxDiagonal(refX, refY);
        double worst = 0;
        var compared = 0;
        var mismatch = false;

        foreach (var (x, y) in frames)
        {
            if (x.Count != refX.Count || y.Count != refY.Count)
            {
                mismatch = true;
                continue;
            }

            compared += 1;
            var fit = Sts2SpineGeometryMath.FitAffine2D(refX, refY, x, y);
            var verdict = Sts2SpineGeometryMath.ClassifyRigidity(fit.MaxResidual, diagonal, threshold);
            worst = Math.Max(worst, double.IsInfinity(verdict.NormalizedResidual) ? 1e9 : verdict.NormalizedResidual);
        }

        return new RigidityTrack(compared > 0 && !mismatch && worst < threshold, worst, compared, mismatch);
    }

    // ── Is this bracketed RID one of the skeleton's slot meshes? ─────────────────────────────────────

    /// <summary>UVs are compared against the unit square with this slack, to absorb float readback noise.</summary>
    internal const double DefaultUvTolerance = 1e-4;

    /// <summary>
    /// A mesh may sit this far outside the skeleton's own bounds (as a fraction of the bounds' longer side) and
    /// still be believed. The bounds are computed FROM the skeleton's meshes, so a real slot mesh is inside by
    /// construction; the slack only covers the fact that the bounds were read at a slightly different instant.
    /// </summary>
    internal const double DefaultSkeletonBoundsTolerance = 0.02;

    /// <summary>
    /// The skeleton's own bounding box in SKELETON-LOCAL units, as MIN/MAX corners. Two spellings of the same box
    /// are in play across this codebase — the engine hands back position+size, the probe's JSON records min/max —
    /// and a filter that reads one as the other rejects every mesh on the rig, so the conversion is named rather
    /// than inlined at each call site.
    /// </summary>
    internal readonly record struct SkeletonBox(double MinX, double MinY, double MaxX, double MaxY)
    {
        internal static SkeletonBox FromPositionSize(double x, double y, double width, double height)
            => new(x, y, x + width, y + height);

        /// <summary>From the baker's <c>[x, y, width, height]</c> bounds array; unusable when it is short.</summary>
        internal static SkeletonBox FromBoundsArray(IReadOnlyList<double>? bounds)
            => bounds is { Count: >= 4 }
                ? FromPositionSize(bounds[0], bounds[1], bounds[2], bounds[3])
                : default;

        /// <summary>A degenerate box states nothing, so the bbox test is SKIPPED rather than failed.</summary>
        internal bool IsUsable => MaxX > MinX && MaxY > MinY;
    }

    /// <summary>
    /// What one candidate surface looks like, flattened out of the engine so the decision below is pure. Built by
    /// <see cref="DescribeSurface"/> on the live side and by hand in tests.
    /// </summary>
    internal sealed record SlotMeshFacts(
        int SurfaceCount,
        bool VerticesAreVector2,
        int VertexCount,
        bool HasUvChannel,
        int UvCount,
        double UvMin,
        double UvMax,
        int IndexCount,
        double MinX,
        double MinY,
        double MaxX,
        double MaxY);

    // The reason keys. They are JSON keys in the bake report's per-window `rejectedByReason`, so they are
    // constants rather than inline strings: a renamed reason silently empties a counter somebody is grading.
    internal const string RejectSurfaceCount = "surface-count";
    internal const string RejectVertexType = "vertex-type";
    internal const string RejectNoVertices = "no-vertices";
    internal const string RejectUvMissing = "uv-missing";
    internal const string RejectUvCountMismatch = "uv-count-mismatch";
    internal const string RejectUvOutOfUnitRange = "uv-out-of-unit-range";
    internal const string RejectIndicesNotTriangles = "indices-not-triangles";
    internal const string RejectBboxOutsideSkeletonBounds = "bbox-outside-skeleton-bounds";

    internal static SlotMeshFacts DescribeSurface(
        int surfaceCount,
        bool verticesAreVector2,
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        bool hasUvChannel,
        IReadOnlyList<double> u,
        IReadOnlyList<double> v,
        int indexCount)
    {
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var vertexCount = Math.Min(x.Count, y.Count);
        if (vertexCount > 0)
        {
            minX = maxX = x[0];
            minY = maxY = y[0];
            for (var i = 1; i < vertexCount; i += 1)
            {
                minX = Math.Min(minX, x[i]);
                maxX = Math.Max(maxX, x[i]);
                minY = Math.Min(minY, y[i]);
                maxY = Math.Max(maxY, y[i]);
            }
        }

        double uvMin = 0, uvMax = 0;
        var uvCount = Math.Min(u.Count, v.Count);
        if (uvCount > 0)
        {
            uvMin = Math.Min(u[0], v[0]);
            uvMax = Math.Max(u[0], v[0]);
            for (var i = 1; i < uvCount; i += 1)
            {
                uvMin = Math.Min(uvMin, Math.Min(u[i], v[i]));
                uvMax = Math.Max(uvMax, Math.Max(u[i], v[i]));
            }
        }

        return new SlotMeshFacts(
            surfaceCount,
            verticesAreVector2,
            vertexCount,
            hasUvChannel,
            uvCount,
            uvMin,
            uvMax,
            indexCount,
            minX,
            minY,
            maxX,
            maxY);
    }

    /// <summary>
    /// Whether a bracketed RID is plausibly one of THIS skeleton's slot meshes. Returns <c>null</c> to accept, or
    /// the reason key it failed on.
    ///
    /// <para>The sweep enumerates an id space, not a node list, so anything the engine happened to mint in the
    /// same window is a candidate: other subsystems' 3D geometry, a full-page quad, a UI mesh. Phase 1's filter
    /// was "one surface, some vertices, triangle indices, a uv channel of matching length", which is why its live
    /// arm picked up 230-vertex <c>PackedVector3Array</c> meshes and a 256×256 quad. STRICT adds the four checks
    /// that a Spine slot mesh satisfies by construction: 2D vertices, a uv channel that actually addresses an
    /// atlas page (in the unit square), a positive triangle count, and a bounding box inside the skeleton's own
    /// bounds.</para>
    ///
    /// <para>KNOWN BLIND SPOT, deliberately not fixed here: a 256×256 quad whose uvs span [0,1] and whose box sits
    /// inside the skeleton's bounds passes every one of these checks. Nothing about a single quad distinguishes
    /// it from a legitimate region attachment; separating those needs the run/stride evidence the live arm
    /// gathers, not a per-mesh predicate. The test suite pins this rather than pretending otherwise.</para>
    ///
    /// <para><paramref name="strict"/> false reproduces Phase-1 behaviour EXACTLY, so a bake can be compared
    /// against the recorded Phase-1 numbers by flipping one environment variable.</para>
    /// </summary>
    internal static string? IsPlausibleSlotMesh(
        SlotMeshFacts facts,
        SkeletonBox skeletonBounds,
        bool strict,
        double uvTolerance = DefaultUvTolerance,
        double boundsTolerance = DefaultSkeletonBoundsTolerance)
    {
        if (facts.SurfaceCount != 1)
        {
            return RejectSurfaceCount;
        }

        if (!strict)
        {
            // Phase 1, verbatim: vertices exist, indices divide into triangles, and a uv channel — if present at
            // all — matches the vertex count. Nothing about vertex TYPE, uv RANGE or position.
            if (facts.VertexCount <= 0)
            {
                return RejectNoVertices;
            }

            if (facts.IndexCount % 3 != 0)
            {
                return RejectIndicesNotTriangles;
            }

            return facts.UvCount != 0 && facts.UvCount != facts.VertexCount ? RejectUvCountMismatch : null;
        }

        if (!facts.VerticesAreVector2)
        {
            return RejectVertexType;
        }

        if (facts.VertexCount <= 0)
        {
            return RejectNoVertices;
        }

        if (!facts.HasUvChannel || facts.UvCount <= 0)
        {
            return RejectUvMissing;
        }

        if (facts.UvCount != facts.VertexCount)
        {
            return RejectUvCountMismatch;
        }

        if (facts.UvMin < -uvTolerance || facts.UvMax > 1d + uvTolerance)
        {
            return RejectUvOutOfUnitRange;
        }

        if (facts.IndexCount <= 0 || facts.IndexCount % 3 != 0)
        {
            return RejectIndicesNotTriangles;
        }

        if (!skeletonBounds.IsUsable)
        {
            // No bounds were readable, so this check has nothing to say. Accepting is the honest outcome: the
            // alternative silently drops every mesh on a rig whose bounds failed to read.
            return null;
        }

        var pad = Math.Abs(boundsTolerance)
            * Math.Max(skeletonBounds.MaxX - skeletonBounds.MinX, skeletonBounds.MaxY - skeletonBounds.MinY);
        var inside = facts.MinX >= skeletonBounds.MinX - pad
            && facts.MinY >= skeletonBounds.MinY - pad
            && facts.MaxX <= skeletonBounds.MaxX + pad
            && facts.MaxY <= skeletonBounds.MaxY + pad;
        return inside ? null : RejectBboxOutsideSkeletonBounds;
    }
}

// ── The two sweep windows ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Planning for the baker's mesh-RID acquisition. The established A and B windows retain their original bounds,
/// enumeration and budgets. A complementary B index strip preserves an endpoint the ordinary B plan does not
/// use, and is available only as a final recovery pass after the established passes remain short.
///
/// <para>So the fix is a SECOND window rather than a wider first one: window A stays exactly what Phase 1 swept
/// (its found count is the regression anchor — 30 on the merchant, 26 on byrdonis), and window B covers
/// (bracketMid, bracketPost], where bracketPost is a probe mesh created after the acquisition-frame seek. The two
/// are reported separately for that reason.</para>
///
/// <para>Window B floors its index axis at zero. Enumeration is validator-major and every plan reports when its
/// cap covers only a prefix; no endpoint or cap is treated as a completeness guarantee.</para>
/// </summary>
internal static class Sts2SpineGeoClipSweep
{
    /// <summary>How many validator rows window B's cap is auto-raised to cover when nothing overrides it.</summary>
    internal const int DefaultWindowBValidatorRows = 512;

    /// <summary>Ceiling on the auto-raise, so a pathological bracket cannot plan a multi-minute sweep.</summary>
    internal const int WindowBCapCeiling = 1_000_000;

    /// <summary>
    /// The same ceiling for window A's auto-raise (<see cref="ChooseWindowACap"/>). Named separately from
    /// window B's because the two raises answer different questions — B raises to cover N validator ROWS of an
    /// unbounded bracket, A raises to cover its whole bracket — and a later measurement that wants to move one
    /// must not silently move the other.
    /// </summary>
    internal const int WindowACapCeiling = 1_000_000;

    internal const string WindowAName = "A";
    internal const string WindowBName = "B";

    /// <summary>
    /// KILL SWITCH for window A's index-floor recovery strip (<see cref="PlanWindowAIndexFloorRecovery"/>).
    /// <c>0</c> restores the pre-fix acquisition exactly: window A covers its slacked hull and nothing else.
    /// Read by the baker at the plan site, so it bites on BOTH lanes — unlike <c>IndexSlack</c>, which the
    /// on-demand lane hard-codes and which is therefore un-A/B-able live (that is what forced the whole
    /// diagnosis onto the env-armed one-shot lane).
    /// </summary>
    internal const string WindowAIndexFloorRecoveryEnv = "SPIRECTL_SPINE_GEOCLIP_INDEX_FLOOR_RECOVERY";

    /// <summary>Armed unless the switch above says otherwise.</summary>
    internal const bool WindowAIndexFloorRecoveryDefault = true;

    /// <summary>
    /// The switch's resolved value. Blank/unset is <see cref="WindowAIndexFloorRecoveryDefault"/>, so "nothing
    /// said otherwise" means the same thing wherever it is read — the baker's plan site and the refusal memo's
    /// lever signature both go through here rather than each parsing the variable their own way.
    /// </summary>
    internal static bool ResolveWindowAIndexFloorRecovery(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0
            ? WindowAIndexFloorRecoveryDefault
            : value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    /// <summary>The switch as a bake starting right now would read it.</summary>
    internal static bool WindowAIndexFloorRecoveryArmed()
        => ResolveWindowAIndexFloorRecovery(
            Environment.GetEnvironmentVariable(WindowAIndexFloorRecoveryEnv));

    /// <summary>
    /// KILL SWITCH for the index-CEILING recovery strip (<see cref="PlanIndexCeilingRecovery"/>), the mirror of
    /// the floor above. <c>0</c> restores the pre-fix acquisition exactly: window A stops at its slacked hull top
    /// and window B's complementary strip reaches only its own discarded middle endpoint, which is all either
    /// window did before.
    ///
    /// <para>SEPARATE FROM THE FLOOR'S SWITCH, and separately folded into the refusal memo's lever signature.
    /// The two strips cover opposite halves of the index axis and a live A/B that moved both at once could not
    /// say which half mattered — and worse, a memo that folded them as one bit would answer a request made with
    /// the ceiling down using the refusal recorded with the floor down.</para>
    /// </summary>
    internal const string IndexCeilingRecoveryEnv = "SPIRECTL_SPINE_GEOCLIP_INDEX_CEILING_RECOVERY";

    /// <summary>Armed unless the switch above says otherwise.</summary>
    internal const bool IndexCeilingRecoveryDefault = true;

    /// <summary>
    /// THE CEILING'S PROBE BUDGET, in candidates, per window per bake. The floor strip needs no budget — it is
    /// bounded below by index 0, the bottom of the address space — but nothing bounds a ceiling, so this does.
    ///
    /// <para>WHY 32 768. It is the largest floor strip this acquisition has already been measured paying live:
    /// arm H2's flail strip planned 31 706 candidates and cost 0.05 s (round WS-N,
    /// <c>.sts2/research/data/geoclip-live-confirm-20260909T020500Z/evidence/index-bands.txt</c>). So a ceiling
    /// strip costs at most what a floor strip has already been measured costing, rather than what a fresh number
    /// guesses. At the round's measured 1.26–1.96 µs per rendering-server probe that is 41–64 ms per window and
    /// at most 83–128 ms per rig with both windows firing, against the 266 747 candidates / 0.52 s that the
    /// rejected blanket <c>IndexSlack=1024</c> widen cost on ironclad's window A alone.</para>
    ///
    /// <para>It also sits BELOW the request lane's hard-coded 50 000 candidate cap, and the budgeted REACH is
    /// trimmed to fit whichever of the two is smaller, so the reach this budget buys can never truncate. (The
    /// sentinel term the strip inherits from window B's old complement is unbounded by the budget and can still
    /// truncate under a small explicit cap, exactly as it did before.) That matters more here than anywhere else
    /// in the sweep: enumeration is validator-major, so a truncated strip would discard the TOP of the validator
    /// axis — exactly where a late-minted mesh sits — and a recovery pass that loses the meshes it exists to find
    /// is worse than not running.</para>
    /// </summary>
    internal const int IndexCeilingRecoveryBudget = 32_768;

    /// <summary>
    /// The furthest the ceiling reaches above a window's hull, in index columns, however cheap the window is.
    ///
    /// <para>WHY 1 024. That is <c>IndexSlack=1024</c>, the blanket widen this design exists to avoid paying for —
    /// and it is the only configuration MEASURED to recover every missing mesh (WS-I arm SlackB: spectral 22/22,
    /// magi 16/16; WS-N's SlackFix1024 leg baked all four rigs complete). So the ceiling reaches exactly as far
    /// as the setting that is known to be sufficient, wherever the validator span makes that affordable, and
    /// never further: the same leg swept indices 0..1206 on window A and 0..1129 on window B and found nothing at
    /// all above the ordinary tops, so reach beyond 1 024 columns has no evidence behind it and would only widen
    /// the foreign-geometry surface.</para>
    ///
    /// <para>The two bounds bite in opposite places, which is the point: a WIDE validator span (window A, 86–221
    /// rows) is trimmed by the budget to 148–381 columns, and a NARROW one (window B, 1–72 rows) reaches the full
    /// 1 024 for 1 024–32 760 candidates. Every residual this round measured sits within a few tens of columns of
    /// its window's top, and every index ever recorded live on any of the four rigs — sentinel or mesh — is under
    /// 305.</para>
    /// </summary>
    internal const int IndexCeilingRecoveryMaxColumns = 1_024;

    /// <summary>The ceiling switch's resolved value; blank/unset is <see cref="IndexCeilingRecoveryDefault"/>.</summary>
    internal static bool ResolveIndexCeilingRecovery(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0
            ? IndexCeilingRecoveryDefault
            : value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    /// <summary>The ceiling switch as a bake starting right now would read it.</summary>
    internal static bool IndexCeilingRecoveryArmed()
        => ResolveIndexCeilingRecovery(Environment.GetEnvironmentVariable(IndexCeilingRecoveryEnv));

    // A Godot RID id packs a 32-bit validator in the high word and a per-owner index in the low word. Composed
    // and decoded locally rather than through the probe's copy: the probe is another work stream's file, and a
    // synthetic id is the whole mechanism by which window B floors its index axis.
    internal static ulong ComposeRidId(uint validator, uint index) => ((ulong)validator << 32) | index;

    internal static (uint Validator, uint Index) DecodeRidId(ulong id)
        => ((uint)(id >> 32), (uint)(id & 0xFFFF_FFFFuL));

    /// <summary>
    /// One planned window: the candidate plan plus how its cap was arrived at. <c>CapSource</c> is
    /// "config" / "env" / "auto-raised", and the from/to pair is reported even when nothing was raised so a
    /// reader never has to infer it.
    ///
    /// <para><c>CoreIndexLow/High</c> is an ORDERING HINT: the sub-band of the index axis the window's meshes
    /// are most likely to sit in, so the walk arm's column scan tries those columns first. It does not narrow
    /// the PLAN — the dense sweep still validates every id on the whole slacked axis, because narrowing coverage
    /// on an index guess is exactly the Phase-1 bug that lost 14 of the merchant's meshes.</para>
    ///
    /// <para>Read the hint's real strength honestly, though: the walk arm's probe budget means a rig parked far
    /// outside the hinted band is not reached before the budget runs out, so for THAT arm the hint is closer to
    /// a bound than to an ordering. What makes it safe is that coming up short arms the dense sweep, which has
    /// no such hint.</para>
    ///
    /// <para><c>CoreBandBoundsSweep</c> is the separate, much stronger claim that the band may also BOUND a
    /// dense pass's coverage — i.e. that the window's meshes are known to be inside it, not merely likely to be
    /// found there first. Only window A makes that claim, and it makes it off recorded ids. Window B does not
    /// and cannot: its whole premise is an index axis floored at zero because nothing predicts where the lazily
    /// minted meshes land, so bounding it on <c>CoreIndexLow</c> is an index guess by another name. Defaulted
    /// FALSE, so a window that has not earned the claim gets full coverage rather than silently losing meshes to
    /// it.</para>
    ///
    /// <para><c>IndexFloorRecoveryPlan</c> / <c>IndexCeilingRecoveryPlan</c> are the two halves of ONE last-resort
    /// mechanism — recover OUTWARD from the hull, downward and then upward — not two mechanisms. They share the
    /// gate (a shortfall after every ordinary pass), the tier name, the truncation roll-up and the driver loop;
    /// only the direction differs. Kept as two nullable plans rather than one list so the record still compares by
    /// value, which is how the kill switches assert that a disarmed window is the pre-fix window exactly.</para>
    /// </summary>
    internal sealed record SweepWindow(
        string Name,
        Sts2SpineGeometryMath.RidCandidatePlan Plan,
        string CapSource,
        int CapAutoRaisedFrom,
        int CapAutoRaisedTo,
        uint CoreIndexLow,
        uint CoreIndexHigh,
        bool CoreBandBoundsSweep = false,
        Sts2SpineGeometryMath.RidCandidatePlan? IndexFloorRecoveryPlan = null,
        Sts2SpineGeometryMath.RidCandidatePlan? IndexCeilingRecoveryPlan = null)
    {
        internal long ValidatorSpan => (long)Plan.ValidatorHigh - Plan.ValidatorLow + 1;

        internal long IndexSpan => (long)Plan.IndexHigh - Plan.IndexLow + 1;

        /// <summary>
        /// The recovery strips this window would sweep, in order: DOWN from the hull's floor first, then UP past
        /// its top. Floor first because it is the cheaper half on the window that carries the wide validator span
        /// (window A's floor is bounded by the hull's own low edge, 25–166 columns live; its ceiling is bounded by
        /// the budget, 148–381) and because it is where four of the six recoveries this round measured were found.
        /// The driver stops as soon as the shortfall closes, so the order is a cost decision, not a coverage one.
        /// </summary>
        internal IEnumerable<Sts2SpineGeometryMath.RidCandidatePlan> IndexRecoveryPlans
        {
            get
            {
                if (IndexFloorRecoveryPlan is { } floor)
                {
                    yield return floor;
                }

                if (IndexCeilingRecoveryPlan is { } ceiling)
                {
                    yield return ceiling;
                }
            }
        }
    }

    /// <summary>
    /// Window A: Phase 1's plan, unchanged. Any edit here is a regression against the recorded numbers
    /// (merchant validators 16204..16448 × indices 23..195 = 42 385 candidates → 30 meshes).
    ///
    /// <para>Its core index band is the bracket's OWN index range, un-slacked. Measured on both rigs and in both
    /// recorded sessions, every window-A mesh sits strictly inside it (merchant bracket 87..131, meshes 88..130;
    /// byrdonis bracket 88..117, meshes 89..116) — the slack is defensive padding, so it is the last place to
    /// look rather than the first.</para>
    /// </summary>
    /// <param name="indexFloorRecovery">
    /// Whether to attach the index-floor recovery strip below the hull — see
    /// <see cref="PlanWindowAIndexFloorRecovery"/>. The baker resolves it from
    /// <see cref="WindowAIndexFloorRecoveryEnv"/>; false is the pre-fix plan, byte for byte.
    /// </param>
    /// <param name="indexCeilingRecovery">
    /// Whether to attach the index-ceiling recovery strip above the hull — see
    /// <see cref="PlanIndexCeilingRecovery"/>. The baker resolves it from
    /// <see cref="IndexCeilingRecoveryEnv"/>. False leaves window A with no ceiling strip at all, which is what
    /// it had before: its two sentinels ARE its hull's endpoints, so the sentinel term the strip inherits from
    /// window B's complement lands exactly on the hull top and plans nothing.
    /// </param>
    internal static SweepWindow PlanWindowA(
        ulong bracketLowId,
        ulong bracketMidId,
        int indexSlack,
        int cap,
        int capCeiling = WindowACapCeiling,
        bool indexFloorRecovery = WindowAIndexFloorRecoveryDefault,
        bool indexCeilingRecovery = IndexCeilingRecoveryDefault)
    {
        // Planned twice for the same reason window B is: the first plan is arithmetic that tells us the window's
        // SHAPE, the cap is chosen against that shape, and the second plan is the one that gets swept.
        var shape = Sts2SpineGeometryMath.PlanRidCandidates(bracketLowId, bracketMidId, indexSlack, cap);
        var (chosenCap, source, from, to) = ChooseWindowACap(shape.TotalCandidates, cap, capCeiling);
        var plan = Sts2SpineGeometryMath.PlanRidCandidates(bracketLowId, bracketMidId, indexSlack, chosenCap);
        var (_, lowIndex) = DecodeRidId(bracketLowId);
        var (_, midIndex) = DecodeRidId(bracketMidId);
        return new(
            WindowAName,
            plan,
            source,
            from,
            to,
            Math.Clamp(Math.Min(lowIndex, midIndex), plan.IndexLow, plan.IndexHigh),
            Math.Clamp(Math.Max(lowIndex, midIndex), plan.IndexLow, plan.IndexHigh),
            // Window A, ALONE, may bound a dense pass on its core band. The band is the bracket's own un-slacked
            // index range and the claim is recorded on both rigs in both offline sessions AND live: across 65
            // recorded live bakes the narrow pass found every mesh the wide pass then found on window A in 56 of
            // them, and in the other 9 the wide tier fired and recovered the rest. See the tier note below.
            CoreBandBoundsSweep: true,
            IndexFloorRecoveryPlan: indexFloorRecovery ? PlanWindowAIndexFloorRecovery(plan) : null,
            // Window A's own sentinels bound its hull, so with the ceiling disarmed this is always null — the
            // sentinel term below can only reach `max(low, mid) + slack`, which IS `plan.IndexHigh`.
            IndexCeilingRecoveryPlan: PlanIndexCeilingRecovery(
                plan,
                Math.Max(lowIndex, midIndex),
                indexSlack,
                indexCeilingRecovery));
    }

    /// <summary>
    /// WINDOW A'S INDEX FLOOR. The strip below the hull — the window's own validator span across indices
    /// <c>0 .. plan.IndexLow - 1</c> — run only as a last resort, after every ordinary pass has finished and the
    /// accumulated result still cannot cover the expected slots.
    ///
    /// <para>WHY IT EXISTS. Window A's index axis is the two bracket sentinels' own index range padded by
    /// <c>IndexSlack</c>, and that hull is an INFERENCE about where the rig's meshes landed. The validator axis
    /// is not: a validator comes from one process-global monotonic counter, so every RID minted inside the
    /// bracket has one inside the span, exactly. The index comes from the per-type RID allocator, and the hull
    /// is only sound while that allocator is BUMP-allocating. Once anything frees a mesh RID — a creature death
    /// is the trigger measured — later allocations are served out of order, the rig's own meshes land at indices
    /// outside the hull, and they are NEVER PROBED. <c>meshesValidated</c> falls below <c>slotsEverVisible</c>,
    /// <c>unassociated</c> is defined as the difference, and the bake refuses with no association fault
    /// anywhere in it.</para>
    ///
    /// <para>WHAT IS MEASURED (round WS-I, `.sts2/research/data/geoclip-driven-combat-20260909T002811Z/`;
    /// ten single-variable arms, virgin process each). Idle, 180 s elapsed, a mirror client attached, eight
    /// concurrent /spines/ bakes and two full rounds of card play with NO deaths all bake 4 of 4 rigs complete.
    /// Two kills alone refuse 3 of 4. Pausing the scene tree for the whole bake — which stops all foreign RID
    /// allocation inside the bracket — changes nothing, because the damage is in the allocator before the bake
    /// starts. In every shortfall <c>associated == meshesValidated</c> exactly and <c>truncated=False</c>.</para>
    ///
    /// <para>WHY THE FLOOR AND NOT A WIDER SLACK. In the matched post-death pair (arms SlackA/SlackB: same
    /// process state, same core bands, only the index axis differing) <c>IndexSlack=1024</c> recovers every
    /// missing mesh — spectral 17/22 → 22/22, magi 10/16 → 16/16 — at 0.20–0.53 s of extra probing per rig,
    /// which would hand back the per-request blocking budget this lane just won. The strip buys the same
    /// recovery for the low side alone: spectral 64 columns × 119 validators = 7 616 candidates against
    /// slack-1024's 137 683, magi 65 × 86 = 5 590 against 101 910 — 18× cheaper, ~10–15 ms at the measured
    /// ~1.3–2.0 µs per probe. Both numbers were then measured LIVE, to the unit, in six independent processes.</para>
    ///
    /// <para>RETRACTED — the scoping claim, not the strip. This used to say the high side of the hull was empty,
    /// on four controls: "raising window B's index top from ~165–225 to ~1123–1185 across the same four rigs found
    /// not one extra mesh, and extending window A's top from 196 to 288 on spectral found none either". Those
    /// controls are real and they DO NOT GENERALISE. Round WS-N drove real fights on the request lane and every
    /// residual the strip could not close sits above a hull TOP: 3 of 4 rigs admitted, with ironclad 4 short in
    /// window B and flail 2 short above window A's own top, and the strip firing and finding ZERO on both, in
    /// every arm. Growing the hull upward is what closes them. See <see cref="PlanIndexCeilingRecovery"/>, which
    /// is this strip's mirror on the same gate — nothing about the floor changed.</para>
    ///
    /// <para>WHY A RECOVERY STRIP AND NOT A DETECTION PROBE. The alternative considered was minting extra
    /// sentinels and widening when their indices come back non-consecutive. That triggers on a whole-allocator
    /// property inferred from engine behaviour nobody has read out of the process, and it OVER-fires: in the
    /// post-death arms ironclad and flail bake 44/44 and 37/37 with the free list demonstrably active, so a
    /// detector would have widened all four rigs to fix two. This strip triggers on the MEASURED shortfall
    /// instead — the same <c>found &lt; expected</c> grade the wide tier and window B's own recovery already
    /// use — and it is the same mechanism, in the same driver loop, rather than a second one beside it.</para>
    ///
    /// <para>Null when the hull already reaches index 0 (nothing below it to sweep), which is also what window B
    /// always looks like — B floors its axis at 0 in its ORDINARY plan and so never needs this.</para>
    /// </summary>
    internal static Sts2SpineGeometryMath.RidCandidatePlan? PlanWindowAIndexFloorRecovery(
        Sts2SpineGeometryMath.RidCandidatePlan ordinaryPlan)
    {
        if (ordinaryPlan.ValidatorHigh < ordinaryPlan.ValidatorLow || ordinaryPlan.IndexLow == 0)
        {
            return null;
        }

        // Planned off two SYNTHETIC ids with no slack — window B's own trick for moving an axis endpoint — so
        // the plan arithmetic (spans, cap, truncation) stays the probe's and the only new thing is where the
        // strip starts and stops. The cap is the ordinary plan's, so a strip that is a strict subset of an
        // untruncated hull cannot truncate either.
        return Sts2SpineGeometryMath.PlanRidCandidates(
            ComposeRidId(ordinaryPlan.ValidatorLow, 0),
            ComposeRidId(ordinaryPlan.ValidatorHigh, ordinaryPlan.IndexLow - 1),
            indexSlack: 0,
            ordinaryPlan.Cap);
    }

    /// <summary>
    /// Window A's cap. RAISED (never lowered) to cover the window's whole planned cross product, ceilinged.
    ///
    /// <para>Window A's span IS the bracket the skeleton's meshes were minted inside, so unlike the probe's
    /// recipe-D canvas bracket there is no "far end where the meshes are known not to be" for a cap to discard.
    /// Enumeration is validator-major, so a cap truncates the TOP of the validator axis — exactly where a
    /// late-minted mesh sits. Recorded live: window A on the menu background planned 77 074 candidates against
    /// the 50 000 cap and returned 8 meshes, while the same window's NARROWER core-band pass — same validator
    /// span, a strict subset of the index axis, small enough not to truncate — returned 21. A superset plan
    /// found 13 fewer meshes, which is only possible if the cap cut the sweep off before it reached them.</para>
    ///
    /// <para>The ceiling is what stops a mis-detected bracket spanning a whole scene load from planning a sweep
    /// measured in minutes. It can afford to be generous now: the merchant sweep this cap was sized for cost
    /// ~1 690 ms when 50 000 was chosen and costs ~130 ms today, so the arithmetic that justified the old number
    /// no longer holds. A window that truncates ANYWAY still arms the wide tier and still says so in the bake
    /// report — the cap is a budget, not the thing keeping the acquisition honest.</para>
    /// </summary>
    internal static (int Cap, string Source, int From, int To) ChooseWindowACap(
        long totalCandidates,
        int configuredCap,
        int capCeiling = WindowACapCeiling)
    {
        var wanted = (int)Math.Min(Math.Max(1, capCeiling), Math.Max(1, totalCandidates));
        return wanted > configuredCap
            ? (wanted, "auto-raised", configuredCap, wanted)
            : (configuredCap, "config", configuredCap, configuredCap);
    }

    /// <summary>
    /// Window B: (bracketMid, bracketPost] with the index axis floored at zero. The floor is applied by planning
    /// against a SYNTHETIC low id — the mid bracket's validator with index 0 — so the plan arithmetic itself is
    /// still the probe's, and the only new thing is where the window starts.
    /// </summary>
    internal static SweepWindow PlanWindowB(
        ulong bracketMidId,
        ulong bracketPostId,
        int indexSlack,
        int configuredCap,
        int? envCap,
        int validatorRowsToCover = DefaultWindowBValidatorRows,
        int capCeiling = WindowBCapCeiling,
        bool indexCeilingRecovery = IndexCeilingRecoveryDefault)
    {
        var (midValidator, _) = DecodeRidId(bracketMidId);
        var syntheticLow = ComposeRidId(midValidator, 0);

        // Planned once with the configured cap to learn the window's SHAPE, then re-planned with whatever cap
        // that shape turns out to need. Cheap: a plan is arithmetic, not a sweep.
        var shape = Sts2SpineGeometryMath.PlanRidCandidates(syntheticLow, bracketPostId, indexSlack, configuredCap);
        var indexSpan = (long)shape.IndexHigh - shape.IndexLow + 1;
        var validatorSpan = (long)shape.ValidatorHigh - shape.ValidatorLow + 1;

        var (cap, source, from, to) = ChooseWindowBCap(
            indexSpan, validatorSpan, configuredCap, envCap, validatorRowsToCover, capCeiling);

        var plan = Sts2SpineGeometryMath.PlanRidCandidates(syntheticLow, bracketPostId, indexSlack, cap);
        var (_, midIndex) = DecodeRidId(bracketMidId);
        var (_, postIndex) = DecodeRidId(bracketPostId);
        var ceilingPlan = PlanIndexCeilingRecovery(
            plan, Math.Max(midIndex, postIndex), indexSlack, indexCeilingRecovery);
        return new SweepWindow(
            WindowBName,
            plan,
            source,
            from,
            to,
            // Where the walk arm's column scan STARTS, and nothing more. Coverage still runs down to the forced
            // index floor of 0, and `CoreBandBoundsSweep` is left at its default false so no dense pass may
            // bound itself here either.
            //
            // WHY IT IS ONLY AN ORDERING HINT: it was believed to be a containment bound — "the lazily minted
            // meshes sit AT OR ABOVE the mid bracket's own index" — and live it is false. Across 65 recorded
            // bakes the window-B core band lost meshes the full axis then found in 62 of them: on the merchant,
            // 13 of 14 window-B meshes sit BELOW the mid bracket index (band 187..253 found 1, axis 0..253 found
            // 14, in 25 arms); on byrdonis BOTH of its 2 do (band 172..237 found 0, axis 0..237 found 2, in 31
            // arms). Bounding a pass here reproduces the Phase-1 defect almost exactly — that one lost 14 of the
            // merchant's meshes to an index guess, this one lost 13.
            Math.Clamp(midIndex, plan.IndexLow, plan.IndexHigh),
            plan.IndexHigh,
            IndexCeilingRecoveryPlan: ceilingPlan);
    }

    /// <summary>
    /// THE HULL'S CEILING. The strip ABOVE a window's index axis — the window's own validator span across
    /// <c>plan.IndexHigh + 1 ..</c> a bounded endpoint — run only as a last resort, after every ordinary pass has
    /// finished and the accumulated result still cannot cover the expected slots. The mirror of
    /// <see cref="PlanWindowAIndexFloorRecovery"/>, on the same gate, in the same driver loop, under the same tier
    /// name.
    ///
    /// <para>WHY IT EXISTS, AND WHAT IT OVERTURNS. The floor strip was scoped on a control that said the high side
    /// of the hull was empty — "raising window B's index top from ~165–225 to ~1123–1185 across all four rigs
    /// found not one extra mesh". That held in the processes it was measured in and DOES NOT GENERALISE. Round
    /// WS-N (<c>.sts2/research/data/geoclip-live-confirm-20260909T020500Z/</c>) drove real fights on the request
    /// lane and every surviving residual sits ABOVE a hull top, not below a floor: ironclad's window A finds the
    /// same 30 meshes at slack 64 and at slack 1024 while its window B goes 10 → 14 as its top moves 165 → 1129,
    /// and flail's two missing meshes sit above window A's own top (arm Hkills: wide 26..166 finds 35, the floor
    /// strip 0..25 finds 0). In six independent processes the floor strip fired on those two rigs and found
    /// exactly ZERO. Raising the tops is the only thing that recovers them.</para>
    ///
    /// <para>THE CAUSE IS THE SAME ONE. A free list hands recycled slots back in last-freed-first-reused order,
    /// so a rig's meshes scatter across the whole range of slots the allocator is currently recycling — which has
    /// no relation to where the three bracket sentinels happen to land. That is measured, not assumed: in arm H
    /// the FIRST sentinel came back at index 181 and the second at 99, so the bracket's own endpoints are already
    /// out of order, and in arm H2 flail's meshes span 0..302 around sentinels at 230 and 238. Scatter is
    /// symmetric; the floor covered one side of it.</para>
    ///
    /// <para>WHY IT IS BOUNDED AND THE FLOOR IS NOT. <c>[0, hull floor)</c> needs no budget — index 0 is the
    /// bottom of the address space. Upward there is no such endpoint, so two bounds are chosen and both are
    /// calibrated on measurements rather than invented: <see cref="IndexCeilingRecoveryBudget"/> caps the
    /// candidates at the largest floor strip already measured live, and
    /// <see cref="IndexCeilingRecoveryMaxColumns"/> caps the reach at the <c>IndexSlack=1024</c> widen that is
    /// the only configuration measured to recover everything. The budget trims the REACH and never the validator
    /// span, because enumeration is validator-major and a cap would discard the top of the validator axis — where
    /// a late-minted mesh sits.</para>
    ///
    /// <para>WHAT BOUNDS THE FOREIGN-GEOMETRY EXPOSURE, which is the specific risk of growing upward. Not the
    /// index axis: the VALIDATOR span is the ownership fence and this strip does not touch it. A validator comes
    /// from one process-global monotonic counter, so an id this strip can compose is by construction an id minted
    /// inside the same MeshCreate bracket as the rig's own meshes — the population the whole sweep is defined
    /// over. Widening the index axis changes how much of that population the sweep SEES, never which population
    /// it is. Everything past that is the mesh filter's and the ownership proof's job, and neither is touched
    /// here. Live: the floor strip already widened window A by up to 166 columns × 191 validators with
    /// <c>foreignMeshes=0</c> on all 27 admitted bakes, and the SlackFix1024 leg swept 0..1206 and 0..1129 — 3–7×
    /// further than this ceiling ever reaches — and baked all four rigs complete with <c>unassociated=0</c>.</para>
    ///
    /// <para>WINDOW B'S OLD COMPLEMENT IS THE DEGENERATE CASE, not a second mechanism beside this one. Window B's
    /// ordinary plan takes its top from the POST sentinel, and when the discarded MIDDLE sentinel sat higher the
    /// old strip preserved that one fact. That is <paramref name="sentinelIndexHigh"/> here, applied to both
    /// windows and kept OUTSIDE the kill switch so disarming the ceiling restores the pre-fix window exactly. On
    /// window A the sentinel term is inert by construction: its two sentinels are its hull's own endpoints, so
    /// <c>sentinelIndexHigh + slack</c> IS <c>plan.IndexHigh</c>.</para>
    ///
    /// <para>Null when there is nothing above the axis to sweep — a plan that already ends at
    /// <c>uint.MaxValue</c>, or a disarmed ceiling whose sentinel term lands on the top it already has.</para>
    /// </summary>
    /// <param name="sentinelIndexHigh">
    /// The highest index among the sentinels this window was bracketed by. Slacked, it is the endpoint the window
    /// would have had if its ordinary plan had used the higher of its two sentinels.
    /// </param>
    /// <param name="ceilingArmed">
    /// <see cref="IndexCeilingRecoveryEnv"/>'s resolved value. False drops the budgeted reach and leaves only the
    /// sentinel term, which is the pre-fix strip.
    /// </param>
    internal static Sts2SpineGeometryMath.RidCandidatePlan? PlanIndexCeilingRecovery(
        Sts2SpineGeometryMath.RidCandidatePlan ordinaryPlan,
        uint sentinelIndexHigh,
        int indexSlack,
        bool ceilingArmed = IndexCeilingRecoveryDefault,
        int budget = IndexCeilingRecoveryBudget,
        int maxColumns = IndexCeilingRecoveryMaxColumns)
    {
        if (ordinaryPlan.ValidatorHigh < ordinaryPlan.ValidatorLow || ordinaryPlan.IndexHigh == uint.MaxValue)
        {
            return null;
        }

        var nonNegativeSlack = (uint)Math.Max(0, indexSlack);
        var desiredHigh = (ulong)sentinelIndexHigh + nonNegativeSlack;

        if (ceilingArmed)
        {
            // The reach in COLUMNS, so the cost is the window's validator span times a number this arithmetic
            // chose — not a span times whatever the index axis happens to look like. Trimmed by the window's own
            // cap as well as by the budget, so the strip is never larger than the prefix it would be allowed to
            // enumerate and therefore never truncates.
            var validatorSpan = (long)ordinaryPlan.ValidatorHigh - ordinaryPlan.ValidatorLow + 1;
            var affordable = Math.Min(Math.Max(0, budget), Math.Max(0, ordinaryPlan.Cap)) / validatorSpan;
            var reach = Math.Min(Math.Max(0, maxColumns), affordable);
            desiredHigh = Math.Max(desiredHigh, (ulong)ordinaryPlan.IndexHigh + (ulong)reach);
        }

        var clampedHigh = (uint)Math.Min(uint.MaxValue, desiredHigh);
        var recoveryLow = ordinaryPlan.IndexHigh + 1;
        if (clampedHigh < recoveryLow)
        {
            return null;
        }

        return Sts2SpineGeometryMath.PlanRidCandidates(
            ComposeRidId(ordinaryPlan.ValidatorLow, recoveryLow),
            ComposeRidId(ordinaryPlan.ValidatorHigh, clampedHigh),
            indexSlack: 0,
            ordinaryPlan.Cap);
    }

    /// <summary>
    /// Window B's cap. An explicit environment cap wins outright. Otherwise the configured cap is RAISED (never
    /// lowered) to whatever it takes to reach <paramref name="validatorRowsToCover"/> rows past the mid edge —
    /// the lazily minted meshes sit within a few hundred validators of it, and a cap chosen for window A's much
    /// narrower index axis would stop short of them. Ceilinged, because a bracket that spans a whole scene load
    /// could otherwise plan a sweep measured in minutes.
    /// </summary>
    internal static (int Cap, string Source, int From, int To) ChooseWindowBCap(
        long indexSpan,
        long validatorSpan,
        int configuredCap,
        int? envCap,
        int validatorRowsToCover = DefaultWindowBValidatorRows,
        int capCeiling = WindowBCapCeiling)
    {
        if (envCap is { } explicitCap && explicitCap > 0)
        {
            return (explicitCap, "env", configuredCap, explicitCap);
        }

        var rows = Math.Max(1, Math.Min(validatorRowsToCover, Math.Max(1, validatorSpan)));
        var needed = Math.Max(1, indexSpan) * rows;
        var wanted = (int)Math.Min(Math.Max(1, capCeiling), needed);
        return wanted > configuredCap
            ? (wanted, "auto-raised", configuredCap, wanted)
            : (configuredCap, "config", configuredCap, configuredCap);
    }

    /// <summary>
    /// The union of both windows' finds, in window order (A first, then B), deduped. The retention cap applies to
    /// the UNION rather than per window: it exists to stop a runaway sweep from making every later pass quadratic,
    /// and two windows that each came in just under it would still do exactly that.
    /// </summary>
    internal static (List<ulong> Merged, int Dropped) MergeWindows(
        IEnumerable<ulong> windowA,
        IEnumerable<ulong> windowB,
        int maxRetained)
    {
        var merged = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var rid in windowA.Concat(windowB))
        {
            if (seen.Add(rid))
            {
                merged.Add(rid);
            }
        }

        var cap = Math.Max(0, maxRetained);
        if (merged.Count <= cap)
        {
            return (merged, 0);
        }

        var dropped = merged.Count - cap;
        return (merged.Take(cap).ToList(), dropped);
    }

    // ── The two dense TIERS ──────────────────────────────────────────────────────────────────────────
    //
    // A window's INDEX axis is almost entirely padding. Window A widens the bracket's own index range by
    // `indexSlack` (64 each way) and window B floors its axis at 0, and the recorded plans show what that costs:
    // byrdonis window A is 143 validators × 158 indices = 22 594 candidates for 25 meshes, the merchant's
    // 222 × 154 = 34 188 for 13. The sweep is 70-76 % of a pose-only bake and most of it is proving empty
    // padding empty.
    //
    // In all four recorded id spaces every WINDOW-A mesh sits STRICTLY INSIDE the un-padded band — merchant
    // bracket 87..131 with meshes at 88..130, byrdonis bracket 88..117 with meshes at 89..116 — in both sessions.
    // So the sweep runs in two tiers: NARROW (that band only, across the window's whole validator span) and then,
    // only if the narrow tier came up short, WIDE (the full slacked plan, unchanged).
    //
    // WINDOW A ONLY. The narrow tier first shipped bounding BOTH windows on their core bands, and live that made
    // the whole thing pure cost: across 65 recorded bakes the wide tier fired every single time, so every rig paid
    // narrow + wide. The cause was window B, not the grade — window A's narrow pass found every mesh its wide pass
    // then found in 56 of the 65, while window B's lost 13 of the merchant's 14 and both of byrdonis's 2, in
    // effectively every arm. The grade itself was reachable all along and the wide tier hit it EXACTLY (merchant
    // 44/44, byrdonis 28/28), which is what rules out the other suspect: no slot sharing a mesh, no region
    // attachment without one, no mesh that is never minted. So the band is now a coverage bound only where a
    // window has earned it (`SweepWindow.CoreBandBoundsSweep`), and window B is swept at full width in tier one.
    //
    // The offline gate could not have caught this. Window B's mesh IDS were never dumped by the probe, so the
    // fixture that "proved" its core band was safe was CONSTRUCTED on the +5/+1 progression above the mid bracket
    // index — i.e. built out of the very assumption the band encodes, and unable to fail. Its own comment said so.
    // The window-B spaces below are now calibrated on the LIVE split instead (how many meshes fall either side of
    // the mid index), which is measured rather than assumed.
    //
    // The shortfall is graded on the UNION of RID ids across both windows AND both arms, by the same predicate
    // the walk arm is graded by (Sts2SpineGeoClipWalk.DenseSweepRequired), because that is the only level at
    // which it is observable: neither window knows how the rig's meshes are split between them. Being wrong
    // therefore costs ONE wasted narrow pass on top of the status quo, and never a missing mesh — the same
    // asymmetry the walk arm has, and the reason the core band may be used to ORDER or to NARROW a first pass but
    // never to bound the last one.
    //
    // A UNION and not a sum of counts, because the two arms sweep the SAME ids. The walk's column order starts at
    // `window.CoreIndexLow` and the narrow tier sweeps exactly that core band, so on every recorded id space the
    // walk's finds are a SUBSET of the narrow tier's. Adding the two counts made a rig look complete at twice its
    // real coverage and skipped the wide sweep — a silently incomplete artifact, which is the one failure this
    // acquisition exists to prevent.

    /// <summary>
    /// The bake-level truncation roll-up: whether any window at either tier swept a SUBSET of its own plan, and a
    /// note naming which, so a consumer never has to walk <c>windows[]</c> — or read a log — to find out that
    /// <c>meshesValidated</c> is a floor rather than a count.
    ///
    /// <para>Built from the window REPORTS rather than from the passes, so it can never disagree with what the
    /// manifest ships. Lives here rather than in the baker because the baker is live-host-only and does not
    /// compile into the test project, and a rule whose failure mode is a silently incomplete artifact has to be
    /// gradeable without a game.</para>
    /// </summary>
    internal static (bool Truncated, string Note) SummariseTruncation(IReadOnlyList<GeoClipBakeWindow> windows)
    {
        var parts = new List<string>();
        foreach (var window in windows)
        {
            if (window.NarrowTruncated)
            {
                parts.Add($"window {window.Name} narrow tier capped at {window.Cap}");
            }

            var ordinaryTruncated = window.OrdinaryTruncated
                ?? (window.IndexRecoveries.Count == 0 && window.Truncated);
            if (ordinaryTruncated)
            {
                parts.Add(
                    $"window {window.Name} wide tier capped at {window.Cap} of {window.TotalCandidates} candidates");
            }

            foreach (var recovery in window.IndexRecoveries)
            {
                if (!recovery.Plan.Truncated)
                {
                    continue;
                }

                var plan = recovery.Plan;
                parts.Add(
                    $"window {window.Name} index-recovery tier capped at {plan.Cap} of {plan.TotalCandidates} "
                    + $"candidates (validators {plan.ValidatorLow}..{plan.ValidatorHigh}, indices "
                    + $"{plan.IndexLow}..{plan.IndexHigh}, emitted {plan.EmittedCount}, probed {recovery.Probed}, "
                    + $"found {recovery.Found})");
            }
        }

        return parts.Count == 0
            ? (false, string.Empty)
            : (true,
                string.Join("; ", parts)
                + ". A capped sweep is validator-major, so it discards the TOP of the validator axis — where a "
                + "late-minted mesh sits. Treat meshesValidated as a floor, not a count.");
    }

    /// <summary>The narrow tier: the core index band only, across the window's whole validator span.</summary>
    internal const string TierNarrow = "narrow";

    /// <summary>The wide tier: the full slacked plan, exactly what the sweep ran before the tiers existed.</summary>
    internal const string TierWide = "wide";

    /// <summary>The complementary window-B index strip, run only after all ordinary passes remain short.</summary>
    internal const string TierIndexRecovery = "index-recovery";

    /// <summary>The narrow tier came up short and the wide sweep ran behind it.</summary>
    internal const string TierNarrowThenWide = "narrow-then-wide";

    /// <summary>No dense tier ran on this window at all — the walk arm carried it.</summary>
    internal const string TierNone = "none";

    /// <summary>Which dense tiers a window's answer came from, for the bake report.</summary>
    internal static string TierName(bool narrowRan, bool wideRan, bool indexRecoveryRan = false)
        => (narrowRan
            ? (wideRan ? TierNarrowThenWide : TierNarrow)
            : (wideRan ? TierWide : TierNone))
            + (indexRecoveryRan ? $"+{TierIndexRecovery}" : string.Empty);

    /// <summary>
    /// The same window with its index axis narrowed to <c>CoreIndexLow..CoreIndexHigh</c> — window A's own
    /// un-slacked bracket band, window B's [mid bracket index, top of the axis].
    ///
    /// <para>Planned off two SYNTHETIC ids with no slack, which is the trick window B already uses to floor its
    /// own axis: the plan arithmetic (spans, cap, truncation) stays the probe's and the only new thing is where
    /// the band starts and stops. A window whose core band already spans its whole axis is returned unchanged,
    /// so the narrow tier is never a differently-shaped copy of the wide one.</para>
    ///
    /// <para>A window that does NOT claim <c>CoreBandBoundsSweep</c> is returned unchanged as well, and that is
    /// the load-bearing clause rather than an optimisation: window B's core band is an ordering hint for the walk
    /// arm, and narrowing a dense pass onto it threw away 13 of the merchant's 14 window-B meshes on every live
    /// bake. The band is allowed to say "look here first". Only window A may say "and nowhere else".</para>
    /// </summary>
    internal static SweepWindow NarrowToCoreBand(SweepWindow window)
    {
        var plan = window.Plan;
        if (!window.CoreBandBoundsSweep)
        {
            return window;
        }

        if (plan.ValidatorHigh < plan.ValidatorLow || plan.IndexHigh < plan.IndexLow)
        {
            return window;
        }

        var low = Math.Clamp(Math.Min(window.CoreIndexLow, window.CoreIndexHigh), plan.IndexLow, plan.IndexHigh);
        var high = Math.Clamp(Math.Max(window.CoreIndexLow, window.CoreIndexHigh), plan.IndexLow, plan.IndexHigh);
        if (low == plan.IndexLow && high == plan.IndexHigh)
        {
            return window;
        }

        return window with
        {
            Plan = Sts2SpineGeometryMath.PlanRidCandidates(
                ComposeRidId(plan.ValidatorLow, low),
                ComposeRidId(plan.ValidatorHigh, high),
                indexSlack: 0,
                plan.Cap),
        };
    }

    /// <summary>One dense pass: one window, at one tier, over whichever plan that tier chose.</summary>
    internal sealed record TierPass(
        string Tier,
        string WindowName,
        Sts2SpineGeometryMath.RidCandidatePlan Plan,
        IReadOnlyList<ulong> Found,
        long Probed,
        double Ms);

    /// <summary>
    /// Every pass a tiered acquisition ran, in order, plus what the narrow tier was graded on.
    /// <c>FoundAfterNarrow</c> is the size of the UNION of distinct mesh RID ids held once the narrow tier
    /// finished — the walk arm's finds and every narrow pass's finds together, counted once each. Not a sum of
    /// per-arm counts: the arms overlap by construction (see the tier note above), so a sum would over-report.
    /// </summary>
    internal sealed record TieredSweep(
        IReadOnlyList<TierPass> Passes,
        bool NarrowRan,
        bool WideRan,
        int FoundAfterNarrow,
        long NarrowProbed,
        long WideCandidates,
        // Whether any NARROW pass hit its candidate cap and so did not enumerate its own plan. Reported because
        // it is a second, independent reason the wide tier fires, and a reader comparing `FoundAfterNarrow`
        // against the slot count would otherwise be unable to explain the fallback.
        bool NarrowTruncated = false);

    /// <summary>
    /// Run the dense acquisition in tiers over every window. The whole decision lives here rather than in the
    /// baker's driver loop so that "does the wide tier fire?" is gradeable without a game — it is the one rule
    /// in this acquisition whose failure mode is a silently incomplete artifact.
    /// </summary>
    /// <param name="foundBeforeTiers">
    /// The mesh RID IDS the walk arm already delivered across all windows, if it ran — the ids themselves, not a
    /// count, because the narrow tier re-sweeps the very band the walk arm starts in and the two answers have to
    /// be unioned before they can be graded. Copied into the accumulating set, so a duplicate or an unordered
    /// sequence is harmless.
    /// </param>
    /// <param name="validatorFor">
    /// The predicate for one (window, tier). A factory rather than a single delegate because the baker keeps a
    /// per-window rejection histogram, and a mesh must not be believed by one pass and refused by another.
    /// </param>
    /// <param name="yieldFrame">
    /// Handed a frame back to the game every <paramref name="chunkSize"/> probes: a rendering-server getter
    /// synchronizes with the render thread, so an unbroken sweep would visibly freeze the host.
    /// </param>
    internal static async Task<TieredSweep> AcquireTieredAsync(
        IReadOnlyList<SweepWindow> windows,
        int expectedRunLength,
        IEnumerable<ulong> foundBeforeTiers,
        Func<SweepWindow, string, Func<ulong, bool>> validatorFor,
        Func<Task>? yieldFrame = null,
        int chunkSize = 0)
    {
        var passes = new List<TierPass>();

        // THE GRADE'S ACCUMULATOR, and the reason it is a set. Every arm that can contribute a mesh unions into
        // this one collection, so an id delivered by the walk AND re-found by a narrow pass is worth one mesh
        // towards the rig's slot count, not two.
        var found = new HashSet<ulong>(foundBeforeTiers);
        var narrowRan = false;
        var narrowTruncated = false;

        // Which windows the narrow tier actually NARROWED. A window swept at its full width in tier one has
        // nothing left for tier two to discover, so re-sweeping it would be a verbatim re-run of the same plan
        // against the same predicate. Keyed by the plan the pass ran, not by the window's name, so the decision
        // is made on what was swept rather than on what we meant to sweep.
        var narrowPlans = new Dictionary<string, Sts2SpineGeometryMath.RidCandidatePlan>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            var pass = await SweepOnceAsync(
                TierNarrow, NarrowToCoreBand(window), validatorFor(window, TierNarrow), yieldFrame, chunkSize);
            passes.Add(pass);
            narrowRan = true;
            narrowTruncated |= pass.Plan.Truncated;
            narrowPlans[window.Name] = pass.Plan;
            found.UnionWith(pass.Found);
        }

        var narrowProbed = passes.Sum(pass => pass.Probed);
        var foundAfterNarrow = found.Count;
        var wideCandidates = windows.Sum(window => window.Plan.TotalCandidates);

        // TWO independent reasons to widen, and truncation is the one a count cannot express. A pass that hit its
        // cap did not enumerate its own plan, so its find count is a floor on a sweep that never finished — even
        // `found >= expected` off such a pass is a coincidence, not a proof, because the ids it never reached are
        // exactly the ones a validator-major cap discards. Widening on it can only cost a pass; not widening on it
        // ships a rig with meshes missing and a report that looks complete.
        if (!narrowTruncated
            && !Sts2SpineGeoClipWalk.DenseSweepRequired(narrowRan, found.Count, expectedRunLength))
        {
            return new TieredSweep(passes, narrowRan, false, foundAfterNarrow, narrowProbed, wideCandidates, false);
        }

        // The wide tier is the pre-tier sweep verbatim, including the candidates the narrow tier just paid for:
        // it re-sweeps every window rather than guessing which one is deficient, for the same reason the dense
        // fallback behind the walk does — the shortfall is only observable in the total.
        var wideRan = false;
        foreach (var window in windows)
        {
            // The one exception, and it is an identity rather than a guess: if the narrow pass ran this window's
            // OWN plan — same bounds, same cap, same truncation, so the same enumeration — the wide pass would
            // probe the same ids in the same order and union in a set it already holds. Record equality, so a
            // narrow pass that differed in any of those respects still gets re-swept.
            if (narrowPlans.TryGetValue(window.Name, out var narrowPlan) && narrowPlan == window.Plan)
            {
                continue;
            }

            wideRan = true;
            var pass = await SweepOnceAsync(TierWide, window, validatorFor(window, TierWide), yieldFrame, chunkSize);
            passes.Add(pass);
            found.UnionWith(pass.Found);
        }

        // Preserve the ordinary plans and their ordering. These complementary strips are only a last resort when
        // all of them have completed and the accumulated distinct result still cannot cover the expected slots.
        //
        // RE-GRADED BETWEEN STRIPS, unlike the ordinary tiers. A wide tier re-sweeps every window because the
        // shortfall is only observable in the total and it cannot know which window is deficient; a recovery
        // strip is answering a shortfall that a previous strip may already have closed, and the strips are the
        // expensive part of a short bake. Live, this is what keeps a rig the FLOOR rescues from also paying for
        // two ceilings: spectral's and magi's floor strips close their shortfall at 7 616 and 5 590 candidates
        // and nothing runs behind them, which is the cost the round measured and this must not regress.
        if (expectedRunLength > 0 && found.Count < expectedRunLength)
        {
            foreach (var window in windows)
            {
                foreach (var recoveryPlan in window.IndexRecoveryPlans)
                {
                    if (found.Count >= expectedRunLength)
                    {
                        break;
                    }

                    var recoveryWindow = window with
                    {
                        Plan = recoveryPlan,
                        IndexFloorRecoveryPlan = null,
                        IndexCeilingRecoveryPlan = null,
                    };
                    var pass = await SweepOnceAsync(
                        TierIndexRecovery,
                        recoveryWindow,
                        validatorFor(window, TierIndexRecovery),
                        yieldFrame,
                        chunkSize);
                    passes.Add(pass);
                    found.UnionWith(pass.Found);
                }
            }
        }

        return new TieredSweep(
            passes, narrowRan, wideRan, foundAfterNarrow, narrowProbed, wideCandidates, narrowTruncated);
    }

    private static async Task<TierPass> SweepOnceAsync(
        string tier,
        SweepWindow window,
        Func<ulong, bool> validate,
        Func<Task>? yieldFrame,
        int chunkSize)
    {
        var found = new List<ulong>();
        var stopwatch = Stopwatch.StartNew();
        long probed = 0;
        try
        {
            foreach (var candidate in Sts2SpineGeometryMath.EnumerateRidCandidates(window.Plan))
            {
                probed += 1;
                if (validate(candidate))
                {
                    found.Add(candidate);
                }

                if (yieldFrame is not null && chunkSize > 0 && probed % chunkSize == 0)
                {
                    await yieldFrame();
                }
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        return new TierPass(tier, window.Name, window.Plan, found, probed, stopwatch.Elapsed.TotalMilliseconds);
    }
}

// ── The walk arm: acquire a window's meshes without sweeping it ──────────────────────────────────────

/// <summary>
/// The bake's CHEAP acquisition arm. The dense two-window sweep above validates every id in the window — 46 145
/// candidates on the merchant, ~1 650 ms at ~33 µs per rendering-server round trip, about half a pose-only
/// bake. This arm finds the same meshes by exploiting the structure the ids actually have, and the dense sweep
/// stays armed behind it (see <c>SweepMeshRidsAsync</c>) for whenever it does not.
///
/// <para>WHAT THE RECORDED IDS SAY. Both rigs' window-A meshes were dumped by the geometry probe in two
/// sessions, and the two axes behave completely differently:</para>
/// <list type="bullet">
///   <item>The INDEX axis is exact. It advances by +1 per mesh in all four datasets, over a contiguous band
///   (merchant 88..130, byrdonis 89..116) that sits strictly inside the bracket's own index range.</item>
///   <item>The VALIDATOR axis is NOT dependable. In the later session it advances by a clean +5 per mesh; in the
///   earlier one the same rigs' deltas run 12, 15, 24, 8, 8, 7, 6, 8, 5, 9, 7, 5 … because something else was
///   minting RIDs during the skeleton build. The stride is a property of the SESSION, not of the rig.</item>
/// </list>
///
/// <para>So the arm is: <b>column scan → stride ladder → two walks</b>.</para>
/// <list type="number">
///   <item><b>Anchor by COLUMN.</b> Sweep every validator at ONE index, starting from the core band's low edge.
///   A column costs the window's validator span (223 candidates on the merchant's window A, 72 on its window B)
///   and the measured index bands are dense enough that the second column tried is a hit on every recorded rig.
///   This replaces the live probe's step-24 LATTICE, which is the right anchor for a window with no index
///   structure and the wrong one here: the lattice needs the validator stride it is trying to find, and it costs
///   several times a column.</item>
///   <item><b>Measure the stride</b> off the anchor with the probe's ladder — never assume it. The assumed +5 is
///   only ever used to seed the ladder's fallback.</item>
///   <item><b>Walk twice from the same anchor and union the results.</b> The LINE sweep steps the measured
///   stride to both window edges with no miss budget, because the merchant's run has a THIRTEEN-point hole in it
///   (indices 116..128 mint meshes that never validate) and a walk that stops on consecutive misses loses the
///   two meshes beyond it. The INDEX walk steps index ±1 and searches a bounded validator reach for each step,
///   which is what recovers a run whose validator deltas are irregular. Neither alone covers both recorded
///   sessions; together they do.</item>
///   <item><b>Stop at the band edge.</b> Once a run is accepted, columns inside its index extent are skipped (the
///   line sweep already visited them) and the scan stops one column past each end. That is what turns window A
///   from 31 889 candidates into roughly 700.</item>
/// </list>
///
/// <para>CORRECTNESS IS NOT THIS CLASS'S JOB. Every acceptance rule here is a heuristic about where meshes are,
/// and each one can be wrong on a rig nobody has measured. What makes that safe is the caller's rule: if the arm
/// delivers fewer meshes than there are slots showing an attachment, the dense sweep runs anyway and its answer
/// is unioned in. Phase 1 shipped a silently incomplete acquisition (29 of 44 slots) and it cost two rounds to
/// find; an arm that is merely SLOW when it is wrong is a different class of bug from one that is quiet.</para>
/// </summary>
internal static class Sts2SpineGeoClipWalk
{
    /// <summary>The walk carried the window alone.</summary>
    internal const string ArmWalk = "walk";

    /// <summary>The walk never ran on this window (forced off, refused, or the window was small enough).</summary>
    internal const string ArmDense = "dense";

    /// <summary>The walk ran and the dense sweep was armed behind it because the total came up short.</summary>
    internal const string ArmWalkThenDense = "walk-then-dense";

    internal const string StrideAssumed = "assumed";
    internal const string StrideMeasured = "measured";

    /// <summary>
    /// What <see cref="GeoClipConfig.DenseSweepOnly"/> is when nothing says otherwise: TRUE, i.e. the walk arm
    /// is off and every window is swept densely. Measuring the walk live turned it from a win into a loss (see
    /// the baker's <c>SPIRECTL_SPINE_GEOCLIP_DENSE_SWEEP</c> note for the six-bake measurement).
    ///
    /// <para>It lives HERE, in the Godot-free core, because the value has to be the same on both lanes and one
    /// of them is not offline-compilable: the env lane's read sits in the live-host-only baker, and the
    /// on-demand <c>/geoclips/</c> lane's plan sits in this file. They each carried their own literal and had
    /// drifted — the request lane still armed the walk long after it was reverted — so the fix is one constant
    /// and one resolver, and a test that pins the two lanes to it.</para>
    /// </summary>
    internal const bool DenseSweepOnlyDefault = true;

    /// <summary>
    /// The env lane's kill switch, resolved from its raw variable. An unset or blank variable is
    /// <see cref="DenseSweepOnlyDefault"/>, which is exactly what the request lane plans with, so "nothing said
    /// otherwise" means the same thing on both.
    /// </summary>
    internal static bool ResolveDenseSweepOnly(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0
            ? DenseSweepOnlyDefault
            : value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    /// <summary>
    /// The stride the ladder falls back to when it cannot measure one. Only ever a seed — the arm reports
    /// <c>strideSource</c> so a reader can tell a measured progression from a defaulted one.
    /// </summary>
    internal const uint AssumedValidatorStride = Sts2SpineGeometryMath.DefaultLiveValidatorStride;

    internal const uint AssumedIndexStride = Sts2SpineGeometryMath.DefaultLiveIndexStride;

    /// <summary>
    /// How many meshes a walked run must hold before it is believed. THE ONE THING THAT EXCLUDES THE 256×256
    /// QUAD: the live battery found a four-vertex quad at index 33 whose uvs are exactly [0,1] and whose box sits
    /// inside the skeleton's bounds, and no per-mesh predicate can reject it — it is excluded only because
    /// nothing walks off it. Six is the number that exclusion was recorded at, and it is flat rather than scaled
    /// off the rig's slot count so that it cannot drift away from the case it was chosen for.
    /// </summary>
    internal const int MinimumRunLength = 6;

    /// <summary>How far the stride ladder reaches when measuring the progression off an anchor.</summary>
    internal const int StrideLadderMaxDelta = 24;

    /// <summary>
    /// How many validators past the last hit the INDEX walk searches for the next index. The largest per-mesh
    /// validator delta in the recorded sessions is 24; 48 leaves the same margin again.
    /// </summary>
    internal const uint DefaultIndexWalkReach = 48;

    /// <summary>
    /// How many consecutive indices the INDEX walk may find nothing at before it stops. Deliberately small: a
    /// missed index costs <c>reach</c> probes and the cost of bridging a long hole grows quadratically, so long
    /// holes are the LINE sweep's job, not this one's.
    /// </summary>
    internal const int DefaultIndexWalkMissBudget = 2;

    /// <summary>
    /// Below this many candidates a window is swept densely without trying the walk at all. A window this small
    /// costs ~100 ms dense (byrdonis's window B is 2 184 candidates), which is less than the walk can save and
    /// less than one failed acquisition would cost — and a window this small is exactly where a run too short to
    /// clear <see cref="MinimumRunLength"/> lives.
    /// </summary>
    internal const int DefaultDenseFloorCandidates = 4_096;

    /// <summary>
    /// Hard ceiling on the arm's probes for one window. Equal to the dense sweep's own chunk size, so the arm can
    /// never stall the main thread longer than one chunk of the sweep it replaces — which is what lets it run
    /// synchronously between two awaited frames instead of interleaving its own.
    /// </summary>
    internal const int DefaultProbeBudget = 8_192;

    /// <summary>How many anchors may fail to produce a run before the arm gives up on the window.</summary>
    internal const int DefaultMaxDeadAnchors = 8;

    /// <summary>
    /// How many times a run is re-swept from its own newly discovered ends. Two, because a line sweep from an
    /// end reaches the window edge in one pass and the second round exists only to pick up what the first
    /// round's new members reveal.
    /// </summary>
    internal const int MaxRunRefinements = 2;

    internal const string StopBandClosed = "band-closed";
    internal const string StopColumnsExhausted = "columns-exhausted";
    internal const string StopFoundEnough = "found-enough";
    internal const string StopProbeBudget = "probe-budget";
    internal const string StopDeadAnchors = "dead-anchors";

    /// <summary>
    /// The arm's plan for one window: which columns to scan in which order, and every bound the walks obey.
    /// Pure data, so the whole decision is offline-testable against a simulated id space.
    /// </summary>
    internal sealed record WalkPlan(
        bool Viable,
        string Reason,
        string WindowName,
        Sts2SpineGeometryMath.RidCandidatePlan Window,
        IReadOnlyList<uint> ColumnOrder,
        Sts2SpineGeometryMath.StrideWalkLimits Limits,
        uint AssumedStride,
        int ExpectedRunLength,
        int MinimumRun,
        int ProbeBudget,
        uint IndexWalkReach,
        int IndexWalkMissBudget,
        int MaxDeadAnchors,
        long DenseCandidates);

    /// <summary>What the arm recovered from one window, and every number the bake report carries about it.</summary>
    internal sealed record WalkResult(
        bool Ran,
        string Reason,
        IReadOnlyList<ulong> Found,
        int ColumnsPlanned,
        int ColumnsScanned,
        int ColumnsSkipped,
        long AnchorProbed,
        int AnchorHypotheses,
        int DeadAnchors,
        int Runs,
        uint ValidatorStride,
        uint IndexStride,
        string StrideSource,
        long StrideProbed,
        long LineProbed,
        int LineFound,
        long IndexWalkProbed,
        int IndexWalkFound,
        string StopUp,
        string StopDown,
        IReadOnlyList<int> GapSizes,
        uint IndexExtentLow,
        uint IndexExtentHigh,
        string StopReason,
        bool BudgetExhausted)
    {
        internal long Probed => AnchorProbed + StrideProbed + LineProbed + IndexWalkProbed;

        internal static WalkResult NotRun(string reason)
            => new(
                false, reason, [], 0, 0, 0, 0, 0, 0, 0,
                AssumedValidatorStride, AssumedIndexStride, StrideAssumed, 0, 0, 0, 0, 0,
                "none", "none", [], 0, 0, reason, false);
    }

    /// <summary>
    /// THE CORRECTNESS RULE. Must the dense sweep run anyway?
    ///
    /// <para>Yes unless the walk arm ran on at least one window AND delivered at least one mesh per slot showing
    /// an attachment at the acquisition frame. That is the same count <c>complete</c> is graded on, so the arm
    /// can never be believed on the strength of a partial answer — and the check is on the TOTAL across windows
    /// because that is the only level at which a shortfall is observable: neither window knows how the rig's
    /// meshes are split between them.</para>
    ///
    /// <para>Stated as a function rather than an <c>if</c> in the driver because it is the one rule in this arm
    /// whose failure mode is a silently incomplete artifact, and it must be gradeable without a game.</para>
    /// </summary>
    internal static bool DenseSweepRequired(bool anyWalkRan, int walkedMeshes, int expectedRunLength)
        => !anyWalkRan || walkedMeshes < expectedRunLength;

    /// <summary>Which arm the window's answer came from, for the bake report.</summary>
    internal static string ArmName(bool walkRan, bool denseRan)
        => walkRan
            ? (denseRan ? ArmWalkThenDense : ArmWalk)
            : ArmDense;

    /// <summary>
    /// The column order: the core band's low edge as the SEED, then outward alternation either side of it —
    /// seed, seed−1, seed+1, seed−2, seed+2, … — clipped at whichever window edge it reaches first and carrying
    /// on down the surviving side. Every index appears exactly once: the hint reorders coverage, it never
    /// removes any.
    ///
    /// <para>WHY ALTERNATION, AND NOT A DIRECTION. This used to run UPWARD from the seed to the top of the axis
    /// and wrap round to <c>IndexLow</c> only after it. That is not an ordering, it is a containment claim —
    /// "this window's meshes sit at or above the seed" — and on window B the claim is false. The live split is
    /// recorded on <see cref="Sts2SpineGeoClipSweep.PlanWindowB"/> and not repeated here: the merchant has 13 of
    /// its 14 window-B meshes BELOW the mid bracket index and 1 at or above it, and byrdonis has both of its 2
    /// below. Everything under the seed therefore sat behind a wrap the arm could not afford to reach — an empty
    /// column costs a whole validator-span scan, so <see cref="DefaultProbeBudget"/> buys roughly 26 columns of
    /// window B against the ~100 the wrap needed — and the single mesh above the seed cannot clear
    /// <see cref="MinimumRunLength"/> on its own. The arm recovered NOTHING from the window that exists to catch
    /// those meshes.</para>
    ///
    /// <para>CONSIDERED AND REJECTED: seeding downward first, and choosing the direction per window off
    /// <see cref="Sts2SpineGeoClipSweep.SweepWindow.CoreBandBoundsSweep"/>. Both are the original mistake with
    /// the sign flipped or with a switch bolted onto it. A direction is a claim about where a window's meshes
    /// are; this arm has already been wrong about exactly that claim once, window B's core band is flagged as an
    /// ordering hint precisely because nothing predicts the answer, and the two rigs do not even agree on it
    /// (13-of-14 below against 2-of-2 below). Alternation asserts nothing: at worst it pays twice the columns a
    /// correct guess would have, and it cannot be wrong about the side.</para>
    ///
    /// <para>THE RESIDUAL — this makes the arm CORRECT about the side, not sufficient. Alternation reaches
    /// window B's sub-seed run and the walk keeps all 13 of it, but the ISOLATED mesh above the seed is still
    /// lost: once a run is accepted, <see cref="Acquire"/> stops at the first column outside that run's own index
    /// extent (<see cref="StopBandClosed"/>), and the isolated mesh is past it. So the arm still comes up short,
    /// <c>DenseSweepRequired(true, 13, 44)</c> is still true, and the dense fallback — which has no seed — is
    /// still what makes the artifact complete. Pinned by
    /// <c>Acquire_RecoversWindowBsSubSeedMeshesButStillComesUpShortOfTheIsolatedOne</c>.</para>
    /// </summary>
    internal static IReadOnlyList<uint> PlanColumnOrder(
        Sts2SpineGeometryMath.RidCandidatePlan window,
        uint coreIndexLow)
    {
        if (window.IndexHigh < window.IndexLow)
        {
            return [];
        }

        var seed = Math.Clamp(coreIndexLow, window.IndexLow, window.IndexHigh);
        var span = (long)window.IndexHigh - window.IndexLow + 1;
        var order = new List<uint>((int)Math.Min(int.MaxValue, span)) { seed };

        // Stepped in `long` and bounded by each side's own reach, so a seed sitting on `uint.MaxValue` or on
        // index 0 clips instead of wrapping — window B's axis is floored at 0 and its seed can land anywhere on
        // it. The two sides advance together while both have axis left; past that, only the longer one does.
        var below = (long)seed - window.IndexLow;
        var above = (long)window.IndexHigh - seed;
        for (var step = 1L; step <= Math.Max(below, above); step += 1)
        {
            if (step <= below)
            {
                order.Add((uint)(seed - step));
            }

            if (step <= above)
            {
                order.Add((uint)(seed + step));
            }
        }

        return order;
    }

    /// <summary>
    /// Plan the arm for one window, or refuse it with a named reason. Refusal is a first-class outcome: every
    /// caller of this has a dense sweep to fall back on, so "this window is not worth walking" is an answer.
    /// </summary>
    internal static WalkPlan Plan(
        Sts2SpineGeoClipSweep.SweepWindow window,
        int expectedRunLength,
        bool denseSweepOnly = false,
        uint assumedStride = AssumedValidatorStride,
        int denseFloorCandidates = DefaultDenseFloorCandidates,
        int probeBudget = DefaultProbeBudget,
        uint indexWalkReach = DefaultIndexWalkReach,
        int indexWalkMissBudget = DefaultIndexWalkMissBudget,
        int maxDeadAnchors = DefaultMaxDeadAnchors)
    {
        var plan = window.Plan;
        if (denseSweepOnly)
        {
            return Refuse(window, "the walk arm is disarmed, so this window is swept densely");
        }

        if (plan.ValidatorHigh < plan.ValidatorLow || plan.IndexHigh < plan.IndexLow)
        {
            return Refuse(window, "the window is empty, so there is nothing to walk");
        }

        if (expectedRunLength < MinimumRunLength)
        {
            return Refuse(
                window,
                $"only {expectedRunLength} slot(s) show an attachment, below the {MinimumRunLength}-mesh run "
                + "length that separates a walked run from a lone false positive");
        }

        if (plan.TotalCandidates <= denseFloorCandidates)
        {
            return Refuse(
                window,
                $"the window holds {plan.TotalCandidates} candidates, at or below the {denseFloorCandidates} "
                + "floor where the dense sweep is already cheaper than an acquisition that can miss");
        }

        var stride = Math.Max(1u, assumedStride);
        var indexSpan = (long)plan.IndexHigh - plan.IndexLow + 1;
        var validatorSpan = (long)plan.ValidatorHigh - plan.ValidatorLow + 1;

        // The LINE sweep's ceiling on both run length and consecutive misses is the number of lattice points the
        // line can hold inside this window. Setting the miss budget there is what makes it "no miss budget":
        // it can only ever stop at a window edge, never on a hole.
        var lineLength = (int)Math.Max(1, Math.Min(indexSpan, (validatorSpan / stride) + 1));
        var limits = new Sts2SpineGeometryMath.StrideWalkLimits(
            plan.ValidatorLow,
            plan.ValidatorHigh,
            plan.IndexLow,
            plan.IndexHigh,
            lineLength,
            lineLength);

        return new WalkPlan(
            true,
            "ok",
            window.Name,
            plan,
            PlanColumnOrder(plan, window.CoreIndexLow),
            limits,
            stride,
            expectedRunLength,
            MinimumRunLength,
            Math.Max(1, probeBudget),
            Math.Max(1u, indexWalkReach),
            Math.Max(1, indexWalkMissBudget),
            Math.Max(1, maxDeadAnchors),
            plan.TotalCandidates);
    }

    private static WalkPlan Refuse(Sts2SpineGeoClipSweep.SweepWindow window, string reason)
        => new(
            false,
            reason,
            window.Name,
            window.Plan,
            [],
            new Sts2SpineGeometryMath.StrideWalkLimits(0, 0, 0, 0, 1, 1),
            AssumedValidatorStride,
            0,
            MinimumRunLength,
            0,
            DefaultIndexWalkReach,
            DefaultIndexWalkMissBudget,
            DefaultMaxDeadAnchors,
            window.Plan.TotalCandidates);

    /// <summary>
    /// Run the arm. <paramref name="validate"/> is the SAME predicate the dense sweep applies — one rendering
    /// server round trip plus <see cref="Sts2SpineGeoClipMath.IsPlausibleSlotMesh"/> — injected so the whole
    /// acquisition is testable against a simulated id space with no engine in the room.
    ///
    /// <para>Synchronous on purpose. Its probe budget is one dense chunk, so it fits between the same two frames
    /// the dense sweep would have yielded between, and a caller does not have to thread an await through the
    /// decision.</para>
    /// </summary>
    internal static WalkResult Acquire(WalkPlan plan, Func<ulong, bool> validate)
    {
        if (!plan.Viable)
        {
            return WalkResult.NotRun(plan.Reason);
        }

        var found = new List<ulong>();
        var foundSet = new HashSet<ulong>();
        long anchorProbed = 0, strideProbed = 0, lineProbed = 0, indexWalkProbed = 0;
        int hypotheses = 0, deadAnchors = 0, runs = 0, columnsScanned = 0, columnsSkipped = 0;
        int lineFound = 0, indexWalkFound = 0;
        var stride = plan.AssumedStride;
        var indexStride = AssumedIndexStride;
        var strideSource = StrideAssumed;
        string stopUp = "none", stopDown = "none";
        var gaps = new List<int>();
        uint extentLow = 0, extentHigh = 0;
        var stopReason = StopColumnsExhausted;
        var budgetExhausted = false;

        long Probed() => anchorProbed + strideProbed + lineProbed + indexWalkProbed;

        foreach (var column in plan.ColumnOrder)
        {
            if (Probed() >= plan.ProbeBudget)
            {
                budgetExhausted = true;
                stopReason = StopProbeBudget;
                break;
            }

            if (found.Count >= plan.ExpectedRunLength)
            {
                stopReason = StopFoundEnough;
                break;
            }

            if (deadAnchors >= plan.MaxDeadAnchors)
            {
                stopReason = StopDeadAnchors;
                break;
            }

            if (runs > 0)
            {
                // THE STOP RULE, and where the arm's saving actually comes from. Inside an accepted run's index
                // extent the line sweep has already visited every column, so scanning them again buys nothing;
                // outside it the band is over. There is deliberately NO margin past the extent: the index walk
                // has already looked `IndexWalkMissBudget + 1` columns past each end of the run, so a margin
                // here would re-probe exactly the columns the walk just cleared. Without this rule the arm
                // proves the empty half of a 173-column index axis empty, 223 probes at a time, and saves
                // nothing at all.
                if (column >= extentLow && column <= extentHigh)
                {
                    columnsSkipped += 1;
                    continue;
                }

                stopReason = StopBandClosed;
                break;
            }

            columnsScanned += 1;
            for (var validator = plan.Window.ValidatorLow; ; validator += 1)
            {
                if (Probed() >= plan.ProbeBudget)
                {
                    budgetExhausted = true;
                    stopReason = StopProbeBudget;
                    break;
                }

                var candidate = Sts2SpineGeoClipSweep.ComposeRidId(validator, column);
                if (!foundSet.Contains(candidate))
                {
                    anchorProbed += 1;
                    if (validate(candidate))
                    {
                        hypotheses += 1;
                        var anchor = new Sts2SpineGeometryMath.RidPoint(validator, column);

                        // Re-measured until it succeeds rather than once: the first hypothesis can be a false
                        // positive (the 256×256 quad is exactly that), and a ladder that found nothing off a
                        // non-member says nothing about the rig's real progression.
                        if (!string.Equals(strideSource, StrideMeasured, StringComparison.Ordinal))
                        {
                            var choice = MeasureStride(anchor, validate, plan, out var ladderProbes);
                            strideProbed += ladderProbes;
                            stride = choice.ValidatorStride;
                            indexStride = choice.IndexStride;
                            strideSource = choice.Source;
                        }

                        var line = Sts2SpineGeometryMath.WalkStride(
                            anchor, stride, indexStride, validate, plan.Limits);
                        lineProbed += line.Probed;

                        var byIndex = WalkByIndex(
                            anchor, validate, plan.Limits, plan.IndexWalkReach, plan.IndexWalkMissBudget);
                        indexWalkProbed += byIndex.Probed;

                        var run = new List<ulong>(line.Found);
                        var runSet = new HashSet<ulong>(line.Found);
                        var lineMembers = line.Found.Count;
                        var indexMembers = byIndex.Found.Count;
                        foreach (var id in byIndex.Found)
                        {
                            if (runSet.Add(id))
                            {
                                run.Add(id);
                            }
                        }

                        // REFINE FROM THE RUN'S OWN ENDS. The ladder measures the progression off ONE anchor,
                        // and on the session whose validator deltas are irregular the anchor sits in the
                        // irregular head: the ladder finds nothing, the line sweep degenerates to the anchor
                        // alone, and the two meshes past the merchant's thirteen-point hole are lost. The run
                        // the index walk built, however, states its own progression — take the modal per-mesh
                        // delta from it and sweep that line from each end. That recovers those two, and it
                        // costs one line sweep.
                        for (var round = 0; round < MaxRunRefinements; round += 1)
                        {
                            var refined = RefineRun(run, runSet, validate, plan, out var refineLine, out var refineIndex);
                            lineProbed += refineLine;
                            indexWalkProbed += refineIndex;
                            if (refined == 0)
                            {
                                break;
                            }
                        }

                        if (run.Count >= plan.MinimumRun)
                        {
                            if (runs == 0)
                            {
                                stopUp = line.StopReasonUp;
                                stopDown = line.StopReasonDown;
                                extentLow = column;
                                extentHigh = column;
                            }

                            runs += 1;
                            lineFound += lineMembers;
                            indexWalkFound += indexMembers;
                            gaps.AddRange(line.GapSizes);
                            foreach (var id in run)
                            {
                                if (foundSet.Add(id))
                                {
                                    found.Add(id);
                                }

                                var (_, memberIndex) = Sts2SpineGeoClipSweep.DecodeRidId(id);
                                extentLow = Math.Min(extentLow, memberIndex);
                                extentHigh = Math.Max(extentHigh, memberIndex);
                            }
                        }
                        else
                        {
                            deadAnchors += 1;
                        }

                        // At most one live RID per (owner index, window), so the rest of this column is noise.
                        break;
                    }
                }

                if (validator >= plan.Window.ValidatorHigh)
                {
                    break;
                }
            }
        }

        return new WalkResult(
            true,
            runs > 0 ? "ok" : "no run cleared the minimum run length",
            found,
            plan.ColumnOrder.Count,
            columnsScanned,
            columnsSkipped,
            anchorProbed,
            hypotheses,
            deadAnchors,
            runs,
            stride,
            indexStride,
            strideSource,
            strideProbed,
            lineProbed,
            lineFound,
            indexWalkProbed,
            indexWalkFound,
            stopUp,
            stopDown,
            gaps,
            extentLow,
            extentHigh,
            stopReason,
            budgetExhausted);
    }

    /// <summary>
    /// The progression a WALKED RUN states about itself: the most common per-mesh validator delta between its
    /// members, taken in index order. A pair contributes <c>Δvalidator / Δindex</c> only when that divides
    /// exactly, so a pair straddling a hole (the merchant's Δ70 over 14 indices) votes for the same 5 as its
    /// neighbours instead of for 70.
    ///
    /// <para>This is a strictly better estimator than the ladder for the case that matters: the ladder sees three
    /// points near one anchor, and on the session whose deltas run 12, 11, 5, 5, 5 … the anchor lands in the
    /// irregular head and the ladder measures nothing at all. Twenty-nine pairs vote 5.</para>
    /// </summary>
    internal static uint ModalStride(IReadOnlyList<ulong> run, uint fallback)
    {
        if (run.Count < 2)
        {
            return Math.Max(1u, fallback);
        }

        var points = run
            .Select(Sts2SpineGeoClipSweep.DecodeRidId)
            .OrderBy(point => point.Index)
            .ToList();
        var votes = new Dictionary<uint, int>();
        for (var i = 1; i < points.Count; i += 1)
        {
            if (points[i].Index <= points[i - 1].Index || points[i].Validator <= points[i - 1].Validator)
            {
                continue;
            }

            var deltaIndex = points[i].Index - points[i - 1].Index;
            var deltaValidator = points[i].Validator - points[i - 1].Validator;
            if (deltaIndex == 0 || deltaValidator % deltaIndex != 0)
            {
                continue;
            }

            var stride = deltaValidator / deltaIndex;
            votes[stride] = votes.GetValueOrDefault(stride) + 1;
        }

        if (votes.Count == 0)
        {
            return Math.Max(1u, fallback);
        }

        // Ties go to the SMALLEST delta: a smaller stride's line is a superset of a multiple's, so it can only
        // find more, and the walk pays one probe per lattice point either way.
        return votes.OrderByDescending(vote => vote.Value).ThenBy(vote => vote.Key).First().Key;
    }

    /// <summary>
    /// Sweep the run's own line, at its own modal stride, from each of its ends, and index-walk from each end
    /// too. Returns how many members that added. Bounded: each call is at most two line sweeps and two index
    /// walks, and the caller runs it a fixed number of rounds.
    /// </summary>
    private static int RefineRun(
        List<ulong> run,
        HashSet<ulong> runSet,
        Func<ulong, bool> validate,
        WalkPlan plan,
        out long lineProbed,
        out long indexProbed)
    {
        lineProbed = 0;
        indexProbed = 0;
        if (run.Count == 0)
        {
            return 0;
        }

        var stride = ModalStride(run, plan.AssumedStride);
        var ordered = run.Select(Sts2SpineGeoClipSweep.DecodeRidId).OrderBy(point => point.Index).ToList();
        var ends = ordered.Count == 1
            ? new[] { ordered[0] }
            : [ordered[0], ordered[^1]];

        var added = 0;
        foreach (var (validator, index) in ends)
        {
            var end = new Sts2SpineGeometryMath.RidPoint(validator, index);
            var line = Sts2SpineGeometryMath.WalkStride(end, stride, AssumedIndexStride, validate, plan.Limits);
            lineProbed += line.Probed;
            var byIndex = WalkByIndex(end, validate, plan.Limits, plan.IndexWalkReach, plan.IndexWalkMissBudget);
            indexProbed += byIndex.Probed;

            foreach (var id in line.Found.Concat(byIndex.Found))
            {
                if (runSet.Add(id))
                {
                    run.Add(id);
                    added += 1;
                }
            }
        }

        return added;
    }

    /// <summary>
    /// The stride ladder, from the live probe's recipe D: is there a delta <c>d</c> for which both
    /// <c>(v + d, i + 1)</c> and <c>(v + 2d, i + 2)</c> validate? Two points are collinear by definition, so only
    /// the three-point progression is evidence — and the decision itself is the probe's
    /// <c>ChooseValidatorStride</c>, so the two acquisitions cannot drift apart on what "measured" means.
    /// </summary>
    internal static Sts2SpineGeometryMath.StrideChoice MeasureStride(
        Sts2SpineGeometryMath.RidPoint anchor,
        Func<ulong, bool> validate,
        WalkPlan plan,
        out long probed)
    {
        var singles = new List<uint>();
        var doubles = new List<uint>();
        probed = 0;
        var ceiling = plan.Limits.ValidatorCeiling;
        var indexHigh = plan.Limits.IndexHigh;

        for (uint delta = 1; delta <= StrideLadderMaxDelta; delta += 1)
        {
            if (ceiling >= delta && anchor.Validator <= ceiling - delta && anchor.Index < indexHigh)
            {
                probed += 1;
                if (validate(Sts2SpineGeoClipSweep.ComposeRidId(anchor.Validator + delta, anchor.Index + 1)))
                {
                    singles.Add(delta);
                }
            }

            if (ceiling >= 2 * delta && anchor.Validator <= ceiling - (2 * delta) && anchor.Index + 1 < indexHigh)
            {
                probed += 1;
                if (validate(Sts2SpineGeoClipSweep.ComposeRidId(anchor.Validator + (2 * delta), anchor.Index + 2)))
                {
                    doubles.Add(delta);
                }
            }
        }

        return Sts2SpineGeometryMath.ChooseValidatorStride(singles, doubles, plan.AssumedStride);
    }

    /// <summary>What the index walk recovered. The anchor is NOT included — the line sweep already carries it.</summary>
    internal sealed record IndexWalkResult(
        IReadOnlyList<ulong> Found,
        long Probed,
        string StopUp,
        string StopDown);

    /// <summary>
    /// Step the INDEX axis ±1 from the anchor, searching a bounded validator reach for each step. The axis this
    /// walks is the one that is exact in every recorded session; the validator is only ever searched, never
    /// assumed. That is what recovers a run whose per-mesh validator delta wanders between 5 and 24, which the
    /// constant-stride line sweep cannot see at all.
    ///
    /// <para>A missed index widens the search proportionally (index +2 is looked for over twice the reach,
    /// because the mesh at +1 may exist as an id and simply fail the predicate), so the miss budget stays small:
    /// bridging a long hole this way is quadratic, and long holes belong to the line sweep.</para>
    /// </summary>
    internal static IndexWalkResult WalkByIndex(
        Sts2SpineGeometryMath.RidPoint anchor,
        Func<ulong, bool> validate,
        Sts2SpineGeometryMath.StrideWalkLimits limits,
        uint reach,
        int missBudget)
    {
        var found = new List<ulong>();
        long probed = 0;
        var budget = Math.Max(1, missBudget);
        var span = Math.Max(1u, reach);

        var up = new List<ulong>();
        var stopUp = Step(ascending: true, up);
        var down = new List<ulong>();
        var stopDown = Step(ascending: false, down);

        down.Reverse();
        found.AddRange(down);
        found.AddRange(up);
        return new IndexWalkResult(found, probed, stopUp, stopDown);

        string Step(bool ascending, List<ulong> into)
        {
            var anchorValidator = anchor.Validator;
            var anchorIndex = anchor.Index;
            var misses = 0;
            while (true)
            {
                var step = (uint)(misses + 1);
                if (ascending)
                {
                    if (limits.IndexHigh < step || anchorIndex > limits.IndexHigh - step)
                    {
                        return "index-high";
                    }
                }
                else if (anchorIndex < limits.IndexLow + step)
                {
                    return "index-low";
                }

                var index = ascending ? anchorIndex + step : anchorIndex - step;
                var window = span * step;
                var hit = false;
                for (uint offset = 1; offset <= window; offset += 1)
                {
                    if (ascending)
                    {
                        if (limits.ValidatorCeiling < offset || anchorValidator > limits.ValidatorCeiling - offset)
                        {
                            break;
                        }
                    }
                    else if (anchorValidator < limits.ValidatorFloor + offset)
                    {
                        break;
                    }

                    var validator = ascending ? anchorValidator + offset : anchorValidator - offset;
                    probed += 1;
                    if (!validate(Sts2SpineGeoClipSweep.ComposeRidId(validator, index)))
                    {
                        continue;
                    }

                    into.Add(Sts2SpineGeoClipSweep.ComposeRidId(validator, index));
                    anchorValidator = validator;
                    anchorIndex = index;
                    misses = 0;
                    hit = true;
                    break;
                }

                if (hit)
                {
                    continue;
                }

                misses += 1;
                if (misses > budget)
                {
                    return "consecutive-misses";
                }
            }
        }
    }
}

// ── Atlas association ────────────────────────────────────────────────────────────────────────────────

internal static class Sts2SpineGeoClipAtlas
{
    /// <summary>
    /// A region's box in PAGE PIXELS. Both atlas spellings store width/height in the region's own
    /// (pre-rotation) orientation, so a region packed at 90/270 occupies the swapped box on the page.
    /// </summary>
    internal static Sts2SpineGeoClipMath.SrcRect PageBox(SpineAtlasRegion region)
    {
        var degrees = ((region.Rotate % 360) + 360) % 360;
        return degrees is 90 or 270
            ? new Sts2SpineGeoClipMath.SrcRect(region.X, region.Y, region.Height, region.Width)
            : new Sts2SpineGeoClipMath.SrcRect(region.X, region.Y, region.Width, region.Height);
    }

    internal sealed record PageMatch(int PageId, string Method, string? RegionName);

    /// <summary>
    /// Which atlas page a part's uvs address. The readback hands back uvs normalized to WHICHEVER page the
    /// slot's mesh samples, and nothing in the readback says which one that was — so it has to be recovered.
    ///
    /// <para>Name match comes first (an attachment usually IS its region), but is never trusted alone: an
    /// attachment can carry a `path` that renames it onto a different region, so a name match is only accepted
    /// when the uv box also lands inside that region on its page. Otherwise the uv box is tested for
    /// containment against every region of every page; agreement on ONE page is the answer. A single-page
    /// atlas short-circuits all of it.</para>
    /// </summary>
    internal static PageMatch ResolvePage(
        string? attachmentName,
        double uMin,
        double vMin,
        double uMax,
        double vMax,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> pageSizes,
        double tolerancePx = Sts2SpineGeoClipMath.DefaultContainmentTolerancePx)
    {
        if (pageSizes.Count == 0)
        {
            return new PageMatch(-1, "no-pages", null);
        }

        if (pageSizes.Count == 1)
        {
            return new PageMatch(0, "single-page", null);
        }

        SpineAtlasRegion? named = null;
        foreach (var region in atlas.Regions)
        {
            if (!IsNameMatch(region.Name, attachmentName))
            {
                continue;
            }

            named = region;
            if (Contains(region, uMin, vMin, uMax, vMax, pageSizes, tolerancePx, allowUnrotated: true))
            {
                return new PageMatch(region.PageIndex, "name+containment", region.Name);
            }
        }

        var containing = new List<SpineAtlasRegion>();
        foreach (var region in atlas.Regions)
        {
            if (Contains(region, uMin, vMin, uMax, vMax, pageSizes, tolerancePx, allowUnrotated: true))
            {
                containing.Add(region);
            }
        }

        if (containing.Count > 0)
        {
            var pages = containing.Select(region => region.PageIndex).Distinct().ToArray();
            if (pages.Length == 1)
            {
                return new PageMatch(pages[0], "containment", containing[0].Name);
            }

            // Ambiguous across pages: the tightest containing region is the most specific claim, so take it and
            // say so — a caller that cares can re-derive from the reported region name.
            var tightest = containing
                .OrderBy(region => (long)PageBox(region).Width * PageBox(region).Height)
                .First();
            return new PageMatch(tightest.PageIndex, "containment-ambiguous", tightest.Name);
        }

        if (named is not null)
        {
            return new PageMatch(named.PageIndex, "name-only", named.Name);
        }

        return new PageMatch(-1, "unresolved", null);
    }

    // Internal rather than private because the ASSOCIATION fallback below needs exactly this notion of "the
    // region this attachment names" — an atlas packed from folders spells a region `folder/name`, and two
    // different answers to that question would make the two passes disagree about the same rig.
    internal static bool IsNameMatch(string regionName, string? attachmentName)
    {
        if (string.IsNullOrEmpty(attachmentName))
        {
            return false;
        }

        return string.Equals(regionName, attachmentName, StringComparison.Ordinal)
            || regionName.EndsWith("/" + attachmentName, StringComparison.Ordinal);
    }

    // ── Association fallback: which unclaimed mesh is this slot's? ───────────────────────────────────

    /// <summary>One candidate mesh's uv bounding box. <c>Order</c> is the caller's own handle for it.</summary>
    internal readonly record struct MeshUvBox(int Order, double UMin, double VMin, double UMax, double VMax);

    /// <summary>One member of an ambiguous verdict's tie set, and how good ITS OWN match was.</summary>
    /// <param name="Order">The candidate's handle in the caller's own list — the same one <see
    /// cref="RegionMatch.Order"/> speaks in.</param>
    /// <param name="Method">
    /// <see cref="MatchExact"/> or <see cref="MatchContainment"/> for this candidate alone. Carried per candidate
    /// rather than per verdict because it is what an outside tie-breaker's claim gets GRADED on: choosing among
    /// candidates that each coincide with the named region to sub-pixel is a different act from choosing among
    /// candidates that merely land inside it.
    /// </param>
    internal readonly record struct TiedCandidate(int Order, string Method);

    /// <summary>
    /// The verdict. <c>Order</c> is -1 whenever nothing was accepted — ambiguity is REPORTED, never guessed,
    /// because a wrong association bakes another slot's art onto this slot for the whole clip, which is a worse
    /// artifact than a missing part (a missing part is visibly missing; a swapped one looks deliberate).
    ///
    /// <para><see cref="TiedCandidates"/> names WHO tied. It is populated only on <see cref="MatchAmbiguous"/>
    /// over a CONTAINED pool of two or more, and it exists so a caller holding evidence this matcher does not
    /// have — the slot's own colour at the sampled pose, say — can break the tie on that evidence instead. It is
    /// not a ranking and it carries no preference: the entries are the candidates this matcher could not
    /// separate, in candidate-list order, and a caller that took the first of them would be making precisely the
    /// guess this refuses.</para>
    /// </summary>
    internal sealed record RegionMatch(
        int Order,
        string Method,
        string? RegionName,
        double Score,
        double RunnerUpScore,
        int ContainedCount,
        IReadOnlyList<TiedCandidate>? TiedCandidatesOrNull = null)
    {
        internal IReadOnlyList<TiedCandidate> TiedCandidates => TiedCandidatesOrNull ?? [];
    }

    // One candidate scored against the best of the regions its slot's attachment names.
    private sealed record ScoredCandidate(
        MeshUvBox Box,
        double Score,
        double Corner,
        bool Contained,
        string RegionName);

    internal const string MatchExact = "uv-region-exact";
    internal const string MatchContainment = "containment";
    internal const string MatchAmbiguous = "ambiguous";
    internal const string MatchNone = "none";
    internal const string MatchNoSuchRegion = "no-such-region";

    // A candidate-only atlas fact for diagnostics. Unlike association, it intentionally has no attachment name:
    // it says only whether this UV box fits zero, one, or several parsed atlas regions, and never exposes one.
    internal const string UvProvenanceNone = "none";
    internal const string UvProvenanceUnique = "unique";
    internal const string UvProvenanceAmbiguous = "ambiguous";

    internal static string ClassifyUvProvenance(
        double uMin,
        double vMin,
        double uMax,
        double vMax,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> pageSizes,
        double tolerancePx = Sts2SpineGeoClipMath.DefaultContainmentTolerancePx)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(pageSizes);
        var matches = 0;
        foreach (var region in atlas.Regions)
        {
            if (Contains(region, uMin, vMin, uMax, vMax, pageSizes, tolerancePx, allowUnrotated: true)
                && ++matches > 1)
            {
                return UvProvenanceAmbiguous;
            }
        }

        return matches == 1 ? UvProvenanceUnique : UvProvenanceNone;
    }

    /// <summary>The best score must beat the runner-up by this factor to be accepted on ranking alone.</summary>
    internal const double DefaultRegionMarginRatio = 4d;

    /// <summary>…or beat it by this many page pixels of corner disagreement, whichever is satisfied first.</summary>
    internal const double DefaultRegionMarginPx = 8d;

    /// <summary>
    /// Which of a set of unclaimed meshes draws the attachment named by <paramref name="attachmentName"/>,
    /// decided from the atlas alone.
    ///
    /// <para>WHY THIS EXISTS. The primary association is a colour nudge: flip one slot's alpha, see which mesh's
    /// vertex colours move. It fails on any slot whose colour the animation's own timeline rewrites before the
    /// next frame, and it cannot discriminate when several meshes move together. But a slot's attachment NAMES an
    /// atlas region, and the mesh's uvs address a box on a page — so when the two agree, that is independent
    /// evidence of the same pairing, available without touching the running skeleton at all.</para>
    ///
    /// <para>SCORING. Each candidate's uv box is mapped to page pixels through the named region's page, then
    /// scored against the region's own box as <c>|Δarea| + Σ|Δcorner|</c>. Containment (with the same tolerance
    /// the page resolver uses) is the strong signal and is preferred; ranking is the fallback. A match is accepted
    /// only on UNIQUE containment, or when the best score beats the runner-up by
    /// <see cref="DefaultRegionMarginRatio"/>× or by <see cref="DefaultRegionMarginPx"/> page pixels of corner
    /// disagreement — the absolute margin is measured on the corner term alone because that is the half of the
    /// score denominated in pixels (the area term is px² and would make an absolute pixel threshold meaningless).
    /// Anything else reports <see cref="MatchAmbiguous"/> and stays unassociated.</para>
    ///
    /// <para>A region on a page with no declared size cannot be located at all, so it counts as
    /// <see cref="MatchNoSuchRegion"/> rather than being scored against a zero-sized page.</para>
    /// </summary>
    internal static RegionMatch MatchMeshToAttachmentRegion(
        string? attachmentName,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> pageSizes,
        IReadOnlyList<MeshUvBox> candidates,
        double containmentTolerancePx = Sts2SpineGeoClipMath.DefaultContainmentTolerancePx,
        double marginRatio = DefaultRegionMarginRatio,
        double marginAbsolutePx = DefaultRegionMarginPx)
    {
        var regions = new List<SpineAtlasRegion>();
        foreach (var region in atlas.Regions)
        {
            if (!IsNameMatch(region.Name, attachmentName)
                || region.PageIndex < 0
                || region.PageIndex >= pageSizes.Count)
            {
                continue;
            }

            var (pageWidth, pageHeight) = pageSizes[region.PageIndex];
            if (pageWidth > 0 && pageHeight > 0)
            {
                regions.Add(region);
            }
        }

        if (regions.Count == 0)
        {
            return new RegionMatch(-1, MatchNoSuchRegion, null, double.PositiveInfinity, double.PositiveInfinity, 0);
        }

        if (candidates.Count == 0)
        {
            return new RegionMatch(-1, MatchNone, regions[0].Name, double.PositiveInfinity, double.PositiveInfinity, 0);
        }

        // Best (region, candidate) pair per candidate: an attachment name can legitimately appear on more than
        // one region (an indexed animation strip), and the candidate only has to match ONE of them.
        var scored = new List<ScoredCandidate>();
        foreach (var candidate in candidates)
        {
            ScoredCandidate? best = null;
            foreach (var region in regions)
            {
                var (pageWidth, pageHeight) = pageSizes[region.PageIndex];
                var box = PageBox(region);
                var x0 = candidate.UMin * pageWidth;
                var y0 = candidate.VMin * pageHeight;
                var x1 = candidate.UMax * pageWidth;
                var y1 = candidate.VMax * pageHeight;

                var corner = Math.Abs(x0 - box.X)
                    + Math.Abs(y0 - box.Y)
                    + Math.Abs(x1 - (box.X + box.Width))
                    + Math.Abs(y1 - (box.Y + box.Height));
                var area = Math.Abs(((x1 - x0) * (y1 - y0)) - ((double)box.Width * box.Height));
                var score = area + corner;
                var contained = Inside(box, x0, y0, x1, y1, containmentTolerancePx);

                // Containment outranks score outright; score only breaks ties within the same containment class.
                var better = best is null
                    || (contained && !best.Contained)
                    || (contained == best.Contained && score < best.Score);
                if (better)
                {
                    best = new ScoredCandidate(candidate, score, corner, contained, region.Name);
                }
            }

            if (best is not null)
            {
                scored.Add(best);
            }
        }

        var containedEntries = scored.Where(entry => entry.Contained).ToList();
        var pool = (containedEntries.Count > 0 ? containedEntries : scored).OrderBy(entry => entry.Score).ToList();

        var winner = pool[0];
        var runnerUp = pool.Count > 1 ? pool[1] : null;
        var runnerUpScore = runnerUp?.Score ?? double.PositiveInfinity;
        var runnerUpCorner = runnerUp?.Corner ?? double.PositiveInfinity;

        // `winner.Score < runnerUpScore` is not redundant with the ratio: two candidates that both score ZERO
        // (identical uv boxes — the same region packed twice, or two slots sharing an attachment) would otherwise
        // satisfy `0 * 4 <= 0` and be silently resolved in list order, which is exactly the guess this refuses.
        var accepted = containedEntries.Count == 1
            || pool.Count == 1
            || (winner.Score < runnerUpScore && winner.Score * marginRatio <= runnerUpScore)
            || runnerUpCorner - winner.Corner >= marginAbsolutePx;
        if (!accepted)
        {
            // WHO tied, for a caller that can bring evidence the atlas does not have. Restricted to a CONTAINED
            // pool on purpose: a tie among candidates that do not even land inside the named region is not a tie
            // worth handing on, it is a slot whose mesh was never swept in. Membership is "not separable from the
            // winner by the acceptance test above", which is symmetric in the only way that matters here — every
            // member could have been the winner had the list arrived in another order.
            var tied = containedEntries.Count > 1
                ? pool
                    .Where(entry => !Separates(winner, entry, marginRatio, marginAbsolutePx))
                    .Select(entry => new TiedCandidate(
                        entry.Box.Order,
                        entry.Corner <= containmentTolerancePx ? MatchExact : MatchContainment))
                    .ToArray()
                : [];
            return new RegionMatch(
                -1,
                MatchAmbiguous,
                winner.RegionName,
                winner.Score,
                runnerUpScore,
                containedEntries.Count,
                tied.Length > 1 ? tied : null);
        }

        return new RegionMatch(
            winner.Box.Order,
            winner.Corner <= containmentTolerancePx ? MatchExact : MatchContainment,
            winner.RegionName,
            winner.Score,
            runnerUpScore,
            containedEntries.Count);
    }

    /// <summary>
    /// Whether <paramref name="winner"/> beats <paramref name="other"/> by enough to be ACCEPTED over it — the
    /// acceptance test of <see cref="MatchMeshToAttachmentRegion"/>, factored out so the ambiguous branch can ask
    /// the same question of every pool entry instead of only the runner-up.
    /// </summary>
    private static bool Separates(
        ScoredCandidate winner,
        ScoredCandidate other,
        double marginRatio,
        double marginAbsolutePx)
        => (winner.Score < other.Score && winner.Score * marginRatio <= other.Score)
            || other.Corner - winner.Corner >= marginAbsolutePx;

    // ── ATLAS-FIRST association: what the atlas can do WITHOUT the colour probe ──────────────────────

    /// <summary>One ordinal the atlas resolved on its own. <c>Order</c> is the mesh's index in the caller's own
    /// candidate list, the same handle <see cref="RegionMatch.Order"/> speaks in.</summary>
    internal sealed record AtlasFirstMatch(int Ordinal, int Order, string Method, string? RegionName);

    /// <summary>One ordinal the atlas declined to resolve, and why — <see cref="MatchAmbiguous"/>,
    /// <see cref="MatchNoSuchRegion"/> or <see cref="MatchNone"/>.</summary>
    internal sealed record AtlasFirstRefusal(int Ordinal, string Reason, string? RegionName);

    internal sealed record AtlasFirstAssociation(
        IReadOnlyDictionary<int, int> OrderByOrdinal,
        IReadOnlyList<AtlasFirstMatch> Resolved,
        IReadOnlyList<AtlasFirstRefusal> Refused);

    /// <summary>
    /// Resolve EVERY ordinal against the atlas alone, to a fixpoint — the question "could the atlas have done
    /// discovery, instead of standing behind the colour probe as a tie-breaker and a fallback?".
    ///
    /// <para>This is the baker's existing atlas-region fallback loop, lifted out of the engine half so it can be
    /// run on inputs it did not gather. The semantics are the fallback's, deliberately unchanged: candidates are
    /// the UNCLAIMED meshes, a resolution claims its mesh immediately, and the pass repeats while anything
    /// progressed — because one slot claiming a mesh is what can turn the next slot's three-way tie into a unique
    /// containment. Bounded by the ordinal count: every pass that changes anything resolves at least one.</para>
    ///
    /// <para>IT REFUSES THE SAME WAY. Acceptance is <see cref="MatchMeshToAttachmentRegion"/>'s, including the
    /// zero-versus-zero refusal — two slots sharing an attachment name over one identical uv box are BOTH left
    /// unresolved rather than handed out in list order. That is not fastidiousness: the caller compares this
    /// against a measured association, and a shadow arm that guessed would report agreement it had not earned.</para>
    ///
    /// <para>REPORT-ONLY by construction: it takes data and returns data, and there is no overload that writes
    /// anything back into a bake.</para>
    /// </summary>
    /// <param name="attachmentByOrdinal">Slot ordinal → the attachment it draws where the association is measured.</param>
    /// <param name="meshUvBoxes">Every candidate mesh's uv box, carrying the caller's own <c>Order</c> handle.</param>
    internal static AtlasFirstAssociation AssociateAll(
        IReadOnlyDictionary<int, string?> attachmentByOrdinal,
        IReadOnlyList<MeshUvBox> meshUvBoxes,
        SpineAtlasDocument atlas,
        IReadOnlyList<(int Width, int Height)> pageSizes,
        double containmentTolerancePx = Sts2SpineGeoClipMath.DefaultContainmentTolerancePx,
        double marginRatio = DefaultRegionMarginRatio,
        double marginAbsolutePx = DefaultRegionMarginPx)
    {
        var resolved = new List<AtlasFirstMatch>();
        var orderByOrdinal = new Dictionary<int, int>();
        var claimed = new HashSet<int>();
        var refusals = new Dictionary<int, AtlasFirstRefusal>();

        for (var pass = 0; pass <= attachmentByOrdinal.Count; pass += 1)
        {
            var progressed = false;
            foreach (var ordinal in attachmentByOrdinal.Keys
                         .Where(candidate => !orderByOrdinal.ContainsKey(candidate))
                         .OrderBy(candidate => candidate))
            {
                var candidates = meshUvBoxes.Where(box => !claimed.Contains(box.Order)).ToList();
                var match = MatchMeshToAttachmentRegion(
                    attachmentByOrdinal[ordinal],
                    atlas,
                    pageSizes,
                    candidates,
                    containmentTolerancePx,
                    marginRatio,
                    marginAbsolutePx);
                if (match.Order >= 0 && claimed.Add(match.Order))
                {
                    orderByOrdinal[ordinal] = match.Order;
                    resolved.Add(new AtlasFirstMatch(ordinal, match.Order, match.Method, match.RegionName));
                    refusals.Remove(ordinal);
                    progressed = true;
                }
                else
                {
                    // Overwritten rather than appended: an earlier pass's verdict on this ordinal is stale the
                    // moment any other ordinal claims a mesh, so only the LAST one describes the fixpoint.
                    refusals[ordinal] = new AtlasFirstRefusal(ordinal, match.Method, match.RegionName);
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return new AtlasFirstAssociation(
            orderByOrdinal,
            [.. resolved.OrderBy(match => match.Ordinal)],
            [.. refusals.Values.OrderBy(refusal => refusal.Ordinal)]);
    }

    private static bool Contains(
        SpineAtlasRegion region,
        double uMin,
        double vMin,
        double uMax,
        double vMax,
        IReadOnlyList<(int Width, int Height)> pageSizes,
        double tolerancePx,
        bool allowUnrotated)
    {
        if (region.PageIndex < 0 || region.PageIndex >= pageSizes.Count)
        {
            return false;
        }

        var (pageWidth, pageHeight) = pageSizes[region.PageIndex];
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return false;
        }

        var x0 = uMin * pageWidth;
        var y0 = vMin * pageHeight;
        var x1 = uMax * pageWidth;
        var y1 = vMax * pageHeight;

        if (Inside(PageBox(region), x0, y0, x1, y1, tolerancePx))
        {
            return true;
        }

        // Fallback for an atlas whose rotate spelling we read the other way round than the packer wrote it: the
        // unrotated box. Reported the same way, because the page (not the exact box) is what this decides.
        return allowUnrotated
            && Inside(new Sts2SpineGeoClipMath.SrcRect(region.X, region.Y, region.Width, region.Height), x0, y0, x1, y1, tolerancePx);
    }

    private static bool Inside(Sts2SpineGeoClipMath.SrcRect box, double x0, double y0, double x1, double y1, double tolerance)
        => x0 >= box.X - tolerance
            && y0 >= box.Y - tolerance
            && x1 <= box.X + box.Width + tolerance
            && y1 <= box.Y + box.Height + tolerance;

    /// <summary>
    /// The imported `.spatlas` ships as a JSON envelope carrying the text atlas verbatim. Pulled out with the
    /// JSON reader rather than a substring search so an escaped newline stays an escaped newline.
    /// </summary>
    internal static string ExtractAtlasTextFromEnvelope(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("atlas_data", out var atlasData)
                && atlasData.ValueKind == JsonValueKind.String)
            {
                return atlasData.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not the envelope shape; fall through and let the caller report an empty parse.
        }

        return string.Empty;
    }

    /// <summary>
    /// Pull the imported resource path out of a Godot `.import` sidecar. Same shape as the font path extraction
    /// in Sts2AssetExtractProvider.ResourceIO: find the imported prefix, run to the known suffix.
    /// </summary>
    internal static string ExtractImportedResourcePath(string importMetadata, string suffix)
    {
        const string Prefix = "res://.godot/imported/";
        if (string.IsNullOrEmpty(importMetadata) || string.IsNullOrEmpty(suffix))
        {
            return string.Empty;
        }

        var start = importMetadata.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = importMetadata.IndexOf(suffix, start, StringComparison.Ordinal);
        return end < 0 ? string.Empty : importMetadata[start..(end + suffix.Length)];
    }
}

// ── Atlas page EXPORT: the source-PNG passthrough, and page identity ─────────────────────────────────

/// <summary>
/// Whether a page's SOURCE image on disk may be copied verbatim instead of being re-encoded out of the runtime
/// texture, and what a page's bytes are NAMED.
///
/// <para>WHY. <c>ExportPages</c> costs <c>Texture2D.GetImage()</c> → <c>Decompress()</c> → <c>SavePngToBuffer()</c>
/// per atlas page — measured at 161 ms for the merchant's four pages, on every bake, against a 271 ms budget. The
/// pages are IMPORTED from source images that are still on disk (it is how the atlas TEXT is read at all: through
/// the resource's <c>.import</c> sidecar), so when the source is already a PNG the bytes can simply be copied.
/// Lossless, and near-free.</para>
///
/// <para>WHY IT IS GUARDED THIS HARD. A page is addressed by the parts' src rects in PAGE PIXELS, and the store
/// content-addresses and SHARES pages across every pose of every rig. Copying the wrong file does not fail — it
/// draws a creature with somebody else's atlas, cached for ever. So the copy is accepted only when the source's
/// own IHDR agrees with what the runtime texture reports AND with what the atlas text declares, and (in
/// <see cref="Mode.Strict"/>) when the file name is the one the atlas names. Every refusal carries a token, so a
/// live run says which clause declined rather than just showing no saving.</para>
/// </summary>
internal static class Sts2SpineGeoClipPageSource
{
    /// <summary>Selector/kill-switch: <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> off, <c>loose</c> drops the name check.</summary>
    internal const string ModeEnv = "SPIRECTL_SPINE_GEOCLIP_PAGE_PASSTHROUGH";

    internal enum Mode
    {
        /// <summary>Always re-encode from the runtime texture, exactly as the baker did before this existed.</summary>
        Disabled,

        /// <summary>Dimensions only. The escape hatch for a build whose atlas text names pages another way.</summary>
        Loose,

        /// <summary>DEFAULT: dimensions AND the atlas's own page name.</summary>
        Strict,
    }

    /// <summary>An accepted passthrough, or a refusal with the clause that refused it.</summary>
    internal sealed record Decision(bool Accept, string Reason);

    internal readonly record struct PngSize(int Width, int Height);

    internal static Mode ParseMode(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => Mode.Disabled,
            "loose" => Mode.Loose,
            _ => Mode.Strict,
        };

    internal static Mode ModeFromEnvironment()
        => ParseMode(Environment.GetEnvironmentVariable(ModeEnv));

    /// <summary>
    /// The image's declared size, read out of the PNG signature + IHDR — the first 24 bytes, and nothing else.
    /// Deliberately not a decode: the point is to verify the file describes the page the manifest references
    /// without paying to turn it into pixels.
    /// </summary>
    internal static PngSize? ReadPngSize(byte[]? bytes)
    {
        // 8-byte signature, then a 4-byte length, "IHDR", width, height — 24 bytes before anything varies.
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes is null || bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
        {
            return null;
        }

        if (bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            return null;
        }

        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0 ? new PngSize(width, height) : null;
    }

    /// <summary>The trailing segment of a <c>res://</c> path. Godot paths are '/'-separated on every platform.</summary>
    internal static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// <paramref name="fileName"/> resolved BESIDE <paramref name="siblingPath"/> — the second candidate source
    /// for a page, and often the only one.
    /// </summary>
    /// <remarks>
    /// A page texture's own <c>ResourcePath</c> may not be a PNG at all: an importer is free to hand back a
    /// sub-resource of the imported atlas (<c>…​.spatlas::1</c>) rather than a standalone image, and then there is
    /// no file to copy by that route. But a Spine atlas NAMES its pages, and the packer writes them next to the
    /// atlas — which is how every Spine runtime finds them in the first place. So the atlas's own directory plus
    /// the declared page name is the other place to look, and it is subject to exactly the same size checks as
    /// the first, which is what keeps a guess from becoming a wrong page.
    /// </remarks>
    internal static string SiblingPath(string? siblingPath, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(siblingPath) || string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var name = fileName.Trim();
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            // A page name is a file name. One that carries a separator is not something to resolve against a
            // directory — it is a reason to leave this candidate alone.
            return string.Empty;
        }

        var slash = siblingPath.LastIndexOf('/');
        return slash < 0 ? name : siblingPath[..(slash + 1)] + name;
    }

    /// <summary>
    /// May <paramref name="sourceBytes"/> be written as page <paramref name="atlasPageName"/> verbatim?
    /// </summary>
    /// <param name="textureWidth">What the RUNTIME texture reports. Zero/negative means "not known", and an
    /// unknown runtime size is a refusal rather than a pass: the whole point is agreement between two sources.</param>
    /// <param name="atlasWidth">What the atlas TEXT declares for this page, or 0 when it declares nothing.</param>
    /// <param name="importSidecar">
    /// The source image's Godot <c>.import</c> text. Consulted in <see cref="Mode.Strict"/> only, and only for
    /// importer settings that make the RUNTIME texture's pixels differ from the file's — see
    /// <see cref="ImportAltersPixels"/>. An empty sidecar is a refusal in strict mode: the same read that fetches
    /// the atlas text proves sidecars are readable on this build, so an unreadable one means something is not
    /// what it looks like.
    /// </param>
    internal static Decision Decide(
        Mode mode,
        string? sourcePath,
        byte[]? sourceBytes,
        int textureWidth,
        int textureHeight,
        string? atlasPageName,
        int atlasWidth,
        int atlasHeight,
        string? importSidecar = null)
    {
        if (mode == Mode.Disabled)
        {
            return new Decision(false, "disabled");
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new Decision(false, "no-source-path");
        }

        if (!sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return new Decision(false, "source-not-png");
        }

        if (sourceBytes is null || sourceBytes.Length == 0)
        {
            return new Decision(false, "source-unreadable");
        }

        if (ReadPngSize(sourceBytes) is not { } size)
        {
            return new Decision(false, "source-not-a-png-file");
        }

        if (textureWidth <= 0 || textureHeight <= 0)
        {
            return new Decision(false, "texture-size-unknown");
        }

        if (size.Width != textureWidth || size.Height != textureHeight)
        {
            return new Decision(
                false,
                $"texture-size-mismatch:{size.Width}x{size.Height}-vs-{textureWidth}x{textureHeight}");
        }

        if (atlasWidth > 0 && atlasHeight > 0 && (size.Width != atlasWidth || size.Height != atlasHeight))
        {
            return new Decision(
                false,
                $"atlas-size-mismatch:{size.Width}x{size.Height}-vs-{atlasWidth}x{atlasHeight}");
        }

        if (mode == Mode.Strict)
        {
            if (!string.IsNullOrWhiteSpace(atlasPageName))
            {
                var sourceName = FileName(sourcePath);
                if (!string.Equals(sourceName, atlasPageName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // NOT folded into the size clauses: this one is the conservative half, and an operator who
                    // measures it firing on a real rig can drop to `loose` without a rebuild.
                    return new Decision(false, $"page-name-mismatch:{sourceName}-vs-{atlasPageName.Trim()}");
                }
            }

            if (string.IsNullOrWhiteSpace(importSidecar))
            {
                return new Decision(false, "no-import-sidecar");
            }

            if (ImportAltersPixels(importSidecar) is { } altered)
            {
                return new Decision(false, altered);
            }
        }

        return new Decision(true, "ok");
    }

    /// <summary>
    /// An importer setting that makes the RUNTIME texture's pixels differ from the source file's, or null.
    /// </summary>
    /// <remarks>
    /// <para>Only <c>premult_alpha</c> is checked, and the omissions are chosen rather than overlooked. VRAM
    /// compression makes the runtime texture LOSSY against the file, so a bake that copies the file is strictly
    /// more faithful, not less. <c>size_limit</c> changes the dimensions, which the size clauses above already
    /// catch from two directions. Mipmaps and roughness never touch level 0. <c>fix_alpha_border</c> is
    /// deliberately tolerated: it only rewrites RGB under fully transparent pixels, which a part crop never
    /// samples as colour, and it defaults ON — refusing it would refuse every page and buy nothing.</para>
    /// <para>Premultiplied alpha is different in kind. The atlas may ALREADY be premultiplied by the Spine
    /// packer (that is what its <c>pma</c> flag records, and the file on disk carries it); the importer doing it
    /// AGAIN would leave the runtime texture darker than the file, and a bake that copied the file would draw a
    /// visibly different creature from the one the raster still shows.</para>
    /// </remarks>
    internal static string? ImportAltersPixels(string? importSidecar)
    {
        if (string.IsNullOrEmpty(importSidecar))
        {
            return null;
        }

        foreach (var line in importSidecar.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("process/premult_alpha", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals > 0 && trimmed[(equals + 1)..].Trim() is "true" or "1")
            {
                return "import-premultiplies-alpha";
            }
        }

        return null;
    }
}

// ── Atlas page EXPORT, second source: the IMPORTED texture the export actually ships ─────────────────

/// <summary>
/// The atlas page's bytes taken out of the <c>.ctex</c> the <c>.import</c> sidecar names, instead of out of a
/// source <c>.png</c> an exported build does not contain.
///
/// <para>WHY THIS EXISTS AND <see cref="Sts2SpineGeoClipPageSource"/> IS NOT ENOUGH. The source passthrough
/// declined 401 times out of 401 on <c>source-unreadable</c>, and the reason is structural rather than a bug: a
/// Godot EXPORT strips source images. The shipped pck holds exactly one <c>.png</c> in 12 327 entries (the app
/// icon); what it ships instead, for every imported texture, is <c>res://.godot/imported/&lt;name&gt;.png-&lt;hash&gt;.ctex</c>
/// — and that container's payload is, for a lossless import, ALREADY a WebP. So the page can still be copied
/// rather than decoded and re-encoded; the file to copy from is just one indirection further in.</para>
///
/// <para>WHY IT IS SAFE IN A WAY THE SOURCE PASSTHROUGH IS NOT. A <c>.ctex</c> is POST-import: whatever the
/// importer did to the pixels is already in it, because it is the very thing the runtime texture is built from.
/// The whole <see cref="Sts2SpineGeoClipPageSource.ImportAltersPixels"/> question — does the importer
/// premultiply, does it alter the border — cannot arise here. What is left to check is that the container is
/// the shape this reader understands and that it describes the page the manifest references, which is what
/// every clause below does. A wrong page is content-addressed and cached for ever, so anything unexpected
/// declines to the re-encode and names the clause that declined.</para>
/// </summary>
internal static class Sts2SpineGeoClipCtexSource
{
    /// <summary>Selector/kill-switch. Default ARMED — see <see cref="ParseMode"/>.</summary>
    internal const string ModeEnv = "SPIRECTL_SPINE_GEOCLIP_PAGE_CTEX";

    /// <summary>Godot's <c>CompressedTexture2D</c> container magic.</summary>
    private const int HeaderLength = 56;

    private const int SupportedContainerVersion = 1;

    /// <summary>Godot's <c>DATA_FORMAT_WEBP</c>. <c>0</c> is a raw (often VRAM-compressed) image, <c>1</c> PNG.</summary>
    private const int DataFormatWebp = 2;

    /// <summary>Godot's <c>Image.FORMAT_RGBA8</c>.</summary>
    private const int ImageFormatRgba8 = 5;

    internal enum Mode
    {
        /// <summary>
        /// Kill switch. The <c>.ctex</c> is never consulted; the baker behaves exactly as it did before. Reached
        /// only by spelling it (<c>0</c>/<c>off</c>/<c>false</c>/<c>no</c>) — it is no longer the default.
        /// </summary>
        Disabled,

        /// <summary>DEFAULT. Armed, and the page is written under its truthful <c>.webp</c> name.</summary>
        Webp,

        /// <summary>
        /// Armed, but the page keeps the <c>page-N.png</c> NAME while carrying WebP bytes. The compatibility arm
        /// for a consumer whose artifact whitelist is spelled in <c>.png</c> (CouchCoop's
        /// <c>CouchCoopGeoclipDirectory.IsPageFileName</c> is, today) — it is a lie about the codec, so it is a
        /// deliberate opt-in and never a default.
        /// </summary>
        PngName,
    }

    /// <summary>The bytes to write and what they describe, or a refusal naming the clause that declined.</summary>
    internal sealed record CtexPayload(byte[] Bytes, int Width, int Height);

    internal sealed record CtexDecision(CtexPayload? Payload, string Reason)
    {
        internal bool Accept => Payload is not null;
    }

    /// <summary>
    /// DEFAULT ARMED, with the same polarity as <see cref="Sts2SpineGeoClipPageSource.ParseMode"/>: an unset or
    /// unrecognised value takes the passthrough, and only an explicit off-word declines it.
    ///
    /// <para>Armed on the phase-6 WS-5 gate measurement: over 16 product-lane arms per condition, on the live
    /// <c>/geoclips/</c> route, the passthrough fired 80/80 pages with no declines and cut the merchant's page
    /// phase from 174 ms to 21 ms and its whole bake from 440 ms to 300 ms (byrdonis 395 -> 335). Every one of
    /// those 16 artifacts decoded to pixels identical to the phase-4 reference, with <c>parts</c> — the drawn
    /// geometry — unchanged, so the only thing that moved is the container.</para>
    ///
    /// <para><c>1</c>/<c>on</c>/<c>webp</c> are kept as explicit spellings rather than folded into the default,
    /// because they are what the measurement arms and the docs already say, and a value that used to arm the
    /// feature must not silently become the one that disables it.</para>
    /// </summary>
    internal static Mode ParseMode(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => Mode.Disabled,
            "png" or "pngname" or "png-name" => Mode.PngName,
            _ => Mode.Webp,
        };

    internal static Mode ModeFromEnvironment()
        => ParseMode(Environment.GetEnvironmentVariable(ModeEnv));

    /// <summary>The file extension a page written from this mode's bytes carries.</summary>
    internal static string ExtensionFor(Mode mode) => mode == Mode.PngName ? "png" : "webp";

    /// <summary>
    /// The <c>res://.godot/imported/….ctex</c> path a source image's <c>.import</c> sidecar names, or empty.
    /// </summary>
    internal static string CtexPathFromSidecar(string? importSidecar)
        => Sts2SpineGeoClipAtlas.ExtractImportedResourcePath(importSidecar ?? string.Empty, ".ctex");

    /// <summary>
    /// Whether <paramref name="ctexPath"/> is the imported form of <paramref name="sourcePath"/> — Godot names an
    /// imported file <c>&lt;source file name&gt;-&lt;hash&gt;.ctex</c>, so the source's own file name is a prefix
    /// of the ctex's. The <see cref="Sts2SpineGeoClipPageSource.Mode.Strict"/>-flavoured half of this reader: it
    /// is what stops a mis-parsed sidecar from handing back some OTHER texture's container.
    /// </summary>
    internal static bool CtexNamesSource(string? ctexPath, string? sourcePath)
    {
        var ctexName = Sts2SpineGeoClipPageSource.FileName(ctexPath);
        var sourceName = Sts2SpineGeoClipPageSource.FileName(sourcePath);
        return ctexName.Length > 0
            && sourceName.Length > 0
            && ctexName.StartsWith(sourceName + "-", StringComparison.OrdinalIgnoreCase)
            && ctexName.EndsWith(".ctex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The embedded WebP of a <c>GST2</c> container, when every clause agrees it describes this page.
    /// </summary>
    /// <param name="textureWidth">What the RUNTIME texture reports; zero/negative is "not known", which refuses.</param>
    /// <param name="atlasWidth">What the atlas TEXT declares, or 0 when it declares nothing.</param>
    internal static CtexDecision Extract(
        byte[]? ctexBytes,
        int textureWidth,
        int textureHeight,
        int atlasWidth,
        int atlasHeight)
    {
        if (ctexBytes is null || ctexBytes.Length == 0)
        {
            return new CtexDecision(null, "ctex-unreadable");
        }

        if (ctexBytes.Length < HeaderLength)
        {
            return new CtexDecision(null, $"ctex-too-short:{ctexBytes.Length}");
        }

        if (ctexBytes[0] != 'G' || ctexBytes[1] != 'S' || ctexBytes[2] != 'T' || ctexBytes[3] != '2')
        {
            return new CtexDecision(null, "ctex-magic");
        }

        var version = ReadUInt32(ctexBytes, 4);
        if (version != SupportedContainerVersion)
        {
            // A later container version may lay the payload out differently, and reading it as if it did not is
            // exactly how a wrong page gets published.
            return new CtexDecision(null, $"ctex-version:{version}");
        }

        var containerWidth = ReadUInt32(ctexBytes, 8);
        var containerHeight = ReadUInt32(ctexBytes, 12);

        // Byte 36 onward is Godot's per-image header, which is where the payload's own description lives.
        var dataFormat = ReadUInt32(ctexBytes, 36);
        if (dataFormat != DataFormatWebp)
        {
            // 0 is a raw image — which is what a VRAM-compressed (BPTC/S3TC) page imports to, and there is
            // nothing a browser could do with those bytes.
            return new CtexDecision(null, $"ctex-data-format:{dataFormat}");
        }

        var width = ReadUInt16(ctexBytes, 40);
        var height = ReadUInt16(ctexBytes, 42);
        var mipmaps = ReadUInt32(ctexBytes, 44);
        if (mipmaps != 0)
        {
            // Level 0 IS the first payload and would be extractable, but a mipmapped container is not a shape
            // this reader has ever seen on an atlas page, so it declines rather than guesses.
            return new CtexDecision(null, $"ctex-mipmaps:{mipmaps}");
        }

        var imageFormat = ReadUInt32(ctexBytes, 48);
        if (imageFormat != ImageFormatRgba8)
        {
            // Godot CONVERTS the decoded payload to the stored format. For RGBA8 that is a no-op and the browser
            // sees exactly what the game does; for anything else (L8, RGB8) the two could disagree about a
            // channel, and a page is not worth that.
            return new CtexDecision(null, $"ctex-image-format:{imageFormat}");
        }

        if (containerWidth != width || containerHeight != height)
        {
            // The container's own two descriptions of the same image must agree. They do not for a VRAM import,
            // whose payload is padded up to the block size.
            return new CtexDecision(
                null,
                $"ctex-header-size-mismatch:{containerWidth}x{containerHeight}-vs-{width}x{height}");
        }

        var payloadSize = ReadUInt32(ctexBytes, 52);
        if (payloadSize == 0)
        {
            return new CtexDecision(null, "ctex-payload-empty");
        }

        if ((long)HeaderLength + payloadSize > ctexBytes.Length)
        {
            return new CtexDecision(
                null,
                $"ctex-payload-truncated:{(long)HeaderLength + payloadSize}-vs-{ctexBytes.Length}");
        }

        var payload = new byte[payloadSize];
        Array.Copy(ctexBytes, HeaderLength, payload, 0, (int)payloadSize);
        if (payload.Length < 12
            || payload[0] != 'R' || payload[1] != 'I' || payload[2] != 'F' || payload[3] != 'F'
            || payload[8] != 'W' || payload[9] != 'E' || payload[10] != 'B' || payload[11] != 'P')
        {
            return new CtexDecision(null, "ctex-payload-not-webp");
        }

        // The RIFF container states its own length. Checking it against the container's makes a truncated or
        // over-long payload a refusal instead of a half-image, WITHOUT decoding anything.
        var riff = ReadUInt32(payload, 4);
        if (riff + 8 != payloadSize)
        {
            return new CtexDecision(null, $"ctex-riff-size-mismatch:{riff + 8}-vs-{payloadSize}");
        }

        if (textureWidth <= 0 || textureHeight <= 0)
        {
            return new CtexDecision(null, "texture-size-unknown");
        }

        if (width != textureWidth || height != textureHeight)
        {
            return new CtexDecision(
                null,
                $"ctex-texture-size-mismatch:{width}x{height}-vs-{textureWidth}x{textureHeight}");
        }

        if (atlasWidth > 0 && atlasHeight > 0 && (width != atlasWidth || height != atlasHeight))
        {
            return new CtexDecision(
                null,
                $"ctex-atlas-size-mismatch:{width}x{height}-vs-{atlasWidth}x{atlasHeight}");
        }

        return new CtexDecision(new CtexPayload(payload, (int)width, (int)height), "ok");
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static uint ReadUInt16(byte[] bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8));
}

// ── Mesh readback: the colour checksum, in the two shapes the two read paths hold colours in ─────────

/// <summary>
/// The association probe's moved/not-moved signal: a position-weighted sum over a surface's vertex colours, so
/// a permutation of the same colours is not mistaken for no change.
///
/// <para>TWO SHAPES, ONE ARITHMETIC. The full mesh read (PASS 3's, which needs vertices, UVs and indices) holds
/// colours as one <c>float[4]</c> per vertex; the association's colour-only read walks the rendering server's
/// colour array directly and allocates nothing per vertex. Both must produce the SAME double, bit for bit, or
/// the association changes and so does the artifact — so both route through <see cref="Accumulate"/> and a test
/// pins that the two shapes agree over random data.</para>
/// </summary>
internal static class Sts2SpineGeoClipColorChecksum
{
    /// <summary>
    /// One vertex's contribution, in the exact expression order the original single-shape loop used:
    /// <c>sum += (i + 1) * (c + 3) * color[c]</c> for c = 0..3, each product taken in FLOAT and each addition in
    /// double. Written out rather than looped so neither caller can accidentally re-associate it.
    /// </summary>
    internal static double Accumulate(double sum, int vertexIndex, float r, float g, float b, float a)
        => sum
            + ((vertexIndex + 1) * 3 * r)
            + ((vertexIndex + 1) * 4 * g)
            + ((vertexIndex + 1) * 5 * b)
            + ((vertexIndex + 1) * 6 * a);

    /// <summary>The full read's shape: one <c>float[]</c> per vertex, of any length.</summary>
    internal static double FromJagged(IReadOnlyList<float[]>? colors)
    {
        double sum = 0;
        if (colors is null)
        {
            return sum;
        }

        for (var i = 0; i < colors.Count; i += 1)
        {
            var color = colors[i];
            for (var c = 0; c < color.Length; c += 1)
            {
                sum += (i + 1) * (c + 3) * color[c];
            }
        }

        return sum;
    }

    /// <summary>The colour-only read's shape: R,G,B,A interleaved, four floats per vertex.</summary>
    internal static double FromRgba(ReadOnlySpan<float> rgba)
    {
        double sum = 0;
        var vertices = rgba.Length / 4;
        for (var i = 0; i < vertices; i += 1)
        {
            var b = i * 4;
            sum = Accumulate(sum, i, rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
        }

        return sum;
    }

    /// <summary>
    /// One vertex's contribution to ALL FIVE sums at once: the combined checksum above, plus one
    /// position-weighted sum per colour component.
    /// </summary>
    /// <remarks>
    /// <see cref="GeoClipColorSums.Combined"/> is produced by calling <see cref="Accumulate"/> itself, so it
    /// stays bit-identical to the single-sum path by CONSTRUCTION rather than by a re-derivation that could
    /// drift. The per-component sums carry the same <c>(i + 1)</c> position weight and drop the channel weight,
    /// which is now redundant — the channel is the sum's identity.
    /// </remarks>
    internal static GeoClipColorSums AccumulateSums(
        GeoClipColorSums sums,
        int vertexIndex,
        float r,
        float g,
        float b,
        float a)
        => new(
            Accumulate(sums.Combined, vertexIndex, r, g, b, a),
            sums.R + ((vertexIndex + 1) * r),
            sums.G + ((vertexIndex + 1) * g),
            sums.B + ((vertexIndex + 1) * b),
            sums.A + ((vertexIndex + 1) * a));

    /// <summary>The colour-only read's shape again, this time yielding every sum the probe can read a code out of.</summary>
    internal static GeoClipColorSums SumsFromRgba(ReadOnlySpan<float> rgba)
    {
        var sums = GeoClipColorSums.Zero;
        var vertices = rgba.Length / 4;
        for (var i = 0; i < vertices; i += 1)
        {
            var b = i * 4;
            sums = AccumulateSums(sums, i, rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
        }

        return sums;
    }
}

/// <summary>
/// What ONE colour readback yields: the combined checksum the alpha-only probe has always compared, and a
/// per-component sum so a probe that nudges several components at once can tell WHICH of them answered.
/// </summary>
/// <remarks>
/// <para>THE POINT. A readback is a <c>RenderingDevice::buffer_get_data</c> that flushes and stalls the device
/// — ~0.26 ms on the merchant's meshes, 19 of 30 of which have four vertices, so the cost is per-CALL. Five
/// sums out of one call cost four extra multiply-adds per vertex and buy up to four INDEPENDENT probe bits per
/// awaited frame instead of one, which divides both the reads and the frames by the channel count.</para>
/// <para>An unread surface is <see cref="NotRead"/> — every sum NaN — because the probe's moved/not-moved
/// predicate treats a NaN/not-NaN transition as movement, and that has to keep working per channel.</para>
/// </remarks>
internal readonly record struct GeoClipColorSums(double Combined, double R, double G, double B, double A)
{
    /// <summary>How many distinct sums one read yields; the index space of <see cref="this[int]"/>.</summary>
    internal const int Count = 5;

    /// <summary>Index of the combined checksum — the one the alpha-only probe has always used.</summary>
    internal const int CombinedIndex = 0;

    internal static GeoClipColorSums Zero { get; } = new(0, 0, 0, 0, 0);

    internal static GeoClipColorSums NotRead { get; } =
        new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

    /// <summary>
    /// The sums by index: 0 combined, then 1..4 for R, G, B, A. Out of range answers NaN, which the
    /// moved/not-moved predicate reads as "unreadable" rather than as a value.
    /// </summary>
    internal double this[int sum]
        => sum switch
        {
            CombinedIndex => Combined,
            1 => R,
            2 => G,
            3 => B,
            4 => A,
            _ => double.NaN,
        };
}

/// <summary>
/// What a page's BYTES are called, independently of which bake wrote them.
/// </summary>
/// <remarks>
/// The lowercase SHA-256 hex of the page file, carried in the manifest as <c>pages[].sha256</c>. It exists so a
/// caller that already holds a page can say so BEFORE the bake pays to produce it, and so an adopting store can
/// name the page without re-hashing bytes it was handed. couch-coop's shared page folder takes the first 16 hex
/// characters of exactly this string (<c>CouchCoopGeoclipStore.PageFileNameFor</c>); the full digest is emitted
/// because truncating is the consumer's policy, not the producer's.
/// </remarks>
internal static class Sts2SpineGeoClipPageId
{
    internal static string ContentId(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}

// ── Baking a RIG: N animations, ONE scene load ───────────────────────────────────────────────────────

/// <summary>
/// The pure decisions behind baking every requested animation of one (scene, node) in a single scene load.
///
/// <para>WHY IT IS WORTH DOING. A pose-only bake is 90-96 % per-SCENE work — the bracket RID sweep and the
/// slot↔mesh association — and N poses of one rig pay it N times because the baker starts from
/// <c>TryLoadSceneAndFindNode</c> per target. Slot→mesh is a property of the RIG, so one sweep and one
/// association can serve every pose, leaving only PASS 1 + PASS 3 + the manifest write per pose.</para>
///
/// <para>WHY IT IS GUARDED. "Slot→mesh is a property of the rig" is only true while a slot keeps drawing the
/// SAME attachment. Two things can break it and they are detected differently:
/// <list type="bullet">
///   <item>Re-setting an animation can re-mint mesh RIDs. The baker already counts that as
///   <c>staleMeshFrames</c> — an ASSOCIATED mesh reading back empty — and any non-zero count sends the whole rig
///   to per-target bakes. That check is a MEASUREMENT of the live rig, not an assumption about it.</item>
///   <item>A slot that shows attachment A at the pose association was measured at and attachment B at another
///   pose may be drawn by a different mesh there, and the stale counter only sees that when the old mesh reads
///   back EMPTY rather than stale-but-populated. So attachment identity is compared directly, from PASS 1 data
///   that is already captured, and a pose that drifts is baked per-target instead
///   (<see cref="AttachmentDrift"/>).</item>
/// </list>
/// Both are conservative in the same direction: the fallback costs the status quo, a wrong association bakes one
/// creature's art onto another slot and the store caches it for ever.</para>
/// </summary>
internal static class Sts2SpineGeoClipBatch
{
    /// <summary>
    /// The animations one rig bake covers: <paramref name="primary"/> first, then <paramref name="others"/>,
    /// trimmed, blanks dropped, duplicates removed, ORDER PRESERVED.
    /// </summary>
    /// <remarks>
    /// The primary is always included and always first, so a request that repeats it in the list (the obvious way
    /// to write "bake these N") means the same thing as one that does not. Order is preserved because it is the
    /// order results come back in and the order a reader sees in the log.
    /// </remarks>
    internal static IReadOnlyList<string> NormalizeAnimations(string? primary, IEnumerable<string>? others)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? candidate)
        {
            var name = (candidate ?? string.Empty).Trim();
            if (name.Length > 0 && seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        Add(primary);
        foreach (var other in others ?? [])
        {
            Add(other);
        }

        return ordered;
    }

    /// <summary>
    /// Which sampled pose the mesh RIDs are acquired at: the one showing the MOST slots with an attachment, ties
    /// going to the earliest. A slot whose mesh is empty right now has no surface to validate, so acquiring
    /// anywhere else loses those slots outright — the same rule a single-animation bake already applies across
    /// its own frames, widened to every pose in the batch.
    /// </summary>
    internal static int ChooseAcquisitionStop(IReadOnlyList<int> visibleCounts)
    {
        var best = 0;
        var bestVisible = -1;
        for (var i = 0; i < visibleCounts.Count; i += 1)
        {
            if (visibleCounts[i] > bestVisible)
            {
                bestVisible = visibleCounts[i];
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The order the association pass VISITS sampled poses in, which decides where each slot is probed (a slot is
    /// probed at the first stop in this order that shows it).
    /// </summary>
    /// <remarks>
    /// <para>A SINGLE-ANIMATION bake keeps the natural frame order, byte for byte. That is not tidiness: the
    /// round's acceptance criterion is that association PAIRS are identical to the pre-change bake, and moving
    /// which frame a slot is probed at can change which mesh moves.</para>
    /// <para>A BATCH puts the acquisition stop first, so the pose that shows the most slots claims them in ONE
    /// probe group and the remaining stops only pay for whatever they add. On a rig whose widest pose covers
    /// every slot — the common shape — that is exactly one group, i.e. exactly one animation's worth of probe
    /// frames for the whole rig.</para>
    /// </remarks>
    internal static int[] ProbeOrder(int stopCount, int acquisitionStop, bool batched)
    {
        if (stopCount <= 0)
        {
            return [];
        }

        var natural = Enumerable.Range(0, stopCount).ToArray();
        if (!batched || acquisitionStop <= 0 || acquisitionStop >= stopCount)
        {
            return natural;
        }

        return [acquisitionStop, .. natural.Where(stop => stop != acquisitionStop)];
    }

    /// <summary>
    /// Slot ordinals that show an attachment at BOTH poses but a DIFFERENT one — the slots for which an
    /// association measured at <paramref name="acquisition"/> is not evidence about <paramref name="pose"/>.
    /// </summary>
    /// <remarks>
    /// A slot absent from either map is not drift: a slot that is hidden at one of the two poses contributes no
    /// geometry there, and one that is newly visible is probed at its own stop by the association pass.
    /// </remarks>
    internal static IReadOnlyList<int> AttachmentDrift(
        IReadOnlyDictionary<int, string> acquisition,
        IReadOnlyDictionary<int, string> pose)
    {
        var drifted = new List<int>();
        foreach (var (ordinal, attachment) in pose.OrderBy(pair => pair.Key))
        {
            if (acquisition.TryGetValue(ordinal, out var atAcquisition)
                && !string.Equals(atAcquisition, attachment, StringComparison.Ordinal))
            {
                drifted.Add(ordinal);
            }
        }

        return drifted;
    }

    /// <summary>
    /// Split a spec's targets into rig bakes. Ungrouped, every target is its own bake (the env lane's historical
    /// behaviour, target for target). Grouped, CONSECUTIVE targets sharing a (scene, node) fuse into one.
    /// </summary>
    /// <remarks>
    /// Consecutive rather than global on purpose: a spec's order is the operator's order, and re-sorting it would
    /// silently move which bake runs when — the one thing a spec written to reproduce a crash cannot survive.
    /// </remarks>
    internal static List<List<GeoClipTarget>> GroupTargets(IReadOnlyList<GeoClipTarget> targets, bool group)
    {
        var groups = new List<List<GeoClipTarget>>();
        foreach (var target in targets)
        {
            var last = groups.Count > 0 ? groups[^1] : null;
            if (group
                && last is not null
                && string.Equals(last[0].ScenePath, target.ScenePath, StringComparison.Ordinal)
                && string.Equals(last[0].NodePath ?? string.Empty, target.NodePath ?? string.Empty, StringComparison.Ordinal))
            {
                last.Add(target);
                continue;
            }

            groups.Add([target]);
        }

        return groups;
    }

    /// <summary>Why this rig may not be batched, or null when it may.</summary>
    /// <remarks>
    /// <para>Three independent detectors, reported apart because they mean different things to whoever reads the
    /// log: a stale mesh says the rig RE-MINTS on an animation set, attachment drift says the rig SWAPS what a
    /// slot draws, a foreign mesh says the sweep validated geometry no probed slot answered for. The first is a
    /// property of the runtime, the second of the content, the third of the batch itself.</para>
    /// <para>THE FOREIGN ARM IS ABOUT NOT BEING HARSHER THAN THE PATH IT REPLACES. A rig bake sets EVERY
    /// requested animation before the bracket closes — that is the point, it is where a third of the merchant's
    /// slot meshes come from — so it can validate meshes that belong to a pose nothing probed at, and those come
    /// back UNCLAIMED. `foreignMeshes` is fatal downstream, so a batch that inflated it would refuse poses a
    /// per-target bake accepts, and the coverage loss would look like a property of the rig rather than of the
    /// batching. Whether this actually happens depends on something not measurable offline (whether the runtime
    /// mints a mesh per slot or per slot-and-attachment), so the safe direction is taken by default and
    /// <paramref name="foreignBlocks"/> is the lever that turns it into a one-session A/B.</para>
    /// <para>THAT PREMISE HAS SINCE WEAKENED, and this arm has deliberately NOT been changed with it. Leftovers
    /// are no longer fatal downstream on their own — see <see cref="Sts2SpineGeoClipOwnership"/>; the admission
    /// rule refuses on an unproven CLAIM beside them — so the coverage loss this guards against is now smaller
    /// than it was. What it costs when it fires is unchanged (N scene loads instead of one), and what it would
    /// cost to relax it is a batching measurement nobody has taken, so relaxing it here on the strength of an
    /// admission change would be trading a known cost for an unmeasured correctness risk. Left as it is,
    /// on purpose.</para>
    /// </remarks>
    internal static string? BatchRefusal(
        int staleMeshFrames,
        int driftedSlots,
        int foreignMeshes = 0,
        bool foreignBlocks = true)
    {
        if (staleMeshFrames > 0)
        {
            return $"staleMeshFrames={staleMeshFrames}";
        }

        if (driftedSlots > 0)
        {
            return $"attachmentDrift={driftedSlots}";
        }

        return foreignBlocks && foreignMeshes > 0 ? $"foreignMeshes={foreignMeshes}" : null;
    }
}

// ── The association PROBE: which mesh is which slot, in O(log slots) awaited frames ──────────────────

/// <summary>
/// How a group of slots is spread across probe frames.
///
/// <para>The measurement itself is unchanged — a slot's colour alpha is nudged and the mesh whose vertex
/// colours move is its mesh — but the SCHEDULE is not. Probing one slot per frame costs one awaited engine
/// frame per slot (44 slots = 88 frames ≈ 1.5 s at 60 fps, which is most of a pose-only bake). Probing a
/// SUBSET per frame and reading a slot's identity out of WHICH frames its mesh moved in costs
/// O(log slots) frames instead.</para>
/// </summary>
internal enum GeoClipProbeCodeScheme
{
    /// <summary>
    /// DEFAULT. Every slot's code has the same number of set bits, so a mesh that responds to TWO slots at
    /// once — it moves in the union of their frames — always lands on a bit count that is not a valid code
    /// and is reported as undecodable instead of being silently attributed to some third slot. Two distinct
    /// sets of equal size always union to a strictly larger set, so this is a property of the code space
    /// rather than a heuristic. Costs ~2 frames more than <see cref="Ordinal"/> on a 44-slot rig.
    /// </summary>
    UnionSafe,

    /// <summary>
    /// The plain binary code: slot at position <c>i</c> gets code <c>i + 1</c> (zero is reserved so "moved in
    /// no frame" stays distinguishable from a slot). Fewest frames, but a mesh driven by two slots decodes to
    /// the OR of their codes, which is frequently another slot's code — a silent mis-pairing. Kept as the
    /// A/B arm behind <c>SPIRECTL_SPINE_GEOCLIP_PROBE_CODES=ordinal</c>.
    /// </summary>
    Ordinal,
}

/// <summary>
/// Which colour components a probe nudges, and therefore how many INDEPENDENT bits one awaited frame carries.
/// </summary>
/// <remarks>
/// <para>A probe position used to be a frame. It is now a (frame, component) pair, because one readback already
/// yields a per-component sum (<see cref="GeoClipColorSums"/>) and the components are driven independently: the
/// vertex colours a Spine slot writes are its slot colour, component for component. That is measured, not
/// assumed — the merchant's one non-white slot sits at <c>(0.61960787, 0.61960787, 0.61960787, 1)</c> and the
/// mesh it drives reads back exactly that, while the other 67 slots and their meshes are white
/// (<c>spine-geometry-phase0-aug27</c>, offline dumps).</para>
/// <para>WHY ALPHA IS NOT IN THE DEFAULT. The probe reads a mesh's own CPU-side vertex-COLOUR array back out of
/// <c>RenderingServer.MeshSurfaceGetArrays</c> — the values the Spine runtime WROTE into the surface, not
/// anything a canvas item does with them afterwards. So the renderer's blend mode is downstream of this
/// measurement and cannot affect it; the only question that matters is whether the runtime premultiplies a
/// slot's colour into that array before writing it.</para>
/// <para>THE MATH IF IT DOES. Premultiplied, the stored components are <c>(r·a, g·a, b·a, a)</c>: an R nudge
/// still moves R alone, but an ALPHA nudge moves all four at once, so alpha could not carry an independent bit
/// alongside R, G and B — every mesh answering on any channel would answer on all of them, the codes would
/// collapse, and the whole group would decode to nothing. That is the failure the default is priced against: the
/// cost of being wrong about alpha is a group with no answers, the cost of leaving it out is one extra awaited
/// frame.</para>
/// <para>WHAT DECIDES IT, PER RIG. spine-godot gates its premultiply path on the ATLAS's own <c>pma</c> flag,
/// which this repo already parses (<c>SpineAtlasPage.PremultipliedAlpha</c>, Sts2SpineGeometryMath.cs
/// ~1230/1351). It is therefore a property of the rig being probed and not of the engine. Census over the shipped
/// content, counted this round: of the 169 <c>.atlas</c> files in the game's pck exactly ONE declares
/// <c>pma:true</c> (the tough_egg monster); every other file omits the field entirely, which is spine's default
/// of false. Neither rig this round is measured against — byrdonis, ironclad_shop — declares it.</para>
/// <para>SO WHY IS RGBA STILL AN ARM. Because "the atlas does not ask for premultiplication" is an argument, not
/// a measurement: nothing has yet run an rgba probe on a live rig and checked that the association PAIRS it
/// produces are the same ones the RGB probe produces. Until that arm is run, <see cref="Rgba"/> stays opt-in
/// behind <c>SPIRECTL_SPINE_GEOCLIP_PROBE_CHANNELS=rgba</c>. If it holds, the honest shape is not a flipped
/// default either — it is a <c>ChooseProbeChannels(settings, atlas)</c> that reads the flag the runtime reads and
/// takes the fourth bit on the 168 rigs that can carry it while leaving the one that cannot on RGB.</para>
/// </remarks>
internal enum GeoClipProbeChannels
{
    /// <summary>
    /// One bit per frame, carried on alpha — the schedule every recorded association pair was produced under,
    /// read out of the COMBINED checksum. The exact legacy arm, and what a group falls back to.
    /// </summary>
    Alpha,

    /// <summary>DEFAULT. Three bits per frame on R, G and B, each read out of its own component sum.</summary>
    Rgb,

    /// <summary>
    /// Four bits per frame. Only sound on a rig whose atlas does not ask for premultiplied alpha, which is all
    /// but one of the shipped ones — but unmeasured on a live rig, so an A/B arm and not the default.
    /// </summary>
    Rgba,
}

/// <summary>
/// One channel of a probe: which colour component carries its nudge, and which of a
/// <see cref="GeoClipColorSums"/>'s sums reads the answer back.
/// </summary>
internal readonly record struct GeoClipProbeChannel(int Component, int Sum);

/// <summary>
/// The per-group probe schedule: <see cref="Codes"/>[i] is the bit set of POSITIONS slot <c>i</c> is nudged in,
/// where a position is a (frame, channel) pair numbered <c>frame * ChannelCount + channel</c>. Codes are
/// assigned per GROUP, not globally — only the slots that first become visible at the same frame are probed
/// together, so a group of one slot needs one frame and not six.
/// </summary>
internal sealed record GeoClipProbePlan(
    GeoClipProbeCodeScheme Scheme,
    int SlotCount,
    int FrameCount,
    IReadOnlyList<int> Codes,
    GeoClipProbeChannels Channels = GeoClipProbeChannels.Alpha)
{
    /// <summary>How many independent bits one awaited frame carries.</summary>
    internal int ChannelCount => Sts2SpineGeoClipProbe.ChannelsOf(Channels).Count;

    /// <summary>The width of the code space actually observable: one bit per (frame, channel).</summary>
    internal int PositionCount => FrameCount * ChannelCount;

    /// <summary>Whether slot <paramref name="slot"/> carries a nudge at probe position <paramref name="position"/>.</summary>
    internal bool IsProbedAt(int slot, int position)
        => slot >= 0
            && slot < Codes.Count
            && position >= 0
            && position < PositionCount
            && (Codes[slot] & (1 << position)) != 0;
}

/// <summary>
/// What the observed per-mesh frame sets say. <see cref="MeshesBySlot"/> is parallel to the plan's slots, and
/// a slot with more than one mesh is exactly the "several meshes moved at once" case the atlas fallback
/// already exists to break — the grouped probe does not change that verdict, it just reaches it in fewer
/// frames.
/// </summary>
internal sealed record GeoClipProbeDecode(
    IReadOnlyList<IReadOnlyList<int>> MeshesBySlot,
    IReadOnlyList<int> MovedButUndecodable,
    int SilentMeshes,
    int MeshesMovedInEveryFrame);

internal static class Sts2SpineGeoClipProbe
{
    /// <summary>
    /// Codes are ints, so a group can span at most this many probe POSITIONS — (frame, channel) pairs. Far
    /// above any real rig (a 30-position plan would encode ~155 million slots under the union-safe scheme); it
    /// exists so the shift below can never be undefined.
    /// </summary>
    internal const int MaxProbePositions = 30;

    private static readonly GeoClipProbeChannel[] AlphaChannels =
        [new(Component: 3, Sum: GeoClipColorSums.CombinedIndex)];

    private static readonly GeoClipProbeChannel[] RgbChannels =
        [new(Component: 0, Sum: 1), new(Component: 1, Sum: 2), new(Component: 2, Sum: 3)];

    private static readonly GeoClipProbeChannel[] RgbaChannels =
        [.. RgbChannels, new GeoClipProbeChannel(Component: 3, Sum: 4)];

    /// <summary>
    /// The channels a scheme drives, in position order. Alpha's single channel reads the COMBINED checksum and
    /// not the A sum, so the legacy arm's moved/not-moved predicate is the one that produced every recorded
    /// association pair, byte for byte.
    /// </summary>
    internal static IReadOnlyList<GeoClipProbeChannel> ChannelsOf(GeoClipProbeChannels channels)
        => channels switch
        {
            GeoClipProbeChannels.Rgb => RgbChannels,
            GeoClipProbeChannels.Rgba => RgbaChannels,
            _ => AlphaChannels,
        };

    /// <summary>
    /// The nudge itself: push a colour component well away from wherever it sits. Unchanged from the
    /// one-at-a-time pass — it lived on alpha there and the rule is the same on any component.
    /// </summary>
    internal static float NudgeValue(float value) => value > 0.5f ? 0.25f : 0.75f;

    /// <summary>One component of an RGBA quadruple nudged; the other three left exactly as they were.</summary>
    internal static (float R, float G, float B, float A) NudgeComponent(
        float r,
        float g,
        float b,
        float a,
        int component)
        => component switch
        {
            0 => (NudgeValue(r), g, b, a),
            1 => (r, NudgeValue(g), b, a),
            2 => (r, g, NudgeValue(b), a),
            _ => (r, g, b, NudgeValue(a)),
        };

    /// <summary>
    /// The bit a (frame, channel) pair occupies in a code. Frames run slowest, so one frame's channels are
    /// adjacent bits and the plan's positions are visited in order.
    /// </summary>
    internal static int PositionOf(int frame, int channel, int channelCount)
        => (frame * channelCount) + channel;

    /// <summary>
    /// The colour a slot wears during one probe frame: its own, with every component the plan drives it on at
    /// that frame pushed away, and every other component untouched.
    /// </summary>
    /// <remarks>
    /// Plain floats rather than a Godot <c>Color</c> so the whole decision — which component, at which frame,
    /// for which slot — is provable offline. The live caller is a two-line adapter over this.
    /// </remarks>
    internal static (float R, float G, float B, float A) ProbeColorFor(
        GeoClipProbePlan plan,
        int slot,
        int frame,
        float r,
        float g,
        float b,
        float a)
    {
        var channels = ChannelsOf(plan.Channels);
        for (var channel = 0; channel < channels.Count; channel += 1)
        {
            if (plan.IsProbedAt(slot, PositionOf(frame, channel, channels.Count)))
            {
                (r, g, b, a) = NudgeComponent(r, g, b, a, channels[channel].Component);
            }
        }

        return (r, g, b, a);
    }

    internal static GeoClipProbePlan PlanProbe(int slotCount, GeoClipProbeCodeScheme scheme)
        => PlanProbe(slotCount, scheme, GeoClipProbeChannels.Alpha);

    internal static GeoClipProbePlan PlanProbe(
        int slotCount,
        GeoClipProbeCodeScheme scheme,
        GeoClipProbeChannels channels)
    {
        if (slotCount <= 0)
        {
            return new GeoClipProbePlan(scheme, 0, 0, [], channels);
        }

        var channelCount = ChannelsOf(channels).Count;
        return scheme == GeoClipProbeCodeScheme.Ordinal
            ? PlanOrdinal(slotCount, channels, channelCount)
            : PlanUnionSafe(slotCount, channels, channelCount);
    }

    /// <summary>
    /// Channels the plan actually DRIVES that no mesh answered on anywhere — a colour component the renderer
    /// does not put through to vertex colours at all.
    /// </summary>
    /// <remarks>
    /// A channel no code uses (a small group whose codes fit in fewer positions than the plan offers) is not
    /// dead, it is unused, and it is not counted. Everything else is: if R is driven and NOTHING moved on any R
    /// position, the R bit of every code was dropped, and that is the one failure that can be mistaken for a
    /// real answer — a mesh driven by two slots loses its R positions from the union and can land back on some
    /// third slot's code. Union-safety does not cover that, so this does.
    /// </remarks>
    internal static int DeadChannels(GeoClipProbePlan plan, IReadOnlyList<int> observedFrameSets)
    {
        var channelCount = plan.ChannelCount;
        if (plan.PositionCount <= 0 || channelCount <= 0)
        {
            return 0;
        }

        var mask = (1 << plan.PositionCount) - 1;
        var driven = 0;
        foreach (var code in plan.Codes)
        {
            driven |= code & mask;
        }

        var answered = 0;
        foreach (var observed in observedFrameSets)
        {
            answered |= observed & mask;
        }

        var dead = 0;
        for (var channel = 0; channel < channelCount; channel += 1)
        {
            var positions = 0;
            for (var frame = 0; frame < plan.FrameCount; frame += 1)
            {
                positions |= 1 << PositionOf(frame, channel, channelCount);
            }

            if ((driven & positions) != 0 && (answered & positions) == 0)
            {
                dead += 1;
            }
        }

        return dead;
    }

    /// <summary>
    /// Whether a group that decoded under a multi-channel plan should be re-probed under the legacy alpha one.
    /// </summary>
    /// <remarks>
    /// <para>WHY A SINGLE SLOT'S FAILURE IS NOT A CORRECTNESS PROBLEM. Under the union-safe code space every
    /// valid code has the same popcount, so a slot whose nudge fails to register on SOME channel yields a mesh
    /// whose observed position set is a strict subset of its code — popcount below the weight, therefore not a
    /// code, therefore undecodable, therefore the slot is starved. A dropped channel can no more be mistaken
    /// for another slot than a union of two codes can.</para>
    /// <para>WHERE THE LINE IS ON COST. The repair pass already re-measures a starved slot the old way, at two
    /// awaited frames each, and it is exact. So while <c>2 · starved</c> is cheaper than re-running the whole
    /// group on alpha, let the repair do it. Past that — or past the repair's own cap, where the surplus slots
    /// would fall through to the atlas fallback and the PAIRS could actually move — re-probe the group instead,
    /// which costs what the bake cost before this lever existed and answers exactly what it answered.</para>
    /// <para>WHERE THE LINE IS ON CORRECTNESS. A WHOLE dead channel is different in kind and is not waited on:
    /// every code loses the same positions, so a mesh two slots drive can land on a third slot's code and be
    /// PAIRED to it. That case re-probes immediately, whatever the starved count says.</para>
    /// </remarks>
    internal static bool ShouldReprobeUnderAlpha(
        GeoClipProbePlan plan,
        IReadOnlyList<int> observedFrameSets,
        int starvedSlots,
        int maxRepairSlots)
    {
        if (plan.Channels == GeoClipProbeChannels.Alpha || plan.SlotCount <= 0)
        {
            return false;
        }

        if (DeadChannels(plan, observedFrameSets) > 0)
        {
            return true;
        }

        if (starvedSlots <= 0)
        {
            return false;
        }

        // The re-probe costs its own frames plus the one that restores the rig afterwards.
        //
        // The cap clause is DOMINATED at the shipped constants and is kept for the invariant, not the arithmetic:
        // an alpha plan can never span more than MaxProbePositions frames, so the cost clause already fires by
        // 16 starved slots, one below DefaultMaxRepairSlots + 1. Only a larger repair cap or a wider position
        // bound could make it the binding one — which is exactly when losing it would let surplus slots fall
        // through to the atlas fallback and move the pairs.
        var alphaFrames = PlanProbe(plan.SlotCount, plan.Scheme, GeoClipProbeChannels.Alpha).FrameCount + 1;
        return starvedSlots > maxRepairSlots || (2 * starvedSlots) > alphaFrames;
    }

    /// <summary>
    /// Which of the swept meshes a group's probe reads back. ALL of them, every group, whatever earlier groups
    /// have already claimed — <paramref name="claimed"/> is taken only so that narrowing the read set by it is
    /// a deliberate edit to this one line rather than an invisible one at the call site.
    /// </summary>
    /// <remarks>
    /// <para>THE OPTIMISATION THIS EXISTS TO REFUSE. Re-reading a mesh some earlier group already paired looks
    /// like pure waste: a readback is a device stall (~0.26 ms per mesh), it happens once per mesh per probe
    /// FRAME, and the answer for a claimed mesh cannot change the pairing it already has. Filtering
    /// <paramref name="claimed"/> out of the read set would be the obvious saving, and it is wrong.</para>
    /// <para>WHY. Take a mesh M that two slots drive at once — S1 in an earlier group, which claimed it, and S2
    /// in a later one. Read unfiltered, S2's group sees M move: under the union-safe code space M's observed
    /// positions are a union of two codes, never a code, so M is reported MOVED-BUT-UNDECODABLE. That is the one
    /// signal <see cref="ShouldRepairOneAtATime"/> waits for, and it is what buys S2 an exact one-at-a-time
    /// re-measurement — or, when the repair does resolve S2 onto M, the named refusal that says S2 moved a mesh
    /// another slot already claimed. Either way a human is told a true thing.</para>
    /// <para>Filter M out and that whole signal disappears. S2's group now observes NOTHING moving, so there are
    /// zero undecodable meshes; the repair predicate is false and never fires; S2 is simply starved and falls
    /// through to the atlas-region fallback, whose candidate pool is exactly the meshes M is NOT in. The fallback
    /// then pairs S2 with whichever unclaimed mesh scores best — a SILENT WRONG PAIRING where the unfiltered read
    /// produced a correct refusal. The saving is a device stall; the cost is a wrong answer nothing downstream
    /// can detect, so the read set stays whole.</para>
    /// </remarks>
    internal static IReadOnlyList<ulong> MeshesToRead(IReadOnlyList<ulong> allMeshes, IReadOnlySet<ulong> claimed)
    {
        _ = claimed;
        return allMeshes;
    }

    // ── The ATLAS-FIRST arm's read set, and the widen rule that licenses it ──────────────────────────

    /// <summary>
    /// The read set for a probe group under the ARMED atlas-first arm: the unclaimed meshes ONLY.
    /// </summary>
    /// <remarks>
    /// <para>THIS IS THE OPTIMISATION <see cref="MeshesToRead"/> REFUSES, and it is a separate function rather
    /// than a flag on that one so the refusal stays legible: the unarmed path cannot reach this code, and the
    /// argument for filtering has to be made here, on its own, where it can be read against the argument
    /// against it.</para>
    /// <para>WHY IT IS ALLOWED HERE — AND ONLY HERE. The hazard is unchanged: filter out a mesh some other slot
    /// claimed and a group that would have reported MOVED-BUT-UNDECODABLE (a correct refusal) instead sees
    /// nothing move, starves, and falls through to a fallback whose candidate pool no longer contains the mesh
    /// it actually drives — a silent wrong pairing. The licence is not that the hazard went away; it is that
    /// the armed caller must WIDEN. Every anomaly the filter could have manufactured presents as one of
    /// <see cref="AtlasFirstWidenReason"/>'s three shapes, and the caller answers each of them by re-running the
    /// same group once on <see cref="MeshesToRead"/>'s whole set, which costs exactly what the unarmed path
    /// pays and answers exactly what it answers. Without that rerun this function is simply the wrong answer;
    /// with it, the filter is a fast path with an exact fallback behind it.</para>
    /// <para>The order of <paramref name="allMeshes"/> is preserved, because a decode's mesh indices are indices
    /// into the list this returns and the caller maps them back through it.</para>
    /// </remarks>
    internal static IReadOnlyList<ulong> MeshesToReadUnderAtlasFirst(
        IReadOnlyList<ulong> allMeshes,
        IReadOnlySet<ulong> claimed)
    {
        if (claimed.Count == 0)
        {
            return allMeshes;
        }

        var kept = new List<ulong>(allMeshes.Count);
        foreach (var rid in allMeshes)
        {
            if (!claimed.Contains(rid))
            {
                kept.Add(rid);
            }
        }

        return kept;
    }

    /// <summary>A group starved a slot under the narrowed read set: the mesh it drives may be one that was
    /// filtered out.</summary>
    internal const string WidenStarvedSlot = "a slot moved no mesh";

    /// <summary>A mesh in the narrowed set moved on no valid code, so several slots drive it and the group's
    /// answer is only trustworthy against the whole set.</summary>
    internal const string WidenUndecodableMesh = "a mesh moved on no valid code";

    /// <summary>A decode landed on a mesh the atlas-first pass had already given to a different ordinal.</summary>
    internal const string WidenAtlasFirstConflict = "a decode contradicts an atlas-first claim";

    /// <summary>
    /// Whether a group probed under <see cref="MeshesToReadUnderAtlasFirst"/> must be re-probed once on the
    /// whole swept read set — the widen rule, and the entire licence for narrowing the read set at all.
    /// </summary>
    /// <remarks>
    /// <para>Returns the REASON to log, or null when the narrowed probe's answer is trustworthy on its own.</para>
    /// <para>THE THREE SHAPES. (1) A starved slot is the one the hazard actually produces: the mesh a slot
    /// drives was claimed by someone else and filtered away, so nothing moved for it. (2) A moved-but-
    /// undecodable mesh says several slots drive one mesh, which is the case whose correct handling depends on
    /// seeing every mesh those slots touch. (3) A decode that lands on a mesh atlas-first already gave to
    /// another ordinal contradicts the claim the filter was derived from, so the filter itself is suspect.
    /// Shape (3) cannot fire while the filter is exactly "drop the claimed" — a claimed mesh is not in the read
    /// set to be decoded — and is checked rather than asserted, because it is the shape a LOOSER filter would
    /// produce and this is where such a filter would first be wrong.</para>
    /// <para>Not narrowed, not widened: when the read set was already whole there is nothing a rerun could add,
    /// and re-running would double a group's frames for an identical answer.</para>
    /// </remarks>
    /// <param name="readSetNarrowed">Whether this group's probe actually read fewer meshes than the sweep found.</param>
    /// <param name="starvedSlots">Group members whose decode produced no mesh.</param>
    /// <param name="movedButUndecodableMeshes">Meshes that moved on a frame set matching no code.</param>
    /// <param name="decodesConflictingWithAtlasFirst">
    /// Decoded (slot, mesh) pairs whose mesh atlas-first had already resolved onto a DIFFERENT ordinal.
    /// </param>
    internal static string? AtlasFirstWidenReason(
        bool readSetNarrowed,
        int starvedSlots,
        int movedButUndecodableMeshes,
        int decodesConflictingWithAtlasFirst)
    {
        if (!readSetNarrowed)
        {
            return null;
        }

        if (starvedSlots > 0)
        {
            return WidenStarvedSlot;
        }

        if (movedButUndecodableMeshes > 0)
        {
            return WidenUndecodableMesh;
        }

        return decodesConflictingWithAtlasFirst > 0 ? WidenAtlasFirstConflict : null;
    }

    /// <summary>The awaited frames a probe group's seek costs when it has to be paid.</summary>
    internal const int DefaultProbeSeekFrames = 2;

    /// <summary>
    /// How many engine frames to await after seeking to a probe group's stop — <c>0</c> when the rig is ALREADY
    /// settled at that stop and the seek would write the pose it is already in.
    /// </summary>
    /// <remarks>
    /// <para>A stop identifies (animation, track time) exactly, so equal stop indices mean an identical pose;
    /// re-writing the same track time on the same lane is a no-op, and awaiting frames after a no-op can only
    /// produce the pose already on screen. The seek does nothing else: brackets, bounds and the sweep are all
    /// finished before the association runs, and the mesh RIDs it reads back were acquired at this very pose.</para>
    /// <para>WHAT MAKES IT NON-VACUOUS. The rig bake re-seeks to the ACQUISITION stop before sweeping, and the
    /// probe order puts the acquisition stop first, so the first group's seek is exactly this redundant write —
    /// two awaited frames, every rig bake, for a pose the rig is already holding.</para>
    /// <para><paramref name="settledStop"/> is a claim the caller makes and must be able to keep: negative means
    /// "unknown", which always pays. A caller that cannot prove the rig is settled passes -1 and nothing
    /// changes.</para>
    /// </remarks>
    internal static int ProbeSeekFramesNeeded(
        int settledStop,
        int groupStop,
        int seekFrames = DefaultProbeSeekFrames)
    {
        if (seekFrames <= 0)
        {
            return 0;
        }

        return settledStop >= 0 && settledStop == groupStop ? 0 : seekFrames;
    }

    /// <summary>
    /// Whether a decoded group should pay for the exact one-at-a-time re-measurement of its starved slots.
    /// </summary>
    /// <remarks>
    /// Both counts, not either. A starved slot on its own is the ordinary case — a slot whose mesh the sweep
    /// never validated, or whose nudge the animation's own colour timeline overwrote — and re-probing it one at
    /// a time would cost two awaited frames to learn the same nothing. What makes the repair worth its frames is
    /// a mesh that MOVED on no valid code: under the union-safe space that can only be a mesh several slots drive
    /// at once, so some starved slot's answer really is in the group and really is recoverable. Dropping the
    /// undecodable clause spends frames on every ordinary starvation; dropping the starved clause repairs slots
    /// that already have an answer. See <see cref="MeshesToRead"/> for why the undecodable count is only nonzero
    /// when the read set is whole.
    /// </remarks>
    internal static bool ShouldRepairOneAtATime(
        bool repairEnabled,
        int movedButUndecodableMeshes,
        int starvedSlots)
        => repairEnabled && movedButUndecodableMeshes > 0 && starvedSlots > 0;

    /// <summary>
    /// Read each mesh's slot out of the POSITIONS it moved in. A mesh whose position set is not one of the
    /// plan's codes belongs to no slot as far as this pass is concerned — including the mesh that moved in
    /// EVERY position, which is responding to something other than a slot colour, unless the all-positions code
    /// genuinely exists in this group (a one-slot group, where "moved in the only position" is the right
    /// answer).
    /// </summary>
    internal static GeoClipProbeDecode Decode(GeoClipProbePlan plan, IReadOnlyList<int> observedFrameSets)
    {
        var slotByCode = new Dictionary<int, int>(plan.SlotCount);
        for (var slot = 0; slot < plan.Codes.Count; slot += 1)
        {
            // TryAdd, not [], so a caller that handed in a duplicated code cannot make two slots claim the
            // same mesh; the first slot wins and the second decodes to nothing.
            slotByCode.TryAdd(plan.Codes[slot], slot);
        }

        var meshesBySlot = new List<int>[Math.Max(0, plan.SlotCount)];
        for (var slot = 0; slot < meshesBySlot.Length; slot += 1)
        {
            meshesBySlot[slot] = [];
        }

        var allFrames = plan.PositionCount <= 0 ? 0 : (1 << plan.PositionCount) - 1;
        var undecodable = new List<int>();
        var silent = 0;
        var everyFrame = 0;
        for (var mesh = 0; mesh < observedFrameSets.Count; mesh += 1)
        {
            var frameSet = observedFrameSets[mesh] & allFrames;
            if (frameSet == 0)
            {
                silent += 1;
                continue;
            }

            if (frameSet == allFrames)
            {
                everyFrame += 1;
            }

            if (slotByCode.TryGetValue(frameSet, out var slot))
            {
                meshesBySlot[slot].Add(mesh);
            }
            else
            {
                undecodable.Add(mesh);
            }
        }

        return new GeoClipProbeDecode(meshesBySlot, undecodable, silent, everyFrame);
    }

    // Codes 1..n. `n + 1` because code 0 is reserved for "moved in no position at all".
    private static GeoClipProbePlan PlanOrdinal(int slotCount, GeoClipProbeChannels channels, int channelCount)
    {
        var positions = 0;
        while (positions < MaxProbePositions && (1L << positions) <= slotCount)
        {
            positions += 1;
        }

        var codes = new int[slotCount];
        for (var slot = 0; slot < slotCount; slot += 1)
        {
            codes[slot] = slot + 1;
        }

        return new GeoClipProbePlan(
            GeoClipProbeCodeScheme.Ordinal,
            slotCount,
            FramesFor(positions, channelCount),
            codes,
            channels);
    }

    // The first `slotCount` weight-w subsets of `positions` bits, in increasing numeric order. `positions` is
    // the smallest width that can hold them at ANY weight (its widest layer is C(m, m/2)), and `w` the smallest
    // weight at that width that fits — a smaller weight means fewer slots nudged per frame.
    private static GeoClipProbePlan PlanUnionSafe(int slotCount, GeoClipProbeChannels channels, int channelCount)
    {
        var positions = 1;
        while (positions < MaxProbePositions && Binomial(positions, positions / 2) < slotCount)
        {
            positions += 1;
        }

        // Rounding the width UP to a whole number of frames costs nothing — those positions are carried by
        // components that are already being read back — and it can only lower the weight, which nudges fewer
        // slots per frame.
        var frames = FramesFor(positions, channelCount);
        positions = frames * channelCount;

        var weight = 1;
        while (weight < positions && Binomial(positions, weight) < slotCount)
        {
            weight += 1;
        }

        var codes = new int[slotCount];
        var code = (1 << weight) - 1;
        for (var slot = 0; slot < slotCount; slot += 1)
        {
            codes[slot] = code;
            code = NextWithSamePopCount(code);
        }

        return new GeoClipProbePlan(GeoClipProbeCodeScheme.UnionSafe, slotCount, frames, codes, channels);
    }

    // How many awaited frames carry `positions` code bits, `channelCount` of them per frame. Capped so the
    // rounded-up width can never exceed the shift bound the codes are ints for.
    private static int FramesFor(int positions, int channelCount)
        => Math.Min(
            (positions + channelCount - 1) / channelCount,
            MaxProbePositions / channelCount);

    // Gosper's hack: the next larger integer with the same number of set bits. Enumerating the weight-w layer
    // this way is O(1) per code, so a plan costs nothing even on a rig with hundreds of slots.
    private static int NextWithSamePopCount(int value)
    {
        var lowest = value & -value;
        var ripple = value + lowest;
        return lowest == 0 ? value : (((ripple ^ value) >> 2) / lowest) | ripple;
    }

    // C(n, k) for 0 <= k <= n <= MaxProbePositions. Each step's running value is itself a binomial coefficient,
    // so the integer division is exact, and the largest value reachable inside the bound is C(30, 15) ≈ 1.6e8
    // — no saturation guard is needed and none is here to rot untested.
    private static long Binomial(int n, int k)
    {
        long result = 1;
        for (var i = 1; i <= k; i += 1)
        {
            result = result * (n - k + i) / i;
        }

        return result;
    }
}

/// <summary>
/// The two knobs on the grouped probe, read from the environment once per bake. Both exist so the change can
/// be A/B'd against the old answer on a live rig without a rebuild; neither is meant to be set in production.
/// </summary>
internal sealed record GeoClipProbeSettings(
    GeoClipProbeCodeScheme Scheme,
    bool RepairEnabled,
    int MaxRepairSlots,
    GeoClipProbeChannels Channels = GeoClipProbeChannels.Rgb,
    bool AtlasFirstArmed = true)
{
    internal const string SchemeEnv = "SPIRECTL_SPINE_GEOCLIP_PROBE_CODES";
    internal const string RepairEnv = "SPIRECTL_SPINE_GEOCLIP_PROBE_REPAIR";

    /// <summary>
    /// Whether the ATLAS-FIRST association runs as the DISCOVERY arm instead of as a fallback behind the colour
    /// probe. DEFAULT ARMED, on the kill-switch reading <see cref="RepairEnv"/> beside it already takes:
    /// <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> disables it, anything else — absent, empty or misspelt —
    /// arms it.
    ///
    /// <para>Armed on measurement, not on the hypothesis this knob was added to test. Four live arms produced
    /// <c>parts</c> and page pixels identical to BOTH earlier references on both rigs, with zero widen events
    /// and the merchant's bake 38 ms and byrdonis's 157 ms faster at the median while the raster control lane
    /// stayed flat. What licenses defaulting it, though, is the UNARMED evidence: over two legs the shadow arm
    /// disagreed with the measured association zero times on every unarmed run. An armed run agreeing with
    /// itself proves nothing, so no armed agreement is counted here.</para>
    ///
    /// <para>The off-word path is not a new mode — it IS the old default. Spelling it restores exactly the
    /// pre-flip behaviour (the colour probe discovers, the atlas shadows and is only reported), which is why
    /// nothing on that path was touched to land this.</para>
    ///
    /// <para>It used to read the other way round, on the argument that a knob choosing WHICH algorithm
    /// discovers the pairing should read a typo as "keep the measured one". The affirmative spellings still
    /// arm it, so every recorded measurement arm means what it did; the argument is answered rather than
    /// dropped, because the two algorithms have now been shown to agree.</para>
    /// </summary>
    internal const string AtlasFirstEnv = "SPIRECTL_SPINE_GEOCLIP_ASSOC_ATLAS_FIRST";

    /// <summary>
    /// Which colour components carry the probe code: <c>alpha</c> for the pre-channel schedule, <c>rgb</c>
    /// (default) or <c>rgba</c>. Anything unrecognised means the default, the way the other knobs read.
    /// </summary>
    internal const string ChannelsEnv = "SPIRECTL_SPINE_GEOCLIP_PROBE_CHANNELS";

    /// <summary>
    /// How many slots the one-at-a-time repair probe will re-measure in a group before it gives up. The
    /// repair costs the OLD algorithm's two frames per slot, so this caps the worst case at well under what
    /// the old algorithm charged for the whole group.
    /// </summary>
    internal const int DefaultMaxRepairSlots = 16;

    internal static GeoClipProbeSettings Default { get; } =
        new(GeoClipProbeCodeScheme.UnionSafe, RepairEnabled: true, DefaultMaxRepairSlots);

    internal static GeoClipProbeSettings Parse(
        string? scheme,
        string? repair,
        string? channels = null,
        string? atlasFirst = null)
        => new(
            (scheme ?? string.Empty).Trim().ToLowerInvariant() is "ordinal" or "binary"
                ? GeoClipProbeCodeScheme.Ordinal
                : GeoClipProbeCodeScheme.UnionSafe,
            (repair ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no"),
            DefaultMaxRepairSlots,
            (channels ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "alpha" or "a" or "legacy" => GeoClipProbeChannels.Alpha,
                "rgba" => GeoClipProbeChannels.Rgba,
                _ => GeoClipProbeChannels.Rgb,
            },
            (atlasFirst ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no"));

    internal static GeoClipProbeSettings FromEnvironment()
        => Parse(
            Environment.GetEnvironmentVariable(SchemeEnv),
            Environment.GetEnvironmentVariable(RepairEnv),
            Environment.GetEnvironmentVariable(ChannelsEnv),
            Environment.GetEnvironmentVariable(AtlasFirstEnv));
}

// ── The bake's FRAME BUDGET ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// What the bake is allowed to do to the engine's frame cap while it holds the main thread.
///
/// <para>Every awaited engine frame costs a full frame interval — 16.7 ms on a 60 fps host — and a bake
/// awaits a dozen or more of them. The host is ALREADY committed to a main-thread stall for the length of
/// the bake (nothing else runs on that thread while it does), so running the loop as fast as the machine
/// allows makes the stall shorter without making anything else worse.</para>
/// </summary>
internal sealed record GeoClipFrameBudget(bool RaiseMaxFps, int MaxFpsTarget)
{
    /// <summary>Kill-switch/selector for the whole lever: <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> disables it.</summary>
    internal const string LeaseEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_FRAME_LEASE";

    /// <summary>An explicit cap to hold the bake at, instead of uncapping. Positive integers only.</summary>
    internal const string MaxFpsEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_FPS";

    /// <summary>Godot reads <c>Engine.MaxFps == 0</c> as "no engine-side frame cap", which is the default target.</summary>
    internal const int UncappedMaxFps = 0;

    internal static GeoClipFrameBudget Disabled { get; } = new(RaiseMaxFps: false, UncappedMaxFps);

    internal static GeoClipFrameBudget Parse(string? lease, string? maxFps)
    {
        if ((lease ?? string.Empty).Trim().ToLowerInvariant() is "0" or "off" or "false" or "no")
        {
            return Disabled;
        }

        var raw = (maxFps ?? string.Empty).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? new GeoClipFrameBudget(RaiseMaxFps: true, parsed)
            : new GeoClipFrameBudget(RaiseMaxFps: true, UncappedMaxFps);
    }

    internal static GeoClipFrameBudget FromEnvironment()
        => Parse(
            Environment.GetEnvironmentVariable(LeaseEnv),
            Environment.GetEnvironmentVariable(MaxFpsEnv));
}

/// <summary>
/// WHICH of the bake's awaited engine frames are closed with a <c>RenderingServer.ForceDraw</c>.
/// </summary>
/// <remarks>
/// <para>A forced draw is the bake's single largest main-thread cost: measured live it is 15.2-16.3 ms per call
/// (geoclip-knights-stage2b phase split, four rigs), and a pose-only bake issued eight to ten of them, which is
/// 75-85 % of everything the bake blocked the game for. That price is a whole synchronous scene render, so the
/// only way to cut it is to issue fewer — never to make one cheaper.</para>
///
/// <para>WHAT A FORCED DRAW BUYS, and therefore where one is load-bearing. A bake resumes from
/// <c>SceneTree.ProcessFrame</c> INSIDE the emission, i.e. after every node's <c>_process</c> has run for that
/// iteration but before the engine's own draw closes it. So:</para>
/// <list type="bullet">
///   <item><description>SKELETON state — attachment, slot colour, draw order, skeleton bounds — is already
///   current at that point, because it is written in <c>_process</c>. A pose READ therefore needs the awaited
///   frame and nothing else.</description></item>
///   <item><description>A MESH SURFACE readback is current there too. That is not an assumption: the offline
///   geometry probe (<c>Sts2SpineGeometryProbe</c>) runs the same seek-then-read loop twice, once with the extra
///   forced draw and once without, and the two arms have reported identical per-mesh tracking on both recorded
///   rigs in every round since Phase 0 — 25/26 meshes and 171/182 changed transitions on byrdonis, 29/30 and
///   203/210 on the ironclad rig, no-draw and with-draw alike. The reason is the one the probe states itself:
///   the host's own main loop keeps rendering between two awaits, so eliding the forced draw removes an EXTRA
///   draw, never the only one.</description></item>
///   <item><description>What the extra draw DOES buy is IMMEDIACY, and exactly one thing in the bake needs it:
///   a RID BRACKET. <c>bracketMid</c> and <c>bracketPost</c> are taken as the upper edge of a window that must
///   contain every mesh the rig has minted so far, and a mesh minted by the frame just awaited is only certain
///   to exist once that frame has actually been drawn. So the LAST frame of a settle that ends in a bracket
///   keeps its draw, and the settle frames before it do not need one.</description></item>
/// </list>
///
/// <para>The COLOUR PROBE is deliberately excluded from the elision and carries its own switch. Its readback is
/// not evidence about the bake's cost but evidence about which mesh belongs to which slot, and a stale read
/// there does not slow a bake down — it drops or mis-assigns a claim. The probe frames are therefore left drawn
/// by default, and <see cref="ProbeDrawEnv"/> exists so a live A/B can price them separately rather than having
/// to bisect them out of the same lever as everything else.</para>
/// </remarks>
internal sealed record GeoClipDrawBudget(bool ElideSettleDraws, bool DrawProbeFrames)
{
    /// <summary>Kill switch for the elision: <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> puts every forced draw
    /// back, which restores the frame-for-frame schedule every recorded shape was produced under.</summary>
    internal const string ElisionEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_DRAW_ELISION";

    /// <summary>Separate switch for the association probe's own frames. DEFAULT ON (they keep drawing); set it
    /// to <c>0</c> to elide those two as well.</summary>
    internal const string ProbeDrawEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_PROBE_DRAW";

    /// <summary>The pre-lever schedule: every awaited frame closed with a forced draw.</summary>
    internal static GeoClipDrawBudget EveryFrame { get; } = new(ElideSettleDraws: false, DrawProbeFrames: true);

    /// <summary>
    /// KILL-SWITCH POLARITY, the same as <see cref="GeoClipFrameBudget"/>'s and deliberately not
    /// <see cref="GeoClipPauseBudget"/>'s: this lever defaults ON and only a word this function recognises turns
    /// it off. A typo therefore costs a slower bake, never a silently different artifact.
    /// </summary>
    internal static bool ParseElision(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    internal static bool ParseProbeDraw(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    internal static GeoClipDrawBudget Parse(string? elision, string? probeDraw)
        => new(ParseElision(elision), ParseProbeDraw(probeDraw));

    internal static GeoClipDrawBudget FromEnvironment()
        => Parse(
            Environment.GetEnvironmentVariable(ElisionEnv),
            Environment.GetEnvironmentVariable(ProbeDrawEnv));

    /// <summary>
    /// Whether frame <paramref name="frameIndex"/> of a <paramref name="frameCount"/>-frame await at
    /// <paramref name="site"/> is closed with a forced draw.
    /// </summary>
    internal bool ForceDrawAt(GeoClipDrawSite site, int frameIndex, int frameCount)
        => site switch
        {
            // The bracket is taken IMMEDIATELY after this await returns, so the last frame must be drawn before
            // it: a mesh minted by that frame and not yet drawn could land above the bracket and be lost to the
            // sweep. The leading frames only settle.
            GeoClipDrawSite.BracketSettle => !ElideSettleDraws || frameIndex >= frameCount - 1,

            // Nothing downstream of a pose read needs THIS frame drawn — see the remarks on the record.
            GeoClipDrawSite.PoseRead => !ElideSettleDraws,

            // Its own switch, and independent of the elision: see the remarks on the record.
            GeoClipDrawSite.ProbeFrame => DrawProbeFrames,
            _ => true,
        };

    /// <summary>What the bake logs about this lever, so a profile can be read against the schedule that produced it.</summary>
    internal string Note =>
        $"elideSettleDraws={(ElideSettleDraws ? 1 : 0)} probeFrameDraws={(DrawProbeFrames ? 1 : 0)}";
}

/// <summary>
/// WHY a bake is awaiting an engine frame, which is what decides whether the frame is closed with a forced draw.
/// Named rather than passed as a bare bool so a new call site has to say what it is waiting for — the same rule
/// the phase names already impose on <c>AwaitFramesAsync</c>.
/// </summary>
internal enum GeoClipDrawSite
{
    /// <summary>Settling the rig immediately before a RID bracket is taken (warmup → <c>bracketMid</c>,
    /// acquisition → <c>bracketPost</c>).</summary>
    BracketSettle,

    /// <summary>Letting a written track time reach the skeleton before its pose (PASS 1) or its geometry
    /// (PASS 3) is read.</summary>
    PoseRead,

    /// <summary>The association probe's own frames: the seek to a group's stop, the nudged colour frames, and
    /// the restore.</summary>
    ProbeFrame,
}

/// <summary>
/// Holds the engine's frame cap at the bake's target and puts back whatever was OBSERVED there before —
/// never a constant, because the host's cap comes from the player's own graphics setting. The engine hop is
/// injected so the whole lease (including the restore-on-throw path) is testable without Godot.
/// </summary>
internal sealed class GeoClipFrameRateLease : IDisposable
{
    private readonly Action<int>? _write;
    private bool _restored;

    private GeoClipFrameRateLease(bool raised, int priorMaxFps, int appliedMaxFps, string note, Action<int>? write)
    {
        Raised = raised;
        PriorMaxFps = priorMaxFps;
        AppliedMaxFps = appliedMaxFps;
        Note = note;
        _write = write;
    }

    /// <summary>Whether the cap was actually changed, and therefore whether there is anything to restore.</summary>
    internal bool Raised { get; }

    internal int PriorMaxFps { get; }

    internal int AppliedMaxFps { get; }

    /// <summary>Why the lease did what it did, for the bake log.</summary>
    internal string Note { get; }

    internal static GeoClipFrameRateLease Acquire(GeoClipFrameBudget budget, Func<int> read, Action<int> write)
    {
        if (!budget.RaiseMaxFps)
        {
            return new GeoClipFrameRateLease(false, 0, 0, $"off ({GeoClipFrameBudget.LeaseEnv})", null);
        }

        int prior;
        try
        {
            prior = read();
        }
        catch (Exception ex)
        {
            return new GeoClipFrameRateLease(false, 0, 0, $"unreadable ({ex.GetType().Name})", null);
        }

        if (prior == budget.MaxFpsTarget)
        {
            return new GeoClipFrameRateLease(false, prior, prior, $"already {Describe(prior)}", null);
        }

        try
        {
            write(budget.MaxFpsTarget);
        }
        catch (Exception ex)
        {
            // The cap may or may not have taken; put back what was read either way, then report the failure.
            try
            {
                write(prior);
            }
            catch (Exception)
            {
                // Nothing further to try: the engine hop is refusing writes.
            }

            return new GeoClipFrameRateLease(false, prior, prior, $"unwritable ({ex.GetType().Name})", null);
        }

        return new GeoClipFrameRateLease(
            true,
            prior,
            budget.MaxFpsTarget,
            $"{Describe(prior)} -> {Describe(budget.MaxFpsTarget)}",
            write);
    }

    public void Dispose()
    {
        if (_restored || !Raised || _write is null)
        {
            _restored = true;
            return;
        }

        _restored = true;
        try
        {
            _write(PriorMaxFps);
        }
        catch (Exception)
        {
            // A bake must never take the host down, and a frame cap that stayed raised is a performance
            // artefact rather than a correctness one.
        }
    }

    private static string Describe(int maxFps)
        => maxFps == GeoClipFrameBudget.UncappedMaxFps
            ? "uncapped"
            : maxFps.ToString(CultureInfo.InvariantCulture);
}

// ── The bake's SCENE-TREE PAUSE (default OFF) ────────────────────────────────────────────────────────

/// <summary>
/// How hard a paused bake checks that the offscreen rig still RE-POSES — i.e. that pausing the tree did not
/// silently freeze the very thing being measured.
/// </summary>
internal enum GeoClipPauseAssertMode
{
    /// <summary>No liveness check. The operator has decided the pause is known-good on this build.</summary>
    Off,

    /// <summary>
    /// The default. Run the FREE check first — two skeleton bounds the bake already read — and pay for the
    /// two-frame checksum only when that comes back inconclusive.
    /// </summary>
    Bounds,

    /// <summary>
    /// Always pay the two-frame checksum, even when the free check already passed. Costs two engine frames per
    /// rig and exists so a live arm can be graded without trusting the cheap check's coverage.
    /// </summary>
    Strict,
}

/// <summary>
/// Whether the bake is allowed to PAUSE the game's scene tree while it holds the main thread, and how hard it
/// checks itself when it does.
///
/// <para>DEFAULT OFF, and the default is not a placeholder. The gate this lever has to pass is
/// <see cref="GeoClipPauseVerdict.ArmingAllowed"/>, which needs a bake that is both mostly-parked and short
/// enough that the freeze is beneath notice; the profiles measured on both available rigs are neither (blocking
/// share 0.30-0.39, parked 0.36-0.57, totals ~290-355 ms), so the verdict is false on every measurement that
/// exists. The lever is built, tested and documented anyway, for a future where the bake is short enough — not
/// armed on a hypothesis.</para>
///
/// <para>WHAT PAUSING WOULD BUY, and why the assertion below is not optional: a paused tree stops running the
/// game between the bake's awaited frames, so the bake's PARKED milliseconds go away. It buys nothing if the
/// paused tree also stops running the detached rig, and in that case it does something far worse than nothing —
/// every pose read comes back identical and the artifact is a still frame wearing an animation's name. So a
/// paused bake must prove the rig moved, and <see cref="GeoClipPauseLivenessCheck"/> is that proof.</para>
/// </summary>
internal sealed record GeoClipPauseBudget(bool PauseTree, GeoClipPauseAssertMode Assert)
{
    /// <summary>The arm. OFF unless explicitly affirmative — see <see cref="ParsePause"/>.</summary>
    internal const string PauseEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_PAUSE_TREE";

    /// <summary>Selects the liveness check: <c>bounds</c> (default) / <c>strict</c> / <c>off</c>.</summary>
    internal const string AssertEnv = "SPIRECTL_SPINE_GEOCLIP_BAKE_PAUSE_ASSERT";

    internal static GeoClipPauseBudget Disabled { get; } = new(PauseTree: false, GeoClipPauseAssertMode.Bounds);

    /// <summary>
    /// ARMED ONLY ON AN EXPLICIT AFFIRMATIVE, the opposite polarity from
    /// <see cref="GeoClipFrameBudget.Parse"/>'s kill-switch. That lever defaults ON and reads a negative to turn
    /// itself off; this one defaults OFF and reads a positive to turn itself on, because the failure mode of a
    /// typo differs: a mis-typed frame-cap value costs a slower bake, a mis-typed pause value would freeze the
    /// game the player is looking at.
    /// </summary>
    internal static bool ParsePause(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";

    /// <summary>
    /// <c>off</c>/<c>0</c>/<c>false</c>/<c>no</c> disables the check, <c>strict</c> forces the expensive one, and
    /// ANYTHING ELSE — including a typo and an empty string — falls back to
    /// <see cref="GeoClipPauseAssertMode.Bounds"/>. Deliberately asymmetric: an unrecognised value must never be
    /// the thing that silently removed the safety net, so only a word this function actually knows can turn it
    /// off.
    /// </summary>
    internal static GeoClipPauseAssertMode ParseAssert(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "off" or "0" or "false" or "no" => GeoClipPauseAssertMode.Off,
            "strict" => GeoClipPauseAssertMode.Strict,
            _ => GeoClipPauseAssertMode.Bounds,
        };

    internal static GeoClipPauseBudget Parse(string? pause, string? assert)
        => new(ParsePause(pause), ParseAssert(assert));

    internal static GeoClipPauseBudget FromEnvironment()
        => Parse(
            Environment.GetEnvironmentVariable(PauseEnv),
            Environment.GetEnvironmentVariable(AssertEnv));
}

/// <summary>
/// The PROCESS-WIDE LATCH that stops the pause lever retrying a failure it already paid for.
///
/// <para>Two things can refuse the pause at runtime rather than at parse time: the deadline probe (the first
/// frame awaited under pause never arrived, which costs that bake the probe's whole 500 ms budget) and the
/// liveness assertion (the rig did not re-pose, which costs that bake its entire pass). Both are properties of
/// the BUILD and the engine, not of the rig, so a second bake in the same process would fail them the same way —
/// and a sweep of 40 rigs would pay for the same discovery 40 times. One failure disarms the lever for the life
/// of the process.</para>
///
/// <para>Latching lives HERE, in the Godot-free core, rather than in the baker, so "one failure, never again"
/// is a unit-testable claim instead of something only a live run could disprove.</para>
/// </summary>
internal static class GeoClipPauseDisarm
{
    private static readonly object Gate = new();
    private static bool _latched;
    private static string _reason = string.Empty;
    private static int _assertFailures;

    /// <summary>Whether the lever has been disarmed for the rest of this process.</summary>
    internal static bool Latched
    {
        get
        {
            lock (Gate)
            {
                return _latched;
            }
        }
    }

    /// <summary>Why, in the words the first failure used. Empty while unlatched.</summary>
    internal static string Reason
    {
        get
        {
            lock (Gate)
            {
                return _reason;
            }
        }
    }

    /// <summary>
    /// How many times a paused bake's liveness assertion has REFUTED the pause in this process. Carried into
    /// every later bake report (see <c>GeoClipBakeReport.PauseAssertFailed</c>) rather than only into the report
    /// of the pass that failed — that pass writes no manifest at all, so a per-pass counter would be a field no
    /// artifact could ever carry a non-zero value for.
    /// </summary>
    internal static int AssertFailures
    {
        get
        {
            lock (Gate)
            {
                return _assertFailures;
            }
        }
    }

    /// <summary>Disarm the lever. TRUE only for the caller that actually latched it, so the loud log line is
    /// printed once rather than once per rig.</summary>
    internal static bool Latch(string reason)
    {
        lock (Gate)
        {
            if (_latched)
            {
                return false;
            }

            _latched = true;
            _reason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
            return true;
        }
    }

    /// <summary>Record a refuted pause and disarm on the same act — a liveness failure is never a reason to try
    /// again.</summary>
    internal static bool RecordAssertFailure(string reason)
    {
        lock (Gate)
        {
            _assertFailures += 1;
        }

        return Latch(reason);
    }

    /// <summary>Process-wide state, so the tests that exercise it have to be able to put it back.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _latched = false;
            _reason = string.Empty;
            _assertFailures = 0;
        }
    }
}

/// <summary>
/// Holds the game's scene tree PAUSED for the duration of a bake and puts back whatever was OBSERVED there
/// before — never a constant. Modelled on <see cref="GeoClipFrameRateLease"/>, engine hop injected, so every
/// refusal path (including the restore-on-throw one) is provable without Godot.
///
/// <para>THE REFUSAL THAT MATTERS is "the game already paused itself". A bake cannot know WHY — a pause menu, a
/// modal, another tool's lease — and it certainly cannot know when that reason has passed, so it must neither
/// re-pause (a no-op that would make it believe it owns the pause) nor unpause on disposal (which would hand
/// control of the game back to a player who never asked for it, in the middle of whatever paused it). It leaves
/// the tree exactly as it found it and says so in the note.</para>
/// </summary>
internal sealed class GeoClipScenePauseLease : IDisposable
{
    private readonly Action<bool>? _write;
    private bool _released;

    private GeoClipScenePauseLease(bool paused, bool priorPaused, string note, Action<bool>? write)
    {
        Paused = paused;
        PriorPaused = priorPaused;
        Note = note;
        _write = write;
    }

    /// <summary>Whether the tree was actually paused BY THIS LEASE, and therefore whether there is anything to
    /// restore — and whether the bake is allowed to believe its parked frames got cheaper.</summary>
    internal bool Paused { get; }

    /// <summary>What <c>readPaused()</c> returned, which is what disposal puts back.</summary>
    internal bool PriorPaused { get; }

    /// <summary>Why the lease did what it did, for the bake log and for <c>bake.pauseNote</c>. Carries env var
    /// names and exception TYPE names only — never a scene path, node name or animation name, because it ships
    /// inside the artifact.</summary>
    internal string Note { get; }

    /// <summary>
    /// Take the pause.
    /// </summary>
    /// <param name="applyRigAlways">
    /// The hop that exempts the bake's own offscreen subtree from the pause it just took. Invoked ONLY on the
    /// path that actually paused, and treated as part of taking the pause: if it fails there is no point holding
    /// a pause that also froze the rig, so the lease unpauses itself and refuses.
    /// </param>
    internal static GeoClipScenePauseLease Acquire(
        GeoClipPauseBudget budget,
        Func<bool> readPaused,
        Action<bool> writePaused,
        Action? applyRigAlways = null)
    {
        if (!budget.PauseTree)
        {
            return new GeoClipScenePauseLease(false, false, $"off ({GeoClipPauseBudget.PauseEnv})", null);
        }

        // Checked BEFORE the read hop, so a disarmed process does not even touch the tree.
        if (GeoClipPauseDisarm.Latched)
        {
            return new GeoClipScenePauseLease(false, false, $"disarmed ({GeoClipPauseDisarm.Reason})", null);
        }

        bool prior;
        try
        {
            prior = readPaused();
        }
        catch (Exception ex)
        {
            return new GeoClipScenePauseLease(false, false, $"unreadable ({ex.GetType().Name})", null);
        }

        if (prior)
        {
            // NOT ours. Nothing is written now and nothing is written on disposal: see the class remarks.
            return new GeoClipScenePauseLease(false, true, "already paused by the game", null);
        }

        try
        {
            writePaused(true);
        }
        catch (Exception ex)
        {
            // The pause may or may not have taken; put back what was READ either way, then report the failure.
            try
            {
                writePaused(prior);
            }
            catch (Exception)
            {
                // Nothing further to try: the engine hop is refusing writes.
            }

            return new GeoClipScenePauseLease(false, prior, $"unwritable ({ex.GetType().Name})", null);
        }

        try
        {
            applyRigAlways?.Invoke();
        }
        catch (Exception ex)
        {
            try
            {
                writePaused(prior);
            }
            catch (Exception)
            {
                // As above.
            }

            return new GeoClipScenePauseLease(false, prior, $"rig-exempt-failed ({ex.GetType().Name})", null);
        }

        return new GeoClipScenePauseLease(true, prior, "running -> paused", writePaused);
    }

    /// <summary>
    /// Give the pause back EARLY, before the bake is finished with it — the recovery path for a probe or liveness
    /// failure, and the way the rig teardown is made to run on an unpaused tree. Idempotent, and
    /// <see cref="Dispose"/> after it is a no-op.
    /// </summary>
    internal void Release() => Dispose();

    public void Dispose()
    {
        // THE GUARD IS THE POINT. A lease that did not take the pause writes NOTHING on the way out: the
        // "already paused by the game" case must not hand control back to a player who never asked for it, and
        // the off / disarmed / refused cases must not touch a tree they never touched going in.
        if (_released || !Paused || _write is null)
        {
            _released = true;
            return;
        }

        _released = true;
        try
        {
            // The OBSERVED prior, read out of the lease's own field rather than restated as a literal. On this
            // two-state lever that value can only be `false` — a `true` prior is refused above and never reaches
            // here — so this and a `false` literal cannot be told apart by behaviour. It is written this way as
            // the shape the lever should have, not as a defence; the defended claim is the guard above it.
            _write(PriorPaused);
        }
        catch (Exception)
        {
            // A bake must never take the host down. A tree left paused is catastrophic in a different way, but
            // there is nothing left to try: the hop that would unpause it is the one that just threw.
        }
    }
}

/// <summary>
/// What a liveness check concluded about a rig posed under a paused tree.
/// </summary>
internal enum GeoClipPauseLiveness
{
    /// <summary>The rig demonstrably re-posed: the pause did not freeze it.</summary>
    Passed,

    /// <summary>The check could not tell. NEVER a pass — see <see cref="GeoClipPauseLivenessCheck"/>.</summary>
    Inconclusive,

    /// <summary>The rig did NOT re-pose. Everything this pass measured is a still frame.</summary>
    Failed,
}

/// <summary>
/// The two checks that stand between a paused bake and an artifact that is silently one frame repeated.
///
/// <para>THE WHOLE RULE IS "EQUAL IS NOT A PASS". Both checks work by re-reading something at two different track
/// times and asking whether it changed. A change proves the rig moved. NO change proves nothing on its own — a
/// skeleton's bounding box legitimately stays put across two poses — so the cheap check is only ever allowed to
/// say "different, therefore alive" or "I cannot tell". Letting it read equality as health is the single
/// mutation that would make this whole file decorative, and it is pinned by name in the tests.</para>
///
/// <para>The expensive check IS entitled to fail, because it chooses its own two times and reads the actual
/// posed vertices of the meshes the artifact is built from: if those are identical at two deliberately different
/// times, on meshes large enough to move, the rig is not being posed.</para>
/// </summary>
internal static class GeoClipPauseLivenessCheck
{
    /// <summary>Skeleton bounds are in skeleton units (roughly pixels); anything under this is noise rather than
    /// a pose change.</summary>
    internal const double BoundsEpsilon = 1e-3;

    /// <summary>Posed vertex sums, same units, same reasoning.</summary>
    internal const double ChecksumEpsilon = 1e-4;

    /// <summary>Two track times closer together than this are the same time.</summary>
    internal const double TimeEpsilon = 1e-4;

    /// <summary>How many associated meshes the expensive check reads. Three rather than one because the largest
    /// mesh on a rig can be a static backdrop; rather than all of them because the check must cost two frames,
    /// not two frames plus a full geometry pass.</summary>
    internal const int ChecksumMeshCount = 3;

    /// <summary>
    /// A1 — FREE. Two skeleton bounds the bake has already read, at two different track times.
    /// </summary>
    /// <returns>
    /// <see cref="GeoClipPauseLiveness.Passed"/> when they differ; <see cref="GeoClipPauseLiveness.Inconclusive"/>
    /// when they are equal, degenerate or missing. NEVER <see cref="GeoClipPauseLiveness.Failed"/>: equal bounds
    /// are a normal property of a live rig, so this check can confirm life but can never refute it.
    /// </returns>
    internal static GeoClipPauseLiveness FromBounds(IReadOnlyList<double>? first, IReadOnlyList<double>? second)
    {
        if (!UsableBounds(first) || !UsableBounds(second))
        {
            return GeoClipPauseLiveness.Inconclusive;
        }

        for (var i = 0; i < 4; i += 1)
        {
            if (Math.Abs(first![i] - second![i]) > BoundsEpsilon)
            {
                return GeoClipPauseLiveness.Passed;
            }
        }

        return GeoClipPauseLiveness.Inconclusive;
    }

    /// <summary>
    /// A3 — TWO FRAMES. Position checksums of the same meshes, read at two deliberately different track times.
    /// </summary>
    /// <returns>
    /// <see cref="GeoClipPauseLiveness.Passed"/> when at least one mesh moved;
    /// <see cref="GeoClipPauseLiveness.Failed"/> when none did; <see cref="GeoClipPauseLiveness.Inconclusive"/>
    /// when the two reads are not comparable at all (nothing read back, or a different number of meshes answered
    /// the second time, which is a broken read rather than a frozen rig).
    /// </returns>
    internal static GeoClipPauseLiveness FromChecksums(
        IReadOnlyList<double>? first,
        IReadOnlyList<double>? second)
    {
        if (first is null || second is null || first.Count == 0 || first.Count != second.Count)
        {
            return GeoClipPauseLiveness.Inconclusive;
        }

        for (var i = 0; i < first.Count; i += 1)
        {
            if (Math.Abs(first[i] - second[i]) > ChecksumEpsilon)
            {
                return GeoClipPauseLiveness.Passed;
            }
        }

        return GeoClipPauseLiveness.Failed;
    }

    /// <summary>
    /// Whether the expensive check has to run, given what the free one said and which mode the operator chose.
    /// </summary>
    internal static bool NeedsChecksumCheck(GeoClipPauseLiveness bounds, GeoClipPauseAssertMode mode)
        => mode switch
        {
            GeoClipPauseAssertMode.Off => false,
            GeoClipPauseAssertMode.Strict => true,
            _ => bounds != GeoClipPauseLiveness.Passed,
        };

    /// <summary>
    /// THE VERDICT. Only the expensive check can refute a pause: an inconclusive free check that was never
    /// escalated (because the operator turned the assertion off) is not evidence of anything.
    /// </summary>
    internal static bool PauseIsRefuted(GeoClipPauseLiveness bounds, GeoClipPauseLiveness? checksums)
        => checksums == GeoClipPauseLiveness.Failed;

    /// <summary>
    /// The <paramref name="take"/> meshes with the most vertices, which are the ones whose motion is most likely
    /// to be measurable. Ties break on the RID id so two runs of the same bake read the same meshes and a
    /// disagreement between them is about the rig rather than about which meshes were sampled. Meshes that read
    /// back with no vertices are dropped: they cannot answer the question either way, and three empty ones would
    /// produce two identical checksum lists and convict a healthy rig.
    /// </summary>
    internal static IReadOnlyList<ulong> LargestMeshes(IReadOnlyDictionary<ulong, int> vertexCounts, int take)
        => take <= 0
            ? []
            : [.. vertexCounts
                .Where(entry => entry.Value > 0)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key)
                .Take(take)
                .Select(entry => entry.Key)];

    /// <summary>
    /// The two track times the expensive check seeks between, or NULL when this animation offers no two
    /// distinguishable ones — a zero-length clip sampled at t=0, where "the vertices did not change" would be the
    /// correct answer and reporting it as a frozen rig would be a false alarm.
    /// </summary>
    internal static (double First, double Second)? ChooseChecksumTimes(double poseSeconds, double durationSeconds)
    {
        if (Math.Abs(poseSeconds) > TimeEpsilon)
        {
            return (0d, poseSeconds);
        }

        return durationSeconds > TimeEpsilon ? (0d, durationSeconds * 0.5d) : null;
    }

    private static bool UsableBounds(IReadOnlyList<double>? bounds)
        => bounds is { Count: >= 4 } && bounds[2] > 0d && bounds[3] > 0d;
}

// ── Manifest assembly ────────────────────────────────────────────────────────────────────────────────

internal sealed record GeoClipBuildRequest(
    string Scene,
    string Node,
    string Anim,
    int Fps,
    double DurationSeconds,
    IReadOnlyList<GeoClipFrameSample> Frames,
    IReadOnlyList<GeoClipPage> Pages,
    SpineAtlasDocument Atlas,
    int SrcRectPadding = Sts2SpineGeoClipMath.DefaultSrcRectPadding,
    double RigidThreshold = Sts2SpineGeometryMath.DefaultRigidThreshold,
    double ContainmentTolerancePx = Sts2SpineGeoClipMath.DefaultContainmentTolerancePx,
    // Where the clip sits (see GeoClipPlacement). Null when the bake could not measure the skeleton's bounds, in
    // which case `meta.placement` is omitted and a client falls back to inverting the raster clip's placement.
    GeoClipPlacement? Placement = null,
    // Baker-supplied notes that lead the manifest's `diagnostics`. Used to say, in the artifact itself, when a
    // counter's MEANING changed (a pose-only bake's "ever visible" is "visible at this pose"), which is the kind
    // of thing a reader of a stray manifest cannot recover from the numbers.
    IReadOnlyList<string>? Notes = null);

internal static class Sts2SpineGeoClipBuilder
{
    // Rounding, chosen per channel so the artifact stays legible and small without costing sub-pixel accuracy:
    // skeleton-local positions are in skeleton units (roughly pixels), uvs are fractions of a crop, and the
    // linear part of a transform is a ratio.
    private const int VertexDecimals = 3;
    private const int UvDecimals = 6;
    private const int MatrixDecimals = 6;
    private const int ColorDecimals = 4;
    private const int BoundsDecimals = 3;

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal sealed record BuildResult(GeoClipDocument Document, IReadOnlyList<string> Diagnostics);

    internal static BuildResult Build(GeoClipBuildRequest request)
    {
        var diagnostics = new List<string>(request.Notes ?? []);
        var pageSizes = request.Pages.Select(page => (page.Width, page.Height)).ToArray();

        // 1. Parts. One per (slotIndex, attachmentName) ever observed WITH geometry; the first frame that shows
        //    it is its reference pose.
        var partIds = new Dictionary<(int Slot, string Attachment), int>();
        var parts = new List<GeoClipPart>();
        var refGeometry = new List<(double[] X, double[] Y)>();
        var activeFrames = new List<List<(IReadOnlyList<double> X, IReadOnlyList<double> Y)>>();

        // Aggregated rather than per frame: a slot whose mesh never associated would otherwise emit one
        // identical line for every sampled frame and bury every other diagnostic.
        var emptyMeshFrames = new Dictionary<(int Slot, string Attachment), int>();
        var unnamedDrawableFrames = new Dictionary<int, int>();

        for (var frameIndex = 0; frameIndex < request.Frames.Count; frameIndex += 1)
        {
            foreach (var slot in request.Frames[frameIndex].Slots)
            {
                if (!slot.RequiresDrawing)
                {
                    continue;
                }

                if (slot.AttachmentName is null)
                {
                    unnamedDrawableFrames[slot.SlotIndex] = unnamedDrawableFrames.GetValueOrDefault(slot.SlotIndex) + 1;
                    continue;
                }

                var key = (slot.SlotIndex, slot.AttachmentName);
                if (!slot.HasGeometry || slot.VertsX.Length == 0)
                {
                    emptyMeshFrames[key] = emptyMeshFrames.GetValueOrDefault(key) + 1;
                    continue;
                }

                if (partIds.ContainsKey(key))
                {
                    continue;
                }

                var partId = parts.Count;
                var (part, note) = BuildPart(partId, frameIndex, slot, request, pageSizes);
                if (note is not null)
                {
                    diagnostics.Add(note);
                }

                partIds[key] = partId;
                parts.Add(part);
                refGeometry.Add((slot.VertsX, slot.VertsY));
                activeFrames.Add([]);
            }
        }

        foreach (var ((slotIndex, attachment), count) in emptyMeshFrames.OrderBy(pair => pair.Key.Slot))
        {
            diagnostics.Add(
                $"slot {slotIndex} attachment '{attachment}': the attachment is set but the slot's mesh read back empty "
                + $"on {count} of {request.Frames.Count} frames; emitted as hidden on those frames.");
        }

        foreach (var (slotIndex, count) in unnamedDrawableFrames.OrderBy(pair => pair.Key))
        {
            diagnostics.Add(
                $"slot {slotIndex}: a drawable attachment was present but its stable attachment name was unavailable "
                + $"on {count} of {request.Frames.Count} frames; emitted as hidden on those frames.");
        }

        // 2. Rigidity, per part, over every frame in which it is on screen.
        foreach (var frame in request.Frames)
        {
            foreach (var slot in frame.Slots)
            {
                if (slot.AttachmentName is null || !slot.RequiresDrawing || !slot.HasGeometry || slot.VertsX.Length == 0)
                {
                    continue;
                }

                if (partIds.TryGetValue((slot.SlotIndex, slot.AttachmentName), out var partId))
                {
                    activeFrames[partId].Add((slot.VertsX, slot.VertsY));
                }
            }
        }

        var rigid = new bool[parts.Count];
        for (var partId = 0; partId < parts.Count; partId += 1)
        {
            var (refX, refY) = refGeometry[partId];
            var track = Sts2SpineGeoClipMath.ClassifyPart(refX, refY, activeFrames[partId], request.RigidThreshold);
            rigid[partId] = track.IsRigid;
            if (track.VertexCountMismatch)
            {
                diagnostics.Add(
                    $"part {partId} (slot {parts[partId].SlotIndex} '{parts[partId].AttachmentName}'): vertex count "
                    + "changed between frames under one attachment name; streamed as deforming.");
            }

            parts[partId] = parts[partId] with { Rigid = track.IsRigid };
        }

        // 3. Frames.
        var frames = new List<GeoClipFrame>(request.Frames.Count);
        for (var frameIndex = 0; frameIndex < request.Frames.Count; frameIndex += 1)
        {
            var sample = request.Frames[frameIndex];
            var slots = new Dictionary<string, GeoClipSlotFrame>(StringComparer.Ordinal);
            foreach (var slot in sample.Slots)
            {
                var color = Round(slot.Color, ColorDecimals);
                var key = slot.SlotIndex.ToString(CultureInfo.InvariantCulture);
                if (slot.AttachmentName is null
                    || !slot.RequiresDrawing
                    || !slot.HasGeometry
                    || slot.VertsX.Length == 0
                    || !partIds.TryGetValue((slot.SlotIndex, slot.AttachmentName), out var partId))
                {
                    slots[key] = new GeoClipSlotFrame(null, color, null, null);
                    continue;
                }

                var (refX, refY) = refGeometry[partId];
                if (rigid[partId] && slot.VertsX.Length == refX.Length)
                {
                    var xform = Sts2SpineGeoClipMath.FitXform(refX, refY, slot.VertsX, slot.VertsY);
                    slots[key] = new GeoClipSlotFrame(partId, color, RoundXform(xform), null);
                    continue;
                }

                slots[key] = new GeoClipSlotFrame(partId, color, null, Interleave(slot.VertsX, slot.VertsY, VertexDecimals));
            }

            frames.Add(new GeoClipFrame(Math.Round(sample.T, 5), sample.DrawOrder, slots));
        }

        var meta = new GeoClipMeta(
            Sts2SpineGeoClipMath.Schema,
            request.Scene,
            request.Node,
            request.Anim,
            request.Fps,
            request.Frames.Count,
            Math.Round(request.DurationSeconds * 1000d, 3),
            [.. request.Frames.Select(frame => Round(frame.Bounds, BoundsDecimals))],
            request.Placement);

        if (parts.Count == 0)
        {
            diagnostics.Add(
                "No drawable part was produced: every sampled frame reported either no drawable attachment or no drawable mesh geometry.");
        }

        return new BuildResult(new GeoClipDocument(meta, request.Pages, parts, frames, diagnostics), diagnostics);
    }

    private static (GeoClipPart Part, string? Diagnostic) BuildPart(
        int partId,
        int refFrame,
        GeoClipSlotSample slot,
        GeoClipBuildRequest request,
        IReadOnlyList<(int Width, int Height)> pageSizes)
    {
        var attachmentName = slot.AttachmentName ?? string.Empty;
        string? diagnostic = null;

        double uMin = 0, uMax = 0, vMin = 0, vMax = 0;
        if (slot.U.Length > 0)
        {
            uMin = slot.U.Min();
            uMax = slot.U.Max();
            vMin = slot.V.Min();
            vMax = slot.V.Max();
        }

        var match = Sts2SpineGeoClipAtlas.ResolvePage(
            attachmentName,
            uMin,
            vMin,
            uMax,
            vMax,
            request.Atlas,
            pageSizes,
            request.ContainmentTolerancePx);

        var pageId = match.PageId;
        if (pageId < 0 || pageId >= pageSizes.Count)
        {
            // Losing a body part outright is worse than cropping it off the wrong page, and the diagnostic makes
            // the guess auditable. Only a page-less atlas leaves pageId negative.
            var fallback = pageSizes.Count > 0 ? 0 : -1;
            diagnostic =
                $"part {partId} (slot {slot.SlotIndex} '{attachmentName}'): could not resolve an atlas page "
                + $"({match.Method}); falling back to page {fallback}.";
            pageId = fallback;
        }

        var (pageWidth, pageHeight) = pageId >= 0 && pageId < pageSizes.Count ? pageSizes[pageId] : (0, 0);
        var rect = Sts2SpineGeoClipMath.DeriveSrcRect(slot.U, slot.V, pageWidth, pageHeight, request.SrcRectPadding);
        if (rect.IsEmpty)
        {
            diagnostic ??=
                $"part {partId} (slot {slot.SlotIndex} '{attachmentName}'): page {pageId} reported no usable size, "
                + "so the src rect is empty and the part's uvs are zeroed.";
        }

        var (u, v) = Sts2SpineGeoClipMath.RenormalizeUvs(slot.U, slot.V, rect, pageWidth, pageHeight);

        return (
            new GeoClipPart(
                partId,
                slot.SlotIndex,
                attachmentName,
                pageId,
                rect.ToArray(),
                slot.Indices,
                Interleave(u, v, UvDecimals),
                Interleave(slot.VertsX, slot.VertsY, VertexDecimals),
                refFrame,
                Rigid: false,
                slot.BlendMode),
            diagnostic);
    }

    internal static string Serialize(GeoClipDocument document)
        => JsonSerializer.Serialize(document, ManifestJson);

    private static double[] Interleave(IReadOnlyList<double> a, IReadOnlyList<double> b, int decimals)
    {
        var count = Math.Min(a.Count, b.Count);
        var flat = new double[count * 2];
        for (var i = 0; i < count; i += 1)
        {
            flat[i * 2] = Math.Round(a[i], decimals);
            flat[(i * 2) + 1] = Math.Round(b[i], decimals);
        }

        return flat;
    }

    private static double[] Round(IReadOnlyList<double> values, int decimals)
    {
        var rounded = new double[values.Count];
        for (var i = 0; i < values.Count; i += 1)
        {
            rounded[i] = Math.Round(values[i], decimals);
        }

        return rounded;
    }

    // The linear half of a transform is a ratio and the translation half is a position, so they do not want the
    // same precision.
    private static double[] RoundXform(IReadOnlyList<double> xform)
        =>
        [
            Math.Round(xform[0], MatrixDecimals),
            Math.Round(xform[1], MatrixDecimals),
            Math.Round(xform[2], MatrixDecimals),
            Math.Round(xform[3], MatrixDecimals),
            Math.Round(xform[4], VertexDecimals),
            Math.Round(xform[5], VertexDecimals),
        ];
}

// ── SLOT-COLOUR IDENTITY: separating meshes the atlas cannot tell apart ──────────────────────────────

/// <summary>
/// Which of several meshes that all draw the SAME atlas region belongs to which slot, decided from the colour
/// each side already carries.
///
/// <para>WHY THIS EXISTS. A rig may legitimately put one attachment on several slots at once — three
/// <c>circ_rotator</c> quads on a flail knight's attack, two sword-swish quads on a spectral knight's. Every one
/// of them addresses one identical uv box, so <see cref="Sts2SpineGeoClipAtlas.MatchMeshToAttachmentRegion"/>
/// scores them all at zero against each other and correctly answers
/// <see cref="Sts2SpineGeoClipAtlas.MatchAmbiguous"/>: there is nothing in the atlas that distinguishes them.
/// The colour NUDGE cannot separate them either, and the measured reason is instructive — a live
/// <c>fight KNIGHTS_ELITE</c> bake spent 264 colour reads on the flail's three and reported "43 of 43 meshes
/// never moved", because those slots are exactly the ones the animation's own colour timeline rewrites every
/// frame, which overwrites the nudge before the next frame can be read.</para>
///
/// <para>AND THAT IS THE OPENING. The timeline that defeats the nudge is itself the discriminator: it gives the
/// three slots three different colours at the sampled pose, and the renderer writes each slot's colour into its
/// mesh's vertex colours. So the pairing can be read off directly, with no nudge, no extra awaited frame and no
/// extra device read — the slot side comes from the pose the baker already captured, and the mesh side from the
/// same surface read that produced the uv box. The evidence is a measurement on the running skeleton, of the
/// same kind as a colour flip; what changes is that the animation is the source of the signal rather than the
/// thing destroying it.</para>
///
/// <para>IT REFUSES RATHER THAN RANKS. The decision is MUTUAL best with a margin, in both directions: a slot
/// claims a candidate only when that candidate is its nearest by more than <see cref="DefaultMargin"/> AND the
/// candidate's nearest slot is that same slot by the same margin. Equal colours therefore produce no pairing at
/// all rather than an arbitrary one — which is what the spectral knight's two swish slots do, both sitting at
/// <c>(1,1,1,0)</c> at the sampled pose. Nothing here consults list order, and no leftover is ever paired by
/// elimination.</para>
/// </summary>
internal static class Sts2SpineGeoClipSlotColorIdentity
{
    /// <summary>Kill switch. <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> leaves every such tie unassociated,
    /// exactly as before this arm existed.</summary>
    internal const string ArmEnv = "SPIRECTL_SPINE_GEOCLIP_ASSOC_SLOT_COLOR";

    /// <summary>
    /// How far apart two colours may be and still count as the same one. The art is 8-bit and the value makes a
    /// float round trip through the engine, so "identical" colours can differ by a couple of LSBs (1/255 ≈
    /// 0.0039); 0.02 is five of those. It is an ABSOLUTE gate, not a ranking: a candidate further than this from
    /// the slot's colour is never paired with it however much the nearest it happens to be.
    /// </summary>
    internal const double DefaultTolerance = 0.02;

    /// <summary>
    /// How much nearer the winner must be than the runner-up before the pairing counts as decided. The flail
    /// knight's three <c>circ_rotator</c> slots separate by 0.125 at their closest pair, so this clears them by
    /// 2.5×; the spectral knight's two swish slots separate by 0.0, so they refuse.
    /// </summary>
    internal const double DefaultMargin = 0.05;

    /// <summary>No candidate was offered for this slot at all.</summary>
    internal const string RefusedNoCandidate = "no-candidate";

    /// <summary>The nearest candidate's colour is not this slot's colour, within <see cref="DefaultTolerance"/>.</summary>
    internal const string RefusedAboveTolerance = "above-tolerance";

    /// <summary>Two or more candidates are equally near this slot — the case that must never be guessed.</summary>
    internal const string RefusedTiedCandidates = "tied-candidates";

    /// <summary>The nearest candidate is nearer to another slot, or is not decisive about this one.</summary>
    internal const string RefusedContested = "contested";

    /// <summary>One slot needing a pairing, and the colour its own pose carries at the sampled time.</summary>
    internal readonly record struct SlotColor(int Ordinal, double R, double G, double B, double A);

    /// <summary>One candidate mesh, and the colour the renderer wrote into its vertices.</summary>
    /// <param name="Order">The caller's own handle, matching <see cref="Sts2SpineGeoClipAtlas.TiedCandidate.Order"/>.</param>
    internal readonly record struct MeshColor(int Order, double R, double G, double B, double A);

    /// <summary>One slot this arm declined to pair, and why.</summary>
    internal sealed record Refusal(int Ordinal, string Reason);

    /// <summary>
    /// The verdict. <see cref="OrderByOrdinal"/> is injective by construction — a candidate has one nearest slot,
    /// so two slots cannot both be its mutual best — and every ordinal not in it is named in
    /// <see cref="Refused"/> with a reason.
    /// </summary>
    internal sealed record Disambiguation(
        IReadOnlyDictionary<int, int> OrderByOrdinal,
        IReadOnlyList<Refusal> Refused);

    /// <summary>
    /// Chebyshev distance over the four channels. Per-channel rather than Euclidean because the signal is
    /// per-channel: two colours that agree on RGB and differ only on alpha are as distinguishable as two that
    /// differ everywhere, and summing would dilute exactly that case (which is the one the flail knight's
    /// closest pair actually is — 0.125 of it is alpha).
    /// </summary>
    internal static double Distance(SlotColor slot, MeshColor mesh)
        => Math.Max(
            Math.Max(Math.Abs(slot.R - mesh.R), Math.Abs(slot.G - mesh.G)),
            Math.Max(Math.Abs(slot.B - mesh.B), Math.Abs(slot.A - mesh.A)));

    internal static Disambiguation Disambiguate(
        IReadOnlyList<SlotColor> slots,
        IReadOnlyList<MeshColor> candidates,
        double tolerance = DefaultTolerance,
        double margin = DefaultMargin)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(candidates);

        var pairs = new Dictionary<int, int>();
        var refused = new List<Refusal>();
        if (slots.Count == 0)
        {
            return new Disambiguation(pairs, refused);
        }

        if (candidates.Count == 0)
        {
            foreach (var slot in slots)
            {
                refused.Add(new Refusal(slot.Ordinal, RefusedNoCandidate));
            }

            return new Disambiguation(pairs, [.. refused.OrderBy(entry => entry.Ordinal)]);
        }

        // Both directions of the same distance matrix, computed once. `Nearest` never resolves a tie: when two
        // entries are equidistant it keeps the first seen, and the margin test below then refuses the slot — so
        // the arbitrary half of the argmin can only ever produce a REFUSAL, never a pairing.
        var slotNearest = new Dictionary<int, (int Order, double Best, double RunnerUp)>();
        foreach (var slot in slots)
        {
            slotNearest[slot.Ordinal] = Nearest(candidates.Select(
                candidate => (candidate.Order, Distance(slot, candidate))));
        }

        var candidateNearest = new Dictionary<int, (int Ordinal, double Best, double RunnerUp)>();
        foreach (var candidate in candidates)
        {
            candidateNearest[candidate.Order] = Nearest(slots.Select(
                slot => (slot.Ordinal, Distance(slot, candidate))));
        }

        foreach (var slot in slots.OrderBy(entry => entry.Ordinal))
        {
            var (order, best, runnerUp) = slotNearest[slot.Ordinal];
            if (best > tolerance)
            {
                refused.Add(new Refusal(slot.Ordinal, RefusedAboveTolerance));
                continue;
            }

            if (!Decisive(best, runnerUp, margin))
            {
                refused.Add(new Refusal(slot.Ordinal, RefusedTiedCandidates));
                continue;
            }

            // …and the same question from the mesh's side. Without this a group of three slots where two are
            // nearest the SAME candidate would pair one of them and leave the other looking merely unlucky,
            // when in fact neither pairing was determined.
            var (ordinal, meshBest, meshRunnerUp) = candidateNearest[order];
            if (ordinal != slot.Ordinal || meshBest > tolerance || !Decisive(meshBest, meshRunnerUp, margin))
            {
                refused.Add(new Refusal(slot.Ordinal, RefusedContested));
                continue;
            }

            pairs[slot.Ordinal] = order;
        }

        return new Disambiguation(pairs, [.. refused.OrderBy(entry => entry.Ordinal)]);
    }

    internal static bool ParseArmed(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    internal static bool ArmedFromEnvironment()
        => ParseArmed(Environment.GetEnvironmentVariable(ArmEnv));

    // `runnerUp > best` is not redundant with the margin: two candidates at the SAME distance would otherwise
    // satisfy `0 >= 0` for a zero margin and be resolved in enumeration order, which is the guess this refuses.
    private static bool Decisive(double best, double runnerUp, double margin)
        => runnerUp > best && runnerUp - best >= margin;

    private static (int Handle, double Best, double RunnerUp) Nearest(IEnumerable<(int Handle, double Distance)> options)
    {
        var handle = -1;
        var best = double.PositiveInfinity;
        var runnerUp = double.PositiveInfinity;
        foreach (var (candidate, distance) in options)
        {
            if (distance < best)
            {
                runnerUp = best;
                best = distance;
                handle = candidate;
            }
            else if (distance < runnerUp)
            {
                runnerUp = distance;
            }
        }

        return (handle, best, runnerUp);
    }
}

// ── OWNERSHIP PROOF: how strong the evidence behind ONE slot→mesh claim is ───────────────────────────

/// <summary>
/// The provenance of a single slot→mesh claim, and whether that provenance is a POSITIVE proof that the mesh is
/// this rig's geometry for this slot.
///
/// <para>WHY A PROOF AND NOT A MESH COUNT. The admission rule used to read <c>foreignMeshes == 0</c> — no
/// validated mesh inside the RID bracket left unclaimed — as a proxy for "every claim is sound". It is a bad
/// proxy in a live process, where other rigs and VFX mint meshes continuously, and a measured counter-example
/// exists: an Ironclad at <c>anim=attack</c> baked <c>complete=true</c> with all 44 drawable slots associated and
/// 8 unclaimed meshes in the bracket, and was thrown away. The 8 were the rig's OWN geometry, minted by the
/// offline instance for attachments that are not drawable at the sampled pose. Nothing about them could have
/// corrupted a claim, and the artifact was correct.</para>
///
/// <para>THE QUESTION THE PROXY WAS REALLY ASKING is per claim, not per bake: could THIS pairing have gone to a
/// mesh that is not ours? Two provenances answer no on their own terms:</para>
/// <list type="bullet">
/// <item><description>A COLOUR FLIP is a measurement on the running skeleton — this slot's colour was nudged and
/// exactly this mesh's vertex colours moved. Geometry that does not belong to this skeleton cannot respond to
/// it, so the pairing is proven regardless of what else sits in the bracket.</description></item>
/// <item><description>An atlas match at <see cref="Sts2SpineGeoClipAtlas.MatchExact"/> — the mesh's uv box
/// coincides, within the containment tolerance on every corner, with the region this slot's attachment NAMES.
/// A foreign mesh samples a foreign atlas; sub-pixel agreement with our named region is an identification.
/// </description></item>
/// </list>
///
/// <para>AND THE TWO THAT DO NOT. A bare <see cref="Sts2SpineGeoClipAtlas.MatchContainment"/> accepts a uv box
/// that merely LANDS inside the named region (uniquely, or by score margin) — which is exactly what a foreign
/// mesh that happens to sit in one of our regions would satisfy. And elimination is, by construction, a claim
/// over the leftover pool: "one slot and one mesh remain, so they pair". That is only sound when the pool is
/// trusted, and a bracket with unclaimed leftovers is precisely the untrusted case.</para>
///
/// <para>COLOUR-FLIP + ATLAS is graded on its atlas half. The co-moving set is provably ours (every member
/// responded to our nudge), but WHICH member the atlas hands back is only as good as the atlas match, and a
/// loose pick inside a set of co-movers is the mis-pairing the atlas is not entitled to make. Conservative by
/// design, and free in practice: every bake measured so far reports zero of them.</para>
///
/// <para>ATLAS + SLOT-COLOUR is graded on its atlas half too, and for a reason worth stating rather than
/// inheriting. That arm (<see cref="Sts2SpineGeoClipSlotColorIdentity"/>) never widens a candidate pool: it
/// chooses inside a set the atlas has already narrowed to a tie, every member of which matched the region this
/// slot's attachment NAMES. So the ownership question — could this claim have gone to a mesh that is not ours? —
/// is answered by that atlas match and by nothing the tie-break does, exactly as for a bare
/// <c>atlas:</c> claim, and the composite is therefore neither stronger nor weaker than the match method it
/// carries. What the tie-break answers is a DIFFERENT question, which of our own slots the mesh belongs to, and
/// it answers it by a measurement on the running skeleton (this slot's colour at the sampled pose against the
/// vertex colours the renderer wrote) rather than by an ordering. It fails closed — no unique mutual-best inside
/// the tolerance leaves every slot in the group unassociated — so grading it on the atlas half cannot admit a
/// bake whose pairing went unmeasured. That is what separates it from elimination, which asserts a pairing from
/// the ABSENCE of alternatives over a pool it does not trust; this asserts one from a positive agreement between
/// two independently read quantities, and refuses when the agreement is not unique.</para>
/// </summary>
internal static class Sts2SpineGeoClipOwnership
{
    /// <summary>Kill switch. <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c> restores the bare foreign-mesh arm.</summary>
    internal const string OwnershipProofEnv = "SPIRECTL_SPINE_GEOCLIP_OWNERSHIP_PROOF";

    /// <summary>A claim measured by nudging this slot's colour and watching exactly one mesh respond.</summary>
    internal const string ClaimColorFlip = "color-flip";

    /// <summary>A claim forced by the last-slot/last-mesh leftover rule.</summary>
    internal const string ClaimElimination = "elimination";

    /// <summary>The prefix of a claim the atlas made on its own, completed by the match method.</summary>
    internal const string ClaimAtlasPrefix = "atlas:";

    /// <summary>The prefix of a claim the colour probe narrowed and the atlas then picked from.</summary>
    internal const string ClaimColorFlipAtlasPrefix = "color-flip+atlas:";

    /// <summary>
    /// The prefix of a claim the atlas narrowed to a tie and the SLOT'S OWN COLOUR at the sampled pose then
    /// picked from — see <see cref="Sts2SpineGeoClipSlotColorIdentity"/>. Completed by the atlas match method,
    /// and graded on it, for the reason spelled out in this class's summary.
    /// </summary>
    internal const string ClaimAtlasSlotColorPrefix = "atlas+slot-color:";

    /// <summary>
    /// The prefix of a claim the atlas narrowed to a tie and a MINT MARK then picked from — a distinguishing
    /// colour this baker wrote to a named slot before the headless re-mint, which the freshly created surface
    /// carries (see <see cref="Sts2SpineGeoClipHeadlessMintMark"/>). Completed by the atlas match method and
    /// graded on it, for the same reason as <see cref="ClaimAtlasSlotColorPrefix"/>: the mark answers WHICH OF
    /// OUR SLOTS owns a mesh the atlas already narrowed to our own regions, never whether the mesh is ours. It is
    /// if anything the stronger of the two — the slot side is a value this process wrote rather than one it read
    /// — and it runs the identical mutual-best-with-margin rule, so it fails closed in the same way.
    /// </summary>
    internal const string ClaimAtlasMintMarkPrefix = "atlas+mint-mark:";

    /// <summary>What is recorded when a claim exists but nothing said how it was made — never a proof.</summary>
    internal const string ClaimUnknown = "unknown";

    internal static string AtlasClaim(string? matchMethod)
        => ClaimAtlasPrefix + Method(matchMethod);

    internal static string ColorFlipAtlasClaim(string? matchMethod)
        => ClaimColorFlipAtlasPrefix + Method(matchMethod);

    internal static string AtlasSlotColorClaim(string? matchMethod)
        => ClaimAtlasSlotColorPrefix + Method(matchMethod);

    internal static string AtlasMintMarkClaim(string? matchMethod)
        => ClaimAtlasMintMarkPrefix + Method(matchMethod);

    /// <summary>
    /// Whether <paramref name="claim"/> positively proves the pairing it stands for. Fails CLOSED: an
    /// unrecognised, absent or blank provenance is not a proof, so a claim minted by a path that forgot to say
    /// how it was made can only ever make a bake stricter.
    /// </summary>
    internal static bool IsPositiveProof(string? claim)
    {
        if (string.IsNullOrWhiteSpace(claim))
        {
            return false;
        }

        var trimmed = claim.Trim();
        if (string.Equals(trimmed, ClaimColorFlip, StringComparison.Ordinal))
        {
            return true;
        }

        // The atlas arms are graded on their METHOD, never on which arm invoked them.
        return (trimmed.StartsWith(ClaimAtlasPrefix, StringComparison.Ordinal)
                || trimmed.StartsWith(ClaimColorFlipAtlasPrefix, StringComparison.Ordinal)
                || trimmed.StartsWith(ClaimAtlasSlotColorPrefix, StringComparison.Ordinal)
                || trimmed.StartsWith(ClaimAtlasMintMarkPrefix, StringComparison.Ordinal))
            && trimmed.EndsWith(':' + Sts2SpineGeoClipAtlas.MatchExact, StringComparison.Ordinal);
    }

    /// <summary>A compact <c>kind=count</c> roll-up for a log line and the manifest, busiest kind first.</summary>
    internal static string Summarise(IEnumerable<string> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            var kind = string.IsNullOrWhiteSpace(claim) ? ClaimUnknown : claim.Trim();
            counts[kind] = counts.TryGetValue(kind, out var seen) ? seen + 1 : 1;
        }

        return string.Join(
            " ",
            counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
    }

    internal static bool ParseArmed(string? raw)
        => (raw ?? string.Empty).Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");

    internal static bool ArmedFromEnvironment()
        => ParseArmed(Environment.GetEnvironmentVariable(OwnershipProofEnv));

    private static string Method(string? matchMethod)
        => string.IsNullOrWhiteSpace(matchMethod) ? ClaimUnknown : matchMethod.Trim();
}

// ── The on-demand REQUEST lane ───────────────────────────────────────────────────────────────────────

/// <summary>
/// What one bake produced, flattened out of the live baker so the request lane's result mapping is pure. The
/// ENV lane throws this away (it logs and moves to the next target); the request lane returns it.
/// </summary>
internal sealed record GeoClipBakeOutcome(
    bool Success,
    string? ManifestPath,
    IReadOnlyList<string> PageFileNames,
    int PartCount,
    int FrameCount,
    double SampleTimeSeconds,
    string SampleTimeSource,
    double ElapsedMs,
    int Slots,
    int SlotsVisible,
    int Associated,
    int Unassociated,
    int ForeignMeshes,
    bool Complete,
    string? FailureReason,
    // WHICH ANIMATION this is the outcome for. Redundant on a single-target bake and load-bearing on a rig bake,
    // where one call answers for N of them and the caller adopts each under its own key.
    string AnimationName = "",
    int StaleMeshFrames = 0,
    int AttachmentDriftSlots = 0,
    // False when this pose was re-baked in its own scene load because the rig-wide association did not hold.
    bool Batched = false,
    // How many of THIS pose's claims carry a positive ownership proof, and how many do not — see
    // Sts2SpineGeoClipOwnership. Their sum is the claim count; zero for both means "this path recorded no
    // provenance", which the admission rule reads as no evidence rather than as a clean bake.
    int ClaimsProven = 0,
    int ClaimsUnproven = 0,
    string ClaimProofNote = "",
    // The RID sweep's own two numbers, carried out of the report so the refusal protocol can tell an acquisition
    // that came up short (retryable — the plan did not cover the rig's meshes) from a verdict about the rig.
    // See Sts2SpineGeoClipRequestLane.IsRetryableAcquisitionShortfall.
    int MeshesValidated = 0,
    bool SweepTruncated = false)
{
    /// <summary>A bake that never got as far as producing counters; <paramref name="reason"/> says why.</summary>
    internal static GeoClipBakeOutcome Failed(
        string reason,
        double sampleTimeSeconds = 0d,
        string sampleTimeSource = "",
        string animationName = "")
        => new(
            Success: false,
            ManifestPath: null,
            PageFileNames: [],
            PartCount: 0,
            FrameCount: 0,
            sampleTimeSeconds,
            sampleTimeSource,
            ElapsedMs: 0d,
            Slots: 0,
            SlotsVisible: 0,
            Associated: 0,
            Unassociated: 0,
            ForeignMeshes: 0,
            Complete: false,
            reason,
            animationName);
}

/// <summary>
/// What ONE rig bake produced: an outcome per requested animation, plus how many scene loads it actually cost.
/// </summary>
/// <param name="ScenesLoaded">
/// 1 when the batch carried every pose. Higher when poses fell back — and equal to <c>Poses.Count</c> when every
/// one did, which is exactly what N separate requests cost. The amortisation is this number, not a timing.
/// </param>
/// <param name="ElapsedMs">
/// The WHOLE rig's wall time, summed across every pass it took — not one pose's. A per-pose figure would be the
/// time up to that pose within a shared pass, which understates what the rig cost and cannot be divided to get
/// the amortized number this round is graded on.
/// </param>
internal sealed record GeoClipRigBakeOutcome(
    IReadOnlyList<GeoClipBakeOutcome> Poses,
    int ScenesLoaded,
    string Note,
    double ElapsedMs = 0d);

/// <summary>
/// The ON-DEMAND lane's planning half: turn a <see cref="SpineGeoClipBakeRequestSnapshot"/> into the same
/// <see cref="GeoClipConfig"/> the env lane builds, so both lanes drive ONE bake implementation.
///
/// <para>THE POINT OF THIS BEING PURE. The property that matters about this lane is a NEGATIVE — it must never
/// consult <c>Sts2OneShotArmClaim</c>. An env-armed one-shot and an on-demand request are different things: the
/// claim exists because ONE environment variable reaches TWO copies of this runtime in one process, and a
/// request has no such ambiguity (it names its own output directory and arrives on a caller's thread). If the
/// request lane claimed, an armed host would refuse every request for the life of the process; if it stood down,
/// it would refuse them silently. Both are worse than doing nothing at all. Planning here, with no claim in
/// sight, is what lets a test prove that while the env lane genuinely holds the claim.</para>
/// </summary>
internal static class Sts2SpineGeoClipRequestLane
{
    /// <summary>The field a rejected request was rejected ON, for the structured error's <c>Field</c>.</summary>
    internal const string FieldScene = "sceneResPath";

    internal const string FieldAnimation = "animationName";

    internal const string FieldOutputDirectory = "outputDirectory";

    // A request bakes exactly ONE target and returns immediately, so the env lane's scene-settle wait and start
    // delay do not apply: the caller only asks once the game is up, and the extraction gate on the host side has
    // already admitted it. Kept as named constants rather than zeros so the intent is legible in the config dump.
    private const int RequestStartDelaySeconds = 0;

    private const int RequestWaitSeconds = 0;

    /// <summary>
    /// Plan the bake, or return null and name the field that was missing. Validation is deliberately shallow —
    /// presence, not plausibility — because everything else (does the scene exist, does it hold a spine node,
    /// does the skeleton have that animation) is answered by the loader with a far better message than a
    /// pre-flight guess could produce.
    /// </summary>
    /// <param name="foreignCandidateDiagnosticsLimit">
    /// The log-only foreign-candidate lever, PASSED IN rather than read here. Both lanes drive one bake, so a
    /// diagnostic that only the env lane could turn on made the lane that actually serves <c>/geoclips/</c>
    /// unable to explain its own refusals — a live round had to construct a separate env-lane bake shaped like
    /// the route to get candidate provenance at all. It is a parameter, trailing and optional, because this
    /// method's purity is the invariant that lets a test prove the request lane never consults
    /// <c>Sts2OneShotArmClaim</c>; reading an environment variable here would spend that to save a caller one
    /// line. The caller resolves it from the same variable, parse and cap the env lane uses. Zero is off, which
    /// is what every caller that does not care keeps getting.
    /// </param>
    internal static GeoClipConfig? PlanConfig(
        SpineGeoClipBakeRequestSnapshot request,
        string owner,
        out string? rejectedField,
        int foreignCandidateDiagnosticsLimit = 0)
    {
        if (string.IsNullOrWhiteSpace(request.SceneResPath))
        {
            rejectedField = FieldScene;
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.AnimationName))
        {
            rejectedField = FieldAnimation;
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            rejectedField = FieldOutputDirectory;
            return null;
        }

        rejectedField = null;
        var scene = request.SceneResPath.Trim();
        var node = string.IsNullOrWhiteSpace(request.NodePath) ? null : request.NodePath.Trim();
        var anim = request.AnimationName.Trim();
        var sampleTime = request.SampleTimeSeconds is { } seconds
            && seconds >= 0d
            && !double.IsNaN(seconds)
            && !double.IsInfinity(seconds)
                ? seconds
                : (double?)null;

        // `Raw` is the spec string this request WOULD have been written as on the env lane. It is what every log
        // line in the baker labels the target with, so making it round-trip keeps one vocabulary across both
        // lanes instead of printing "<request>" and leaving a reader to reconstruct it.
        var raw = $"{scene}?anim={anim}"
            + (node is null ? string.Empty : $"&node={node}")
            + (request.PoseOnly ? "&pose=1" : string.Empty)
            + (sampleTime is { } t ? $"&t={t.ToString("0.#####", CultureInfo.InvariantCulture)}" : string.Empty);

        // ONE TARGET PER ANIMATION, all sharing (scene, node) — which is what makes a rig bake expressible in the
        // config shape both lanes already use, rather than a second parallel notion of "what to bake".
        var animations = Sts2SpineGeoClipBatch.NormalizeAnimations(anim, request.AnimationNames);
        var targets = animations
            .Select(name => new GeoClipTarget(
                scene,
                node,
                name,
                $"{scene}?anim={name}"
                    + (node is null ? string.Empty : $"&node={node}")
                    + (request.PoseOnly ? "&pose=1" : string.Empty)
                    + (sampleTime is { } each ? $"&t={each.ToString("0.#####", CultureInfo.InvariantCulture)}" : string.Empty),
                request.PoseOnly,
                sampleTime))
            .ToArray();
        return new GeoClipConfig(
            owner,
            raw,
            request.OutputDirectory.Trim(),
            targets,
            Math.Clamp(request.Fps, Sts2SpineGeoClipSpec.MinFps, Sts2SpineGeoClipSpec.MaxFps),
            RequestStartDelaySeconds,
            CandidateCap: 50_000,
            IndexSlack: 64,
            MaxFrames: Math.Clamp(request.MaxFrames ?? Sts2SpineGeoClipMath.DefaultMaxFrames, 1, 100_000),
            RequestWaitSeconds,
            WindowBCap: null,
            StrictMeshFilter: true,
            request.PoseOnly,
            sampleTime,
            // The SAME default the env lane takes when its variable is unset — see DenseSweepOnlyDefault. A
            // literal here is how the two lanes drifted the first time.
            DenseSweepOnly: Sts2SpineGeoClipWalk.DenseSweepOnlyDefault,
            KnownPageContentIds: request.KnownPageContentIds,
            ForeignCandidateDiagnosticsLimit: foreignCandidateDiagnosticsLimit,
            MaxOutputBytes: request.MaxOutputBytes);
    }

    /// <summary>The <c>Note</c> a rig outcome carries when <see cref="RefuseWholeClipOnDummyBackend"/> stopped it.</summary>
    internal const string WholeClipOnDummyBackendNote = "refused-whole-clip-dummy-backend";

    /// <summary>
    /// The frame-plan guard for THIS lane: a whole-clip bake on a backend that cannot write a mesh in place is
    /// refused before any engine work, as a rig outcome whose every pose FAILED. Null means carry on.
    /// </summary>
    /// <remarks>
    /// <para>WHY IT IS A FAILURE AND NOT AN INCOMPLETENESS REFUSAL, which is the only real design choice here.
    /// A refusal is a verdict about the RIG and is remembered: spirectl memoises it in-process and couch writes a
    /// durable receipt under its refusal-policy revision. This is a verdict about THIS PROCESS'S RENDERER, and
    /// the store key it would be filed under does not carry the frame count — so a remembered whole-clip refusal
    /// would answer the ordinary single-pose request too, on that host, for ever. A failed bake is remembered by
    /// neither side (<see cref="RefusalVerdictFor"/> returns <c>Adoptable</c> for <c>Success == false</c>, and
    /// couch's <c>!outcome.Success</c> arm returns before its receipt is written), which is exactly right: ask
    /// for one pose, or ask a host with a real renderer, and the same key still bakes.</para>
    /// <para>EVERY pose is failed, not just the primary: <see cref="ToSnapshot(GeoClipRigBakeOutcome)"/> reports
    /// the primary at the top level and the rest in <c>Poses</c>, and couch's rig lane grades each pose on its
    /// own. One adoptable pose in the list would be adopted.</para>
    /// <para>The backend answer is PASSED IN. This method is compiled into the Godot-free build and is the unit
    /// under test; the three engine reads that produce <paramref name="dummyRenderer"/> live in the baker, on the
    /// main thread, exactly where the re-mint's own reads already are.</para>
    /// </remarks>
    internal static GeoClipRigBakeOutcome? RefuseWholeClipOnDummyBackend(GeoClipConfig config, bool dummyRenderer)
    {
        ArgumentNullException.ThrowIfNull(config);

        // THE SINGLE-POSE LANE NEVER REACHES THE PREDICATE. Every shipped `/geoclips/` bake is pose-only, and
        // that path is verified byte-identical to a real renderer's; this returns before it can be touched.
        if (config.Targets.Count == 0 || config.Targets.All(config.IsPoseOnly))
        {
            return null;
        }

        var reason = Sts2SpineGeoClipHeadlessRemint.WholeClipRefusal(
            dummyRenderer,
            poseOnly: false,
            config.MaxFrames);
        return reason is null
            ? null
            : new GeoClipRigBakeOutcome(
                [
                    .. config.Targets.Select(target => GeoClipBakeOutcome.Failed(
                        reason,
                        animationName: target.Animation ?? string.Empty)),
                ],
                ScenesLoaded: 0,
                WholeClipOnDummyBackendNote);
    }

    /// <summary>
    /// Map a completed rig bake onto the public result snapshot: the PRIMARY animation at the top level (which
    /// is what every caller predating batching reads), and every animation in <c>Poses</c>.
    /// </summary>
    internal static SpineGeoClipBakeResultSnapshot ToSnapshot(GeoClipRigBakeOutcome rig)
    {
        if (rig.Poses.Count == 0)
        {
            return SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                "the geoclip bake produced no pose at all.",
                [new AssetExtractDetail("bake", "empty", "The rig bake returned no per-animation outcome.")]);
        }

        var primary = ToSnapshot(rig.Poses[0]);
        return primary with
        {
            // The RIG's wall time, not the primary pose's — see GeoClipRigBakeOutcome.ElapsedMs.
            ElapsedMs = rig.ElapsedMs > 0d ? rig.ElapsedMs : primary.ElapsedMs,
            Poses = [.. rig.Poses.Select(ToPoseSnapshot)],
            ScenesLoaded = rig.ScenesLoaded,
            BatchNote = rig.Note,
        };
    }

    /// <summary>Map a completed bake onto the public result snapshot.</summary>
    internal static SpineGeoClipBakeResultSnapshot ToSnapshot(GeoClipBakeOutcome outcome)
        => outcome.Success
            ? new SpineGeoClipBakeResultSnapshot(
                Success: true,
                outcome.ManifestPath,
                outcome.PageFileNames,
                outcome.PartCount,
                outcome.FrameCount,
                outcome.SampleTimeSeconds,
                outcome.SampleTimeSource,
                outcome.ElapsedMs,
                outcome.Slots,
                outcome.SlotsVisible,
                outcome.Associated,
                outcome.Unassociated,
                outcome.ForeignMeshes,
                outcome.Complete,
                Error: null,
                Poses: [ToPoseSnapshot(outcome)],
                ClaimsProven: outcome.ClaimsProven,
                ClaimsUnproven: outcome.ClaimsUnproven,
                MeshesValidated: outcome.MeshesValidated,
                SweepTruncated: outcome.SweepTruncated)
            : SpineGeoClipBakeResultSnapshot.Failure(
                AssetExtractFailureCode.RuntimeFailure,
                outcome.FailureReason ?? "the geoclip bake did not complete.",
                [
                    new AssetExtractDetail(
                        Field: "bake",
                        Value: "incomplete",
                        Note: outcome.FailureReason
                            ?? "The baker reported no reason; check the host log for SPINE_GEOCLIP lines."),
                ]) with
            {
                Poses = [ToPoseSnapshot(outcome)],
            };

    // ── The INCOMPLETENESS (refusal) rule ────────────────────────────────────────────────────────

    /// <summary>The arm tokens <see cref="ClassifyRefusal"/> buckets a refusal into.</summary>
    /// <remarks>
    /// Stable vocabulary, not free text: they are what a caller groups refusals by and what the refusal memo
    /// stores, so they have to survive a reworded reason string.
    /// </remarks>
    internal const string RefusalArmIncomplete = "incomplete";

    internal const string RefusalArmUnassociated = "unassociated";

    internal const string RefusalArmForeign = "foreign";

    /// <summary>
    /// The arm that REPLACED the bare foreign-mesh one: unclaimed geometry in the bracket next to at least one
    /// claim that carries no positive ownership proof. See <see cref="Sts2SpineGeoClipOwnership"/>.
    /// </summary>
    internal const string RefusalArmOwnership = "ownership";

    internal const string RefusalArmOther = "other";

    /// <summary>
    /// Whether a bake that RAN nonetheless failed to acquire the whole rig, and the detail that says how. Null
    /// means the bake is whole and may be adopted.
    /// </summary>
    /// <remarks>
    /// <para>WHY THE PRODUCER OWNS THIS. Every embedder that caches geoclip artifacts has to answer the same
    /// question before it commits one — a bake reports <c>Success</c> for "it ran and wrote a manifest", which is
    /// not the same as "it acquired every visible slot" — and each one answering it from the raw counters is a
    /// second copy of a rule that decides what goes into a content-addressed store. Two copies that drift admit an
    /// artifact one of them would have refused, on a store that never re-decides. So the rule lives beside the
    /// counters it reads.</para>
    /// <para>The arms are ordered by how much they explain: an incomplete bake's association numbers are not
    /// evidence about anything, and an unassociated slot makes a foreign-mesh count unreadable, so the first arm
    /// that fires is the one worth reporting.</para>
    /// <para>THE THIRD ARM IS OWNERSHIP, not a mesh count. Because the two arms above it have already fired,
    /// the third is only ever reached by a bake that COMPLETED with every drawable slot associated — so the
    /// question it is really asking is whether those claims are sound, and unclaimed leftovers were only ever a
    /// proxy for it. A measured counter-example retired the proxy (see <see cref="Sts2SpineGeoClipOwnership"/>):
    /// a complete, fully associated Ironclad bake was discarded over 8 leftovers that were its own geometry.
    /// The arm now fires when the bracket holds leftovers AND at least one claim rests on something weaker than
    /// a colour flip or an exact atlas match — i.e. when contamination could actually have corrupted a claim.
    /// Leftovers alone no longer sink an otherwise perfect bake; they stay a first-class reported counter.</para>
    /// <para>IT NEVER ADMITS MORE THAN IT CAN SEE. A caller with no per-claim provenance (an older receipt, a
    /// seam that does not carry the counters yet) reaches the legacy foreign arm unchanged, because
    /// <c>claimsProven + claimsUnproven == 0</c> is missing evidence rather than clean evidence.</para>
    /// </remarks>
    internal static string? IncompletenessReason(GeoClipBakeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return IncompletenessReason(
            outcome.Complete,
            outcome.Associated,
            outcome.SlotsVisible,
            outcome.ForeignMeshes,
            outcome.ClaimsProven,
            outcome.ClaimsUnproven);
    }

    /// <summary>
    /// The same verdict for ONE pose of a rig bake. The same function, deliberately not a second copy: a batch
    /// that graded its poses by a slightly different rule than a single bake would let a pose through that the
    /// on-demand path would have refused.
    /// </summary>
    internal static string? IncompletenessReason(SpineGeoClipBakePoseSnapshot pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return IncompletenessReason(
            pose.Complete,
            pose.Associated,
            pose.SlotsVisible,
            pose.ForeignMeshes,
            pose.ClaimsProven,
            pose.ClaimsUnproven);
    }

    /// <summary>
    /// The rule, on raw counters. The <paramref name="claimsProven"/> / <paramref name="claimsUnproven"/> pair
    /// defaults to "no provenance recorded", which reproduces the pre-ownership behaviour exactly — so every
    /// caller that has not been taught to carry them keeps the verdict it had.
    /// </summary>
    /// <param name="ownershipProofArmed">
    /// Null reads <see cref="Sts2SpineGeoClipOwnership.OwnershipProofEnv"/>. Passed explicitly by tests, and by
    /// anything that has already resolved the lever, so the rule itself stays a pure function of its arguments.
    /// </param>
    internal static string? IncompletenessReason(
        bool complete,
        int associated,
        int slotsVisible,
        int foreignMeshes,
        int claimsProven = 0,
        int claimsUnproven = 0,
        bool? ownershipProofArmed = null)
    {
        if (!complete)
        {
            return "complete=false";
        }

        if (associated < slotsVisible)
        {
            return $"associated={associated} of slotsEverVisible={slotsVisible}";
        }

        if (foreignMeshes <= 0)
        {
            // Nothing unclaimed in the bracket. That is what the third arm has always been about, and no
            // ownership question arises: the claims exhaust the validated mesh pool.
            return null;
        }

        var claims = claimsProven + claimsUnproven;
        if (claims <= 0 || !(ownershipProofArmed ?? Sts2SpineGeoClipOwnership.ArmedFromEnvironment()))
        {
            // No provenance to grade, or the kill switch is down: the bare leftover count, exactly as before.
            return $"foreignMeshes={foreignMeshes}";
        }

        return claimsUnproven > 0 ? $"ownership={claimsUnproven} of claimed={claims}" : null;
    }

    /// <summary>
    /// Bucket a reason string from <see cref="IncompletenessReason"/> into one of the stable arms.
    /// </summary>
    /// <remarks>
    /// The reasons carry COUNTS (<c>associated=40 of slotsEverVisible=44</c>), so the raw text is unbucketable —
    /// one distinct value per rig. What is stable is WHICH ARM fired, which is the text before the first
    /// <c>=</c>; that token is then renamed to a reader's vocabulary rather than the guard's field names. The full
    /// detail is kept beside the bucket everywhere it is stored or reported, so bucketing here loses nothing.
    /// </remarks>
    internal static string ClassifyRefusal(string? reasonDetail)
    {
        if (string.IsNullOrWhiteSpace(reasonDetail))
        {
            return RefusalArmOther;
        }

        var equals = reasonDetail.IndexOf('=', StringComparison.Ordinal);
        return (equals > 0 ? reasonDetail[..equals] : reasonDetail).Trim() switch
        {
            "complete" => RefusalArmIncomplete,
            "associated" => RefusalArmUnassociated,
            "foreignMeshes" => RefusalArmForeign,
            "ownership" => RefusalArmOwnership,
            _ => RefusalArmOther,
        };
    }

    // ── RETRYABLE ACQUISITION FAULTS ─────────────────────────────────────────────────────────────────
    //
    // A refusal is normally EVIDENCE ABOUT THE RIG: this skeleton's slots could not all be tied to geometry, or
    // its claims are not sound, and re-running the same bake reaches the same verdict. That is what makes the
    // refusal memo (and a host's durable receipt behind it) worth having at all.
    //
    // ONE SHAPE IS NOT LIKE THAT. When the sweep validated FEWER MESHES THAN THERE ARE SLOTS and no window hit
    // its candidate cap, the bake has diagnosed itself: the plan it made did not cover the rig's own meshes, and
    // it completed that plan. Window A's index axis is inferred from two bracket sentinels, and that inference
    // is sound only while Godot's RID allocator is bump-allocating; a creature death anywhere earlier in the
    // session populates its free list, the rig's meshes land at recycled indices outside the hull, and they are
    // never probed. Measured (round WS-I): the SAME four rigs bake 4/4 complete on a quiet room and 1/4 after
    // two kills, in virgin processes, with `associated == meshesValidated` exactly in every shortfall.
    //
    // So it is a property of ALLOCATOR STATE AT ONE INSTANT, not of the rig, and pinning it is pinning noise.
    // Today one unlucky bake refuses the identity for the life of the process — and, through couch-coop's
    // durable receipt, for the life of the install. The index-floor recovery strip above closes most of the
    // cause; this closes the consequence, because "most" is not "all" and the next allocator surprise should
    // cost a re-bake rather than a permanently missing creature.

    /// <summary>
    /// KILL SWITCH: <c>0</c> restores the old behaviour, where an acquisition shortfall is memoised like any
    /// other refusal. Kept because "which refusals stick" is exactly the kind of rule whose failure mode is a
    /// caller re-paying a 1.45 s median bake in a loop, and that has to be switchable without a build.
    /// </summary>
    internal const string RetryableShortfallEnv = "SPIRECTL_SPINE_GEOCLIP_RETRYABLE_SHORTFALL";

    internal const bool RetryableShortfallDefault = true;

    internal static bool ResolveRetryableShortfall(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        return value.Length == 0
            ? RetryableShortfallDefault
            : value is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }

    internal static bool RetryableShortfallArmed()
        => ResolveRetryableShortfall(Environment.GetEnvironmentVariable(RetryableShortfallEnv));

    /// <summary>
    /// Whether a refused bake's own counters say the refusal is an ACQUISITION fault that a later bake of the
    /// same identity could pass.
    /// </summary>
    /// <remarks>
    /// <para>BOTH CLAUSES ARE LOAD-BEARING. <c>truncated</c> means a window swept a subset of its own plan
    /// because it hit the candidate cap — the sweep did not finish, so its shortfall says nothing about whether
    /// the plan was right, and re-running it under the same cap reproduces it exactly. That is a configuration
    /// verdict, and it sticks. Only an UNTRUNCATED shortfall is the "my plan missed" shape.</para>
    /// <para>It is deliberately silent about the OTHER refusal arms. A bake that acquired every mesh and then
    /// failed on association, leftovers or ownership is a verdict about the rig and stays sticky; a caller that
    /// wants one of those re-run has <c>IgnoreRefusalMemo</c>, which is what that flag is for.</para>
    /// <para>A caller with no counters (<c>meshesValidated == 0</c> on a result that predates them, a memo
    /// replay) reads as NOT retryable, because zero is missing evidence rather than a measured shortfall — the
    /// same asymmetry the ownership arm takes on <c>claimsProven + claimsUnproven == 0</c>.</para>
    /// </remarks>
    internal static bool IsRetryableAcquisitionShortfall(
        int meshesValidated,
        int slotsVisible,
        bool truncated)
        => meshesValidated > 0 && !truncated && meshesValidated < slotsVisible;

    /// <summary>What <c>BatchNote</c> carries on a result the refusal memo answered instead of baking.</summary>
    internal const string RefusedFromMemoNote = "refused-memo";

    /// <summary>
    /// The refusal that applies to a WHOLE result, or null when the result holds something a caller may adopt.
    /// </summary>
    /// <remarks>
    /// <para>Two guards, both about not memoising more than was measured. A result that did not SUCCEED is not a
    /// completeness refusal at all — the bake never got as far as counters (no scene, no spine node, a throw),
    /// and those are the failures most likely to be transient, so they are exactly the ones a memo must not
    /// pin.</para>
    /// <para>And a rig result is refused only when NO pose in it is adoptable. One good pose makes the whole
    /// result worth returning, so remembering it as a refusal would throw away work that succeeded.</para>
    /// </remarks>
    internal static string? RefusalReasonFor(SpineGeoClipBakeResultSnapshot result)
        => RefusalVerdictFor(result).Reason;

    /// <summary>
    /// A refusal verdict and the sweep counters it was reached on. <c>Reason</c> null means the result holds
    /// something a caller may adopt.
    /// </summary>
    internal readonly record struct GeoClipRefusalVerdict(
        string? Reason,
        bool Retryable,
        int MeshesValidated,
        int SlotsVisible,
        bool SweepTruncated)
    {
        internal static readonly GeoClipRefusalVerdict Adoptable = new(null, false, 0, 0, false);
    }

    /// <summary>
    /// The verdict, plus whether it is a retryable acquisition fault
    /// (<see cref="IsRetryableAcquisitionShortfall"/>) and the three numbers that decided it.
    /// </summary>
    /// <remarks>
    /// ONE WALK, not two, and the counters travel with the reason: on a rig bake the refusal can come from a
    /// pose that is not the primary, and the top-level fields are the PRIMARY pose's. A flag or a log line built
    /// from the top-level counters would then describe a different pose than the one that was refused.
    /// </remarks>
    internal static GeoClipRefusalVerdict RefusalVerdictFor(SpineGeoClipBakeResultSnapshot result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
        {
            return GeoClipRefusalVerdict.Adoptable;
        }

        if (result.Poses is not { Count: > 0 } poses)
        {
            var topLevel = IncompletenessReason(
                result.Complete,
                result.Associated,
                result.SlotsVisible,
                result.ForeignMeshes,
                result.ClaimsProven,
                result.ClaimsUnproven);
            return topLevel is null
                ? GeoClipRefusalVerdict.Adoptable
                : new GeoClipRefusalVerdict(
                    topLevel,
                    IsRetryableAcquisitionShortfall(
                        result.MeshesValidated, result.SlotsVisible, result.SweepTruncated),
                    result.MeshesValidated,
                    result.SlotsVisible,
                    result.SweepTruncated);
        }

        var first = GeoClipRefusalVerdict.Adoptable;
        foreach (var pose in poses)
        {
            if (!pose.Success)
            {
                continue;
            }

            var reason = IncompletenessReason(pose);
            if (reason is null)
            {
                return GeoClipRefusalVerdict.Adoptable;
            }

            first = first.Reason is null
                ? new GeoClipRefusalVerdict(
                    reason,
                    IsRetryableAcquisitionShortfall(
                        pose.MeshesValidated, pose.SlotsVisible, pose.SweepTruncated),
                    pose.MeshesValidated,
                    pose.SlotsVisible,
                    pose.SweepTruncated)
                : first;
        }

        return first;
    }

    /// <summary>
    /// Stamp a result that WAS baked with the verdict the completeness rule reached, so a caller reads the arm
    /// and the reason from the same two fields whether the bake ran or the memo answered.
    /// </summary>
    internal static SpineGeoClipBakeResultSnapshot WithRefusal(
        SpineGeoClipBakeResultSnapshot result,
        string reason,
        bool retryable = false)
        => result with
        {
            RefusalArm = ClassifyRefusal(reason),
            RefusalReason = reason,
            RefusedFromMemo = false,
            RefusalRetryable = retryable,
        };

    /// <summary>
    /// The whole refusal-memo protocol around ONE bake: consult, run only if the answer is not already known,
    /// classify what came back, and remember it when it was a refusal.
    /// </summary>
    /// <remarks>
    /// <para>Godot-free and takes the bake as a DELEGATE so the protocol is the unit under test: whether a second
    /// identical request re-runs the bake is the entire behaviour, and it cannot be asserted through a lane that
    /// needs an engine to reach it.</para>
    /// <para>The consult happens BEFORE the delegate is touched — before planning, before the output directory,
    /// before the hop onto the game's main thread — because the point is to spend nothing at all on a verdict
    /// that is already known.</para>
    /// </remarks>
    internal static SpineGeoClipBakeResultSnapshot BakeWithRefusalMemo(
        SpineGeoClipBakeRequestSnapshot request,
        Sts2SpineGeoClipRefusalMemo memo,
        string leverSignature,
        string bridgeVersion,
        Func<SpineGeoClipBakeResultSnapshot> bake,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        Func<bool>? retryableShortfallArmed = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(bake);

        var now = clock ?? (() => DateTimeOffset.UtcNow);
        var retryableArmed = retryableShortfallArmed ?? RetryableShortfallArmed;
        var key = Sts2SpineGeoClipRefusalKey.From(request, leverSignature, bridgeVersion);
        if (!request.IgnoreRefusalMemo && memo.Lookup(key, now()) is { } remembered)
        {
            log?.Invoke(
                $"refused from memo (attempt {remembered.Attempts}) arm={remembered.Arm} {remembered.Reason} "
                + $"request={key}");
            return RefusedFromMemoResult(key, remembered);
        }

        var result = bake();
        var verdict = RefusalVerdictFor(result);
        if (verdict.Reason is not { } reason)
        {
            return result;
        }

        var arm = ClassifyRefusal(reason);

        // A RETRYABLE ACQUISITION FAULT IS NOT MEMOISED. The memo exists to stop a caller re-paying for a verdict
        // whose answer is already known, and this one's answer is NOT known: the sweep's index plan missed the
        // rig's own meshes at one moment in this process's allocator history, and the very next bake of the same
        // identity can acquire all of them. Pinning it turns a transient into a permanent hole in the catalog.
        // The alternative — memoise and let the caller pass IgnoreRefusalMemo — needs the caller to know which
        // refusals are worth retrying, which is exactly the producer-side knowledge that put IncompletenessReason
        // in this file in the first place.
        if (verdict.Retryable && retryableArmed())
        {
            log?.Invoke(
                $"REFUSED (acquisition shortfall, NOT memoised) arm={arm} {reason} "
                + $"meshesValidated={verdict.MeshesValidated} slotsEverVisible={verdict.SlotsVisible} "
                + $"truncated={(verdict.SweepTruncated ? 1 : 0)} "
                + $"elapsedMs={result.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture)} request={key} "
                + "— the sweep completed its plan and the plan did not cover the rig's own meshes, which is a "
                + "property of this process's RID allocator at that instant rather than of the rig. A later bake "
                + $"of the same identity may succeed. Set {RetryableShortfallEnv}=0 to memoise it anyway.");
            return WithRefusal(result, reason, retryable: true);
        }

        var poses = result.Poses;
        var receipt = memo.Record(
            key,
            arm,
            reason,
            result.Slots,
            result.SlotsVisible,
            result.Associated,
            result.Unassociated,
            result.ForeignMeshes,
            poses is { Count: > 0 } ? poses[0].StaleMeshFrames : 0,
            poses is { Count: > 0 } ? poses[0].AttachmentDriftSlots : 0,
            result.ElapsedMs,
            now(),
            result.ClaimsProven,
            result.ClaimsUnproven);

        log?.Invoke(
            $"REFUSED (incomplete) arm={arm} {reason} "
            + $"elapsedMs={result.ElapsedMs.ToString("0.###", CultureInfo.InvariantCulture)} "
            + $"attempt={receipt.Attempts} request={key}");
        return WithRefusal(result, reason);
    }

    /// <summary>
    /// The O(1) answer for an identity this process has already baked and already refused.
    /// </summary>
    /// <remarks>
    /// It is a FAILURE, not a replay of the incomplete result: the artifact the original bake wrote is not
    /// carried here (no manifest path, no pages), so returning anything that looked adoptable would be a lie
    /// about what this call produced. The counters ARE carried, because they are what the refusal was derived
    /// from and a caller re-deriving its own verdict must be able to see them. <c>ElapsedMs</c> stays 0 — this
    /// call cost nothing, which is the entire point — and what the first one cost is in the details.
    /// </remarks>
    internal static SpineGeoClipBakeResultSnapshot RefusedFromMemoResult(
        Sts2SpineGeoClipRefusalKey key,
        Sts2SpineGeoClipRefusalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var failure = SpineGeoClipBakeResultSnapshot.Failure(
            AssetExtractFailureCode.RuntimeFailure,
            $"the geoclip bake for this request was already refused in this process ({receipt.Reason}); "
                + "it was not re-run.",
            [
                new AssetExtractDetail("refusalArm", receipt.Arm, receipt.Reason),
                new AssetExtractDetail(
                    "refusalAttempts",
                    receipt.Attempts.ToString(CultureInfo.InvariantCulture),
                    "How many times this identity has been refused, memo answers included."),
                new AssetExtractDetail(
                    "refusalFirstAtUtc",
                    receipt.FirstRefusedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    $"The first refusal took {receipt.ElapsedMsFirst.ToString("0.###", CultureInfo.InvariantCulture)}ms."),
                new AssetExtractDetail(
                    "request",
                    key.ToString(),
                    "Set IgnoreRefusalMemo on the request to bake it again anyway."),
            ]);

        return failure with
        {
            Slots = receipt.Slots,
            SlotsVisible = receipt.SlotsVisible,
            Associated = receipt.Associated,
            Unassociated = receipt.Unassociated,
            ForeignMeshes = receipt.ForeignMeshes,
            ClaimsProven = receipt.ClaimsProven,
            ClaimsUnproven = receipt.ClaimsUnproven,
            BatchNote = RefusedFromMemoNote,
            RefusalArm = receipt.Arm,
            RefusalReason = receipt.Reason,
            RefusedFromMemo = true,
        };
    }

    internal static SpineGeoClipBakePoseSnapshot ToPoseSnapshot(GeoClipBakeOutcome outcome)
        => new(
            outcome.AnimationName,
            outcome.Success,
            outcome.ManifestPath,
            outcome.PageFileNames,
            outcome.PartCount,
            outcome.FrameCount,
            outcome.SampleTimeSeconds,
            outcome.SampleTimeSource,
            outcome.Slots,
            outcome.SlotsVisible,
            outcome.Associated,
            outcome.Unassociated,
            outcome.ForeignMeshes,
            outcome.StaleMeshFrames,
            outcome.AttachmentDriftSlots,
            outcome.Complete,
            outcome.Batched,
            outcome.FailureReason,
            outcome.ClaimsProven,
            outcome.ClaimsUnproven,
            outcome.ClaimProofNote,
            outcome.MeshesValidated,
            outcome.SweepTruncated);
}
