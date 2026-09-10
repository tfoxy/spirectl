namespace Spirectl.Sts2.Core.State;

internal static class StateDeckViewSortNormalizer
{
    public static bool TryNormalize(object? value, out StateDeckViewSortSnapshot sort)
    {
        sort = null!;
        switch (Normalize(value?.ToString()))
        {
            case "Ascending":
                sort = new StateDeckViewSortSnapshot("obtained", "ascending");
                return true;
            case "Descending":
                sort = new StateDeckViewSortSnapshot("obtained", "descending");
                return true;
            case "TypeAscending":
                sort = new StateDeckViewSortSnapshot("type", "ascending");
                return true;
            case "TypeDescending":
                sort = new StateDeckViewSortSnapshot("type", "descending");
                return true;
            case "CostAscending":
                sort = new StateDeckViewSortSnapshot("cost", "ascending");
                return true;
            case "CostDescending":
                sort = new StateDeckViewSortSnapshot("cost", "descending");
                return true;
            case "AlphabetAscending":
                sort = new StateDeckViewSortSnapshot("alphabet", "ascending");
                return true;
            case "AlphabetDescending":
                sort = new StateDeckViewSortSnapshot("alphabet", "descending");
                return true;
            default:
                return false;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
