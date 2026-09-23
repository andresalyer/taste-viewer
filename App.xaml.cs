using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Taste.Preview;
using Taste.Services;
using WinForms = System.Windows.Forms;

namespace Taste;

public partial class App : Application
{
    public   PreviewWindow   Preview { get; private set; } = null!;
    internal ExplorerWatcher Watcher { get; private set; } = null!;
    private  WinForms.NotifyIcon _tray = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --background (used by the "Start with Windows" entry) starts in the tray only;
        // the window is built now but not shown until the user opens it from the tray.
        MainWindow = new MainWindow();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            MainWindow.Show();

        var settings = SettingsService.Load();

        Preview = new PreviewWindow();

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            (Action)(() => new WindowInteropHelper(Preview).EnsureHandle()));

        Watcher = new ExplorerWatcher
        {
            ExplorerSpacebarEnabled = settings.ExplorerSpacebarEnabled,
        };
        Watcher.FileSelected += (path, folder, files) =>
            Dispatcher.Invoke(() => Preview.OpenOrClose(path, folder, files));
        Watcher.Start();

        _tray = new WinForms.NotifyIcon
        {
            Text    = "Taste",
            Icon    = LoadTrayIcon(),
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        var open = menu.Items.Add("Open Taste", null, (_, _) => RestoreMainWindow());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { Watcher.Stop(); Shutdown(); });
        _tray.ContextMenuStrip = menu;

        _tray.DoubleClick += (_, _) => RestoreMainWindow();

        _ = CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3)); // let startup/UI settle before hitting the network

        var update = await UpdateService.CheckForUpdateAsync();
        if (update == null) return;

        // Owner must be a window that has been shown — not the case when started in the tray.
        var dialog = new UpdateDialog(update);
        if (MainWindow is { IsVisible: true })
            dialog.Owner = MainWindow;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        if (!dialog.UpdateAccepted) return;

        try
        {
            var installerPath = await UpdateService.DownloadInstallerAsync(update.DownloadUrl, update.Sha256);
            UpdateService.RunInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Watcher?.Stop();
        base.OnExit(e);
    }

    private void RestoreMainWindow()
    {
        // Closed windows drop out of Windows, so if every browser window was closed
        // (the app keeps running in the tray), open a fresh one.
        MainWindow = Windows.OfType<MainWindow>().FirstOrDefault() ?? new MainWindow();
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"))?.Stream;
            if (stream != null)
                return new System.Drawing.Icon(stream, 16, 16);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
