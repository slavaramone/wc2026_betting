namespace Wc26.Betting.Core.Models;

public sealed class Wc2026CalendarSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceFolder { get; init; } = string.Empty;
    public string SourceKind { get; init; } = "sofascore";
    public int TournamentId { get; init; } = 16;
    public int SeasonId { get; init; } = 58210;
    public List<Wc2026CalendarMatch> Matches { get; init; } = [];
}

public sealed class Wc2026CalendarMatch
{
    public long EventId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public int? RoundNumber { get; init; }
    public string RoundFolder { get; init; } = string.Empty;
    public string RoundSlug { get; init; } = string.Empty;
    public DateTimeOffset? StartUtc { get; init; }
    public long? StartTimestamp { get; init; }
    public string StatusType { get; init; } = string.Empty;
    public string StatusDescription { get; init; } = string.Empty;
    public string TournamentName { get; init; } = string.Empty;
    public string TournamentSlug { get; init; } = string.Empty;
    public string SeasonName { get; init; } = string.Empty;
    public string SeasonYear { get; init; } = string.Empty;
    public string SourceCalendarFile { get; init; } = string.Empty;

    public bool HasKnownTeams => !string.IsNullOrWhiteSpace(HomeTeam)
        && !string.IsNullOrWhiteSpace(AwayTeam)
        && !IsPlaceholderTeam(HomeTeam)
        && !IsPlaceholderTeam(AwayTeam);

    private static bool IsPlaceholderTeam(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "tbd" or "to be decided" or "winner" or "unknown" or "-";
    }
}
