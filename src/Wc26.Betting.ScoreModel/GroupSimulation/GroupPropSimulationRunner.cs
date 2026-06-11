using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Utilities;
using Wc26.Betting.ScoreModel.ScoreMatrix;

namespace Wc26.Betting.ScoreModel.GroupSimulation;

public sealed class GroupPropSimulationRunner
{
    public async Task<GroupPropSimulationSet> RunAndWriteAsync(
        string scoreMatrixFile,
        string outputFolder,
        int iterations,
        int seed,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = await RunFromScoreMatrixFileAsync(scoreMatrixFile, iterations, seed, cancellationToken);
        Directory.CreateDirectory(outputFolder);

        await WriteJsonAsync(Path.Combine(outputFolder, "wc26-group-prop-simulation.json"), set, overwrite, cancellationToken);
        await WritePropProbabilitiesCsvAsync(Path.Combine(outputFolder, "wc26-group-special-prop-probabilities.csv"), set, overwrite, cancellationToken);
        await WriteDiagnosticsCsvAsync(Path.Combine(outputFolder, "wc26-group-simulation-diagnostics.csv"), set, overwrite, cancellationToken);

        return set;
    }

    public async Task<GroupPropSimulationSet> RunFromScoreMatrixFileAsync(string scoreMatrixFile, int iterations, int seed, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scoreMatrixFile))
            throw new ArgumentException("Score matrix file is required.", nameof(scoreMatrixFile));
        if (!File.Exists(scoreMatrixFile))
            throw new FileNotFoundException("Score matrix file was not found.", scoreMatrixFile);
        if (iterations < 1000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "Use at least 1000 iterations.");

        var matrixSet = await ReadScoreMatrixAsync(scoreMatrixFile, cancellationToken);
        var fixtures = matrixSet.Fixtures
            .Where(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var warnings = new List<string>();
        var validationErrors = Validate(fixtures);
        warnings.AddRange(validationErrors);

        var rng = new Random(seed);
        var snapshots = new List<SimulatedGroupSnapshot>();
        var diagnostics = new List<GroupSimulationDiagnostic>();
        var propProbabilities = new List<GroupPropProbability>();

        var groups = fixtures
            .GroupBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var groupFixtures = group.ToList();
            var teams = groupFixtures
                .SelectMany(x => new[] { x.TeamA, x.TeamB })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preparedFixtures = groupFixtures.Select(PrepareFixture).ToList();
            snapshots.Clear();

            for (var i = 0; i < iterations; i++)
                snapshots.Add(SimulateGroup(group.Key, teams, preparedFixtures, rng));

            diagnostics.Add(BuildDiagnostic(group.Key, groupFixtures.Count, teams, snapshots));
            propProbabilities.AddRange(BuildPropProbabilities(group.Key, snapshots));
        }

        return new GroupPropSimulationSet
        {
            SourceScoreMatrixFile = scoreMatrixFile,
            Iterations = iterations,
            Seed = seed,
            GroupCount = groups.Count,
            FixtureCount = matrixSet.FixtureCount,
            ValidFixtureCount = fixtures.Count,
            PropProbabilities = propProbabilities,
            Diagnostics = diagnostics,
            ValidationErrors = validationErrors,
            Warnings = warnings
        };
    }

    private static PreparedFixture PrepareFixture(FixtureScoreMatrix fixture)
    {
        var scores = fixture.Scores
            .Where(x => x.Probability > 0)
            .OrderBy(x => x.ScoreA)
            .ThenBy(x => x.ScoreB)
            .ToList();

        var cumulative = new List<PreparedScore>(scores.Count);
        var running = 0.0d;
        foreach (var score in scores)
        {
            running += score.Probability;
            cumulative.Add(new PreparedScore(score.ScoreA, score.ScoreB, running));
        }

        if (cumulative.Count == 0)
            throw new InvalidOperationException($"Fixture {fixture.TeamA} - {fixture.TeamB} has empty score matrix.");

        cumulative[^1] = cumulative[^1] with { CumulativeProbability = 1.0d };
        return new PreparedFixture(fixture.TeamA, fixture.TeamB, cumulative);
    }

    private static SimulatedGroupSnapshot SimulateGroup(string groupCode, IReadOnlyList<string> teams, IReadOnlyList<PreparedFixture> fixtures, Random rng)
    {
        var table = teams.ToDictionary(x => x, _ => new TeamTableRow(), StringComparer.OrdinalIgnoreCase);
        var totalGoals = 0;
        var draws = 0;
        var zeroZero = 0;
        var oneNil = 0;
        var twoOne = 0;
        var twoTwo = 0;
        var threeTwo = 0;

        foreach (var fixture in fixtures)
        {
            var score = SampleScore(fixture, rng);
            var a = table[fixture.TeamA];
            var b = table[fixture.TeamB];

            a.GoalsFor += score.ScoreA;
            a.GoalsAgainst += score.ScoreB;
            b.GoalsFor += score.ScoreB;
            b.GoalsAgainst += score.ScoreA;

            totalGoals += score.ScoreA + score.ScoreB;

            if (score.ScoreA > score.ScoreB)
            {
                a.Points += 3;
            }
            else if (score.ScoreA < score.ScoreB)
            {
                b.Points += 3;
            }
            else
            {
                a.Points += 1;
                b.Points += 1;
                draws++;
            }

            if (score.ScoreA == 0 && score.ScoreB == 0)
                zeroZero++;
            if ((score.ScoreA == 1 && score.ScoreB == 0) || (score.ScoreA == 0 && score.ScoreB == 1))
                oneNil++;
            if ((score.ScoreA == 2 && score.ScoreB == 1) || (score.ScoreA == 1 && score.ScoreB == 2))
                twoOne++;
            if (score.ScoreA == 2 && score.ScoreB == 2)
                twoTwo++;
            if ((score.ScoreA == 3 && score.ScoreB == 2) || (score.ScoreA == 2 && score.ScoreB == 3))
                threeTwo++;
        }

        var ranked = table
            .Select(x => new RankedTeam(x.Key, x.Value.Points, x.Value.GoalDifference, x.Value.GoalsFor, rng.NextDouble()))
            .OrderByDescending(x => x.Points)
            .ThenByDescending(x => x.GoalDifference)
            .ThenByDescending(x => x.GoalsFor)
            .ThenBy(x => x.RandomTieBreaker)
            .ToList();

        return new SimulatedGroupSnapshot
        {
            TotalGoals = totalGoals,
            DrawCount = draws,
            ZeroZeroCount = zeroZero,
            OneNilCount = oneNil,
            TwoOneCount = twoOne,
            TwoTwoCount = twoTwo,
            ThreeTwoCount = threeTwo,
            TeamsScoredZeroGoals = table.Values.Count(x => x.GoalsFor == 0),
            TeamsConcededZeroGoals = table.Values.Count(x => x.GoalsAgainst == 0),
            TeamsWith9Points = table.Values.Count(x => x.Points == 9),
            TeamsWith0Points = table.Values.Count(x => x.Points == 0),
            FirstPlacePoints = ranked.ElementAtOrDefault(0)?.Points ?? 0,
            SecondPlacePoints = ranked.ElementAtOrDefault(1)?.Points ?? 0,
            ThirdPlacePoints = ranked.ElementAtOrDefault(2)?.Points ?? 0,
            FourthPlacePoints = ranked.ElementAtOrDefault(3)?.Points ?? 0
        };
    }

    private static PreparedScore SampleScore(PreparedFixture fixture, Random rng)
    {
        var value = rng.NextDouble();
        var scores = fixture.Scores;
        var lo = 0;
        var hi = scores.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (value <= scores[mid].CumulativeProbability)
                hi = mid;
            else
                lo = mid + 1;
        }

        return scores[lo];
    }

    private static GroupSimulationDiagnostic BuildDiagnostic(string groupCode, int fixtureCount, IReadOnlyList<string> teams, IReadOnlyList<SimulatedGroupSnapshot> snapshots)
    {
        var n = snapshots.Count;
        return new GroupSimulationDiagnostic
        {
            GroupCode = groupCode,
            FixtureCount = fixtureCount,
            TeamCount = teams.Count,
            Teams = string.Join(" | ", teams),
            AvgGoals = snapshots.Average(x => x.TotalGoals),
            AvgDraws = snapshots.Average(x => x.DrawCount),
            AvgZeroZeroMatches = snapshots.Average(x => x.ZeroZeroCount),
            AvgOneNilMatches = snapshots.Average(x => x.OneNilCount),
            AvgTwoOneMatches = snapshots.Average(x => x.TwoOneCount),
            AvgTwoTwoMatches = snapshots.Average(x => x.TwoTwoCount),
            AvgThreeTwoMatches = snapshots.Average(x => x.ThreeTwoCount),
            AvgTeamsScoredZeroGoals = snapshots.Average(x => x.TeamsScoredZeroGoals),
            AvgTeamsConcededZeroGoals = snapshots.Average(x => x.TeamsConcededZeroGoals),
            AvgTeamsWith9Points = snapshots.Average(x => x.TeamsWith9Points),
            AvgTeamsWith0Points = snapshots.Average(x => x.TeamsWith0Points),
            AvgFirstPlacePoints = snapshots.Average(x => x.FirstPlacePoints),
            AvgSecondPlacePoints = snapshots.Average(x => x.SecondPlacePoints),
            AvgThirdPlacePoints = snapshots.Average(x => x.ThirdPlacePoints),
            AvgFourthPlacePoints = snapshots.Average(x => x.FourthPlacePoints),
            Status = fixtureCount == 6 && teams.Count == 4 && n > 0 ? "valid" : "invalid",
            Warning = fixtureCount == 6 && teams.Count == 4 ? string.Empty : $"Expected 6 fixtures and 4 teams, got {fixtureCount} fixtures and {teams.Count} teams."
        };
    }

    private static IEnumerable<GroupPropProbability> BuildPropProbabilities(string groupCode, IReadOnlyList<SimulatedGroupSnapshot> snapshots)
    {
        yield return BuildOverUnder(groupCode, "GroupTotalGoals", "Group total goals", "Group", 13.5, snapshots, x => x.TotalGoals, "Over");
        yield return BuildOverUnder(groupCode, "GroupTotalGoals", "Group total goals", "Group", 13.5, snapshots, x => x.TotalGoals, "Under");

        foreach (var line in HalfLines(0, 5))
        {
            yield return BuildOverUnder(groupCode, "DrawCount", "Number of draws", "Group", line, snapshots, x => x.DrawCount, "Over");
            yield return BuildOverUnder(groupCode, "DrawCount", "Number of draws", "Group", line, snapshots, x => x.DrawCount, "Under");
        }

        foreach (var line in HalfLines(0, 5))
        {
            yield return BuildOverUnder(groupCode, "ExactScoreCount_0_0", "Number of 0-0 matches", "Group", line, snapshots, x => x.ZeroZeroCount, "Over");
            yield return BuildOverUnder(groupCode, "ExactScoreCount_0_0", "Number of 0-0 matches", "Group", line, snapshots, x => x.ZeroZeroCount, "Under");
            yield return BuildOverUnder(groupCode, "ScorePairCount_1_0_0_1", "Number of 1-0 or 0-1 matches", "Group", line, snapshots, x => x.OneNilCount, "Over");
            yield return BuildOverUnder(groupCode, "ScorePairCount_1_0_0_1", "Number of 1-0 or 0-1 matches", "Group", line, snapshots, x => x.OneNilCount, "Under");
            yield return BuildOverUnder(groupCode, "ScorePairCount_2_1_1_2", "Number of 2-1 or 1-2 matches", "Group", line, snapshots, x => x.TwoOneCount, "Over");
            yield return BuildOverUnder(groupCode, "ScorePairCount_2_1_1_2", "Number of 2-1 or 1-2 matches", "Group", line, snapshots, x => x.TwoOneCount, "Under");
            yield return BuildOverUnder(groupCode, "ExactScoreCount_2_2", "Number of 2-2 matches", "Group", line, snapshots, x => x.TwoTwoCount, "Over");
            yield return BuildOverUnder(groupCode, "ExactScoreCount_2_2", "Number of 2-2 matches", "Group", line, snapshots, x => x.TwoTwoCount, "Under");
            yield return BuildOverUnder(groupCode, "ScorePairCount_3_2_2_3", "Number of 3-2 or 2-3 matches", "Group", line, snapshots, x => x.ThreeTwoCount, "Over");
            yield return BuildOverUnder(groupCode, "ScorePairCount_3_2_2_3", "Number of 3-2 or 2-3 matches", "Group", line, snapshots, x => x.ThreeTwoCount, "Under");
        }

        foreach (var line in HalfLines(0, 3))
        {
            yield return BuildOverUnder(groupCode, "TeamsScoredZeroGoals", "Teams scoring 0 goals", "Group", line, snapshots, x => x.TeamsScoredZeroGoals, "Over");
            yield return BuildOverUnder(groupCode, "TeamsScoredZeroGoals", "Teams scoring 0 goals", "Group", line, snapshots, x => x.TeamsScoredZeroGoals, "Under");
            yield return BuildOverUnder(groupCode, "TeamsConcededZeroGoals", "Teams conceding 0 goals", "Group", line, snapshots, x => x.TeamsConcededZeroGoals, "Over");
            yield return BuildOverUnder(groupCode, "TeamsConcededZeroGoals", "Teams conceding 0 goals", "Group", line, snapshots, x => x.TeamsConcededZeroGoals, "Under");
            yield return BuildOverUnder(groupCode, "TeamsWith9Points", "Teams with 9 points", "Group", line, snapshots, x => x.TeamsWith9Points, "Over");
            yield return BuildOverUnder(groupCode, "TeamsWith9Points", "Teams with 9 points", "Group", line, snapshots, x => x.TeamsWith9Points, "Under");
            yield return BuildOverUnder(groupCode, "TeamsWith0Points", "Teams with 0 points", "Group", line, snapshots, x => x.TeamsWith0Points, "Over");
            yield return BuildOverUnder(groupCode, "TeamsWith0Points", "Teams with 0 points", "Group", line, snapshots, x => x.TeamsWith0Points, "Under");
        }

        foreach (var line in HalfLines(0, 8))
        {
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "1st place points", "1", line, snapshots, x => x.FirstPlacePoints, "Over");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "1st place points", "1", line, snapshots, x => x.FirstPlacePoints, "Under");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "2nd place points", "2", line, snapshots, x => x.SecondPlacePoints, "Over");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "2nd place points", "2", line, snapshots, x => x.SecondPlacePoints, "Under");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "3rd place points", "3", line, snapshots, x => x.ThirdPlacePoints, "Over");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "4th place points", "4", line, snapshots, x => x.FourthPlacePoints, "Over");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "3rd place points", "3", line, snapshots, x => x.ThirdPlacePoints, "Under");
            yield return BuildOverUnder(groupCode, "RankPointsTotal", "4th place points", "4", line, snapshots, x => x.FourthPlacePoints, "Under");
        }
    }

    private static GroupPropProbability BuildOverUnder(
        string groupCode,
        string marketType,
        string marketName,
        string subject,
        double line,
        IReadOnlyList<SimulatedGroupSnapshot> snapshots,
        Func<SimulatedGroupSnapshot, int> selector,
        string side)
    {
        var probability = string.Equals(side, "Over", StringComparison.OrdinalIgnoreCase)
            ? snapshots.Count(x => selector(x) > line) / (double)snapshots.Count
            : snapshots.Count(x => selector(x) < line) / (double)snapshots.Count;

        return new GroupPropProbability
        {
            GroupCode = groupCode,
            MarketType = marketType,
            MarketName = marketName,
            Subject = subject,
            Side = side,
            Line = line,
            ModelProbability = probability,
            FairOdds = probability > 0 ? 1.0d / probability : 0.0d,
            MeanValue = snapshots.Average(selector)
        };
    }

    private static IEnumerable<double> HalfLines(int min, int max)
    {
        for (var i = min; i <= max; i++)
            yield return i + 0.5d;
    }

    private static List<string> Validate(IReadOnlyList<FixtureScoreMatrix> fixtures)
    {
        var errors = new List<string>();

        foreach (var fixture in fixtures.Where(x => string.IsNullOrWhiteSpace(x.GroupCode)))
            errors.Add($"Missing group: {fixture.TeamA} - {fixture.TeamB}");

        var groups = fixtures
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GroupCode) ? "<missing>" : x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups.Where(x => x.Key != "<missing>"))
        {
            var teams = group.SelectMany(x => new[] { x.TeamA, x.TeamB }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (group.Count() != 6)
                errors.Add($"Group {group.Key}: expected 6 fixtures, got {group.Count()}.");
            if (teams != 4)
                errors.Add($"Group {group.Key}: expected 4 teams, got {teams}.");
        }

        return errors;
    }

    private static async Task<FixtureScoreMatrixSet> ReadScoreMatrixAsync(string scoreMatrixFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(scoreMatrixFile);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(scoreMatrixFile);
            var set = await JsonSerializer.DeserializeAsync<FixtureScoreMatrixSet>(stream, cancellationToken: cancellationToken);
            return set ?? throw new InvalidOperationException($"Could not deserialize score matrix JSON: {scoreMatrixFile}");
        }

        return await ReadScoreMatrixCsvAsync(scoreMatrixFile, cancellationToken);
    }

    private static async Task<FixtureScoreMatrixSet> ReadScoreMatrixCsvAsync(string scoreMatrixFile, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(scoreMatrixFile, cancellationToken);
        if (lines.Length == 0)
            throw new InvalidOperationException($"Score matrix CSV is empty: {scoreMatrixFile}");

        var headers = SimpleCsv.ParseLine(lines[0]);
        var headerByName = headers
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var fixtures = new Dictionary<string, FixtureScoreMatrixBuilderState>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var cells = SimpleCsv.ParseLine(lines[i]);
            var group = Get(cells, headerByName, "Group");
            var teamA = Get(cells, headerByName, "TeamA");
            var teamB = Get(cells, headerByName, "TeamB");
            var key = $"{group}|{teamA}|{teamB}";

            if (!fixtures.TryGetValue(key, out var state))
            {
                state = new FixtureScoreMatrixBuilderState
                {
                    GroupCode = group,
                    MatchDate = Get(cells, headerByName, "MatchDate"),
                    MatchTime = Get(cells, headerByName, "MatchTime"),
                    TeamA = teamA,
                    TeamB = teamB,
                    TeamAXg = GetDouble(cells, headerByName, "TeamAXg"),
                    TeamBXg = GetDouble(cells, headerByName, "TeamBXg"),
                    TotalLambda = GetDouble(cells, headerByName, "TotalLambda"),
                    TotalLine = GetDouble(cells, headerByName, "TotalLine"),
                    GridMass = GetDouble(cells, headerByName, "GridMass"),
                    Status = Get(cells, headerByName, "Status", "valid"),
                    Warning = Get(cells, headerByName, "Warning")
                };
                fixtures.Add(key, state);
            }

            state.Scores.Add(new FixtureScoreProbability
            {
                ScoreA = GetInt(cells, headerByName, "ScoreA"),
                ScoreB = GetInt(cells, headerByName, "ScoreB"),
                RawProbability = GetDouble(cells, headerByName, "RawProbability"),
                Probability = GetDouble(cells, headerByName, "Probability")
            });
        }

        var resultFixtures = fixtures.Values.Select(x => new FixtureScoreMatrix
        {
            MatchDate = x.MatchDate,
            MatchTime = x.MatchTime,
            GroupCode = x.GroupCode,
            TeamA = x.TeamA,
            TeamB = x.TeamB,
            TeamAXg = x.TeamAXg,
            TeamBXg = x.TeamBXg,
            TotalLambda = x.TotalLambda,
            TotalLine = x.TotalLine,
            GridMass = x.GridMass,
            Status = x.Status,
            Warning = x.Warning,
            Scores = x.Scores
        }).ToList();

        return new FixtureScoreMatrixSet
        {
            SourceMarketXgFile = scoreMatrixFile,
            FixtureCount = resultFixtures.Count,
            ValidFixtureCount = resultFixtures.Count(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            InvalidFixtureCount = resultFixtures.Count(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            Fixtures = resultFixtures
        };
    }

    private static async Task WriteJsonAsync(string path, GroupPropSimulationSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task WritePropProbabilitiesCsvAsync(string path, GroupPropSimulationSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MarketType,MarketName,Subject,Line,Side,ModelProbability,FairOdds,MeanValue");
        foreach (var p in set.PropProbabilities.OrderBy(x => x.GroupCode).ThenBy(x => x.MarketType).ThenBy(x => x.Subject).ThenBy(x => x.Line).ThenBy(x => x.Side))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(p.GroupCode), Csv(p.MarketType), Csv(p.MarketName), Csv(p.Subject), D(p.Line), Csv(p.Side), D(p.ModelProbability), D(p.FairOdds), D(p.MeanValue)
            }));
        }
    }

    private static async Task WriteDiagnosticsCsvAsync(string path, GroupPropSimulationSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,FixtureCount,TeamCount,Teams,AvgGoals,AvgDraws,AvgZeroZeroMatches,AvgOneNilMatches,AvgTwoOneMatches,AvgTwoTwoMatches,AvgThreeTwoMatches,AvgTeamsScoredZeroGoals,AvgTeamsConcededZeroGoals,AvgTeamsWith9Points,AvgTeamsWith0Points,AvgFirstPlacePoints,AvgSecondPlacePoints,AvgThirdPlacePoints,AvgFourthPlacePoints,Status,Warning");
        foreach (var d in set.Diagnostics.OrderBy(x => x.GroupCode))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(d.GroupCode), d.FixtureCount.ToString(CultureInfo.InvariantCulture), d.TeamCount.ToString(CultureInfo.InvariantCulture), Csv(d.Teams),
                D(d.AvgGoals), D(d.AvgDraws), D(d.AvgZeroZeroMatches), D(d.AvgOneNilMatches), D(d.AvgTwoOneMatches), D(d.AvgTwoTwoMatches), D(d.AvgThreeTwoMatches),
                D(d.AvgTeamsScoredZeroGoals), D(d.AvgTeamsConcededZeroGoals), D(d.AvgTeamsWith9Points), D(d.AvgTeamsWith0Points),
                D(d.AvgFirstPlacePoints), D(d.AvgSecondPlacePoints), D(d.AvgThirdPlacePoints), D(d.AvgFourthPlacePoints), Csv(d.Status), Csv(d.Warning)
            }));
        }
    }

    private static void EnsureCanWrite(string path, bool overwrite)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");
    }

    private static string Get(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headerByName, string name, string defaultValue = "")
    {
        return headerByName.TryGetValue(name, out var index) && index >= 0 && index < cells.Count
            ? cells[index]
            : defaultValue;
    }

    private static double GetDouble(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headerByName, string name)
    {
        var value = Get(cells, headerByName, name).Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"CSV column '{name}' has invalid numeric value '{value}'.");
    }

    private static int GetInt(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headerByName, string name)
    {
        var value = Get(cells, headerByName, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"CSV column '{name}' has invalid integer value '{value}'.");
    }

    private static string Csv(string? value) => SimpleCsv.Escape(value ?? string.Empty);

    private static string D(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private sealed class TeamTableRow
    {
        public int Points { get; set; }
        public int GoalsFor { get; set; }
        public int GoalsAgainst { get; set; }
        public int GoalDifference => GoalsFor - GoalsAgainst;
    }

    private sealed record RankedTeam(string Team, int Points, int GoalDifference, int GoalsFor, double RandomTieBreaker);

    private sealed record PreparedFixture(string TeamA, string TeamB, IReadOnlyList<PreparedScore> Scores);

    private sealed record PreparedScore(int ScoreA, int ScoreB, double CumulativeProbability);

    private sealed class FixtureScoreMatrixBuilderState
    {
        public string MatchDate { get; init; } = string.Empty;
        public string MatchTime { get; init; } = string.Empty;
        public string GroupCode { get; init; } = string.Empty;
        public string TeamA { get; init; } = string.Empty;
        public string TeamB { get; init; } = string.Empty;
        public double TeamAXg { get; init; }
        public double TeamBXg { get; init; }
        public double TotalLambda { get; init; }
        public double TotalLine { get; init; }
        public double GridMass { get; init; }
        public string Status { get; init; } = "valid";
        public string Warning { get; init; } = string.Empty;
        public List<FixtureScoreProbability> Scores { get; } = [];
    }
}
