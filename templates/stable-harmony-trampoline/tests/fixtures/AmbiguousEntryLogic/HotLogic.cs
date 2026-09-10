using HotMod.Contracts;

namespace AmbiguousEntryLogic;

public sealed class FirstLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = ManifestFactory.Create("first");
    public void Initialize(IHotHost host, HotReloadContext context) { }
    public HotLogicResult OnHook(HotHookContext context) => new(false, []);
    public void Dispose() { }
}

public sealed class SecondLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = ManifestFactory.Create("second");
    public void Initialize(IHotHost host, HotReloadContext context) { }
    public HotLogicResult OnHook(HotHookContext context) => new(false, []);
    public void Dispose() { }
}

internal static class ManifestFactory
{
    public static HotLogicManifest Create(string id)
    {
        return new HotLogicManifest(id, id, "1", HotContract.ContractVersion, new Dictionary<string, string>());
    }
}
