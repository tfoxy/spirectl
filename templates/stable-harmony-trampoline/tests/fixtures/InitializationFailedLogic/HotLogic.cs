using HotMod.Contracts;

namespace InitializationFailedLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogicManifest Manifest { get; } = new("init-failed", "Init Failed", "1", HotContract.ContractVersion, new Dictionary<string, string>());
    public void Initialize(IHotHost host, HotReloadContext context) => throw new InvalidOperationException("initialization failed");
    public HotLogicResult OnHook(HotHookContext context) => new(false, []);
    public void Dispose() { }
}
