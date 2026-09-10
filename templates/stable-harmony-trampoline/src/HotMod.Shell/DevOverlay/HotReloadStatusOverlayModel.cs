using HotMod.Shell.Runtime;

namespace HotMod.Shell.DevOverlay;

public sealed record HotReloadStatusOverlayModel(
    string ShellModId,
    int ActiveGeneration,
    string LastReloadStatus,
    long? LastReloadDurationMs,
    bool? PreviousCollected,
    bool RestartRequired)
{
    public static HotReloadStatusOverlayModel FromStatus(SpirectlHotReloadStatus status)
    {
        return new HotReloadStatusOverlayModel(
            status.ShellModId,
            status.ActiveGeneration,
            status.ReloadInProgress ? "reloading" : status.LastReloadReport?.Status.ToString().ToLowerInvariant() ?? "idle",
            status.LastReloadReport?.DurationMs,
            status.LastReloadReport?.PreviousCollected,
            status.RestartRequired);
    }

    public string ToDisplayText()
    {
        var duration = LastReloadDurationMs is null ? "-" : $"{LastReloadDurationMs} ms";
        var collected = PreviousCollected switch
        {
            true => "collected",
            false => "not collected",
            null => "-"
        };
        var restart = RestartRequired ? "restart required" : "reloadable";
        return $"shell: {ShellModId}\ngeneration: {ActiveGeneration}\nstatus: {LastReloadStatus}\nduration: {duration}\nunload: {collected}\n{restart}";
    }
}
