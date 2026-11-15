using System.Diagnostics;
using Spirectl.Sts2.Live;
using Xunit;
using Xunit.Abstractions;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2TypeCapabilityCacheTests(ITestOutputHelper output)
{
    private static readonly string[] Hooks = ["_GetPropertyList", "_ValidateProperty"];

    // The boundary the scan stops at. These fixtures derive straight from object, so CoreLib plays the engine.
    private static readonly System.Reflection.Assembly Boundary = typeof(object).Assembly;

    // A synthetic scene: many nodes over a few dozen (type, class) identities, plus nodes that must be probed on
    // their own (null key). The uncached probe's answer is a fixed function of the identity, which is the premise
    // the memo relies on; an unkeyed node gets an instance-specific answer to prove it is never shared.
    private sealed record SyntheticNode(int Index, Type ManagedType, string NativeClass, bool Keyable)
    {
        public Sts2TypeCapabilityCache.Key? Key => Keyable ? new(ManagedType, NativeClass) : null;
    }

    private static readonly Type[] ManagedTypes =
    [
        typeof(PlainScript), typeof(OtherScript), typeof(string), typeof(int), typeof(Uri), typeof(Version),
    ];

    private static readonly string[] NativeClasses =
        ["Control", "TextureRect", "Label", "Node2D", "Sprite2D", "MarginContainer", "RichTextLabel"];

    private static List<SyntheticNode> BuildScene(int count)
    {
        var nodes = new List<SyntheticNode>(count);
        for (var i = 0; i < count; i++)
        {
            nodes.Add(new SyntheticNode(
                i,
                ManagedTypes[i % ManagedTypes.Length],
                NativeClasses[(i / 3) % NativeClasses.Length],
                Keyable: i % 97 != 0));
        }

        return nodes;
    }

    private static bool Truth(SyntheticNode node)
        => node.Keyable
            ? (node.ManagedType.Name.Length + node.NativeClass.Length) % 3 == 0
            : node.Index % 2 == 0;

    [Fact]
    public void CachedAnswersMatchTheUncachedProbeForEveryNode()
    {
        var scene = BuildScene(5_000);
        var cache = new Sts2TypeCapabilityCache();
        var probes = 0;

        var uncached = scene.Select(Truth).ToArray();
        var cached = scene.Select(node => cache.GetOrProbe(node.Key, node, n =>
        {
            probes++;
            return Truth(n);
        })).ToArray();

        Assert.Equal(uncached, cached);
        var distinctKeys = scene.Where(n => n.Keyable).Select(n => n.Key).Distinct().Count();
        var unkeyed = scene.Count(n => !n.Keyable);
        Assert.Equal(distinctKeys + unkeyed, probes);
        Assert.Equal(distinctKeys, cache.Count);

        // A second full capture (a resubscribe) probes only the nodes that can never share an answer.
        probes = 0;
        var again = scene.Select(node => cache.GetOrProbe(node.Key, node, n =>
        {
            probes++;
            return Truth(n);
        })).ToArray();
        Assert.Equal(uncached, again);
        Assert.Equal(unkeyed, probes);
    }

    [Fact]
    public void AFailedProbeAnswersFalseAndIsNotRemembered()
    {
        var cache = new Sts2TypeCapabilityCache();
        var key = new Sts2TypeCapabilityCache.Key(typeof(PlainScript), "Control");

        Assert.False(cache.GetOrProbe<object?>(key, null, _ => null));
        Assert.Equal(0, cache.Count);

        // The next live node of the same identity is probed and its real answer is the one kept.
        Assert.True(cache.GetOrProbe<object?>(key, null, _ => true));
        Assert.True(cache.GetOrProbe<object?>(key, null, _ => throw new InvalidOperationException("memoized")));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void ANullKeyAlwaysProbes()
    {
        var cache = new Sts2TypeCapabilityCache();
        var probes = 0;
        Assert.True(cache.GetOrProbe<object?>(null, null, _ => { probes++; return true; }));
        Assert.False(cache.GetOrProbe<object?>(null, null, _ => { probes++; return false; }));
        Assert.Equal(2, probes);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void KeyIncludesTheNativeClass()
    {
        // One managed wrapper type can front several native classes (a script on a different native node, or a
        // native class with no managed binding of its own), so the class is part of the identity.
        var cache = new Sts2TypeCapabilityCache();
        Assert.True(cache.GetOrProbe<object?>(new(typeof(PlainScript), "Control"), null, _ => true));
        Assert.False(cache.GetOrProbe<object?>(new(typeof(PlainScript), "TextureRect"), null, _ => false));
        Assert.True(cache.GetOrProbe<object?>(new(typeof(PlainScript), "Control"), null, _ => false));
    }

    [Theory]
    [InlineData(typeof(PlainScript), false)]
    [InlineData(typeof(OtherScript), false)]
    [InlineData(typeof(PropertyListScript), true)]
    [InlineData(typeof(InheritsPropertyListScript), true)]
    [InlineData(typeof(ValidatePropertyScript), true)]
    [InlineData(typeof(InheritsPlainScript), false)]
    public void APerInstancePropertyListHookMakesTheTypeUnkeyable(Type managedType, bool expected)
    {
        var cache = new Sts2TypeCapabilityCache();
        Assert.Equal(expected, cache.DeclaresInstanceHook(managedType, Boundary, Hooks));
        Assert.Equal(expected, cache.DeclaresInstanceHook(managedType, Boundary, Hooks)); // memoized, same verdict
    }

    [Fact]
    public void TheScanStopsAtTheEngineBoundary()
    {
        // A hook declared on an engine type is the engine's empty default, not a per-instance property source.
        var cache = new Sts2TypeCapabilityCache();
        Assert.False(cache.DeclaresInstanceHook(
            typeof(InheritsPropertyListScript), typeof(PropertyListScript).Assembly, Hooks));
    }

    // Timing note, not a gate: the number of expensive probes a first capture pays before and after the memo, over
    // a synthetic scene whose probe costs one small allocation per "property". Wall time is printed, never asserted.
    [Fact]
    public void TimingNoteProbeCountBeforeAndAfter()
    {
        const int PropertiesPerNode = 150;
        var scene = BuildScene(5_000);

        static bool? ExpensiveProbe(SyntheticNode node)
        {
            var sink = 0;
            for (var i = 0; i < PropertiesPerNode; i++)
            {
                sink += new Dictionary<string, int>(1) { ["name"] = i }.Count;
            }

            return sink > 0 && Truth(node);
        }

        var before = Stopwatch.StartNew();
        var uncachedProbes = 0;
        var uncached = scene.Select(n => { uncachedProbes++; return ExpensiveProbe(n) ?? false; }).ToArray();
        before.Stop();

        var cache = new Sts2TypeCapabilityCache();
        var cachedProbes = 0;
        var after = Stopwatch.StartNew();
        var cached = scene.Select(n => cache.GetOrProbe(n.Key, n, s => { cachedProbes++; return ExpensiveProbe(s); }))
            .ToArray();
        after.Stop();

        Assert.Equal(uncached, cached);
        Assert.True(cachedProbes < uncachedProbes);
        output.WriteLine(
            $"nodes={scene.Count} probes before={uncachedProbes} after={cachedProbes} "
            + $"elapsed before={before.Elapsed.TotalMilliseconds:F1}ms after={after.Elapsed.TotalMilliseconds:F1}ms");
    }

    private class PlainScript;

    private sealed class OtherScript;

    private sealed class InheritsPlainScript : PlainScript;

    // The fixtures below declare the engine's hook names verbatim, underscore prefix included: matching them is
    // the behaviour under test.
#pragma warning disable IDE1006
    private class PropertyListScript
    {
        public object? _GetPropertyList() => null;
    }

    private sealed class InheritsPropertyListScript : PropertyListScript;

    private sealed class ValidatePropertyScript
    {
        private void _ValidateProperty(object property) => _ = property;
    }
#pragma warning restore IDE1006
}
