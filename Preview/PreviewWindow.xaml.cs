using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Taste.Interop;
using Taste.Shared;

namespace Taste.Preview;

public partial class PreviewWindow : Window
{
    static readonly double[] SpeedSteps = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    private List<string> _files = new();
    private int          _index = -1;

    // Zoom
    private double _naturalW, _naturalH;
    private double _scale    = 1.0;
    private double _fitScale = 1.0;
    private bool   _fitMode  = true;

    // Video
    private int  _speedIndex = 3;
    private bool _isPlaying  = false;

    // GIF animation
    private GifFrameInfo[]   _gifFrames = [];
    private int              _gifFrame  = 0;
    private DispatcherTimer? _gifTimer  = null;
    private WriteableBitmap? _gifCanvas = null;
    private byte[]?          _gifSave   = null;
    private int              _gifW, _gifH;

    // HEIC async loading
    private static readonly char[] SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏".ToCharArray();
    private DispatcherTimer?         _spinnerTimer = null;
    private int                      _spinnerFrame = 0;
    private string                   _baseTitle    = "";
    private CancellationTokenSource? _heicCts      = null;

    // Delete / undo
    private record struct DeletedEntry(string Path, string RFile, string IFile, int Index);
    private readonly List<DeletedEntry> _undoStack = new();
    private DispatcherTimer? _undoTimer = null;

    // Scrubber
    private DispatcherTimer? _posTimer;
    private DispatcherTimer? _volumeTimer;
    private DispatcherTimer? _scrubberHideTimer;
    private double           _videoDuration;
    private bool             _scrubberDragging;
    private bool             _wasPlayingBeforeScrub;

    // Pan
    private static Cursor?    _panCursor  = null;
    private bool              _isPanning  = false;
    private System.Windows.Point _panStart;
    private double            _panOriginH = 0;
    private double            _panOriginV = 0;

    // Fullscreen
    private bool        _isFullscreen;
    private WindowState _savedWindowState;
    private WindowStyle _savedWindowStyle;
    private ResizeMode  _savedResizeMode;

    public PreviewWindow()
    {
        InitializeComponent();
        VolumeSlider.Value = 1.0;
        VideoView.Volume   = 1.0;
        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _volumeTimer.Tick += (s, e) => { _volumeTimer.Stop(); HideVolumePopup(); };
        _scrubberHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _scrubberHideTimer.Tick += (s, e) => { _scrubberHideTimer.Stop(); ApplyScrubberState(false); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    // ── Open / Close ─────────────────────────────────────────────────────────

    public void OpenOrClose(string selectedPath, string folderPath, List<string> allFiles)
    {
        if (IsVisible && string.Equals(
                _files.Count > _index && _index >= 0 ? _files[_index] : null,
                selectedPath, StringComparison.OrdinalIgnoreCase))
        {
            Hide();
            return;
        }

        _files = allFiles;
        _index = allFiles.FindIndex(f => string.Equals(f, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (_index < 0) _index = 0;

        Topmost = true;
        Show();
        Activate();
        Focus();
        Topmost = false;
        LoadCurrent();
    }

    void LoadCurrent()
    {
        if (_files.Count == 0) return;
        LoadFile(_files[_index]);
    }

    void LoadFile(string path)
    {
        _heicCts?.Cancel();
        _heicCts = null;
        StopSpinner();

        EmptyState.Visibility  = Visibility.Collapsed;
        ContentGrid.Visibility = Visibility.Visible;
        Title = $"Taste — {Path.GetFileName(path)}";
        LoadingDim.Visibility  = Visibility.Visible;

        _naturalW = 0;
        _naturalH = 0;

        ImageView.Source = null;
        GenericView.Visibility = Visibility.Collapsed;
        StopGifAnimation();
        var ext = Path.GetExtension(path);
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            VideoView.Stop();
            VideoView.Source     = null;
            VideoView.Visibility = Visibility.Collapsed;
            ShowVideoControls(false);
            Panel.SetZIndex(ImageView, 1);
            Panel.SetZIndex(VideoView, 0);
            try { StartGifAnimation(path); }
            catch { LoadingDim.Visibility = Visibility.Collapsed; }
        }
        else if (MediaExtensions.Video.Contains(ext))
        {
            Panel.SetZIndex(ImageView, 1);
            Panel.SetZIndex(VideoView, 0);
            VideoView.Visibility = Visibility.Visible;
            VideoView.Stop();
            VideoView.Source = new Uri(path);
            _isPlaying = true;
            SetSpeed(3);
            VideoView.Play();
            UpdatePlayPauseButton();
            ShowVideoControls(true);
        }
        else if (MediaExtensions.Image.Contains(ext))
        {
            VideoView.Stop();
            VideoView.Source     = null;
            VideoView.Visibility = Visibility.Collapsed;
            ShowVideoControls(false);
            Panel.SetZIndex(ImageView, 1);
            Panel.SetZIndex(VideoView, 0);
            ImageView.Visibility = Visibility.Visible;

            bool isHeic = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                          ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
            if (isHeic)
            {
                var thumb = ShellInterop.GetShellThumbnail(path, 512);
                if (thumb != null)
                {
                    ImageView.Source   = thumb;
                    _naturalW = thumb.PixelWidth;
                    _naturalH = thumb.PixelHeight;
                    ContentGrid.Width  = _naturalW;
                    ContentGrid.Height = _naturalH;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                    {
                        SetFitZoom();
                        LoadingDim.Visibility = Visibility.Collapsed;
                    }));
                }
                StartSpinner(Title);
                _heicCts = new CancellationTokenSource();
                LoadHeicAsync(path, _heicCts.Token);
            }
            else
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = stream;
                    bmp.CacheOption  = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    ImageView.Source = bmp;

                    _naturalW = bmp.PixelWidth;
                    _naturalH = bmp.PixelHeight;
                    ContentGrid.Width  = _naturalW;
                    ContentGrid.Height = _naturalH;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                    {
                        SetFitZoom();
                        LoadingDim.Visibility = Visibility.Collapsed;
                    }));
                }
                catch
                {
                    ImageView.Source      = null;
                    LoadingDim.Visibility = Visibility.Collapsed;
                }
            }
        }
        else
        {
            LoadGenericFile(path);
        }
    }

    void LoadGenericFile(string path)
    {
        VideoView.Stop();
        VideoView.Source     = null;
        VideoView.Visibility = Visibility.Collapsed;
        ImageView.Visibility = Visibility.Collapsed;
        ShowVideoControls(false);

        GenericFileName.Text   = Path.GetFileName(path);
        GenericIcon.Source     = ShellInterop.GetShellIcon(path, 256);
        GenericView.Visibility = Visibility.Visible;
        LoadingDim.Visibility  = Visibility.Collapsed;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    void Navigate(int delta)
    {
        if (_files.Count == 0) return;
        _index = (_index + delta + _files.Count) % _files.Count;
        LoadCurrent();
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────

    void SetFitZoom()
    {
        if (_naturalW <= 0 || _naturalH <= 0) return;
        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0) return;

        _fitScale = Math.Min(vw / _naturalW, vh / _naturalH);
        _scale    = _fitScale;
        _fitMode  = true;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _scale;
        ContentGrid.Margin = new Thickness(0);
        UpdateZoomLabel();
    }

    void ZoomBy(double factor, System.Windows.Point? mouseViewport)
    {
        if (_naturalW <= 0 || _naturalH <= 0) return;

        double newScale = Math.Clamp(_scale * factor, _fitScale * 0.25, _fitScale * 32);
        if (newScale == _scale) return;

        double vw = Scroller.ViewportWidth;
        double vh = Scroller.ViewportHeight;
        System.Windows.Point mp = mouseViewport ?? new System.Windows.Point(vw / 2, vh / 2);

        // Image left/top edge in viewport coordinates.
        // Fit mode: image is centered (possibly letterboxed); zoom mode: margin of vw/2 shifts content.
        double imgLeft = _fitMode ? (vw - _naturalW * _scale) / 2 : vw / 2 - Scroller.HorizontalOffset;
        double imgTop  = _fitMode ? (vh - _naturalH * _scale) / 2 : vh / 2 - Scroller.VerticalOffset;

        double natX = (mp.X - imgLeft) / _scale;
        double natY = (mp.Y - imgTop)  / _scale;

        _scale   = newScale;
        _fitMode = false;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _scale;
        UpdateZoomLabel();
        UpdatePanMargin();

        Scroller.ScrollToHorizontalOffset(vw / 2 + natX * _scale - mp.X);
        Scroller.ScrollToVerticalOffset  (vh / 2 + natY * _scale - mp.Y);
    }

    // Adds half-viewport padding around the content so the user can pan until
    // an image edge reaches the center of the screen (not just the viewport boundary).
    // Margin is in layout units (pre-scale); LayoutTransform scales it to screen pixels.
    void UpdatePanMargin()
    {
        if (_fitMode || _scale <= 0) { ContentGrid.Margin = new Thickness(0); return; }
        ContentGrid.Margin = new Thickness(
            Scroller.ViewportWidth  / 2,
            Scroller.ViewportHeight / 2,
            Scroller.ViewportWidth  / 2,
            Scroller.ViewportHeight / 2);
    }

    void UpdateZoomLabel()
    {
        ZoomLabel.Text = _fitScale > 0
            ? $"{_scale / _fitScale * 100:0}%"
            : "—";
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode) SetFitZoom();
        else UpdatePanMargin();
    }

    static Cursor GetPanCursor()
    {
        if (_panCursor != null) return _panCursor;

        using var bmp = new System.Drawing.Bitmap(48, 48, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            float cx = 23.5f, cy = 23.5f, shaft = 11f, head = 8f, hw = 6f;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var pen   = new System.Drawing.Pen(System.Drawing.Color.White, 1.5f);

            g.DrawLine(pen, cx, cy - shaft, cx, cy + shaft);
            g.DrawLine(pen, cx - shaft, cy, cx + shaft, cy);

            g.FillPolygon(brush, new[] { new System.Drawing.PointF(cx, cy - shaft - head), new System.Drawing.PointF(cx - hw, cy - shaft), new System.Drawing.PointF(cx + hw, cy - shaft) });
            g.FillPolygon(brush, new[] { new System.Drawing.PointF(cx, cy + shaft + head), new System.Drawing.PointF(cx - hw, cy + shaft), new System.Drawing.PointF(cx + hw, cy + shaft) });
            g.FillPolygon(brush, new[] { new System.Drawing.PointF(cx - shaft - head, cy), new System.Drawing.PointF(cx - shaft, cy - hw), new System.Drawing.PointF(cx - shaft, cy + hw) });
            g.FillPolygon(brush, new[] { new System.Drawing.PointF(cx + shaft + head, cy), new System.Drawing.PointF(cx + shaft, cy - hw), new System.Drawing.PointF(cx + shaft, cy + hw) });
        }

        var hIcon = bmp.GetHicon();
        Native.GetIconInfo(hIcon, out var info);
        info.fIcon    = false;
        info.xHotspot = 23;
        info.yHotspot = 23;
        var hCursor = Native.CreateIconIndirect(ref info);
        Native.DestroyIcon(hIcon);
        Native.DeleteObject(info.hbmMask);
        Native.DeleteObject(info.hbmColor);

        _panCursor = CursorInteropHelper.Create(new SafeFileHandle(hCursor, true));
        return _panCursor;
    }

    void Scroller_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SetFitZoom();
        e.Handled = true;
    }

    void Scroller_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_fitMode) return;
        _isPanning  = true;
        _panStart   = e.GetPosition(Scroller);
        _panOriginH = Scroller.HorizontalOffset;
        _panOriginV = Scroller.VerticalOffset;
        Scroller.CaptureMouse();
        Scroller.Cursor = GetPanCursor();
        e.Handled = true;
    }

    void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_panOriginH - (pos.X - _panStart.X));
        Scroller.ScrollToVerticalOffset  (_panOriginV - (pos.Y - _panStart.Y));
        e.Handled = true;
    }

    void Scroller_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        Scroller.ReleaseMouseCapture();
        Scroller.Cursor = null;
        e.Handled = true;
    }

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        double factor = ctrl ? 1.02 : 1.10;
        ZoomBy(e.Delta > 0 ? factor : 1.0 / factor, e.GetPosition(Scroller));
    }

    // ── Video ─────────────────────────────────────────────────────────────────

    void VideoView_MediaOpened(object sender, RoutedEventArgs e)
    {
        _naturalW = VideoView.NaturalVideoWidth  > 0 ? VideoView.NaturalVideoWidth  : 640;
        _naturalH = VideoView.NaturalVideoHeight > 0 ? VideoView.NaturalVideoHeight : 360;
        ContentGrid.Width  = _naturalW;
        ContentGrid.Height = _naturalH;
        Panel.SetZIndex(VideoView, 1);
        Panel.SetZIndex(ImageView, 0);
        ImageView.Visibility = Visibility.Collapsed;
        ImageView.Source     = null;
        SetFitZoom();
        LoadingDim.Visibility = Visibility.Collapsed;
        _videoDuration = VideoView.NaturalDuration.HasTimeSpan
            ? VideoView.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (ScrubberOverlay.Visibility == Visibility.Visible) { StopPosTimer(); StartPosTimer(); }
    }

    void VideoView_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoView.Position = TimeSpan.Zero;
        VideoView.Play();
    }

    // ── GIF animation ─────────────────────────────────────────────────────────

    void StartGifAnimation(string path)
    {
        _gifFrame = 0;
        _gifSave  = null;

        int logW = 0, logH = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hdr = new byte[10];
            if (fs.Read(hdr, 0, 10) == 10)
            {
                logW = hdr[6] | (hdr[7] << 8);
                logH = hdr[8] | (hdr[9] << 8);
            }
        }
        catch { }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        if (logW <= 0) logW = decoder.Frames[0].PixelWidth;
        if (logH <= 0) logH = decoder.Frames[0].PixelHeight;
        _gifW = logW;
        _gifH = logH;

        int count = decoder.Frames.Count;
        _gifFrames = new GifFrameInfo[count];
        for (int i = 0; i < count; i++)
            _gifFrames[i] = GifFrameInfo.Read(decoder.Frames[i]);

        _gifCanvas = new WriteableBitmap(logW, logH, 96, 96, PixelFormats.Bgra32, null);

        if (_gifFrames[0].Disposal == 3) GifSaveCanvas();
        GifBlitFrame(0);

        _naturalW = logW;
        _naturalH = logH;
        ContentGrid.Width    = logW;
        ContentGrid.Height   = logH;
        ImageView.Source     = _gifCanvas;
        ImageView.Visibility = Visibility.Visible;

        if (count > 1)
        {
            _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_gifFrames[0].Delay) };
            _gifTimer.Tick += GifTick;
            _gifTimer.Start();
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            SetFitZoom();
            LoadingDim.Visibility = Visibility.Collapsed;
        }));
    }

    void GifTick(object? sender, EventArgs e)
    {
        _gifTimer!.Stop();
        GifApplyDisposal(_gifFrame);
        _gifFrame = (_gifFrame + 1) % _gifFrames.Length;
        if (_gifFrames[_gifFrame].Disposal == 3) GifSaveCanvas();
        GifBlitFrame(_gifFrame);
        _gifTimer.Interval = TimeSpan.FromMilliseconds(_gifFrames[_gifFrame].Delay);
        _gifTimer.Start();
    }

    void GifBlitFrame(int index)
    {
        var f  = _gifFrames[index];
        int fw = f.Src.PixelWidth;
        int fh = f.Src.PixelHeight;
        int bw = Math.Min(fw, _gifW - f.Left);
        int bh = Math.Min(fh, _gifH - f.Top);
        if (bw <= 0 || bh <= 0) return;

        var conv = new FormatConvertedBitmap(f.Src, PixelFormats.Bgra32, null, 0);
        var src  = new byte[fw * fh * 4];
        conv.CopyPixels(src, fw * 4, 0);

        var dst = new byte[bw * bh * 4];
        _gifCanvas!.CopyPixels(new Int32Rect(f.Left, f.Top, bw, bh), dst, bw * 4, 0);

        for (int row = 0; row < bh; row++)
        {
            for (int col = 0; col < bw; col++)
            {
                int si = (row * fw + col) * 4;
                int di = (row * bw + col) * 4;
                byte a = src[si + 3];
                if (a == 255)
                {
                    dst[di] = src[si]; dst[di+1] = src[si+1]; dst[di+2] = src[si+2]; dst[di+3] = 255;
                }
                else if (a > 0)
                {
                    float fa = a / 255f, fb = 1f - fa;
                    dst[di]   = (byte)(src[si]   * fa + dst[di]   * fb);
                    dst[di+1] = (byte)(src[si+1] * fa + dst[di+1] * fb);
                    dst[di+2] = (byte)(src[si+2] * fa + dst[di+2] * fb);
                    dst[di+3] = (byte)(a          + dst[di+3]     * fb);
                }
            }
        }

        _gifCanvas!.WritePixels(new Int32Rect(f.Left, f.Top, bw, bh), dst, bw * 4, 0);
    }

    void GifApplyDisposal(int index)
    {
        var f  = _gifFrames[index];
        int fw = f.Src.PixelWidth;
        int fh = f.Src.PixelHeight;

        switch (f.Disposal)
        {
            case 2:
                int bw = Math.Min(fw, _gifW - f.Left);
                int bh = Math.Min(fh, _gifH - f.Top);
                if (bw > 0 && bh > 0)
                    _gifCanvas!.WritePixels(new Int32Rect(f.Left, f.Top, bw, bh), new byte[bw * bh * 4], bw * 4, 0);
                break;
            case 3:
                if (_gifSave != null)
                    _gifCanvas!.WritePixels(new Int32Rect(0, 0, _gifW, _gifH), _gifSave, _gifW * 4, 0);
                break;
        }
    }

    void GifSaveCanvas()
    {
        _gifSave = new byte[_gifW * _gifH * 4];
        _gifCanvas!.CopyPixels(_gifSave, _gifW * 4, 0);
    }

    void StopGifAnimation()
    {
        _gifTimer?.Stop();
        _gifTimer  = null;
        _gifFrames = [];
        _gifCanvas = null;
        _gifSave   = null;
    }

    // ── HEIC two-stage loading ────────────────────────────────────────────────

    async void LoadHeicAsync(string path, CancellationToken ct)
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var b = new BitmapImage();
                b.BeginInit();
                b.StreamSource = stream;
                b.CacheOption  = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return b;
            }, ct);

            if (ct.IsCancellationRequested) return;

            StopSpinner();
            ImageView.Source   = bmp;
            _naturalW = bmp.PixelWidth;
            _naturalH = bmp.PixelHeight;
            ContentGrid.Width  = _naturalW;
            ContentGrid.Height = _naturalH;
            if (_fitMode) SetFitZoom();
            LoadingDim.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!ct.IsCancellationRequested) StopSpinner();
        }
    }

    void StartSpinner(string baseTitle)
    {
        _baseTitle    = baseTitle;
        _spinnerFrame = 0;
        Title         = $"{baseTitle} {SpinnerFrames[0]}";
        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
            Title = $"{_baseTitle} {SpinnerFrames[_spinnerFrame]}";
        };
        _spinnerTimer.Start();
    }

    void StopSpinner()
    {
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        if (_baseTitle.Length > 0)
            Title = _baseTitle;
        _baseTitle = "";
    }

    // ── Playback controls ────────────────────────────────────────────────────

    void TogglePlayPause()
    {
        _isPlaying = !_isPlaying;
        if (_isPlaying) VideoView.Play(); else VideoView.Pause();
        UpdatePlayPauseButton();
    }

    void UpdatePlayPauseButton()
    {
        PlayIcon.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowVideoControls(bool show)
    {
        var v = show ? Visibility.Visible : Visibility.Collapsed;
        SpeedPill.Visibility = v;
        if (!show)
        {
            _scrubberHideTimer!.Stop();
            ScrubberOverlay.Visibility = Visibility.Collapsed;
            VolumePopup.IsOpen         = false;
            StopPosTimer();
        }
    }

    void ScrubberHoverZone_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (VideoView.Visibility != Visibility.Visible) return;
        _scrubberHideTimer!.Stop();
        ApplyScrubberState(true);
    }

    void ScrubberHoverZone_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_scrubberDragging) return;
        _scrubberHideTimer!.Start();
    }

    void ApplyScrubberState(bool show)
    {
        if (show)
        {
            ScrubberOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation(ScrubberOverlay.Opacity, 1, TimeSpan.FromMilliseconds(150));
            ScrubberOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
            StartPosTimer();
        }
        else
        {
            var anim = new DoubleAnimation(ScrubberOverlay.Opacity, 0, TimeSpan.FromMilliseconds(150));
            anim.Completed += (_, _) => ScrubberOverlay.Visibility = Visibility.Collapsed;
            ScrubberOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
            VolumePopup.IsOpen = false;
            StopPosTimer();
        }
    }

    void VolumePanel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => ShowVolumePopup();

    void VolumePanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => _volumeTimer!.Start();

    void VolumePopupContent_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => _volumeTimer!.Stop();

    void VolumePopupContent_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => _volumeTimer!.Start();

    void ShowVolumePopup()
    {
        _volumeTimer!.Stop();
        VolumePopup.IsOpen = true;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        VolumePopupBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void HideVolumePopup()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) => VolumePopup.IsOpen = false;
        VolumePopupBorder.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoView == null) return;
        VideoView.Volume = e.NewValue;
        bool silent = e.NewValue == 0;
        VideoView.IsMuted = silent;
        MuteBtn.IsChecked = silent;
    }

    void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        bool muting = MuteBtn.IsChecked == true;
        VideoView.IsMuted = muting;
        if (!muting && VolumeSlider.Value == 0)
            VolumeSlider.Value = 0.5;
    }

    void StartPosTimer()
    {
        if (_posTimer != null) return;
        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _posTimer.Tick += PosTimer_Tick;
        _posTimer.Start();
    }

    void StopPosTimer()
    {
        _posTimer?.Stop();
        _posTimer = null;
    }

    void PosTimer_Tick(object? sender, EventArgs e)
    {
        if (_videoDuration <= 0 || _scrubberDragging) return;
        double ratio = VideoView.Position.TotalSeconds / _videoDuration;
        UpdateScrubberVisual(ratio);
        UpdateScrubberTime();
    }

    void UpdateScrubberVisual(double ratio)
    {
        double w     = ScrubberTrack.ActualWidth;
        double x     = w * ratio;
        ScrubberFill.Width = x;
        ThumbTranslate.X   = Math.Clamp(x, 0, Math.Max(0, w - 6));
    }

    void UpdateScrubberTime() =>
        ScrubberTime.Text = $"{FormatTime(VideoView.Position)} / {FormatTime(TimeSpan.FromSeconds(_videoDuration))}";

    static string FormatTime(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";

    void ScrubberTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _scrubberDragging = true;
        ScrubberTrack.CaptureMouse();
        if (_isPlaying) { _wasPlayingBeforeScrub = true; VideoView.Pause(); }
        SeekToMouseX(e.GetPosition(ScrubberTrack).X);
        e.Handled = true;
    }

    void ScrubberTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubberDragging) return;
        SeekToMouseX(e.GetPosition(ScrubberTrack).X);
    }

    void ScrubberTrack_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubberDragging) return;
        _scrubberDragging = false;
        ScrubberTrack.ReleaseMouseCapture();
        if (_wasPlayingBeforeScrub) { _wasPlayingBeforeScrub = false; VideoView.Play(); }
    }

    void SeekToMouseX(double x)
    {
        double ratio = Math.Clamp(x / ScrubberTrack.ActualWidth, 0, 1);
        UpdateScrubberVisual(ratio);
        if (_videoDuration > 0)
            VideoView.Position = TimeSpan.FromSeconds(ratio * _videoDuration);
        UpdateScrubberTime();
    }

    void SetSpeed(int index)
    {
        _speedIndex = Math.Clamp(index, 0, SpeedSteps.Length - 1);
        VideoView.SpeedRatio = SpeedSteps[_speedIndex];
        SpeedLabel.Text = $"{SpeedSteps[_speedIndex]}×";
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────

    void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
    }

    void EnterFullscreen()
    {
        _savedWindowState = WindowState;
        _savedWindowStyle = WindowStyle;
        _savedResizeMode  = ResizeMode;
        WindowStyle  = WindowStyle.None;
        ResizeMode   = ResizeMode.NoResize;
        WindowState  = WindowState.Maximized;
        _isFullscreen = true;
    }

    void ExitFullscreen()
    {
        WindowStyle   = _savedWindowStyle;
        ResizeMode    = _savedResizeMode;
        WindowState   = _savedWindowState;
        _isFullscreen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ApplyDarkTitleBar);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    async void DeleteCurrent()
    {
        if (_files.Count == 0 || _index < 0) return;

        _heicCts?.Cancel();
        _heicCts = null;
        StopSpinner();

        var path        = _files[_index];
        var deleteIndex = _index;
        StopGifAnimation();

        _files.RemoveAt(_index);
        if (_files.Count == 0)
        {
            ShowEmptyState();
        }
        else
        {
            if (_index >= _files.Count) _index = _files.Count - 1;
            LoadCurrent();
        }

        var (deleted, entry) = await Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    break;
                }
                catch { await Task.Delay(100); }
            }

            var op = new SHFILEOPSTRUCT
            {
                wFunc  = Native.FO_DELETE,
                pFrom  = path + "\0",
                fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI,
            };
            int result = ShellInterop.SHFileOperationInternal(ref op);
            bool ok = result == 0 && !op.fAnyOperationsAborted;
            (string IFile, string RFile)? e = null;
            if (ok)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    e = ShellInterop.FindRecycleBinEntry(path);
                    if (e.HasValue) break;
                    await Task.Delay(100);
                }
            }
            return (ok, e);
        });

        if (deleted)
        {
            if (entry.HasValue)
            {
                if (_undoStack.Count >= 10) _undoStack.RemoveAt(0);
                _undoStack.Add(new DeletedEntry(path, entry.Value.RFile, entry.Value.IFile, deleteIndex));
                ShowUndoButton();
            }
        }
        else
        {
            _files.Insert(Math.Min(deleteIndex, _files.Count), path);
        }
    }

    void ShowUndoButton()
    {
        _undoTimer?.Stop();
        UndoBtn.Visibility  = Visibility.Visible;
        UndoSep.Visibility  = Visibility.Visible;
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _undoTimer.Tick += (_, _) => HideUndoButton();
        _undoTimer.Start();
    }

    void HideUndoButton()
    {
        _undoTimer?.Stop();
        _undoTimer         = null;
        UndoBtn.Visibility = Visibility.Collapsed;
        UndoSep.Visibility = Visibility.Collapsed;
        _undoStack.Clear();
    }

    void UndoDelete_Click(object sender, RoutedEventArgs e) => UndoDelete();

    void UndoDelete()
    {
        if (_undoStack.Count == 0) return;
        var e = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        try
        {
            File.Move(e.RFile, e.Path);
            File.Delete(e.IFile);
            int insertAt = Math.Min(e.Index, _files.Count);
            _files.Insert(insertAt, e.Path);
            _index = insertAt;
            LoadCurrent();
        }
        catch { }

        if (_undoStack.Count == 0)
            HideUndoButton();
        else
            ShowUndoButton();
    }

    void ShowEmptyState()
    {
        StopGifAnimation();
        VideoView.Stop();
        VideoView.Source     = null;
        ImageView.Source     = null;
        VideoView.Visibility = Visibility.Collapsed;
        ImageView.Visibility = Visibility.Collapsed;
        Panel.SetZIndex(ImageView, 0);
        Panel.SetZIndex(VideoView, 0);
        ShowVideoControls(false);
        ContentGrid.Visibility = Visibility.Collapsed;
        LoadingDim.Visibility  = Visibility.Collapsed;
        EmptyState.Visibility  = Visibility.Visible;
        Title = "Taste";
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.Escape:
                if (ShortcutsOverlay.Visibility == Visibility.Visible)
                    ShortcutsOverlay.Visibility = Visibility.Collapsed;
                else if (_isFullscreen)
                    ExitFullscreen();
                else
                    Hide();
                e.Handled = true;
                break;
            case Key.Space:
                Hide();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                Navigate(+1);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteCurrent();
                e.Handled = true;
                break;
            case Key.P:
                if (!ctrl && VideoView.Visibility == Visibility.Visible)
                {
                    TogglePlayPause();
                    e.Handled = true;
                }
                break;
            case Key.S:
                if (!ctrl && VideoView.Visibility == Visibility.Visible)
                {
                    _scrubberHideTimer!.Stop();
                    ApplyScrubberState(ScrubberOverlay.Visibility != Visibility.Visible);
                    e.Handled = true;
                }
                break;
            case Key.OemMinus:
            case Key.Subtract:
                if (ctrl) { ZoomBy(1.0 / 1.25, null); e.Handled = true; }
                else      { SetSpeed(_speedIndex - 1); e.Handled = true; }
                break;
            case Key.OemPlus:
            case Key.Add:
                if (ctrl) { ZoomBy(1.25, null);        e.Handled = true; }
                else      { SetSpeed(_speedIndex + 1); e.Handled = true; }
                break;
        }
    }

    // ── Toolbar clicks ────────────────────────────────────────────────────────

    void Info_Click(object sender, RoutedEventArgs e)       => ShortcutsOverlay.Visibility = Visibility.Visible;
    void CloseInfo_Click(object sender, RoutedEventArgs e)  => ShortcutsOverlay.Visibility = Visibility.Collapsed;
    void ZoomOut_Click(object sender, RoutedEventArgs e)    => ZoomBy(1.0 / 1.25, null);
    void ZoomIn_Click(object sender, RoutedEventArgs e)     => ZoomBy(1.25, null);
    void Fit_Click(object sender, RoutedEventArgs e) => SetFitZoom();
    void PlayPause_Click(object sender, RoutedEventArgs e)  => TogglePlayPause();
    void SpeedDown_Click(object sender, RoutedEventArgs e)  => SetSpeed(_speedIndex - 1);
    void SpeedUp_Click(object sender, RoutedEventArgs e)    => SetSpeed(_speedIndex + 1);

    // ── Window lifecycle ──────────────────────────────────────────────────────

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        if (_isFullscreen)
        {
            WindowStyle   = _savedWindowStyle;
            ResizeMode    = _savedResizeMode;
            WindowState   = _savedWindowState;
            _isFullscreen = false;
        }
        _heicCts?.Cancel();
        _heicCts = null;
        StopSpinner();
        StopGifAnimation();
        VideoView.Stop();
        VideoView.Source = null;
        ImageView.Source = null;
        Hide();
    }

    void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ShellInterop.SetDarkTitleBar(hwnd);
    }
}

readonly struct GifFrameInfo(BitmapFrame src, int delay, int disposal, int left, int top)
{
    public BitmapFrame Src      { get; } = src;
    public int         Delay    { get; } = delay;
    public int         Disposal { get; } = disposal;
    public int         Left     { get; } = left;
    public int         Top      { get; } = top;

    public static GifFrameInfo Read(BitmapFrame frame)
    {
        int delay = 100, disposal = 0, left = 0, top = 0;
        try
        {
            var meta = (BitmapMetadata)frame.Metadata;
            if (meta.GetQuery("/grctlext/Delay") is ushort d)
                delay = d == 0 ? 100 : Math.Max(d * 10, 20);
            if (meta.GetQuery("/grctlext/Disposal") is ushort disp)
                disposal = disp;
            if (meta.GetQuery("/imgdesc/Left") is ushort l)
                left = l;
            if (meta.GetQuery("/imgdesc/Top") is ushort t)
                top = t;
        }
        catch { }
        return new GifFrameInfo(frame, delay, disposal, left, top);
    }
}
