using System.IO;
using Taste.Interop;

namespace Taste.Services;

public static class FileOperationService
{
    public static void SendToRecycleBin(IntPtr ownerHwnd, string path) =>
        ShellInterop.SendToRecycleBin(ownerHwnd, path);

    public static string? Rename(string path, string newName)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            string newPath = Path.Combine(dir, newName);
            if (File.Exists(path))
            {
                File.Move(path, newPath);
                return newPath;
            }
            if (Directory.Exists(path))
            {
                Directory.Move(path, newPath);
                return newPath;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? CreateFolder(string parentPath, string name)
    {
        try
        {
            string newPath = Path.Combine(parentPath, name);
            Directory.CreateDirectory(newPath);
            return newPath;
        }
        catch
        {
            return null;
        }
    }
}
