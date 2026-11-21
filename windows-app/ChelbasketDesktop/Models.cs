namespace ChelbasketDesktop;

public sealed record TournamentInfo(string Name, string Url);

public sealed class GameRecord
{
    public int Number { get; set; }
    public required string Id { get; init; }
    public required DateTime Date { get; init; }
    public required string Time { get; set; }
    public required string HomeTeam { get; set; }
    public required string GuestTeam { get; set; }
    public required string GameUrl { get; init; }
    public string Hall { get; set; } = string.Empty;
    public bool IsSelected { get; set; } = true;
    public string CoverUrl { get; set; } = string.Empty;
    public string CoverPath { get; set; } = string.Empty;
    public string Status { get; set; } = "Ожидает";
}

public sealed record DownloadResult(string FolderPath, string ZipPath, int GameCount, int CoverCount);

public sealed class VkStreamRow
{
    public required GameRecord Game { get; init; }
    public required string Title { get; init; }
    public string Status { get; set; } = "Ожидает";
    public string Link { get; set; } = string.Empty;
}
