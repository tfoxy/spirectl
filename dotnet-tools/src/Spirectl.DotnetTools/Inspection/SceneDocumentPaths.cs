namespace Spirectl.DotnetTools.Inspection;

internal static class SceneDocumentPaths
{
    internal static string NormalizeReferencedPath(string currentDocumentPath, string? referencedPath)
    {
        if (string.IsNullOrWhiteSpace(referencedPath))
        {
            return string.Empty;
        }

        var normalized = referencedPath.Replace('\\', '/');
        if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var currentDirectory = Path.GetDirectoryName(TrimResourcePrefix(currentDocumentPath))?
            .Replace('\\', '/')
            ?? string.Empty;
        var segments = new List<string>();

        if (!normalized.StartsWith('/'))
        {
            segments.AddRange(currentDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in normalized.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return $"res://{string.Join("/", segments)}";
    }

    internal static string NormalizeParentPath(string rootNodeName, string rawParentPath)
    {
        if (rawParentPath == ".")
        {
            return $"/{rootNodeName}";
        }

        return $"/{rootNodeName}/{rawParentPath.TrimStart('/')}";
    }

    internal static string NormalizeNodePath(string nodePath)
    {
        var normalized = nodePath.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : $"/{normalized.TrimStart('/')}";
    }

    internal static string? ResolveNodeTargetPath(string currentNodePath, string rawNodePath)
    {
        var nodePortion = rawNodePath.Split(':', 2)[0].Trim();
        if (string.IsNullOrEmpty(nodePortion))
        {
            return null;
        }

        if (nodePortion.StartsWith('/'))
        {
            return NormalizeNodePath(nodePortion);
        }

        var segments = currentNodePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        foreach (var segment in nodePortion.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : $"/{string.Join("/", segments)}";
    }

    internal static string TrimResourcePrefix(string path)
    {
        return path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? path["res://".Length..]
            : path;
    }

    internal static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }

}
