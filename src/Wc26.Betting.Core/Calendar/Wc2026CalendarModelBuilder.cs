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

        var groups = BuildGroups(set);
        await WriteJsonAsync(Path.Combine(folder, "wc2026-groups.json"), groups, overwrite, cancellationToken);
        await WriteGroupsCsvAsync(Path.Combine(folder, "wc2026-groups.csv"), groups, overwrite, cancellationToken);
        await WriteGroupMatchesCsvAsync(Path.Combine(folder, "wc2026-group-matches.csv"), groups, overwrite, cancellationToken);
    }

    public Wc2026GroupSet BuildGroups(Wc2026CalendarSet calendar)
    {
        var groupStageMatches = calendar.Matches
            .Where(x => x.Stage == "group_stage" && x.HasKnownTeams)
            .OrderBy(x => x.StartUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.EventId)
            .ToList();

        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in groupStageMatches)
        {
            AddEdge(adjacency, match.HomeTeam, match.AwayTeam);
            AddEdge(adjacency, match.AwayTeam, match.HomeTeam);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<List<string>>();
        foreach (var team in adjacency.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!visited.Add(team))
                continue;

            var component = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(team);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                foreach (var next in adjacency[current].OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }

            components.Add(component.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }

        var orderedComponents = components
            .Select(component => new
            {
                Teams = component,
                Matches = groupStageMatches
                    .Where(m => component.Contains(m.HomeTeam, StringComparer.OrdinalIgnoreCase)
                        && component.Contains(m.AwayTeam, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(m => m.StartUtc ?? DateTimeOffset.MaxValue)
                    .ThenBy(m => m.EventId)
                    .ToList()
            })
            .OrderBy(x => x.Matches.FirstOrDefault()?.StartUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(x => string.Join("|", x.Teams), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<Wc2026Group>();
        for (var i = 0; i < orderedComponents.Count; i++)
        {
            var component = orderedComponents[i];
            var groupCode = i < 12 ? ((char)('A' + i)).ToString() : $"X{i + 1}";
            var teams = component.Teams
                .Select(team => new Wc2026GroupTeam
                {
                    TeamName = team,
                    MatchCount = component.Matches.Count(m => string.Equals(m.HomeTeam, team, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.AwayTeam, team, StringComparison.OrdinalIgnoreCase)),
                    Opponents = component.Matches
                        .Where(m => string.Equals(m.HomeTeam, team, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.AwayTeam, team, StringComparison.OrdinalIgnoreCase))
                        .Select(m => string.Equals(m.HomeTeam, team, StringComparison.OrdinalIgnoreCase) ? m.AwayTeam : m.HomeTeam)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .OrderBy(x => x.TeamName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(new Wc2026Group
            {
                GroupCode = groupCode,
                FirstMatchUtc = component.Matches.FirstOrDefault()?.StartUtc,
                Teams = teams,
                Matches = component.Matches.Select(m => new Wc2026GroupMatch
                {
                    EventId = m.EventId,
                    StartUtc = m.StartUtc,
                    RoundNumber = m.RoundNumber,
                    HomeTeam = m.HomeTeam,
                    AwayTeam = m.AwayTeam,
                    StatusType = m.StatusType
                }).ToList()
            });
        }

        return new Wc2026GroupSet
        {
            SourceCalendarFile = "calendar/wc2026-calendar.json",
            Groups = groups
        };
    }

    private static void AddEdge(Dictionary<string, HashSet<string>> adjacency, string from, string to)
    {
        if (!adjacency.TryGetValue(from, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            adjacency[from] = set;
        }

        set.Add(to);
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


    private static async Task WriteGroupsCsvAsync(string path, Wc2026GroupSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("group_code,team_name,match_count,opponents");
        foreach (var group in set.Groups.OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var team in group.Teams.OrderBy(x => x.TeamName, StringComparer.OrdinalIgnoreCase))
            {
                var values = new[]
                {
                    group.GroupCode,
                    team.TeamName,
                    team.MatchCount.ToString(),
                    string.Join('|', team.Opponents)
                };
                await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
            }
        }
    }

    private static async Task WriteGroupMatchesCsvAsync(string path, Wc2026GroupSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("group_code,event_id,start_utc,round_number,home_team,away_team,status_type");
        foreach (var group in set.Groups.OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var match in group.Matches.OrderBy(x => x.StartUtc ?? DateTimeOffset.MaxValue).ThenBy(x => x.EventId))
            {
                var values = new[]
                {
                    group.GroupCode,
                    match.EventId.ToString(),
                    match.StartUtc?.ToString("O") ?? string.Empty,
                    match.RoundNumber?.ToString() ?? string.Empty,
                    match.HomeTeam,
                    match.AwayTeam,
                    match.StatusType
                };
                await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
            }
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
