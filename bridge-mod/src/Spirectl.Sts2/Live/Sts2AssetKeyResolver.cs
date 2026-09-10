using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Live.EncounterVisuals;
using System.Reflection;

namespace Spirectl.Sts2.Live;

public sealed class Sts2AssetKeyResolver(Sts2AssetProviderCatalog catalog)
{
    private static readonly Lazy<Sts2EncounterVisualCatalog> EncounterCatalog = new(Sts2EncounterVisualCatalog.LoadBaseGame);

    public ResolveResult Resolve(string key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return ResolveResult.Error("invalid-asset-key", "Asset key must be non-empty.");
        if (trimmed.StartsWith("res://", StringComparison.Ordinal))
        {
            return ResolveResult.Success("resources", trimmed, trimmed);
        }

        // spine://<scene>?node=<relPath>&anim=<name>: a SpineSprite render (clip/still) addressed by its
        // scene + scene-relative node path. Pass the key through unchanged; the live extractor parses the
        // scene/node/anim selectors (TryParseSpineClipRequest) and renders the clip as a Timeline.
        if (trimmed.StartsWith("spine://", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveResult.Success("spine", trimmed, trimmed);
        }

        // scene-subtree://<res-scene>?node=<relPath>: render ONLY the addressed subtree of a scene (couch-coop's
        // room-backdrop still). Pass the key through unchanged; the live extractor parses it
        // (TryParseSceneSubtreeRequest) and rewrites onto the ordinary PackedScene path.
        if (trimmed.StartsWith("scene-subtree://", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveResult.Success("scene-subtree", trimmed, trimmed);
        }

        if (catalog.TryResolve(trimmed, out var entry)) return ResolveResult.Success("resources", entry.LoadPath, entry.LoadPath);

        if (TryParseSchemePath(trimmed, "model", out var modelParts))
        {
            return ResolveModelKey(modelParts, trimmed);
        }

        if (TryParseSchemePath(trimmed, "composed", out var composedParts))
        {
            return ResolveComposedKey(composedParts, trimmed);
        }

        var family = trimmed.Split(':', 2)[0].ToLowerInvariant();
        return family switch
        {
            "model" or "composed" or "card" or "relic" or "potion" or "status" or "combat-ui" or "card-ui" or "enemy-intent" or "character" or "enemy" or "combat-background" or "event-background" or "vfx" or "encounter"
                => ResolveResult.Error("invalid-asset-key", $"Invalid asset key '{trimmed}'."),
            _ => ResolveResult.Error("unsupported-asset-kind", $"Unsupported asset key family '{family}'.")
        };
    }

    private static bool TryParseSchemePath(string key, string scheme, out string[] parts)
    {
        parts = [];
        var prefix = $"{scheme}://";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        parts = key[prefix.Length..]
            .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && !key[prefix.Length..].Split('/').Any(segment => string.IsNullOrWhiteSpace(segment));
    }

    private ResolveResult ResolveModelKey(string[] parts, string key)
    {
        if (parts.Length != 3)
        {
            return ResolveResult.Error("invalid-asset-key", $"Invalid model asset key '{key}'.");
        }

        var modelKind = parts[0].ToLowerInvariant();
        var id = parts[1];
        return modelKind switch
        {
            "cards" => ResolveCard(id, parts[2], key),
            "relics" => ResolveRelic(id, parts[2], key),
            "potions" => ResolvePotion(id, parts[2], key),
            "characters" => ResolveCharacter(id, parts[2], key),
            "monsters" when parts[2].Equals("visuals", StringComparison.OrdinalIgnoreCase) => ResolveMonster(id),
            "events" => ResolveEvent(id, parts[2], key),
            "acts" => ResolveAct(id, parts[2]),
            _ => ResolveResult.Error("invalid-asset-key", $"Invalid model asset key '{key}'."),
        };
    }

    private static ResolveResult ResolveComposedKey(string[] parts, string key)
    {
        if (parts.Length < 3)
        {
            return ResolveResult.Error("invalid-asset-key", $"Invalid composed asset key '{key}'.");
        }

        var family = parts[0].ToLowerInvariant();
        var id = parts[1];
        return family switch
        {
            "combat-background" when parts.Length == 3 && parts[2].Equals("image", StringComparison.OrdinalIgnoreCase) => ResolveResult.Success("composed", key, key),
            "encounters" when parts.Length == 3 && parts[2].Equals("scene-package", StringComparison.OrdinalIgnoreCase) => ResolveEncounter(id, key),
            "encounters" => ResolveEncounterRenderTarget(id, parts, key),
            _ => ResolveResult.Error("unsupported-asset-kind", $"Unsupported composed asset key '{key}'."),
        };
    }

    private static ResolveResult ResolveEncounter(string id, string key)
    {
        if (!EncounterCatalog.Value.TryGetPackage(id, out _))
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown encounter '{id}'.");
        }

        return ResolveResult.Success("composed", key, key);
    }

    private static ResolveResult ResolveEncounterRenderTarget(string id, string[] parts, string key)
    {
        if (!EncounterCatalog.Value.TryGetPackage(id, out _))
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown encounter '{id}'.");
        }

        var supported = parts.Length == 4 && parts[2] == "background" && parts[3] == "image"
            || parts.Length == 6 && parts[2] == "visual-state" && parts[4] == "overlay" && parts[5] == "image"
            || parts.Length == 7 && parts[2] == "visual-part" && parts[4] == "state" && parts[6] == "image";

        return supported
            ? ResolveResult.Success("composed", key, key)
            : ResolveResult.Error("unsupported-asset-kind", $"Unsupported encounter render target '{key}'.");
    }

    private static ResolveResult ResolveCard(string id, string variant, string key)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveCard(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            var path = variant.ToLowerInvariant() switch
            {
                "image" => TryExtractCardPath(model),
                "overlay" => model.HasBuiltInOverlay ? model.OverlayPath : null,
                _ => null,
            };
            if (path is null && variant.Equals("overlay", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveResult.Success("model", key, key);
            }
            if (path is null)
            {
                return ResolveResult.Error("invalid-asset-key", $"Invalid card model asset variant '{variant}'.");
            }
            if (string.IsNullOrWhiteSpace(path)) return ResolveResult.Error("asset-key-partial", $"Model '{id}' found but no renderable path was discovered.");
            return ResolveResult.Success("resources", path!, path!);
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveRelic(string id, string variant, string key)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveRelic(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            return variant.ToLowerInvariant() switch
            {
                "icon" => ResolveRelicIcon(model, id),
                "iconoutline" or "bigicon" => ResolveResult.Success("model", key, key),
                _ => ResolveResult.Error("invalid-asset-key", $"Invalid relic model asset variant '{variant}'."),
            };
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveRelicIcon(RelicModel model, string id)
    {
        var path = TryExtractRelicIconPath(model);
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResolveResult.Error("asset-key-partial", $"Model '{id}' found but no renderable path was discovered.");
        }

        return ResolveResult.Success("resources", path!, path!);
    }

    private static ResolveResult ResolvePotion(string id, string variant, string key)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolvePotion(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            var path = variant.ToLowerInvariant() switch
            {
                "icon" => TryExtractPotionPath(model),
                "outline" => TryExtractFirstStringProperty(model, "OutlinePath"),
                _ => null,
            };
            if (path is null)
            {
                return ResolveResult.Error("invalid-asset-key", $"Invalid potion model asset variant '{variant}'.");
            }
            if (string.IsNullOrWhiteSpace(path)) return ResolveResult.Error("asset-key-partial", $"Model '{id}' found but no renderable path was discovered.");
            return ResolveResult.Success("resources", path!, path!);
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveCharacter(string id, string variant, string key)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveCharacter(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            return variant.ToLowerInvariant() switch
            {
                "icon" or "iconoutline" or "characterselecticon" or "characterselectlockedicon" or "mapmarker" => ResolveResult.Success("model", key, key),
                "characterselectbg" => ResolveCharacterPath(model, id, "CharacterSelectBg", key),
                "energycounter" => ResolveCharacterPath(model, id, "EnergyCounterPath", key),
                "merchantanim" => ResolveCharacterPath(model, id, "MerchantAnimPath", key),
                "restsiteanim" => ResolveCharacterPath(model, id, "RestSiteAnimPath", key),
                "visuals" => ResolveResult.Success("model", key, key),
                _ => ResolveResult.Error("invalid-asset-key", $"Invalid character model asset variant '{variant}'."),
            };
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveCharacterPath(CharacterModel model, string id, string propertyName, string key)
    {
        var path = propertyName switch
        {
            "CharacterSelectBg" => model.CharacterSelectBg,
            "EnergyCounterPath" => model.EnergyCounterPath,
            "MerchantAnimPath" => model.MerchantAnimPath,
            "RestSiteAnimPath" => model.RestSiteAnimPath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResolveResult.Error("asset-key-partial", $"Character model '{id}' found but '{propertyName}' was empty.");
        }

        var normalized = NormalizeResourcePath(path!, propertyName is "CharacterSelectBg" ? "res://scenes/screens/char_select/" : null);
        return ResolveResult.Success("model", normalized, normalized);
    }

    private static ResolveResult ResolveMonster(string id)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveMonster(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            var path = TryExtractMonsterVisualPath(model);
            if (string.IsNullOrWhiteSpace(path)) return ResolveResult.Error("asset-key-partial", $"Monster model '{id}' found but no visual scene path was discovered.");
            return ResolveResult.Success("model", path!, path!);
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveEvent(string id, string variant, string key)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveEvent(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            var propertyName = variant.ToLowerInvariant() switch
            {
                "backgroundscene" => "BackgroundScenePath",
                "backgroundspinestill" => "BackgroundScenePath",
                "initialportrait" => "InitialPortraitPath",
                "vfx" => "VfxPath",
                "mapicon" => "MapIconPath",
                "mapiconoutline" => "MapIconOutlinePath",
                "runhistoryicon" => "RunHistoryIcon",
                "runhistoryiconoutline" => "RunHistoryIconOutlinePath",
                _ => null,
            };
            if (propertyName is null)
            {
                return ResolveResult.Error("invalid-asset-key", $"Invalid event model asset variant '{variant}'.");
            }

            var path = variant.Equals("runhistoryicon", StringComparison.OrdinalIgnoreCase)
                ? TryExtractResourcePathProperty(model, propertyName)
                : TryExtractFirstStringProperty(model, propertyName);
            if (string.IsNullOrWhiteSpace(path)) return ResolveResult.Error("asset-key-partial", $"Event model '{id}' found but '{propertyName}' was empty.");

            // The spine still is rendered from the background scene's SpineSprite, so it keeps
            // the model key as its source root while loading the background scene as its load path
            // (mirrors the character-select spine-still resolution).
            return variant.Equals("backgroundspinestill", StringComparison.OrdinalIgnoreCase)
                ? ResolveResult.Success("model", key, path!)
                : ResolveResult.Success("model", path!, path!);
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static ResolveResult ResolveAct(string id, string variant)
    {
        try
        {
            if (!Sts2ModelResolver.TryResolveAct(id, out var model))
            {
                return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
            }

            var path = variant.ToLowerInvariant() switch
            {
                "backgroundscene" => model.BackgroundScenePath,
                "restsitebackground" => model.RestSiteBackgroundPath,
                "maptopbg" => model.MapTopBgPath,
                "mapmidbg" => model.MapMidBgPath,
                "mapbotbg" => model.MapBotBgPath,
                "chestspine" => model.ChestSpineResourcePath,
                _ => null,
            };
            if (path is null)
            {
                return ResolveResult.Error("invalid-asset-key", $"Invalid act model asset variant '{variant}'.");
            }
            if (string.IsNullOrWhiteSpace(path)) return ResolveResult.Error("asset-key-partial", $"Act model '{id}' found but no renderable path was discovered.");
            return ResolveResult.Success("resources", path!, path!);
        }
        catch (KeyNotFoundException)
        {
            return ResolveResult.Error("asset-key-not-found", $"Unknown model id '{id}'.");
        }
    }

    private static string NormalizeResourcePath(string path, string? relativePrefix)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return string.IsNullOrWhiteSpace(relativePrefix)
            ? normalized
            : $"{relativePrefix}{normalized}";
    }

    private static string? TryExtractCardPath<TModel>(TModel model)
    {
        var direct = TryExtractFirstStringProperty(model, "PortraitPath", "PortraitPngPath");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var allPortraits = TryExtractStringEnumerableProperty(model, "AllPortraitPaths");
        if (allPortraits is not null)
        {
            foreach (var path in allPortraits)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return TryExtractFirstStringProperty(model, "BetaPortraitPath");
    }

    private static string? TryExtractRelicIconPath(RelicModel model)
    {
        return FirstNonBlank(model.IconPath, model.PackedIconPath);
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TryExtractPotionPath<TModel>(TModel model)
    {
        return TryExtractFirstStringProperty(model, "ImagePath", "PackedImagePath");
    }

    private static string? TryExtractMonsterVisualPath(MonsterModel model)
    {
        if (TryExtractFirstStringProperty(model, "VisualsPath") is { } visualsPath)
        {
            return visualsPath;
        }

        return TryExtractStringEnumerableProperty(model, "AssetPaths")?.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private static string? TryExtractFirstStringProperty<TModel>(TModel model, params string[] names)
    {
        var type = model?.GetType();
        if (type is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var property = FindProperty(type, name);
            if (property?.GetValue(model) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string>? TryExtractStringEnumerableProperty<TModel>(TModel model, string name)
    {
        var type = model?.GetType();
        if (type is null)
        {
            return null;
        }

        var property = FindProperty(type, name);
        if (property?.GetValue(model) is not System.Collections.IEnumerable values)
        {
            return null;
        }

        return values.OfType<string>();
    }

    private static string? TryExtractResourcePathProperty<TModel>(TModel model, string name)
    {
        var type = model?.GetType();
        if (type is null)
        {
            return null;
        }

        var property = FindProperty(type, name);
        var value = property?.GetValue(model);
        var resourcePath = value?.GetType().GetProperty("ResourcePath")?.GetValue(value)?.ToString();
        return string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath.Trim();
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return property;
            }
        }

        return null;
    }

    public sealed record ResolveResult(bool IsSuccess, string SourceRoot, string SourcePath, string LoadPath, string ErrorCode, string ErrorMessage)
    {
        public static ResolveResult Success(string root, string sourcePath, string loadPath) => new(true, root, sourcePath, loadPath, string.Empty, string.Empty);
        public static ResolveResult Error(string code, string message) => new(false, string.Empty, string.Empty, string.Empty, code, message);
    }
}
