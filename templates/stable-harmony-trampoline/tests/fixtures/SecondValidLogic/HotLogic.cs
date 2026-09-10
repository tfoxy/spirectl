using HotMod.Contracts;

namespace SecondValidLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = new(
        "second-valid",
        "Second Valid",
        "2",
        HotContract.ContractVersion,
        new Dictionary<string, string>());

    public void Initialize(IHotHost host, HotReloadContext context)
    {
        host.Log(HotLogLevel.Information, "second initialized");
    }

    public HotLogicResult OnHook(HotHookContext context)
    {
        return new HotLogicResult(true, ["second"]);
    }

    public void Dispose()
    {
    }
}
