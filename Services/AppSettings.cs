namespace Taste.Services;

public class AppSettings
{
    public int    ThumbnailSize   { get; set; } = 220;
    public string LastFolder      { get; set; } = "";
    public double WindowWidth     { get; set; } = 1200;
    public double WindowHeight    { get; set; } = 800;
    public double         WindowLeft      { get; set; } = -1;
    public double         WindowTop       { get; set; } = -1;
    public bool           SidebarOpen     { get; set; } = true;
    public double         SidebarWidth    { get; set; } = 220;
    public List<string>   PinnedFolders          { get; set; } = new();
    public bool           ExplorerSpacebarEnabled { get; set; } = true;
    // Set once the first-run default pin (the user's home folder) has been offered,
    // so removing it sticks instead of it reappearing on every launch.
    public bool           DefaultPinsSeeded      { get; set; }
}
