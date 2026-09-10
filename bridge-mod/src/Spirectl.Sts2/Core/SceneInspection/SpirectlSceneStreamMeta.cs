namespace Spirectl.Sts2.Core.SceneInspection;

/// <summary>
/// Godot metadata keys the scene stream reads off nodes an embedder owns.
/// </summary>
/// <remarks>
/// These are cross-repo contracts, not implementation details: the embedder writes the key on its own node in its
/// own assembly, and spirectl reads it during the walk. Publishing the constant is what lets an embedder stamp the
/// key by reference instead of by hand-copied literal — a copy nothing can hold to this one, where a rename fails
/// open (every previously excluded subtree silently returns to the wire) rather than failing the build.
/// </remarks>
public static class SpirectlSceneStreamMeta
{
    /// <summary>
    /// Stamp this key (<c>node.SetMeta(SpirectlSceneStreamMeta.StreamSkipMetaKey, true)</c>) on the ROOT of a
    /// subtree that must never reach mirror clients, ideally before <c>AddChild</c> so it is already stamped the
    /// first time the walk sees it.
    /// </summary>
    /// <remarks>
    /// <para>Intended for host-local chrome an embedder injects into the live tree: UI that exists for the person
    /// at the machine and would be actively harmful mirrored, because mirror clients drive the host with real
    /// injected input — a remote tap on the mirrored image of a host-only control operates it on the host.</para>
    ///
    /// <para>PRESENCE is the entire signal; the value is never read. The stamp covers the whole subtree (the walk
    /// returns before descending), and removing the meta restores streaming. It applies to the streaming watcher
    /// only, deliberately not to the dev scene-inspection surface, so injected UI stays testable from the CLI.</para>
    /// </remarks>
    public const string StreamSkipMetaKey = "spirectl_stream_skip";
}
