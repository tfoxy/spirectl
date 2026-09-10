namespace Spirectl.Sts2.Core.Artifacts;

/// <summary>
/// An ON-DEMAND request to bake one (scene, node, animation) into a <c>geoclip/</c> artifact DIRECTORY: per-part
/// posed triangles, uvs, tints, blend mode and draw order, plus the atlas page PNGs those triangles sample.
///
/// <para>THE BAKE WRITES THE DIRECTORY ITSELF, exactly as the env-armed one-shot lane does — a manifest, a page
/// PNG per atlas page, and (once packed) a vertex blob. The result below carries paths and counts, not bytes:
/// inventing a multi-file payload shape for a producer that already has a filesystem contract would be a second
/// format to version and test for nothing.</para>
///
/// <para>THE ARM CLAIM IS NOT CONSULTED. <c>Sts2OneShotArmClaim</c> guards the ENV lane, where two copies of this
/// runtime in one process would otherwise arm the same one-shot twice. A request is not a one-shot: an
/// env-armed bake and an on-demand request must be able to coexist, so this lane never claims and never stands
/// down. (Concurrency between the two is the main-thread dispatcher's problem, and it is serialised there.)</para>
/// </summary>
/// <param name="SceneResPath">The <c>res://…</c> scene to load offline. Required.</param>
/// <param name="NodePath">
/// The SpineSprite's path within that scene, or null to let the loader find the single spine node.
/// </param>
/// <param name="AnimationName">The animation to pose. Required.</param>
/// <param name="SampleTimeSeconds">
/// An explicit pose time. Null means "let <c>Sts2SpineStillFrame.ChooseSampleTime</c> pick" — the mid point of
/// the clip, or its END for a terminal die/defeat animation. Ignored when <paramref name="PoseOnly"/> is false.
/// </param>
/// <param name="OutputDirectory">The root the artifact directory is created under. Required.</param>
/// <param name="Fps">
/// The nominal sample rate. Recorded in the manifest either way; it only DRIVES the frame clock for a whole-clip
/// bake (<paramref name="PoseOnly"/> false).
/// </param>
/// <param name="MaxFrames">Frame-clock cap for a whole-clip bake; null takes the baker's default.</param>
/// <param name="PoseOnly">
/// DEFAULTS TO TRUE, because this seam exists for the single-pose pipeline: one frame, sampled at the pose the
/// still renderer would have rendered. Pass false for the whole-clip artifact the env lane bakes.
/// </param>
/// <param name="AnimationNames">
/// EVERY animation of this rig to bake in ONE scene load, or null/empty for the single-animation bake this seam
/// started as. <paramref name="AnimationName"/> is always part of the set and always FIRST, so listing it again
/// here is harmless and omitting it changes nothing.
///
/// <para>WHY IT IS ON THE REQUEST rather than a second entry point: a rig bake IS a bake, with the same output
/// contract (one artifact directory per (scene, node, anim), named exactly as before) and the same failure modes.
/// What it saves is the per-SCENE work — 90-96 % of a pose-only bake is the bracket RID sweep plus the slot↔mesh
/// association, both properties of the RIG — which N separate requests pay N times.</para>
///
/// <para>NOT A PROMISE. The baker measures whether the shared association actually holds for each pose
/// (re-minted mesh RIDs, attachment drift) and silently re-bakes any pose it does not, per target. The result's
/// <c>ScenesLoaded</c> and <c>BatchNote</c> say what it did.</para>
/// </param>
/// <param name="KnownPageContentIds">
/// SHA-256 content ids (lowercase hex, as <c>pages[].sha256</c> carries them) of atlas pages the CALLER already
/// holds. The bake still reports those pages in the manifest, with their size and their id, but does not write
/// the PNG — which is the whole of a rig's page cost after its first pose.
///
/// <para>ITS REACH IS BOUNDED BY THE PASSTHROUGH, and honestly so: a page's id is a hash of the bytes, so a page
/// whose SOURCE image cannot be copied has to be re-encoded before anyone can tell whether the caller has it.
/// This skips the WRITE always and the ENCODE only on pages that came through
/// <c>Sts2SpineGeoClipPageSource</c>.</para>
/// </param>
/// <param name="IgnoreRefusalMemo">
/// Bake even when this exact request has already been refused in this process. Default false: a repeat of a
/// request the baker has already run and refused is answered from the refusal memo, in O(1), instead of paying
/// for the same verdict again (a refusal costs what a success costs — seconds).
///
/// <para>THE ESCAPE HATCH, and the reason it is on the request rather than a global switch. The memo's key
/// carries everything the baker's answer is known to depend on (identity, the environment levers, the bridge
/// build), but a fix can land somewhere the key cannot see — game content, a resource pack, a repaired scene —
/// and a memo that could not be bypassed would keep answering with the old verdict for the life of the process.
/// Set this to re-run the bake and re-record whatever it now finds.</para>
///
/// <para>WHAT A MEMO HIT LOOKS LIKE, because it is NOT the shape the first refusal had: an incomplete bake
/// reports <c>Success</c> (it ran, and it wrote a manifest) with <c>Complete</c> false, while a memo hit is an
/// explicit failure carrying <c>BatchNote "refused-memo"</c>. A caller that adopts incomplete artifacts wants
/// this flag; a caller that refuses them — which is why the rule exists — sees the same verdict either way,
/// spelled once as counters and once as a refusal.</para>
/// </param>
/// <param name="MaxOutputBytes">
/// Optional cumulative byte ceiling for every artifact written by this request, across all animations in a rig
/// batch and any per-pose fallbacks. Null keeps the tooling default unlimited. The baker checks complete page and
/// manifest payloads before creating their artifact directory, so exceeding the ceiling leaves no partial pose.
/// </param>
public sealed record SpineGeoClipBakeRequestSnapshot(
    string SceneResPath,
    string? NodePath,
    string AnimationName,
    double? SampleTimeSeconds,
    string OutputDirectory,
    // Mirrors Sts2SpineGeoClipSpec.DefaultFps; spelled literally because a default parameter value must be a
    // compile-time constant and Core must not take a dependency on the Live namespace to get one.
    int Fps = 30,
    int? MaxFrames = null,
    bool PoseOnly = true,
    IReadOnlyList<string>? AnimationNames = null,
    IReadOnlySet<string>? KnownPageContentIds = null,
    bool IgnoreRefusalMemo = false,
    long? MaxOutputBytes = null);

/// <summary>
/// What ONE animation of a rig bake produced. The same counters
/// <see cref="SpineGeoClipBakeResultSnapshot"/> carries for a single-animation bake, per pose — because a batch
/// that reported one merged verdict could not say which pose was refused, and the completeness guard is per pose.
/// </summary>
/// <param name="Batched">
/// False when this pose was re-baked in its OWN scene load because the shared association did not hold for it.
/// The one field that says whether the amortisation actually happened, so a measurement cannot mistake a rig that
/// fell back for one that batched.
/// </param>
/// <param name="StaleMeshFrames">
/// Frames where an ASSOCIATED mesh read back empty. Non-zero on a batched pose is what arms the per-target
/// fallback; it is reported per pose so the reason survives into a log.
/// </param>
/// <param name="AttachmentDriftSlots">
/// Slots that show a DIFFERENT attachment here than at the pose the association was measured at. Non-zero means
/// the rig-wide slot→mesh map is not evidence about this pose, and it was baked per-target instead.
/// </param>
public sealed record SpineGeoClipBakePoseSnapshot(
    string AnimationName,
    bool Success,
    string? ManifestPath,
    IReadOnlyList<string> PageFileNames,
    int PartCount,
    int FrameCount,
    double SampleTimeSeconds,
    string SampleTimeSource,
    int Slots,
    int SlotsVisible,
    int Associated,
    int Unassociated,
    int ForeignMeshes,
    int StaleMeshFrames,
    int AttachmentDriftSlots,
    bool Complete,
    bool Batched,
    string? FailureReason,
    int ClaimsProven = 0,
    int ClaimsUnproven = 0,
    string ClaimProofNote = "",
    // ── The two counters that make an acquisition shortfall self-diagnosing ──────────────────────────
    //
    // How many meshes the RID sweep validated, and whether any window at any tier hit its candidate cap. They are
    // already in the manifest (`bake.meshesValidated` / `bake.truncated`) and they were already the numbers a
    // reader had to fetch off disk to tell an ACQUISITION fault from a verdict about the rig. Carried on the
    // snapshot so the producer's own refusal protocol can tell them apart without re-reading the artifact —
    // `Sts2SpineGeoClipRequestLane.IsRetryableAcquisitionShortfall`.
    int MeshesValidated = 0,
    bool SweepTruncated = false);

/// <summary>
/// What a bake produced. <c>Success</c> false always carries <see cref="Error"/>; a caller never has to parse a
/// message to find out what went wrong.
/// </summary>
/// <param name="ManifestPath">Absolute path to the written <c>manifest.json</c>, or null on failure.</param>
/// <param name="PageFileNames">The page PNG file names written beside it, in page-id order.</param>
/// <param name="SampleTimeSource">
/// Which rule chose <paramref name="SampleTimeSeconds"/>: <c>requested</c> / <c>terminal-end</c> / <c>mid</c> /
/// <c>degenerate</c>, or <c>clip</c> when the bake was not pose-only and no single pose was selected. The tokens
/// are <c>Sts2SpineStillFrame.SampleSource*</c>.
/// </param>
/// <param name="SlotsVisible">
/// How many slots showed an attachment across the SAMPLED frames. For a pose-only bake that is "visible AT THIS
/// POSE", not "visible anywhere in the animation" — a slot with no attachment at the sampled pose has no surface
/// to acquire and contributes nothing, which is correct but narrows what <paramref name="Complete"/> asserts.
/// </param>
/// <param name="Poses">
/// One entry per animation the request named, in request order — ALWAYS populated by a bake that ran, including
/// a single-animation one, where it holds exactly one entry restating the fields above. Null only on a request
/// that was rejected before any bake started.
///
/// <para>Uniform on purpose: a caller that reads <c>Poses</c> needs no special case for N=1, and the top-level
/// fields keep meaning what they always meant (the PRIMARY animation), so nothing that predates batching has to
/// change.</para>
/// </param>
/// <param name="ScenesLoaded">
/// How many times the scene was loaded. 1 is the amortisation working; N means every pose fell back to its own
/// load, which costs exactly what N separate requests cost. THE number to grade a batch on.
/// </param>
/// <param name="BatchNote">
/// <c>single</c> / <c>batched</c> / <c>batched+fallback:&lt;reason&gt;</c>, or <c>refused-memo</c> when the result
/// was answered from the refusal memo rather than baked. Present so a reader never has to infer what happened by
/// comparing counts.
/// </param>
/// <param name="ClaimsProven">
/// How many of this bake's slot→mesh claims carry a POSITIVE ownership proof — a colour-flip response, or an
/// atlas match whose corners coincide with the region the slot's attachment names. Together with
/// <paramref name="ClaimsUnproven"/> this is what the third arm of the completeness rule grades; the two being
/// 0 means the producing path recorded no provenance, which the rule reads as missing evidence rather than as a
/// clean bake, and falls back to the bare leftover count for.
/// </param>
/// <param name="ClaimsUnproven">
/// How many claims rest on something weaker: a bare containment match against the named region, or the
/// last-slot/last-mesh elimination rule. Non-zero next to unclaimed geometry in the bracket is the one shape in
/// which contamination could actually have corrupted a claim, and is what the ownership arm refuses.
/// </param>
/// <param name="RefusalArm">
/// Which arm of the completeness rule refused this bake — <c>incomplete</c> / <c>unassociated</c> /
/// <c>ownership</c> / <c>foreign</c> / <c>other</c> — or null when nothing refused it.
///
/// <para>A STABLE BUCKET, unlike <paramref name="RefusalReason"/>: the reason carries counts, so it takes one
/// distinct value per rig and cannot be grouped. Populated on a bake that ran and was refused as well as on a
/// memo hit, so a caller reads the same field either way.</para>
/// </param>
/// <param name="RefusalReason">
/// The full detail behind <paramref name="RefusalArm"/>, counts included (<c>associated=40 of
/// slotsEverVisible=44</c>). Null when nothing refused this bake.
/// </param>
/// <param name="RefusedFromMemo">
/// True when this result was answered from the process's refusal memo instead of by baking. The bake it stands
/// for happened earlier in this process, under the same identity, the same environment levers and the same
/// bridge build; <c>IgnoreRefusalMemo</c> on the request forces a real bake.
/// </param>
/// <param name="RefusalRetryable">
/// True when the refusal is a RETRYABLE ACQUISITION FAULT rather than a verdict about the rig: the sweep
/// validated fewer meshes than there are slots to fill (<paramref name="MeshesValidated"/> &lt;
/// <paramref name="SlotsVisible"/>) and it was not cut short by its own candidate cap
/// (<paramref name="SweepTruncated"/> false). That shape says the sweep's PLAN did not cover the rig's own
/// meshes, which is a property of the process's RID allocator at that instant and not of the rig — a creature
/// death earlier in the session is enough to cause it, and a later bake of the same identity can succeed.
///
/// <para>WHAT A CONSUMER MUST DO WITH IT. Do not write a DURABLE refusal receipt for one. The producer's own
/// in-process memo already declines to pin it (see <c>Sts2SpineGeoClipRequestLane.BakeWithRefusalMemo</c>), and
/// a host with a persistent store that pins it anyway poisons that identity for every later launch on the
/// strength of one unlucky moment. False on every other refusal, and on a memo replay.</para>
/// </param>
public sealed record SpineGeoClipBakeResultSnapshot(
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
    AssetExtractFailure? Error,
    IReadOnlyList<SpineGeoClipBakePoseSnapshot>? Poses = null,
    int ScenesLoaded = 1,
    string BatchNote = "single",
    string? RefusalArm = null,
    string? RefusalReason = null,
    bool RefusedFromMemo = false,
    int ClaimsProven = 0,
    int ClaimsUnproven = 0,
    // See the pose snapshot's copies: the sweep's own two numbers, so a refusal can say whether it is a verdict
    // about the rig or an acquisition that came up short.
    int MeshesValidated = 0,
    bool SweepTruncated = false,
    bool RefusalRetryable = false)
{
    public static SpineGeoClipBakeResultSnapshot Failure(
        AssetExtractFailureCode code,
        string message,
        IReadOnlyList<AssetExtractDetail> details)
        => new(
            Success: false,
            ManifestPath: null,
            PageFileNames: [],
            PartCount: 0,
            FrameCount: 0,
            SampleTimeSeconds: 0d,
            SampleTimeSource: string.Empty,
            ElapsedMs: 0d,
            Slots: 0,
            SlotsVisible: 0,
            Associated: 0,
            Unassociated: 0,
            ForeignMeshes: 0,
            Complete: false,
            Error: new AssetExtractFailure(code, message, details),
            Poses: null,
            ScenesLoaded: 0,
            BatchNote: "rejected");

    /// <summary>The "this build has no live game behind it" answer, shared by every non-live implementation.</summary>
    public static SpineGeoClipBakeResultSnapshot NotSupported(string note)
        => Failure(
            AssetExtractFailureCode.NotImplemented,
            "baking a spine geoclip requires the live STS2 bridge host.",
            [new AssetExtractDetail(Field: "capability", Value: "spine-geoclip-bake", Note: note)]);
}
