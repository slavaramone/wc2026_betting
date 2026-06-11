namespace Wc26.Betting.ScoreModel.GroupPropComparison;

public sealed class GroupPropMarketComparisonSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourcePropProbabilitiesFile { get; init; } = string.Empty;
    public string SourcePropOddsFile { get; init; } = string.Empty;
    public int OddsRows { get; init; }
    public int ComparedRows { get; init; }
    public int MissingModelRows { get; init; }
    public List<GroupPropMarketComparisonRow> Rows { get; init; } = [];
    public List<GroupPropComparisonDiagnostic> Diagnostics { get; init; } = [];
    public List<string> ValidationErrors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class GroupPropMarketComparisonRow
{
    public string GroupCode { get; init; } = string.Empty;
    public int MarketOrder { get; init; }
    public string BookMarketType { get; init; } = string.Empty;
    public string ModelMarketType { get; init; } = string.Empty;
    public string MarketNameRu { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public double Line { get; init; }
    public string Side { get; init; } = string.Empty;
    public double BookOdds { get; init; }
    public double BookNoVigProbability { get; init; }
    public double ModelProbability { get; init; }
    public double ModelFairOdds { get; init; }
    public double Edge { get; init; }
    public double RoiAtBookOdds { get; init; }
    public double MeanValue { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Warning { get; init; } = string.Empty;
}

public sealed class GroupPropComparisonDiagnostic
{
    public string GroupCode { get; init; } = string.Empty;
    public int OddsRows { get; init; }
    public int ComparedRows { get; init; }
    public int MissingModelRows { get; init; }
    public int BetRows { get; init; }
    public double MaxEdge { get; init; }
    public double MaxRoiAtBookOdds { get; init; }
    public string Status { get; init; } = "valid";
    public string Warning { get; init; } = string.Empty;
}

internal sealed class GroupPropOddsRow
{
    public string GroupCode { get; init; } = string.Empty;
    public int MarketOrder { get; init; }
    public string MarketType { get; init; } = string.Empty;
    public string MarketNameRu { get; init; } = string.Empty;
    public double Line { get; init; }
    public double OverOdds { get; init; }
    public double UnderOdds { get; init; }
    public double OverNoVigProbability { get; init; }
    public double UnderNoVigProbability { get; init; }
}

internal sealed record ModelPropKey(string GroupCode, string MarketType, string Subject, double Line, string Side)
{
    public static ModelPropKey Create(string groupCode, string marketType, string subject, double line, string side)
    {
        return new ModelPropKey(
            Normalize(groupCode),
            Normalize(marketType),
            Normalize(subject),
            Math.Round(line, 4),
            Normalize(side));
    }

    private static string Normalize(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

internal sealed record MappedMarket(string ModelMarketType, string Subject);
