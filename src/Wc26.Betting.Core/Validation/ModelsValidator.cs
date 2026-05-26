using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Players;

namespace Wc26.Betting.Core.Validation;

public sealed class ModelsValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<ModelValidationReport> ValidateAsync(string modelsFolder, bool writeReport, CancellationToken cancellationToken)
    {
        var report = new ModelValidationReport { ModelsFolder = modelsFolder };

        if (!Directory.Exists(modelsFolder))
        {
            report.Errors.Add($"Models folder not found: {modelsFolder}");
            return report;
        }

        var calendarPath = Path.Combine(modelsFolder, "calendar", "wc2026-calendar.json");
        var playerPath = Path.Combine(modelsFolder, "player-ratings", "eafc26-men-player-ratings.json");
        var seedsPath = Path.Combine(modelsFolder, "player-ratings", "eafc26-nation-rating-seeds.json");

        Wc2026CalendarSet? calendar = null;
        EaFcPlayerRatingSet? ratings = null;
        List<NationRatingSeed>? seeds = null;

        if (!File.Exists(calendarPath)) report.Errors.Add($"Missing calendar model: {calendarPath}");
        else calendar = await ReadAsync<Wc2026CalendarSet>(calendarPath, report, cancellationToken);

        if (!File.Exists(playerPath)) report.Errors.Add($"Missing EA player ratings model: {playerPath}");
        else ratings = await ReadAsync<EaFcPlayerRatingSet>(playerPath, report, cancellationToken);

        if (!File.Exists(seedsPath)) report.Warnings.Add($"Missing nation rating seed model: {seedsPath}");
        else seeds = await ReadAsync<List<NationRatingSeed>>(seedsPath, report, cancellationToken);

        if (calendar is not null) ValidateCalendar(calendar, report);
        if (ratings is not null) ValidateRatings(ratings, report);
        if (calendar is not null && ratings is not null) ValidateCalendarTeamCoverage(calendar, ratings, seeds, report);

        if (writeReport)
        {
            var folder = Path.Combine(modelsFolder, "validation");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "validation-report.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "validation-report.txt"), ToText(report), cancellationToken);
        }

        return report;
    }

    private static async Task<T?> ReadAsync<T>(string path, ModelValidationReport report, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"Failed to read {path}: {ex.Message}");
            return default;
        }
    }

    private static void ValidateCalendar(Wc2026CalendarSet calendar, ModelValidationReport report)
    {
        report.Info.Add($"Calendar matches: {calendar.Matches.Count}");

        if (calendar.Matches.Count == 0)
        {
            report.Errors.Add("Calendar model has zero matches.");
            return;
        }

        var duplicateEventIds = calendar.Matches.GroupBy(x => x.EventId).Where(g => g.Count() > 1).Select(g => g.Key).Take(20).ToList();
        if (duplicateEventIds.Count > 0)
            report.Errors.Add($"Duplicate event ids in calendar model: {string.Join(", ", duplicateEventIds)}");

        var missingTeams = calendar.Matches.Count(x => string.IsNullOrWhiteSpace(x.HomeTeam) || string.IsNullOrWhiteSpace(x.AwayTeam));
        if (missingTeams > 0)
            report.Warnings.Add($"Calendar matches with missing team names: {missingTeams}");

        var missingStart = calendar.Matches.Count(x => x.StartUtc is null);
        if (missingStart > 0)
            report.Warnings.Add($"Calendar matches with missing start time: {missingStart}");

        var groupStageCount = calendar.Matches.Count(x => x.Stage == "group_stage");
        var knockoutCount = calendar.Matches.Count - groupStageCount;
        report.Info.Add($"Calendar group-stage matches: {groupStageCount}; knockout/placement matches: {knockoutCount}");

        if (calendar.Matches.Count != 104)
            report.Warnings.Add($"Expected 104 WC2026 matches for a full model; current calendar has {calendar.Matches.Count}. This can be OK if SofaScore has not published all fixtures yet or you downloaded only selected rounds.");
        if (groupStageCount != 72)
            report.Warnings.Add($"Expected 72 group-stage matches for full WC2026; current group-stage count is {groupStageCount}.");

        var expectedStages = new[] { "group_stage", "round_of_32", "round_of_16", "quarterfinal", "semifinal", "third_place", "final" };
        foreach (var stage in expectedStages)
        {
            if (calendar.Matches.All(x => x.Stage != stage))
                report.Warnings.Add($"Calendar has no matches for stage '{stage}'.");
        }

        var invalidTimes = calendar.Matches
            .Where(x => x.StartUtc is not null && (x.StartUtc.Value.Year < 2026 || x.StartUtc.Value.Year > 2026))
            .Take(10)
            .Select(x => $"{x.EventId}:{x.StartUtc:O}")
            .ToList();
        if (invalidTimes.Count > 0)
            report.Warnings.Add($"Calendar has start times outside 2026: {string.Join(", ", invalidTimes)}");
    }

    private static void ValidateRatings(EaFcPlayerRatingSet ratings, ModelValidationReport report)
    {
        report.Info.Add($"EA ratings rows read: {ratings.RowCount}; players imported: {ratings.Players.Count}");

        if (ratings.Players.Count < 1000)
            report.Errors.Add($"EA ratings model has too few players: {ratings.Players.Count}");

        var duplicateIds = ratings.Players
            .Where(x => !string.IsNullOrWhiteSpace(x.SourcePlayerId))
            .GroupBy(x => x.SourcePlayerId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(20)
            .ToList();
        if (duplicateIds.Count > 0)
            report.Warnings.Add($"Duplicate EA player ids: {string.Join(", ", duplicateIds)}");

        var badOverall = ratings.Players.Count(x => x.Overall < 1 || x.Overall > 99);
        if (badOverall > 0)
            report.Errors.Add($"Players with invalid overall rating outside 1..99: {badOverall}");

        var missingNation = ratings.Players.Count(x => string.IsNullOrWhiteSpace(x.Nation));
        if (missingNation > 0)
            report.Warnings.Add($"Players with missing nation: {missingNation}");

        var missingPosition = ratings.Players.Count(x => string.IsNullOrWhiteSpace(x.Position));
        if (missingPosition > 0)
            report.Warnings.Add($"Players with missing primary position: {missingPosition}");

        var nationCount = ratings.Players.Select(x => x.Nation).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        report.Info.Add($"EA ratings nations: {nationCount}");

        var topNations = ratings.Players
            .Where(x => !string.IsNullOrWhiteSpace(x.Nation))
            .GroupBy(x => x.Nation)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key}={g.Count()}");
        report.Info.Add($"Top EA nations by player count: {string.Join(", ", topNations)}");
    }

    private static void ValidateCalendarTeamCoverage(Wc2026CalendarSet calendar, EaFcPlayerRatingSet ratings, List<NationRatingSeed>? seeds, ModelValidationReport report)
    {
        var ratingNations = ratings.Players
            .Select(x => NormalizeNation(x.Nation))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var calendarTeams = calendar.Matches
            .Where(x => x.HasKnownTeams)
            .SelectMany(x => new[] { x.HomeTeam, x.AwayTeam })
            .Select(NormalizeNation)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = calendarTeams.Where(team => !ratingNations.Contains(team)).ToList();
        report.Info.Add($"Known calendar teams: {calendarTeams.Count}; directly mapped to EA nations: {calendarTeams.Count - missing.Count}");

        if (missing.Count > 0)
            report.Warnings.Add($"Calendar teams not directly found as EA nations: {string.Join(", ", missing.Take(40))}. Add a name mapping later if these are qualified teams.");

        if (seeds is not null)
        {
            var lowConfidence = seeds.Where(x => x.Confidence != "High").Take(20).Select(x => $"{x.Nation}:{x.Confidence}/{x.PlayerCount}").ToList();
            if (lowConfidence.Count > 0)
                report.Info.Add($"Example low/medium confidence nation seeds: {string.Join(", ", lowConfidence)}");
        }
    }

    private static string NormalizeNation(string value)
    {
        var v = value.Trim();
        return v switch
        {
            "USA" => "United States",
            "United States of America" => "United States",
            "Korea Republic" => "South Korea",
            "Korea, Republic of" => "South Korea",
            "IR Iran" => "Iran",
            "Türkiye" => "Turkey",
            "Côte d'Ivoire" => "Ivory Coast",
            "Czechia" => "Czech Republic",
            _ => v
        };
    }

    private static string ToText(ModelValidationReport report)
    {
        var lines = new List<string>
        {
            $"MODEL VALIDATION REPORT",
            $"Status: {report.Status}",
            $"Models folder: {report.ModelsFolder}",
            $"Validated at UTC: {report.ValidatedAtUtc:O}",
            string.Empty,
            $"Errors ({report.Errors.Count})"
        };
        lines.AddRange(report.Errors.Select(x => $"- {x}"));
        lines.Add(string.Empty);
        lines.Add($"Warnings ({report.Warnings.Count})");
        lines.AddRange(report.Warnings.Select(x => $"- {x}"));
        lines.Add(string.Empty);
        lines.Add($"Info ({report.Info.Count})");
        lines.AddRange(report.Info.Select(x => $"- {x}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
