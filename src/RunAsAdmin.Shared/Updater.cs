using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RunAsAdmin.Shared;

public sealed class UpdateInfo
{
    public bool Available { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Prüft GitHub-Releases auf eine neue Version und lädt das Update-ZIP herunter.
/// Öffentliches Repo – kein Token nötig.
/// </summary>
public static class Updater
{
    // Dein Repo:
    public const string Owner = "Delido";
    public const string Repo = "makemeadmin";
    public const string AssetName = "RunAsAdmin-Update.zip";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub-API verlangt einen User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RunAsAdmin-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static async Task<UpdateInfo> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var info = new UpdateInfo { CurrentVersion = currentVersion };
        try
        {
            using var http = CreateClient();
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var rel = await http.GetFromJsonAsync<GhRelease>(url, ct);
            if (rel is null) return info;

            info.LatestVersion = (rel.TagName ?? "").TrimStart('v', 'V');
            info.Notes = rel.Body;
            var asset = rel.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            info.DownloadUrl = asset?.BrowserDownloadUrl;

            info.Available = IsNewer(info.LatestVersion, currentVersion) && info.DownloadUrl is not null;
            return info;
        }
        catch
        {
            return info; // offline / kein Release – kein Update
        }
    }

    /// <summary>Lädt das Update-ZIP herunter und gibt den lokalen Pfad zurück.</summary>
    public static async Task<string> DownloadAsync(string downloadUrl, string targetDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, AssetName);
        using var http = CreateClient();
        await using var s = await http.GetStreamAsync(downloadUrl, ct);
        await using var f = File.Create(dest);
        await s.CopyToAsync(f, ct);
        return dest;
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(Normalize(latest), out var l) &&
            Version.TryParse(Normalize(current), out var c))
            return l > c;
        return false;
    }

    private static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        // "1.2" -> "1.2.0"
        var parts = v.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => v,
        };
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
