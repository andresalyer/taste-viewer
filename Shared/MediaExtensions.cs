namespace Taste.Shared;

internal static class MediaExtensions
{
    public static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
        ".heic", ".heif", ".ico", ".raw", ".cr2", ".nef", ".arw", ".dng", ".orf", ".sr2",
    };

    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".wmv", ".mkv", ".3gp", ".webm",
        ".m4v", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg",
    };

    public static readonly HashSet<string> All = new(Image.Concat(Video), StringComparer.OrdinalIgnoreCase);
}
