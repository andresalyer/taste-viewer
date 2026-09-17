namespace Taste.Shared;

internal static class AppConstants
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — iCloud/OneDrive placeholder not yet synced
    public const int CloudPlaceholderAttribute = 0x00400000;

    // UI timing
    public const int ScrollbarHideDelayMs  = 1200;
    public const int WatcherDebounceMs     = 2000;
    public const int UndoExpiryMs          = 12000;

    // Window defaults (also declared in AppSettings for serialisation)
    public const double DefaultWindowWidth  = 1200;
    public const double DefaultWindowHeight = 800;
}
