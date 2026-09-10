using Godot;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;

internal sealed class Sts2SpineCatalogService(ILogStream log) : ISpineCatalogProvider, ISpineGeoClipBaker
{
    /// <summary>
    /// The live geoclip bake seam. Delegates straight to the baker's REQUEST lane, which marshals itself onto the
    /// Godot main thread and — unlike the env-armed one-shot lane — never touches
    /// <c>Sts2OneShotArmClaim</c>, so an env-armed bake and an on-demand request can coexist in one process.
    /// </summary>
    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request)
        => Sts2SpineGeoClipBaker.BakeOnDemand(request, log);

    public SpineCatalogOperationResult CatalogSpines(SpineCatalogRequestSnapshot request)
    {
        try { return Sts2MainThreadDispatcher.Invoke(CatalogSpinesOnMainThread); }
        catch (Exception ex)
        {
            log.Write(BridgeLogLevel.Error, "bridge.assets", $"Spine catalog enumeration failed: {ex}");
            return SpineCatalogOperationResult.Failure(DataSourceKind.Live, false, AssetExtractFailureCode.RuntimeFailure,
                "The live bridge failed while enumerating the Spine catalog.", [new AssetExtractDetail("exception", ex.GetType().Name, ex.Message)]);
        }
    }

    private static SpineCatalogOperationResult CatalogSpinesOnMainThread()
    {
        var scenePaths = new List<string>();
        var failures = new List<SpineCatalogFailureSnapshot>();
        EnumerateScenePaths("res://", scenePaths, failures);
        scenePaths.Sort(StringComparer.Ordinal);

        var discovered = new Dictionary<string, SpineCatalogEntryBuilder>(StringComparer.Ordinal);
        var scannedScenes = 0;
        foreach (var scenePath in scenePaths)
        {
            try
            {
                if (ResourceLoader.Load(scenePath) is not PackedScene scene)
                {
                    failures.Add(new SpineCatalogFailureSnapshot(
                        scenePath,
                        "scene-not-packed",
                        "The visible resource did not load as a PackedScene."));
                    continue;
                }

                scannedScenes++;
                var state = scene.GetState();
                if (state is null)
                {
                    failures.Add(new SpineCatalogFailureSnapshot(
                        scenePath,
                        "scene-state-unavailable",
                        "PackedScene.GetState() returned null."));
                    continue;
                }

                for (var index = 0; index < state.GetNodeCount(); index++)
                {
                    var typeName = state.GetNodeType(index).ToString();
                    if (!Sts2SpineInspector.LooksLikeSpineNodeType(typeName))
                    {
                        continue;
                    }

                    var nodePathText = state.GetNodePath(index).ToString();
                    var nodePath = string.IsNullOrEmpty(nodePathText) || nodePathText == "."
                        ? null
                        : nodePathText;
                    var animations = ReadAnimations(state, index);
                    var key = $"{scenePath}\u001f{nodePath ?? string.Empty}";
                    if (!discovered.TryGetValue(key, out var builder))
                    {
                        builder = new SpineCatalogEntryBuilder(scenePath, nodePath);
                        discovered.Add(key, builder);
                    }

                    builder.Animations.UnionWith(animations);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new SpineCatalogFailureSnapshot(
                    scenePath,
                    "scene-inspection-failed",
                    $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        var entries = SpineCatalogNormalizer.Normalize(discovered.Values.Select(builder => new SpineCatalogCandidate(
                builder.SceneResPath,
                builder.NodePath,
                builder.Animations.ToArray())));

        return SpineCatalogOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            scannedSceneCount: scannedScenes,
            spineNodeCount: discovered.Count,
            entries,
            failures,
            [
                "Enumerated visible PackedScene resources through Godot's res:// filesystem.",
                "Canonical scene/node entries were deduplicated and sorted deterministically.",
                "Spine helper animations beginning with _ignore were excluded.",
            ]);
    }

    private static IReadOnlyList<string> ReadAnimations(SceneState state, int nodeIndex)
    {
        for (var propertyIndex = 0; propertyIndex < state.GetNodePropertyCount(nodeIndex); propertyIndex++)
        {
            if (state.GetNodePropertyName(nodeIndex, propertyIndex).ToString() != "skeleton_data_res")
            {
                continue;
            }

            return Sts2SpineInspector.ReadAnimationNames(
                state.GetNodePropertyValue(nodeIndex, propertyIndex));
        }

        return [];
    }

    private static void EnumerateScenePaths(
        string directoryPath,
        List<string> scenePaths,
        List<SpineCatalogFailureSnapshot> failures)
    {
        using var directory = DirAccess.Open(directoryPath);
        if (directory is null)
        {
            failures.Add(new SpineCatalogFailureSnapshot(
                directoryPath,
                "directory-open-failed",
                "The visible res:// directory could not be opened."));
            return;
        }

        directory.ListDirBegin();
        try
        {
            for (var entry = directory.GetNext(); !string.IsNullOrEmpty(entry); entry = directory.GetNext())
            {
                if (entry is "." or "..")
                {
                    continue;
                }

                var childPath = directoryPath.EndsWith('/')
                    ? directoryPath + entry
                    : directoryPath + "/" + entry;
                if (directory.CurrentIsDir())
                {
                    EnumerateScenePaths(childPath, scenePaths, failures);
                    continue;
                }

                if (entry.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                    || entry.EndsWith(".scn", StringComparison.OrdinalIgnoreCase)
                    || entry.EndsWith(".escn", StringComparison.OrdinalIgnoreCase))
                {
                    scenePaths.Add(childPath);
                }
            }
        }
        finally
        {
            directory.ListDirEnd();
        }
    }

    private sealed class SpineCatalogEntryBuilder(string sceneResPath, string? nodePath)
    {
        public string SceneResPath { get; } = sceneResPath;
        public string? NodePath { get; } = nodePath;
        public HashSet<string> Animations { get; } = new(StringComparer.Ordinal);
    }
}
