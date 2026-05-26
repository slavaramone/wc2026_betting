using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Odds;
using Wc26.Betting.Core.Players;
using Wc26.Betting.Core.TeamRatings;

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
        var groupsPath = Path.Combine(modelsFolder, "calendar", "wc2026-groups.json");
        var playerPath = Path.Combine(modelsFolder, "player-ratings", "eafc26-men-player-ratings.json");
        var seedsPath = Path.Combine(modelsFolder, "player-ratings", "eafc26-nation-rating-seeds.json");
        var eloPath = Path.Combine(modelsFolder, "team-ratings", "hardcoded-elo-ratings.json");
        var oddsPath = Path.Combine(modelsFolder, "odds", "game-odds.json");
        var simulationPath = Path.Combine(modelsFolder, "simulation", "wc2026-simulation-summary.json");

        Wc2026CalendarSet? calendar = null;
        Wc2026GroupSet? groups = null;
        EaFcPlayerRatingSet? ratings = null;
        List<NationRatingSeed>? seeds = null;
        EloRatingSet? elo = null;
        GameOddsSet? odds = null;
        Wc2026SimulationResultSet? simulation = null;

        if (!File.Exists(calendarPath)) report.Errors.Add($"Missing calendar model: {calendarPath}");
        else calendar = await ReadAsync<Wc2026CalendarSet>(calendarPath, report, cancellationToken);

        if (!File.Exists(groupsPath)) report.Errors.Add($"Missing group model: {groupsPath}. Re-run build-models to create it.");
        else groups = await ReadAsync<Wc2026GroupSet>(groupsPath, report, cancellationToken);

        if (!File.Exists(playerPath)) report.Errors.Add($"Missing EA player ratings model: {playerPath}");
        else ratings = await ReadAsync<EaFcPlayerRatingSet>(playerPath, report, cancellationToken);

        if (!File.Exists(seedsPath)) report.Warnings.Add($"Missing nation rating seed model: {seedsPath}");
        else seeds = await ReadAsync<List<NationRatingSeed>>(seedsPath, report, cancellationToken);

        if (!File.Exists(eloPath)) report.Errors.Add($"Missing hardcoded Elo ratings model: {eloPath}");
        else elo = await ReadAsync<EloRatingSet>(eloPath, report, cancellationToken);

        if (File.Exists(oddsPath)) odds = await ReadAsync<GameOddsSet>(oddsPath, report, cancellationToken);
        else report.Warnings.Add($"Missing game odds model: {oddsPath}. This is OK only before odds are imported.");

        if (File.Exists(simulationPath)) simulation = await ReadAsync<Wc2026SimulationResultSet>(simulationPath, report, cancellationToken);

        if (calendar is not null) ValidateCalendar(calendar, report);
        if (groups is not null) ValidateGroups(groups, calendar, report);
        if (ratings is not null) ValidateRatings(ratings, report);
        if (elo is not null) ValidateEloRatings(elo, report);
        if (odds is not null) ValidateGameOdds(odds, calendar, groups, report);
        if (calendar is not null && ratings is not null) ValidateCalendarTeamCoverage(calendar, ratings, seeds, report);
        if (calendar is not null && elo is not null) ValidateCalendarEloCoverage(calendar, elo, report);
        if (simulation is not null) ValidateSimulation(simulation, groups, report);

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


    private static void ValidateGroups(Wc2026GroupSet groups, Wc2026CalendarSet? calendar, ModelValidationReport report)
    {
        report.Info.Add($"Calendar groups: {groups.Groups.Count}");

        if (groups.Groups.Count == 0)
        {
            report.Errors.Add("Group model has zero groups.");
            return;
        }

        if (groups.Groups.Count != 12)
            report.Warnings.Add($"Expected 12 WC2026 groups; current group model has {groups.Groups.Count}.");

        var duplicateCodes = groups.Groups
            .GroupBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateCodes.Count > 0)
            report.Errors.Add($"Duplicate group codes: {string.Join(", ", duplicateCodes)}");

        var teams = groups.Groups.SelectMany(x => x.Teams.Select(t => t.TeamName)).ToList();
        var duplicateTeams = teams
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(20)
            .ToList();
        if (duplicateTeams.Count > 0)
            report.Errors.Add($"Teams assigned to multiple groups: {string.Join(", ", duplicateTeams)}");

        var totalGroupTeams = teams.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var totalGroupMatches = groups.Groups.Sum(x => x.Matches.Count);
        report.Info.Add($"Group model teams: {totalGroupTeams}; group matches: {totalGroupMatches}");

        foreach (var group in groups.Groups.OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Teams.Count != 4)
                report.Warnings.Add($"Group {group.GroupCode} has {group.Teams.Count} teams, expected 4.");
            if (group.Matches.Count != 6)
                report.Warnings.Add($"Group {group.GroupCode} has {group.Matches.Count} matches, expected 6.");

            var badTeamMatchCounts = group.Teams.Where(x => x.MatchCount != 3).Select(x => $"{x.TeamName}={x.MatchCount}").ToList();
            if (badTeamMatchCounts.Count > 0)
                report.Warnings.Add($"Group {group.GroupCode} teams not playing 3 group matches: {string.Join(", ", badTeamMatchCounts)}.");

            var duplicateMatchIds = group.Matches
                .GroupBy(x => x.EventId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (duplicateMatchIds.Count > 0)
                report.Errors.Add($"Group {group.GroupCode} duplicate match ids: {string.Join(", ", duplicateMatchIds)}");
        }

        if (calendar is not null)
        {
            var calendarGroupMatchIds = calendar.Matches
                .Where(x => x.Stage == "group_stage" && x.HasKnownTeams)
                .Select(x => x.EventId)
                .ToHashSet();
            var modelGroupMatchIds = groups.Groups.SelectMany(x => x.Matches).Select(x => x.EventId).ToHashSet();

            var missingFromGroupModel = calendarGroupMatchIds.Except(modelGroupMatchIds).Take(20).ToList();
            var unknownInGroupModel = modelGroupMatchIds.Except(calendarGroupMatchIds).Take(20).ToList();

            if (missingFromGroupModel.Count > 0)
                report.Errors.Add($"Group-stage calendar matches missing from group model: {string.Join(", ", missingFromGroupModel)}");
            if (unknownInGroupModel.Count > 0)
                report.Errors.Add($"Group model contains match ids not present in known group-stage calendar: {string.Join(", ", unknownInGroupModel)}");
        }
    }



    private static void ValidateGameOdds(GameOddsSet odds, Wc2026CalendarSet? calendar, Wc2026GroupSet? groups, ModelValidationReport report)
    {
        report.Info.Add($"Game odds rows read: {odds.RowCount}; odds matches: {odds.Matches.Count}");

        if (odds.Matches.Count == 0)
        {
            report.Errors.Add("Game odds model has zero matches.");
            return;
        }

        var duplicateKeys = odds.Matches
            .Where(x => !string.IsNullOrWhiteSpace(x.MatchKey))
            .GroupBy(x => x.MatchKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(20)
            .ToList();
        if (duplicateKeys.Count > 0)
            report.Errors.Add($"Duplicate odds match keys: {string.Join(", ", duplicateKeys)}");

        var invalid1X2 = odds.Matches
            .Where(x => !IsValidOdds(x.Odds1) || !IsValidOdds(x.OddsX) || !IsValidOdds(x.Odds2))
            .Select(x => $"{x.HomeTeam}-{x.AwayTeam}")
            .Take(20)
            .ToList();
        if (invalid1X2.Count > 0)
            report.Errors.Add($"Invalid or missing 1X2 odds for matches: {string.Join(", ", invalid1X2)}");

        var invalidTotals = odds.Matches
            .Where(x => x.TotalLine is not null && (!IsValidOdds(x.OverOdds) || !IsValidOdds(x.UnderOdds)))
            .Select(x => $"{x.HomeTeam}-{x.AwayTeam}")
            .Take(20)
            .ToList();
        if (invalidTotals.Count > 0)
            report.Warnings.Add($"Total line exists but over/under odds are invalid or missing: {string.Join(", ", invalidTotals)}");

        var mapped = odds.Matches.Count(x => x.CalendarEventId is not null);
        report.Info.Add($"Game odds mapped to calendar: {mapped}/{odds.Matches.Count}");
        if (mapped != odds.Matches.Count)
        {
            var examples = odds.Matches
                .Where(x => x.CalendarEventId is null)
                .Select(x => $"{x.MatchDate} {x.HomeTeam}-{x.AwayTeam}")
                .Take(20)
                .ToList();
            report.Warnings.Add($"Game odds not matched to calendar: {odds.Matches.Count - mapped}. Examples: {string.Join(", ", examples)}");
        }

        var duplicateEventIds = odds.Matches
            .Where(x => x.CalendarEventId is not null)
            .GroupBy(x => x.CalendarEventId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!.Value)
            .Take(20)
            .ToList();
        if (duplicateEventIds.Count > 0)
            report.Errors.Add($"Multiple odds rows mapped to same calendar event ids: {string.Join(", ", duplicateEventIds)}");

        var badOverrounds = odds.Matches
            .Where(x => x.Odds1X2Overround is < 1.0 or > 1.35)
            .Select(x => $"{x.HomeTeam}-{x.AwayTeam}={x.Odds1X2Overround:0.###}")
            .Take(20)
            .ToList();
        if (badOverrounds.Count > 0)
            report.Warnings.Add($"Suspicious 1X2 overrounds outside 1.00..1.35: {string.Join(", ", badOverrounds)}");

        var totalBadOverrounds = odds.Matches
            .Where(x => x.TotalOverround is < 1.0 or > 1.25)
            .Select(x => $"{x.HomeTeam}-{x.AwayTeam}={x.TotalOverround:0.###}")
            .Take(20)
            .ToList();
        if (totalBadOverrounds.Count > 0)
            report.Warnings.Add($"Suspicious total overrounds outside 1.00..1.25: {string.Join(", ", totalBadOverrounds)}");

        var rawRussianNotMapped = odds.Matches
            .SelectMany(x => new[] { (Raw: x.HomeTeamRaw, English: x.HomeTeam), (Raw: x.AwayTeamRaw, English: x.AwayTeam) })
            .Where(x => ContainsCyrillic(x.English))
            .Select(x => x.Raw)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
        if (rawRussianNotMapped.Count > 0)
            report.Errors.Add($"Russian team names not mapped to English in odds model: {string.Join(", ", rawRussianNotMapped)}");

        if (calendar is not null)
        {
            var groupEventIds = calendar.Matches
                .Where(x => x.Stage == "group_stage" && x.HasKnownTeams)
                .Select(x => x.EventId)
                .ToHashSet();
            var oddsGroupEventIds = odds.Matches
                .Where(x => x.CalendarEventId is not null && groupEventIds.Contains(x.CalendarEventId.Value))
                .Select(x => x.CalendarEventId!.Value)
                .ToHashSet();

            report.Info.Add($"Group-stage calendar matches with odds: {oddsGroupEventIds.Count}/{groupEventIds.Count}");
            if (groupEventIds.Count == 72 && oddsGroupEventIds.Count != 72)
                report.Warnings.Add($"Expected odds for 72 group-stage matches; mapped odds cover {oddsGroupEventIds.Count}.");
        }

        if (groups is not null)
        {
            var byGroup = odds.Matches
                .Where(x => !string.IsNullOrWhiteSpace(x.CalendarGroupCode))
                .GroupBy(x => x.CalendarGroupCode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key}={g.Count()}");
            report.Info.Add($"Game odds by group: {string.Join(", ", byGroup)}");
        }
    }

    private static bool IsValidOdds(double? value)
        => value is > 1.0 and < 1000.0;

    private static bool ContainsCyrillic(string value)
        => value.Any(c => c >= '\u0400' && c <= '\u04FF');

    private static void ValidateEloRatings(EloRatingSet elo, ModelValidationReport report)
    {
        report.Info.Add($"Hardcoded Elo teams: {elo.Teams.Count}; as of {elo.AsOfDate:yyyy-MM-dd}");

        if (elo.Teams.Count < 100)
            report.Warnings.Add($"Hardcoded Elo model has only {elo.Teams.Count} teams. This can be OK for MVP, but broader coverage is better.");

        var duplicateTeams = elo.Teams
            .GroupBy(x => x.NormalizedTeam, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(20)
            .ToList();
        if (duplicateTeams.Count > 0)
            report.Errors.Add($"Duplicate normalized teams in hardcoded Elo model: {string.Join(", ", duplicateTeams)}");

        var invalidRatings = elo.Teams.Where(x => x.Rating < 300 || x.Rating > 2500).Select(x => $"{x.Team}={x.Rating}").Take(20).ToList();
        if (invalidRatings.Count > 0)
            report.Errors.Add($"Invalid Elo ratings outside 300..2500: {string.Join(", ", invalidRatings)}");

        var top = elo.Teams.OrderByDescending(x => x.Rating).Take(5).Select(x => $"{x.Team}={x.Rating}");
        report.Info.Add($"Top hardcoded Elo teams: {string.Join(", ", top)}");
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
            report.Warnings.Add($"Calendar teams not directly found as EA nations after built-in normalization: {string.Join(", ", missing.Take(40))}.");

        if (seeds is not null)
        {
            var lowConfidence = seeds.Where(x => x.Confidence != "High").Take(20).Select(x => $"{x.Nation}:{x.Confidence}/{x.PlayerCount}").ToList();
            if (lowConfidence.Count > 0)
                report.Info.Add($"Example low/medium confidence nation seeds: {string.Join(", ", lowConfidence)}");
        }
    }


    private static void ValidateCalendarEloCoverage(Wc2026CalendarSet calendar, EloRatingSet elo, ModelValidationReport report)
    {
        var eloTeams = elo.Teams
            .Select(x => HardcodedEloRatingsBuilder.NormalizeToEloName(x.NormalizedTeam))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var calendarTeams = calendar.Matches
            .Where(x => x.HasKnownTeams)
            .SelectMany(x => new[] { x.HomeTeam, x.AwayTeam })
            .Select(HardcodedEloRatingsBuilder.NormalizeToEloName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = calendarTeams.Where(team => !eloTeams.Contains(team)).ToList();
        report.Info.Add($"Known calendar teams mapped to hardcoded Elo: {calendarTeams.Count - missing.Count}/{calendarTeams.Count}");

        if (missing.Count > 0)
            report.Warnings.Add($"Calendar teams not found in hardcoded Elo ratings after built-in normalization: {string.Join(", ", missing.Take(40))}.");
    }

    private static void ValidateSimulation(Wc2026SimulationResultSet simulation, Wc2026GroupSet? groups, ModelValidationReport report)
    {
        report.Info.Add($"Simulation skeleton: iterations={simulation.Iterations}; teams={simulation.Teams.Count}; groups={simulation.Groups.Count}");

        if (simulation.Iterations <= 0)
            report.Errors.Add("Simulation iterations must be greater than zero.");
        if (simulation.Teams.Count != 48)
            report.Warnings.Add($"Simulation has {simulation.Teams.Count} teams, expected 48.");
        if (simulation.Groups.Count != 12)
            report.Warnings.Add($"Simulation has {simulation.Groups.Count} groups, expected 12.");

        report.Info.Add($"Simulation pair comparisons: {simulation.PairComparisons.Count}");
        if (simulation.PairComparisons.Count > 0 && simulation.PairComparisons.Count != 72)
            report.Warnings.Add($"Simulation has {simulation.PairComparisons.Count} pair comparisons, expected 72 for 12 groups of 4 teams.");

        var duplicateTeams = simulation.Teams
            .GroupBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(20)
            .ToList();
        if (duplicateTeams.Count > 0)
            report.Errors.Add($"Simulation duplicate teams: {string.Join(", ", duplicateTeams)}");

        var badProbabilities = simulation.Teams
            .Where(x => !IsProbability(x.WinGroupProbability)
                || !IsProbability(x.TopTwoProbability)
                || !IsProbability(x.ThirdPlaceProbability)
                || !IsProbability(x.ThirdPlaceQualifiedProbability)
                || !IsProbability(x.QualifiedToRoundOf32Probability)
                || !IsProbability(x.EliminatedInGroupProbability))
            .Select(x => x.Team)
            .Take(20)
            .ToList();
        if (badProbabilities.Count > 0)
            report.Errors.Add($"Simulation probabilities outside 0..1: {string.Join(", ", badProbabilities)}");

        var qualifiedSum = simulation.Teams.Sum(x => x.QualifiedToRoundOf32Probability);
        if (Math.Abs(qualifiedSum - 32.0) > 0.25)
            report.Warnings.Add($"Simulation qualified-to-R32 probability sum is {qualifiedSum:0.###}, expected about 32.");

        var thirdQualifiedSum = simulation.Teams.Sum(x => x.ThirdPlaceQualifiedProbability);
        if (Math.Abs(thirdQualifiedSum - 8.0) > 0.25)
            report.Warnings.Add($"Simulation third-place qualified probability sum is {thirdQualifiedSum:0.###}, expected about 8.");

        foreach (var group in simulation.Groups)
        {
            var winGroupSum = group.TeamProbabilities.Sum(x => x.Rank1Probability);
            var rank2Sum = group.TeamProbabilities.Sum(x => x.Rank2Probability);
            var rank3Sum = group.TeamProbabilities.Sum(x => x.Rank3Probability);
            var rank4Sum = group.TeamProbabilities.Sum(x => x.Rank4Probability);
            if (Math.Abs(winGroupSum - 1.0) > 0.05
                || Math.Abs(rank2Sum - 1.0) > 0.05
                || Math.Abs(rank3Sum - 1.0) > 0.05
                || Math.Abs(rank4Sum - 1.0) > 0.05)
                report.Warnings.Add($"Simulation group {group.GroupCode} rank probability sums are suspicious: r1={winGroupSum:0.###}, r2={rank2Sum:0.###}, r3={rank3Sum:0.###}, r4={rank4Sum:0.###}.");
        }

        if (groups is not null)
        {
            var groupTeams = groups.Groups.SelectMany(g => g.Teams.Select(t => t.TeamName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var simTeams = simulation.Teams.Select(x => x.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = groupTeams.Except(simTeams).Take(20).ToList();
            if (missing.Count > 0)
                report.Errors.Add($"Simulation missing group teams: {string.Join(", ", missing)}");
        }
    }

    private static bool IsProbability(double value) => value >= -0.000001 && value <= 1.000001 && !double.IsNaN(value) && !double.IsInfinity(value);


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

            // Built-in SofaScore -> EAFC26 country-name normalization.
            // Kept in code by design; no external alias file is used.
            "Bosnia & Herzegovina" => "Bosnia and Herzegovina",
            "Cabo Verde" => "Cape Verde Islands",
            "DR Congo" => "Congo DR",
            "Netherlands" => "Holland",

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
