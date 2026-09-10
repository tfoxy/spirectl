using HotMod.Contracts;
using System.Reflection;
using System.Runtime.Loader;

namespace LeakyLogic;

public sealed class HotLogic : IHotLogic
{
    private static object? Retained;

    public HotLogicManifest Manifest { get; } = new("leaky", "Leaky", "1", HotContract.ContractVersion, new Dictionary<string, string>());

    public void Initialize(IHotHost host, HotReloadContext context)
    {
        Retained = this;
        AssemblyLoadContext.Default.Resolving += OnResolving;
    }

    public HotLogicResult OnHook(HotHookContext context) => new(true, ["leaky"]);

    public void Dispose()
    {
    }

    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        return null;
    }
}
