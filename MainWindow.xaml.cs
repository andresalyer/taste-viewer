using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;
using Taste.Controls;
using Taste.Interop;
using Taste.Services;
using Taste.Shared;
using Taste.ViewModels;

namespace Taste;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;
    private System.Threading.Timer? _scrollbarHideTimer;
    private double _sidebarWidth = 220;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded  += OnLoaded;
        Closing += OnClosing;
        SizeChanged += (_, _) => DoUpdateViewport();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SettingsService.Save(new AppSettings
        {
            ThumbnailSize = Vm.ThumbnailSize,
            LastFolder    = Vm.CurrentPath,
            WindowWidth   = Width,
            WindowHeight  = Height,
            WindowLeft    = Left,
            WindowTop     = Top,
            SidebarOpen   = Vm.IsSidebarOpen,
            SidebarWidth  = Vm.IsSidebarOpen ? SidebarColumn.ActualWidth : _sidebarWidth,
            PinnedFolders          = Vm.PinnedFolders.Select(p => p.FullPath).ToList(),
            ExplorerSpacebarEnabled = ((App)Application.Current).Watcher.ExplorerSpacebarEnabled,
        });
        // Preview is an app-lifetime singleton — without this, every closed secondary
        // window (Ctrl+N) stays rooted by its CurrentFileChanged subscription forever,
        // leaking the window's whole visual tree and every thumbnail it ever loaded.
        ((App)Application.Current).Preview.CurrentFileChanged -= Preview_CurrentFileChanged;
        Vm.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Vm.OwnerHwnd = hwnd;
        ShellInterop.SetDarkTitleBar(hwnd);

        Vm.PropertyChanged += OnVmPropertyChanged;
        ((App)Application.Current).Preview.CurrentFileChanged += Preview_CurrentFileChanged;

        var s = SettingsService.Load();

        // Restore window geometry
        if (s.WindowLeft >= 0) { Left = s.WindowLeft; Top = s.WindowTop; }
        Width  = s.WindowWidth;
        Height = s.WindowHeight;

        // Restore VM state
        Vm.ThumbnailSize = s.ThumbnailSize;

        // Restore pinned folders
        foreach (var path in s.PinnedFolders)
            Vm.PinnedFolders.Add(new PinnedFolderViewModel(path));

        // Sync sort menu checkmarks to default sort (Name, Ascending)
        foreach (MenuItem item in new[] { SortName, SortDate, SortType, SortSize })
            item.IsChecked = item.Header.ToString() == Vm.SelectedSort;
        foreach (MenuItem item in new[] { SortAscending, SortDescending })
            item.IsChecked = item.Header.ToString() == Vm.SelectedSortDirection;

        ExplorerSpacebarItem.IsChecked = s.ExplorerSpacebarEnabled;

        // Restore sidebar state
        _sidebarWidth = s.SidebarWidth;
        if (s.SidebarOpen)
        {
            Vm.IsSidebarOpen = true;
            ApplySidebarState(true);
        }

        // Navigate to last folder or default. A brand-new user (no pinned folders yet)
        // has no "last folder" either, so drop them somewhere familiar instead of My Pictures.
        string? start = System.IO.Directory.Exists(s.LastFolder) ? s.LastFolder : null;
        if (start == null)
        {
            start = Vm.PinnedFolders.Count == 0
                ? @"C:\"
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        if (System.IO.Directory.Exists(start))
            Vm.Navigate(start);
        else
            Vm.EnterPathEditMode(); // Nowhere familiar to land — invite them to paste a folder path.
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLoading) && !Vm.IsLoading)
        {
            DoUpdateViewport();
            if (Vm.IsListView) Vm.RequestListViewThumbnails();

            if (Vm.PendingScrollRestore is double savedOffset)
            {
                string targetPath = Vm.CurrentPath;
                Vm.ClearPendingScrollRestore();
                // Defer past the rest of this load's synchronous tail (it still swaps _loadCts
                // after this event fires) so our own paging calls below don't race its cancellation.
                Dispatcher.BeginInvoke(() => _ = RestoreScrollAsync(targetPath, savedOffset),
                    DispatcherPriority.Background);
            }
        }

        if (e.PropertyName == nameof(MainViewModel.CurrentPath))
            Dispatcher.BeginInvoke(BreadcrumbScroller.ScrollToRightEnd,
                System.Windows.Threading.DispatcherPriority.Loaded);

        if (e.PropertyName == nameof(MainViewModel.IsSidebarOpen))
            ApplySidebarState(Vm.IsSidebarOpen);

        if (e.PropertyName is nameof(MainViewModel.ShowDateColumn)
                           or nameof(MainViewModel.ShowTypeColumn)
                           or nameof(MainViewModel.ShowSizeColumn)
                           or nameof(MainViewModel.ShowPathColumn))
            UpdateListColumns();
    }

    private void ApplySidebarState(bool open)
    {
        if (open)
        {
            SidebarColumn.Width        = new GridLength(_sidebarWidth);
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (SidebarColumn.ActualWidth > 0)
                _sidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.Width        = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        double w = SidebarColumn.ActualWidth;
        if (w < 150) w = 150;
        SidebarColumn.Width = new GridLength(w);
        _sidebarWidth = w;
    }

    // Column view — each column's width is in-memory only (ColumnViewModel.Width), not persisted
    // to AppSettings, so every column always starts at ColumnViewModel.WidthDefault each session.
    private void ColumnResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ColumnViewModel col)
            col.Width += e.HorizontalChange;
    }

    // ── Sidebar navigation ────────────────────────────────────────

    private void PinnedItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (Controls.ListBoxDragDropBehavior.IsDragging) return;
        if (sender is FrameworkElement fe && fe.DataContext is PinnedFolderViewModel pf)
        {
            if (Vm.IsSearching) Vm.ExitSearchMode();
            Vm.Navigate(pf.FullPath);
        }
    }

    private void PinnedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb) lb.SelectedItem = null;
    }

    // ── Viewport thumbnail management ──────────────────────────────

    private void DoUpdateViewport()
    {
        Dispatcher.BeginInvoke(() =>
        {
            double contentWidth = FileListBox.ActualWidth > 0
                ? FileListBox.ActualWidth
                : MainScrollViewer.ActualWidth;
            Vm.UpdateViewport(
                MainScrollViewer.VerticalOffset,
                MainScrollViewer.ViewportHeight,
                contentWidth);
        });
    }

    // Large folders page in ~500 items at a time, so a saved offset from a previous visit may
    // point past what's currently loaded. Keep paging in more until the extent covers it (or the
    // folder runs out) before scrolling, so we don't scroll into empty space below the last item.
    private async Task RestoreScrollAsync(string path, double offset)
    {
        int safety = 0;
        while (Vm.CurrentPath == path
               && Vm.HasMoreItems
               && MainScrollViewer.ExtentHeight < offset + MainScrollViewer.ViewportHeight
               && safety++ < 50)
        {
            await Vm.LoadNextPageAsync();
            MainScrollViewer.UpdateLayout();
        }

        if (Vm.CurrentPath == path)
            MainScrollViewer.ScrollToVerticalOffset(offset);
    }

    // ── Scrollbar auto-hide ────────────────────────────────────────

    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        DoUpdateViewport();

        // Show scrollbar briefly, then fade it out
        ShowScrollBar();
        _scrollbarHideTimer?.Dispose();
        _scrollbarHideTimer = new System.Threading.Timer(_ =>
            Dispatcher.BeginInvoke(HideScrollBar), null, AppConstants.ScrollbarHideDelayMs, Timeout.Infinite);
    }

    private ScrollBar? _vertScrollBar;

    private ScrollBar? GetVertScrollBar()
    {
        if (_vertScrollBar != null) return _vertScrollBar;
        _vertScrollBar = MainScrollViewer.Template.FindName("PART_VerticalScrollBar", MainScrollViewer) as ScrollBar
                         ?? FindVisualChild<ScrollBar>(MainScrollViewer, sb => sb.Orientation == Orientation.Vertical);
        return _vertScrollBar;
    }

    private void ShowScrollBar() { if (GetVertScrollBar() is { } sb) sb.Opacity = 1; }
    private void HideScrollBar() { if (GetVertScrollBar() is { } sb) sb.ClearValue(UIElement.OpacityProperty); }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (predicate == null || predicate(t))) return t;
            var result = FindVisualChild(child, predicate);
            if (result != null) return result;
        }
        return null;
    }

    // ── Path bar ──────────────────────────────────────────────────

    private void PathBar_ContainerPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Breadcrumb, path-edit, and search mode all share this container. Without this,
        // clicking into the search box (not a Button, so the walk-up below doesn't exempt
        // it) fell through to EnterPathEditMode() and silently swapped out of search —
        // the edit-mode Grid then occupies the same spot search's UI just vacated, so the
        // "search" the user thinks they're still looking at (including its close button)
        // is actually path-edit mode's own text field and clear button.
        if (Vm.IsPathEditing || Vm.IsSearching) return;

        // If the click landed on a breadcrumb button let it navigate normally
        var src = e.OriginalSource as DependencyObject;
        while (src != null && !ReferenceEquals(src, sender))
        {
            if (src is Button) return;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }

        Vm.EnterPathEditMode();
        e.Handled = true; // prevent mouse-up from repositioning cursor and clearing selection
    }

    private void PathBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
            tb.Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PathBar_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void PathBar_LostFocus(object sender, RoutedEventArgs e)
    {
        Vm.ExitPathEditMode();
    }

    private void PathBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.CommitPathCommand.Execute(null);
            FileListBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.ExitPathEditMode();
            FileListBox.Focus();
            e.Handled = true;
        }
    }

    // ── Rename box ────────────────────────────────────────────────

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
            tb.Dispatcher.BeginInvoke(tb.Focus, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
            tb.Dispatcher.BeginInvoke(tb.Focus, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        string text = tb.Text;
        int dot = text.LastIndexOf('.');
        tb.Select(0, dot > 0 ? dot : text.Length);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not FileItemViewModel item) return;
        if (e.Key == Key.Enter)        { Vm.CommitRename(item); e.Handled = true; }
        else if (e.Key == Key.Escape)  { Vm.CancelRename(item); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItemViewModel item && item.IsRenaming)
            Vm.CommitRename(item);
    }

    // ── Window-level keyboard ──────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FeedbackOverlay.Visibility == Visibility.Visible)
        {
            FeedbackOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == Key.N && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
        {
            var count = Application.Current.Windows.OfType<MainWindow>().Count();
            if (count < 4)
                new MainWindow().Show();
            e.Handled = true;
        }
        else if (e.Key == Key.F && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0)
        {
            Vm.EnterSearchMode();
            e.Handled = true;
        }
    }

    // ── File list keyboard ─────────────────────────────────────────

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e) => HandleFileListKeyDown(e);
    private void FileListView_PreviewKeyDown(object sender, KeyEventArgs e) => HandleFileListKeyDown(e);

    private void HandleFileListKeyDown(KeyEventArgs e)
    {
        if (Vm.SelectedItem is { IsRenaming: true }) return;

        switch (e.Key)
        {
            case Key.Delete:
                Vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                Vm.RenameSelected();
                e.Handled = true;
                break;
            case Key.Enter:
                if (Vm.SelectedItem != null) Vm.Open(Vm.SelectedItem);
                e.Handled = true;
                break;
            case Key.Back:
                Vm.GoBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0:
                Vm.UndoDeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                // Grid view wraps tiles into rows via JustifiedWrapPanel, which plain
                // ListBox arrow-key handling treats as one long list (Up/Down move by a
                // single item, same as Left/Right). List view is already a single column,
                // so its default behavior is correct as-is.
                if (Vm.IsGridView)
                {
                    MoveGridSelection(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                }
                break;
            case Key.Space:
                if (Vm.SelectedItem is not null)
                    ((App)Application.Current).Preview.OpenOrClose(
                        Vm.SelectedItem.FullPath,
                        Vm.CurrentPath,
                        Vm.OrderedFilePaths.ToList(),
                        Vm.IsGridView ? FindVisualChild<JustifiedWrapPanel>(FileListBox)?.Columns ?? 1 : 1);
                e.Handled = true;
                break;
        }
    }

    // Jumps a full row up/down in grid view by stepping the current on-screen index
    // by the panel's live column count, rather than by one item like Left/Right.
    private void MoveGridSelection(int rowDelta)
    {
        var items = Vm.OrderedFiles;
        if (items.Count == 0) return;

        int columns = FindVisualChild<JustifiedWrapPanel>(FileListBox)?.Columns ?? 1;
        int currentIndex = Vm.SelectedItem != null && items is IList<FileItemViewModel> list
            ? list.IndexOf(Vm.SelectedItem)
            : -1;

        int newIndex = currentIndex < 0
            ? 0
            : Math.Clamp(currentIndex + rowDelta * columns, 0, items.Count - 1);

        var target = items[newIndex];
        FileListBox.SelectedItem = target;
        FileListBox.ScrollIntoView(target);

        // Setting SelectedItem alone doesn't move keyboard focus off the previously
        // focused tile — leaving it there makes the *next* arrow key (e.g. Left/Right)
        // navigate relative to the stale focus instead of the newly selected tile.
        if (FileListBox.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement container)
            container.Focus();
    }

    // Keeps the main window's selection highlight in sync as the preview window
    // navigates (arrow keys, delete/undo). No-ops if the file isn't in the
    // currently visible collection, e.g. when the preview was opened from Explorer.
    private void Preview_CurrentFileChanged(string path)
    {
        if (Vm.IsColumnView)
        {
            foreach (var col in Vm.Columns)
            {
                var match = col.Items.FirstOrDefault(i =>
                    string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                // Setting SelectedItem on a directory would otherwise trigger
                // ColumnPanel_SelectionChanged's auto-drill into that folder.
                _suppressColumnAutoOpen = true;
                col.SelectedItem = match;
                _suppressColumnAutoOpen = false;
                return;
            }
        }
        else
        {
            var match = Vm.Files.FirstOrDefault(i =>
                string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;

            if (Vm.IsListView)
            {
                FileListView.SelectedItem = match;
                FileListView.ScrollIntoView(match);
            }
            else
            {
                FileListBox.SelectedItem = match;
                FileListBox.ScrollIntoView(match);
            }
        }
    }

    // ── Type-ahead (grid/list/column) ───────────────────────────────
    // Shared buffer/timeout: only one list can have keyboard focus at a time.
    private string   _typeAheadBuffer = "";
    private DateTime _typeAheadLastKeyTime;
    private static readonly TimeSpan TypeAheadTimeout = TimeSpan.FromSeconds(1);

    private void FileList_PreviewTextInput(object sender, TextCompositionEventArgs e)     => HandleFileListTextInput(e);
    private void FileListView_PreviewTextInput(object sender, TextCompositionEventArgs e) => HandleFileListTextInput(e);

    private void HandleFileListTextInput(TextCompositionEventArgs e)
    {
        if (Vm.SelectedItem is { IsRenaming: true } || string.IsNullOrEmpty(e.Text)) return;

        var match = TypeAheadMatch.Find(Vm.OrderedFiles, AdvanceTypeAheadBuffer(e.Text), Vm.SelectedItem);
        if (match == null) return;

        Vm.SelectedItem = match;
        if (Vm.IsGridView) FileListBox.ScrollIntoView(match);
        else if (Vm.IsListView) FileListView.ScrollIntoView(match);
        e.Handled = true;
    }

    private void ColumnListBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnViewModel col) return;
        if (col.SelectedItem is { IsRenaming: true } || string.IsNullOrEmpty(e.Text)) return;

        var match = TypeAheadMatch.Find(col.Items, AdvanceTypeAheadBuffer(e.Text), col.SelectedItem);
        if (match == null) return;

        col.SelectedItem = match;
        lb.ScrollIntoView(match);
        e.Handled = true;
    }

    private string AdvanceTypeAheadBuffer(string text)
    {
        var now = DateTime.UtcNow;
        _typeAheadBuffer = now - _typeAheadLastKeyTime > TypeAheadTimeout ? text : _typeAheadBuffer + text;
        _typeAheadLastKeyTime = now;
        return _typeAheadBuffer;
    }

    // ── File drag-out ─────────────────────────────────────────────

    private Point _dragStartPoint;
    private FileItemViewModel? _dragCandidate;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private void FileListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragCandidate  = GetFileItemFromSource(e.OriginalSource as DependencyObject);
    }

    private void FileListBox_PreviewMouseMove(object sender, MouseEventArgs e) =>
        HandleDragMouseMove(FileListBox, Vm.SelectedItems, e);

    private void FileListView_PreviewMouseMove(object sender, MouseEventArgs e) =>
        HandleDragMouseMove(FileListView, Vm.SelectedItems, e);

    private void ColumnListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragCandidate  = GetFileItemFromSource(e.OriginalSource as DependencyObject);
    }

    private void ColumnListBox_PreviewMouseMove(object sender, MouseEventArgs e) =>
        HandleDragMouseMove((UIElement)sender, null, e);

    private void HandleDragMouseMove(UIElement source, IEnumerable<FileItemViewModel>? selection, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        if (_dragCandidate.IsRenaming) return;

        var pos  = e.GetPosition(null);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var candidate = _dragCandidate;
        _dragCandidate = null;

        var paths = selection != null && selection.Contains(candidate)
            ? selection.Select(i => i.FullPath).ToArray()
            : new[] { candidate.FullPath };

        PerformDragDrop(source, paths);
    }

    private void PerformDragDrop(UIElement source, string[] paths)
    {
        var (labelWindow, labelText) = CreateDragLabel();
        labelWindow.Show();

        double dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        void OnGiveFeedback(object s, GiveFeedbackEventArgs fe)
        {
            fe.UseDefaultCursors = false;
            fe.Handled = true;
            labelText.Text = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? "Copy" : "Move";
            GetCursorPos(out var pt);
            labelWindow.Left = pt.X / dpi + 16;
            labelWindow.Top  = pt.Y / dpi + 16;
        }

        source.GiveFeedback += OnGiveFeedback;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            source.GiveFeedback -= OnGiveFeedback;
            labelWindow.Close();
        }
    }

    private static (Window window, TextBlock text) CreateDragLabel()
    {
        var tb = new TextBlock
        {
            Text       = "Move",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Padding    = new Thickness(8, 4, 8, 4),
        };

        var win = new Window
        {
            Content            = new Border
            {
                Background   = new SolidColorBrush(Color.FromArgb(220, 22, 22, 22)),
                CornerRadius = new CornerRadius(4),
                Child        = tb,
            },
            WindowStyle        = WindowStyle.None,
            AllowsTransparency = true,
            Background         = Brushes.Transparent,
            IsHitTestVisible   = false,
            ShowInTaskbar      = false,
            Topmost            = true,
            SizeToContent      = SizeToContent.WidthAndHeight,
            Left               = -9999,
            Top                = -9999,
        };

        return (win, tb);
    }

    private static FileItemViewModel? GetFileItemFromSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement fe && fe.DataContext is FileItemViewModel vm) return vm;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T typed) return typed;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    // ── Drop target — receive files dragged from Explorer or other Taste windows ──

    private FileItemViewModel? _activeDropTarget;

    private void SetDropTarget(FileItemViewModel? target)
    {
        if (target == _activeDropTarget) return;
        if (_activeDropTarget != null) _activeDropTarget.IsDropTarget = false;
        _activeDropTarget = target;
        if (_activeDropTarget != null) _activeDropTarget.IsDropTarget = true;
    }

    private void HandleDragOver(DragEventArgs e, string fallbackFolder)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var hovered = GetFileItemFromSource(e.OriginalSource as DependencyObject);
        SetDropTarget(hovered?.IsDirectory == true ? hovered : null);

        string destFolder = _activeDropTarget?.FullPath ?? fallbackFolder;
        bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        bool allSameFolder = files.All(f => string.Equals(
            Path.GetDirectoryName(f), destFolder, StringComparison.OrdinalIgnoreCase));

        e.Effects = allSameFolder ? DragDropEffects.None
                  : isCopy        ? DragDropEffects.Copy
                                  : DragDropEffects.Move;
        e.Handled = true;
    }

    private void HandleDragLeave() => SetDropTarget(null);

    private void ExecuteFileDrop(string destFolder, string[] paths, bool isCopy)
    {
        foreach (var src in paths)
        {
            string dest = Path.Combine(destFolder, Path.GetFileName(src));
            if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (isCopy)
                {
                    if (Directory.Exists(src)) CopyDirectory(src, dest);
                    else File.Copy(src, dest, overwrite: false);
                    Vm.NotifyFileAdded(dest);
                }
                else
                {
                    if (Directory.Exists(src)) Directory.Move(src, dest);
                    else File.Move(src, dest, overwrite: false);
                    Vm.NotifyFileMoved(src, dest);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Taste", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private void FileListBox_DragOver(object sender, DragEventArgs e)  => HandleDragOver(e, Vm.CurrentPath);
    private void FileListBox_DragLeave(object sender, DragEventArgs e) => HandleDragLeave();
    private void FileListBox_Drop(object sender, DragEventArgs e)
    {
        var dir = _activeDropTarget;
        SetDropTarget(null);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is not { Length: > 0 }) return;
        ExecuteFileDrop(dir?.FullPath ?? Vm.CurrentPath, files, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
    }

    private void FileListView_DragOver(object sender, DragEventArgs e)  => HandleDragOver(e, Vm.CurrentPath);
    private void FileListView_DragLeave(object sender, DragEventArgs e) => HandleDragLeave();
    private void FileListView_Drop(object sender, DragEventArgs e)      => FileListBox_Drop(sender, e);

    private void ColumnListBox_DragOver(object sender, DragEventArgs e)
    {
        string fallback = ((sender as FrameworkElement)?.DataContext as ColumnViewModel)?.Path ?? Vm.CurrentPath;
        HandleDragOver(e, fallback);
    }
    private void ColumnListBox_DragLeave(object sender, DragEventArgs e) => HandleDragLeave();
    private void ColumnListBox_Drop(object sender, DragEventArgs e)
    {
        var dir = _activeDropTarget;
        SetDropTarget(null);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files is not { Length: > 0 }) return;

        var col      = (sender as FrameworkElement)?.DataContext as ColumnViewModel;
        string fallback   = col?.Path ?? Vm.CurrentPath;
        string destFolder = dir?.FullPath ?? fallback;
        bool alreadyOpen  = dir == null ||
            Vm.Columns.Any(c => string.Equals(c.Path, destFolder, StringComparison.OrdinalIgnoreCase));

        ExecuteFileDrop(destFolder, files, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);

        // Dropping onto a folder that isn't already expanded into its own column: open it
        // (same as drilling in via click/Right-arrow) so the moved-in file becomes visible.
        if (dir != null && !alreadyOpen && col != null)
        {
            Vm.ColumnFolderOpened(col, dir);
            _ = FocusColumnAfterOpen(col);
        }
    }

    // ── Mouse wheel — forward to outer ScrollViewer ────────────────
    // The ListBox consumes wheel events internally even with its scroll disabled;
    // re-raise them on the outer ScrollViewer so scrolling actually works.

    private void FileListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        MainScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source      = sender,
        });
    }

    // ── Selection changed — scroll selected item into view ─────────
    // This makes arrow key navigation work: ListBox moves selection,
    // then we tell the outer ScrollViewer to reveal it.

    private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Sync full selection to VM
        Vm.SelectedItem = FileListBox.SelectedItem as FileItemViewModel;
        Vm.SelectedItems.Clear();
        foreach (FileItemViewModel item in FileListBox.SelectedItems)
            Vm.SelectedItems.Add(item);
        Vm.UpdateStatus();

        // Scroll the focused item into view
        if (Vm.SelectedItem is { } focused)
        {
            var container = FileListBox.ItemContainerGenerator.ContainerFromItem(focused) as FrameworkElement;
            container?.BringIntoView();
        }

        DoUpdateViewport();
    }

    // ── Mouse double-click ─────────────────────────────────────────

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock) return; // label double-click → rename, not open
        if (Vm.SelectedItem != null) Vm.Open(Vm.SelectedItem);
    }

    private void FileNameLabel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FileItemViewModel item) return;

        if (e.ClickCount == 1 && Vm.SelectedItem == item)
        {
            // Second deliberate click on an already-selected item's label → rename
            Vm.RenameSelected();
            e.Handled = true;
        }
        else if (e.ClickCount == 2)
        {
            // Fast double-click → rename regardless of prior selection
            Vm.SelectedItem = item;
            Vm.RenameSelected();
            e.Handled = true;
        }
    }

    // ── Icon dropdown buttons ──────────────────────────────────────

    private void MoreBtn_Click(object sender, RoutedEventArgs e) => OpenContextMenu(MoreBtn);

    private void SearchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.IsSearching) Vm.ExitSearchMode();
        else Vm.EnterSearchMode();
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => Vm.ExitSearchMode();

    private void SearchBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && (bool)e.NewValue)
            tb.Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SearchBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm.ExitSearchMode();
            FileListBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchResultsList.Items.Count > 0)
        {
            SearchResultsList.SelectedIndex = 0;
            SearchResultsList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // No results yet — most likely the debounce just hasn't fired. Run now instead
            // of making Enter feel like it did nothing.
            Vm.RunSearchNow();
            e.Handled = true;
        }
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not FileItemViewModel item) return;
        string? parent = Path.GetDirectoryName(item.FullPath);
        if (parent == null) return;
        Vm.ExitSearchMode();
        Vm.Navigate(parent);
    }

    private void ViewGrid_Click(object sender, RoutedEventArgs e)
    {
        Vm.ViewMode = ViewMode.Grid;
    }
    private void ViewGridOptions_Click(object sender, RoutedEventArgs e)
    {
        Vm.ViewMode = ViewMode.Grid;
        SizePopup.IsOpen = !SizePopup.IsOpen;
    }
    private void ViewList_Click(object sender, RoutedEventArgs e)
    {
        Vm.ViewMode = ViewMode.List;
        UpdateListColumns();
        Vm.RequestListViewThumbnails();
        Dispatcher.BeginInvoke(EnsureListViewScrollHook, DispatcherPriority.Loaded);
    }

    // ── List view scroll — load more when nearing the bottom ────────

    private ScrollViewer? _listScrollViewer;

    private void EnsureListViewScrollHook()
    {
        if (_listScrollViewer != null) return;
        _listScrollViewer = FindVisualChild<ScrollViewer>(FileListView);
        if (_listScrollViewer != null)
            _listScrollViewer.ScrollChanged += ListScrollViewer_ScrollChanged;
    }

    private void ListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ViewportHeight)
            Vm.LoadMoreIfNeeded();
    }
    private void ViewColumn_Click(object sender, RoutedEventArgs e)
    {
        Vm.ViewMode = ViewMode.Column;
        Vm.InitColumns();
    }

    // ── List view ──────────────────────────────────────────────────

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView lv) return;
        Vm.SelectedItem = lv.SelectedItem as FileItemViewModel;
        Vm.SelectedItems.Clear();
        foreach (FileItemViewModel item in lv.SelectedItems)
            Vm.SelectedItems.Add(item);
        Vm.UpdateStatus();
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedItem != null) Vm.Open(Vm.SelectedItem);
    }

    private void FileListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv) return;
        var item = GetFileItemFromSource(e.OriginalSource as DependencyObject);
        if (item != null && !Vm.SelectedItems.Contains(item))
            lv.SelectedItem = item;
    }

    // Right-click on empty space (no item, no column header) shows the same menu as the
    // overflow (…) button instead of the per-item Cut/Copy/Paste menu set on the ListView.
    private void FileListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<GridViewColumnHeader>(source) != null) return;
        if (GetFileItemFromSource(source) != null) return;

        e.Handled = true;
        ShowOverflowMenu(FileListView);
    }

    private void ListHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader h && h.Tag is string key)
            Vm.SortByColumn(key);
    }

    private void ColToggle_Click(object sender, RoutedEventArgs e) => UpdateListColumns();

    private void UpdateListColumns()
    {
        var cols = ListViewGrid.Columns;

        void Ensure(GridViewColumn col, bool show, int insertAfter)
        {
            bool has = cols.Contains(col);
            if (show && !has) cols.Insert(Math.Min(insertAfter, cols.Count), col);
            else if (!show && has) cols.Remove(col);
        }

        // Maintain visual order: Name(0), Date(1), Type(2), Size(3), Path(4)
        Ensure(DateColumn, Vm.ShowDateColumn, 1);
        Ensure(TypeColumn, Vm.ShowTypeColumn, cols.Contains(DateColumn) ? 2 : 1);
        Ensure(SizeColumn, Vm.ShowSizeColumn, cols.IndexOf(TypeColumn) is >= 0 and var ti ? ti + 1 : cols.Count);
        Ensure(PathColumn, Vm.ShowPathColumn, cols.Count);
    }

    // ── Column view ────────────────────────────────────────────────

    private void ColumnPanel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnViewModel col) return;
        if (lb.SelectedItem is not FileItemViewModel item) return;

        if (_suppressColumnAutoOpen) { _suppressColumnAutoOpen = false; return; }

        // Selecting a folder exposes its contents in the next column, but doesn't select
        // anything there yet — the source column stays the active (primary) selection until
        // the user actually picks something in the new column. Explicit drill-in (Right
        // arrow, below) is the only thing that auto-selects into the next column.
        if (item.IsDirectory)
        {
            Vm.ColumnFolderOpened(col, item);
            _ = ScrollNextColumnIntoView(col);
        }
    }

    // Brings the newly-appended next column into view without selecting anything in it or
    // moving keyboard focus away from the column the user is currently in.
    private async Task ScrollNextColumnIntoView(ColumnViewModel source)
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);

        int idx = Vm.Columns.IndexOf(source);
        if (idx < 0 || idx + 1 >= Vm.Columns.Count) return;
        var target = Vm.Columns[idx + 1];

        for (int i = 0; i < 200 && target.IsLoading; i++)
            await Task.Delay(10);
        if (idx + 1 >= Vm.Columns.Count || Vm.Columns[idx + 1] != target) return;

        if (ColumnsItemsControl.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement container)
            container.BringIntoView();
    }

    private void ColumnListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnViewModel col) return;
        if (col.SelectedItem is not { IsDirectory: false } item) return;

        Vm.Open(item);
    }

    // Right-click on empty space in a column panel shows a "New Folder" menu scoped to that
    // panel's own path (column view has no single "current folder" the way grid/list view do).
    private ColumnViewModel? _emptyMenuColumn;

    private void ColumnListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnViewModel col) return;
        if (GetFileItemFromSource(e.OriginalSource as DependencyObject) != null) return;

        e.Handled = true;
        _emptyMenuColumn = col;
        var menu = (ContextMenu)FindResource("ColumnEmptyMenu");
        menu.PlacementTarget = lb;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void ColumnEmptyNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_emptyMenuColumn != null) Vm.CreateNewFolderInColumn(_emptyMenuColumn);
    }

    // Only used to keep the Left-key "select matching folder in the previous column" case
    // from re-triggering ColumnFolderOpened (that column is already open — it's where we came from).
    private bool _suppressColumnAutoOpen;

    private void ColumnListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not ColumnViewModel col) return;
        if (col.SelectedItem is { IsRenaming: true }) return;

        switch (e.Key)
        {
            case Key.Right:
                if (col.SelectedItem is { IsDirectory: true } dirItem)
                {
                    Vm.ColumnFolderOpened(col, dirItem);
                    _ = FocusColumnAfterOpen(col);
                }
                e.Handled = true;
                break;

            case Key.Left:
                int idx = Vm.Columns.IndexOf(col);
                if (idx > 0)
                {
                    var prev = Vm.Columns[idx - 1];
                    if (prev.SelectedItem == null)
                    {
                        var match = prev.Items.FirstOrDefault(i => i.IsDirectory && i.FullPath == col.Path);
                        if (match != null)
                        {
                            _suppressColumnAutoOpen = true;
                            prev.SelectedItem = match;
                            _suppressColumnAutoOpen = false;
                        }
                    }
                    FocusColumn(prev);
                }
                else
                    Vm.GoUpCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                if (col.SelectedItem is { IsDirectory: false } enterItem)
                    Vm.Open(enterItem);
                e.Handled = true;
                break;

            case Key.Space:
                if (col.SelectedItem is { } spaceItem)
                    ((App)Application.Current).Preview.OpenOrClose(
                        spaceItem.FullPath,
                        col.Path,
                        col.Items.Select(i => i.FullPath).ToList());
                e.Handled = true;
                break;

            case Key.Delete:
                Vm.DeleteSelectedInColumn(col);
                e.Handled = true;
                break;
        }
    }

    private async Task FocusColumnAfterOpen(ColumnViewModel source)
    {
        // Let the ItemsControl generate a container for the newly-added column.
        await Dispatcher.Yield(DispatcherPriority.Loaded);

        int idx = Vm.Columns.IndexOf(source);
        if (idx < 0 || idx + 1 >= Vm.Columns.Count) return;
        var target = Vm.Columns[idx + 1];

        for (int i = 0; i < 200 && target.IsLoading; i++)
            await Task.Delay(10);
        if (idx + 1 >= Vm.Columns.Count || Vm.Columns[idx + 1] != target) return;

        if (target.SelectedItem == null && target.Items.Count > 0)
        {
            _suppressColumnAutoOpen = true;
            target.SelectedItem = target.Items[0];
            _suppressColumnAutoOpen = false;
        }

        FocusColumn(target);
    }

    private void FocusColumn(ColumnViewModel col)
    {
        var lb = FindColumnListBox(col);
        if (lb == null) return;

        lb.Focus();
        if (col.SelectedItem != null &&
            lb.ItemContainerGenerator.ContainerFromItem(col.SelectedItem) is ListBoxItem lbi)
            lbi.Focus();
    }

    private ListBox? FindColumnListBox(ColumnViewModel col)
    {
        if (ColumnsItemsControl.ItemContainerGenerator.ContainerFromItem(col) is not DependencyObject container)
            return null;
        return FindVisualChild<ListBox>(container);
    }

    private static void OpenContextMenu(Button btn)
    {
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement       = PlacementMode.Bottom;
        btn.ContextMenu.IsOpen          = true;
    }

    // Reuses the overflow (…) button's own ContextMenu instance so right-click-on-empty-space
    // always shows exactly the same menu (and stays in sync if that menu ever grows more items).
    private void ShowOverflowMenu(UIElement placementTarget)
    {
        var menu = MoreBtn.ContextMenu;
        menu.PlacementTarget = placementTarget;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked) return;
        foreach (MenuItem item in new[] { SortName, SortDate, SortType, SortSize })
            item.IsChecked = item == clicked;
        Vm.SelectedSort = clicked.Header.ToString()!;
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked) return;
        foreach (MenuItem item in new[] { SortAscending, SortDescending })
            item.IsChecked = item == clicked;
        Vm.SelectedSortDirection = clicked.Header.ToString()!;
    }

    private void ExplorerSpacebar_Click(object sender, RoutedEventArgs e)
    {
        var watcher = ((App)Application.Current).Watcher;
        watcher.ExplorerSpacebarEnabled = ExplorerSpacebarItem.IsChecked;
    }

    // ── Context menu ───────────────────────────────────────────────

    // Right-click should select the item under the cursor (if not already selected).
    private void FileListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = GetFileItemFromSource(e.OriginalSource as DependencyObject);
        if (item != null && !Vm.SelectedItems.Contains(item))
            FileListBox.SelectedItem = item;
    }

    // Right-click on empty space (no item under the cursor) shows the same menu as the
    // overflow (…) button instead of the per-item Cut/Copy/Paste menu set on the ListBox.
    private void FileListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (GetFileItemFromSource(e.OriginalSource as DependencyObject) != null) return;

        e.Handled = true;
        ShowOverflowMenu(FileListBox);
    }

    private string[] GetSelectedPaths() =>
        Vm.SelectedItems.Count > 0
            ? Vm.SelectedItems.Select(i => i.FullPath).ToArray()
            : Vm.SelectedItem != null ? new[] { Vm.SelectedItem.FullPath } : Array.Empty<string>();

    private static StringCollection ToStringCollection(string[] paths)
    {
        var sc = new StringCollection();
        sc.AddRange(paths);
        return sc;
    }

    private void ContextCut_Click(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedPaths();
        if (paths.Length == 0) return;
        try
        {
            var data = new DataObject();
            data.SetFileDropList(ToStringCollection(paths));
            data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 2, 0, 0, 0 }));
            Clipboard.SetDataObject(data, true);
        }
        catch { }
    }

    private void ContextCopy_Click(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedPaths();
        if (paths.Length == 0) return;
        try { Clipboard.SetFileDropList(ToStringCollection(paths)); }
        catch { }
    }

    private void ContextPaste_Click(object sender, RoutedEventArgs e)
    {
        var list = Clipboard.GetFileDropList();
        if (list == null || list.Count == 0) return;

        bool isMove = false;
        try
        {
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
                isMove = BitConverter.ToInt32(ms.ToArray(), 0) == 2;
        }
        catch { }

        var paths = list.Cast<string>().Where(s => s != null).ToArray();
        ExecuteFileDrop(Vm.CurrentPath, paths, isCopy: !isMove);
        Vm.RefreshCommand.Execute(null);
    }

    private void ContextRename_Click(object sender, RoutedEventArgs e) => Vm.RenameSelected();

    private void ContextDelete_Click(object sender, RoutedEventArgs e) =>
        Vm.DeleteSelectedCommand.Execute(null);

    private void ContextProperties_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedItem == null) return;
        ShellInterop.ShowProperties(Vm.OwnerHwnd, Vm.SelectedItem.FullPath);
    }

    private void ContextCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedPaths();
        if (paths.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch { }
    }

    private const string FeedbackRepoUrl = "https://github.com/andresalyer/taste-viewer";
    private const int FeedbackMaxLength = 1000;
    private const int FeedbackMinLength = 10;

    private void Feedback_Click(object sender, RoutedEventArgs e)
    {
        FeedbackTypeBug.IsChecked = false;
        FeedbackTypeFeature.IsChecked = false;
        FeedbackTypeSuggestion.IsChecked = false;
        FeedbackText.Text = string.Empty;
        FeedbackText.IsEnabled = false;
        FeedbackSubmitBtn.IsEnabled = false;
        FeedbackCharCount.Text = $"0 / {FeedbackMaxLength}";
        FeedbackOverlay.Visibility = Visibility.Visible;
    }

    private void CloseFeedback_Click(object sender, RoutedEventArgs e) =>
        FeedbackOverlay.Visibility = Visibility.Collapsed;

    private void FeedbackType_Checked(object sender, RoutedEventArgs e)
    {
        FeedbackText.IsEnabled = true;
        FeedbackText.Focus();
        UpdateFeedbackSubmitState();
    }

    private void FeedbackText_TextChanged(object sender, TextChangedEventArgs e)
    {
        FeedbackCharCount.Text = $"{FeedbackText.Text.Length} / {FeedbackMaxLength}";
        UpdateFeedbackSubmitState();
    }

    private void UpdateFeedbackSubmitState() =>
        FeedbackSubmitBtn.IsEnabled = FeedbackText.IsEnabled && FeedbackText.Text.Trim().Length >= FeedbackMinLength;

    private void FeedbackSubmit_Click(object sender, RoutedEventArgs e)
    {
        var template = FeedbackTypeBug.IsChecked == true ? "bug_report.yml"
                      : FeedbackTypeFeature.IsChecked == true ? "feature_request.yml"
                      : "suggestion.yml";

        var description = Uri.EscapeDataString(FeedbackText.Text.Trim());
        var url = $"{FeedbackRepoUrl}/issues/new?template={template}&description={description}";

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }

        FeedbackOverlay.Visibility = Visibility.Collapsed;
    }
}
