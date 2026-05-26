namespace Wc26.Betting.Core.Models;

public sealed class GameOddsSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceFile { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public List<GameOddsMatch> Matches { get; init; } = [];
}

public sealed class GameOddsMatch
{
    public string MatchKey { get; init; } = string.Empty;
    public string MatchDate { get; init; } = string.Empty;
    public string MatchTime { get; init; } = string.Empty;

    public string HomeTeamRaw { get; init; } = string.Empty;
    public string AwayTeamRaw { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string NormalizedHomeTeam { get; init; } = string.Empty;
    public string NormalizedAwayTeam { get; init; } = string.Empty;

    public long? CalendarEventId { get; init; }
    public string CalendarStage { get; init; } = string.Empty;
    public string CalendarGroupCode { get; init; } = string.Empty;
    public DateTimeOffset? CalendarStartUtc { get; init; }
    public string MatchStatus { get; init; } = "unmatched";

    public double? Odds1 { get; init; }
    public double? OddsX { get; init; }
    public double? Odds2 { get; init; }
    public double? Odds1X2Overround { get; init; }

    public double? Handicap1Line { get; init; }
    public double? Handicap1Odds { get; init; }
    public double? Handicap2Line { get; init; }
    public double? Handicap2Odds { get; init; }

    public double? TotalLine { get; init; }
    public double? OverOdds { get; init; }
    public double? UnderOdds { get; init; }
    public double? TotalOverround { get; init; }

    public double? BttsYesOdds { get; init; }
    public double? BttsNoOdds { get; init; }
    public double? BttsOverround { get; init; }

    public string SourceImage { get; init; } = string.Empty;
}
