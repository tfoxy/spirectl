using HotMod.Contracts;

namespace ValidLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = new(
        "valid",
        "Valid",
        "1",
        HotContract.ContractVersion,
        new Dictionary<string, string>());

    public void Initialize(IHotHost host, HotReloadContext context)
    {
        host.Log(HotLogLevel.Information, "valid initialized");
    }

    public HotLogicResult OnHook(HotHookContext context)
    {
        return new HotLogicResult(true, ["valid"]);
    }

    public void Dispose()
    {
    }
}
