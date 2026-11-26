using System.Collections.Concurrent;
using System.Reflection;
using Spirectl.Sts2.Core.Protocol;
#if ENABLE_STS2_LIVE_HOST
using Godot;
#endif

namespace Spirectl.Sts2.Live;

internal static class Sts2LiveIntrospection
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed record MethodCandidate(MethodInfo Method, ParameterInfo[] Parameters);
    private static readonly ConcurrentDictionary<(Type Type, string Name), MethodCandidate[]> Methods = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), byte> ReportedMethodMisses = new();

    /// <summary>
    /// Assembly-qualified name of the game's active-screen context — the object that knows which screen is
    /// currently on top. Named here once so the resolver below and <see cref="Sts2ScreenContext"/>'s event
    /// subscription cannot drift onto different types.
    /// </summary>
    internal const string ActiveScreenContextTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext, sts2";

    public static bool IsType(object? target, string fullTypeName)
    {
        for (var current = target?.GetType(); current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, fullTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static object? ResolveCurrentScreenObject()
    {
        try
        {
            var contextType = Type.GetType(ActiveScreenContextTypeName);
            if (contextType is null)
            {
                return null;
            }

            var instance = contextType
                .GetProperty("Instance", StaticFlags)?
                .GetValue(null);
            if (instance is null)
            {
                return null;
            }

            return contextType
                .GetMethod("GetCurrentScreen", InstanceFlags)?
                .Invoke(instance, null)
                ?? contextType.GetProperty("CurrentScreen", InstanceFlags)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    // Find the first live node of the given type inside the active run's scene tree. Browser mode renders
    // rooms (treasure, rest site, …) as descendants of the Run node while the located "screen" stays the
    // run screen, so screen-relative resolution (ResolveCurrentScreenObject + up-walk) misses them. This
    // searches DOWN from the Run node (the same anchor the debug resolvers use), falling back to the whole
    // tree, so room nodes that have no static Instance singleton are still resolvable.
    public static object? FindRunTreeNodeOfType(string fullTypeName)
    {
#if ENABLE_STS2_LIVE_HOST
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            {
                return null;
            }

            var runNode = tree.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run");
            return FindDescendantOfType(runNode ?? tree.Root, fullTypeName);
        }
        catch
        {
            return null;
        }
#else
        _ = fullTypeName;
        return null;
#endif
    }

    /// <summary>
    /// ALL live nodes of the given type inside the active run's scene tree — the plural of
    /// <see cref="FindRunTreeNodeOfType"/>, for the cases where every instance matters rather than the first
    /// (e.g. re-stamping the per-player multiplayer panels, one per player). Empty when the tree is unavailable.
    /// </summary>
    public static IReadOnlyList<object> FindRunTreeNodesOfType(string fullTypeName)
    {
#if ENABLE_STS2_LIVE_HOST
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            {
                return [];
            }

            var found = new List<object>();
            var runNode = tree.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run");
            CollectDescendantsOfType(runNode ?? tree.Root, fullTypeName, found);
            if (found.Count == 0 && runNode is not null)
            {
                // Not everything a run shows hangs off the Run node — the global UI layers are parented higher up
                // — so a miss under Run widens to the whole tree rather than reporting "none". Only ever walked on
                // a miss, and the callers are one-shot (a name registration), not per-frame.
                CollectDescendantsOfType(tree.Root, fullTypeName, found);
            }

            return found;
        }
        catch
        {
            return [];
        }
#else
        _ = fullTypeName;
        return [];
#endif
    }

#if ENABLE_STS2_LIVE_HOST
    private static void CollectDescendantsOfType(Node? node, string fullTypeName, List<object> found)
    {
        if (node is null)
        {
            return;
        }

        if (IsType(node, fullTypeName))
        {
            found.Add(node);
        }

        foreach (var child in node.GetChildren())
        {
            CollectDescendantsOfType(child, fullTypeName, found);
        }
    }

    private static Node? FindDescendantOfType(Node? node, string fullTypeName)
    {
        if (node is null)
        {
            return null;
        }

        if (IsType(node, fullTypeName))
        {
            return node;
        }

        foreach (var child in node.GetChildren())
        {
            var found = FindDescendantOfType(child, fullTypeName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
#endif

    public static object? GetMemberValue(object? target, string memberName)
    {
        if (target is null)
        {
            return null;
        }

        var type = target.GetType();
        var property = FindProperty(type, memberName);
        if (property is not null)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
            }
        }

        var field = FindField(type, memberName);
        if (field is not null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
            }
        }

        return null;
    }

    public static IReadOnlyList<(string Name, object? Value)> GetInstanceFieldValues(object? target)
    {
        if (target is null)
        {
            return [];
        }

        var values = new List<(string Name, object? Value)>();
        for (var current = target.GetType(); current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(InstanceFlags | BindingFlags.DeclaredOnly))
            {
                try
                {
                    values.Add((field.Name, field.GetValue(target)));
                }
                catch
                {
                }
            }
        }

        return values;
    }

    public static bool TrySetMemberValue(object? target, string memberName, object? value)
    {
        if (target is null)
        {
            return false;
        }

        var type = target.GetType();
        var property = FindProperty(type, memberName);
        if (property?.SetMethod is not null)
        {
            try
            {
                property.SetValue(target, value);
                return true;
            }
            catch
            {
            }
        }

        var field = FindField(type, memberName);
        if (field is not null)
        {
            try
            {
                field.SetValue(target, value);
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static PropertyInfo? FindProperty(Type type, string propertyName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(propertyName, InstanceFlags | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? FindField(Type type, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    public static object? InvokeMethod(object? target, string methodName, params object?[] arguments)
    {
        if (target is null)
        {
            return null;
        }

        var method = FindMethod(target.GetType(), methodName, arguments);
        return method?.Method.Invoke(target, CompleteArguments(method, arguments));
    }

    public static bool TryInvokeMethod(object? target, string methodName, params object?[] arguments)
    {
        if (target is null)
        {
            return false;
        }

        var method = FindMethod(target.GetType(), methodName, arguments);
        if (method is null)
        {
            return false;
        }

        method.Method.Invoke(target, CompleteArguments(method, arguments));
        return true;
    }

    public static bool TryInvokeParameterlessMethod(object? target, params string[] methodNames)
    {
        if (target is null)
        {
            return false;
        }

        foreach (var methodName in methodNames)
        {
            var method = FindParameterlessMethod(target.GetType(), methodName);
            if (method is null)
            {
                if (TryInvokeMethod(target, methodName))
                {
                    return true;
                }

                continue;
            }

            method.Invoke(target, null);
            return true;
        }

        return false;
    }

    private static MethodInfo? FindParameterlessMethod(Type type, string methodName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var method = current.GetMethod(
                methodName,
                InstanceFlags | BindingFlags.DeclaredOnly,
                Type.DefaultBinder,
                Type.EmptyTypes,
                null);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    private static MethodCandidate? FindMethod(Type type, string methodName, object?[] arguments)
    {
        var candidates = Methods.GetOrAdd((type, methodName), static key => [.. key.Type
            .GetMethods(InstanceFlags)
            .Where(method => method.Name == key.Name && !method.ContainsGenericParameters)
            .Select(method => new MethodCandidate(method, method.GetParameters()))]);

        if (candidates.Length == 0)
        {
            // Optional name probes are not evidence of signature drift.
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Length == arguments.Length && AcceptsArguments(candidate.Parameters, arguments))
            {
                return candidate;
            }
        }

        MethodCandidate? fallback = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Length <= arguments.Length || !AcceptsArguments(candidate.Parameters, arguments))
            {
                continue;
            }

            var optional = true;
            for (var index = arguments.Length; index < candidate.Parameters.Length; index++)
            {
                if (!candidate.Parameters[index].HasDefaultValue)
                {
                    optional = false;
                    break;
                }
            }

            if (!optional)
            {
                continue;
            }

            if (fallback is not null)
            {
                ReportMethodMiss(type, methodName, "Multiple overloads accept optional trailing arguments; invocation refused.");
                return null;
            }

            fallback = candidate;
        }

        ReportMethodMiss(type, methodName, fallback is null
            ? "No compatible method signature was found; the dependent operation is unavailable."
            : "Exact argument count no longer matches; using the single overload with optional trailing arguments.");
        return fallback;
    }

    private static bool AcceptsArguments(ParameterInfo[] parameters, object?[] arguments)
    {
        if (parameters.Length < arguments.Length)
        {
            return false;
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] is { } argument && !parameters[index].ParameterType.IsInstanceOfType(argument))
            {
                return false;
            }
        }

        return true;
    }

    private static object?[] CompleteArguments(MethodCandidate method, object?[] arguments)
    {
        var parameters = method.Parameters;
        if (parameters.Length == arguments.Length)
        {
            return arguments;
        }

        var completed = new object?[parameters.Length];
        arguments.CopyTo(completed, 0);
        for (var index = arguments.Length; index < completed.Length; index++)
        {
            completed[index] = Type.Missing;
        }

        return completed;
    }

    private static void ReportMethodMiss(Type type, string methodName, string reason)
    {
        if (ReportedMethodMisses.TryAdd((type, methodName), 0))
        {
            Sts2CompatibilityReport.Process.Record("method_signature_miss", $"{type.FullName}.{methodName}", reason);
        }
    }
}
