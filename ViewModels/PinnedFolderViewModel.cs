using System.IO;

namespace Taste.ViewModels;

public class PinnedFolderViewModel
{
    public string FullPath    { get; }
    public string DisplayName { get; }

    public PinnedFolderViewModel(string path)
    {
        FullPath    = path;
        DisplayName = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
    }
}
