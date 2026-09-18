using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Taste.Services;

public sealed record UpdateInfo(string Version, string DownloadUrl, string Sha256);

public static class UpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/andresalyer/taste-viewer/releases/latest";
    private static readonly Regex InstallerNamePattern = new(@"^Taste-Setup-[\w.\-]+\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            if (!asset.BrowserDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

            // Require a published checksum so the download can be verified before it's ever run.
            var checksumAsset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (checksumAsset == null) return null;

            var sha256 = (await client.GetStringAsync(checksumAsset.BrowserDownloadUrl)).Trim();
            if (!Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$")) return null;

            return new UpdateInfo(release.TagName.TrimStart('v', 'V'), asset.BrowserDownloadUrl, sha256);
        }
        catch
        {
            return null; // offline, rate-limited, etc. — update checks should never block the app
        }
    }

    public static async Task<string> DownloadInstallerAsync(string downloadUrl, string expectedSha256)
    {
        if (!downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update download URL must use HTTPS.");

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (!InstallerNamePattern.IsMatch(fileName))
            throw new InvalidOperationException("Unexpected installer filename in update download URL.");

        CleanupStaleInstallers();

        var tempPath = Path.Combine(Path.GetTempPath(), fileName);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TasteViewer-UpdateCheck");

        await using (var source = await client.GetStreamAsync(downloadUrl))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination);
        }

        var actualSha256 = await ComputeSha256Async(tempPath);
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException("Downloaded installer failed checksum verification.");
        }

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

    // Removes installers left behind by earlier update attempts (crash, cancel, checksum
    // mismatch) so they don't accumulate in %TEMP% — the running installer can't be deleted
    // here since RunInstallerAndExit hands it off and shuts the app down immediately after.
    private static void CleanupStaleInstallers()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), "Taste-Setup-*.exe"))
            {
                try { File.Delete(path); } catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
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
