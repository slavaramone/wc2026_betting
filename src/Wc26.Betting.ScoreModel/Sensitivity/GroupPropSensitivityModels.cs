using Wc26.Betting.ScoreModel.GroupPropComparison;
using Wc26.Betting.ScoreModel.ScoreMatrix;

namespace Wc26.Betting.ScoreModel.Sensitivity;

public sealed class GroupPropSensitivitySet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceMarketXgFile { get; init; } = string.Empty;
    public string SourcePropOddsFile { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int ScenarioCount { get; init; }
    public List<GroupPropSensitivityScenarioSummary> Scenarios { get; init; } = [];
    public List<GroupPropSensitivityRow> Rows { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class GroupPropSensitivityScenarioSummary
{
    public string Scenario { get; init; } = string.Empty;
    public int ComparedRows { get; init; }
    public int BetRows { get; init; }
    public int MissingModelRows { get; init; }
    public double MaxRoiAtBookOdds { get; init; }
}

public sealed class GroupPropSensitivityRow
{
    public string GroupCode { get; init; } = string.Empty;
    public int MarketOrder { get; init; }
    public string BookMarketType { get; init; } = string.Empty;
    public string MarketNameRu { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public double Line { get; init; }
    public string Side { get; init; } = string.Empty;
    public double BookOdds { get; init; }
    public double BookNoVigProbability { get; init; }

    public double BaseProbability { get; init; }
    public double BaseRoi { get; init; }
    public double DrawMatchProbability { get; init; }
    public double DrawMatchRoi { get; init; }
    public double LowScoreBoostProbability { get; init; }
    public double LowScoreBoostRoi { get; init; }
    public double DrawAndLowScoreProbability { get; init; }
    public double DrawAndLowScoreRoi { get; init; }

    public double MinProbabilityCore { get; init; }
    public double MinRoiCore { get; init; }
    public double MaxRoiCore { get; init; }
    public double ProbabilityRangeCore { get; init; }
    public double MinEdgeCore { get; init; }
    public string StabilityDecision { get; init; } = string.Empty;
    public string Warning { get; init; } = string.Empty;
}

internal sealed record SensitivityScenario(string Name, ScoreMatrixCalibrationOptions Options);

internal sealed record SensitivityKey(
    string GroupCode,
    int MarketOrder,
    string BookMarketType,
    string Subject,
    double Line,
    string Side)
{
    public static SensitivityKey Create(GroupPropMarketComparisonRow row)
    {
        return new SensitivityKey(
            Normalize(row.GroupCode),
            row.MarketOrder,
            Normalize(row.BookMarketType),
            Normalize(row.Subject),
            Math.Round(row.Line, 4),
            Normalize(row.Side));
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
