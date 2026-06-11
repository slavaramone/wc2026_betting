namespace Wc26.Betting.ScoreModel.MarketXg;

public sealed class MarketImpliedXgSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceOddsFile { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public int ValidFixtureCount { get; init; }
    public int InvalidFixtureCount { get; init; }
    public int MaxGoalsUsed { get; init; }
    public List<MarketImpliedXgFixture> Fixtures { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class MarketImpliedXgFixture
{
    public string MatchKey { get; init; } = string.Empty;
    public string MatchDate { get; init; } = string.Empty;
    public string MatchTime { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public string MatchStatus { get; init; } = string.Empty;
    public long? CalendarEventId { get; init; }

    public string TeamA { get; init; } = string.Empty;
    public string TeamB { get; init; } = string.Empty;
    public string NormalizedTeamA { get; init; } = string.Empty;
    public string NormalizedTeamB { get; init; } = string.Empty;

    public double Odds1 { get; init; }
    public double OddsX { get; init; }
    public double Odds2 { get; init; }
    public double RawP1 { get; init; }
    public double RawPX { get; init; }
    public double RawP2 { get; init; }
    public double Overround1X2 { get; init; }
    public double NoVigP1 { get; init; }
    public double NoVigPX { get; init; }
    public double NoVigP2 { get; init; }

    public double TotalLine { get; init; }
    public double OverOdds { get; init; }
    public double UnderOdds { get; init; }
    public double RawPOver { get; init; }
    public double RawPUnder { get; init; }
    public double TotalOverround { get; init; }
    public double NoVigPOver { get; init; }
    public double NoVigPUnder { get; init; }

    public double TotalLambda { get; init; }
    public double TeamAShare { get; init; }
    public double TeamAXg { get; init; }
    public double TeamBXg { get; init; }

    public double ModelP1 { get; init; }
    public double ModelPX { get; init; }
    public double ModelP2 { get; init; }
    public double ModelPOver { get; init; }
    public double ModelPUnder { get; init; }

    public double ErrorP1 { get; init; }
    public double ErrorPX { get; init; }
    public double ErrorP2 { get; init; }
    public double ErrorOver { get; init; }
    public double ObjectiveError { get; init; }

    public string Status { get; init; } = "valid";
    public string Warning { get; init; } = string.Empty;
}
