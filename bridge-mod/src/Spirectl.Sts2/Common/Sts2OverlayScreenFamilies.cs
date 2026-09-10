namespace Spirectl.Sts2;

internal static class Sts2OverlayScreenFamilies
{
    private const string SimpleCardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen";
    private const string DeckCardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen";
    private const string DeckUpgradeSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen";
    private const string DeckTransformSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen";
    private const string DeckEnchantSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen";
    private const string BundleSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen";
    private const string RelicSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection";

    public static bool TryResolve(object? overlay, out OverlayScreenIdMatch? match)
    {
        match = null;
        if (overlay is null)
        {
            return false;
        }

        if (Sts2SupportedScreenIds.TryResolve(overlay, out var supportedMatch))
        {
            match = new OverlayScreenIdMatch(
                supportedMatch!.ScreenType,
                supportedMatch.ScreenTitle,
                "blocking",
                IsSupported: true,
                IsBlocking: true,
                IsPassive: false);
            return true;
        }

        if (IsTypeOrSubtype(overlay, SimpleCardSelectionScreenType))
        {
            match = Blocking(Sts2SupportedScreenIds.SimpleCardSelectionScreenId, "Simple Card Selection");
            return true;
        }

        if (IsTypeOrSubtype(overlay, DeckCardSelectionScreenType)
            || IsTypeOrSubtype(overlay, DeckUpgradeSelectionScreenType)
            || IsTypeOrSubtype(overlay, DeckTransformSelectionScreenType)
            || IsTypeOrSubtype(overlay, DeckEnchantSelectionScreenType))
        {
            match = Blocking(Sts2SupportedScreenIds.ScreenIdFor(overlay.GetType()), "Deck Card Selection");
            return true;
        }

        if (IsTypeOrSubtype(overlay, BundleSelectionScreenType))
        {
            match = Blocking(Sts2SupportedScreenIds.BundleSelectionScreenId, "Choose a Bundle");
            return true;
        }

        if (IsTypeOrSubtype(overlay, RelicSelectionScreenType))
        {
            match = Blocking(Sts2SupportedScreenIds.RelicSelectionScreenId, "Choose a Relic");
            return true;
        }

        var overlayTypeName = overlay.GetType().Name;
        if (overlayTypeName.Contains("Card", StringComparison.OrdinalIgnoreCase))
        {
            match = new OverlayScreenIdMatch(
                "card-overlay",
                "Card Overlay",
                "passive",
                IsSupported: true,
                IsBlocking: false,
                IsPassive: true);
            return true;
        }

        if (overlayTypeName.Contains("Overlay", StringComparison.OrdinalIgnoreCase))
        {
            match = new OverlayScreenIdMatch(
                "unsupported-overlay",
                HumanizeTypeName(overlayTypeName),
                "unsupported",
                IsSupported: false,
                IsBlocking: true,
                IsPassive: false);
            return true;
        }

        return false;
    }

    private static OverlayScreenIdMatch Blocking(string screenType, string screenTitle)
        => new(screenType, screenTitle, "blocking", IsSupported: true, IsBlocking: true, IsPassive: false);

    private static bool IsTypeOrSubtype(object target, string fullTypeName)
    {
        for (var current = target.GetType(); current is not null; current = current.BaseType)
        {
            var candidate = current.FullName ?? current.Name;
            if (string.Equals(candidate, fullTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string HumanizeTypeName(string typeName)
    {
        var trimmed = typeName.Trim();
        if (trimmed.Length > 1 && trimmed[0] == 'N' && char.IsUpper(trimmed[1]))
        {
            trimmed = trimmed[1..];
        }

        var words = new List<char>(trimmed.Length + 8);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var current = trimmed[i];
            if (i > 0 && char.IsUpper(current) && !char.IsWhiteSpace(trimmed[i - 1]))
            {
                words.Add(' ');
            }

            words.Add(current);
        }

        return words.Count == 0 ? "Unsupported Overlay" : new string(words.ToArray());
    }
}

internal sealed record OverlayScreenIdMatch(
    string ScreenType,
    string ScreenTitle,
    string OverlayPolicy,
    bool IsSupported,
    bool IsBlocking,
    bool IsPassive);
