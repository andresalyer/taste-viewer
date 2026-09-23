using System.Windows;
using Taste.Services;

namespace Taste;

public partial class UpdateDialog : Window
{
    public bool UpdateAccepted { get; private set; }

    public UpdateDialog(UpdateInfo update)
    {
        InitializeComponent();
        TitleText.Text = $"Taste Viewer {update.Version} is available";
        NotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? "No release notes were provided for this version."
            : update.ReleaseNotes.Trim();
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateAccepted = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        UpdateAccepted = false;
        Close();
    }
}
