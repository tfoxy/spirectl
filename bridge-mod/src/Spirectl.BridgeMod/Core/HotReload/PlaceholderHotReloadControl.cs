namespace Spirectl.Sts2.Core.HotReload;


public sealed class PlaceholderHotReloadControl : IHotReloadControl
{
    private static readonly HotReloadNoticeSnapshot UnsupportedNotice = new(
        "hot-reload-shell-unsupported",
        "Hot-reload control requires a live M57-compatible shell.");

    public HotReloadStatusResultSnapshot GetStatus(HotReloadStatusRequestSnapshot request)
    {
        return HotReloadStatusResultSnapshot.Success(
            new HotReloadShellStatusSnapshot(
                Supported: false,
                Protocol: new HotReloadProtocolSnapshot("spirectl.m57.hot-reload-shell", 0),
                ShellModId: request.ShellModId,
                ShellProtocolVersion: 0,
                ActiveGeneration: 0,
                ExpectedLogicArtifactPath: string.Empty,
                ContractVersion: 0,
                ReloadInProgress: false,
                LastReloadReport: null,
                RestartRequired: false,
                Notices: [UnsupportedNotice]),
            [UnsupportedNotice]);
    }

    public Task<HotReloadOperationResult> RequestReloadAsync(HotReloadRequestSnapshot request)
    {
        return Task.FromResult(HotReloadOperationResult.Failure(
            "hot_reload_shell_unsupported",
            "Hot-reload control requires a live M57-compatible shell.",
            [UnsupportedNotice]));
    }
}
