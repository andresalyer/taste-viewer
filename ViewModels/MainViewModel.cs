using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Taste.Services;
using Taste.Shared;

namespace Taste.ViewModels;

public enum SortMode { Name, DateModified, Type, Size }
public enum SortDirection { Ascending, Descending }
public enum ViewMode { Grid, List, Column }

public class MainViewModel : ObservableObject, IDisposable
{
    private record DeletedItem(string OriginalPath, string RFile, string IFile, bool IsDirectory,
                                ObservableCollection<FileItemViewModel> TargetCollection);

    private readonly ThumbnailService _thumbnailService = new();
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private readonly ListCollectionView _filesView;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource _loadCts = new();
    private SortMode _sortMode = SortMode.DateModified;
    private SortDirection _sortDirection = SortDirection.Descending;
    private System.Threading.Timer? _watcherDebounce;
    private readonly List<List<DeletedItem>> _undoGroups = new();
    private System.Threading.Timer? _undoTimer;
    private CancellationTokenSource _searchCts = new();
    private System.Threading.Timer? _searchDebounce;

    // Last known viewport — set by the view on scroll/resize, used here for thumbnail requests
    private double _vpScrollOffset;
    private double _vpHeight;
    private double _vpContentWidth;
    private double _lastThumbScrollOffset = -1;

    // Folder enumeration is paged in from here as the user scrolls, rather than capped outright.
    private IEnumerator<DirectoryInfo>? _dirEnumerator;
    private IEnumerator<FileInfo>? _fileEnumerator;
    private bool _isLoadingMore;

    public ObservableCollection<FileItemViewModel> Files        { get; } = new();
    public ObservableCollection<PathSegment>       PathSegments { get; } = new();

    public IEnumerable<string> OrderedFilePaths =>
        _filesView.Cast<FileItemViewModel>().Select(f => f.FullPath);

    // Current on-screen order (respects the active sort mode) — used for type-ahead.
    public IReadOnlyList<FileItemViewModel> OrderedFiles =>
        _filesView.Cast<FileItemViewModel>().ToList();

    public static readonly int[] SnapPoints = [100, 160, 220, 280, 340, 400, 460, 520];
    public const int SizeMin  = 100;
    public const int SizeMax  = 520;

    private int _thumbnailSize = 220;
    public int ThumbnailSize
    {
        get => _thumbnailSize;
        set
        {
            int snapped = Math.Clamp((int)Math.Round(value / 4.0) * 4, SizeMin, SizeMax);
            if (!SetField(ref _thumbnailSize, snapped)) return;
            ReloadThumbnails();
        }
    }

    private string _selectedSort = "Date Modified";
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetField(ref _selectedSort, value)) return;
            _sortMode = value switch
            {
                "Name"          => SortMode.Name,
                "Date Modified" => SortMode.DateModified,
                "Type"          => SortMode.Type,
                "Size"          => SortMode.Size,
                _               => SortMode.Name,
            };
            NotifySortIndicators();
            ApplySort();
        }
    }

    private string _selectedSortDirection = "Descending";
    public string SelectedSortDirection
    {
        get => _selectedSortDirection;
        set
        {
            if (!SetField(ref _selectedSortDirection, value)) return;
            _sortDirection = value == "Ascending" ? SortDirection.Ascending : SortDirection.Descending;
            NotifySortIndicators();
            ApplySort();
        }
    }

    // Sort indicator helpers (for list view column headers)
    public bool IsSortedByName => _sortMode == SortMode.Name;
    public bool IsSortedByDate => _sortMode == SortMode.DateModified;
    public bool IsSortedByType => _sortMode == SortMode.Type;
    public bool IsSortedBySize => _sortMode == SortMode.Size;
    public bool SortIsAscending => _sortDirection == SortDirection.Ascending;

    private void NotifySortIndicators()
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedByDate));
        OnPropertyChanged(nameof(IsSortedByType));
        OnPropertyChanged(nameof(IsSortedBySize));
        OnPropertyChanged(nameof(SortIsAscending));
    }

    public void SortByColumn(string columnName)
    {
        if (SelectedSort == columnName)
            SelectedSortDirection = SelectedSortDirection == "Ascending" ? "Descending" : "Ascending";
        else
            SelectedSort = columnName;
    }

    // List view column visibility
    private bool _showDateColumn = true;
    public bool ShowDateColumn { get => _showDateColumn; set => SetField(ref _showDateColumn, value); }
    private bool _showTypeColumn = true;
    public bool ShowTypeColumn { get => _showTypeColumn; set => SetField(ref _showTypeColumn, value); }
    private bool _showSizeColumn = true;
    public bool ShowSizeColumn { get => _showSizeColumn; set => SetField(ref _showSizeColumn, value); }
    private bool _showPathColumn = false;
    public bool ShowPathColumn { get => _showPathColumn; set => SetField(ref _showPathColumn, value); }

    // Column view
    public ObservableCollection<ColumnViewModel> Columns { get; } = new();

    public void InitColumns()
    {
        foreach (var col in Columns)
        {
            col.PropertyChanged -= ColumnViewModel_PropertyChanged;
            col.Dispose();
        }
        Columns.Clear();
        if (!string.IsNullOrEmpty(_currentPath))
            AppendColumn(_currentPath);
    }

    public void ColumnFolderOpened(ColumnViewModel source, FileItemViewModel folder)
    {
        int idx = Columns.IndexOf(source);
        while (Columns.Count > idx + 1)
        {
            Columns[^1].PropertyChanged -= ColumnViewModel_PropertyChanged;
            Columns[^1].Dispose();
            Columns.RemoveAt(Columns.Count - 1);
        }
        AppendColumn(folder.FullPath);
    }

    private void ColumnViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ColumnViewModel.SelectedItem))
            RecomputeColumnSelectionStates();
    }

    // Only the deepest column with a selection shows the full "active" highlight; any
    // ancestor column still holding a selection (the path drilled through to get here)
    // shows a dimmer, secondary highlight instead.
    private void RecomputeColumnSelectionStates()
    {
        int deepest = -1;
        for (int i = 0; i < Columns.Count; i++)
            if (Columns[i].SelectedItem != null) deepest = i;
        for (int i = 0; i < Columns.Count; i++)
            Columns[i].IsPrimarySelection = i == deepest;
    }

    // ── Drag/drop move & copy — optimistic UI sync ─────────────────
    // File.Move/Copy already happened on disk by the time these run; we mirror the
    // change into whichever collections are currently showing the affected folder(s)
    // instead of waiting on the debounced FileSystemWatcher refresh (was the source of
    // a multi-second lag between dropping a file and seeing it move).

    public void NotifyFileMoved(string oldPath, string newPath)
    {
        RemoveTrackedItem(oldPath);
        AddTrackedItem(newPath);
    }

    public void NotifyFileAdded(string newPath) => AddTrackedItem(newPath);

    private void RemoveTrackedItem(string path)
    {
        var rootItem = Files.FirstOrDefault(f => PathsEqual(f.FullPath, path));
        if (rootItem != null) Files.Remove(rootItem);

        foreach (var col in Columns)
        {
            var item = col.Items.FirstOrDefault(f => PathsEqual(f.FullPath, path));
            if (item == null) continue;
            col.Items.Remove(item);
            break;
        }
    }

    private void AddTrackedItem(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder)) return;

        bool isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return;
        bool isMedia = isDir || MediaExtensions.All.Contains(Path.GetExtension(path));

        if (PathsEqual(folder, _currentPath) && !Files.Any(f => PathsEqual(f.FullPath, path)))
        {
            var vm = MakeFileItem(path, isDir);
            Files.Add(vm);
            if (!isDir) _thumbnailService.Enqueue(vm, ThumbnailSize, _loadCts.Token);
        }

        if (!isMedia) return;
        foreach (var col in Columns)
        {
            if (!PathsEqual(col.Path, folder) || col.Items.Any(f => PathsEqual(f.FullPath, path))) continue;
            var vm = MakeFileItem(path, isDir);
            InsertColumnSorted(col.Items, vm);
            if (!isDir) _thumbnailService.Enqueue(vm, 24, _loadCts.Token);
            break;
        }
    }

    private static FileItemViewModel MakeFileItem(string path, bool isDir) =>
        isDir ? new FileItemViewModel(new DirectoryInfo(path)) : new FileItemViewModel(new FileInfo(path));

    private static void InsertColumnSorted(ObservableCollection<FileItemViewModel> items, FileItemViewModel vm)
    {
        int i = 0;
        if (vm.IsDirectory)
        {
            while (i < items.Count && items[i].IsDirectory &&
                   string.Compare(items[i].FileName, vm.FileName, StringComparison.OrdinalIgnoreCase) < 0) i++;
        }
        else
        {
            while (i < items.Count && items[i].IsDirectory) i++;
            while (i < items.Count &&
                   string.Compare(items[i].FileName, vm.FileName, StringComparison.OrdinalIgnoreCase) < 0) i++;
        }
        items.Insert(i, vm);
    }

    private static bool PathsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void RequestListViewThumbnails()
    {
        var token = _loadCts.Token;
        foreach (FileItemViewModel item in _filesView)
            if (!item.IsDirectory && item.Thumbnail == null && !item.ThumbnailUnavailable)
                _thumbnailService.Enqueue(item, 24, token);
    }

    private async void AppendColumn(string path)
    {
        var col = new ColumnViewModel(path);
        col.PropertyChanged += ColumnViewModel_PropertyChanged;
        Columns.Add(col);
        RecomputeColumnSelectionStates();
        await col.LoadAsync();
        var token = _loadCts.Token;
        foreach (var item in col.Items)
            if (!item.IsDirectory)
                _thumbnailService.Enqueue(item, 24, token);
    }

    private string _currentPath = string.Empty;
    public string CurrentPath => _currentPath;

    private string _pathBarText = string.Empty;
    public string PathBarText
    {
        get => _pathBarText;
        set => SetField(ref _pathBarText, value);
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private bool _undoAvailable;
    public bool UndoAvailable
    {
        get => _undoAvailable;
        private set => SetField(ref _undoAvailable, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    private bool _isPathEditing;
    public bool IsPathEditing
    {
        get => _isPathEditing;
        set { if (SetField(ref _isPathEditing, value)) OnPropertyChanged(nameof(IsBreadcrumbMode)); }
    }

    // ── Search (recursive, filename-only, current folder down) ──────
    public ObservableCollection<FileItemViewModel> SearchResults { get; } = new();

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set { if (SetField(ref _isSearching, value)) OnPropertyChanged(nameof(IsBreadcrumbMode)); }
    }

    public bool IsBreadcrumbMode => !IsPathEditing && !IsSearching;

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set { if (SetField(ref _searchQuery, value)) ScheduleSearch(); }
    }

    private string _searchStatusText = string.Empty;
    public string SearchStatusText
    {
        get => _searchStatusText;
        private set => SetField(ref _searchStatusText, value);
    }

    private FileItemViewModel? _selectedItem;
    public FileItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetField(ref _selectedItem, value);
    }

    // Managed by the view's SelectionChanged handler
    public List<FileItemViewModel> SelectedItems { get; } = new();

    public bool CanGoBack    => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;
    public bool CanGoUp      => !string.IsNullOrEmpty(_currentPath) &&
                                Path.GetDirectoryName(_currentPath) is not null;

    public ICommand GoBackCommand              { get; }
    public ICommand GoForwardCommand           { get; }
    public ICommand GoUpCommand                { get; }
    public ICommand NavigateCommand            { get; }
    public ICommand CommitPathCommand          { get; }
    public ICommand NavigateToSegmentCommand   { get; }
    public ICommand ClearPathBarCommand        { get; }
    public ICommand DecreaseSizeCommand        { get; }
    public ICommand IncreaseSizeCommand        { get; }
    public ICommand DeleteSelectedCommand      { get; }
    public ICommand UndoDeleteCommand          { get; }
    public ICommand NewFolderCommand           { get; }
    public ICommand RefreshCommand             { get; }
    public ICommand PinCurrentFolderCommand    { get; }
    public ICommand UnpinFolderCommand         { get; }
    public ICommand ToggleSidebarCommand       { get; }
    public ICommand OpenRecycleBinCommand      { get; }

    public ObservableCollection<PinnedFolderViewModel> PinnedFolders { get; } = new();

    private bool _isSidebarOpen;
    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => SetField(ref _isSidebarOpen, value);
    }

    private ViewMode _viewMode = ViewMode.Grid;
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (!SetField(ref _viewMode, value)) return;
            OnPropertyChanged(nameof(IsGridView));
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(IsColumnView));
        }
    }
    public bool IsGridView   => _viewMode == ViewMode.Grid;
    public bool IsListView   => _viewMode == ViewMode.List;
    public bool IsColumnView => _viewMode == ViewMode.Column;

    public bool CanPinCurrentFolder =>
        !string.IsNullOrEmpty(_currentPath) &&
        !PinnedFolders.Any(p => string.Equals(p.FullPath, _currentPath, StringComparison.OrdinalIgnoreCase));

    public IntPtr OwnerHwnd { get; set; }

    public MainViewModel()
    {
        _filesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Files);
        _filesView.CustomSort = new FileItemComparer(_sortMode, _sortDirection);

        GoBackCommand            = new RelayCommand(_ => GoBack(),                   _ => CanGoBack);
        GoForwardCommand         = new RelayCommand(_ => GoForward(),                _ => CanGoForward);
        GoUpCommand              = new RelayCommand(_ => GoUp(),                     _ => CanGoUp);
        NavigateCommand          = new RelayCommand(p => { if (p is string s) Navigate(s); });
        CommitPathCommand        = new RelayCommand(_ => CommitPath());
        NavigateToSegmentCommand = new RelayCommand(p => { if (p is string s) Navigate(s); });
        ClearPathBarCommand      = new RelayCommand(_ => PathBarText = string.Empty);
        DecreaseSizeCommand      = new RelayCommand(_ => ThumbnailSize = SnapPoints.Reverse().FirstOrDefault(s => s < _thumbnailSize, SizeMin));
        IncreaseSizeCommand      = new RelayCommand(_ => ThumbnailSize = SnapPoints.FirstOrDefault(s => s > _thumbnailSize, SizeMax));
        DeleteSelectedCommand    = new RelayCommand(_ => DeleteSelected(),   _ => SelectedItem != null);
        UndoDeleteCommand        = new RelayCommand(_ => UndoDelete(),      _ => _undoGroups.Count > 0);
        NewFolderCommand         = new RelayCommand(_ => CreateNewFolder());
        RefreshCommand           = new RelayCommand(_ => _ = LoadFolderAsync(_currentPath));
        PinCurrentFolderCommand  = new RelayCommand(
            _ => { PinnedFolders.Add(new PinnedFolderViewModel(_currentPath)); OnPropertyChanged(nameof(CanPinCurrentFolder)); },
            _ => CanPinCurrentFolder);
        UnpinFolderCommand       = new RelayCommand(
            p => { if (p is PinnedFolderViewModel vm) { PinnedFolders.Remove(vm); OnPropertyChanged(nameof(CanPinCurrentFolder)); } });
        ToggleSidebarCommand     = new RelayCommand(_ => IsSidebarOpen = !IsSidebarOpen);
        OpenRecycleBinCommand    = new RelayCommand(_ => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }));
    }

    // ── Navigation ────────────────────────────────────────────────

    public void Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _ = NavigateAsync(path);
    }

    // Directory.Exists is a blocking filesystem call — for a pinned folder on a network
    // share or a not-yet-hydrated cloud (OneDrive) placeholder it can stall for a
    // noticeable moment. Do it off the UI thread and flip IsLoading on first, so the
    // window shows a spinner immediately instead of freezing with no feedback.
    private async Task NavigateAsync(string path)
    {
        IsLoading  = true;
        StatusText = "Loading…";

        bool exists = await Task.Run(() => Directory.Exists(path));
        if (!exists)
        {
            IsLoading  = false;
            StatusText = "Folder not found.";
            return;
        }

        if (!string.IsNullOrEmpty(_currentPath)) _backStack.Push(_currentPath);
        _forwardStack.Clear();
        await LoadFolderAsync(path);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        _forwardStack.Push(_currentPath);
        _ = LoadFolderAsync(_backStack.Pop());
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        _backStack.Push(_currentPath);
        _ = LoadFolderAsync(_forwardStack.Pop());
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public void GoUp()
    {
        string? parent = Path.GetDirectoryName(_currentPath);
        if (parent != null) Navigate(parent);
    }

    public void Open(FileItemViewModel item)
    {
        if (item.IsDirectory) Navigate(item.FullPath);
        else Taste.Interop.ShellInterop.OpenFile(item.FullPath);
    }

    private void CommitPath()
    {
        string path = _pathBarText.Trim();
        if (Directory.Exists(path)) Navigate(path);
        else PathBarText = _currentPath;
        ExitPathEditMode();
    }

    public void EnterPathEditMode()
    {
        IsSearching   = false;
        PathBarText   = _currentPath;
        IsPathEditing = true;
    }

    public void ExitPathEditMode()
    {
        IsPathEditing = false;
        PathBarText   = _currentPath;
    }

    public void EnterSearchMode()
    {
        IsPathEditing = false;
        IsSearching   = true;
    }

    public void ExitSearchMode()
    {
        IsSearching  = false;
        SearchQuery  = string.Empty;
        SearchResults.Clear();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
        _searchCts.Cancel();
    }

    private void ScheduleSearch()
    {
        _searchCts.Cancel();
        _searchDebounce?.Dispose();

        if (string.IsNullOrWhiteSpace(_searchQuery)) { SearchResults.Clear(); SearchStatusText = ""; return; }

        string query = _searchQuery;
        _searchDebounce = new System.Threading.Timer(_ => _ = RunSearchAsync(query),
            null, AppConstants.SearchDebounceMs, Timeout.Infinite);
    }

    // Runs the recursive walk on a background thread, flushing matches to SearchResults in
    // small batches (by count or by time, whichever comes first) so a fast local disk can't
    // flood the UI thread with per-item updates, while a slow one still feels live.
    private async Task RunSearchAsync(string query)
    {
        _searchCts.Cancel();
        _searchCts.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        string root = _currentPath;
        var dispatcher = Application.Current.Dispatcher;

        dispatcher.Invoke(() => { SearchResults.Clear(); SearchStatusText = "Searching…"; });

        int total = 0;
        bool capped = false;

        void Flush(List<FileItemViewModel> batch)
        {
            if (batch.Count == 0 || token.IsCancellationRequested) return;
            var items = batch.ToList();
            batch.Clear();
            dispatcher.Invoke(() =>
            {
                foreach (var item in items)
                {
                    SearchResults.Add(item);
                    if (!item.IsDirectory) _thumbnailService.Enqueue(item, 24, token);
                }
            });
        }

        try
        {
            await Task.Run(() =>
            {
                var batch = new List<FileItemViewModel>(AppConstants.SearchBatchSize);
                var lastFlush = DateTime.UtcNow;

                foreach (var item in FolderSearchService.Search(root, query, token))
                {
                    token.ThrowIfCancellationRequested();
                    batch.Add(item);
                    total++;
                    if (total >= AppConstants.SearchResultCap) { capped = true; break; }

                    if (batch.Count >= AppConstants.SearchBatchSize ||
                        (DateTime.UtcNow - lastFlush).TotalMilliseconds >= AppConstants.SearchBatchMs)
                    {
                        Flush(batch);
                        lastFlush = DateTime.UtcNow;
                    }
                }
                Flush(batch);
            }, token);

            if (token.IsCancellationRequested) return;
            dispatcher.Invoke(() => SearchStatusText = capped
                ? $"More than {AppConstants.SearchResultCap:N0} results — refine your search"
                : total == 0 ? "No results" : $"{total:N0} result{(total == 1 ? "" : "s")}");
        }
        catch (OperationCanceledException) { }
    }

    private void RebuildPathSegments(string path)
    {
        PathSegments.Clear();
        if (string.IsNullOrEmpty(path)) return;

        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string cumulative = string.Empty;
        foreach (var part in parts)
        {
            cumulative = string.IsNullOrEmpty(cumulative)
                ? part + Path.DirectorySeparatorChar
                : Path.Combine(cumulative, part);
            PathSegments.Add(new PathSegment(part, cumulative));
        }
        if (PathSegments.Count > 0)
            PathSegments[^1].IsLast = true;
    }

    // ── Folder loading ────────────────────────────────────────────
    // Folders are loaded a page at a time so huge folders don't stall the UI or get
    // silently truncated — LoadMoreIfNeeded() pulls in the next page as the user scrolls near
    // the bottom of what's currently loaded, using the enumerators left open from the last page.

    private const int PageSize  = 500;
    private const int EnumBatch = 50;

    public bool HasMoreItems => _dirEnumerator != null || _fileEnumerator != null;

    private async Task LoadFolderAsync(string path)
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        _vpScrollOffset = 0;
        _lastThumbScrollOffset = -1; // force thumb request after load

        // Dispose old watcher immediately so it doesn't fire during the new load
        _watcher?.Dispose();
        _watcher = null;

        DisposeEnumerators();

        _currentPath = path;
        PathBarText  = path;
        RebuildPathSegments(path);
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanPinCurrentFolder));

        Files.Clear();
        SelectedItem = null;
        SelectedItems.Clear();
        IsLoading    = true;
        StatusText   = "Loading…";

        try
        {
            var di = new DirectoryInfo(path);
            _dirEnumerator  = di.EnumerateDirectories().GetEnumerator();
            _fileEnumerator = di.EnumerateFiles().GetEnumerator();
        }
        catch
        {
            IsLoading = false;
            StatusText = "Could not open folder.";
            return;
        }

        await LoadNextPageAsync();

        if (token.IsCancellationRequested) return;

        IsLoading = false;
        UpdateStatus();

        // Cycle the token: any stale requests the progress batches may have enqueued
        // are now cancelled so the worker thread doesn't burn time on off-screen items.
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        // The cancelled requests above included the initial visible-thumbnail batch, at the
        // current scroll offset — force this re-request through even though the offset hasn't
        // moved, or the scroll-debounce below would otherwise skip it entirely.
        _lastThumbScrollOffset = -1;
        RequestVisibleThumbnails(_loadCts.Token);

        // Skip the watcher while there's more to page in — not worth the overhead, and a live
        // folder change mid-pagination would be hard to reconcile with the open enumerators.
        if (!HasMoreItems)
            SetupWatcher(path);

        if (_viewMode == ViewMode.Column)
            InitColumns();
    }

    /// Pulls in the next page of items from the enumerators left open by the last page,
    /// appending to Files. Safe to call repeatedly (e.g. from scroll); no-ops if a page is
    /// already loading or the folder is fully loaded.
    public async Task LoadNextPageAsync()
    {
        if (_isLoadingMore || !HasMoreItems) return;
        _isLoadingMore = true;
        var token = _loadCts.Token;

        var progress = new Progress<List<FileItemViewModel>>(batch =>
        {
            if (token.IsCancellationRequested) return;
            foreach (var item in batch)
                Files.Add(item);
        });

        try
        {
            await Task.Run(() => EnumeratePage(progress, token), token);
        }
        catch (OperationCanceledException) { return; }
        finally { _isLoadingMore = false; }

        if (token.IsCancellationRequested) return;

        UpdateStatus();
        RequestVisibleThumbnails(token);
    }

    public void LoadMoreIfNeeded()
    {
        if (_isLoadingMore || !HasMoreItems) return;
        _ = LoadNextPageAsync();
    }

    /// Consumes up to PageSize items from _dirEnumerator then _fileEnumerator, reporting in
    /// batches. Exhausted enumerators are disposed and nulled out so HasMoreItems reflects reality.
    private void EnumeratePage(IProgress<List<FileItemViewModel>> progress, CancellationToken token)
    {
        var batch  = new List<FileItemViewModel>(EnumBatch);
        int loaded = 0;

        void Flush()
        {
            if (batch.Count == 0) return;
            progress.Report(batch.ToList());
            batch.Clear();
        }

        while (loaded < PageSize && _dirEnumerator != null)
        {
            token.ThrowIfCancellationRequested();
            bool moved;
            try { moved = _dirEnumerator.MoveNext(); }
            catch { moved = false; }
            if (!moved) { _dirEnumerator.Dispose(); _dirEnumerator = null; break; }

            try
            {
                var d = _dirEnumerator.Current;
                if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                if (d.Name.StartsWith('.')) continue;
                batch.Add(new FileItemViewModel(d));
                loaded++;
                if (batch.Count >= EnumBatch) Flush();
            }
            catch { /* skip inaccessible entries */ }
        }

        while (loaded < PageSize && _fileEnumerator != null)
        {
            token.ThrowIfCancellationRequested();
            bool moved;
            try { moved = _fileEnumerator.MoveNext(); }
            catch { moved = false; }
            if (!moved) { _fileEnumerator.Dispose(); _fileEnumerator = null; break; }

            try
            {
                var f = _fileEnumerator.Current;
                if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                if (f.Name.StartsWith('.')) continue;
                batch.Add(new FileItemViewModel(f));
                loaded++;
                if (batch.Count >= EnumBatch) Flush();
            }
            catch { /* skip inaccessible entries */ }
        }

        Flush();
    }

    private void DisposeEnumerators()
    {
        _dirEnumerator?.Dispose();
        _dirEnumerator = null;
        _fileEnumerator?.Dispose();
        _fileEnumerator = null;
    }

    // ── Sorting ───────────────────────────────────────────────────

    private void ApplySort()
    {
        _filesView.CustomSort = new FileItemComparer(_sortMode, _sortDirection);
        _lastThumbScrollOffset = -1;
        RequestVisibleThumbnails(_loadCts.Token);
    }

    private sealed class FileItemComparer(SortMode mode, SortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not FileItemViewModel a || y is not FileItemViewModel b) return 0;
            int dirOrder = a.SortOrder.CompareTo(b.SortOrder);
            if (dirOrder != 0) return dirOrder;
            int result = mode switch
            {
                SortMode.Name         => StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName),
                SortMode.DateModified => a.DateModified.CompareTo(b.DateModified),
                SortMode.Type         => StringComparer.OrdinalIgnoreCase.Compare(a.Extension, b.Extension) is var ec && ec != 0
                                         ? ec : StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName),
                SortMode.Size         => a.FileSize.CompareTo(b.FileSize),
                _                     => 0,
            };
            return direction == SortDirection.Descending ? -result : result;
        }
    }

    public void UpdateStatus()
    {
        if (SelectedItems.Count > 1)
        {
            StatusText = $"{SelectedItems.Count} items selected";
            return;
        }
        int dirs  = Files.Count(f => f.IsDirectory);
        int files = Files.Count(f => !f.IsDirectory);
        string more = HasMoreItems ? " (scroll for more)" : "";
        StatusText = (dirs > 0 ? $"{files:N0} items · {dirs:N0} folders" : $"{files:N0} items") + more;
    }

    // ── Viewport-aware thumbnail management ───────────────────────

    public void UpdateViewport(double scrollOffset, double viewportHeight, double contentWidth)
    {
        _vpScrollOffset  = scrollOffset;
        _vpHeight        = viewportHeight;
        _vpContentWidth  = contentWidth;
        RequestVisibleThumbnails(_loadCts.Token);
    }

    private void RequestVisibleThumbnails(CancellationToken token)
    {
        if (Files.Count == 0) return;

        // Skip if scroll moved less than half a thumbnail row — avoids burning the UI thread on every pixel
        double halfRow = (ThumbnailSize + 25) / 2.0;
        if (_lastThumbScrollOffset >= 0 && Math.Abs(_vpScrollOffset - _lastThumbScrollOffset) < halfRow) return;
        _lastThumbScrollOffset = _vpScrollOffset;

        double vpH = _vpHeight > 0 ? _vpHeight : 600;
        double vpW = _vpContentWidth > 0 ? _vpContentWidth : 1000;

        double itemW = ThumbnailSize + 8;   // StackPanel Margin="4" each side
        double itemH = ThumbnailSize + 25;
        const double MinGap = 6.0;          // matches JustifiedWrapPanel.MinGap default
        int perRow = Math.Max(1, (int)((vpW - 16 + MinGap) / (itemW + MinGap)));

        int firstLoadRow = Math.Max(0, (int)((_vpScrollOffset - vpH) / itemH));
        int lastLoadRow  = (int)((_vpScrollOffset + vpH * 2) / itemH) + 1;
        int firstLoad    = firstLoadRow * perRow;
        int lastLoad     = Math.Min(Files.Count - 1, lastLoadRow * perRow + perRow - 1);

        int firstKeepRow = Math.Max(0, (int)((_vpScrollOffset - vpH * 3) / itemH));
        int lastKeepRow  = (int)((_vpScrollOffset + vpH * 4) / itemH) + 1;
        int firstKeep    = firstKeepRow * perRow;
        int lastKeep     = Math.Min(Files.Count - 1, lastKeepRow * perRow + perRow - 1);

        // Scrolled near the bottom of what's currently loaded — pull in the next page
        int totalRows = (Files.Count + perRow - 1) / perRow;
        if (lastLoadRow >= totalRows - 2)
            LoadMoreIfNeeded();

        // Iterate _filesView (display order) so indices match what's actually visible on screen
        int i = 0;
        foreach (FileItemViewModel item in _filesView)
        {
            if (i < firstKeep || i > lastKeep)
                item.ClearThumbnail();
            else if (i >= firstLoad && i <= lastLoad && !item.IsDirectory && item.Thumbnail == null && !item.ThumbnailUnavailable)
                _thumbnailService.Enqueue(item, ThumbnailSize, token);
            i++;
        }
    }

    private void ReloadThumbnails()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        _lastThumbScrollOffset = -1;
        foreach (var item in Files) item.ClearThumbnail();
        RequestVisibleThumbnails(_loadCts.Token);
    }

    // ── File operations ───────────────────────────────────────────

    private void DeleteSelected()
    {
        var toDelete = SelectedItems.Count > 0 ? SelectedItems.ToList()
                     : SelectedItem != null     ? new List<FileItemViewModel> { SelectedItem }
                     : null;
        if (toDelete == null || toDelete.Count == 0) return;

        int firstIndex = Files.IndexOf(toDelete[0]);
        foreach (var item in toDelete) Files.Remove(item);
        SelectedItem = Files.Count > 0 ? Files[Math.Min(firstIndex, Files.Count - 1)] : null;
        SelectedItems.Clear();
        UpdateStatus();

        SendToRecycleBinAndTrackUndo(toDelete, Files);
    }

    // Column view has no notion of "the current folder" — each panel browses its own path —
    // so deleting the selection there must remove it from that panel's own Items list.
    public void DeleteSelectedInColumn(ColumnViewModel col)
    {
        if (col.SelectedItem == null) return;
        var toDelete = new List<FileItemViewModel> { col.SelectedItem };

        int firstIndex = col.Items.IndexOf(toDelete[0]);
        col.Items.Remove(toDelete[0]);
        col.SelectedItem = col.Items.Count > 0 ? col.Items[Math.Min(firstIndex, col.Items.Count - 1)] : null;

        SendToRecycleBinAndTrackUndo(toDelete, col.Items);
    }

    private void SendToRecycleBinAndTrackUndo(List<FileItemViewModel> toDelete, ObservableCollection<FileItemViewModel> targetCollection)
    {
        var group = new List<DeletedItem>();
        foreach (var item in toDelete)
        {
            FileOperationService.SendToRecycleBin(OwnerHwnd, item.FullPath);
            var entry = Taste.Interop.ShellInterop.FindRecycleBinEntry(item.FullPath);
            if (entry.HasValue)
                group.Add(new DeletedItem(item.FullPath, entry.Value.RFile, entry.Value.IFile, item.IsDirectory, targetCollection));
        }

        if (group.Count > 0)
        {
            if (_undoGroups.Count >= 10) _undoGroups.RemoveAt(0);
            _undoGroups.Add(group);
            UndoAvailable = true;
            _undoTimer?.Dispose();
            _undoTimer = new System.Threading.Timer(_ =>
                Application.Current?.Dispatcher.BeginInvoke(() => UndoAvailable = false),
                null, AppConstants.UndoExpiryMs, Timeout.Infinite);
        }
    }

    private void UndoDelete()
    {
        if (_undoGroups.Count == 0) return;
        var group = _undoGroups[^1];
        _undoGroups.RemoveAt(_undoGroups.Count - 1);
        _undoTimer?.Dispose();

        foreach (var item in group)
        {
            try
            {
                if (Directory.Exists(item.RFile))
                    Directory.Move(item.RFile, item.OriginalPath);
                else
                    File.Move(item.RFile, item.OriginalPath);
                File.Delete(item.IFile);

                FileItemViewModel vm = item.IsDirectory
                    ? new FileItemViewModel(new DirectoryInfo(item.OriginalPath))
                    : new FileItemViewModel(new FileInfo(item.OriginalPath));
                if (item.TargetCollection == Files)
                {
                    Files.Add(vm);
                    SelectedItem = vm;
                }
                else
                {
                    InsertColumnSorted(item.TargetCollection, vm);
                }
                if (!vm.IsDirectory) _thumbnailService.Enqueue(vm, ThumbnailSize, _loadCts.Token);
            }
            catch { }
        }

        UpdateStatus();
        UndoAvailable = _undoGroups.Count > 0;
    }

    private void CreateNewFolder()
    {
        string name      = "New Folder";
        string candidate = Path.Combine(_currentPath, name);
        int i = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(_currentPath, $"{name} ({i++})");

        string? created = FileOperationService.CreateFolder(_currentPath, Path.GetFileName(candidate));
        if (created == null) return;

        var vm = new FileItemViewModel(created, true) { IsRenaming = true };
        Files.Insert(0, vm);
        SelectedItem = vm;
        UpdateStatus();
    }

    // Column view has no notion of "the current folder" — each panel browses its own path —
    // so a folder created from a panel's empty-space menu must target that panel specifically.
    public void CreateNewFolderInColumn(ColumnViewModel col)
    {
        string name      = "New Folder";
        string candidate = Path.Combine(col.Path, name);
        int i = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(col.Path, $"{name} ({i++})");

        string? created = FileOperationService.CreateFolder(col.Path, Path.GetFileName(candidate));
        if (created == null) return;

        var vm = new FileItemViewModel(created, true);
        InsertColumnSorted(col.Items, vm);
        col.SelectedItem = vm;
    }

    public void RenameSelected()
    {
        if (SelectedItem == null) return;
        SelectedItem.RenameText = SelectedItem.FileName;
        SelectedItem.IsRenaming = true;
    }

    public void CommitRename(FileItemViewModel item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        string newName = item.RenameText.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.FileName) return;
        string oldPath = item.FullPath;
        item.ApplyRename(newName);          // update UI immediately
        FileOperationService.Rename(oldPath, newName);
    }

    public void CancelRename(FileItemViewModel item)
    {
        item.IsRenaming = false;
        item.RenameText = item.FileName;
    }

    // ── FileSystemWatcher ─────────────────────────────────────────

    private void SetupWatcher(string path)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter        = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnWatcherEvent;
            _watcher.Deleted += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
            _watcher.Error   += OnWatcherError;
        }
        catch { _watcher = null; }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => ScheduleDebouncedRefresh();
    private void OnWatcherError(object sender, ErrorEventArgs e)      => ScheduleDebouncedRefresh();

    private void ScheduleDebouncedRefresh()
    {
        _watcherDebounce?.Dispose();
        _watcherDebounce = new System.Threading.Timer(_ =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Files.Any(f => f.IsRenaming)) return;
                _ = LoadFolderAsync(_currentPath);
            }),
            null, AppConstants.WatcherDebounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _watcherDebounce?.Dispose();
        _undoTimer?.Dispose();
        _searchDebounce?.Dispose();
        _searchCts.Cancel();
        _searchCts.Dispose();
        _watcher?.Dispose();
        _thumbnailService.Dispose();
        DisposeEnumerators();
    }
}
