namespace Taste.ViewModels;

public class PathSegment
{
    public string Name     { get; }
    public string FullPath { get; }
    public bool   IsLast   { get; set; }

    public PathSegment(string name, string fullPath)
    {
        Name     = name;
        FullPath = fullPath;
    }
}
