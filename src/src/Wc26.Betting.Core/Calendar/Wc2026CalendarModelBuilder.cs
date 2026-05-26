using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Sofascore;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Calendar;

public sealed class Wc2026CalendarModelBuilder
{
    public Wc2026CalendarSet BuildFromSofascoreFolder(string sofascoreFolder)
    {
        if (string.IsNullOrWhiteSpace(sofascoreFolder))
            throw new ArgumentException("SofaScore folder is required.");
        if (!Directory.Exists(sofascoreFolder))
            throw new DirectoryNotFoundException($"SofaScore folder not found: {sofascoreFolder}");

        var calendarFiles = Directory.EnumerateFiles(sofascoreFolder, "calendar.json", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (calendarFiles.Count == 0)
            throw new InvalidOperationException($"No calendar.json files found under {sofascoreFolder}.");

        var matches = new List<Wc2026CalendarMatch>();
        foreach (var calendarFile in calendarFiles)
        {
            var json = File.ReadAllText(calendarFile);
            var summaries = SofascoreEventSummary.FromCalendarJson(json);
            var roundFolder = new DirectoryInfo(Path.GetDirectoryName(calendarFile) ?? string.Empty).Name;
            var stage = InferStage(roundFolder);
            var roundSlug = InferRoundSlug(roundFolder);
            var relativeFile = Path.GetRelativePath(sofascoreFolder, calendarFile);

            foreach (var item in summaries)
            {
                matches.Add(new Wc2026CalendarMatch
                {
                    EventId = item.EventId,
                    Slug = item.Slug,
                    HomeTeam = item.HomeTeam,
                    AwayTeam = item.AwayTeam,
                    Stage = stage,
                    RoundNumber = item.Round ?? InferRoundNumber(roundFolder),
                    RoundFolder = roundFolder,
                    RoundSlug = roundSlug,
                    StartTimestamp = item.StartTimestamp,
                    StartUtc = item.StartTimestamp.HasValue ? DateTimeOffset.FromUnixTimeSeconds(item.StartTimestamp.Value) : null,
                    StatusType = item.StatusType,
                    StatusDescription = item.StatusDescription,
                    TournamentName = item.TournamentName,
                    TournamentSlug = item.TournamentSlug,
                    SeasonName = item.SeasonName,
                    SeasonYear = item.SeasonYear,
                    SourceCalendarFile = relativeFile
                });
            }
        }

        return new Wc2026CalendarSet
        {
            SourceFolder = sofascoreFolder,
            Matches = matches
                .GroupBy(x => x.EventId)
                .Select(g => g.OrderBy(x => x.SourceCalendarFile).First())
                .OrderBy(x => x.StartUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.EventId)
                .ToList()
        };
    }

    public async Task WriteAsync(Wc2026CalendarSet set, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputFolder, "calendar");
        Directory.CreateDirectory(folder);

        await WriteJsonAsync(Path.Combine(folder, "wc2026-calendar.json"), set, overwrite, cancellationToken);
        await WriteCalendarCsvAsync(Path.Combine(folder, "wc2026-calendar.csv"), set, overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, options), cancellationToken);
    }

    private static async Task WriteCalendarCsvAsync(string path, Wc2026CalendarSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("event_id,start_utc,stage,round_number,round_folder,home_team,away_team,status_type,status_description,slug,source_calendar_file");
        foreach (var m in set.Matches)
        {
            var values = new[]
            {
                m.EventId.ToString(),
                m.StartUtc?.ToString("O") ?? string.Empty,
                m.Stage,
                m.RoundNumber?.ToString() ?? string.Empty,
                m.RoundFolder,
                m.HomeTeam,
                m.AwayTeam,
                m.StatusType,
                m.StatusDescription,
                m.Slug,
                m.SourceCalendarFile
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static string InferStage(string roundFolder)
    {
        var value = roundFolder.ToLowerInvariant();
        if (value.Contains("round-of-32")) return "round_of_32";
        if (value.Contains("round-of-16")) return "round_of_16";
        if (value.Contains("quarter")) return "quarterfinal";
        if (value.Contains("semi")) return "semifinal";
        if (value.Contains("3rd") || value.Contains("third")) return "third_place";
        if (value.Contains("final")) return "final";
        return "group_stage";
    }

    private static string InferRoundSlug(string roundFolder)
    {
        var parts = roundFolder.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? string.Empty : string.Join('-', parts.Skip(2));
    }

    private static int? InferRoundNumber(string roundFolder)
    {
        var value = roundFolder.ToLowerInvariant().Replace("round-", string.Empty);
        var numberPart = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(numberPart, out var parsed) ? parsed : null;
    }
}
