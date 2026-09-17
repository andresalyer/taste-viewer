using System.IO;
using System.Windows.Media.Imaging;
using Taste.Shared;

namespace Taste.ViewModels;

public enum FileKind { Directory, Image, Video, Other }

public class FileItemViewModel : ObservableObject
{

    private string _fullPath = string.Empty;
    private string _fileName = string.Empty;
    private string _displayName = string.Empty;

    public string FullPath    { get => _fullPath;    private set => SetField(ref _fullPath, value); }
    public string FileName    { get => _fileName;    private set => SetField(ref _fileName, value); }
    public string DisplayName { get => _displayName; private set => SetField(ref _displayName, value); }
    public string Extension { get; }
    public FileKind Kind { get; }
    public DateTime DateModified { get; }
    public long FileSize { get; }

    public bool IsDirectory => Kind == FileKind.Directory;
    public bool IsVideo => Kind == FileKind.Video;
    public bool IsImage => Kind == FileKind.Image;
    public bool IsCloudPlaceholder { get; private set; }
    public int SortOrder => IsDirectory ? 0 : 1; // dirs always before files

    public string DateModifiedDisplay => DateModified == DateTime.MinValue ? "—" : DateModified.ToString("MMM d, yyyy  h:mm tt");
    public string FileSizeDisplay     => IsDirectory ? "—" : FormatFileSize(FileSize);
    public string TypeDisplay         => Kind switch
    {
        FileKind.Directory => "Folder",
        FileKind.Image     => Extension.TrimStart('.').ToUpperInvariant() + " Image",
        FileKind.Video     => Extension.TrimStart('.').ToUpperInvariant() + " Video",
        _                  => Extension.Length > 1 ? Extension.TrimStart('.').ToUpperInvariant() + " File" : "File",
    };

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1_024                => $"{bytes} B",
        < 1_048_576            => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824        => $"{bytes / 1_048_576.0:F1} MB",
        _                      => $"{bytes / 1_073_741_824.0:F2} GB",
    };

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetField(ref _thumbnail, value);
    }

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set => SetField(ref _isRenaming, value);
    }

    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetField(ref _isDropTarget, value);
    }

    private string _renameText = string.Empty;
    public string RenameText
    {
        get => _renameText;
        set => SetField(ref _renameText, value);
    }

    // Used by FileSystemWatcher handlers (path-only, does a stat)
    public FileItemViewModel(string path, bool isDirectory)
    {
        FullPath = path;
        FileName = Path.GetFileName(path);
        Extension = isDirectory ? string.Empty : Path.GetExtension(path);
        DisplayName = isDirectory ? FileName : Path.GetFileNameWithoutExtension(path);

        if (isDirectory)
        {
            Kind = FileKind.Directory;
            try { DateModified = new DirectoryInfo(path).LastWriteTime; }
            catch { DateModified = DateTime.MinValue; }
        }
        else
        {
            try
            {
                var fi = new FileInfo(path);
                DateModified = fi.LastWriteTime;
                FileSize = fi.Length;
                IsCloudPlaceholder = ((int)fi.Attributes & AppConstants.CloudPlaceholderAttribute) != 0;
            }
            catch { }

            if (MediaExtensions.Image.Contains(Extension)) Kind = FileKind.Image;
            else if (MediaExtensions.Video.Contains(Extension)) Kind = FileKind.Video;
            else Kind = FileKind.Other;
        }
        RenameText = FileName;
    }

    // Used during folder load — metadata already fetched by enumeration, no extra stat
    public FileItemViewModel(DirectoryInfo di)
    {
        FullPath = di.FullName;
        FileName = di.Name;
        DisplayName = di.Name;
        Extension = string.Empty;
        Kind = FileKind.Directory;
        DateModified = di.LastWriteTime;
        FileSize = 0;
        RenameText = FileName;
    }

    public FileItemViewModel(FileInfo fi)
    {
        FullPath = fi.FullName;
        FileName = fi.Name;
        Extension = fi.Extension;
        DisplayName = Path.GetFileNameWithoutExtension(fi.Name);
        DateModified = fi.LastWriteTime;
        FileSize = fi.Length;

        if (MediaExtensions.Image.Contains(Extension)) Kind = FileKind.Image;
        else if (MediaExtensions.Video.Contains(Extension)) Kind = FileKind.Video;
        else Kind = FileKind.Other;

        IsCloudPlaceholder = ((int)fi.Attributes & AppConstants.CloudPlaceholderAttribute) != 0;
        RenameText = FileName;
    }

    public void ApplyRename(string newName)
    {
        string dir  = Path.GetDirectoryName(_fullPath) ?? string.Empty;
        FullPath    = Path.Combine(dir, newName);
        FileName    = newName;
        DisplayName = IsDirectory ? newName : Path.GetFileNameWithoutExtension(newName);
        RenameText  = newName;
    }

    public void SetThumbnail(BitmapSource? thumbnail) => Thumbnail = thumbnail;
    public void ClearThumbnail() => Thumbnail = null;

    public static bool IsSupportedExtension(string ext) =>
        MediaExtensions.All.Contains(ext);
}
