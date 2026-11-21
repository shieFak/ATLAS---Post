using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace ChelbasketDesktop;

public sealed record AppUpdate(Version Version, Uri ReleasePage);

public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();
    private const string LatestReleaseUrl = "https://api.github.com/repos/shieFak/CRBF_post/releases/latest";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var page = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(page)) return null;

        const string prefix = "app-v";
        if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(tag[prefix.Length..], out var latest) ||
            latest <= CurrentVersion ||
            !Uri.TryCreate(page, UriKind.Absolute, out var releaseUri)) return null;

        return new AppUpdate(latest, releaseUri);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChelbasketDesktop", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
