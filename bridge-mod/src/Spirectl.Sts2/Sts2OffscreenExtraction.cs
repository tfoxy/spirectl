namespace Spirectl.Sts2;

/// <summary>
/// The stable NAME this runtime gives every throwaway <c>SubViewport</c> it parents onto the scene tree's root
/// to render or read back a rig off screen — the asset extractor's render lanes, the spine still/clip bake, the
/// geometry probe and the geoclip baker.
///
/// <para>WHY IT IS PUBLIC. An embedding host can run its own idle/CPU suspender that walks the tree from
/// <c>SceneTree.Root</c> and freezes what it finds (CouchCoop's headless visual suspender does exactly this:
/// an unconditional DFS that sets <c>ProcessMode = Disabled</c> on every spine node). A <c>SubViewport</c>'s
/// children are ordinary children of that root, so such a walk reaches INSIDE an extraction viewport and
/// freezes the detached rig the extraction is in the middle of posing — spine-godot deforms in
/// <c>NOTIFICATION_INTERNAL_PROCESS</c>, so a frozen node never re-poses and a per-frame seek silently returns
/// the same pose for every frame. The host needs a way to recognise "this subtree is somebody's off-screen
/// extraction, skip it whole" without guessing, and a C#-type match is useless because the node is a plain
/// <c>SubViewport</c>. This constant is that marker, and it lives here (rather than in a live-host-only file)
/// so a host can reference it whether or not it was built against the game assemblies.</para>
///
/// <para>MATCH BY PREFIX, NOT BY EQUALITY. Godot uniquifies duplicate sibling names, so two concurrent
/// extractions are named <c>Sts2OffscreenExtraction</c> and <c>Sts2OffscreenExtraction2</c> (or
/// <c>@Sts2OffscreenExtraction@…</c> for an engine-generated variant). Use
/// <see cref="IsExtractionSubtreeRoot"/> rather than comparing to <see cref="SubViewportNodeName"/>.</para>
///
/// <para>SCOPE, deliberately narrow: this marks the extraction viewport ONLY. A host must not widen an
/// exemption to "every SubViewport" — the game renders its own content off screen, and un-freezing that gives
/// back the whole CPU win a suspender exists for.</para>
/// </summary>
public static class Sts2OffscreenExtraction
{
    /// <summary>The node name every off-screen extraction <c>SubViewport</c> in this runtime is given.</summary>
    public const string SubViewportNodeName = "Sts2OffscreenExtraction";

    /// <summary>
    /// Whether <paramref name="nodeName"/> names an off-screen extraction viewport created by this runtime.
    /// Prefix-matched, because the engine uniquifies duplicate sibling names (see the type remarks).
    /// </summary>
    public static bool IsExtractionSubtreeRoot(string? nodeName)
    {
        if (string.IsNullOrEmpty(nodeName))
        {
            return false;
        }

        // An engine-generated name can carry a leading '@'; the uniquifier only ever APPENDS, so trimming that
        // one prefix character is enough to make the test a plain StartsWith.
        var name = nodeName[0] == '@' ? nodeName[1..] : nodeName;
        return name.StartsWith(SubViewportNodeName, StringComparison.Ordinal);
    }
}
