using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Artifacts;

/// <summary>
/// Request for a Spine catalog scan. The catalog spans everything reachable via
/// <c>res://</c>, including mounted mod content overlaid onto that virtual filesystem,
/// so no explicit content-selection knobs are needed.
/// </summary>
public sealed record SpineCatalogRequestSnapshot();

public sealed record SpineCatalogCandidate(
    string SceneResPath,
    string? NodePath,
    IReadOnlyList<string> Animations);

public sealed record SpineCatalogEntrySnapshot(
    string SceneResPath,
    string? NodePath,
    string AnimationName);

public sealed record SpineCatalogFailureSnapshot(
    string ResourcePath,
    string Code,
    string Message);

public sealed record SpineCatalogOperationResult(
    DataSourceKind Source,
    bool Provisional,
    int ScannedSceneCount,
    int SpineNodeCount,
    IReadOnlyList<SpineCatalogEntrySnapshot> Entries,
    IReadOnlyList<SpineCatalogFailureSnapshot> Failures,
    IReadOnlyList<string> Notes,
    AssetExtractFailure? Error)
{
    public int ClipCount => Entries.Count;

    public static SpineCatalogOperationResult Success(
        DataSourceKind source,
        bool provisional,
        int scannedSceneCount,
        int spineNodeCount,
        IReadOnlyList<SpineCatalogEntrySnapshot> entries,
        IReadOnlyList<SpineCatalogFailureSnapshot> failures,
        IReadOnlyList<string> notes)
        => new(source, provisional, scannedSceneCount, spineNodeCount, entries, failures, notes, null);

    public static SpineCatalogOperationResult Failure(
        DataSourceKind source,
        bool provisional,
        AssetExtractFailureCode code,
        string message,
        IReadOnlyList<AssetExtractDetail> details)
        => new(
            source,
            provisional,
            0,
            0,
            [],
            [],
            [],
            new AssetExtractFailure(code, message, details));
}

public static class SpineCatalogNormalizer
{
    public static IReadOnlyList<SpineCatalogEntrySnapshot> Normalize(
        IEnumerable<SpineCatalogCandidate> candidates)
    {
        var entries = new HashSet<SpineCatalogEntrySnapshot>();
        foreach (var candidate in candidates)
        {
            var scene = NormalizeScenePath(candidate.SceneResPath);
            var node = NormalizeNodePath(candidate.NodePath);
            foreach (var animation in candidate.Animations)
            {
                var name = animation.Trim();
                if (name.Length == 0 || name.StartsWith("_ignore/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new SpineCatalogEntrySnapshot(scene, node, name));
            }
        }

        return entries
            .OrderBy(entry => entry.SceneResPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.NodePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(entry => entry.AnimationName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeScenePath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/').Trim('/');
        return trimmed.StartsWith("res://", StringComparison.Ordinal)
            ? "res://" + trimmed["res://".Length..].Trim('/')
            : "res://" + trimmed;
    }

    private static string? NormalizeNodePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == ".")
        {
            return null;
        }

        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
