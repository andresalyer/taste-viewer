using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taste.Services;

public sealed record UpdateInfo(string Version, string DownloadUrl);

public static class UpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/andresalyer/taste-viewer/releases/latest";

    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TasteViewer-UpdateCheck");

            var json = await client.GetStringAsync(ReleasesApiUrl);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release?.TagName == null) return null;

            if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var latest)) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (Normalize(latest) <= Normalize(current)) return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.StartsWith("Taste-Setup-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo(release.TagName.TrimStart('v', 'V'), asset.BrowserDownloadUrl);
        }
        catch
        {
            return null; // offline, rate-limited, etc. — update checks should never block the app
        }
    }

    public static async Task<string> DownloadInstallerAsync(string downloadUrl)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TasteViewer-UpdateCheck");

        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(downloadUrl));
        await using var source = await client.GetStreamAsync(downloadUrl);
        await using var destination = File.Create(tempPath);
        await source.CopyToAsync(destination);
        return tempPath;
    }

    public static void RunInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        });
        System.Windows.Application.Current.Shutdown();
    }

    // Drops the revision component so a 3-part release tag (1.2.0) compares
    // correctly against the 4-part AssemblyVersion .NET generates (1.2.0.0).
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}
