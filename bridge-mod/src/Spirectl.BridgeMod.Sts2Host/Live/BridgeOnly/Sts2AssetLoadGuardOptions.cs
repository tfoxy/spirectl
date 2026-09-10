using System;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Opt-in switch for the bridge's asset-load crash guards
/// (<see cref="Sts2HeadlessTextureLoadHooks"/> and
/// <see cref="Sts2AncientEventHeadlessHooks"/>).
///
/// Those guards trade real game-side textures / event visuals for stability by
/// returning empty placeholders, which corrupts what renders. They are only
/// needed by the auto-player, whose software-rendered (xvfb) launch still
/// segfaults on <c>ResourceLoader.Load</c> for some assets. So they are
/// disabled by default and enabled only when the launcher sets
/// <see cref="EnvVar"/>; the auto-player opts in via its launch config.
///
/// This is deliberately a temporary escape hatch: once the underlying load
/// crash under software rendering is root-caused, the env var and both guard
/// classes should be removed.
/// </summary>
internal static class Sts2AssetLoadGuardOptions
{
    public const string EnvVar = "SPIRECTL_BRIDGE_ASSET_LOAD_GUARD";

    /// <summary>
    /// True only when <see cref="EnvVar"/> is set to a truthy value
    /// (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>, case-insensitive). Default false.
    /// </summary>
    public static bool IsEnabled() => ParseTruthy(Environment.GetEnvironmentVariable(EnvVar));

    private static bool ParseTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };
    }
}
