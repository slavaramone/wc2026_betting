namespace Wc26.Betting.Core.Models;

public sealed class Wc2026SimulationResultSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceKind { get; init; } = "monte_carlo_group_and_knockout_skeleton";
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public string ModelsFolder { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public string Notes { get; init; } = string.Empty;
    public bool KnockoutSimulated { get; init; }
    public string KnockoutBracketSource { get; init; } = string.Empty;
    public List<Wc2026KnockoutBracketRuleSummary> KnockoutBracketRules { get; init; } = [];
    public List<Wc2026SimulationTeamSummary> Teams { get; init; } = [];
    public List<Wc2026SimulationGroupSummary> Groups { get; init; } = [];
    public List<Wc2026SimulationPairComparisonSummary> PairComparisons { get; init; } = [];
}


public sealed class Wc2026KnockoutBracketRuleSummary
{
    public int MatchNumber { get; init; }
    public string Stage { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Slot1 { get; init; } = string.Empty;
    public string Slot2 { get; init; } = string.Empty;
    public string Slot1Groups { get; init; } = string.Empty;
    public string Slot2Groups { get; init; } = string.Empty;
}

public sealed class Wc2026SimulationPairComparisonSummary
{
    public string GroupCode { get; init; } = string.Empty;
    public string Team1 { get; init; } = string.Empty;
    public string Team2 { get; init; } = string.Empty;
    public double Team1FinishHigherProbability { get; init; }
    public double Team2FinishHigherProbability { get; init; }
}

public sealed class Wc2026SimulationTeamSummary
{
    public string Team { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public int EloRating { get; init; }
    public double EaTop11Rating { get; init; }
    public double EaTop26Rating { get; init; }
    public string EaConfidence { get; init; } = string.Empty;

    public double AvgPoints { get; init; }
    public double AvgGoalsFor { get; init; }
    public double AvgGoalsAgainst { get; init; }
    public double AvgGoalDifference { get; init; }
    public double WinGroupProbability { get; init; }
    public double TopTwoProbability { get; init; }
    public double ThirdPlaceProbability { get; init; }
    public double ThirdPlaceQualifiedProbability { get; init; }
    public double QualifiedToRoundOf32Probability { get; init; }
    public double EliminatedInGroupProbability { get; init; }
    public double ReachRoundOf16Probability { get; init; }
    public double ReachQuarterFinalProbability { get; init; }
    public double ReachSemiFinalProbability { get; init; }
    public double ReachFinalProbability { get; init; }
    public double WinnerProbability { get; init; }
}

public sealed class Wc2026SimulationGroupSummary
{
    public string GroupCode { get; init; } = string.Empty;
    public List<string> Teams { get; init; } = [];
    public List<Wc2026SimulationGroupTeamProbability> TeamProbabilities { get; init; } = [];
}

public sealed class Wc2026SimulationGroupTeamProbability
{
    public string Team { get; init; } = string.Empty;
    public double ExpectedRank { get; init; }
    public double Rank1Probability { get; init; }
    public double Rank2Probability { get; init; }
    public double Rank3Probability { get; init; }
    public double Rank4Probability { get; init; }
}
