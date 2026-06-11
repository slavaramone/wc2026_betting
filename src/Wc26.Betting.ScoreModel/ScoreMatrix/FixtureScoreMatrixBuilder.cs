using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Utilities;
using Wc26.Betting.ScoreModel.MarketXg;

namespace Wc26.Betting.ScoreModel.ScoreMatrix;

public sealed class FixtureScoreMatrixBuilder
{
    public async Task<FixtureScoreMatrixSet> BuildAndWriteAsync(
        string marketXgFile,
        string outputFolder,
        int maxGoals,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = await BuildFromMarketXgFileAsync(marketXgFile, maxGoals, cancellationToken);
        Directory.CreateDirectory(outputFolder);

        await WriteJsonAsync(Path.Combine(outputFolder, "wc26-fixture-score-matrix.json"), set, overwrite, cancellationToken);
        await WriteScoreMatrixCsvAsync(Path.Combine(outputFolder, "wc26-fixture-score-matrix.csv"), set, overwrite, cancellationToken);
        await WriteDiagnosticsCsvAsync(Path.Combine(outputFolder, "wc26-fixture-score-matrix-diagnostics.csv"), set, overwrite, cancellationToken);

        return set;
    }

    public async Task<FixtureScoreMatrixSet> BuildFromMarketXgFileAsync(string marketXgFile, int maxGoals, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(marketXgFile))
            throw new ArgumentException("Market xG file is required.", nameof(marketXgFile));
        if (!File.Exists(marketXgFile))
            throw new FileNotFoundException("Market xG file was not found.", marketXgFile);
        if (maxGoals < 4)
            throw new ArgumentOutOfRangeException(nameof(maxGoals), "Use at least 4 goals.");
        if (maxGoals > 20)
            throw new ArgumentOutOfRangeException(nameof(maxGoals), "Use at most 20 goals.");

        var xgSet = await ReadMarketXgAsync(marketXgFile, cancellationToken);
        var fixtures = new List<FixtureScoreMatrix>();
        var diagnostics = new List<FixtureScoreMatrixDiagnostic>();
        var warnings = new List<string>();

        foreach (var fixture in xgSet.Fixtures)
        {
            try
            {
                var matrix = BuildFixtureMatrix(fixture, maxGoals);
                fixtures.Add(matrix);
                diagnostics.Add(BuildDiagnostic(matrix));
            }
            catch (Exception ex)
            {
                var warning = $"{fixture.TeamA} - {fixture.TeamB}: {ex.Message}";
                warnings.Add(warning);
                var invalid = BuildInvalidFixtureMatrix(fixture, warning);
                fixtures.Add(invalid);
                diagnostics.Add(BuildDiagnostic(invalid));
            }
        }

        var validationErrors = Validate(fixtures);
        warnings.AddRange(validationErrors);

        return new FixtureScoreMatrixSet
        {
            SourceMarketXgFile = marketXgFile,
            FixtureCount = fixtures.Count,
            ValidFixtureCount = fixtures.Count(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            InvalidFixtureCount = fixtures.Count(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            MaxGoals = maxGoals,
            Fixtures = fixtures,
            Diagnostics = diagnostics,
            Warnings = warnings,
            ValidationErrors = validationErrors
        };
    }

    private static FixtureScoreMatrix BuildFixtureMatrix(MarketImpliedXgFixture fixture, int maxGoals)
    {
        if (!string.Equals(fixture.Status, "valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source xG fixture is invalid: {fixture.Warning}");
        if (string.IsNullOrWhiteSpace(fixture.GroupCode))
            throw new InvalidOperationException("Group is missing.");
        if (fixture.TeamAXg <= 0 || fixture.TeamBXg <= 0)
            throw new InvalidOperationException("Team xG must be positive.");

        var pA = BuildPoissonProbabilities(fixture.TeamAXg, maxGoals);
        var pB = BuildPoissonProbabilities(fixture.TeamBXg, maxGoals);
        var scores = new List<FixtureScoreProbability>((maxGoals + 1) * (maxGoals + 1));
        var rawMass = 0.0d;

        for (var a = 0; a <= maxGoals; a++)
        {
            for (var b = 0; b <= maxGoals; b++)
            {
                var raw = pA[a] * pB[b];
                rawMass += raw;
                scores.Add(new FixtureScoreProbability
                {
                    ScoreA = a,
                    ScoreB = b,
                    RawProbability = raw
                });
            }
        }

        if (rawMass <= 0)
            throw new InvalidOperationException("Score matrix probability mass is zero.");

        var normalizedScores = scores
            .Select(x => new FixtureScoreProbability
            {
                ScoreA = x.ScoreA,
                ScoreB = x.ScoreB,
                RawProbability = x.RawProbability,
                Probability = x.RawProbability / rawMass
            })
            .ToList();

        return new FixtureScoreMatrix
        {
            MatchKey = fixture.MatchKey,
            MatchDate = fixture.MatchDate,
            MatchTime = fixture.MatchTime,
            GroupCode = fixture.GroupCode,
            TeamA = fixture.TeamA,
            TeamB = fixture.TeamB,
            TeamAXg = fixture.TeamAXg,
            TeamBXg = fixture.TeamBXg,
            TotalLambda = fixture.TotalLambda,
            TotalLine = fixture.TotalLine,
            GridMass = rawMass,
            Status = "valid",
            Scores = normalizedScores
        };
    }

    private static FixtureScoreMatrix BuildInvalidFixtureMatrix(MarketImpliedXgFixture fixture, string warning)
    {
        return new FixtureScoreMatrix
        {
            MatchKey = fixture.MatchKey,
            MatchDate = fixture.MatchDate,
            MatchTime = fixture.MatchTime,
            GroupCode = fixture.GroupCode,
            TeamA = fixture.TeamA,
            TeamB = fixture.TeamB,
            TeamAXg = fixture.TeamAXg,
            TeamBXg = fixture.TeamBXg,
            TotalLambda = fixture.TotalLambda,
            TotalLine = fixture.TotalLine,
            Status = "invalid",
            Warning = warning
        };
    }

    private static FixtureScoreMatrixDiagnostic BuildDiagnostic(FixtureScoreMatrix matrix)
    {
        var sum = matrix.Scores.Sum(x => x.Probability);
        var p1 = matrix.Scores.Where(x => x.ScoreA > x.ScoreB).Sum(x => x.Probability);
        var px = matrix.Scores.Where(x => x.ScoreA == x.ScoreB).Sum(x => x.Probability);
        var p2 = matrix.Scores.Where(x => x.ScoreA < x.ScoreB).Sum(x => x.Probability);
        var pOver = matrix.Scores.Where(x => x.ScoreA + x.ScoreB > matrix.TotalLine).Sum(x => x.Probability);
        var p00 = matrix.Scores.Where(x => x.ScoreA == 0 && x.ScoreB == 0).Sum(x => x.Probability);
        var p10Or01 = matrix.Scores.Where(x => (x.ScoreA == 1 && x.ScoreB == 0) || (x.ScoreA == 0 && x.ScoreB == 1)).Sum(x => x.Probability);
        var p21Or12 = matrix.Scores.Where(x => (x.ScoreA == 2 && x.ScoreB == 1) || (x.ScoreA == 1 && x.ScoreB == 2)).Sum(x => x.Probability);
        var p22 = matrix.Scores.Where(x => x.ScoreA == 2 && x.ScoreB == 2).Sum(x => x.Probability);
        var p32Or23 = matrix.Scores.Where(x => (x.ScoreA == 3 && x.ScoreB == 2) || (x.ScoreA == 2 && x.ScoreB == 3)).Sum(x => x.Probability);

        return new FixtureScoreMatrixDiagnostic
        {
            GroupCode = matrix.GroupCode,
            TeamA = matrix.TeamA,
            TeamB = matrix.TeamB,
            TeamAXg = matrix.TeamAXg,
            TeamBXg = matrix.TeamBXg,
            TotalLambda = matrix.TotalLambda,
            TotalLine = matrix.TotalLine,
            GridMass = matrix.GridMass,
            ProbabilitySum = sum,
            P1 = p1,
            PX = px,
            P2 = p2,
            POver = pOver,
            PUnder = 1.0d - pOver,
            P00 = p00,
            P10Or01 = p10Or01,
            P21Or12 = p21Or12,
            P22 = p22,
            P32Or23 = p32Or23,
            Status = matrix.Status,
            Warning = matrix.Warning
        };
    }

    private static List<string> Validate(IReadOnlyList<FixtureScoreMatrix> fixtures)
    {
        var errors = new List<string>();

        foreach (var fixture in fixtures.Where(x => string.IsNullOrWhiteSpace(x.GroupCode)))
            errors.Add($"Missing group: {fixture.TeamA} - {fixture.TeamB}");

        foreach (var fixture in fixtures.Where(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)))
        {
            var sum = fixture.Scores.Sum(x => x.Probability);
            if (Math.Abs(sum - 1.0d) > 0.000001d)
                errors.Add($"Probability sum is not 1 for {fixture.TeamA} - {fixture.TeamB}: {sum:0.########}");
            if (fixture.GridMass < 0.999d)
                errors.Add($"Score grid mass is too low for {fixture.TeamA} - {fixture.TeamB}: {fixture.GridMass:0.########}. Increase --max-goals.");
        }

        var groupDiagnostics = fixtures
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GroupCode) ? "<missing>" : x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { Group = x.Key, Count = x.Count() })
            .ToList();

        foreach (var group in groupDiagnostics.Where(x => x.Group != "<missing>" && x.Count != 6))
            errors.Add($"Group {group.Group}: expected 6 fixtures, got {group.Count}.");

        return errors;
    }

    private static async Task<MarketImpliedXgSet> ReadMarketXgAsync(string marketXgFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(marketXgFile);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(marketXgFile);
            var set = await JsonSerializer.DeserializeAsync<MarketImpliedXgSet>(stream, cancellationToken: cancellationToken);
            return set ?? throw new InvalidOperationException($"Could not deserialize market xG JSON: {marketXgFile}");
        }

        return await ReadMarketXgCsvAsync(marketXgFile, cancellationToken);
    }

    private static async Task<MarketImpliedXgSet> ReadMarketXgCsvAsync(string marketXgFile, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(marketXgFile, cancellationToken);
        if (lines.Length == 0)
            throw new InvalidOperationException($"Market xG CSV is empty: {marketXgFile}");

        var headers = SimpleCsv.ParseLine(lines[0]);
        var headerByName = headers
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var fixtures = new List<MarketImpliedXgFixture>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var cells = SimpleCsv.ParseLine(lines[i]);
            fixtures.Add(new MarketImpliedXgFixture
            {
                MatchDate = Get(cells, headerByName, "MatchDate"),
                MatchTime = Get(cells, headerByName, "MatchTime"),
                GroupCode = Get(cells, headerByName, "Group"),
                GroupSource = Get(cells, headerByName, "GroupSource"),
                TeamA = Get(cells, headerByName, "TeamA"),
                TeamB = Get(cells, headerByName, "TeamB"),
                NormalizedTeamA = Get(cells, headerByName, "TeamA"),
                NormalizedTeamB = Get(cells, headerByName, "TeamB"),
                TeamAXg = GetDouble(cells, headerByName, "TeamAXg"),
                TeamBXg = GetDouble(cells, headerByName, "TeamBXg"),
                TotalLambda = GetDouble(cells, headerByName, "TotalLambda"),
                TotalLine = GetDouble(cells, headerByName, "TotalLine"),
                Status = Get(cells, headerByName, "Status", "valid"),
                Warning = Get(cells, headerByName, "Warning")
            });
        }

        return new MarketImpliedXgSet
        {
            SourceOddsFile = marketXgFile,
            RowCount = fixtures.Count,
            ValidFixtureCount = fixtures.Count(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            InvalidFixtureCount = fixtures.Count(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            Fixtures = fixtures
        };
    }

    private static double[] BuildPoissonProbabilities(double lambda, int maxGoals)
    {
        var result = new double[maxGoals + 1];
        result[0] = Math.Exp(-lambda);
        for (var i = 1; i <= maxGoals; i++)
            result[i] = result[i - 1] * lambda / i;
        return result;
    }

    private static async Task WriteJsonAsync(string path, FixtureScoreMatrixSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task WriteScoreMatrixCsvAsync(string path, FixtureScoreMatrixSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MatchDate,MatchTime,TeamA,TeamB,TeamAXg,TeamBXg,TotalLambda,TotalLine,GridMass,ScoreA,ScoreB,RawProbability,Probability,Status,Warning");

        foreach (var fixture in set.Fixtures)
        {
            foreach (var score in fixture.Scores)
            {
                await writer.WriteLineAsync(string.Join(',', new[]
                {
                    Csv(fixture.GroupCode), Csv(fixture.MatchDate), Csv(fixture.MatchTime), Csv(fixture.TeamA), Csv(fixture.TeamB),
                    D(fixture.TeamAXg), D(fixture.TeamBXg), D(fixture.TotalLambda), D(fixture.TotalLine), D(fixture.GridMass),
                    score.ScoreA.ToString(CultureInfo.InvariantCulture), score.ScoreB.ToString(CultureInfo.InvariantCulture),
                    D(score.RawProbability), D(score.Probability), Csv(fixture.Status), Csv(fixture.Warning)
                }));
            }
        }
    }

    private static async Task WriteDiagnosticsCsvAsync(string path, FixtureScoreMatrixSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,TeamA,TeamB,TeamAXg,TeamBXg,TotalLambda,TotalLine,GridMass,ProbabilitySum,P1,PX,P2,POver,PUnder,P00,P10Or01,P21Or12,P22,P32Or23,Status,Warning");

        foreach (var d in set.Diagnostics.OrderBy(x => x.GroupCode).ThenBy(x => x.TeamA).ThenBy(x => x.TeamB))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(d.GroupCode), Csv(d.TeamA), Csv(d.TeamB),
                D(d.TeamAXg), D(d.TeamBXg), D(d.TotalLambda), D(d.TotalLine), D(d.GridMass), D(d.ProbabilitySum),
                D(d.P1), D(d.PX), D(d.P2), D(d.POver), D(d.PUnder),
                D(d.P00), D(d.P10Or01), D(d.P21Or12), D(d.P22), D(d.P32Or23), Csv(d.Status), Csv(d.Warning)
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
        var value = Get(cells, headerByName, name);
        value = value.Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"CSV column '{name}' has invalid numeric value '{value}'.");
    }

    private static string Csv(string? value) => SimpleCsv.Escape(value ?? string.Empty);

    private static string D(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
