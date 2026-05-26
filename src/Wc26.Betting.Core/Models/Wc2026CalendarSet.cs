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
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        var lower = normalized.ToLowerInvariant();
        if (lower is "tbd" or "to be decided" or "winner" or "unknown" or "-")
            return true;

        // SofaScore publishes future knockout placeholders such as 1A, 2B, G1, H2, W100, L101
        // and best-third combinations such as 3A/3B/3C/3D/3F. These are bracket labels, not nations.
        if (IsRankGroupPlaceholder(normalized) || IsWinnerLoserPlaceholder(normalized))
            return true;

        if (normalized.Contains('/'))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 0 && parts.All(IsRankGroupPlaceholder);
        }

        return false;
    }

    private static bool IsRankGroupPlaceholder(string value)
    {
        if (value.Length != 2)
            return false;

        var first = char.ToUpperInvariant(value[0]);
        var second = char.ToUpperInvariant(value[1]);

        return ((first is '1' or '2' or '3') && second >= 'A' && second <= 'L')
            || (first >= 'A' && first <= 'L' && second is '1' or '2' or '3');
    }

    private static bool IsWinnerLoserPlaceholder(string value)
    {
        if (value.Length < 2)
            return false;

        var first = char.ToUpperInvariant(value[0]);
        return (first is 'W' or 'L') && value.Skip(1).All(char.IsDigit);
    }
}
