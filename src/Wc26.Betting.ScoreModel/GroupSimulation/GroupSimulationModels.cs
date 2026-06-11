namespace Wc26.Betting.ScoreModel.GroupSimulation;

public sealed class GroupPropSimulationSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceScoreMatrixFile { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int GroupCount { get; init; }
    public int FixtureCount { get; init; }
    public int ValidFixtureCount { get; init; }
    public List<GroupPropProbability> PropProbabilities { get; init; } = [];
    public List<GroupSimulationDiagnostic> Diagnostics { get; init; } = [];
    public List<string> ValidationErrors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class GroupPropProbability
{
    public string GroupCode { get; init; } = string.Empty;
    public string MarketType { get; init; } = string.Empty;
    public string MarketName { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public double Line { get; init; }
    public double ModelProbability { get; init; }
    public double FairOdds { get; init; }
    public double MeanValue { get; init; }
}

public sealed class GroupSimulationDiagnostic
{
    public string GroupCode { get; init; } = string.Empty;
    public int FixtureCount { get; init; }
    public int TeamCount { get; init; }
    public string Teams { get; init; } = string.Empty;
    public double AvgGoals { get; init; }
    public double AvgDraws { get; init; }
    public double AvgZeroZeroMatches { get; init; }
    public double AvgOneNilMatches { get; init; }
    public double AvgTwoOneMatches { get; init; }
    public double AvgTwoTwoMatches { get; init; }
    public double AvgThreeTwoMatches { get; init; }
    public double AvgTeamsScoredZeroGoals { get; init; }
    public double AvgTeamsConcededZeroGoals { get; init; }
    public double AvgTeamsWith9Points { get; init; }
    public double AvgTeamsWith0Points { get; init; }
    public double AvgFirstPlacePoints { get; init; }
    public double AvgSecondPlacePoints { get; init; }
    public double AvgThirdPlacePoints { get; init; }
    public double AvgFourthPlacePoints { get; init; }
    public string Status { get; init; } = "valid";
    public string Warning { get; init; } = string.Empty;
}

internal sealed class SimulatedGroupSnapshot
{
    public int TotalGoals { get; init; }
    public int DrawCount { get; init; }
    public int ZeroZeroCount { get; init; }
    public int OneNilCount { get; init; }
    public int TwoOneCount { get; init; }
    public int TwoTwoCount { get; init; }
    public int ThreeTwoCount { get; init; }
    public int TeamsScoredZeroGoals { get; init; }
    public int TeamsConcededZeroGoals { get; init; }
    public int TeamsWith9Points { get; init; }
    public int TeamsWith0Points { get; init; }
    public int FirstPlacePoints { get; init; }
    public int SecondPlacePoints { get; init; }
    public int ThirdPlacePoints { get; init; }
    public int FourthPlacePoints { get; init; }
}
