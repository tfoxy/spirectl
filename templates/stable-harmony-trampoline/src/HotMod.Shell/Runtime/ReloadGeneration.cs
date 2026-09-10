using HotMod.Contracts;

namespace HotMod.Shell.Runtime;

public sealed class ReloadGeneration
{
    private bool _disposeAttempted;

    public ReloadGeneration(
        int Generation,
        HotLogicManifest Manifest,
        IHotLogic Logic,
        PluginLoadContext LoadContext,
        string SourceAssemblyPath,
        string ShadowAssemblyPath,
        DateTimeOffset LoadedAt)
    {
        this.Generation = Generation;
        this.Manifest = Manifest;
        this.Logic = Logic;
        this.LoadContext = LoadContext;
        this.LoadContextWeakReference = new WeakReference(LoadContext, trackResurrection: false);
        this.SourceAssemblyPath = SourceAssemblyPath;
        this.ShadowAssemblyPath = ShadowAssemblyPath;
        this.LoadedAt = LoadedAt;
    }

    public int Generation { get; }

    public HotLogicManifest Manifest { get; }

    public IHotLogic? Logic { get; private set; }

    public PluginLoadContext? LoadContext { get; private set; }

    public WeakReference LoadContextWeakReference { get; }

    public string SourceAssemblyPath { get; }

    public string ShadowAssemblyPath { get; }

    public DateTimeOffset LoadedAt { get; }

    public DisposeLogicResult DisposeLogic()
    {
        if (_disposeAttempted)
        {
            return new DisposeLogicResult(Disposed: true, Exception: null);
        }

        _disposeAttempted = true;

        try
        {
            Logic?.Dispose();
            return new DisposeLogicResult(Disposed: true, Exception: null);
        }
        catch (Exception exception)
        {
            return new DisposeLogicResult(Disposed: false, exception);
        }
    }

    public void RequestUnload()
    {
        var loadContext = LoadContext;
        Logic = null;
        LoadContext = null;
        loadContext?.Unload();
    }
}

public sealed record DisposeLogicResult(bool Disposed, Exception? Exception);
