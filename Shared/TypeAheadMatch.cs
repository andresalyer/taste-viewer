using System.Linq;
using Taste.ViewModels;

namespace Taste.Shared;

// Explorer-style type-ahead: jump to the next item whose name starts with what was typed,
// searching forward from the current selection and wrapping around. Unlike WPF's built-in
// TextSearch, falls back to the alphabetically nearest item when nothing matches at all.
internal static class TypeAheadMatch
{
    public static FileItemViewModel? Find(IReadOnlyList<FileItemViewModel> items, string query, FileItemViewModel? current)
    {
        int n = items.Count;
        if (n == 0 || query.Length == 0) return null;

        int currentIndex = current == null ? -1 : IndexOf(items, current);
        for (int step = 1; step <= n; step++)
        {
            var item = items[(currentIndex + step) % n];
            if (item.FileName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return item;
        }

        // No prefix match anywhere — jump to the alphabetically nearest item instead,
        // regardless of the view's current sort mode (Date/Type/Size).
        FileItemViewModel? nearest = null;
        foreach (var item in items)
        {
            if (string.Compare(item.FileName, query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (nearest == null || string.Compare(item.FileName, nearest.FileName, StringComparison.OrdinalIgnoreCase) < 0)
                nearest = item;
        }
        return nearest ?? items.Aggregate((a, b) =>
            string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase) >= 0 ? a : b);
    }

    private static int IndexOf(IReadOnlyList<FileItemViewModel> items, FileItemViewModel item)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }
}
