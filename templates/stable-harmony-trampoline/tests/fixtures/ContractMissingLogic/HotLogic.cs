using HotMod.Contracts;

namespace ContractMissingLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest => throw new InvalidOperationException("manifest missing");

    public void Initialize(IHotHost host, HotReloadContext context)
    {
    }

    public HotLogicResult OnHook(HotHookContext context)
    {
        return new HotLogicResult(false, Array.Empty<string>());
    }

    public void Dispose()
    {
    }
}
