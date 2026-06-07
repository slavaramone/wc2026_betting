using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Simulation;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class BestConfederationTeamMarketStabilityReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static readonly IReadOnlyList<Wc2026SimulationWeights> DefaultBlends =
    [
        new(1.00, 0.00, 0.00),
        new(0.90, 0.08, 0.02),
        new(0.85, 0.12, 0.03),
        new(0.80, 0.15, 0.05)
    ];

    public async Task<BestConfederationTeamMarketStabilityReport> BuildAsync(
        string modelsFolder,
        string oddsFile,
        string outputFolder,
        int iterations,
        int seed,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");
        if (string.IsNullOrWhiteSpace(oddsFile))
            throw new ArgumentException("--best-confederation-odds-file is required.");
        if (!File.Exists(oddsFile))
            throw new FileNotFoundException($"Best-confederation-team odds CSV not found: {oddsFile}", oddsFile);
        if (iterations <= 0)
            throw new ArgumentException("--iterations must be greater than zero.");

        Directory.CreateDirectory(outputFolder);

        var runner = new Wc2026SimulationRunner();
        var comparer = new BestConfederationTeamMarketOddsComparer();
        var blendResults = new List<BestConfederationTeamMarketStabilityBlendResult>();
        var allRows = new List<BestConfederationTeamMarketStabilityBlendRow>();

        foreach (var blend in DefaultBlends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = blend.Normalized();
            var label = BlendLabel(normalized);
            var blendFolder = Path.Combine(outputFolder, "blends", label);

            var simulation = await runner.RunFromModelsFolderAsync(modelsFolder, iterations, seed, blendFolder, overwrite, normalized, cancellationToken);
            var comparison = await comparer.CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, blendFolder, minEdge, overwrite, cancellationToken);

            blendResults.Add(new BestConfederationTeamMarketStabilityBlendResult
            {
                Label = label,
                MarketWeight = normalized.Market,
                EloWeight = normalized.Elo,
                EaWeight = normalized.Ea,
                BetRows = comparison.Summary.BetRows,
                StrictBetRows = comparison.Summary.StrictBetRows,
                OutputFolder = blendFolder
            });

            foreach (var row in comparison.Rows)
            {
                allRows.Add(new BestConfederationTeamMarketStabilityBlendRow
                {
                    BlendLabel = label,
                    MarketWeight = normalized.Market,
                    EloWeight = normalized.Elo,
                    EaWeight = normalized.Ea,
                    IsStrictBet = BestConfederationTeamMarketOddsComparer.IsStrictBet(row),
                    Row = row
                });
            }
        }

        var candidates = BuildCandidates(allRows, DefaultBlends.Count);
        var report = new BestConfederationTeamMarketStabilityReport
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            OutputFolder = outputFolder,
            Iterations = iterations,
            Seed = seed,
            MinEdge = minEdge,
            StrictRules = "edge >= 0.05; book odds >= 1.50; no-vig probability required within confederation market",
            StrongStableDefinition = "strict bet in all blends",
            SoftStableDefinition = "strict bet in all but one blend",
            Blends = blendResults,
            CandidateCount = candidates.Count,
            StrongStableStrictBetCount = candidates.Count(x => x.StrongStableStrictBet),
            SoftStableStrictBetCount = candidates.Count(x => x.SoftStableStrictBet),
            Candidates = candidates,
            StrongStableStrictBets = candidates.Where(x => x.StrongStableStrictBet).ToList(),
            SoftStableStrictBets = candidates.Where(x => x.SoftStableStrictBet).ToList()
        };

        await WriteReportAsync(report, allRows, outputFolder, overwrite, cancellationToken);
        return report;
    }

    private static List<BestConfederationTeamMarketStabilityCandidate> BuildCandidates(IReadOnlyList<BestConfederationTeamMarketStabilityBlendRow> allRows, int blendCount)
    {
        var strictKeys = allRows.Where(x => x.IsStrictBet).Select(x => CandidateKey(x.Row)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allRows
            .Where(x => strictKeys.Contains(CandidateKey(x.Row)))
            .GroupBy(x => CandidateKey(x.Row), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToList();
                var first = rows.First().Row;
                var strictRows = rows.Where(x => x.IsStrictBet).ToList();
                var betRows = rows.Where(x => string.Equals(x.Row.Decision, "BET", StringComparison.OrdinalIgnoreCase)).ToList();
                var edgeValues = rows.Select(x => x.Row.EdgeProbability ?? 0).ToList();
                var simValues = rows.Select(x => x.Row.SimulationProbability ?? 0).ToList();
                var strictBlendCount = strictRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                return new BestConfederationTeamMarketStabilityCandidate
                {
                    Confederation = first.Confederation,
                    Selection = first.Selection,
                    GroupCode = first.GroupCode,
                    BookOdds = first.BookOdds,
                    BookProbabilityUsed = first.BookProbabilityUsed,
                    StrictBlendCount = strictBlendCount,
                    BetBlendCount = betRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    BlendCount = blendCount,
                    StrongStableStrictBet = strictBlendCount == blendCount,
                    SoftStableStrictBet = strictBlendCount >= Math.Max(1, blendCount - 1),
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
            .OrderByDescending(x => x.StrongStableStrictBet)
            .ThenByDescending(x => x.SoftStableStrictBet)
            .ThenByDescending(x => x.MinEdgeProbability)
            .ToList();
    }

    private static string CandidateKey(BestConfederationTeamMarketComparisonRow row)
        => string.Join('|', row.Confederation, row.Selection);

    private static string BlendLabel(Wc2026SimulationWeights weights)
        => $"market{weights.Market * 100:0}_elo{weights.Elo * 100:0}_ea{weights.Ea * 100:0}";

    private static async Task WriteReportAsync(BestConfederationTeamMarketStabilityReport report, IReadOnlyList<BestConfederationTeamMarketStabilityBlendRow> allRows, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(Path.Combine(outputFolder, "best-confederation-team-stability-summary.json"), report, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, "best-confederation-team-stability-candidates.csv"), report.Candidates, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, "best-confederation-team-stability-strong-stable-strict-bets.csv"), report.StrongStableStrictBets, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, "best-confederation-team-stability-soft-stable-strict-bets.csv"), report.SoftStableStrictBets, overwrite, cancellationToken);
        await WriteAllRowsCsvAsync(Path.Combine(outputFolder, "best-confederation-team-stability-all-blend-results.csv"), allRows, overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    private static async Task WriteCandidatesCsvAsync(string path, IEnumerable<BestConfederationTeamMarketStabilityCandidate> candidates, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("confederation,selection,group_code,book_odds,book_probability_used,strict_blend_count,bet_blend_count,blend_count,strong_stable_strict_bet,soft_stable_strict_bet,avg_edge_probability,min_edge_probability,max_edge_probability,avg_simulation_probability,min_simulation_probability,max_simulation_probability,strict_blend_labels,decisions_by_blend,notes");
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                c.Confederation, c.Selection, c.GroupCode,
                Format(c.BookOdds), Format(c.BookProbabilityUsed),
                c.StrictBlendCount.ToString(CultureInfo.InvariantCulture), c.BetBlendCount.ToString(CultureInfo.InvariantCulture), c.BlendCount.ToString(CultureInfo.InvariantCulture),
                c.StrongStableStrictBet.ToString(), c.SoftStableStrictBet.ToString(),
                Format(c.AvgEdgeProbability), Format(c.MinEdgeProbability), Format(c.MaxEdgeProbability),
                Format(c.AvgSimulationProbability), Format(c.MinSimulationProbability), Format(c.MaxSimulationProbability),
                c.StrictBlendLabels, c.DecisionsByBlend, c.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteAllRowsCsvAsync(string path, IEnumerable<BestConfederationTeamMarketStabilityBlendRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("blend_label,market_weight,elo_weight,ea_weight,is_strict_bet,confederation,selection,group_code,book_odds,book_probability_used,simulation_probability,edge_probability,decision,notes");
        foreach (var item in rows.OrderBy(x => x.BlendLabel).ThenByDescending(x => x.IsStrictBet).ThenByDescending(x => x.Row.EdgeProbability))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = item.Row;
            var values = new[]
            {
                item.BlendLabel, Format(item.MarketWeight), Format(item.EloWeight), Format(item.EaWeight), item.IsStrictBet.ToString(),
                r.Confederation, r.Selection, r.GroupCode, Format(r.BookOdds), Format(r.BookProbabilityUsed), Format(r.SimulationProbability), Format(r.EdgeProbability), r.Decision, r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class BestConfederationTeamMarketStabilityReport
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public double MinEdge { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public string StrongStableDefinition { get; init; } = string.Empty;
    public string SoftStableDefinition { get; init; } = string.Empty;
    public List<BestConfederationTeamMarketStabilityBlendResult> Blends { get; init; } = [];
    public int CandidateCount { get; init; }
    public int StrongStableStrictBetCount { get; init; }
    public int SoftStableStrictBetCount { get; init; }
    public List<BestConfederationTeamMarketStabilityCandidate> Candidates { get; init; } = [];
    public List<BestConfederationTeamMarketStabilityCandidate> StrongStableStrictBets { get; init; } = [];
    public List<BestConfederationTeamMarketStabilityCandidate> SoftStableStrictBets { get; init; } = [];
}

public sealed class BestConfederationTeamMarketStabilityBlendResult
{
    public string Label { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public int BetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string OutputFolder { get; init; } = string.Empty;
}

public sealed class BestConfederationTeamMarketStabilityBlendRow
{
    public string BlendLabel { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public bool IsStrictBet { get; init; }
    public BestConfederationTeamMarketComparisonRow Row { get; init; } = new();
}

public sealed class BestConfederationTeamMarketStabilityCandidate
{
    public string Confederation { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public int StrictBlendCount { get; init; }
    public int BetBlendCount { get; init; }
    public int BlendCount { get; init; }
    public bool StrongStableStrictBet { get; init; }
    public bool SoftStableStrictBet { get; init; }
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
