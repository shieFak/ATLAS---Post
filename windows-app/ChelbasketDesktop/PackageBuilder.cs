using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ChelbasketDesktop;

public static class PackageBuilder
{
    public static string CreatePackageFolder(string outputRoot, string tournamentName, DateTime date)
    {
        Directory.CreateDirectory(outputRoot);
        var baseName = $"{Slug(PostBuilder.DisplayTournament(tournamentName))}_{date:yyyy-MM-dd}";
        var folderPath = UniquePath(outputRoot, baseName, string.Empty);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    public static async Task<DownloadResult> WritePackageAsync(
        string folderPath,
        string tournamentName,
        DateTime date,
        IReadOnlyList<GameRecord> games,
        string intro,
        string venue,
        string hashtags,
        string firstStreamLink,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? streamLinksByGameId = null)
    {
        var displayName = PostBuilder.DisplayTournament(tournamentName);
        Directory.CreateDirectory(folderPath);

        var gameLines = games.Select(game => GameTitle(displayName, date, game));
        var idLines = games.Select(game => $"{game.Number:00} — {game.Id}");
        var post = PostBuilder.BuildPost(displayName, date, games, intro, venue, hashtags, firstStreamLink, streamLinksByGameId);
        var utf8 = new UTF8Encoding(false);

        await File.WriteAllTextAsync(Path.Combine(folderPath, "games.txt"), string.Join(Environment.NewLine, gameLines), utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "game_ids.txt"), string.Join(Environment.NewLine, idLines), utf8, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "post.txt"), post, utf8, cancellationToken);

        var outputRoot = Directory.GetParent(folderPath)?.FullName ?? folderPath;
        var zipName = Path.GetFileName(folderPath);
        var zipPath = UniquePath(outputRoot, zipName, ".zip");
        ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return new DownloadResult(folderPath, zipPath, games.Count, games.Count(game => !string.IsNullOrWhiteSpace(game.CoverPath)));
    }

    public static string CoverFileName(GameRecord game)
    {
        var core = $"{game.Number:00}_{game.Time.Replace(':', '-')}_{game.Date:dd.MM.yyyy}_{game.HomeTeam}_-_{game.GuestTeam}";
        var safe = Regex.Replace(core, @"[<>:""/\\|?*\p{Cc}]", "_");
        safe = Regex.Replace(safe, @"\s+", "_").Trim(' ', '.', '_');
        return safe + $"_{game.Id}.png";
    }

    public static string GameTitle(string tournamentName, DateTime date, GameRecord game) =>
        $"{PostBuilder.DisplayTournament(tournamentName)}: {game.HomeTeam} - {game.GuestTeam} | {date:dd.MM.yyyy}";

    public static string ScheduleFileName(string tournamentName, DateTime date, string category)
    {
        var parts = new List<string> { date.ToString("yyyy-MM-dd"), "Расписание", Slug(PostBuilder.DisplayTournament(tournamentName)) };
        if (!string.IsNullOrWhiteSpace(category) &&
            !category.StartsWith("Автоматически", StringComparison.CurrentCultureIgnoreCase))
            parts.Add(Slug(category));
        return string.Join('_', parts) + ".png";
    }

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value, @"[^\p{L}\p{N}]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(slug) ? "Chelbasket" : slug;
    }

    private static string UniquePath(string root, string baseName, string extension)
    {
        var path = Path.Combine(root, baseName + extension);
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(root, $"{baseName}_{suffix}{extension}");
        return path;
    }
}
