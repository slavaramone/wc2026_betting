namespace Wc26.Betting.Core.Models;

public sealed class Wc2026GroupSet
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourceKind { get; init; } = "inferred_from_group_stage_calendar_graph";
    public string SourceCalendarFile { get; init; } = string.Empty;
    public List<Wc2026Group> Groups { get; init; } = [];
}

public sealed class Wc2026Group
{
    public string GroupCode { get; init; } = string.Empty;
    public string InferenceMethod { get; init; } = "connected_component_of_group_stage_fixtures";
    public DateTimeOffset? FirstMatchUtc { get; init; }
    public List<Wc2026GroupTeam> Teams { get; init; } = [];
    public List<Wc2026GroupMatch> Matches { get; init; } = [];
}

public sealed class Wc2026GroupTeam
{
    public string TeamName { get; init; } = string.Empty;
    public int MatchCount { get; init; }
    public List<string> Opponents { get; init; } = [];
}

public sealed class Wc2026GroupMatch
{
    public long EventId { get; init; }
    public DateTimeOffset? StartUtc { get; init; }
    public int? RoundNumber { get; init; }
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public string StatusType { get; init; } = string.Empty;
}
