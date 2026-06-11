using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Utilities;
using Wc26.Betting.ScoreModel.GroupPropComparison;
using Wc26.Betting.ScoreModel.GroupSimulation;
using Wc26.Betting.ScoreModel.ScoreMatrix;

namespace Wc26.Betting.ScoreModel.Sensitivity;

public sealed class GroupPropSensitivityRunner
{
    public async Task<GroupPropSensitivitySet> RunAndWriteAsync(
        string marketXgFile,
        string propOddsFile,
        string outputFolder,
        int maxGoals,
        int iterations,
        int seed,
        double edgeThreshold,
        double minOdds,
        bool statPropsOnly,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = await RunAsync(marketXgFile, propOddsFile, outputFolder, maxGoals, iterations, seed, edgeThreshold, minOdds, statPropsOnly, overwrite, cancellationToken);
        Directory.CreateDirectory(outputFolder);
        await WriteJsonAsync(Path.Combine(outputFolder, "wc26-group-special-props-sensitivity.json"), set, overwrite, cancellationToken);
        await WriteCsvAsync(Path.Combine(outputFolder, "wc26-group-special-props-sensitivity.csv"), set, overwrite, cancellationToken);
        await WriteStableEdgesCsvAsync(Path.Combine(outputFolder, "wc26-group-special-props-stable-edges.csv"), set, overwrite, cancellationToken);
        return set;
    }

    public async Task<GroupPropSensitivitySet> RunAsync(
        string marketXgFile,
        string propOddsFile,
        string outputFolder,
        int maxGoals,
        int iterations,
        int seed,
        double edgeThreshold,
        double minOdds,
        bool statPropsOnly,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(marketXgFile))
            throw new ArgumentException("--market-xg-file is required.", nameof(marketXgFile));
        if (string.IsNullOrWhiteSpace(propOddsFile))
            throw new ArgumentException("--prop-odds-file is required.", nameof(propOddsFile));
        if (!File.Exists(marketXgFile))
            throw new FileNotFoundException("Market xG file was not found.", marketXgFile);
        if (!File.Exists(propOddsFile))
            throw new FileNotFoundException("Props odds file was not found.", propOddsFile);

        var scenarios = new[]
        {
            new SensitivityScenario("Base", new ScoreMatrixCalibrationOptions { Mode = ScoreMatrixCalibrationMode.None }),
            new SensitivityScenario("DrawMatch", new ScoreMatrixCalibrationOptions { Mode = ScoreMatrixCalibrationMode.DrawMatch }),
            new SensitivityScenario("LowScoreBoost", new ScoreMatrixCalibrationOptions { Mode = ScoreMatrixCalibrationMode.LowScoreBoost }),
            new SensitivityScenario("DrawAndLowScore", new ScoreMatrixCalibrationOptions { Mode = ScoreMatrixCalibrationMode.DrawAndLowScore })
        };

        Directory.CreateDirectory(outputFolder);
        var matrixBuilder = new FixtureScoreMatrixBuilder();
        var simulator = new GroupPropSimulationRunner();
        var comparer = new GroupPropMarketComparer();

        var comparisonByScenario = new Dictionary<string, GroupPropMarketComparisonSet>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var scenario in scenarios)
        {
            var scenarioFolder = Path.Combine(outputFolder, "sensitivity", scenario.Name);
            Directory.CreateDirectory(scenarioFolder);
            var matrix = await matrixBuilder.BuildAndWriteAsync(marketXgFile, scenarioFolder, maxGoals, scenario.Options, overwrite, cancellationToken);
            if (matrix.ValidationErrors.Count > 0)
                warnings.AddRange(matrix.ValidationErrors.Select(x => $"{scenario.Name}: {x}"));

            var simulation = await simulator.RunAndWriteAsync(
                Path.Combine(scenarioFolder, "wc26-fixture-score-matrix.json"),
                scenarioFolder,
                iterations,
                seed,
                overwrite,
                cancellationToken);
            if (simulation.ValidationErrors.Count > 0)
                warnings.AddRange(simulation.ValidationErrors.Select(x => $"{scenario.Name}: {x}"));

            var comparison = await comparer.CompareAndWriteAsync(
                Path.Combine(scenarioFolder, "wc26-group-prop-simulation.json"),
                propOddsFile,
                scenarioFolder,
                edgeThreshold,
                minOdds,
                statPropsOnly,
                overwrite,
                cancellationToken);
            comparisonByScenario[scenario.Name] = comparison;
        }

        var rows = BuildRows(comparisonByScenario, edgeThreshold, minOdds);
        var summaries = scenarios
            .Select(s => comparisonByScenario[s.Name])
            .Select((comparison, index) => new GroupPropSensitivityScenarioSummary
            {
                Scenario = scenarios[index].Name,
                ComparedRows = comparison.ComparedRows,
                BetRows = comparison.Rows.Count(x => string.Equals(x.Decision, "BET", StringComparison.OrdinalIgnoreCase)),
                MissingModelRows = comparison.MissingModelRows,
                MaxRoiAtBookOdds = comparison.Rows.Count == 0 ? 0.0d : comparison.Rows.Max(x => x.RoiAtBookOdds)
            })
            .ToList();

        return new GroupPropSensitivitySet
        {
            SourceMarketXgFile = marketXgFile,
            SourcePropOddsFile = propOddsFile,
            Iterations = iterations,
            Seed = seed,
            ScenarioCount = scenarios.Length,
            Scenarios = summaries,
            Rows = rows,
            Warnings = warnings
        };
    }

    private static List<GroupPropSensitivityRow> BuildRows(
        IReadOnlyDictionary<string, GroupPropMarketComparisonSet> comparisonByScenario,
        double edgeThreshold,
        double minOdds)
    {
        var baseRows = comparisonByScenario["Base"].Rows
            .Where(x => string.IsNullOrWhiteSpace(x.Warning))
            .ToDictionary(SensitivityKey.Create, x => x);
        var drawRows = comparisonByScenario["DrawMatch"].Rows
            .Where(x => string.IsNullOrWhiteSpace(x.Warning))
            .ToDictionary(SensitivityKey.Create, x => x);
        var lowRows = comparisonByScenario["LowScoreBoost"].Rows
            .Where(x => string.IsNullOrWhiteSpace(x.Warning))
            .ToDictionary(SensitivityKey.Create, x => x);
        var bothRows = comparisonByScenario["DrawAndLowScore"].Rows
            .Where(x => string.IsNullOrWhiteSpace(x.Warning))
            .ToDictionary(SensitivityKey.Create, x => x);

        var result = new List<GroupPropSensitivityRow>();
        foreach (var (key, b) in baseRows)
        {
            if (!drawRows.TryGetValue(key, out var d) || !lowRows.TryGetValue(key, out var l) || !bothRows.TryGetValue(key, out var dl))
                continue;

            var coreProbabilities = new[] { b.ModelProbability, d.ModelProbability, l.ModelProbability };
            var coreRois = new[] { b.RoiAtBookOdds, d.RoiAtBookOdds, l.RoiAtBookOdds };
            var coreEdges = new[] { b.Edge, d.Edge, l.Edge };
            var minRoi = coreRois.Min();
            var maxRoi = coreRois.Max();
            var minEdge = coreEdges.Min();
            var stable = b.BookOdds >= minOdds && minRoi > 0.0d && minEdge >= edgeThreshold;

            result.Add(new GroupPropSensitivityRow
            {
                GroupCode = b.GroupCode,
                MarketOrder = b.MarketOrder,
                BookMarketType = b.BookMarketType,
                MarketNameRu = b.MarketNameRu,
                Subject = b.Subject,
                Line = b.Line,
                Side = b.Side,
                BookOdds = b.BookOdds,
                BookNoVigProbability = b.BookNoVigProbability,
                BaseProbability = b.ModelProbability,
                BaseRoi = b.RoiAtBookOdds,
                DrawMatchProbability = d.ModelProbability,
                DrawMatchRoi = d.RoiAtBookOdds,
                LowScoreBoostProbability = l.ModelProbability,
                LowScoreBoostRoi = l.RoiAtBookOdds,
                DrawAndLowScoreProbability = dl.ModelProbability,
                DrawAndLowScoreRoi = dl.RoiAtBookOdds,
                MinProbabilityCore = coreProbabilities.Min(),
                MinRoiCore = minRoi,
                MaxRoiCore = maxRoi,
                ProbabilityRangeCore = coreProbabilities.Max() - coreProbabilities.Min(),
                MinEdgeCore = minEdge,
                StabilityDecision = stable ? "STABLE_BET" : "NO_BET"
            });
        }

        return result
            .OrderByDescending(x => string.Equals(x.StabilityDecision, "STABLE_BET", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.MinRoiCore)
            .ThenBy(x => x.GroupCode)
            .ThenBy(x => x.MarketOrder)
            .ToList();
    }

    private static async Task WriteJsonAsync(string path, GroupPropSensitivitySet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task WriteCsvAsync(string path, GroupPropSensitivitySet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MarketOrder,BookMarketType,MarketNameRu,Subject,Line,Side,BookOdds,BookNoVigProbability,BaseProbability,BaseRoi,DrawMatchProbability,DrawMatchRoi,LowScoreBoostProbability,LowScoreBoostRoi,DrawAndLowScoreProbability,DrawAndLowScoreRoi,MinProbabilityCore,MinRoiCore,MaxRoiCore,ProbabilityRangeCore,MinEdgeCore,StabilityDecision,Warning");
        foreach (var row in set.Rows)
            await writer.WriteLineAsync(ToCsv(row));
    }

    private static async Task WriteStableEdgesCsvAsync(string path, GroupPropSensitivitySet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MarketOrder,BookMarketType,MarketNameRu,Subject,Line,Side,BookOdds,BookNoVigProbability,BaseProbability,BaseRoi,DrawMatchProbability,DrawMatchRoi,LowScoreBoostProbability,LowScoreBoostRoi,DrawAndLowScoreProbability,DrawAndLowScoreRoi,MinProbabilityCore,MinRoiCore,MaxRoiCore,ProbabilityRangeCore,MinEdgeCore,StabilityDecision,Warning");
        foreach (var row in set.Rows.Where(x => string.Equals(x.StabilityDecision, "STABLE_BET", StringComparison.OrdinalIgnoreCase)))
            await writer.WriteLineAsync(ToCsv(row));
    }

    private static string ToCsv(GroupPropSensitivityRow row)
    {
        return string.Join(',', new[]
        {
            Csv(row.GroupCode), row.MarketOrder.ToString(CultureInfo.InvariantCulture), Csv(row.BookMarketType), Csv(row.MarketNameRu), Csv(row.Subject),
            D(row.Line), Csv(row.Side), D(row.BookOdds), D(row.BookNoVigProbability),
            D(row.BaseProbability), D(row.BaseRoi), D(row.DrawMatchProbability), D(row.DrawMatchRoi),
            D(row.LowScoreBoostProbability), D(row.LowScoreBoostRoi), D(row.DrawAndLowScoreProbability), D(row.DrawAndLowScoreRoi),
            D(row.MinProbabilityCore), D(row.MinRoiCore), D(row.MaxRoiCore), D(row.ProbabilityRangeCore), D(row.MinEdgeCore),
            Csv(row.StabilityDecision), Csv(row.Warning)
        });
    }

    private static void EnsureCanWrite(string path, bool overwrite)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");
    }

    private static string Csv(string? value) => SimpleCsv.Escape(value ?? string.Empty);
    private static string D(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
