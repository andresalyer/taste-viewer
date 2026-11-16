using System.IO;

namespace Taste.ViewModels;

public class PinnedFolderViewModel
{
    public string FullPath    { get; }
    public string DisplayName { get; }

    public PinnedFolderViewModel(string path)
    {
        FullPath    = path;
        // The profile folder is named after the account (e.g. "Personal"), which reads
        // oddly as a sidebar label, so show it as "Home" like File Explorer does.
        bool isHome = string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            StringComparison.OrdinalIgnoreCase);
        DisplayName = isHome ? "Home"
                    : Path.GetFileName(path) is { Length: > 0 } n ? n : path;
    }
}
