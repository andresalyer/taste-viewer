using System.Collections.Concurrent;
using System.Windows;
using Taste.Interop;
using Taste.ViewModels;

namespace Taste.Services;

public sealed class ThumbnailService : IDisposable
{
    private readonly BlockingCollection<(FileItemViewModel Item, int Size, CancellationToken Token)> _queue =
        new(new ConcurrentQueue<(FileItemViewModel, int, CancellationToken)>(), 2000);

    private readonly Thread _worker;
    private bool _disposed;

    public ThumbnailService()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ThumbnailWorker"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public void Enqueue(FileItemViewModel item, int size, CancellationToken token) =>
        _queue.TryAdd((item, size, token));

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
            if (item.IsCloudPlaceholder) continue;

            var thumbnail = ShellInterop.GetThumbnail(item.FullPath, size);
            if (token.IsCancellationRequested) continue;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested)
                    item.SetThumbnail(thumbnail);
            });
        }
    }
}
