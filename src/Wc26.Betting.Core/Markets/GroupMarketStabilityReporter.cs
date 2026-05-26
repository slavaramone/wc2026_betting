using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Simulation;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class GroupMarketStabilityReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static readonly IReadOnlyList<Wc2026SimulationWeights> DefaultBlends =
    [
        new(1.00, 0.00, 0.00),
        new(0.90, 0.08, 0.02),
        new(0.85, 0.12, 0.03),
        new(0.80, 0.15, 0.05)
    ];

    public async Task<GroupMarketStabilityReport> BuildAsync(
        string modelsFolder,
        string? groupStageResultsOddsFile,
        string? finishHigherOddsFile,
        string outputFolder,
        int iterations,
        int seed,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");
        if (iterations <= 0)
            throw new ArgumentException("--iterations must be greater than zero.");

        Directory.CreateDirectory(outputFolder);

        var runner = new Wc2026SimulationRunner();
        var comparer = new GroupMarketOddsComparer();
        var blendResults = new List<GroupMarketStabilityBlendResult>();
        var allRows = new List<GroupMarketStabilityBlendRow>();

        foreach (var blend in DefaultBlends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedBlend = blend.Normalized();
            var blendLabel = BlendLabel(normalizedBlend);
            var blendFolder = Path.Combine(outputFolder, "blends", blendLabel);
            var simulationFolder = Path.Combine(blendFolder, "simulation");
            var comparisonFolder = Path.Combine(blendFolder, "comparison");

            var simulation = await runner.RunFromModelsFolderAsync(
                modelsFolder,
                iterations,
                seed,
                simulationFolder,
                overwrite,
                normalizedBlend,
                cancellationToken);

            var comparison = await comparer.CompareFromSimulationAsync(
                modelsFolder,
                simulation,
                groupStageResultsOddsFile,
                finishHigherOddsFile,
                comparisonFolder,
                minEdge,
                overwrite,
                cancellationToken);

            blendResults.Add(new GroupMarketStabilityBlendResult
            {
                Label = blendLabel,
                MarketWeight = normalizedBlend.Market,
                EloWeight = normalizedBlend.Elo,
                EaWeight = normalizedBlend.Ea,
                BetRows = comparison.Summary.BetRows,
                StrictBetRows = comparison.Summary.StrictBetRows,
                TopStrictEdges = comparison.Summary.StrictTopEdges.Take(10).ToList()
            });

            foreach (var row in comparison.Rows.Where(r => !string.Equals(r.Decision, "INVALID", StringComparison.OrdinalIgnoreCase)))
            {
                allRows.Add(new GroupMarketStabilityBlendRow
                {
                    BlendLabel = blendLabel,
                    MarketWeight = normalizedBlend.Market,
                    EloWeight = normalizedBlend.Elo,
                    EaWeight = normalizedBlend.Ea,
                    Row = row,
                    IsStrictBet = IsStrictBet(row)
                });
            }
        }

        var candidates = BuildCandidates(allRows, DefaultBlends.Count)
            .OrderByDescending(x => x.StableStrictBet)
            .ThenByDescending(x => x.StrictBlendCount)
            .ThenByDescending(x => x.MinEdgeProbability)
            .ThenByDescending(x => x.AvgEdgeProbability)
            .ToList();

        var report = new GroupMarketStabilityReport
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            OutputFolder = outputFolder,
            Iterations = iterations,
            Seed = seed,
            MinEdge = minEdge,
            StrictRules = "edge >= 0.05; book odds >= 1.15; paired no-vig probability required; exclude unpaired exact-rank longshots",
            Blends = blendResults,
            CandidateCount = candidates.Count,
            StableStrictBetCount = candidates.Count(x => x.StableStrictBet),
            Candidates = candidates,
            StableStrictBets = candidates.Where(x => x.StableStrictBet).ToList()
        };

        await WriteReportAsync(report, allRows, outputFolder, overwrite, cancellationToken);
        return report;
    }

    private static List<GroupMarketStabilityCandidate> BuildCandidates(IReadOnlyList<GroupMarketStabilityBlendRow> allRows, int blendCount)
    {
        var strictKeys = allRows.Where(x => x.IsStrictBet).Select(x => CandidateKey(x.Row)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateRows = allRows.Where(x => strictKeys.Contains(CandidateKey(x.Row))).ToList();

        return candidateRows
            .GroupBy(x => CandidateKey(x.Row), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToList();
                var first = rows.First().Row;
                var strictRows = rows.Where(x => x.IsStrictBet).ToList();
                var betRows = rows.Where(x => string.Equals(x.Row.Decision, "BET", StringComparison.OrdinalIgnoreCase)).ToList();
                var edgeValues = rows.Select(x => x.Row.EdgeProbability ?? 0).ToList();
                var simValues = rows.Select(x => x.Row.SimulationProbability ?? 0).ToList();

                return new GroupMarketStabilityCandidate
                {
                    ReportGroupCode = first.ReportGroupCode,
                    Market = first.Market,
                    Selection = first.Selection,
                    Side = first.Side,
                    Opponent = first.Opponent,
                    BookOdds = first.BookOdds,
                    PairedBookOdds = first.PairedBookOdds,
                    BookProbabilityUsed = first.BookProbabilityUsed,
                    StrictBlendCount = strictRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    BetBlendCount = betRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    BlendCount = blendCount,
                    StableStrictBet = strictRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count() == blendCount,
                    AvgEdgeProbability = Round(edgeValues.Average()),
                    MinEdgeProbability = Round(edgeValues.Min()),
                    MaxEdgeProbability = Round(edgeValues.Max()),
                    AvgSimulationProbability = Round(simValues.Average()),
                    MinSimulationProbability = Round(simValues.Min()),
                    MaxSimulationProbability = Round(simValues.Max()),
                    StrictBlendLabels = string.Join('|', strictRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)),
                    DecisionsByBlend = string.Join("; ", rows.OrderBy(x => x.BlendLabel).Select(x => $"{x.BlendLabel}:{x.Row.Decision}:edge={(x.Row.EdgeProbability ?? 0):0.000}")),
                    Notes = first.Notes
                };
            })
            .ToList();
    }

    private static bool IsStrictBet(GroupMarketComparisonRow row)
    {
        if (!string.Equals(row.Decision, "BET", StringComparison.OrdinalIgnoreCase)) return false;
        if (row.EdgeProbability is null or < 0.05) return false;
        if (row.BookOdds is null or < 1.15) return false;
        if (row.PairedBookOdds is null or <= 1.0) return false;
        if (row.BookProbabilityNoVig is null) return false;
        if (row.Market.StartsWith("ExactRank", StringComparison.OrdinalIgnoreCase) && row.PairedBookOdds is null) return false;
        return true;
    }

    private static string CandidateKey(GroupMarketComparisonRow row)
        => string.Join('|', row.ReportGroupCode, row.Market, row.Selection, row.Side, row.Opponent);

    private static string BlendLabel(Wc2026SimulationWeights weights)
        => $"market{weights.Market * 100:0}_elo{weights.Elo * 100:0}_ea{weights.Ea * 100:0}";

    private static async Task WriteReportAsync(GroupMarketStabilityReport report, IReadOnlyList<GroupMarketStabilityBlendRow> allRows, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(Path.Combine(outputFolder, "model-stability-summary.json"), report, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, "model-stability-candidates.csv"), report.Candidates, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, "model-stability-stable-strict-bets.csv"), report.StableStrictBets, overwrite, cancellationToken);
        await WriteAllRowsCsvAsync(Path.Combine(outputFolder, "model-stability-all-blend-results.csv"), allRows, overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    private static async Task WriteCandidatesCsvAsync(string path, IEnumerable<GroupMarketStabilityCandidate> candidates, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("report_group_code,market,selection,side,opponent,book_odds,paired_book_odds,book_probability_used,strict_blend_count,bet_blend_count,blend_count,stable_strict_bet,avg_edge_probability,min_edge_probability,max_edge_probability,avg_simulation_probability,min_simulation_probability,max_simulation_probability,strict_blend_labels,decisions_by_blend,notes");
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                c.ReportGroupCode, c.Market, c.Selection, c.Side, c.Opponent,
                Format(c.BookOdds), Format(c.PairedBookOdds), Format(c.BookProbabilityUsed),
                c.StrictBlendCount.ToString(CultureInfo.InvariantCulture), c.BetBlendCount.ToString(CultureInfo.InvariantCulture), c.BlendCount.ToString(CultureInfo.InvariantCulture), c.StableStrictBet.ToString(),
                Format(c.AvgEdgeProbability), Format(c.MinEdgeProbability), Format(c.MaxEdgeProbability),
                Format(c.AvgSimulationProbability), Format(c.MinSimulationProbability), Format(c.MaxSimulationProbability),
                c.StrictBlendLabels, c.DecisionsByBlend, c.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteAllRowsCsvAsync(string path, IEnumerable<GroupMarketStabilityBlendRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("blend_label,market_weight,elo_weight,ea_weight,is_strict_bet,report_group_code,market,selection,side,opponent,book_odds,paired_book_odds,book_probability_used,simulation_probability,edge_probability,decision,notes");
        foreach (var item in rows.OrderBy(x => x.BlendLabel).ThenByDescending(x => x.IsStrictBet).ThenByDescending(x => x.Row.EdgeProbability))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = item.Row;
            var values = new[]
            {
                item.BlendLabel, Format(item.MarketWeight), Format(item.EloWeight), Format(item.EaWeight), item.IsStrictBet.ToString(),
                r.ReportGroupCode, r.Market, r.Selection, r.Side, r.Opponent,
                Format(r.BookOdds), Format(r.PairedBookOdds), Format(r.BookProbabilityUsed), Format(r.SimulationProbability), Format(r.EdgeProbability), r.Decision, r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class GroupMarketStabilityReport
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public double MinEdge { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public List<GroupMarketStabilityBlendResult> Blends { get; init; } = [];
    public int CandidateCount { get; init; }
    public int StableStrictBetCount { get; init; }
    public List<GroupMarketStabilityCandidate> Candidates { get; init; } = [];
    public List<GroupMarketStabilityCandidate> StableStrictBets { get; init; } = [];
}

public sealed class GroupMarketStabilityBlendResult
{
    public string Label { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public int BetRows { get; init; }
    public int StrictBetRows { get; init; }
    public List<GroupMarketTopEdge> TopStrictEdges { get; init; } = [];
}

public sealed class GroupMarketStabilityCandidate
{
    public string ReportGroupCode { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string Opponent { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? PairedBookOdds { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public int StrictBlendCount { get; init; }
    public int BetBlendCount { get; init; }
    public int BlendCount { get; init; }
    public bool StableStrictBet { get; init; }
    public double AvgEdgeProbability { get; init; }
    public double MinEdgeProbability { get; init; }
    public double MaxEdgeProbability { get; init; }
    public double AvgSimulationProbability { get; init; }
    public double MinSimulationProbability { get; init; }
    public double MaxSimulationProbability { get; init; }
    public string StrictBlendLabels { get; init; } = string.Empty;
    public string DecisionsByBlend { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

internal sealed class GroupMarketStabilityBlendRow
{
    public string BlendLabel { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public GroupMarketComparisonRow Row { get; init; } = new();
    public bool IsStrictBet { get; init; }
}
