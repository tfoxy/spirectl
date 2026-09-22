using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.EncounterVisuals;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2AssetExtractProvider
{
    private static string ResolveLoadPath(AssetExtractRequestSnapshot request)
    {
        var loadPath = request.LoadPath.Replace('\\', '/');
        if (loadPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            || loadPath.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
        {
            return loadPath;
        }

        if (!string.IsNullOrWhiteSpace(loadPath))
        {
            var localized = ProjectSettings.LocalizePath(loadPath);
            if (localized.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                || localized.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
            {
                return localized;
            }
        }

        if (string.Equals(request.SourceRoot, "resources", StringComparison.OrdinalIgnoreCase))
        {
            return $"res://{request.SourcePath.Replace('\\', '/')}";
        }

        return loadPath;
    }

    private static bool TryExtractFontBytes(
        string loadPath,
        AssetExtractRequestSnapshot request,
        out AssetExtractOperationResult result)
    {
        result = null!;
        if (!IsFontResourcePath(loadPath))
        {
            return false;
        }

        // Prefer the embedded font file the engine already holds. The raw resource
        // bytes at a `res://….ttf` path are the imported, Godot-compressed FontFile
        // container (`RSCC…`) in an exported game, which is not a browser font; the
        // loaded `FontFile.Data` is the original TTF/OTF the game shipped.
        var bytes = TryReadFontFileData(loadPath);
        string[] notes = ["Extracted the embedded font file from the loaded Godot FontFile resource."];
        if (bytes.Length == 0 || !TryDetectBrowserFontFormat(bytes, out _))
        {
            bytes = TryReadRawResourceBytes(loadPath);
            notes = ["Extracted raw font bytes from the Godot resource path."];
        }
        if (bytes.Length == 0 || !TryDetectBrowserFontFormat(bytes, out _))
        {
            bytes = TryReadImportedFontDataBytes(loadPath, out var importedFontDataPath);
            if (bytes.Length > 0)
            {
                notes =
                [
                    "Extracted browser-consumable bytes from the Godot imported font payload.",
                    $"Imported font data path: {importedFontDataPath}",
                ];
            }
        }

        if (bytes.Length == 0)
        {
            result = FontBytesUnavailable(request, loadPath);
            return true;
        }

        if (!TryDetectBrowserFontFormat(bytes, out var extension))
        {
            result = FontBytesUnavailable(
                request,
                loadPath,
                $"The extracted payload begins with '{PreviewMagic(bytes)}', which is not a browser-consumable TTF, OTF, WOFF, or WOFF2 font header.");
            return true;
        }

        result = AssetExtractOperationResult.SuccessFont(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: extension,
            contentType: FontContentType(extension),
            contents: bytes,
            renderMode: "raw-font-file",
            notes: notes);
        return true;
    }

    private static bool TryDetectBrowserFontFormat(byte[] bytes, out string format)
    {
        format = string.Empty;
        if (bytes.Length < 4)
        {
            return false;
        }

        if (bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            format = "ttf";
            return true;
        }

        var tag = System.Text.Encoding.ASCII.GetString(bytes, 0, 4);
        format = tag switch
        {
            "true" or "typ1" => "ttf",
            "OTTO" => "otf",
            "wOFF" => "woff",
            "wOF2" => "woff2",
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(format);
    }

    private static string PreviewMagic(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "<empty>";
        }

        var count = Math.Min(8, bytes.Length);
        return Convert.ToHexString(bytes, 0, count);
    }

    private static byte[] TryReadFontFileData(string loadPath)
    {
        try
        {
            if (!ResourceLoader.Exists(loadPath))
            {
                return [];
            }

            // A `.ttf`/`.otf` import loads as a FontFile whose `Data` is the original
            // font file bytes (what the engine embedded), unlike the compressed
            // resource container stored at the logical resource path.
            return ResourceLoader.Load(loadPath) is FontFile font
                ? font.Data ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static byte[] TryReadImportedFontDataBytes(string loadPath, out string importedFontDataPath)
    {
        importedFontDataPath = string.Empty;
        var importMetadata = TryReadRawResourceBytes($"{loadPath}.import");
        if (importMetadata.Length == 0)
        {
            return [];
        }

        var text = System.Text.Encoding.UTF8.GetString(importMetadata);
        importedFontDataPath = ExtractImportedFontDataPath(text);
        return string.IsNullOrWhiteSpace(importedFontDataPath)
            ? []
            : TryReadRawResourceBytes(importedFontDataPath);
    }

    private static string ExtractImportedFontDataPath(string importMetadata)
    {
        const string Prefix = "res://.godot/imported/";
        const string Suffix = ".fontdata";
        var start = importMetadata.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = importMetadata.IndexOf(Suffix, start, StringComparison.Ordinal);
        return end < 0
            ? string.Empty
            : importMetadata[start..(end + Suffix.Length)];
    }

    private static bool IsFontResourcePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".ttf" or ".otf" or ".woff" or ".woff2" or ".fontdata";
    }

    private static string FontFormatFromPath(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension != "fontdata")
        {
            return extension;
        }

        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        foreach (var originalExtension in new[] { ".ttf-", ".otf-", ".woff2-", ".woff-" })
        {
            if (normalized.Contains(originalExtension, StringComparison.Ordinal))
            {
                return originalExtension.Trim('.', '-');
            }
        }

        return "ttf";
    }

    private static bool TryExtractRawResourceBytes(
        string loadPath,
        AssetExtractRequestSnapshot request,
        out AssetExtractOperationResult result)
    {
        result = null!;
        var extension = Path.GetExtension(loadPath).ToLowerInvariant();
        if (extension is not ".ico")
        {
            return false;
        }

        var bytes = TryReadRawResourceBytes(loadPath);

        if (bytes.Length == 0)
        {
            return false;
        }

        result = SuccessRawResource(request, loadPath, bytes);
        return true;
    }

    // Serve a localization table (res://localization/<lang>/<table>.json) as raw JSON via
    // Godot.FileAccess so the renderer can fetch it through the asset seam. Path-faithful
    // and UNMERGED (one file per request). Returns false for non-loc paths so the
    // normal resource-render dispatch continues.
    private bool TryExtractLocalizationJson(
        string loadPath,
        AssetExtractRequestSnapshot request,
        out AssetExtractOperationResult result)
    {
        result = null!;
        if (!loadPath.StartsWith("res://localization/", StringComparison.OrdinalIgnoreCase)
            || !loadPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Godot.FileAccess.FileExists(loadPath))
        {
            result = Failure(request, "load_path", loadPath, "Localization table not found at the requested res:// path.");
            return true;
        }

        using var file = Godot.FileAccess.Open(loadPath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            result = Failure(request, "load_path", loadPath, "Godot FileAccess.Open returned null for the localization table.");
            return true;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(file.GetAsText());
        result = AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: "json",
            contentType: "application/json",
            contents: bytes,
            renderMode: "localization-table",
            notes: []);
        return true;
    }

    private static byte[] TryReadRawResourceBytes(string loadPath)
    {
        var extension = Path.GetExtension(loadPath).ToLowerInvariant();
        if (extension is ".ico")
        {
            var packedBytes = TryReadRawResourceBytesFromPack(loadPath);
            if (packedBytes.Length > 0)
            {
                return packedBytes;
            }
        }

        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes(loadPath);
            if (bytes.Length > 0)
            {
                return bytes;
            }
        }
        catch
        {
        }

        return TryReadRawResourceBytesFromPack(loadPath);
    }

    public static IReadOnlyDictionary<string, byte[]> PreloadRawResources(params string[] loadPaths)
    {
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var loadPath in loadPaths)
        {
            if (string.IsNullOrWhiteSpace(loadPath))
            {
                continue;
            }

            try
            {
                var bytes = TryReadRawResourceBytes(loadPath);
                if (bytes.Length > 0)
                {
                    resources[loadPath] = bytes;
                }
            }
            catch
            {
            }
        }

        return resources;
    }

    public static IReadOnlyDictionary<string, byte[]> PreloadRawResourcesDeferred(params string[] loadPaths)
    {
        var resources = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        Callable.From(() =>
        {
            foreach (var loadPath in loadPaths)
            {
                if (string.IsNullOrWhiteSpace(loadPath))
                {
                    continue;
                }

                try
                {
                    var bytes = TryReadRawResourceBytes(loadPath);
                    if (bytes.Length > 0)
                    {
                        resources[loadPath] = bytes;
                    }
                }
                catch
                {
                }
            }
        }).CallDeferred();

        return resources;
    }

    private static byte[] TryReadRawResourceBytesFromPack(string loadPath)
    {
        var packPath = CandidateGameDirectories()
            .Select(directory => Path.Combine(directory, "SlayTheSpire2.pck"))
            .FirstOrDefault(File.Exists);
        if (packPath is null)
        {
            return [];
        }

        var normalizedLoadPath = NormalizePackLogicalPath(loadPath);
        try
        {
            using var stream = File.OpenRead(packPath);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != 0x43504447)
            {
                return [];
            }

            var version = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            if (version is not (2 or 3 or 4))
            {
                return [];
            }

            var packFlags = reader.ReadUInt32();
            var encryptedDirectory = (packFlags & 1) != 0;
            var sparseBundle = (packFlags & 4) != 0;
            var fileBase = reader.ReadUInt64();
            if (version is 3 or 4)
            {
                var directoryOffset = reader.ReadUInt64();
                if (encryptedDirectory && version == 4 && sparseBundle)
                {
                    _ = reader.ReadBytes(32);
                }

                stream.Seek((long)directoryOffset, SeekOrigin.Begin);
            }
            else
            {
                for (var i = 0; i < 16; i++)
                {
                    _ = reader.ReadUInt32();
                }
            }

            if (encryptedDirectory)
            {
                return [];
            }

            var fileCount = reader.ReadInt32();
            for (var i = 0; i < fileCount; i++)
            {
                var pathLength = reader.ReadUInt32();
                var logicalPath = NormalizePackLogicalPath(DecodePackUtf8(reader.ReadBytes((int)pathLength)));
                var offset = reader.ReadUInt64();
                var size = reader.ReadUInt64();
                _ = reader.ReadBytes(16);
                var fileFlags = reader.ReadUInt32();
                if (!string.Equals(logicalPath, normalizedLoadPath, StringComparison.Ordinal)
                    || (fileFlags & 1) != 0
                    || (fileFlags & 2) != 0
                    || (fileFlags & 4) != 0
                    || sparseBundle)
                {
                    continue;
                }

                stream.Seek((long)(offset + fileBase), SeekOrigin.Begin);
                return reader.ReadBytes((int)size);
            }
        }
        catch
        {
        }

        return [];
    }

    private static IEnumerable<string> CandidateGameDirectories()
    {
        yield return Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            yield return AppContext.BaseDirectory;
        }

        var processPath = System.Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return directory;
            }
        }
    }

    private static string NormalizePackLogicalPath(string path)
        => path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? path.Replace('\\', '/')
            : $"res://{path.TrimStart('/').Replace('\\', '/')}";

    private static string DecodePackUtf8(byte[] bytes)
        => bytes.Length > 0 && bytes[^1] == 0
            ? System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1)
            : System.Text.Encoding.UTF8.GetString(bytes);

    private static AssetExtractOperationResult SuccessRawResource(
        AssetExtractRequestSnapshot request,
        string loadPath,
        byte[] bytes)
    {
        var extension = Path.GetExtension(loadPath).ToLowerInvariant();
        return AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: extension.TrimStart('.'),
            contentType: extension switch
            {
                ".ico" => "image/x-icon",
                _ => "application/octet-stream",
            },
            contents: bytes,
            renderMode: "raw-resource-file",
            notes: ["Extracted raw bytes from the Godot resource path."]);
    }

    private static string FontContentType(string extension)
        => extension.TrimStart('.').ToLowerInvariant() switch
        {
            "ttf" => "font/ttf",
            "otf" => "font/otf",
            "woff" => "font/woff",
            "woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

    private static AssetExtractOperationResult FontBytesUnavailable(
        AssetExtractRequestSnapshot request,
        string loadPath,
        string? note = null)
        => AssetExtractOperationResult.Failure(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            code: AssetExtractFailureCode.RuntimeFailure,
            message: "The live bridge could not extract reusable font bytes.",
            details:
            [
                new AssetExtractDetail(
                    Field: "font-bytes-unavailable",
                    Value: loadPath,
                    Note: note ?? "The active runtime exposed a font path or font-like resource, but the bridge could not read reusable browser font bytes from it."),
            ]);

    private static bool TryParseCharacterVisualRequest(
        AssetExtractRequestSnapshot request,
        out CharacterVisualRequest parsed)
    {
        parsed = null!;
        var raw = !string.IsNullOrWhiteSpace(request.LoadPath)
            ? request.LoadPath
            : request.SourcePath;
        if (!TryParseModelAssetPath(raw, out var modelType, out var modelId, out var variant)
            || modelType != "characters"
            || variant is "characterselectbg" or "characterselectbgspinestill" or "energycounter" or "merchantanim" or "restsiteanim")
        {
            return false;
        }

        parsed = new CharacterVisualRequest(modelId, variant);
        return true;
    }

    // scene-subtree://<res-scene>?node=<sceneRelativePath>[&posing knobs] -> render ONLY the addressed subtree of
    // the scene (couch-coop's room-backdrop still: the merchant shop's SceneContainer/BgContainer; and its
    // effect-still lane, which addresses a card's highlight or a rarity-glow emitter). The grammar, and why each
    // optional knob exists, live in the Godot-free `Sts2SceneSubtreeStillKey` so they are unit-testable without
    // the game. The parse REWRITES the request onto the ordinary res:// PackedScene path with the node path and
    // the options riding the snapshot — ExtractPackedSceneAsync then detaches that subtree from a
    // never-tree-entered instantiation.
    private static bool TryParseSceneSubtreeRequest(
        AssetExtractRequestSnapshot request,
        out AssetExtractRequestSnapshot rewritten)
    {
        rewritten = null!;
        var raw = !string.IsNullOrWhiteSpace(request.LoadPath) ? request.LoadPath : request.SourcePath;
        if (Sts2SceneSubtreeStillKey.TryParse(raw) is not { } parsed)
        {
            return false;
        }

        rewritten = request with
        {
            SourcePath = parsed.ScenePath,
            LoadPath = parsed.ScenePath,
            SceneSubtreeNodePath = parsed.NodePath,
            SceneSubtreeStillOptions = parsed.Options.IsDefault ? null : parsed.Options,
        };
        return true;
    }

    // spine://<scene-path-no-res-prefix>?node=<sceneRelativePath>&anim=<name> -> render the named Spine
    // animation of the addressed SpineSprite as a Timeline clip. The scene path is a res:// resource path
    // with the res:// prefix dropped (parallels res://X <-> spine://X); `node` (optional, defaults to the
    // sole/first SpineSprite) and `anim` (required) are query selectors. Case-preserving: Spine
    // animation/node names are case-sensitive, unlike TryParseModelAssetPath which lowercases.
    private static bool TryParseSpineClipRequest(
        AssetExtractRequestSnapshot request,
        out SpineClipRequest parsed)
    {
        parsed = null!;
        var raw = (!string.IsNullOrWhiteSpace(request.LoadPath)
            ? request.LoadPath
            : request.SourcePath).Replace('\\', '/').Trim();
        const string prefix = "spine://";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = raw[prefix.Length..];
        var queryStart = rest.IndexOf('?');
        var scenePart = (queryStart >= 0 ? rest[..queryStart] : rest).Trim().Trim('/');
        var queryPart = queryStart >= 0 ? rest[(queryStart + 1)..] : string.Empty;
        if (string.IsNullOrWhiteSpace(scenePart))
        {
            return false;
        }

        string? node = null;
        string? anim = null;
        // SERVER-SIDE size policy (a host appends these; the bare CLI omits them → png at the full sample rate).
        var codec = "png";
        var fps = 0;
        var quality = 0.85f;
        var still = false;
        // Optional selectors (a host appends these; the bare CLI omits them so its key/URL is byte-identical to
        // today). `skin` = the runtime skin to apply per lane (#3); `skel` = a res:// skeleton-data path for the
        // standalone fallback lane (#8), validated to a res:// prefix; `mat` = the live shader-material signature
        // (R9) the render resolves back to the LIVE uniform values. `v` (version bump) is a client-side
        // cache-buster only (folded into the mod route key) — it changes nothing spirectl renders, so it is
        // ignored here (falls through the else, like any unrecognised param).
        string? skin = null;
        string? skel = null;
        string? mat = null;
        // `t` (R10) = the STILL sample time in seconds (Sts2SpineStillFrame's explicit override). Only a paused
        // track's client sends it; absent → the mid/end heuristic, i.e. the byte-identical pre-R10 bake.
        float? stillTime = null;
        foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
            if (key.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                node = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            else if (key.Equals("anim", StringComparison.OrdinalIgnoreCase))
            {
                anim = value;
            }
            else if (key.Equals("codec", StringComparison.OrdinalIgnoreCase))
            {
                codec = value.ToLowerInvariant() == "webp" ? "webp" : "png";
            }
            else if (key.Equals("fps", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && f > 0)
                {
                    fps = f;
                }
            }
            else if (key.Equals("q", StringComparison.OrdinalIgnoreCase) || key.Equals("quality", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && q > 0)
                {
                    // Accept either a 0..1 fraction or a 1..100 percentage.
                    quality = (float)Math.Clamp(q > 1d ? q / 100d : q, 0.01d, 1d);
                }
            }
            else if (key.Equals("still", StringComparison.OrdinalIgnoreCase))
            {
                still = value is "1" or "true" or "on" or "yes";
            }
            else if (key.Equals("skin", StringComparison.OrdinalIgnoreCase))
            {
                // Case-preserving: Spine skin names are case-sensitive (like anim/node above).
                skin = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            else if (key.Equals("skel", StringComparison.OrdinalIgnoreCase))
            {
                // Must be a res:// resource path — validate the prefix so a garbled/hostile query can never point
                // ResourceLoader.Load at anything else. An invalid value drops to null (scene-addressed render only).
                skel = value.StartsWith("res://", StringComparison.Ordinal) ? value : null;
            }
            else if (key.Equals("mat", StringComparison.OrdinalIgnoreCase))
            {
                // R9: an opaque signature minted by Sts2SpineMaterialKey (16 lowercase hex chars). Used ONLY as a
                // lookup key into the live-uniform snapshot store, so an unrecognised value simply misses and the
                // render falls back to the offline scene material — it can never address a resource.
                mat = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            else if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                // R10: the STILL sample time in seconds. Non-negative + finite only; anything else drops to null
                // (the mid/end heuristic). ChooseSampleTime additionally clamps it to the clip's real duration, so
                // an overshooting wall-clock track time lands on the last frame rather than failing.
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                    && t >= 0f && !float.IsNaN(t) && !float.IsInfinity(t))
                {
                    stillTime = t;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(anim) && !still)
        {
            // Animation clips require an explicit anim. A still (&still=1) may omit it: the render
            // path falls back to a default preview animation (ResolveSpinePreviewAnimation ->
            // preview_animation or "idle_loop"), so a scheme-agnostic caller can ask for a single
            // frame without knowing an animation name (the recon/restructured view has none).
            return false;
        }

        parsed = new SpineClipRequest(
            "res://" + Uri.UnescapeDataString(scenePart), node, anim ?? string.Empty, codec, fps, quality, still, skin, skel, mat,
            // A sample time is only meaningful for a collapsed STILL (an animated clip renders every frame), so an
            // animated request drops it — the render is byte-identical either way, and this keeps the field honest.
            still ? stillTime : null);
        return true;
    }

    private static bool TryParseRelicVisualRequest(
        AssetExtractRequestSnapshot request,
        out RelicVisualRequest parsed)
    {
        parsed = null!;
        var raw = !string.IsNullOrWhiteSpace(request.LoadPath)
            ? request.LoadPath
            : request.SourcePath;
        if (!TryParseModelAssetPath(raw, out var modelType, out var modelId, out var variant)
            || modelType != "relics"
            || variant is not ("iconoutline" or "bigicon"))
        {
            return false;
        }

        parsed = new RelicVisualRequest(modelId, variant);
        return true;
    }

    private static bool TryResolveModelResourceRequest(
        AssetExtractRequestSnapshot request,
        out AssetExtractRequestSnapshot aliasRequest)
    {
        aliasRequest = null!;
        var raw = !string.IsNullOrWhiteSpace(request.LoadPath)
            ? request.LoadPath
            : request.SourcePath;
        if (!TryParseModelAssetPath(raw, out var modelType, out var modelId, out var variant))
        {
            return false;
        }

        string? path = modelType switch
        {
            "cards" when variant == "image" && Sts2ModelResolver.TryResolveCard(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractCardPath(model),
            "relics" when variant == "icon" && Sts2ModelResolver.TryResolveRelic(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractRelicIconPath(model),
            "potions" when variant == "icon" && Sts2ModelResolver.TryResolvePotion(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractPotionPath(model),
            "potions" when variant == "outline" && Sts2ModelResolver.TryResolvePotion(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "OutlinePath"),
            "characters" when variant == "characterselectbg" && TryResolveCharacterModel(modelId, out var model) => Sts2AssetModelResourceResolver.CharacterSelectBackgroundPath(model, modelId),
            "characters" when variant == "energycounter" && TryResolveCharacterModel(modelId, out var model) => model.EnergyCounterPath,
            "characters" when variant == "merchantanim" && TryResolveCharacterModel(modelId, out var model) => model.MerchantAnimPath,
            "characters" when variant == "restsiteanim" && TryResolveCharacterModel(modelId, out var model) => model.RestSiteAnimPath,
            "monsters" when variant == "visuals" && TryResolveMonsterModel(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractMonsterVisualPath(model),
            "events" when variant == "backgroundscene" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "BackgroundScenePath"),
            "events" when variant == "initialportrait" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "InitialPortraitPath"),
            "events" when variant == "mapicon" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "MapIconPath"),
            "events" when variant == "mapiconoutline" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "MapIconOutlinePath"),
            "events" when variant == "runhistoryicon" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractResourcePathProperty(model, "RunHistoryIcon"),
            "events" when variant == "runhistoryiconoutline" && Sts2ModelResolver.TryResolveEvent(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "RunHistoryIconOutlinePath"),
            "acts" when variant == "backgroundscene" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.BackgroundScenePath,
            "acts" when variant == "restsitebackground" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.RestSiteBackgroundPath,
            "acts" when variant == "maptopbg" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.MapTopBgPath,
            "acts" when variant == "mapmidbg" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.MapMidBgPath,
            "acts" when variant == "mapbotbg" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.MapBotBgPath,
            "acts" when variant == "chestspine" && Sts2ModelResolver.TryResolveAct(modelId, out var model) => model.ChestSpineResourcePath,
            "acts" when variant.StartsWith("backgroundlayer/", StringComparison.Ordinal) && Sts2ModelResolver.TryResolveAct(modelId, out var model) => BackgroundLayerPath(model.Id.Entry, variant),
            "encounters" when variant == "scene" && Sts2ModelResolver.TryResolveEncounter(modelId, out var model) => Sts2AssetModelResourceResolver.TryExtractFirstStringProperty(model, "ScenePath"),
            "encounters" when variant == "background" && Sts2ModelResolver.TryResolveEncounter(modelId, out var model) && EncounterHasCustomBackground(model) => CustomBackgroundRootPath(model.Id.Entry),
            "encounters" when variant.StartsWith("backgroundlayer/", StringComparison.Ordinal) && Sts2ModelResolver.TryResolveEncounter(modelId, out var model) && EncounterHasCustomBackground(model) => BackgroundLayerPath(model.Id.Entry, variant),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // An act chest spine bundles a normal skin plus a stroke/outline skin; the union
        // bake would render the white outline halo and inflate the bounds. Carry the
        // act's authoritative normal skin name so the standalone bake renders only it.
        string? preferredSkinName = null;
        if (modelType == "acts"
            && variant == "chestspine"
            && Sts2ModelResolver.TryResolveAct(modelId, out var chestAct)
            && chestAct is not null
            && !string.IsNullOrWhiteSpace(chestAct.ChestSpineSkinNameNormal))
        {
            preferredSkinName = chestAct.ChestSpineSkinNameNormal;
        }

        aliasRequest = request with
        {
            SourceRoot = "model",
            SourcePath = (modelType == "characters" && variant == "characterselectbgspinestill")
                || (modelType == "events" && variant == "backgroundspinestill")
                || (modelType == "encounters" && variant == "backgroundspinestill")
                ? raw
                : path!,
            LoadPath = path!,
            PreferredSkinName = preferredSkinName,
        };
        return true;
    }

    private static bool TryParseModelAssetPath(string raw, out string modelType, out string modelId, out string variant)
    {
        modelType = string.Empty;
        modelId = string.Empty;
        variant = string.Empty;
        var normalized = raw.Replace('\\', '/').Trim().ToLowerInvariant();
        const string prefix = "model://";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = normalized[prefix.Length..].Split('/', StringSplitOptions.TrimEntries);
        // At least type/id/variant. The variant may itself contain '/' (e.g.
        // backgroundLayer/<stem>); join the trailing segments back together.
        if (parts.Length < 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        modelType = parts[0];
        modelId = parts[1];
        variant = string.Join('/', parts[2..]);
        return true;
    }

    private static bool TryParseCombatBackgroundAliasRequest(
        AssetExtractRequestSnapshot request,
        out CombatBackgroundAliasRequest parsed)
    {
        parsed = null!;
        var raw = request.LoadPath.Replace('\\', '/').Trim().ToLowerInvariant();
        const string prefix = "composed://";
        var parts = raw.StartsWith(prefix, StringComparison.Ordinal)
            ? raw[prefix.Length..].Split('/', StringSplitOptions.TrimEntries)
            : [];
        if (parts.Length != 3
            || !string.Equals(parts[0], "combat-background", StringComparison.Ordinal)
            || !string.Equals(parts[2], "image", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || !parts[1].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-'))
        {
            return false;
        }

        var backgroundId = parts[1];
        parsed = new CombatBackgroundAliasRequest(
            backgroundId,
            $"res://scenes/backgrounds/{backgroundId}/{backgroundId}_background.tscn");
        return true;
    }

    private static bool TryParseEncounterScenePackageRequest(
        AssetExtractRequestSnapshot request,
        out EncounterScenePackageRequest parsed)
    {
        parsed = null!;
        var raw = request.LoadPath.Replace('\\', '/').Trim().ToLowerInvariant();
        var parts = TryParseComposedAssetPath(raw);
        if (parts.Length != 3
            || !string.Equals(parts[0], "encounters", StringComparison.Ordinal)
            || !string.Equals(parts[2], "scene-package", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || !parts[1].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-'))
        {
            return false;
        }

        parsed = new EncounterScenePackageRequest(parts[1]);
        return true;
    }

    private static bool TryParseEncounterRenderTargetRequest(
        AssetExtractRequestSnapshot request,
        out EncounterRenderTargetRequest parsed)
    {
        parsed = null!;
        var raw = request.LoadPath.Replace('\\', '/').Trim().ToLowerInvariant();
        var parts = TryParseComposedAssetPath(raw);
        if (parts.Length == 4
            && string.Equals(parts[0], "encounters", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[1])
            && string.Equals(parts[2], "background", StringComparison.Ordinal)
            && string.Equals(parts[3], "image", StringComparison.Ordinal))
        {
            parsed = new EncounterRenderTargetRequest(parts[1], "background", null, null);
            return true;
        }

        if (parts.Length == 6
            && string.Equals(parts[0], "encounters", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[1])
            && string.Equals(parts[2], "visual-state", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[3])
            && string.Equals(parts[4], "overlay", StringComparison.Ordinal)
            && string.Equals(parts[5], "image", StringComparison.Ordinal))
        {
            parsed = new EncounterRenderTargetRequest(parts[1], "overlay", parts[3], null);
            return true;
        }

        if (parts.Length == 7
            && string.Equals(parts[0], "encounters", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[1])
            && string.Equals(parts[2], "visual-part", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[3])
            && string.Equals(parts[4], "state", StringComparison.Ordinal)
            && IsVirtualAssetId(parts[5])
            && string.Equals(parts[6], "image", StringComparison.Ordinal))
        {
            parsed = new EncounterRenderTargetRequest(parts[1], "part", parts[5], parts[3]);
            return true;
        }

        return false;
    }

    private static string[] TryParseComposedAssetPath(string raw)
    {
        const string prefix = "composed://";
        return raw.StartsWith(prefix, StringComparison.Ordinal)
            ? raw[prefix.Length..].Split('/', StringSplitOptions.TrimEntries)
            : [];
    }

    private static bool IsVirtualAssetId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-');
    }

    private static PackedScene? LoadCombatBackgroundLayerScene(string layerPath)
    {
        return ResourceLoader.Exists(layerPath)
            ? ResourceLoader.Load(layerPath) as PackedScene
            : null;
    }

    private static IReadOnlyList<string> DiscoverCombatBackgroundLayerPaths(string backgroundId)
    {
        var layersDir = $"res://scenes/backgrounds/{backgroundId}/layers";
        using var dirAccess = DirAccess.Open(layersDir);
        if (dirAccess is null)
        {
            return [];
        }

        var paths = new List<string>();
        dirAccess.ListDirBegin();
        for (var entry = dirAccess.GetNext(); !string.IsNullOrEmpty(entry); entry = dirAccess.GetNext())
        {
            if (dirAccess.CurrentIsDir())
            {
                continue;
            }

            if (!entry.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                && !entry.EndsWith(".escn", StringComparison.OrdinalIgnoreCase)
                && !entry.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            paths.Add($"{layersDir}/{entry}");
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static bool TryResolveCharacterModel(string characterId, out CharacterModel model)
    {
        try
        {
            return Sts2ModelResolver.TryResolveCharacter(characterId, out model);
        }
        catch
        {
            model = null!;
            return false;
        }
    }

    private static bool TryResolveRelicModel(string relicId, out RelicModel model)
    {
        try
        {
            return Sts2ModelResolver.TryResolveRelic(relicId, out model);
        }
        catch
        {
            model = null!;
            return false;
        }
    }

    private static bool TryResolveMonsterModel(string monsterId, out MonsterModel model)
    {
        try
        {
            return Sts2ModelResolver.TryResolveMonster(monsterId, out model);
        }
        catch
        {
            model = null!;
            return false;
        }
    }

    private static string CanonicalCharacterVariant(string normalizedVariant)
        => normalizedVariant switch
        {
            "iconoutline" => "iconOutline",
            "characterselecticon" => "characterSelectIcon",
            "characterselectlockedicon" => "characterSelectLockedIcon",
            "mapmarker" => "mapMarker",
            _ => normalizedVariant,
        };

    private static string CanonicalRelicVariant(string normalizedVariant)
        => normalizedVariant switch
        {
            "iconoutline" => "iconOutline",
            "bigicon" => "bigIcon",
            _ => normalizedVariant,
        };

    // model://acts|encounters/<id>/backgroundLayer/<stem> -> the layer scene in
    // res://scenes/backgrounds/<filePathIdentifier>/layers/. <id> is the game id
    // entry; the filesystem identifier is its lowercased form.
    private static string? BackgroundLayerPath(string idEntry, string variant)
    {
        const string prefix = "backgroundlayer/";
        if (!variant.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var stem = variant[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(stem) || stem.Contains("..", StringComparison.Ordinal) || stem.Contains('/'))
        {
            return null;
        }

        var fpi = idEntry.ToLowerInvariant();
        return $"res://scenes/backgrounds/{fpi}/layers/{stem}.tscn";
    }

    // Custom-encounter background root scene (mirrors BackgroundAssets' title-based
    // path); only meaningful when the encounter has a custom background.
    private static string CustomBackgroundRootPath(string idEntry)
    {
        var fpi = idEntry.ToLowerInvariant();
        return $"res://scenes/backgrounds/{fpi}/{fpi}_background.tscn";
    }

    // EncounterModel.HasCustomBackground is a protected virtual member; read it
    // reflectively rather than hardcoding the ~10 overriding ids.
    private static bool EncounterHasCustomBackground(EncounterModel model)
    {
        return Sts2LiveIntrospection.GetMemberValue(model, "HasCustomBackground") is bool value && value;
    }

    private bool TryCreateCharacterVisualNode(
        object model,
        out Node node,
        out IReadOnlyList<string> notes)
    {
        if (TryCreateLiveAllyCharacterVisualNode(model, out node, out notes))
        {
            return true;
        }

        if (TryInvokeCreateVisuals(model, out node))
        {
            notes = CharacterVisualNotes(
                $"Rendered model-backed battlefield character visual for '{CharacterModelIdOrUnknown(model)}' via CharacterModel.CreateVisuals().");
            return true;
        }

        notes = CharacterVisualNotes(
            $"Could not create a model-backed battlefield character visual for '{CharacterModelIdOrUnknown(model)}'.");
        return false;
    }

    private bool TryCreateLiveAllyCharacterVisualNode(
        object model,
        out Node node,
        out IReadOnlyList<string> notes)
    {
        var allies = CombatManager.Instance?.DebugOnlyGetState()?.Allies;
        if (allies is null)
        {
            node = null!;
            notes = [];
            return false;
        }

        return TryCreateLiveAllyCharacterVisualNode(
            model,
            allies.Cast<object>(),
            out node,
            out notes);
    }

    private bool TryCreateLiveAllyCharacterVisualNode(
        object model,
        IEnumerable<object> allies,
        out Node node,
        out IReadOnlyList<string> notes)
    {
        foreach (var ally in allies)
        {
            if (!MatchesCharacterModel(ally, model))
            {
                continue;
            }

            if (TryInvokeCreateVisuals(ally, out node))
            {
                notes = CharacterVisualNotes(
                    $"Rendered model-backed battlefield character visual for '{CharacterModelIdOrUnknown(model)}' via live combat ally CreateVisuals().");
                return true;
            }

            var player = Sts2LiveIntrospection.GetMemberValue(ally, "Player");
            var character = Sts2LiveIntrospection.GetMemberValue(player, "Character");
            if (TryInvokeCreateVisuals(character, out node))
            {
                notes = CharacterVisualNotes(
                    $"Rendered model-backed battlefield character visual for '{CharacterModelIdOrUnknown(model)}' via live combat ally player CharacterModel.CreateVisuals().");
                return true;
            }
        }

        node = null!;
        notes = [];
        return false;
    }

    private static IReadOnlyList<string> CharacterVisualNotes(string sourceNote)
    {
        return
        [
            sourceNote,
            "This is a virtual live asset, not a packed creature_visuals scene.",
        ];
    }

    private static bool MatchesCharacterModel(object? target, object model)
    {
        if (target is null)
        {
            return false;
        }

        if (CharacterModelId(target) is { } targetId
            && CharacterModelId(model) is { } modelId
            && string.Equals(targetId, modelId, StringComparison.Ordinal))
        {
            return true;
        }

        var directCharacter = Sts2LiveIntrospection.GetMemberValue(target, "Character");
        if (MatchesCharacterModel(directCharacter, model))
        {
            return true;
        }

        var player = Sts2LiveIntrospection.GetMemberValue(target, "Player");
        var playerCharacter = Sts2LiveIntrospection.GetMemberValue(player, "Character");
        return MatchesCharacterModel(playerCharacter, model);
    }

    private static string CharacterModelIdOrUnknown(object model)
    {
        return CharacterModelId((object?)model) ?? "unknown";
    }

    private static string RelicModelIdOrUnknown(RelicModel model)
        => Sts2LiveIntrospection.GetMemberValue(model.Id, "Entry") as string ?? "unknown";

    private static string? CharacterModelId(object? model)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id");
        return Sts2LiveIntrospection.GetMemberValue(id, "Entry") as string;
    }

    private static bool TryInvokeCreateVisuals(object? target, out Node node)
    {
        node = null!;
        if (target is null)
        {
            return false;
        }

        var method = target.GetType().GetMethod(
            "CreateVisuals",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (method is null)
        {
            return false;
        }

        try
        {
            if (method.Invoke(target, null) is Node result)
            {
                node = result;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool LooksLikeStandaloneSkeletonData(AssetExtractRequestSnapshot request)
    {
        var sourcePath = request.SourcePath.Replace('\\', '/');
        return sourcePath.Contains("skel_data", StringComparison.OrdinalIgnoreCase)
            || sourcePath.EndsWith("_skel.tres", StringComparison.OrdinalIgnoreCase)
            || sourcePath.EndsWith("_skeleton.tres", StringComparison.OrdinalIgnoreCase);
    }

    // The spine runtime's SpineSkeletonDataResource is a GDExtension type, so it surfaces as the
    // base Godot.Resource under C# static typing; its real Godot class is reported by GetClass().
    private static bool IsSpineSkeletonDataResource(Resource? resource)
        => resource is not null
            && resource.GetClass().Contains("SpineSkeletonData", StringComparison.OrdinalIgnoreCase);

    private static bool TryLoadExternalImage(string path, out Image image)
    {
        image = null!;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
        {
            return false;
        }

        image = Image.LoadFromFile(path);
        return image is not null && !image.IsEmpty();
    }

    private static bool IsUnresolvedVirtualAssetKey(AssetExtractRequestSnapshot request)
    {
        var loadPath = request.LoadPath.Replace('\\', '/').Trim();
        return string.Equals(request.SourceRoot, "virtual", StringComparison.OrdinalIgnoreCase)
            && !loadPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            && loadPath.Contains(':', StringComparison.Ordinal);
    }

    private static int DurationMs(double seconds)
    {
        return Math.Max(0, (int)Math.Round(seconds * 1000d));
    }

}
