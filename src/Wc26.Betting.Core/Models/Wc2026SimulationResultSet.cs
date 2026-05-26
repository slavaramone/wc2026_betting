namespace Wc26.Betting.Core.Models;

public sealed class Wc2026SimulationResultSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceKind { get; init; } = "monte_carlo_group_stage_skeleton";
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public string ModelsFolder { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public List<Wc2026SimulationTeamSummary> Teams { get; init; } = [];
    public List<Wc2026SimulationGroupSummary> Groups { get; init; } = [];
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
