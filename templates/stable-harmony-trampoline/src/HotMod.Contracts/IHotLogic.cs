namespace HotMod.Contracts;

public interface IHotLogic : IDisposable
{
    HotLogicManifest Manifest { get; }

    void Initialize(IHotHost host, HotReloadContext context);

    HotLogicResult OnHook(HotHookContext context);
}
