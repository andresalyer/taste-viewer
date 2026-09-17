namespace Taste.Services;

public class AppSettings
{
    public int    ThumbnailSize   { get; set; } = 220;
    public string SortMode        { get; set; } = "Date Modified";
    public string SortDirection   { get; set; } = "Descending";
    public string LastFolder      { get; set; } = "";
    public double WindowWidth     { get; set; } = 1200;
    public double WindowHeight    { get; set; } = 800;
    public double         WindowLeft      { get; set; } = -1;
    public double         WindowTop       { get; set; } = -1;
    public bool           SidebarOpen     { get; set; } = true;
    public double         SidebarWidth    { get; set; } = 220;
    public List<string>   PinnedFolders          { get; set; } = new();
    public bool           ExplorerSpacebarEnabled { get; set; } = true;
}
