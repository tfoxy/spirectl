using HotMod.Contracts;

namespace ContractMismatchLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = new(
        "mismatch",
        "Mismatch",
        "1",
        HotContract.ContractVersion + 1,
        new Dictionary<string, string>());

    public void Initialize(IHotHost host, HotReloadContext context)
    {
    }

    public HotLogicResult OnHook(HotHookContext context)
    {
        return new HotLogicResult(false, []);
    }

    public void Dispose()
    {
    }
}
