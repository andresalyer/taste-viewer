using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media.Imaging;
using Taste.Interop;
using Taste.ViewModels;

namespace Taste.Services;

public sealed class ThumbnailService : IDisposable
{
    // One shell thumbnail call is one COM round-trip to disk/provider — a handful of worker
    // threads let visible tiles resolve concurrently instead of queuing behind each other one at a time.
    private const int WorkerCount = 4;

    // Small cap: this is a "reuse across refresh/back-forward within a session" cache, not a
    // full-library cache. Bitmaps are frozen so sharing the same instance across items is safe.
    private const int MaxCacheEntries = 1500;

    private readonly BlockingCollection<(FileItemViewModel Item, int Size, CancellationToken Token)> _queue =
        new(new ConcurrentQueue<(FileItemViewModel, int, CancellationToken)>(), 2000);

    private readonly ConcurrentDictionary<(string Path, int Size), (DateTime Modified, BitmapSource Thumb)> _cache = new();
    private readonly ConcurrentQueue<(string Path, int Size)> _cacheOrder = new();

    private readonly List<Thread> _workers = new();
    private bool _disposed;

    public ThumbnailService()
    {
        for (int i = 0; i < WorkerCount; i++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ThumbnailWorker{i}"
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            _workers.Add(worker);
        }
    }

    public void Enqueue(FileItemViewModel item, int size, CancellationToken token)
    {
        if (_cache.TryGetValue((item.FullPath, size), out var cached) && cached.Modified == item.DateModified)
        {
            item.SetThumbnail(cached.Thumb);
            return;
        }
        _queue.TryAdd((item, size, token));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }

    private void WorkerLoop()
    {
        foreach (var (item, size, token) in _queue.GetConsumingEnumerable())
        {
            if (token.IsCancellationRequested) continue;

            // Cloud placeholders aren't fully downloaded — GetThumbnail's ResizeToFit path would force a
            // hydration/download per tile. GetShellThumbnail (InCacheOnly) asks the provider for whatever
            // cheap preview it already has (same mechanism Explorer and PreviewWindow use) with no download.
            var thumbnail = item.IsCloudPlaceholder
                ? ShellInterop.GetShellThumbnail(item.FullPath, size)
                : ShellInterop.GetThumbnail(item.FullPath, size);

            if (thumbnail != null)
                StoreInCache(item.FullPath, size, item.DateModified, thumbnail);

            if (token.IsCancellationRequested) continue;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                if (thumbnail != null) item.SetThumbnail(thumbnail);
                else item.MarkThumbnailUnavailable();
            });
        }
    }

    private void StoreInCache(string path, int size, DateTime modified, BitmapSource thumb)
    {
        var key = (path, size);
        _cache[key] = (modified, thumb);
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var old))
            _cache.TryRemove(old, out _);
    }
}
