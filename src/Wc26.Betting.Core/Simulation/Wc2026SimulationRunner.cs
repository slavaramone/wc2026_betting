using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Odds;
using Wc26.Betting.Core.TeamRatings;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Simulation;

public sealed record Wc2026SimulationWeights(double Market, double Elo, double Ea)
{
    public string Label => $"{(int)Math.Round(Market * 100)}_{(int)Math.Round(Elo * 100)}_{(int)Math.Round(Ea * 100)}";

    public void Validate()
    {
        if (Market < 0 || Elo < 0 || Ea < 0)
            throw new ArgumentException("Simulation blend weights cannot be negative.");

        var sum = Market + Elo + Ea;
        if (sum <= 0)
            throw new ArgumentException("At least one simulation blend weight must be greater than zero.");
    }

    public Wc2026SimulationWeights Normalized()
    {
        var sum = Market + Elo + Ea;
        return sum <= 0 ? this : new Wc2026SimulationWeights(Market / sum, Elo / sum, Ea / sum);
    }
}

public sealed class Wc2026SimulationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static readonly Wc2026SimulationWeights DefaultWeights = new(0.85, 0.12, 0.03);


    public Task<Wc2026SimulationResultSet> RunFromModelsFolderAsync(
        string modelsFolder,
        int iterations,
        int seed,
        string? outputFolder,
        bool overwrite,
        CancellationToken cancellationToken)
        => RunFromModelsFolderAsync(modelsFolder, iterations, seed, outputFolder, overwrite, DefaultWeights, cancellationToken);

    public async Task<Wc2026SimulationResultSet> RunFromModelsFolderAsync(
        string modelsFolder,
        int iterations,
        int seed,
        string? outputFolder,
        bool overwrite,
        Wc2026SimulationWeights weights,
        CancellationToken cancellationToken)
    {
        if (iterations <= 0)
            throw new ArgumentException("--iterations must be greater than zero.");
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");

        var groups = await ReadRequiredAsync<Wc2026GroupSet>(Path.Combine(modelsFolder, "calendar", "wc2026-groups.json"), cancellationToken);
        var odds = await ReadRequiredAsync<GameOddsSet>(Path.Combine(modelsFolder, "odds", "game-odds.json"), cancellationToken);
        var elo = await ReadRequiredAsync<EloRatingSet>(Path.Combine(modelsFolder, "team-ratings", "hardcoded-elo-ratings.json"), cancellationToken);
        var seeds = await ReadRequiredAsync<List<NationRatingSeed>>(Path.Combine(modelsFolder, "player-ratings", "eafc26-nation-rating-seeds.json"), cancellationToken);

        var result = Run(groups, odds, elo, seeds, modelsFolder, iterations, seed, weights);

        var destination = outputFolder ?? Path.Combine(modelsFolder, "simulation");
        await WriteAsync(result, destination, overwrite, cancellationToken);
        return result;
    }

    public Wc2026SimulationResultSet Run(
        Wc2026GroupSet groups,
        GameOddsSet odds,
        EloRatingSet elo,
        IReadOnlyList<NationRatingSeed> seeds,
        string modelsFolder,
        int iterations,
        int seed,
        Wc2026SimulationWeights? weights = null)
    {
        var activeWeights = weights ?? DefaultWeights;
        activeWeights.Validate();

        var rng = new Random(seed);
        var allTeams = groups.Groups
            .SelectMany(g => g.Teams.Select(t => new TeamRef(g.GroupCode, t.TeamName)))
            .DistinctBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var accum = allTeams.ToDictionary(x => x.Team, x => new TeamAccum(x.Team, x.GroupCode), StringComparer.OrdinalIgnoreCase);
        var pairHigherCounts = new Dictionary<(string Higher, string Lower), int>(StringTupleComparer.OrdinalIgnoreCase);
        var oddsByEventId = odds.Matches
            .Where(x => x.CalendarEventId is not null)
            .GroupBy(x => x.CalendarEventId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var eloByTeam = elo.Teams
            .GroupBy(x => HardcodedEloRatingsBuilder.NormalizeToEloName(x.Team), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var seedByTeam = seeds
            .GroupBy(x => NormalizeEaNation(x.Nation), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < iterations; i++)
        {
            var thirdPlaced = new List<GroupStandingRow>();

            foreach (var group in groups.Groups.OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase))
            {
                var table = group.Teams.ToDictionary(t => t.TeamName, t => new GroupStandingRow(group.GroupCode, t.TeamName), StringComparer.OrdinalIgnoreCase);
                var playedMatches = new List<SimulatedMatchResult>();

                foreach (var match in group.Matches.OrderBy(x => x.StartUtc ?? DateTimeOffset.MaxValue).ThenBy(x => x.EventId))
                {
                    var result = SimulateMatch(match, oddsByEventId.GetValueOrDefault(match.EventId), eloByTeam, seedByTeam, activeWeights, rng);
                    ApplyResult(table[result.HomeTeam], table[result.AwayTeam], result.HomeGoals, result.AwayGoals);
                    playedMatches.Add(result);
                }

                var ranked = RankGroup(table.Values.ToList(), playedMatches, rng);
                for (var rankIndex = 0; rankIndex < ranked.Count; rankIndex++)
                {
                    var rank = rankIndex + 1;
                    var row = ranked[rankIndex];
                    var team = accum[row.Team];
                    team.Points += row.Points;
                    team.GoalsFor += row.GoalsFor;
                    team.GoalsAgainst += row.GoalsAgainst;
                    team.GoalDifference += row.GoalDifference;
                    team.RankCounts[rank]++;
                    if (rank == 1) team.WinGroup++;
                    if (rank <= 2) team.TopTwo++;
                    if (rank == 3)
                    {
                        team.ThirdPlace++;
                        thirdPlaced.Add(row);
                    }
                }

                for (var higherIndex = 0; higherIndex < ranked.Count; higherIndex++)
                {
                    for (var lowerIndex = higherIndex + 1; lowerIndex < ranked.Count; lowerIndex++)
                    {
                        var key = (Higher: ranked[higherIndex].Team, Lower: ranked[lowerIndex].Team);
                        pairHigherCounts[key] = pairHigherCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }

            var qualifiedThirds = RankThirdPlacedTeams(thirdPlaced, rng).Take(8).ToList();
            foreach (var row in qualifiedThirds)
                accum[row.Team].ThirdPlaceQualified++;
        }

        var teamSummaries = allTeams.Select(x =>
        {
            var a = accum[x.Team];
            var eloTeam = eloByTeam.GetValueOrDefault(HardcodedEloRatingsBuilder.NormalizeToEloName(x.Team));
            var seedTeam = seedByTeam.GetValueOrDefault(NormalizeEaNation(x.Team));
            var qualified = a.TopTwo + a.ThirdPlaceQualified;
            return new Wc2026SimulationTeamSummary
            {
                Team = x.Team,
                GroupCode = x.GroupCode,
                EloRating = eloTeam?.Rating ?? 0,
                EaTop11Rating = Round(seedTeam?.Top11AverageOverall ?? 0),
                EaTop26Rating = Round(seedTeam?.Top26AverageOverall ?? 0),
                EaConfidence = seedTeam?.Confidence ?? "Missing",
                AvgPoints = Round(a.Points / iterations),
                AvgGoalsFor = Round(a.GoalsFor / iterations),
                AvgGoalsAgainst = Round(a.GoalsAgainst / iterations),
                AvgGoalDifference = Round(a.GoalDifference / iterations),
                WinGroupProbability = RoundProbability(a.WinGroup, iterations),
                TopTwoProbability = RoundProbability(a.TopTwo, iterations),
                ThirdPlaceProbability = RoundProbability(a.ThirdPlace, iterations),
                ThirdPlaceQualifiedProbability = RoundProbability(a.ThirdPlaceQualified, iterations),
                QualifiedToRoundOf32Probability = RoundProbability(qualified, iterations),
                EliminatedInGroupProbability = Round(1.0 - (qualified / (double)iterations))
            };
        })
        .OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(x => x.QualifiedToRoundOf32Probability)
        .ThenByDescending(x => x.WinGroupProbability)
        .ToList();

        var groupSummaries = groups.Groups
            .OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Wc2026SimulationGroupSummary
            {
                GroupCode = group.GroupCode,
                Teams = group.Teams.Select(t => t.TeamName).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                TeamProbabilities = group.Teams
                    .Select(t =>
                    {
                        var a = accum[t.TeamName];
                        return new Wc2026SimulationGroupTeamProbability
                        {
                            Team = t.TeamName,
                            ExpectedRank = Round((a.RankCounts[1] + 2.0 * a.RankCounts[2] + 3.0 * a.RankCounts[3] + 4.0 * a.RankCounts[4]) / iterations),
                            Rank1Probability = RoundProbability(a.RankCounts[1], iterations),
                            Rank2Probability = RoundProbability(a.RankCounts[2], iterations),
                            Rank3Probability = RoundProbability(a.RankCounts[3], iterations),
                            Rank4Probability = RoundProbability(a.RankCounts[4], iterations)
                        };
                    })
                    .OrderBy(x => x.ExpectedRank)
                    .ThenBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        var pairSummaries = groups.Groups
            .OrderBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var teams = group.Teams.Select(t => t.TeamName).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
                var pairs = new List<Wc2026SimulationPairComparisonSummary>();
                for (var i = 0; i < teams.Count; i++)
                {
                    for (var j = i + 1; j < teams.Count; j++)
                    {
                        var team1 = teams[i];
                        var team2 = teams[j];
                        var team1Higher = pairHigherCounts.GetValueOrDefault((team1, team2));
                        var team2Higher = pairHigherCounts.GetValueOrDefault((team2, team1));
                        pairs.Add(new Wc2026SimulationPairComparisonSummary
                        {
                            GroupCode = group.GroupCode,
                            Team1 = team1,
                            Team2 = team2,
                            Team1FinishHigherProbability = RoundProbability(team1Higher, iterations),
                            Team2FinishHigherProbability = RoundProbability(team2Higher, iterations)
                        });
                    }
                }
                return pairs;
            })
            .ToList();

        return new Wc2026SimulationResultSet
        {
            ModelsFolder = modelsFolder,
            Iterations = iterations,
            Seed = seed,
            MarketWeight = activeWeights.Market,
            EloWeight = activeWeights.Elo,
            EaWeight = activeWeights.Ea,
            Notes = $"Group-stage simulation uses blended match probabilities: {activeWeights.Market:P0} normalized market 1X2, {activeWeights.Elo:P0} Elo, {activeWeights.Ea:P0} EA nation strength. Ranks groups with FIFA-style MVP tiebreakers and selects 8 best third-place teams. Knockout bracket is not simulated yet.",
            Teams = teamSummaries,
            Groups = groupSummaries,
            PairComparisons = pairSummaries
        };
    }

    private static SimulatedMatchResult SimulateMatch(
        Wc2026GroupMatch match,
        GameOddsMatch? odds,
        IReadOnlyDictionary<string, EloTeamRating> eloByTeam,
        IReadOnlyDictionary<string, NationRatingSeed> seedByTeam,
        Wc2026SimulationWeights weights,
        Random rng)
    {
        var probabilities = BlendedMatchProbabilities(match.HomeTeam, match.AwayTeam, odds, eloByTeam, seedByTeam, weights);

        var roll = rng.NextDouble();
        if (roll < probabilities.HomeWin)
        {
            var away = WeightedChoice(rng, new[] { (0, 0.46), (1, 0.34), (2, 0.15), (3, 0.05) });
            var margin = WeightedChoice(rng, new[] { (1, 0.62), (2, 0.27), (3, 0.09), (4, 0.02) });
            return new SimulatedMatchResult(match.EventId, match.HomeTeam, match.AwayTeam, away + margin, away);
        }

        if (roll < probabilities.HomeWin + probabilities.Draw)
        {
            var goals = WeightedChoice(rng, new[] { (0, 0.30), (1, 0.46), (2, 0.19), (3, 0.05) });
            return new SimulatedMatchResult(match.EventId, match.HomeTeam, match.AwayTeam, goals, goals);
        }

        var home = WeightedChoice(rng, new[] { (0, 0.46), (1, 0.34), (2, 0.15), (3, 0.05) });
        var awayMargin = WeightedChoice(rng, new[] { (1, 0.62), (2, 0.27), (3, 0.09), (4, 0.02) });
        return new SimulatedMatchResult(match.EventId, match.HomeTeam, match.AwayTeam, home, home + awayMargin);
    }

    private static OutcomeProbabilities BlendedMatchProbabilities(
        string homeTeam,
        string awayTeam,
        GameOddsMatch? odds,
        IReadOnlyDictionary<string, EloTeamRating> eloByTeam,
        IReadOnlyDictionary<string, NationRatingSeed> seedByTeam,
        Wc2026SimulationWeights weights)
    {
        var weighted = new List<(OutcomeProbabilities Probabilities, double Weight)>();

        if (odds is not null && odds.Odds1 is > 1.0 && odds.OddsX is > 1.0 && odds.Odds2 is > 1.0)
            weighted.Add((ProbabilitiesFromOdds(odds.Odds1.Value, odds.OddsX.Value, odds.Odds2.Value), weights.Market));

        if (weights.Elo > 0)
            weighted.Add((ProbabilitiesFromElo(homeTeam, awayTeam, eloByTeam), weights.Elo));
        if (weights.Ea > 0)
            weighted.Add((ProbabilitiesFromEa(homeTeam, awayTeam, seedByTeam), weights.Ea));

        var totalWeight = weighted.Sum(x => x.Weight);
        if (totalWeight <= 0)
            return new OutcomeProbabilities(0.365, 0.27, 0.365);

        var home = weighted.Sum(x => x.Probabilities.HomeWin * x.Weight) / totalWeight;
        var draw = weighted.Sum(x => x.Probabilities.Draw * x.Weight) / totalWeight;
        var away = weighted.Sum(x => x.Probabilities.AwayWin * x.Weight) / totalWeight;
        return NormalizeProbabilities(new OutcomeProbabilities(home, draw, away));
    }

    private static OutcomeProbabilities ProbabilitiesFromOdds(double home, double draw, double away)
    {
        var ih = 1.0 / home;
        var id = 1.0 / draw;
        var ia = 1.0 / away;
        var sum = ih + id + ia;
        return new OutcomeProbabilities(ih / sum, id / sum, ia / sum);
    }

    private static OutcomeProbabilities ProbabilitiesFromElo(string homeTeam, string awayTeam, IReadOnlyDictionary<string, EloTeamRating> eloByTeam)
    {
        var home = eloByTeam.GetValueOrDefault(HardcodedEloRatingsBuilder.NormalizeToEloName(homeTeam))?.Rating ?? 1500;
        var away = eloByTeam.GetValueOrDefault(HardcodedEloRatingsBuilder.NormalizeToEloName(awayTeam))?.Rating ?? 1500;
        return ProbabilitiesFromRatingDifference(home - away, 0.27);
    }

    private static OutcomeProbabilities ProbabilitiesFromEa(string homeTeam, string awayTeam, IReadOnlyDictionary<string, NationRatingSeed> seedByTeam)
    {
        var home = EaStrength(seedByTeam.GetValueOrDefault(NormalizeEaNation(homeTeam)));
        var away = EaStrength(seedByTeam.GetValueOrDefault(NormalizeEaNation(awayTeam)));

        // EA overall points are much tighter than Elo points. Treat one EA overall point
        // as roughly 25 Elo points so EA can slightly pull match probabilities without
        // overpowering market prices. Missing teams are neutralized at 75.0.
        var eaRatingDiffAsElo = (home - away) * 25.0;
        return ProbabilitiesFromRatingDifference(eaRatingDiffAsElo, 0.27);
    }

    private static double EaStrength(NationRatingSeed? seed)
    {
        if (seed is null || seed.Top11AverageOverall <= 0)
            return 75.0;

        // Top11 is the main first-XI signal. Top26 adds a small squad-depth component.
        var top26 = seed.Top26AverageOverall > 0 ? seed.Top26AverageOverall : seed.Top11AverageOverall;
        return (0.75 * seed.Top11AverageOverall) + (0.25 * top26);
    }

    private static OutcomeProbabilities ProbabilitiesFromRatingDifference(double ratingDiff, double draw)
    {
        var homeNoDraw = 1.0 / (1.0 + Math.Pow(10.0, -ratingDiff / 400.0));
        return NormalizeProbabilities(new OutcomeProbabilities(homeNoDraw * (1.0 - draw), draw, (1.0 - homeNoDraw) * (1.0 - draw)));
    }

    private static OutcomeProbabilities NormalizeProbabilities(OutcomeProbabilities probabilities)
    {
        var home = Math.Max(0.0, probabilities.HomeWin);
        var draw = Math.Max(0.0, probabilities.Draw);
        var away = Math.Max(0.0, probabilities.AwayWin);
        var sum = home + draw + away;
        return sum <= 0
            ? new OutcomeProbabilities(0.365, 0.27, 0.365)
            : new OutcomeProbabilities(home / sum, draw / sum, away / sum);
    }

    private static void ApplyResult(GroupStandingRow home, GroupStandingRow away, int homeGoals, int awayGoals)
    {
        home.GoalsFor += homeGoals;
        home.GoalsAgainst += awayGoals;
        away.GoalsFor += awayGoals;
        away.GoalsAgainst += homeGoals;

        if (homeGoals > awayGoals)
        {
            home.Points += 3;
            home.Wins++;
            away.Losses++;
        }
        else if (homeGoals < awayGoals)
        {
            away.Points += 3;
            away.Wins++;
            home.Losses++;
        }
        else
        {
            home.Points += 1;
            away.Points += 1;
            home.Draws++;
            away.Draws++;
        }
    }

    private static List<GroupStandingRow> RankGroup(List<GroupStandingRow> table, IReadOnlyList<SimulatedMatchResult> matches, Random rng)
    {
        return table
            .Select(row => row with { RandomTieBreaker = rng.NextDouble() })
            .OrderByDescending(x => x.Points)
            .ThenByDescending(x => x.GoalDifference)
            .ThenByDescending(x => x.GoalsFor)
            .ThenByDescending(x => HeadToHeadPoints(x.Team, table, matches))
            .ThenByDescending(x => HeadToHeadGoalDifference(x.Team, table, matches))
            .ThenByDescending(x => HeadToHeadGoalsFor(x.Team, table, matches))
            .ThenBy(x => x.RandomTieBreaker)
            .ToList();
    }

    private static List<GroupStandingRow> RankThirdPlacedTeams(List<GroupStandingRow> thirdPlaced, Random rng)
    {
        return thirdPlaced
            .Select(row => row with { RandomTieBreaker = rng.NextDouble() })
            .OrderByDescending(x => x.Points)
            .ThenByDescending(x => x.GoalDifference)
            .ThenByDescending(x => x.GoalsFor)
            .ThenBy(x => x.RandomTieBreaker)
            .ToList();
    }

    private static int HeadToHeadPoints(string team, IReadOnlyList<GroupStandingRow> fullTable, IReadOnlyList<SimulatedMatchResult> matches)
    {
        var tiedTeams = fullTable.Where(x => x.Points == fullTable.First(t => string.Equals(t.Team, team, StringComparison.OrdinalIgnoreCase)).Points).Select(x => x.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tiedTeams.Count <= 1) return 0;

        var points = 0;
        foreach (var match in matches.Where(m => tiedTeams.Contains(m.HomeTeam) && tiedTeams.Contains(m.AwayTeam)))
        {
            if (!string.Equals(match.HomeTeam, team, StringComparison.OrdinalIgnoreCase) && !string.Equals(match.AwayTeam, team, StringComparison.OrdinalIgnoreCase))
                continue;
            var goalsFor = string.Equals(match.HomeTeam, team, StringComparison.OrdinalIgnoreCase) ? match.HomeGoals : match.AwayGoals;
            var goalsAgainst = string.Equals(match.HomeTeam, team, StringComparison.OrdinalIgnoreCase) ? match.AwayGoals : match.HomeGoals;
            points += goalsFor > goalsAgainst ? 3 : goalsFor == goalsAgainst ? 1 : 0;
        }
        return points;
    }

    private static int HeadToHeadGoalDifference(string team, IReadOnlyList<GroupStandingRow> fullTable, IReadOnlyList<SimulatedMatchResult> matches)
    {
        var tiedTeams = fullTable.Where(x => x.Points == fullTable.First(t => string.Equals(t.Team, team, StringComparison.OrdinalIgnoreCase)).Points).Select(x => x.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tiedTeams.Count <= 1) return 0;
        return matches.Where(m => tiedTeams.Contains(m.HomeTeam) && tiedTeams.Contains(m.AwayTeam))
            .Sum(m => string.Equals(m.HomeTeam, team, StringComparison.OrdinalIgnoreCase) ? m.HomeGoals - m.AwayGoals : string.Equals(m.AwayTeam, team, StringComparison.OrdinalIgnoreCase) ? m.AwayGoals - m.HomeGoals : 0);
    }

    private static int HeadToHeadGoalsFor(string team, IReadOnlyList<GroupStandingRow> fullTable, IReadOnlyList<SimulatedMatchResult> matches)
    {
        var tiedTeams = fullTable.Where(x => x.Points == fullTable.First(t => string.Equals(t.Team, team, StringComparison.OrdinalIgnoreCase)).Points).Select(x => x.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tiedTeams.Count <= 1) return 0;
        return matches.Where(m => tiedTeams.Contains(m.HomeTeam) && tiedTeams.Contains(m.AwayTeam))
            .Sum(m => string.Equals(m.HomeTeam, team, StringComparison.OrdinalIgnoreCase) ? m.HomeGoals : string.Equals(m.AwayTeam, team, StringComparison.OrdinalIgnoreCase) ? m.AwayGoals : 0);
    }

    private static int WeightedChoice(Random rng, IReadOnlyList<(int Value, double Weight)> options)
    {
        var total = options.Sum(x => x.Weight);
        var roll = rng.NextDouble() * total;
        double cumulative = 0;
        foreach (var option in options)
        {
            cumulative += option.Weight;
            if (roll <= cumulative)
                return option.Value;
        }
        return options[^1].Value;
    }

    private static string NormalizeEaNation(string value)
    {
        var v = value.Trim();
        return v switch
        {
            // Normalize calendar/SofaScore names to EAFC26 nation names.
            // Kept in code by design; no external alias file is used.
            "Bosnia & Herzegovina" => "Bosnia and Herzegovina",
            "Cabo Verde" => "Cape Verde Islands",
            "DR Congo" => "Congo DR",
            "Netherlands" => "Holland",
            "Czechia" => "Czech Republic",
            "South Korea" => "Korea Republic",
            "Korea, Republic of" => "Korea Republic",
            "USA" => "United States",
            "United States of America" => "United States",
            "Türkiye" => "Turkey",
            _ => v
        };
    }

    private static async Task<T> ReadRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required model file not found: {path}", path);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize model file: {path}");
    }

    private static async Task WriteAsync(Wc2026SimulationResultSet result, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        await WriteJsonAsync(Path.Combine(outputFolder, "wc2026-simulation-summary.json"), result, overwrite, cancellationToken);
        await WriteTeamCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-team-probabilities.csv"), result, overwrite, cancellationToken);
        await WriteGroupCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-group-probabilities.csv"), result, overwrite, cancellationToken);
        await WritePairComparisonCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-pair-comparisons.csv"), result, overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    private static async Task WriteTeamCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("team,group_code,elo_rating,ea_top11_rating,ea_top26_rating,ea_confidence,avg_points,avg_goals_for,avg_goals_against,avg_goal_difference,win_group_probability,top_two_probability,third_place_probability,third_place_qualified_probability,qualified_to_round_of_32_probability,eliminated_in_group_probability");
        foreach (var t in result.Teams)
        {
            var values = new[]
            {
                t.Team, t.GroupCode, t.EloRating.ToString(), t.EaTop11Rating.ToString("0.###"), t.EaTop26Rating.ToString("0.###"), t.EaConfidence,
                t.AvgPoints.ToString("0.###"), t.AvgGoalsFor.ToString("0.###"), t.AvgGoalsAgainst.ToString("0.###"), t.AvgGoalDifference.ToString("0.###"),
                t.WinGroupProbability.ToString("0.######"), t.TopTwoProbability.ToString("0.######"), t.ThirdPlaceProbability.ToString("0.######"),
                t.ThirdPlaceQualifiedProbability.ToString("0.######"), t.QualifiedToRoundOf32Probability.ToString("0.######"), t.EliminatedInGroupProbability.ToString("0.######")
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteGroupCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("group_code,team,expected_rank,rank1_probability,rank2_probability,rank3_probability,rank4_probability");
        foreach (var g in result.Groups)
        {
            foreach (var t in g.TeamProbabilities)
            {
                var values = new[]
                {
                    g.GroupCode, t.Team, t.ExpectedRank.ToString("0.###"), t.Rank1Probability.ToString("0.######"),
                    t.Rank2Probability.ToString("0.######"), t.Rank3Probability.ToString("0.######"), t.Rank4Probability.ToString("0.######")
                };
                await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
            }
        }
    }


    private static async Task WritePairComparisonCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("group_code,team1,team2,team1_finish_higher_probability,team2_finish_higher_probability");
        foreach (var pair in result.PairComparisons)
        {
            var values = new[]
            {
                pair.GroupCode, pair.Team1, pair.Team2,
                pair.Team1FinishHigherProbability.ToString("0.######"),
                pair.Team2FinishHigherProbability.ToString("0.######")
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static double Round(double value) => Math.Round(value, 6);
    private static double RoundProbability(int count, int iterations) => Round(count / (double)iterations);

    private sealed class StringTupleComparer : IEqualityComparer<(string First, string Second)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string First, string Second) x, (string First, string Second) y)
            => string.Equals(x.First, y.First, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Second, y.Second, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string First, string Second) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.First), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Second));
    }

    private sealed record TeamRef(string GroupCode, string Team);
    private sealed record OutcomeProbabilities(double HomeWin, double Draw, double AwayWin);
    private sealed record SimulatedMatchResult(long EventId, string HomeTeam, string AwayTeam, int HomeGoals, int AwayGoals);

    private sealed record GroupStandingRow(string GroupCode, string Team)
    {
        public int Points { get; set; }
        public int Wins { get; set; }
        public int Draws { get; set; }
        public int Losses { get; set; }
        public int GoalsFor { get; set; }
        public int GoalsAgainst { get; set; }
        public int GoalDifference => GoalsFor - GoalsAgainst;
        public double RandomTieBreaker { get; init; }
    }

    private sealed class TeamAccum
    {
        public TeamAccum(string team, string groupCode)
        {
            Team = team;
            GroupCode = groupCode;
        }

        public string Team { get; }
        public string GroupCode { get; }
        public double Points { get; set; }
        public double GoalsFor { get; set; }
        public double GoalsAgainst { get; set; }
        public double GoalDifference { get; set; }
        public int WinGroup { get; set; }
        public int TopTwo { get; set; }
        public int ThirdPlace { get; set; }
        public int ThirdPlaceQualified { get; set; }
        public Dictionary<int, int> RankCounts { get; } = new() { [1] = 0, [2] = 0, [3] = 0, [4] = 0 };
    }
}
