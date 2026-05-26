namespace Wc26.Betting.Core.Models;

public sealed class EaFcPlayerRatingSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceFile { get; init; } = string.Empty;
    public string SourceCsvName { get; init; } = string.Empty;
    public string SourceKind { get; init; } = "EAFC26-Men Kaggle CSV";
    public int RowCount { get; init; }
    public List<EaFcPlayerRating> Players { get; init; } = [];
}

public sealed class EaFcPlayerRating
{
    public string SourcePlayerId { get; init; } = string.Empty;
    public int? Rank { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public int Overall { get; init; }
    public int? Pace { get; init; }
    public int? Shooting { get; init; }
    public int? Passing { get; init; }
    public int? Dribbling { get; init; }
    public int? Defending { get; init; }
    public int? Physical { get; init; }
    public string Position { get; init; } = string.Empty;
    public string AlternativePositions { get; init; } = string.Empty;
    public int? Age { get; init; }
    public string Nation { get; init; } = string.Empty;
    public string League { get; init; } = string.Empty;
    public string Club { get; init; } = string.Empty;
    public string PreferredFoot { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public string PositionGroup => PositionClassifier.ToPositionGroup(Position, AlternativePositions);
}

public sealed class NationRatingSeed
{
    public string Nation { get; init; } = string.Empty;
    public int PlayerCount { get; init; }
    public int Top26Count { get; init; }
    public double Top26AverageOverall { get; init; }
    public double Top11AverageOverall { get; init; }
    public double AttackRating { get; init; }
    public double MidfieldRating { get; init; }
    public double DefenceRating { get; init; }
    public double GoalkeeperRating { get; init; }
    public double BenchRating { get; init; }
    public string Confidence { get; init; } = string.Empty;
}

public static class PositionClassifier
{
    public static string ToPositionGroup(string primaryPosition, string alternativePositions)
    {
        var value = ($"{primaryPosition} {alternativePositions}").ToUpperInvariant();
        if (value.Contains("GK")) return "GK";
        if (value.Contains("CB") || value.Contains("LB") || value.Contains("RB") || value.Contains("LWB") || value.Contains("RWB")) return "DEF";
        if (value.Contains("CDM") || value.Contains("CM") || value.Contains("CAM") || value.Contains("LM") || value.Contains("RM")) return "MID";
        if (value.Contains("ST") || value.Contains("CF") || value.Contains("LW") || value.Contains("RW")) return "ATT";
        return "UNK";
    }
}
