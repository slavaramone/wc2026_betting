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
        var calendar = await TryReadAsync<Wc2026CalendarSet>(Path.Combine(modelsFolder, "calendar", "wc2026-calendar.json"), cancellationToken);
        var odds = await ReadRequiredAsync<GameOddsSet>(Path.Combine(modelsFolder, "odds", "game-odds.json"), cancellationToken);
        var elo = await ReadRequiredAsync<EloRatingSet>(Path.Combine(modelsFolder, "team-ratings", "hardcoded-elo-ratings.json"), cancellationToken);
        var seeds = await ReadRequiredAsync<List<NationRatingSeed>>(Path.Combine(modelsFolder, "player-ratings", "eafc26-nation-rating-seeds.json"), cancellationToken);

        var result = Run(groups, odds, elo, seeds, modelsFolder, iterations, seed, weights, calendar);

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
        Wc2026SimulationWeights? weights = null,
        Wc2026CalendarSet? calendar = null)
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
        var knockoutRules = BuildKnockoutBracketRules(calendar, groups);
        var thirdPlaceAllocationTable = OfficialThirdPlaceAllocationTable.CreateDefault();
        var pairHigherCounts = new Dictionary<(string Higher, string Lower), int>(StringTupleComparer.OrdinalIgnoreCase);
        var tournamentHigherCounts = new Dictionary<(string Higher, string Lower), int>(StringTupleComparer.OrdinalIgnoreCase);
        var bestConfederationCounts = new Dictionary<(string Confederation, string Team), int>(StringTupleComparer.OrdinalIgnoreCase);
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
            var rankedByGroup = new Dictionary<string, List<GroupStandingRow>>(StringComparer.OrdinalIgnoreCase);

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
                rankedByGroup[group.GroupCode] = ranked;
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

            var tournamentRanks = BuildInitialTournamentRanks(allTeams, rankedByGroup, qualifiedThirds, rng);
            SimulateKnockout(rankedByGroup, qualifiedThirds, knockoutRules, thirdPlaceAllocationTable, accum, tournamentRanks, eloByTeam, seedByTeam, activeWeights, rng);
            CountTournamentHigherPairs(tournamentRanks, tournamentHigherCounts);
            CountBestConfederationTeams(tournamentRanks, bestConfederationCounts);
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
                EliminatedInGroupProbability = Round(1.0 - (qualified / (double)iterations)),
                ReachRoundOf16Probability = RoundProbability(a.ReachRoundOf16, iterations),
                ReachQuarterFinalProbability = RoundProbability(a.ReachQuarterFinal, iterations),
                ReachSemiFinalProbability = RoundProbability(a.ReachSemiFinal, iterations),
                ReachFinalProbability = RoundProbability(a.ReachFinal, iterations),
                WinnerProbability = RoundProbability(a.Winner, iterations)
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

        var tournamentPairSummaries = allTeams
            .Select(x => x.Team)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SelectMany((team1, i) => allTeams
                .Select(x => x.Team)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Skip(i + 1)
                .Select(team2 =>
                {
                    var team1Higher = tournamentHigherCounts.GetValueOrDefault((team1, team2));
                    var team2Higher = tournamentHigherCounts.GetValueOrDefault((team2, team1));
                    return new Wc2026SimulationTournamentPairComparisonSummary
                    {
                        Team1 = team1,
                        Team2 = team2,
                        Team1FinishHigherProbability = RoundProbability(team1Higher, iterations),
                        Team2FinishHigherProbability = RoundProbability(team2Higher, iterations)
                    };
                }))
            .ToList();

        var bestConfederationSummaries = allTeams
            .Select(x => new { x.Team, x.GroupCode, Info = TeamConfederationCatalog.TryGetByTeam(x.Team) })
            .Where(x => x.Info is not null)
            .Select(x => new Wc2026SimulationBestConfederationTeamSummary
            {
                Confederation = x.Info!.Confederation,
                Team = x.Team,
                GroupCode = x.GroupCode,
                BestInConfederationProbability = RoundProbability(bestConfederationCounts.GetValueOrDefault((x.Info!.Confederation, x.Team)), iterations)
            })
            .OrderBy(x => x.Confederation, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.BestInConfederationProbability)
            .ThenBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new Wc2026SimulationResultSet
        {
            ModelsFolder = modelsFolder,
            Iterations = iterations,
            Seed = seed,
            MarketWeight = activeWeights.Market,
            EloWeight = activeWeights.Elo,
            EaWeight = activeWeights.Ea,
            KnockoutSimulated = true,
            KnockoutBracketSource = knockoutRules.Source,
            KnockoutBracketRules = knockoutRules.Rules.Select(r => new Wc2026KnockoutBracketRuleSummary
            {
                MatchNumber = r.MatchNumber,
                Stage = "round_of_32",
                Source = r.Source,
                Slot1 = r.Slot1.Raw,
                Slot2 = r.Slot2.Raw,
                Slot1Groups = string.Join("|", r.Slot1.Groups),
                Slot2Groups = string.Join("|", r.Slot2.Groups)
            }).ToList(),
            Notes = $"Group-stage simulation uses blended match probabilities: {activeWeights.Market:P0} normalized market 1X2, {activeWeights.Elo:P0} Elo, {activeWeights.Ea:P0} EA nation strength. Ranks groups with FIFA-style MVP tiebreakers and selects 8 best third-place teams. Knockout bracket uses hardcoded official R32 slot order, fixed later-round pairing, and a third-place allocation table for the 1A/1B/1D/1E/1G/1I/1K/1L slots. Third-place allocation table source: {thirdPlaceAllocationTable.Source}; rows: {thirdPlaceAllocationTable.RowCount}.",
            Teams = teamSummaries,
            Groups = groupSummaries,
            PairComparisons = pairSummaries,
            TournamentPairComparisons = tournamentPairSummaries,
            BestConfederationTeams = bestConfederationSummaries
        };
    }


    private static Dictionary<string, TournamentRankRow> BuildInitialTournamentRanks(
        IReadOnlyList<TeamRef> allTeams,
        IReadOnlyDictionary<string, List<GroupStandingRow>> rankedByGroup,
        IReadOnlyList<GroupStandingRow> qualifiedThirds,
        Random rng)
    {
        var qualified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ranked in rankedByGroup.Values)
        {
            foreach (var row in ranked.Take(2))
                qualified.Add(row.Team);
        }
        foreach (var row in qualifiedThirds)
            qualified.Add(row.Team);

        var rows = allTeams.ToDictionary(x => x.Team, x => new TournamentRankRow(x.Team, x.GroupCode)
        {
            StageLevel = qualified.Contains(x.Team) ? 1 : 0,
            GroupRank = 5,
            RandomTieBreaker = rng.NextDouble()
        }, StringComparer.OrdinalIgnoreCase);

        foreach (var group in rankedByGroup)
        {
            for (var index = 0; index < group.Value.Count; index++)
            {
                var standing = group.Value[index];
                if (!rows.TryGetValue(standing.Team, out var row))
                    continue;
                row.GroupRank = index + 1;
                row.Points = standing.Points;
                row.GoalDifference = standing.GoalDifference;
                row.GoalsFor = standing.GoalsFor;
                row.RandomTieBreaker = rng.NextDouble();
            }
        }

        return rows;
    }

    private static void SetTournamentStage(IReadOnlyDictionary<string, TournamentRankRow> tournamentRanks, IEnumerable<string> teams, int stageLevel)
    {
        foreach (var team in teams)
        {
            if (tournamentRanks.TryGetValue(team, out var row))
                row.StageLevel = Math.Max(row.StageLevel, stageLevel);
        }
    }

    private static void CountTournamentHigherPairs(
        IReadOnlyDictionary<string, TournamentRankRow> tournamentRanks,
        Dictionary<(string Higher, string Lower), int> higherCounts)
    {
        var teams = tournamentRanks.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < teams.Count; i++)
        {
            for (var j = i + 1; j < teams.Count; j++)
            {
                var first = tournamentRanks[teams[i]];
                var second = tournamentRanks[teams[j]];
                var higher = CompareTournamentRank(first, second) <= 0 ? first : second;
                var lower = ReferenceEquals(higher, first) ? second : first;
                higherCounts[(higher.Team, lower.Team)] = higherCounts.GetValueOrDefault((higher.Team, lower.Team)) + 1;
            }
        }
    }

    private static void CountBestConfederationTeams(
        IReadOnlyDictionary<string, TournamentRankRow> tournamentRanks,
        Dictionary<(string Confederation, string Team), int> bestCounts)
    {
        var rowsByConfederation = tournamentRanks.Values
            .Select(row => new { Row = row, Info = TeamConfederationCatalog.TryGetByTeam(row.Team) })
            .Where(x => x.Info is not null)
            .GroupBy(x => x.Info!.Confederation, StringComparer.OrdinalIgnoreCase);

        foreach (var group in rowsByConfederation)
        {
            var best = group
                .Select(x => x.Row)
                .OrderBy(x => x, Comparer<TournamentRankRow>.Create(CompareTournamentRank))
                .FirstOrDefault();

            if (best is null)
                continue;

            var info = TeamConfederationCatalog.TryGetByTeam(best.Team);
            if (info is null)
                continue;

            bestCounts[(info.Confederation, best.Team)] = bestCounts.GetValueOrDefault((info.Confederation, best.Team)) + 1;
        }
    }

    private static int CompareTournamentRank(TournamentRankRow left, TournamentRankRow right)
    {
        // Higher tournament finish is primarily the furthest stage reached.
        // Same-stage ties are broken with group-stage quality and finally random drawing of lots.
        var stage = right.StageLevel.CompareTo(left.StageLevel);
        if (stage != 0) return stage;
        var groupRank = left.GroupRank.CompareTo(right.GroupRank);
        if (groupRank != 0) return groupRank;
        var points = right.Points.CompareTo(left.Points);
        if (points != 0) return points;
        var gd = right.GoalDifference.CompareTo(left.GoalDifference);
        if (gd != 0) return gd;
        var gf = right.GoalsFor.CompareTo(left.GoalsFor);
        if (gf != 0) return gf;
        return left.RandomTieBreaker.CompareTo(right.RandomTieBreaker);
    }

    private static void SimulateKnockout(
        IReadOnlyDictionary<string, List<GroupStandingRow>> rankedByGroup,
        IReadOnlyList<GroupStandingRow> qualifiedThirds,
        KnockoutBracketPlan bracket,
        OfficialThirdPlaceAllocationTable thirdPlaceAllocationTable,
        IReadOnlyDictionary<string, TeamAccum> accum,
        IReadOnlyDictionary<string, TournamentRankRow> tournamentRanks,
        IReadOnlyDictionary<string, EloTeamRating> eloByTeam,
        IReadOnlyDictionary<string, NationRatingSeed> seedByTeam,
        Wc2026SimulationWeights weights,
        Random rng)
    {
        var thirdPlaceAllocation = thirdPlaceAllocationTable.Resolve(qualifiedThirds.Select(x => x.GroupCode));
        var roundOf32Teams = new List<(string Team1, string Team2)>();

        foreach (var rule in bracket.Rules.OrderBy(x => x.MatchNumber))
        {
            var team1 = ResolveBracketSlot(rule.Slot1, rankedByGroup, qualifiedThirds, thirdPlaceAllocation);
            var team2 = ResolveBracketSlot(rule.Slot2, rankedByGroup, qualifiedThirds, thirdPlaceAllocation);
            if (!string.IsNullOrWhiteSpace(team1) && !string.IsNullOrWhiteSpace(team2) && !string.Equals(team1, team2, StringComparison.OrdinalIgnoreCase))
                roundOf32Teams.Add((team1, team2));
        }

        var roundOf16 = SimulateKnockoutRound(roundOf32Teams, accum, a => a.ReachRoundOf16++, eloByTeam, seedByTeam, weights, rng);
        SetTournamentStage(tournamentRanks, roundOf16, 2);
        var quarterFinal = SimulateKnockoutRound(PairSequentially(roundOf16), accum, a => a.ReachQuarterFinal++, eloByTeam, seedByTeam, weights, rng);
        SetTournamentStage(tournamentRanks, quarterFinal, 3);
        var semiFinal = SimulateKnockoutRound(PairSequentially(quarterFinal), accum, a => a.ReachSemiFinal++, eloByTeam, seedByTeam, weights, rng);
        SetTournamentStage(tournamentRanks, semiFinal, 4);
        var final = SimulateKnockoutRound(PairSequentially(semiFinal), accum, a => a.ReachFinal++, eloByTeam, seedByTeam, weights, rng);
        SetTournamentStage(tournamentRanks, final, 5);
        var winner = SimulateKnockoutRound(PairSequentially(final), accum, a => a.Winner++, eloByTeam, seedByTeam, weights, rng);
        SetTournamentStage(tournamentRanks, winner, 6);
    }

    private static List<string> SimulateKnockoutRound(
        IReadOnlyList<(string Team1, string Team2)> matches,
        IReadOnlyDictionary<string, TeamAccum> accum,
        Action<TeamAccum> incrementWinnerStage,
        IReadOnlyDictionary<string, EloTeamRating> eloByTeam,
        IReadOnlyDictionary<string, NationRatingSeed> seedByTeam,
        Wc2026SimulationWeights weights,
        Random rng)
    {
        var winners = new List<string>();
        foreach (var (team1, team2) in matches)
        {
            var winner = SimulateKnockoutWinner(team1, team2, eloByTeam, seedByTeam, weights, rng);
            winners.Add(winner);
            if (accum.TryGetValue(winner, out var teamAccum))
                incrementWinnerStage(teamAccum);
        }
        return winners;
    }

    private static List<(string Team1, string Team2)> PairSequentially(IReadOnlyList<string> teams)
    {
        var pairs = new List<(string Team1, string Team2)>();
        for (var i = 0; i + 1 < teams.Count; i += 2)
            pairs.Add((teams[i], teams[i + 1]));
        return pairs;
    }

    private static string SimulateKnockoutWinner(
        string team1,
        string team2,
        IReadOnlyDictionary<string, EloTeamRating> eloByTeam,
        IReadOnlyDictionary<string, NationRatingSeed> seedByTeam,
        Wc2026SimulationWeights weights,
        Random rng)
    {
        // Knockout stage has no 1X2 market prices in this MVP. Passing null odds makes the
        // blend automatically renormalize to Elo + EA only. Draw probability is converted
        // into extra-time/penalty coin-flip probability.
        var p = BlendedMatchProbabilities(team1, team2, null, eloByTeam, seedByTeam, weights);
        var team1Advance = p.HomeWin + (0.5 * p.Draw);
        return rng.NextDouble() < team1Advance ? team1 : team2;
    }

    private static string? ResolveBracketSlot(
        KnockoutSlotSpec slot,
        IReadOnlyDictionary<string, List<GroupStandingRow>> rankedByGroup,
        IReadOnlyList<GroupStandingRow> qualifiedThirds,
        IReadOnlyDictionary<string, string> thirdPlaceAllocation)
    {
        if (slot.Rank is 1 or 2)
        {
            foreach (var group in slot.Groups)
            {
                if (rankedByGroup.TryGetValue(group, out var ranked) && ranked.Count >= slot.Rank.Value)
                    return ranked[slot.Rank.Value - 1].Team;
            }
            return null;
        }

        if (slot.Rank == 3)
        {
            var anchor = slot.ThirdPlaceAnchor;
            if (!string.IsNullOrWhiteSpace(anchor) && thirdPlaceAllocation.TryGetValue(anchor, out var allocatedThirdSlot))
            {
                var allocatedGroup = allocatedThirdSlot.Trim().TrimStart('3').ToUpperInvariant();
                var row = qualifiedThirds.FirstOrDefault(x => string.Equals(x.GroupCode, allocatedGroup, StringComparison.OrdinalIgnoreCase));
                if (row is not null)
                    return row.Team;
            }

            // Should be unreachable when the official table covers the qualified third-place set.
            // Keep a safe fallback so malformed custom calendars do not crash the full simulation.
            foreach (var row in qualifiedThirds)
            {
                if (slot.Groups.Count == 0 || slot.Groups.Contains(row.GroupCode, StringComparer.OrdinalIgnoreCase))
                    return row.Team;
            }
        }

        return null;
    }

    private static KnockoutBracketPlan BuildKnockoutBracketRules(Wc2026CalendarSet? calendar, Wc2026GroupSet groups)
        => new("official_hardcoded_r32_and_path", BuildOfficialRoundOf32Rules());

    private static List<KnockoutBracketRule> BuildOfficialRoundOf32Rules()
    {
        var raw = new (string Slot1, string Slot2)[]
        {
            ("1E", "3A/3B/3C/3D/3F"),
            ("1I", "3C/3D/3F/3G/3H"),
            ("2A", "2B"),
            ("1F", "2C"),

            ("2K", "2L"),
            ("1H", "2J"),
            ("1D", "3B/3E/3F/3I/3J"),
            ("1G", "3A/3E/3H/3I/3J"),

            ("1C", "2F"),
            ("2E", "2I"),
            ("1A", "3C/3E/3F/3H/3I"),
            ("1L", "3E/3H/3I/3J/3K"),

            ("1J", "2H"),
            ("2D", "2G"),
            ("1B", "3E/3F/3G/3I/3J"),
            ("1K", "3D/3E/3I/3J/3L")
        };

        return raw.Select((x, index) => new KnockoutBracketRule(
            MatchNumber: index + 1,
            Source: "official_hardcoded_r32_and_path",
            Slot1: ParseKnockoutSlot(x.Slot1),
            Slot2: ParseKnockoutSlot(x.Slot2, GetThirdPlaceAnchor(x.Slot1)))).ToList();
    }

    private static string? GetThirdPlaceAnchor(string oppositeSlot)
    {
        var parsed = ParseKnockoutSlot(oppositeSlot);
        return parsed.Rank == 1 && parsed.Groups.Count == 1 ? $"1{parsed.Groups[0]}" : null;
    }

    private static KnockoutSlotSpec ParseKnockoutSlot(string raw, string? thirdPlaceAnchor = null)
    {
        var value = NormalizeSlotText(raw);
        if (string.IsNullOrWhiteSpace(value))
            return new KnockoutSlotSpec(raw, null, [], thirdPlaceAnchor);

        var rank = TryParseRank(value);
        var groups = ExtractGroupCodes(value);
        var canonical = CanonicalizeSlot(raw, rank, groups);
        return new KnockoutSlotSpec(canonical, rank, groups, thirdPlaceAnchor);
    }

    private static string NormalizeSlotText(string raw)
        => (raw ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty);

    private static string CanonicalizeSlot(string raw, int? rank, IReadOnlyList<string> groups)
    {
        if (rank is null || groups.Count == 0)
            return raw;

        // SofaScore placeholders are inconsistent: most slots are 1G / 2H,
        // but some are returned as G1 / H2. Internally and in reports we use
        // the canonical FIFA-like format rank + group, e.g. 1G, 2H, 3A/3B.
        return string.Join("/", groups.Select(group => $"{rank.Value}{group}"));
    }

    private static int? TryParseRank(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '1' or '2' or '3')
                return ch - '0';
        }
        return null;
    }

    private static List<string> ExtractGroupCodes(string value)
    {
        var groups = new List<string>();
        foreach (var ch in value)
        {
            if (ch >= 'A' && ch <= 'L')
            {
                var group = ch.ToString();
                if (!groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                    groups.Add(group);
            }
        }
        return groups;
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

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return default;
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
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
        await WriteTournamentPairComparisonCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-tournament-pair-comparisons.csv"), result, overwrite, cancellationToken);
        await WriteBestConfederationCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-best-confederation-team-probabilities.csv"), result, overwrite, cancellationToken);
        await WriteStageProbabilityCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-stage-probabilities.csv"), result, overwrite, cancellationToken);
        await WriteKnockoutBracketRulesCsvAsync(Path.Combine(outputFolder, "wc2026-simulation-knockout-bracket-rules.csv"), result, overwrite, cancellationToken);
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
        await writer.WriteLineAsync("team,group_code,elo_rating,ea_top11_rating,ea_top26_rating,ea_confidence,avg_points,avg_goals_for,avg_goals_against,avg_goal_difference,win_group_probability,top_two_probability,third_place_probability,third_place_qualified_probability,qualified_to_round_of_32_probability,reach_round_of_16_probability,reach_quarter_final_probability,reach_semi_final_probability,reach_final_probability,winner_probability,eliminated_in_group_probability");
        foreach (var t in result.Teams)
        {
            var values = new[]
            {
                t.Team, t.GroupCode, t.EloRating.ToString(), t.EaTop11Rating.ToString("0.###"), t.EaTop26Rating.ToString("0.###"), t.EaConfidence,
                t.AvgPoints.ToString("0.###"), t.AvgGoalsFor.ToString("0.###"), t.AvgGoalsAgainst.ToString("0.###"), t.AvgGoalDifference.ToString("0.###"),
                t.WinGroupProbability.ToString("0.######"), t.TopTwoProbability.ToString("0.######"), t.ThirdPlaceProbability.ToString("0.######"),
                t.ThirdPlaceQualifiedProbability.ToString("0.######"), t.QualifiedToRoundOf32Probability.ToString("0.######"),
                t.ReachRoundOf16Probability.ToString("0.######"), t.ReachQuarterFinalProbability.ToString("0.######"),
                t.ReachSemiFinalProbability.ToString("0.######"), t.ReachFinalProbability.ToString("0.######"),
                t.WinnerProbability.ToString("0.######"), t.EliminatedInGroupProbability.ToString("0.######")
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



    private static async Task WriteTournamentPairComparisonCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("team1,team2,team1_finish_higher_probability,team2_finish_higher_probability");
        foreach (var pair in result.TournamentPairComparisons)
        {
            var values = new[]
            {
                pair.Team1, pair.Team2,
                pair.Team1FinishHigherProbability.ToString("0.######"),
                pair.Team2FinishHigherProbability.ToString("0.######")
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }


    private static async Task WriteBestConfederationCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("confederation,team,group_code,best_in_confederation_probability");
        foreach (var row in result.BestConfederationTeams.OrderBy(x => x.Confederation).ThenByDescending(x => x.BestInConfederationProbability))
        {
            var values = new[]
            {
                row.Confederation, row.Team, row.GroupCode, row.BestInConfederationProbability.ToString("0.######")
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteStageProbabilityCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("team,group_code,qualified_to_round_of_32_probability,reach_round_of_16_probability,reach_quarter_final_probability,reach_semi_final_probability,reach_final_probability,winner_probability");
        foreach (var t in result.Teams.OrderByDescending(x => x.WinnerProbability).ThenByDescending(x => x.ReachFinalProbability))
        {
            var values = new[]
            {
                t.Team, t.GroupCode,
                t.QualifiedToRoundOf32Probability.ToString("0.######"),
                t.ReachRoundOf16Probability.ToString("0.######"),
                t.ReachQuarterFinalProbability.ToString("0.######"),
                t.ReachSemiFinalProbability.ToString("0.######"),
                t.ReachFinalProbability.ToString("0.######"),
                t.WinnerProbability.ToString("0.######")
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteKnockoutBracketRulesCsvAsync(string path, Wc2026SimulationResultSet result, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("match_number,stage,source,slot1,slot2,slot1_groups,slot2_groups");
        foreach (var r in result.KnockoutBracketRules.OrderBy(x => x.MatchNumber))
        {
            var values = new[] { r.MatchNumber.ToString(), r.Stage, r.Source, r.Slot1, r.Slot2, r.Slot1Groups, r.Slot2Groups };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static double Round(double value) => Math.Round(value, 6);
    private static double RoundProbability(int count, int iterations) => Round(count / (double)iterations);


    private sealed class OfficialThirdPlaceAllocationTable
    {
        private static readonly string[] ThirdWinnerAnchors = ["1A", "1B", "1D", "1E", "1G", "1I", "1K", "1L"];

        private static readonly IReadOnlyDictionary<string, string[]> AllowedThirdGroupsByAnchor = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["1A"] = ["C", "E", "F", "H", "I"],
            ["1B"] = ["E", "F", "G", "I", "J"],
            ["1D"] = ["B", "E", "F", "I", "J"],
            ["1E"] = ["A", "B", "C", "D", "F"],
            ["1G"] = ["A", "E", "H", "I", "J"],
            ["1I"] = ["C", "D", "F", "G", "H"],
            ["1K"] = ["D", "E", "I", "J", "L"],
            ["1L"] = ["E", "H", "I", "J", "K"]
        };

        private OfficialThirdPlaceAllocationTable(string source, Dictionary<string, Dictionary<string, string>> rows)
        {
            Source = source;
            Rows = rows;
        }

        public string Source { get; }
        public int RowCount => Rows.Count;
        private Dictionary<string, Dictionary<string, string>> Rows { get; }

        public static OfficialThirdPlaceAllocationTable CreateDefault()
        {
            var csvPath = TryFindAnnexCsv();
            if (csvPath is not null)
                return new OfficialThirdPlaceAllocationTable($"csv:{csvPath}", LoadFromCsv(csvPath));

            // Fallback table keeps the simulator usable even when the external CSV is not
            // deployed. The preferred production path is the CSV table at
            // data/raw/bracket/third-place-allocation-annex-c.csv.
            var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var groups in Combinations(["A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L"], 8))
            {
                var key = string.Concat(groups.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                rows[key] = BuildAllocationFor(groups);
            }

            return new OfficialThirdPlaceAllocationTable("generated_fallback_from_official_slot_pools", rows);
        }

        private static string? TryFindAnnexCsv()
        {
            const string relative = "data/raw/bracket/third-place-allocation-annex-c.csv";
            var candidates = new List<string>
            {
                Path.Combine(Environment.CurrentDirectory, relative),
                Path.Combine(AppContext.BaseDirectory, relative)
            };

            var dir = new DirectoryInfo(Environment.CurrentDirectory);
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
                candidates.Add(Path.Combine(dir.FullName, relative));

            return candidates.FirstOrDefault(File.Exists);
        }

        private static Dictionary<string, Dictionary<string, string>> LoadFromCsv(string path)
        {
            var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(path);
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cells = SimpleCsv.ParseLine(line).Select(x => x.Trim()).ToList();
                if (cells.Count < 9)
                    continue;

                var key = string.Concat(cells[0].Where(char.IsLetter).Select(char.ToUpperInvariant).OrderBy(x => x));
                rows[key] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1A"] = NormalizeThirdSlot(cells[1]),
                    ["1B"] = NormalizeThirdSlot(cells[2]),
                    ["1D"] = NormalizeThirdSlot(cells[3]),
                    ["1E"] = NormalizeThirdSlot(cells[4]),
                    ["1G"] = NormalizeThirdSlot(cells[5]),
                    ["1I"] = NormalizeThirdSlot(cells[6]),
                    ["1K"] = NormalizeThirdSlot(cells[7]),
                    ["1L"] = NormalizeThirdSlot(cells[8])
                };
            }

            if (rows.Count != 495)
                throw new InvalidOperationException($"Third-place allocation CSV must contain 495 rows, but found {rows.Count}: {path}");

            return rows;
        }

        private static string NormalizeThirdSlot(string value)
        {
            var group = new string((value ?? string.Empty).Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
            if (group.Length != 1 || group[0] < 'A' || group[0] > 'L')
                throw new InvalidOperationException($"Invalid third-place allocation slot: '{value}'.");
            return $"3{group}";
        }

        public IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> qualifiedThirdGroups)
        {
            var key = string.Concat(qualifiedThirdGroups
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            if (Rows.TryGetValue(key, out var row))
                return row;

            throw new InvalidOperationException($"No third-place allocation row found for qualified third-place groups '{key}'. Expected exactly 8 groups A-L.");
        }

        private static Dictionary<string, string> BuildAllocationFor(IReadOnlyList<string> groups)
        {
            var groupSet = groups.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var best = Search(0, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (best is null)
                throw new InvalidOperationException($"Could not build third-place allocation for key '{string.Concat(groups)}'.");
            return best;

            Dictionary<string, string>? Search(int anchorIndex, Dictionary<string, string> current, HashSet<string> usedGroups)
            {
                if (anchorIndex >= ThirdWinnerAnchors.Length)
                    return new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

                var anchor = ThirdWinnerAnchors[anchorIndex];
                var candidates = AllowedThirdGroupsByAnchor[anchor]
                    .Where(groupSet.Contains)
                    .Where(g => !usedGroups.Contains(g))
                    .OrderByDescending(g => GroupPreference(anchor, g))
                    .ThenBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var group in candidates)
                {
                    current[anchor] = $"3{group}";
                    usedGroups.Add(group);
                    var result = Search(anchorIndex + 1, current, usedGroups);
                    if (result is not null)
                        return result;
                    usedGroups.Remove(group);
                    current.Remove(anchor);
                }

                return null;
            }
        }

        private static int GroupPreference(string anchor, string group)
        {
            // Slot-specific preferences make the valid matching deterministic and stable.
            // They are not used for ranking teams; they only break ties between legal third-place slots.
            var preference = anchor switch
            {
                "1A" => "HCEFI",
                "1B" => "JGEFI",
                "1D" => "BIEJF",
                "1E" => "CDBFA",
                "1G" => "AHIJE",
                "1I" => "FGHCD",
                "1K" => "LDEIJ",
                "1L" => "KEHJI",
                _ => "ABCDEFGHIJKL"
            };

            var index = preference.IndexOf(group, StringComparison.OrdinalIgnoreCase);
            return index < 0 ? 0 : preference.Length - index;
        }

        private static IEnumerable<List<string>> Combinations(IReadOnlyList<string> values, int take)
        {
            var buffer = new string[take];
            foreach (var row in Recurse(0, 0))
                yield return row;

            IEnumerable<List<string>> Recurse(int start, int depth)
            {
                if (depth == take)
                {
                    yield return buffer.ToList();
                    yield break;
                }

                for (var i = start; i <= values.Count - (take - depth); i++)
                {
                    buffer[depth] = values[i];
                    foreach (var row in Recurse(i + 1, depth + 1))
                        yield return row;
                }
            }
        }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string First, string Second)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string First, string Second) x, (string First, string Second) y)
            => string.Equals(x.First, y.First, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Second, y.Second, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string First, string Second) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.First), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Second));
    }

    private sealed record KnockoutBracketPlan(string Source, List<KnockoutBracketRule> Rules);
    private sealed record KnockoutBracketRule(int MatchNumber, string Source, KnockoutSlotSpec Slot1, KnockoutSlotSpec Slot2);
    private sealed record KnockoutSlotSpec(string Raw, int? Rank, List<string> Groups, string? ThirdPlaceAnchor = null);

    private sealed record TeamRef(string GroupCode, string Team);
    private sealed record OutcomeProbabilities(double HomeWin, double Draw, double AwayWin);
    private sealed record SimulatedMatchResult(long EventId, string HomeTeam, string AwayTeam, int HomeGoals, int AwayGoals);

    private sealed class TournamentRankRow
    {
        public TournamentRankRow(string team, string groupCode)
        {
            Team = team;
            GroupCode = groupCode;
        }

        public string Team { get; }
        public string GroupCode { get; }
        public int StageLevel { get; set; }
        public int GroupRank { get; set; } = 5;
        public int Points { get; set; }
        public int GoalDifference { get; set; }
        public int GoalsFor { get; set; }
        public double RandomTieBreaker { get; set; }
    }

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
        public int ReachRoundOf16 { get; set; }
        public int ReachQuarterFinal { get; set; }
        public int ReachSemiFinal { get; set; }
        public int ReachFinal { get; set; }
        public int Winner { get; set; }
        public Dictionary<int, int> RankCounts { get; } = new() { [1] = 0, [2] = 0, [3] = 0, [4] = 0 };
    }
}
