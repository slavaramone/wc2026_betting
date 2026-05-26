namespace Wc26.Betting.Core.Sofascore;

public sealed class SofascoreGrabber
{
    private readonly TextWriter _log;

    public SofascoreGrabber(TextWriter? log = null)
    {
        _log = log ?? TextWriter.Null;
    }

    public async Task<SofascoreGrabResult> GrabAsync(
        SofascoreGrabRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        Directory.CreateDirectory(request.OutputDirectory);

        var internalResult = new DownloadCounters();

        await using var client = await SofascoreClient.CreateAsync(request, _log, cancellationToken);
        var fileStore = new SofascoreJsonFileStore();

        var baseFolder = Path.Combine(request.OutputDirectory, $"tournament-{request.TournamentId}", $"season-{request.SeasonId}");

        foreach (var round in request.Rounds.DistinctBy(x => (x.Round, x.Slug)).OrderBy(x => x.Round))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var roundFolder = Path.Combine(baseFolder, round.FolderName);
            var eventsFolder = Path.Combine(roundFolder, "events");

            await _log.WriteLineAsync($"Round {round.Round}{FormatSlug(round.Slug)}: downloading calendar...");

            string calendarJson;
            try
            {
                calendarJson = await client.GetCalendarAsync(request, round, cancellationToken);
            }
            catch (Exception ex)
            {
                var failure = $"Round {round.Round}{FormatSlug(round.Slug)} calendar failed: {ex.Message}";
                internalResult.Failures.Add(failure);
                await _log.WriteLineAsync($"  ERROR: {failure}");
                continue;
            }

            Count(await fileStore.WriteJsonAsync(Path.Combine(roundFolder, "calendar.json"), calendarJson, request.Overwrite, cancellationToken), internalResult);

            var events = SofascoreEventSummary.FromCalendarJson(calendarJson);
            internalResult.RoundsDownloaded++;
            internalResult.EventsDiscovered += events.Count;

            await _log.WriteLineAsync($"  Events: {events.Count}");

            var manifest = new SofascoreRoundManifest
            {
                TournamentId = request.TournamentId,
                SeasonId = request.SeasonId,
                Round = round.Round,
                Slug = round.Slug ?? string.Empty,
                DownloadedAtUtc = DateTimeOffset.UtcNow,
                EventCount = events.Count,
                Events = events.Select(e => new SofascoreRoundManifestEvent
                {
                    EventId = e.EventId,
                    Slug = e.Slug,
                    HomeTeam = e.HomeTeam,
                    AwayTeam = e.AwayTeam,
                    StartTimestamp = e.StartTimestamp,
                    StatusType = e.StatusType,
                    StatusDescription = e.StatusDescription,
                    TournamentName = e.TournamentName,
                    TournamentSlug = e.TournamentSlug,
                    SeasonName = e.SeasonName,
                    SeasonYear = e.SeasonYear,
                    Round = e.Round,
                    Folder = Path.Combine("events", e.EventId.ToString())
                }).ToList()
            };

            Count(await fileStore.WriteObjectAsync(Path.Combine(roundFolder, "manifest.json"), manifest, request.Overwrite, cancellationToken), internalResult);

            foreach (var eventSummary in events)
            {
                var eventFolder = Path.Combine(eventsFolder, eventSummary.EventId.ToString());
                Count(await fileStore.WriteObjectAsync(Path.Combine(eventFolder, "event-meta.json"), eventSummary, request.Overwrite, cancellationToken), internalResult);

                var skipEventDetails = request.SkipDetailsForNotStartedEvents && IsNotStartedOrFutureFixture(eventSummary);
                if (skipEventDetails)
                {
                    var warning = $"event {eventSummary.EventId} {eventSummary.HomeTeam} vs {eventSummary.AwayTeam}: status '{eventSummary.StatusType}' - calendar/event-meta saved, incidents/statistics skipped";
                    internalResult.Warnings.Add(warning);
                    await _log.WriteLineAsync($"    SKIP details: {warning}");
                }
                else
                {
                    if (request.DownloadIncidents)
                    {
                        await DownloadEventEndpoint(
                            endpointName: "incidents",
                            targetPath: Path.Combine(eventFolder, "incidents.json"),
                            download: ct => client.GetIncidentsAsync(eventSummary.EventId, ct),
                            request,
                            internalResult,
                            cancellationToken);
                    }

                    if (request.DownloadStatistics)
                    {
                        await DownloadEventEndpoint(
                            endpointName: "statistics",
                            targetPath: Path.Combine(eventFolder, "statistics.json"),
                            download: ct => client.GetStatisticsAsync(eventSummary.EventId, ct),
                            request,
                            internalResult,
                            cancellationToken);
                    }
                }

                if (request.DelayMs > 0)
                    await Task.Delay(request.DelayMs, cancellationToken);
            }
        }

        var status = internalResult.Failures.Count == 0 ? "completed" : "completed-with-failures";
        var message = $"Downloaded rounds={internalResult.RoundsDownloaded}, events={internalResult.EventsDiscovered}, filesWritten={internalResult.FilesWritten}, filesSkipped={internalResult.FilesSkipped}, warnings={internalResult.Warnings.Count}, failures={internalResult.Failures.Count}.";

        return new SofascoreGrabResult(
            Status: status,
            OutputPath: baseFolder,
            Message: message,
            RoundsDownloaded: internalResult.RoundsDownloaded,
            EventsDiscovered: internalResult.EventsDiscovered,
            FilesWritten: internalResult.FilesWritten,
            FilesSkipped: internalResult.FilesSkipped,
            Warnings: internalResult.Warnings,
            Failures: internalResult.Failures);
    }

    private async Task DownloadEventEndpoint(
        string endpointName,
        string targetPath,
        Func<CancellationToken, Task<string>> download,
        SofascoreGrabRequest request,
        DownloadCounters result,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) && !request.Overwrite)
        {
            result.FilesSkipped++;
            return;
        }

        try
        {
            var fileStore = new SofascoreJsonFileStore();
            var json = await download(cancellationToken);
            Count(await fileStore.WriteJsonAsync(targetPath, json, request.Overwrite, cancellationToken), result);
            await _log.WriteLineAsync($"    saved {endpointName}: {targetPath}");
        }
        catch (Exception ex)
        {
            var message = $"{targetPath}: {ex.Message}";

            if (request.StrictEventDetails)
            {
                result.Failures.Add(message);
                await _log.WriteLineAsync($"    ERROR {endpointName}: {ex.Message}");
            }
            else
            {
                result.Warnings.Add(message);
                await _log.WriteLineAsync($"    WARN {endpointName}: {ex.Message}");
            }
        }
    }

    private static bool IsNotStartedOrFutureFixture(SofascoreEventSummary eventSummary)
    {
        var status = (eventSummary.StatusType ?? string.Empty).Trim().ToLowerInvariant();
        return status is "notstarted" or "not_started" or "scheduled" or "postponed" or "canceled" or "cancelled";
    }

    private static void Count(FileWriteResult fileResult, DownloadCounters result)
    {
        if (fileResult.WasWritten)
            result.FilesWritten++;
        else
            result.FilesSkipped++;
    }

    private static void Validate(SofascoreGrabRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new ArgumentException("OutputDirectory is required.");
        if (request.TournamentId <= 0)
            throw new ArgumentException("TournamentId must be positive.");
        if (request.SeasonId <= 0)
            throw new ArgumentException("SeasonId must be positive.");
        if (request.Rounds.Count == 0)
            throw new ArgumentException("At least one round is required.");
        if (request.DelayMs < 0)
            throw new ArgumentException("DelayMs cannot be negative.");
    }

    private static string FormatSlug(string? slug)
        => string.IsNullOrWhiteSpace(slug) ? string.Empty : $"/{slug}";

    private sealed class DownloadCounters
    {
        public int RoundsDownloaded { get; set; }
        public int EventsDiscovered { get; set; }
        public int FilesWritten { get; set; }
        public int FilesSkipped { get; set; }
        public List<string> Warnings { get; } = [];
        public List<string> Failures { get; } = [];
    }
}

public sealed class SofascoreRoundManifest
{
    public int TournamentId { get; init; }
    public int SeasonId { get; init; }
    public int Round { get; init; }
    public string Slug { get; init; } = string.Empty;
    public DateTimeOffset DownloadedAtUtc { get; init; }
    public int EventCount { get; init; }
    public List<SofascoreRoundManifestEvent> Events { get; init; } = [];
}

public sealed class SofascoreRoundManifestEvent
{
    public long EventId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public long? StartTimestamp { get; init; }
    public string StatusType { get; init; } = string.Empty;
    public string StatusDescription { get; init; } = string.Empty;
    public string TournamentName { get; init; } = string.Empty;
    public string TournamentSlug { get; init; } = string.Empty;
    public string SeasonName { get; init; } = string.Empty;
    public string SeasonYear { get; init; } = string.Empty;
    public int? Round { get; init; }
    public string Folder { get; init; } = string.Empty;
}
