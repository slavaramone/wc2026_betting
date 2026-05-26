namespace Wc26.Betting.Core.Sofascore;

public sealed record SofascoreGrabRequest(
    string OutputDirectory,
    int TournamentId,
    int SeasonId,
    IReadOnlyList<SofascoreRoundRequest> Rounds,
    bool Overwrite,
    bool DownloadIncidents,
    bool DownloadStatistics,
    bool SkipDetailsForNotStartedEvents,
    bool StrictEventDetails,
    bool Headless,
    int DelayMs,
    int WarmupDelayMs,
    string WarmupUrl,
    string UserAgent);

public sealed record SofascoreRoundRequest(int Round, string? Slug)
{
    public string CalendarPathSegment => string.IsNullOrWhiteSpace(Slug)
        ? $"round/{Round}"
        : $"round/{Round}/slug/{Slug.Trim()}";

    public string FolderName => string.IsNullOrWhiteSpace(Slug)
        ? $"round-{Round:00}"
        : $"round-{Round:00}-{FileNameSanitizer.Slugify(Slug)}";
}
