using HarmonyLib;
using HotMod.Shell.Diagnostics;
using HotMod.Shell.DevOverlay;
using HotMod.Shell.Runtime;

namespace HotMod.Shell;

public static class ModEntry
{
    private const string HarmonyOwnerId = "com.spirectl.hotmod.template";

    public static void Initialize()
    {
        var options = HotRuntimeOptions.FromEnvironment(AppContext.BaseDirectory);
        var ownerError = ValidatePatchOwnerId(HarmonyOwnerId);
        if (ownerError is not null)
        {
            ReloadLog.WriteError("hot mod harmony patching failed", ownerError);
            HotRuntime.Current.Initialize(options);
            HotRuntime.Current.RecordRestartRequiredError(ownerError);
            InstallOverlayIfEnabled(options);
            return;
        }

        try
        {
            var harmony = new Harmony(HarmonyOwnerId);
            harmony.PatchAll(typeof(ModEntry).Assembly);
        }
        catch (Exception exception)
        {
            var patchError = CreatePatchFailure(exception);
            ReloadLog.WriteError("hot mod harmony patching failed", patchError);
            HotRuntime.Current.RecordRestartRequiredError(patchError);
        }

        HotRuntime.Current.Initialize(options);
        InstallOverlayIfEnabled(options);
    }

    public static ReloadError? ValidatePatchOwnerId(string? ownerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            return null;
        }

        return new ReloadError(
            ReloadErrorCode.ReloadPatchOwnerMissing,
            ReloadPhase.Patch,
            "Harmony patch owner id is missing. A stable owner id is required for shell-owned patch methods.",
            ExceptionType: null,
            ExceptionMessage: null,
            RestartRequired: true);
    }

    public static ReloadError CreatePatchFailure(Exception exception)
    {
        return new ReloadError(
            ReloadErrorCode.ReloadHookSignatureChanged,
            ReloadPhase.Patch,
            "Harmony patching failed. A restart is required after fixing shell patch ownership or hook signatures.",
            exception.GetType().FullName,
            exception.Message,
            RestartRequired: true);
    }

    private static void InstallOverlayIfEnabled(HotRuntimeOptions options)
    {
        if (options.DevOverlayEnabled)
        {
            HotReloadStatusOverlay.TryInstall(HotRuntime.Current);
        }
    }
}
