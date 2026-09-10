using HotMod.Contracts;

namespace DisposeFailedLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = new("dispose-failed", "Dispose Failed", "1", HotContract.ContractVersion, new Dictionary<string, string>());
    public void Initialize(IHotHost host, HotReloadContext context) { }
    public HotLogicResult OnHook(HotHookContext context) => new(true, ["dispose-failed"]);
    public void Dispose() => throw new InvalidOperationException("dispose failed");
}
