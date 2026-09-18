using System.IO;
using Taste.Shared;
using Taste.ViewModels;

namespace Taste.Services;

// Lightweight recursive filename search: no indexing, no content search — just a
// cancellable directory walk filtered to media files, matching Taste's existing scope.
public static class FolderSearchService
{
    public static IEnumerable<FileItemViewModel> Search(string root, string query, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = stack.Pop();

            IEnumerable<string> subdirs = Array.Empty<string>();
            IEnumerable<string> files   = Array.Empty<string>();
            // One inaccessible/locked folder shouldn't abort the whole search, unlike
            // Directory.EnumerateFiles(..., SearchOption.AllDirectories).
            try { subdirs = Directory.EnumerateDirectories(dir); } catch { }
            try { files   = Directory.EnumerateFiles(dir); } catch { }

            foreach (var d in subdirs)
                if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);

            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested();
                if (!MediaExtensions.All.Contains(Path.GetExtension(f))) continue;
                if (Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
                    yield return new FileItemViewModel(new FileInfo(f));
            }
        }
    }
}
