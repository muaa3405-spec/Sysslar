using System.Net.Http;
using System.Text.Json;

namespace Tasky.Services;

/// <summary>
/// Optional check for a newer Tasky on GitHub. It is off unless the user turns it on in Settings,
/// and it sends nothing about the user: one anonymous request for the latest release number.
/// </summary>
public sealed class UpdateCheckService
{
    /// <summary>An automatic check runs at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Http = CreateClient();

    public sealed record UpdateInfo(Version Version, string PageUrl);

    /// <summary>The newest release when it is newer than this build, otherwise null.</summary>
    /// <exception cref="HttpRequestException">No connection, or GitHub refused the request.</exception>
    public async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await Http.GetAsync(AppInfo.LatestReleaseApiUrl);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(body);

        var tag     = json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var pageUrl = json.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;

        return latest > AppInfo.Version ? new UpdateInfo(latest, pageUrl ?? AppInfo.ReleasesPageUrl) : null;
    }

    /// <summary>True when the user turned checks on and the last one is older than <see cref="Interval"/>.</summary>
    public static bool IsAutomaticCheckDue(AppSettings settings)
        => settings.CheckForUpdates && DateTime.Now - settings.LastUpdateCheck >= Interval;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = RequestTimeout };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Sysslar/{AppInfo.VersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
