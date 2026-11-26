using System.Collections.Concurrent;
using System.Reflection;

namespace Spirectl.Sts2.Live;

/// <summary>Known upgrade signatures shared by preview invocation and event hooks.</summary>
internal static class Sts2CardUpgradeCompatibility
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<MethodInfo>> Targets = new();

    internal static IReadOnlyList<MethodInfo> FindTargets(Type type)
        => Targets.GetOrAdd(type, static cardType =>
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var targets = new List<MethodInfo>();
            // Prefer a silent preview when supported; keep both signatures available to the hooks.
            Type[][] signatures = [[typeof(bool)], []];
            foreach (var parameters in signatures)
            {
                var method = cardType.GetMethod("UpgradeInternal", flags, null, parameters, null);
                if (method is not null && method.ReturnType == typeof(void))
                {
                    targets.Add(method);
                }
            }

            return targets;
        });

    internal static void UpgradePreview(object clone)
    {
        var targets = FindTargets(clone.GetType());
        if (targets.Count == 0)
        {
            throw new MissingMethodException(clone.GetType().FullName, "UpgradeInternal");
        }

        var target = targets[0];
        target.Invoke(clone, target.GetParameters().Length == 0 ? null : [true]);
    }
}
