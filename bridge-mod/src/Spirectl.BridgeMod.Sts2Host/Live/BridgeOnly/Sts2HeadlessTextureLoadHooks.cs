using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Headless texture-load crash guard.
///
/// The embedded game runs without an interactive rendering context. Most textures reach the
/// browser as raw bytes over <c>/res</c> and are never loaded game-side, but some UI forces a
/// game-side load through <c>AssetCache</c> (which calls <c>ResourceLoader.Load</c>). Loading a
/// texture resource that way hard-crashes (segfaults) the process in headless. The forced Act 2
/// (Hive) Ancient event (e.g. Tezcatara) hits this while building its option UI: it accesses
/// enchantment / relic icons, and the run dies on the texture load (the log ends on
/// "Asset not cached: &lt;...&gt;.png"). Redirecting the asset (e.g. to the missing-icon
/// placeholder) does not help — the crash is in the load itself, for any texture.
///
/// This prefixes <c>AssetCache.GetTexture2D</c> and <c>AssetCache.GetCompressedTexture2D</c> to
/// return empty placeholder textures without loading anything. Game-side textures are purely
/// cosmetic for the headless auto-player (the browser renders from <c>/res</c>), so this trades
/// game-side visuals for headless stability and lets the bot pass forced Ancient events.
/// Scenes, materials and other resources are unaffected (they load normally and the game logic
/// depends on them).
/// </summary>
internal static class Sts2HeadlessTextureLoadHooks
{
    private static readonly object Sync = new();
    private static bool _installed;
    private static CompressedTexture2D? _emptyCompressed;
    private static PlaceholderTexture2D? _placeholder;

    public static bool IsInstalled
    {
        get
        {
            lock (Sync)
            {
                return _installed;
            }
        }
    }

    public static void Install(ILogStream logStream)
    {
        lock (Sync)
        {
            if (_installed)
            {
                return;
            }

            Sts2MonoModNativeDependencies.EnsureLoaded(logStream);

            var compressedTarget = typeof(AssetCache).GetMethod(
                nameof(AssetCache.GetCompressedTexture2D),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            var textureTarget = typeof(AssetCache).GetMethod(
                nameof(AssetCache.GetTexture2D),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            var compressedPrefix = typeof(Sts2HeadlessTextureLoadHooks).GetMethod(
                nameof(GetCompressedTexture2DPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            var texturePrefix = typeof(Sts2HeadlessTextureLoadHooks).GetMethod(
                nameof(GetTexture2DPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            if (compressedTarget is null || textureTarget is null || compressedPrefix is null || texturePrefix is null)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.assets.headless-texture",
                    "Unable to install headless texture-load guard; AssetCache texture getters were not found.");
                return;
            }

            try
            {
                var harmony = new Harmony("spirectl.assets.headless-texture-load");
                harmony.Patch(compressedTarget, prefix: new HarmonyMethod(compressedPrefix));
                harmony.Patch(textureTarget, prefix: new HarmonyMethod(texturePrefix));
            }
            catch (Exception ex)
            {
                logStream.Write(
                    BridgeLogLevel.Warn,
                    "bridge.assets.headless-texture",
                    $"Skipping headless texture-load guard because Harmony patching failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            _installed = true;
            logStream.Write(
                BridgeLogLevel.Info,
                "bridge.assets.headless-texture",
                "Installed headless texture-load guard; game-side texture loads resolve to empty placeholders.");
        }
    }

    private static bool GetCompressedTexture2DPrefix(ref CompressedTexture2D __result)
    {
        __result = _emptyCompressed ??= new CompressedTexture2D();
        return false;
    }

    private static bool GetTexture2DPrefix(ref Texture2D __result)
    {
        __result = _placeholder ??= new PlaceholderTexture2D();
        return false;
    }
}
