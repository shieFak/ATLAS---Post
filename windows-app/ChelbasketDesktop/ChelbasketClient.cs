using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ChelbasketDesktop;

public sealed class ChelbasketClient : IDisposable
{
    public static readonly Uri SiteRoot = new("https://fed.chelbasket.zeebra.ru/");
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(40)
    };

    public async Task<IReadOnlyList<TournamentInfo>> GetTournamentsAsync(CancellationToken cancellationToken = default)
    {
        var html = await GetHtmlAsync(SiteRoot, cancellationToken);
        var tournaments = new List<TournamentInfo>();

        foreach (Match anchor in Regex.Matches(html, """(?is)<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>"""))
        {
            var href = Attribute(anchor.Groups["attrs"].Value, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            href = WebUtility.HtmlDecode(href).Trim();
            if (!Regex.IsMatch(href, """^/?games/?\?""", RegexOptions.IgnoreCase)) continue;

            var name = CleanText(anchor.Groups["body"].Value);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!Uri.TryCreate(SiteRoot, href, out var uri)) continue;
            if (tournaments.Any(item => item.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) continue;
            tournaments.Add(new TournamentInfo(name, uri.AbsoluteUri));
        }

        return tournaments.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<GameRecord>> GetGamesForDateAsync(
        TournamentInfo tournament,
        DateTime selectedDate,
        CancellationToken cancellationToken = default)
    {
        var separator = tournament.Url.Contains('?') ? '&' : '?';
        var listUri = new Uri($"{tournament.Url}{separator}gamestatus=3&gameteam=0");
        var html = await GetHtmlAsync(listUri, cancellationToken);
        var citySuffixes = GetCitySuffixes(html);
        var games = new List<GameRecord>();

        const string cardPattern = """(?is)<div\b(?=[^>]*\bclass\s*=\s*['"][^'"]*\ballgames__item\b[^'"]*['"])[^>]*>(?<body>.*?)</div\s*>\s*</div\s*>""";
        foreach (Match card in Regex.Matches(html, cardPattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = card.Groups["body"].Value;
            var dateText = InnerDivText(body, "allgames__datetime");
            if (string.IsNullOrWhiteSpace(dateText) || !DateMatches(dateText, selectedDate)) continue;

            var gameHref = Regex.Match(body, """(?is)href\s*=\s*['"](?<url>[^'"]*game/\?id=\d+[^'"]*)['"]""").Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(gameHref)) continue;
            gameHref = WebUtility.HtmlDecode(gameHref);
            if (!Uri.TryCreate(SiteRoot, gameHref, out var gameUri)) continue;
            var idMatch = Regex.Match(gameUri.Query, """(?:\?|&)id=(?<id>\d+)""");
            if (!idMatch.Success) continue;

            var timeMatch = Regex.Match(dateText, """(?<hour>\d{1,2}):(?<minute>\d{2})""");
            if (!timeMatch.Success) continue;
            var time = $"{int.Parse(timeMatch.Groups["hour"].Value):00}:{timeMatch.Groups["minute"].Value}";
            var home = StripCity(InnerDivText(body, "allgames__teamhome"), citySuffixes);
            var guest = StripCity(InnerDivText(body, "allgames__teamguest"), citySuffixes);
            if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(guest)) continue;

            var coverBuilder = new UriBuilder(gameUri) { Path = "/tvtitle/tvtitle22/", Fragment = string.Empty };
            games.Add(new GameRecord
            {
                Id = idMatch.Groups["id"].Value,
                Date = selectedDate.Date,
                Time = time,
                HomeTeam = home,
                GuestTeam = guest,
                GameUrl = gameUri.AbsoluteUri,
                CoverUrl = coverBuilder.Uri.AbsoluteUri
            });
        }

        return games
            .GroupBy(game => game.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(game => TimeSpan.Parse(game.Time))
            .Select((game, index) => { game.Number = index + 1; return game; })
            .ToArray();
    }

    public async Task<string> GetHtmlAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private static HashSet<string> GetCitySuffixes(string html)
    {
        var select = Regex.Match(html, """(?is)<select\b(?=[^>]*\bid\s*=\s*['"]gameteam['"])[^>]*>(?<body>.*?)</select>""");
        var cities = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (Match option in Regex.Matches(select.Groups["body"].Value, """(?is)<option\b[^>]*>(?<text>.*?)</option>"""))
        {
            var text = CleanText(option.Groups["text"].Value);
            var city = Regex.Match(text, """\((?<city>[^()\d]+)\)\s*$""");
            if (city.Success) cities.Add(city.Groups["city"].Value.Trim());
        }
        return cities;
    }

    private static string StripCity(string name, HashSet<string> citySuffixes)
    {
        var suffix = Regex.Match(name, """\((?<city>[^()]*)\)\s*$""");
        if (suffix.Success && citySuffixes.Contains(suffix.Groups["city"].Value.Trim()))
            return name[..suffix.Index].TrimEnd();
        return name.Trim();
    }

    private static bool DateMatches(string dateText, DateTime selectedDate)
    {
        if (dateText.Contains("сегодня", StringComparison.CurrentCultureIgnoreCase)) return selectedDate.Date == DateTime.Today;
        if (dateText.Contains("вчера", StringComparison.CurrentCultureIgnoreCase)) return selectedDate.Date == DateTime.Today.AddDays(-1);
        if (dateText.Contains("завтра", StringComparison.CurrentCultureIgnoreCase)) return selectedDate.Date == DateTime.Today.AddDays(1);

        var date = Regex.Match(dateText, """(?<day>\d{1,2})\s+(?<month>[а-яё]+)\.?\s*(?<year>20\d{2})?""", RegexOptions.IgnoreCase);
        if (!date.Success) return false;
        var month = MonthNumber(date.Groups["month"].Value);
        if (month == 0 || int.Parse(date.Groups["day"].Value) != selectedDate.Day || month != selectedDate.Month) return false;
        return !date.Groups["year"].Success || int.Parse(date.Groups["year"].Value) == selectedDate.Year;
    }

    private static int MonthNumber(string text)
    {
        var month = text.TrimEnd('.').ToLowerInvariant();
        return month switch
        {
            "янв" or "января" => 1,
            "фев" or "февраля" => 2,
            "мар" or "марта" => 3,
            "апр" or "апреля" => 4,
            "май" or "мая" => 5,
            "июн" or "июня" => 6,
            "июл" or "июля" => 7,
            "авг" or "августа" => 8,
            "сен" or "сентября" => 9,
            "окт" or "октября" => 10,
            "ноя" or "ноября" => 11,
            "дек" or "декабря" => 12,
            _ => 0
        };
    }

    private static string InnerDivText(string html, string className)
    {
        var pattern = $"""(?is)<div\b(?=[^>]*\bclass\s*=\s*['"][^'"]*\b{Regex.Escape(className)}\b[^'"]*['"])[^>]*>(?<text>.*?)</div>""";
        var value = Regex.Match(html, pattern);
        return CleanText(value.Groups["text"].Value);
    }

    private static string Attribute(string attributes, string name)
    {
        var pattern = $"""(?is)\b{Regex.Escape(name)}\s*=\s*['"](?<value>[^'"]*)['"]""";
        return Regex.Match(attributes, pattern).Groups["value"].Value;
    }

    private static string CleanText(string text)
    {
        var noTags = Regex.Replace(text, """(?is)<[^>]+>""", " ");
        return Regex.Replace(WebUtility.HtmlDecode(noTags).Replace('\u00a0', ' '), @"\s+", " ").Trim();
    }
}
