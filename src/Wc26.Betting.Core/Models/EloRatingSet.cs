namespace Wc26.Betting.Core.Models;

public sealed class EloRatingSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateOnly AsOfDate { get; init; } = new(2026, 5, 26);
    public string Source { get; init; } = "Hardcoded Elo ratings from user-provided table";
    public List<EloTeamRating> Teams { get; init; } = [];
}

public sealed class EloTeamRating
{
    public int Rank { get; init; }
    public string Team { get; init; } = string.Empty;
    public string NormalizedTeam { get; init; } = string.Empty;
    public int Rating { get; init; }
}
