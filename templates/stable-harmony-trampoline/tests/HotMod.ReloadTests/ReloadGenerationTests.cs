using System.Reflection;
using HotMod.Contracts;
using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class ReloadGenerationTests
{
    [Fact]
    public void DisposeLogicDisposesInstanceOnceAndReportsException()
    {
        var logic = new CountingLogic(throwOnDispose: true);
        var generation = CreateGeneration(logic);

        var first = generation.DisposeLogic();
        var second = generation.DisposeLogic();

        Assert.False(first.Disposed);
        Assert.IsType<InvalidOperationException>(first.Exception);
        Assert.True(second.Disposed);
        Assert.Null(second.Exception);
        Assert.Equal(1, logic.DisposeCount);
    }

    [Fact]
    public void RequestUnloadClearsStrongLogicReferenceAndRequestsCollectibleUnload()
    {
        var loadContext = new PluginLoadContext(typeof(ReloadGenerationTests).Assembly.Location);
        var generation = CreateGeneration(new CountingLogic(), loadContext);

        var weakReference = generation.LoadContextWeakReference;
        generation.RequestUnload();

        Assert.Null(generation.Logic);
        Assert.Same(loadContext, weakReference.Target);
    }

    [Fact]
    public void PluginLoadContextKeepsContractsInDefaultContext()
    {
        var loadContext = new PluginLoadContext(typeof(ReloadGenerationTests).Assembly.Location);

        var loaded = loadContext.LoadFromAssemblyName(new AssemblyName("HotMod.Contracts"));

        Assert.Same(typeof(IHotLogic).Assembly, loaded);
    }

    private static ReloadGeneration CreateGeneration(CountingLogic logic, PluginLoadContext? loadContext = null)
    {
        return new ReloadGeneration(
            Generation: 1,
            Manifest: logic.Manifest,
            Logic: logic,
            LoadContext: loadContext ?? new PluginLoadContext(typeof(ReloadGenerationTests).Assembly.Location),
            SourceAssemblyPath: "/source/HotMod.Logic.dll",
            ShadowAssemblyPath: "/shadow/HotMod.Logic.dll",
            LoadedAt: DateTimeOffset.Parse("2026-04-24T00:00:00Z"));
    }

    private sealed class CountingLogic : IHotLogic
    {
        private readonly bool _throwOnDispose;

        public CountingLogic(bool throwOnDispose = false)
        {
            _throwOnDispose = throwOnDispose;
        }

        public int DisposeCount { get; private set; }

        public HotLogicManifest Manifest { get; } = new(
            "test",
            "Test",
            "1",
            HotContract.ContractVersion,
            new Dictionary<string, string>());

        public void Initialize(IHotHost host, HotReloadContext context)
        {
        }

        public HotLogicResult OnHook(HotHookContext context)
        {
            return new HotLogicResult(false, Array.Empty<string>());
        }

        public void Dispose()
        {
            DisposeCount++;
            if (_throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
