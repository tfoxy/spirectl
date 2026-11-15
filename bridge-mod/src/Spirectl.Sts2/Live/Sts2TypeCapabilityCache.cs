using System.Reflection;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Memo for a per-node capability probe whose answer is a function of the node's TYPE, not of the instance.
/// </summary>
/// <remarks>
/// <para>
/// Godot builds an object's property list from its native class, its script and any per-instance property-list
/// hook. Without a per-instance hook, two nodes with the same managed type and the same native class have the same
/// properties, so a question such as "does this node expose a readable bool property named X" has one answer per
/// <see cref="Key"/>. The scene watcher asks such a question for every node it starts tracking; answering it from
/// the node's full property list marshals one dictionary per property, which on a full scene was most of the main
/// thread's time in the first capture after a subscription. This memo keeps one answer per key for the process.
/// </para>
/// <para>
/// Godot-free so the memo and the reflection rule that decides keyability are offline-unit-testable. The caller
/// builds the key (or passes null for a node that must be probed on its own) and supplies the uncached probe.
/// A probe that cannot read (the node is being freed) returns null: that answers false for this node, exactly as
/// the uncached path does, and is never remembered for the key.
/// </para>
/// </remarks>
internal sealed class Sts2TypeCapabilityCache
{
    private readonly object _gate = new();
    private readonly Dictionary<Key, bool> _answers = new();
    private readonly Dictionary<Type, bool> _declaresInstanceHook = new();

    /// <summary>The identity a node's answer is shared under: its managed wrapper type and its native class.</summary>
    internal readonly record struct Key(Type ManagedType, string NativeClass);

    /// <summary>Number of remembered answers (tests and diagnostics).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _answers.Count;
            }
        }
    }

    /// <summary>
    /// The remembered answer for <paramref name="key"/>, or the probe's answer (remembered when the key is non-null
    /// and the probe could read). A null key always probes.
    /// </summary>
    internal bool GetOrProbe<TState>(Key? key, TState state, Func<TState, bool?> probe)
    {
        if (key is { } lookup)
        {
            lock (_gate)
            {
                if (_answers.TryGetValue(lookup, out var known))
                {
                    return known;
                }
            }
        }

        var answer = probe(state);
        if (answer is not { } value)
        {
            return false;
        }

        if (key is { } store)
        {
            lock (_gate)
            {
                _answers.TryAdd(store, value);
            }
        }

        return value;
    }

    /// <summary>
    /// Whether <paramref name="managedType"/>, or any of its base types above the engine boundary, declares one of
    /// <paramref name="instanceHookNames"/> — a per-instance property-list hook that makes the property set an
    /// instance fact, so the type cannot key the answer. Engine types (those in <paramref name="engineAssembly"/>)
    /// are not inspected: their own virtual declarations are the empty defaults. Memoized per type.
    /// </summary>
    internal bool DeclaresInstanceHook(Type managedType, Assembly engineAssembly, IReadOnlyList<string> instanceHookNames)
    {
        lock (_gate)
        {
            if (_declaresInstanceHook.TryGetValue(managedType, out var known))
            {
                return known;
            }
        }

        var declares = ScanForInstanceHook(managedType, engineAssembly, instanceHookNames);
        lock (_gate)
        {
            _declaresInstanceHook.TryAdd(managedType, declares);
        }

        return declares;
    }

    private static bool ScanForInstanceHook(Type managedType, Assembly engineAssembly, IReadOnlyList<string> instanceHookNames)
    {
        const BindingFlags Declared =
            BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = managedType; type is not null && type.Assembly != engineAssembly; type = type.BaseType)
        {
            foreach (var method in type.GetMethods(Declared))
            {
                foreach (var name in instanceHookNames)
                {
                    if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
