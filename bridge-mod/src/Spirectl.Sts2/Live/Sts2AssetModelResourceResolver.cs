using MegaCrit.Sts2.Core.Models;
using System.Reflection;

namespace Spirectl.Sts2.Live;

/// <summary>Shared model-to-resource-path rules used by extraction and catalog enumeration.</summary>
internal static class Sts2AssetModelResourceResolver
{
    internal static string? ResolveAssetModelId(object? model)
    {
        var raw = Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(model, "Id"),
                "Entry")?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(model, "ModelId")?.ToString()
            ?? Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(model, "ModelId"),
                "Entry")?.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : NormalizeAssetKeyId(raw);
    }

    internal static string NormalizeAssetKeyId(string id)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var previousWasLowerOrDigit = false;
        foreach (var character in id.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && previousWasLowerOrDigit && current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }

                current.Append(char.ToLowerInvariant(character));
                previousWasLowerOrDigit = char.IsLower(character) || char.IsDigit(character);
                continue;
            }

            if (current.Length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }

            previousWasLowerOrDigit = false;
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        var normalized = string.Join('-', segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        return normalized.StartsWith("the-", StringComparison.Ordinal)
            ? normalized["the-".Length..]
            : normalized;
    }

    internal static string? CharacterSelectBackgroundPath(CharacterModel model, string id)
    {
        var configured = model.CharacterSelectBg;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = configured.Replace('\\', '/').Trim();
            return normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                ? normalized : $"res://scenes/screens/char_select/{normalized}";
        }
        return $"res://scenes/screens/char_select/char_select_bg_{id.Replace('-', '_')}.tscn";
    }

    internal static string? TryExtractCardPath<TModel>(TModel model)
    {
        var direct = TryExtractFirstStringProperty(model, "PortraitPath", "PortraitPngPath");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        return TryExtractStringEnumerableProperty(model, "AllPortraitPaths")?.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? TryExtractFirstStringProperty(model, "BetaPortraitPath");
    }

    internal static string? TryExtractRelicIconPath(RelicModel model) => FirstNonBlank(model.IconPath, model.PackedIconPath);
    internal static string? TryExtractPotionPath<TModel>(TModel model) => TryExtractFirstStringProperty(model, "ImagePath", "PackedImagePath");
    internal static string? TryExtractMonsterVisualPath(MonsterModel model)
        => TryExtractFirstStringProperty(model, "VisualsPath")
            ?? TryExtractStringEnumerableProperty(model, "AssetPaths")?.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    internal static string? TryExtractFirstStringProperty<TModel>(TModel model, params string[] names)
    {
        var type = model?.GetType();
        if (type is null) return null;
        foreach (var name in names)
        {
            if (FindProperty(type, name)?.GetValue(model) is string value && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
    internal static string? TryExtractResourcePathProperty<TModel>(TModel model, string name)
    {
        var type = model?.GetType();
        var value = type is null ? null : FindProperty(type, name)?.GetValue(model);
        var path = value?.GetType().GetProperty("ResourcePath")?.GetValue(value)?.ToString();
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }
    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static IEnumerable<string>? TryExtractStringEnumerableProperty<TModel>(TModel model, string name)
    {
        var type = model?.GetType();
        return type is null || FindProperty(type, name)?.GetValue(model) is not System.Collections.IEnumerable values ? null : values.OfType<string>();
    }
    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null) return property;
        }
        return null;
    }
}
