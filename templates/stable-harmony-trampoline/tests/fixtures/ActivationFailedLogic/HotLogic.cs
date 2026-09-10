using HotMod.Contracts;

namespace ActivationFailedLogic;

public sealed class HotLogic : IHotLogic
{
    public HotLogic()
    {
        throw new InvalidOperationException("activation failed");
    }

    public HotLogicManifest Manifest => throw new InvalidOperationException("unreachable");
    public void Initialize(IHotHost host, HotReloadContext context) { }
    public HotLogicResult OnHook(HotHookContext context) => new(false, []);
    public void Dispose() { }
}
