using Godot;

namespace Spirectl.Sts2.Live;

// Resolve a resource addressed inside a loaded scene/document. The parent is loaded by the caller;
// a `::` suffix is an identity inside it, not a second file for ResourceLoader to open.
internal static class Sts2EmbeddedResourcePath
{
    internal static bool TrySplit(string path, out string parentPath, out string id)
    {
        var mark = path.LastIndexOf("::", StringComparison.Ordinal);
        if (mark <= "res://".Length || mark + 2 >= path.Length)
        {
            parentPath = string.Empty;
            id = string.Empty;
            return false;
        }

        parentPath = path[..mark];
        id = path[(mark + 2)..];
        return parentPath.StartsWith("res://", StringComparison.Ordinal)
            && !parentPath.Contains("::", StringComparison.Ordinal)
            && !id.Contains(':');
    }

    internal static Resource? Find(Resource parent, string qualifiedPath)
    {
        var visited = new HashSet<ulong>();
        if (parent is PackedScene scene && scene.GetState() is { } state)
        {
            for (var node = 0; node < state.GetNodeCount(); node++)
            {
                for (var property = 0; property < state.GetNodePropertyCount(node); property++)
                {
                    if (FindInVariant(state.GetNodePropertyValue(node, property), qualifiedPath, visited) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return FindInResource(parent, qualifiedPath, visited, root: true);
    }

    private static Resource? FindInVariant(Variant value, string qualifiedPath, HashSet<ulong> visited)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Object when value.AsGodotObject() is Resource resource:
                return FindInResource(resource, qualifiedPath, visited);
            case Variant.Type.Array:
                foreach (var item in value.AsGodotArray())
                {
                    if (FindInVariant(item, qualifiedPath, visited) is { } found) return found;
                }
                break;
            case Variant.Type.Dictionary:
                var dictionary = value.AsGodotDictionary();
                foreach (var key in dictionary.Keys)
                {
                    if (FindInVariant(key, qualifiedPath, visited) is { } found) return found;
                    if (FindInVariant(dictionary[key], qualifiedPath, visited) is { } nested) return nested;
                }
                break;
        }

        return null;
    }

    private static Resource? FindInResource(Resource resource, string qualifiedPath, HashSet<ulong> visited, bool root = false)
    {
        if (string.Equals(resource.ResourcePath, qualifiedPath, StringComparison.Ordinal)) return resource;
        if (!visited.Add(resource.GetInstanceId())) return null;
        // External resources have their own file and cannot contain a sub-resource of this parent.
        if (!root && Sts2GodotSceneStateEncoding.IsExternalResourcePath(resource.ResourcePath)) return null;

        foreach (var property in resource.GetPropertyList())
        {
            if (!property.TryGetValue("usage", out var usage)
                || ((PropertyUsageFlags)usage.AsInt64() & PropertyUsageFlags.Storage) == 0
                || !property.TryGetValue("name", out var nameValue)
                || nameValue.AsString() is not { Length: > 0 } name
                || name is "resource_path" or "script") continue;

            if (FindInVariant(resource.Get(name), qualifiedPath, visited) is { } found) return found;
        }

        return null;
    }
}
