using System.Collections.ObjectModel;
using System.IO;
using Taste.Shared;

namespace Taste.ViewModels;

public class ColumnViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource _cts = new();

    public string Path       { get; }
    public string FolderName { get; }

    public const double WidthDefault = 220;
    public const double WidthMin     = 160;
    public const double WidthMax     = 480;

    // Not persisted — always starts at WidthDefault each session; adjustable per-column via drag.
    private double _width = WidthDefault;
    public double Width
    {
        get => _width;
        set => SetField(ref _width, Math.Clamp(value, WidthMin, WidthMax));
    }

    public ObservableCollection<FileItemViewModel> Items { get; } = new();

    private FileItemViewModel? _selectedItem;
    public FileItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }

    // True when this is the deepest column with a selection (the "active" one, full highlight).
    // An ancestor column that still has a selection but was drilled past shows the dimmer,
    // secondary highlight instead. Recomputed by MainViewModel.RecomputeColumnSelectionStates.
    private bool _isPrimarySelection = true;
    public bool IsPrimarySelection
    {
        get => _isPrimarySelection;
        set => SetField(ref _isPrimarySelection, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public ColumnViewModel(string path)
    {
        Path       = path;
        FolderName = System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path;
    }

    public async Task LoadAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Items.Clear();
        IsLoading = true;

        try
        {
            var items = await Task.Run(() => Enumerate(Path, token), token);
            if (token.IsCancellationRequested) return;
            foreach (var item in items)
                Items.Add(item);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { IsLoading = false; }
    }

    public void Cancel() => _cts.Cancel();
    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }

    private static List<FileItemViewModel> Enumerate(string path, CancellationToken token)
    {
        var result = new List<FileItemViewModel>();
        try
        {
            var di = new DirectoryInfo(path);
            foreach (var d in di.EnumerateDirectories().OrderBy(x => x.Name))
            {
                token.ThrowIfCancellationRequested();
                if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                if (d.Name.StartsWith('.')) continue;
                result.Add(new FileItemViewModel(d));
            }
            foreach (var f in di.EnumerateFiles().OrderBy(x => x.Name))
            {
                token.ThrowIfCancellationRequested();
                if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                if (f.Name.StartsWith('.')) continue;
                if (!MediaExtensions.All.Contains(f.Extension)) continue;
                result.Add(new FileItemViewModel(f));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return result;
    }
}
