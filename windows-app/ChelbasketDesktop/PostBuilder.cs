using System.Globalization;
using System.Text.RegularExpressions;

namespace ChelbasketDesktop;

public static class PostBuilder
{
    public const string CallToAction = "Подключайтесь к трансляции и болейте за любимые команды 💥";
    public const string StatisticsLine = "Подробную статистику матчей можно посмотреть на портале Федерации баскетбола Челябинской области: vk.cc/cG31oX";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex StreamPattern = new(@"^/live-(?<owner>\d+)_(?<video>\d+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string DisplayTournament(string name)
    {
        if (name.Contains("Кубок Голоса", StringComparison.CurrentCultureIgnoreCase)) return "Кубок Голоса";
        if (Regex.IsMatch(name, @"^Чемпионат Челябинска(?:\s*-\s*\d+\s*группа)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Мужской Чемпионат города";
        return name.Trim();
    }

    public static string DefaultIntro(string name, DateTime date)
    {
        var tournament = DisplayTournament(name);
        var day = date.ToString("d MMMM", Russian);
        var openings = new[]
        {
            $"🏀 Время баскетбола! {day} встречаем игровой день турнира «{tournament}».",
            $"🏀 Паркет готов к новым встречам! {day} — матчи турнира «{tournament}».",
            $"🏀 Заряжаемся баскетболом: {day} нас ждёт игровой день «{tournament}»."
        };
        var selector = Math.Abs(HashCode.Combine(date.Day, date.Month, tournament.Length)) % openings.Length;
        return openings[selector];
    }

    public static string DefaultHashtags(string name)
    {
        if (name.Contains("Кубок Голоса", StringComparison.CurrentCultureIgnoreCase)) return "#кубокголоса #дети";
        if (name.Contains("ЮБЛ", StringComparison.CurrentCultureIgnoreCase)) return "#ЮБЛ #ЮжныйУрал";
        var compact = Regex.Replace(DisplayTournament(name), @"[^\p{L}\p{N}]", "");
        return string.IsNullOrWhiteSpace(compact) ? "#баскетбол" : $"#{compact}";
    }

    public static bool TryExpandStreamLink(string firstLink, int offset, out string link)
    {
        link = string.Empty;
        if (!Uri.TryCreate(firstLink.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("vk.ru", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("vk.com", StringComparison.OrdinalIgnoreCase)) return false;

        var match = StreamPattern.Match(uri.AbsolutePath);
        if (!match.Success || !long.TryParse(match.Groups["video"].Value, out var firstId)) return false;
        try
        {
            var nextId = checked(firstId + offset);
            var builder = new UriBuilder(uri) { Path = $"/live-{match.Groups["owner"].Value}_{nextId}" };
            link = builder.Uri.AbsoluteUri;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static string BuildPost(string tournamentName, DateTime date, IReadOnlyList<GameRecord> games,
        string intro, string venue, string hashtags, string firstStreamLink,
        IReadOnlyDictionary<string, string>? streamLinksByGameId = null)
    {
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(intro) ? DefaultIntro(tournamentName, date) : intro.Trim(),
            CallToAction,
            string.Empty
        };

        for (var index = 0; index < games.Count; index++)
        {
            var game = games[index];
            var teams = $"{game.HomeTeam} - {game.GuestTeam}";
            string? createdUrl = null;
            var hasCreatedLink = streamLinksByGameId is not null && streamLinksByGameId.TryGetValue(game.Id, out createdUrl);
            var hasSequentialLink = TryExpandStreamLink(firstStreamLink, index, out var sequentialUrl);
            var label = hasCreatedLink
                ? $"[{createdUrl}|{teams}]"
                : hasSequentialLink
                    ? $"[{sequentialUrl}|{teams}]"
                    : $"[|{teams}]";
            lines.Add($"⚡{game.Time} - {label}");
        }

        lines.Add(string.Empty);
        lines.Add(StatisticsLine);
        lines.Add(string.Empty);
        lines.Add($"📍{(string.IsNullOrWhiteSpace(venue) ? "[Название зала и адрес]" : venue.Trim())}");
        lines.Add(string.Empty);
        lines.Add(NormalizeHashtags(hashtags));
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    public static string NormalizeHashtags(string value) =>
        string.Join(' ', Regex.Split(value.Trim(), @"\s+").Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => $"#{tag.TrimStart('#')}"));
}
