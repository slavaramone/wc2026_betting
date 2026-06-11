namespace Wc26.Betting.ScoreModel.ScoreMatrix;

public sealed class FixtureScoreMatrixSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceMarketXgFile { get; init; } = string.Empty;
    public int FixtureCount { get; init; }
    public int ValidFixtureCount { get; init; }
    public int InvalidFixtureCount { get; init; }
    public int MaxGoals { get; init; }
    public string CalibrationMode { get; init; } = "None";
    public double P00Multiplier { get; init; } = 1.0d;
    public double P11Multiplier { get; init; } = 1.0d;
    public double P10Or01Multiplier { get; init; } = 1.0d;
    public double P21Or12Multiplier { get; init; } = 1.0d;
    public List<FixtureScoreMatrix> Fixtures { get; init; } = [];
    public List<FixtureScoreMatrixDiagnostic> Diagnostics { get; init; } = [];
    public List<string> ValidationErrors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class FixtureScoreMatrix
{
    public string MatchKey { get; init; } = string.Empty;
    public string MatchDate { get; init; } = string.Empty;
    public string MatchTime { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public string TeamA { get; init; } = string.Empty;
    public string TeamB { get; init; } = string.Empty;
    public double TeamAXg { get; init; }
    public double TeamBXg { get; init; }
    public double TotalLambda { get; init; }
    public double TotalLine { get; init; }
    public double TargetP1 { get; init; }
    public double TargetPX { get; init; }
    public double TargetP2 { get; init; }
    public double TargetPOver { get; init; }
    public string CalibrationMode { get; init; } = "None";
    public double GridMass { get; init; }
    public string Status { get; init; } = "valid";
    public string Warning { get; init; } = string.Empty;
    public List<FixtureScoreProbability> Scores { get; init; } = [];
}

public sealed class FixtureScoreProbability
{
    public int ScoreA { get; init; }
    public int ScoreB { get; init; }
    public double RawProbability { get; init; }
    public double Probability { get; init; }
}

public sealed class FixtureScoreMatrixDiagnostic
{
    public string GroupCode { get; init; } = string.Empty;
    public string TeamA { get; init; } = string.Empty;
    public string TeamB { get; init; } = string.Empty;
    public double TeamAXg { get; init; }
    public double TeamBXg { get; init; }
    public double TotalLambda { get; init; }
    public double TotalLine { get; init; }
    public double TargetP1 { get; init; }
    public double TargetPX { get; init; }
    public double TargetP2 { get; init; }
    public double TargetPOver { get; init; }
    public string CalibrationMode { get; init; } = "None";
    public double GridMass { get; init; }
    public double ProbabilitySum { get; init; }
    public double P1 { get; init; }
    public double PX { get; init; }
    public double P2 { get; init; }
    public double POver { get; init; }
    public double PUnder { get; init; }
    public double P00 { get; init; }
    public double P10Or01 { get; init; }
    public double P21Or12 { get; init; }
    public double P22 { get; init; }
    public double P32Or23 { get; init; }
    public double DrawCalibrationError { get; init; }
    public string Status { get; init; } = "valid";
    public string Warning { get; init; } = string.Empty;
}
