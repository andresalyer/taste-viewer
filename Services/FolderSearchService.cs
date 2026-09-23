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

            // One inaccessible/locked folder shouldn't abort the whole search, unlike
            // Directory.EnumerateFiles(..., SearchOption.AllDirectories). Directory.Enumerate*
            // is lazy, so a try/catch around the call itself only guards a synchronous failure —
            // an UnauthorizedAccessException raised mid-iteration (the common case: a protected
            // subfolder found partway through) would otherwise escape uncaught and silently kill
            // the whole recursive search. SafeEnumerate catches per-item so only that folder is skipped.
            foreach (var d in SafeEnumerate(Directory.EnumerateDirectories, dir))
                if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);

            foreach (var f in SafeEnumerate(Directory.EnumerateFiles, dir))
            {
                token.ThrowIfCancellationRequested();
                if (!MediaExtensions.All.Contains(Path.GetExtension(f))) continue;
                if (Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
                    yield return new FileItemViewModel(new FileInfo(f));
            }
        }
    }

    private static List<string> SafeEnumerate(Func<string, IEnumerable<string>> enumerate, string dir)
    {
        var result = new List<string>();
        IEnumerator<string> e;
        try { e = enumerate(dir).GetEnumerator(); }
        catch { return result; }

        try
        {
            while (true)
            {
                bool moved;
                try { moved = e.MoveNext(); }
                catch { break; }
                if (!moved) break;
                result.Add(e.Current);
            }
        }
        finally { e.Dispose(); }
        return result;
    }
}
