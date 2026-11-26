using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ChelbasketDesktop;

public static class AsbPackageBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string BroadcastTitle(TournamentInfo tournament, DateTime date, GameRecord game)
    {
        var teams = $"{NormalizeTeam(game.HomeTeam)} - {NormalizeTeam(game.GuestTeam)}";
        var gender = string.IsNullOrWhiteSpace(game.BroadcastGender) ? tournament.BroadcastGender : game.BroadcastGender;
        return tournament.IsAsbSpo
            ? $"# {date:dd.MM} | {teams} ({gender})"
            : $"АСБ Танкоград, {gender}: {teams}";
    }

    public static string BuildSingleGamePost(TournamentInfo tournament, GameRecord game) =>
        $"{BroadcastTitle(tournament, game.Date, game)}{Environment.NewLine}{Environment.NewLine}" +
        $"Соревнования проводятся при поддержке Российского спортивного фонда{Environment.NewLine}#РСФ74";

    public static string BuildSpoDayPost(TournamentInfo tournament, IReadOnlyList<GameRecord> games)
    {
        var hasGirls = games.Any(game => game.BroadcastGender.Equals("девушки", StringComparison.CurrentCultureIgnoreCase));
        var hasBoys = games.Any(game => game.BroadcastGender.Equals("юноши", StringComparison.CurrentCultureIgnoreCase));
        var players = (hasGirls, hasBoys) switch
        {
            (true, false) => "девушек",
            (false, true) => "юношей",
            _ => "юношей и девушек"
        };
        return $"Встречаем очередной игровой день в дивизионе «СПО Челябинская область» 🏀{Environment.NewLine}{Environment.NewLine}" +
               $"Сегодня играют команды {players}, приглашаем вас к просмотру матчей, поддерживайте свои любимые команды 🔥{Environment.NewLine}{Environment.NewLine}" +
               $"Соревнования проводятся при поддержке Российского спортивного фонда{Environment.NewLine}#РСФ74";
    }

    public static string NormalizeTeam(string team)
    {
        var clean = Regex.Replace(team.Trim(), @"\s+\([^()]*\)$", string.Empty).Trim();
        if (clean.Equals("МГТУ-Стальные сердца", StringComparison.CurrentCultureIgnoreCase))
            return "МГТУ им. Носова - Стальные сердца";
        return clean;
    }

    public static async Task<DownloadResult> WritePackageAsync(
        string folderPath, TournamentInfo tournament, DateTime date, IReadOnlyList<GameRecord> games,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folderPath);
        var gameTitles = games.Select(game => BroadcastTitle(tournament, date, game)).ToArray();
        var ids = games.Select(game => $"{game.Number:00} — {game.Id}");
        await File.WriteAllTextAsync(Path.Combine(folderPath, "games.txt"), string.Join(Environment.NewLine, gameTitles), Utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "game_ids.txt"), string.Join(Environment.NewLine, ids), Utf8, cancellationToken);

        if (tournament.IsAsbSpo)
        {
            await File.WriteAllTextAsync(Path.Combine(folderPath, "post.txt"), BuildSpoDayPost(tournament, games), Utf8, cancellationToken);
        }
        else
        {
            var postsFolder = Path.Combine(folderPath, "posts");
            Directory.CreateDirectory(postsFolder);
            foreach (var game in games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filename = $"{game.Number:00}_{Slug(NormalizeTeam(game.HomeTeam))}_-_{Slug(NormalizeTeam(game.GuestTeam))}.txt";
                await File.WriteAllTextAsync(Path.Combine(postsFolder, filename), BuildSingleGamePost(tournament, game), Utf8, cancellationToken);
            }
        }

        var root = Directory.GetParent(folderPath)?.FullName ?? folderPath;
        var zipBaseName = Path.GetFileName(folderPath);
        var zipPath = Path.Combine(root, zipBaseName + ".zip");
        for (var suffix = 2; File.Exists(zipPath); suffix++) zipPath = Path.Combine(root, $"{zipBaseName}_{suffix}.zip");
        ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return new DownloadResult(folderPath, zipPath, games.Count, games.Count(game => !string.IsNullOrWhiteSpace(game.CoverPath)));
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value, @"[^\p{L}\p{N}]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(slug) ? "team" : slug;
    }
}
