using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ChelbasketDesktop;

public sealed class AsbClient : IDisposable
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(40) };
    private readonly Dictionary<string, string> _companionUrls = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<TournamentInfo>> GetTournamentsAsync(CancellationToken cancellationToken = default)
    {
        var year = DateTime.Today.Year;
        var root = new Uri($"https://ig.asbasket.ru/{year}/");
        using var response = await _http.GetAsync(root, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var sources = new Dictionary<bool, Dictionary<bool, string>>
        {
            [false] = new(),
            [true] = new()
        };

        foreach (Match match in Regex.Matches(html, """(?is)<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>"""))
        {
            var attrs = match.Groups["attrs"].Value;
            if (Attribute(attrs, "data-parent") != "114") continue;
            var href = WebUtility.HtmlDecode(Attribute(attrs, "href")).Trim();
            var sourceName = CleanText(match.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(href) || !sourceName.StartsWith("Челябинская область", StringComparison.CurrentCultureIgnoreCase)) continue;
            if (!Uri.TryCreate(root, href, out var uri)) continue;

            var isSpo = sourceName.Contains(" СПО", StringComparison.CurrentCultureIgnoreCase);
            var isWomen = sourceName.Contains("(жен)", StringComparison.CurrentCultureIgnoreCase);
            sources[isSpo][isWomen] = uri.AbsoluteUri;
        }

        _companionUrls.Clear();
        var results = new List<TournamentInfo>();
        foreach (var isSpo in new[] { false, true })
        {
            var group = sources[isSpo];
            if (group.Count == 0) continue;
            var primary = group.GetValueOrDefault(false) ?? group.Values.First();
            var companion = group.GetValueOrDefault(true);
            if (!string.IsNullOrWhiteSpace(companion) && !companion.Equals(primary, StringComparison.OrdinalIgnoreCase))
                _companionUrls[primary] = companion;
            results.Add(new TournamentInfo(
                isSpo ? "АСБ — Челябинская область СПО" : "АСБ — Челябинская область",
                primary,
                TournamentSource.Asb,
                isSpo));
        }
        return results;
    }

    public async Task<IReadOnlyList<GameRecord>> GetGamesForDateAsync(
        TournamentInfo tournament, DateTime selectedDate, CancellationToken cancellationToken = default)
    {
        var sourceUrls = new List<string> { tournament.Url };
        if (_companionUrls.TryGetValue(tournament.Url, out var companion)) sourceUrls.Add(companion);
        var games = new List<GameRecord>();

        foreach (var sourceUrl in sourceUrls)
        {
            using var response = await _http.GetAsync(sourceUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var sourceGender = GenderFromUrl(sourceUrl, tournament.IsAsbSpo);

            foreach (Match card in Regex.Matches(html, """(?is)<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>"""))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attrs = card.Groups["attrs"].Value;
                if (!Attribute(attrs, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("games__item", StringComparer.Ordinal)) continue;
                var href = WebUtility.HtmlDecode(Attribute(attrs, "href")).Trim();
                if (!Uri.TryCreate(new Uri(sourceUrl), href, out var gameUri)) continue;
                var idMatch = Regex.Match(gameUri.Query, """(?:\?|&)id=(?<id>\d+)""");
                if (!idMatch.Success) continue;

                var body = card.Groups["body"].Value;
                var titleHtml = Regex.Match(body, """(?is)<span\b(?=[^>]*\bclass\s*=\s*['"][^'"]*\bgames__item-title\b[^'"]*['"])[^>]*>(?<value>.*?)</span>""").Groups["value"].Value;
                titleHtml = Regex.Replace(titleHtml, """(?is)\s*<span\b.*$""", string.Empty);
                var title = CleanText(titleHtml);
                var teamsMatch = Regex.Match(title, "^(?<home>.*?)\\s+-\\s+(?<guest>.*)$");
                if (!teamsMatch.Success) continue;

                var metaHtml = Regex.Match(body, """(?is)<span\b(?=[^>]*\bclass\s*=\s*['"][^'"]*\bgames__item-meta\b[^'"]*['"])[^>]*>(?<value>.*?)</span>""").Groups["value"].Value;
                var meta = CleanText(metaHtml);
                if (!DateTime.TryParseExact(meta, "dd.MM.yyyy '·' HH:mm", Russian, DateTimeStyles.None, out var dateTime) || dateTime.Date != selectedDate.Date)
                    continue;

                var coverBuilder = new UriBuilder(gameUri) { Path = $"/{dateTime.Year}/tvtitle/", Fragment = string.Empty };
                games.Add(new GameRecord
                {
                    Id = idMatch.Groups["id"].Value,
                    Date = dateTime.Date,
                    Time = dateTime.ToString("HH:mm"),
                    HomeTeam = StripCity(teamsMatch.Groups["home"].Value),
                    GuestTeam = StripCity(teamsMatch.Groups["guest"].Value),
                    GameUrl = gameUri.AbsoluteUri,
                    CoverUrl = coverBuilder.Uri.AbsoluteUri,
                    BroadcastGender = sourceGender
                });
            }
        }

        return games
            .GroupBy(game => game.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(game => TimeSpan.Parse(game.Time, CultureInfo.InvariantCulture))
            .ThenBy(game => long.TryParse(game.Id, out var id) ? id : 0)
            .Select((game, index) => { game.Number = index + 1; return game; })
            .ToArray();
    }

    private static string GenderFromUrl(string url, bool isSpo)
    {
        var tag = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["tag"] ?? string.Empty;
        var isWomen = tag.EndsWith("-w", StringComparison.OrdinalIgnoreCase);
        return isSpo ? (isWomen ? "девушки" : "юноши") : (isWomen ? "женщины" : "мужчины");
    }

    private static string Attribute(string attributes, string name) =>
        Regex.Match(attributes, $"""(?is)\b{Regex.Escape(name)}\s*=\s*['"](?<value>[^'"]*)['"]""").Groups["value"].Value;

    private static string CleanText(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")) ?? string.Empty, "\\s+", " ").Trim();

    private static string StripCity(string team) => Regex.Replace(team.Trim(), "\\s+\\([^()]*\\)$", string.Empty).Trim();

    public void Dispose() => _http.Dispose();
}
