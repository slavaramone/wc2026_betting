using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Simulation;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public interface ISimpleSimulationMarketComparer
{
    string RequiredOddsFileOption { get; }
    string DefaultOutputPrefix { get; }
    Task<SimpleMarketComparisonResult> CompareFromSimulationAsync(
        string modelsFolder,
        Wc2026SimulationResultSet simulation,
        string oddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken);
    bool IsStrictBet(SimpleMarketComparisonRow row);
}

public sealed class WinnerGroupMarketOddsComparer : ISimpleSimulationMarketComparer
{
    public string RequiredOddsFileOption => "--winner-group-odds-file";
    public string DefaultOutputPrefix => "winner-group-market";

    public async Task<SimpleMarketComparisonResult> CompareFromFilesAsync(string modelsFolder, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        var simulation = await SimpleMarketIo.ReadSimulationAsync(modelsFolder, "compare-winner-group-markets", cancellationToken);
        return await CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, outputFolder, minEdge, overwrite, cancellationToken);
    }

    public async Task<SimpleMarketComparisonResult> CompareFromSimulationAsync(string modelsFolder, Wc2026SimulationResultSet simulation, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        SimpleMarketIo.ValidateOddsFile(oddsFile, RequiredOddsFileOption);
        var probabilityByGroup = simulation.Teams
            .GroupBy(x => x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => SimpleMarketMath.Round(x.Sum(t => t.WinnerProbability)), StringComparer.OrdinalIgnoreCase);

        var rows = CompareYesNoBySegment(
            oddsFile,
            segmentHeaders: ["group", "group_code", "market_group"],
            defaultMarket: "WinnerGroup",
            probabilityBySegment: probabilityByGroup,
            minEdge: minEdge).ToList();

        var result = SimpleMarketResultBuilder.Build(modelsFolder, simulation.Iterations, minEdge, rows, StrictRules, IsStrictBet);
        await SimpleMarketIo.WriteResultAsync(result, outputFolder, DefaultOutputPrefix, IsStrictBet, overwrite, cancellationToken);
        return result;
    }

    public bool IsStrictBet(SimpleMarketComparisonRow row)
        => row.Decision == "BET" && row.EdgeProbability is >= 0.05 && row.BookOdds is >= 1.50 && row.BookProbabilityNoVig is not null && row.Side.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    private const string StrictRules = "edge >= 0.05; book odds >= 1.50; paired no-vig probability required; YES side only";

    private static IEnumerable<SimpleMarketComparisonRow> CompareYesNoBySegment(string oddsFile, IReadOnlyList<string> segmentHeaders, string defaultMarket, IReadOnlyDictionary<string, double> probabilityBySegment, double minEdge)
    {
        var rawRows = SimpleMarketIo.ReadCsv(oddsFile).ToList();
        var oddsByKey = rawRows
            .Select(x => new
            {
                Segment = NormalizeSegment(SimpleMarketIo.GetAny(x, segmentHeaders)),
                Side = SimpleMarketMath.NormalizeSide(SimpleMarketIo.Get(x, "side")),
                Odds = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(x, "odds"))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Segment) && x.Odds is > 1.0)
            .GroupBy(x => $"{x.Segment}|{x.Side}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Odds!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rawRows)
        {
            var market = SimpleMarketIo.Get(row, "market");
            if (string.IsNullOrWhiteSpace(market)) market = defaultMarket;
            var rawSegment = SimpleMarketIo.GetAny(row, segmentHeaders);
            var segment = NormalizeSegment(rawSegment);
            var selection = SimpleMarketIo.Get(row, "selection");
            if (string.IsNullOrWhiteSpace(selection)) selection = string.IsNullOrWhiteSpace(segment) ? rawSegment : $"Group {segment}";
            var side = SimpleMarketMath.NormalizeSide(SimpleMarketIo.Get(row, "side"));
            var bookOdds = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(row, "odds"));
            var source = SimpleMarketIo.Get(row, "source_image");
            if (bookOdds is null or <= 1.0) continue;
            if (string.IsNullOrWhiteSpace(segment))
            {
                yield return SimpleMarketResultBuilder.Invalid(market, rawSegment, selection, side, bookOdds, source, "Missing group/segment column value.");
                continue;
            }
            if (!probabilityBySegment.TryGetValue(segment, out var yesProbability))
            {
                yield return SimpleMarketResultBuilder.Invalid(market, segment, selection, side, bookOdds, source, $"Segment not found in simulation: {segment}");
                continue;
            }
            var paired = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(row, "paired_odds"));
            if (paired is null && oddsByKey.TryGetValue($"{segment}|{SimpleMarketMath.OppositeSide(side)}", out var pairedFromMap)) paired = pairedFromMap;
            var sim = side.Equals("No", StringComparison.OrdinalIgnoreCase) ? 1.0 - yesProbability : yesProbability;
            yield return SimpleMarketResultBuilder.Row(market, segment, selection, string.Empty, side, bookOdds, paired, sim, source, minEdge, string.Empty);
        }
    }

    private static string NormalizeSegment(string value)
    {
        var s = (value ?? string.Empty).Trim().ToUpperInvariant();
        s = s.Replace("GROUP", string.Empty).Replace("ГРУППА", string.Empty).Trim();
        return s;
    }
}

public sealed class WinnerConfederationMarketOddsComparer : ISimpleSimulationMarketComparer
{
    public string RequiredOddsFileOption => "--winner-confederation-odds-file";
    public string DefaultOutputPrefix => "winner-confederation-market";

    public async Task<SimpleMarketComparisonResult> CompareFromFilesAsync(string modelsFolder, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        var simulation = await SimpleMarketIo.ReadSimulationAsync(modelsFolder, "compare-winner-confederation-markets", cancellationToken);
        return await CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, outputFolder, minEdge, overwrite, cancellationToken);
    }

    public async Task<SimpleMarketComparisonResult> CompareFromSimulationAsync(string modelsFolder, Wc2026SimulationResultSet simulation, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        SimpleMarketIo.ValidateOddsFile(oddsFile, RequiredOddsFileOption);
        var probabilityByConfederation = simulation.Teams
            .Select(t => new { Team = t, Info = TeamConfederationCatalog.TryGetByTeam(t.Team) })
            .Where(x => x.Info is not null)
            .GroupBy(x => x.Info!.Confederation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => SimpleMarketMath.Round(x.Sum(t => t.Team.WinnerProbability)), StringComparer.OrdinalIgnoreCase);

        var rows = CompareYesNoByConfederation(oddsFile, probabilityByConfederation, minEdge).ToList();
        var result = SimpleMarketResultBuilder.Build(modelsFolder, simulation.Iterations, minEdge, rows, StrictRules, IsStrictBet);
        await SimpleMarketIo.WriteResultAsync(result, outputFolder, DefaultOutputPrefix, IsStrictBet, overwrite, cancellationToken);
        return result;
    }

    public bool IsStrictBet(SimpleMarketComparisonRow row)
        => row.Decision == "BET" && row.EdgeProbability is >= 0.05 && row.BookOdds is >= 1.50 && row.BookProbabilityNoVig is not null && row.Side.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    private const string StrictRules = "edge >= 0.05; book odds >= 1.50; paired no-vig probability required; YES side only";

    private static IEnumerable<SimpleMarketComparisonRow> CompareYesNoByConfederation(string oddsFile, IReadOnlyDictionary<string, double> probabilityByConfederation, double minEdge)
    {
        var rawRows = SimpleMarketIo.ReadCsv(oddsFile).ToList();
        var oddsByKey = rawRows
            .Select(x => new
            {
                Segment = TeamConfederationCatalog.NormalizeConfederation(SimpleMarketIo.GetAny(x, ["confederation", "confed", "market_group"])),
                Side = SimpleMarketMath.NormalizeSide(SimpleMarketIo.Get(x, "side")),
                Odds = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(x, "odds"))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Segment) && x.Odds is > 1.0)
            .GroupBy(x => $"{x.Segment}|{x.Side}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Odds!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rawRows)
        {
            var market = SimpleMarketIo.Get(row, "market");
            if (string.IsNullOrWhiteSpace(market)) market = "WinnerConfederation";
            var confederation = TeamConfederationCatalog.NormalizeConfederation(SimpleMarketIo.GetAny(row, ["confederation", "confed", "market_group"]));
            var selection = SimpleMarketIo.Get(row, "selection");
            var side = SimpleMarketMath.NormalizeSide(SimpleMarketIo.Get(row, "side"));
            var bookOdds = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(row, "odds"));
            var source = SimpleMarketIo.Get(row, "source_image");
            if (bookOdds is null or <= 1.0) continue;
            if (string.IsNullOrWhiteSpace(confederation))
            {
                yield return SimpleMarketResultBuilder.Invalid(market, confederation, selection, side, bookOdds, source, "Missing confederation column/value.");
                continue;
            }
            if (!probabilityByConfederation.TryGetValue(confederation, out var yesProbability))
            {
                yield return SimpleMarketResultBuilder.Invalid(market, confederation, selection, side, bookOdds, source, $"Confederation not found in simulation: {confederation}");
                continue;
            }
            var paired = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(row, "paired_odds"));
            if (paired is null && oddsByKey.TryGetValue($"{confederation}|{SimpleMarketMath.OppositeSide(side)}", out var pairedFromMap)) paired = pairedFromMap;
            var sim = side.Equals("No", StringComparison.OrdinalIgnoreCase) ? 1.0 - yesProbability : yesProbability;
            yield return SimpleMarketResultBuilder.Row(market, confederation, selection, string.Empty, side, bookOdds, paired, sim, source, minEdge, string.Empty);
        }
    }
}

public sealed class FinalistPairMarketOddsComparer : ISimpleSimulationMarketComparer
{
    public string RequiredOddsFileOption => "--finalist-pair-odds-file";
    public string DefaultOutputPrefix => "finalist-pair-market";

    public async Task<SimpleMarketComparisonResult> CompareFromFilesAsync(string modelsFolder, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        var simulation = await SimpleMarketIo.ReadSimulationAsync(modelsFolder, "compare-finalist-pair-markets", cancellationToken);
        return await CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, outputFolder, minEdge, overwrite, cancellationToken);
    }

    public async Task<SimpleMarketComparisonResult> CompareFromSimulationAsync(string modelsFolder, Wc2026SimulationResultSet simulation, string oddsFile, string outputFolder, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        SimpleMarketIo.ValidateOddsFile(oddsFile, RequiredOddsFileOption);
        if (simulation.FinalistPairs.Count == 0)
            throw new InvalidOperationException("Simulation summary has no finalist-pair probabilities. Re-run run-simulation after applying this patch.");

        var rows = CompareFinalistPairs(oddsFile, simulation, minEdge).ToList();
        var result = SimpleMarketResultBuilder.Build(modelsFolder, simulation.Iterations, minEdge, rows, StrictRules, IsStrictBet);
        await SimpleMarketIo.WriteResultAsync(result, outputFolder, DefaultOutputPrefix, IsStrictBet, overwrite, cancellationToken);
        return result;
    }

    public bool IsStrictBet(SimpleMarketComparisonRow row)
        => row.Decision == "BET" && row.EdgeProbability is >= 0.05 && row.BookOdds is >= 1.50 && row.BookProbabilityNoVig is not null;

    private const string StrictRules = "edge >= 0.05; book odds >= 1.50; no-vig probability required within finalist-pair market";

    private static IEnumerable<SimpleMarketComparisonRow> CompareFinalistPairs(string oddsFile, Wc2026SimulationResultSet simulation, double minEdge)
    {
        var rawRows = SimpleMarketIo.ReadCsv(oddsFile).ToList();
        var overround = rawRows.Select(x => SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(x, "odds"))).Where(x => x is > 1.0).Sum(x => 1.0 / x!.Value);
        var simByKey = simulation.FinalistPairs.ToDictionary(x => PairKey(x.Team1, x.Team2), x => x.FinalistPairProbability, StringComparer.OrdinalIgnoreCase);
        var teamNames = simulation.Teams.Select(x => x.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rawRows)
        {
            var market = SimpleMarketIo.Get(row, "market");
            if (string.IsNullOrWhiteSpace(market)) market = "FinalistPair";
            var team1Raw = SimpleMarketIo.GetAny(row, ["team1", "selection1", "p1"]);
            var team2Raw = SimpleMarketIo.GetAny(row, ["team2", "selection2", "p2"]);
            var selection = SimpleMarketIo.Get(row, "selection");
            var source = SimpleMarketIo.Get(row, "source_image");
            var bookOdds = SimpleMarketIo.ParseDouble(SimpleMarketIo.Get(row, "odds"));
            if (bookOdds is null or <= 1.0) continue;

            if ((string.IsNullOrWhiteSpace(team1Raw) || string.IsNullOrWhiteSpace(team2Raw)) && !string.IsNullOrWhiteSpace(selection))
            {
                var split = selection.Split(['-', '—', '–'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (split.Length >= 2)
                {
                    team1Raw = split[0];
                    team2Raw = split[1];
                }
            }

            var team1 = ResolveTeamName(team1Raw, teamNames);
            var team2 = ResolveTeamName(team2Raw, teamNames);
            if (team1 is null || team2 is null)
            {
                yield return SimpleMarketResultBuilder.Invalid(market, string.Empty, selection, "Yes", bookOdds, source, $"Finalist pair teams not found: '{team1Raw}' / '{team2Raw}'.");
                continue;
            }

            var key = PairKey(team1, team2);
            var sim = simByKey.GetValueOrDefault(key);
            var rawProbability = SimpleMarketMath.Round(1.0 / bookOdds.Value);
            var noVig = overround > 0 ? SimpleMarketMath.Round((1.0 / bookOdds.Value) / overround) : (double?)null;
            yield return SimpleMarketResultBuilder.Row(market, string.Empty, $"{team1} - {team2}", string.Empty, "Yes", bookOdds, null, sim, source, minEdge, string.Empty, rawProbability, noVig);
        }
    }

    private static string PairKey(string team1, string team2)
    {
        var a = SimpleMarketMath.NormalizeTeam(team1);
        var b = SimpleMarketMath.NormalizeTeam(team2);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private static string? ResolveTeamName(string raw, IReadOnlySet<string> teamNames)
    {
        var normalized = SimpleMarketMath.NormalizeTeam(raw);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var direct = teamNames.FirstOrDefault(x => string.Equals(SimpleMarketMath.NormalizeTeam(x), normalized, StringComparison.OrdinalIgnoreCase));
        return direct;
    }
}

public sealed class WinnerGroupMarketStabilityReporter : SimpleMarketStabilityReporterBase
{
    public WinnerGroupMarketStabilityReporter() : base(new WinnerGroupMarketOddsComparer(), "winner-group-stability") { }
}

public sealed class WinnerConfederationMarketStabilityReporter : SimpleMarketStabilityReporterBase
{
    public WinnerConfederationMarketStabilityReporter() : base(new WinnerConfederationMarketOddsComparer(), "winner-confederation-stability") { }
}

public sealed class FinalistPairMarketStabilityReporter : SimpleMarketStabilityReporterBase
{
    public FinalistPairMarketStabilityReporter() : base(new FinalistPairMarketOddsComparer(), "finalist-pair-stability") { }
}

public abstract class SimpleMarketStabilityReporterBase
{
    private readonly ISimpleSimulationMarketComparer _comparer;
    private readonly string _filePrefix;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static readonly IReadOnlyList<Wc2026SimulationWeights> DefaultBlends =
    [
        new(1.00, 0.00, 0.00),
        new(0.90, 0.08, 0.02),
        new(0.85, 0.12, 0.03),
        new(0.80, 0.15, 0.05)
    ];

    protected SimpleMarketStabilityReporterBase(ISimpleSimulationMarketComparer comparer, string filePrefix)
    {
        _comparer = comparer;
        _filePrefix = filePrefix;
    }

    public async Task<SimpleMarketStabilityReport> BuildAsync(string modelsFolder, string oddsFile, string outputFolder, int iterations, int seed, double minEdge, bool overwrite, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder)) throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");
        SimpleMarketIo.ValidateOddsFile(oddsFile, _comparer.RequiredOddsFileOption);
        if (iterations <= 0) throw new ArgumentException("--iterations must be greater than zero.");
        Directory.CreateDirectory(outputFolder);

        var runner = new Wc2026SimulationRunner();
        var blendResults = new List<SimpleMarketStabilityBlendResult>();
        var allRows = new List<SimpleMarketStabilityBlendRow>();

        foreach (var blend in DefaultBlends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = blend.Normalized();
            var label = $"market{normalized.Market * 100:0}_elo{normalized.Elo * 100:0}_ea{normalized.Ea * 100:0}";
            var blendFolder = Path.Combine(outputFolder, "blends", label);
            var simulation = await runner.RunFromModelsFolderAsync(modelsFolder, iterations, seed, blendFolder, overwrite, normalized, cancellationToken);
            var comparison = await _comparer.CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, blendFolder, minEdge, overwrite, cancellationToken);

            blendResults.Add(new SimpleMarketStabilityBlendResult
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
                allRows.Add(new SimpleMarketStabilityBlendRow
                {
                    BlendLabel = label,
                    MarketWeight = normalized.Market,
                    EloWeight = normalized.Elo,
                    EaWeight = normalized.Ea,
                    IsStrictBet = _comparer.IsStrictBet(row),
                    Row = row
                });
            }
        }

        var candidates = BuildCandidates(allRows, DefaultBlends.Count);
        var report = new SimpleMarketStabilityReport
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            OutputFolder = outputFolder,
            Iterations = iterations,
            Seed = seed,
            MinEdge = minEdge,
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

    private static List<SimpleMarketStabilityCandidate> BuildCandidates(IReadOnlyList<SimpleMarketStabilityBlendRow> allRows, int blendCount)
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
                var strictBlendCount = strictRows.Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var edgeValues = rows.Select(x => x.Row.EdgeProbability ?? 0).ToList();
                var simValues = rows.Select(x => x.Row.SimulationProbability ?? 0).ToList();
                return new SimpleMarketStabilityCandidate
                {
                    Market = first.Market,
                    Segment = first.Segment,
                    Selection = first.Selection,
                    Side = first.Side,
                    BookOdds = first.BookOdds,
                    BookProbabilityUsed = first.BookProbabilityUsed,
                    StrictBlendCount = strictBlendCount,
                    BetBlendCount = rows.Where(x => x.Row.Decision == "BET").Select(x => x.BlendLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    BlendCount = blendCount,
                    StrongStableStrictBet = strictBlendCount == blendCount,
                    SoftStableStrictBet = strictBlendCount >= Math.Max(1, blendCount - 1),
                    AvgEdgeProbability = SimpleMarketMath.Round(edgeValues.Average()),
                    MinEdgeProbability = SimpleMarketMath.Round(edgeValues.Min()),
                    MaxEdgeProbability = SimpleMarketMath.Round(edgeValues.Max()),
                    AvgSimulationProbability = SimpleMarketMath.Round(simValues.Average()),
                    MinSimulationProbability = SimpleMarketMath.Round(simValues.Min()),
                    MaxSimulationProbability = SimpleMarketMath.Round(simValues.Max()),
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

    private static string CandidateKey(SimpleMarketComparisonRow row)
        => string.Join('|', row.Market, row.Segment, row.Selection, row.Side);

    private async Task WriteReportAsync(SimpleMarketStabilityReport report, IReadOnlyList<SimpleMarketStabilityBlendRow> allRows, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        await SimpleMarketIo.WriteJsonAsync(Path.Combine(outputFolder, $"{_filePrefix}-summary.json"), report, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, $"{_filePrefix}-candidates.csv"), report.Candidates, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, $"{_filePrefix}-strong-stable-strict-bets.csv"), report.StrongStableStrictBets, overwrite, cancellationToken);
        await WriteCandidatesCsvAsync(Path.Combine(outputFolder, $"{_filePrefix}-soft-stable-strict-bets.csv"), report.SoftStableStrictBets, overwrite, cancellationToken);
        await WriteAllRowsCsvAsync(Path.Combine(outputFolder, $"{_filePrefix}-all-blend-results.csv"), allRows, overwrite, cancellationToken);
    }

    private static async Task WriteCandidatesCsvAsync(string path, IEnumerable<SimpleMarketStabilityCandidate> candidates, bool overwrite, CancellationToken cancellationToken)
    {
        SimpleMarketIo.EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("market,segment,selection,side,book_odds,book_probability_used,strict_blend_count,bet_blend_count,blend_count,strong_stable_strict_bet,soft_stable_strict_bet,avg_edge_probability,min_edge_probability,max_edge_probability,avg_simulation_probability,min_simulation_probability,max_simulation_probability,strict_blend_labels,decisions_by_blend,notes");
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[] { c.Market, c.Segment, c.Selection, c.Side, SimpleMarketIo.Format(c.BookOdds), SimpleMarketIo.Format(c.BookProbabilityUsed), c.StrictBlendCount.ToString(CultureInfo.InvariantCulture), c.BetBlendCount.ToString(CultureInfo.InvariantCulture), c.BlendCount.ToString(CultureInfo.InvariantCulture), c.StrongStableStrictBet.ToString(), c.SoftStableStrictBet.ToString(), SimpleMarketIo.Format(c.AvgEdgeProbability), SimpleMarketIo.Format(c.MinEdgeProbability), SimpleMarketIo.Format(c.MaxEdgeProbability), SimpleMarketIo.Format(c.AvgSimulationProbability), SimpleMarketIo.Format(c.MinSimulationProbability), SimpleMarketIo.Format(c.MaxSimulationProbability), c.StrictBlendLabels, c.DecisionsByBlend, c.Notes };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteAllRowsCsvAsync(string path, IEnumerable<SimpleMarketStabilityBlendRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        SimpleMarketIo.EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("blend_label,market_weight,elo_weight,ea_weight,is_strict_bet,market,segment,selection,side,book_odds,book_probability_used,simulation_probability,edge_probability,decision,notes");
        foreach (var item in rows.OrderBy(x => x.BlendLabel).ThenByDescending(x => x.IsStrictBet).ThenByDescending(x => x.Row.EdgeProbability))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = item.Row;
            var values = new[] { item.BlendLabel, SimpleMarketIo.Format(item.MarketWeight), SimpleMarketIo.Format(item.EloWeight), SimpleMarketIo.Format(item.EaWeight), item.IsStrictBet.ToString(), r.Market, r.Segment, r.Selection, r.Side, SimpleMarketIo.Format(r.BookOdds), SimpleMarketIo.Format(r.BookProbabilityUsed), SimpleMarketIo.Format(r.SimulationProbability), SimpleMarketIo.Format(r.EdgeProbability), r.Decision, r.Notes };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }
}

public static class SimpleMarketResultBuilder
{
    public static SimpleMarketComparisonResult Build(string modelsFolder, int iterations, double minEdge, List<SimpleMarketComparisonRow> rows, string strictRules, Func<SimpleMarketComparisonRow, bool> isStrictBet)
    {
        return new SimpleMarketComparisonResult
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            SimulationIterations = iterations,
            MinEdge = minEdge,
            Rows = rows,
            Summary = new SimpleMarketComparisonSummary
            {
                Rows = rows.Count,
                ValidRows = rows.Count(x => x.Decision != "INVALID"),
                InvalidRows = rows.Count(x => x.Decision == "INVALID"),
                BetRows = rows.Count(x => x.Decision == "BET"),
                LeanRows = rows.Count(x => x.Decision == "LEAN"),
                NoBetRows = rows.Count(x => x.Decision == "NO_BET"),
                StrictBetRows = rows.Count(isStrictBet),
                StrictRules = strictRules,
                TopEdges = rows.Where(x => x.Decision is "BET" or "LEAN").OrderByDescending(x => x.EdgeProbability).Take(50).Select(ToTopEdge).ToList(),
                StrictTopEdges = rows.Where(isStrictBet).OrderByDescending(x => x.EdgeProbability).Take(50).Select(ToTopEdge).ToList()
            }
        };
    }

    public static SimpleMarketTopEdge ToTopEdge(SimpleMarketComparisonRow row) => new()
    {
        Market = row.Market,
        Segment = row.Segment,
        Selection = row.Selection,
        Side = row.Side,
        BookOdds = row.BookOdds,
        SimulationProbability = row.SimulationProbability,
        BookProbabilityUsed = row.BookProbabilityUsed,
        EdgeProbability = row.EdgeProbability,
        Decision = row.Decision
    };

    public static SimpleMarketComparisonRow Invalid(string market, string segment, string selection, string side, double? bookOdds, string source, string notes) => new()
    {
        Market = string.IsNullOrWhiteSpace(market) ? "Unknown" : market,
        Segment = segment,
        Selection = selection,
        Side = string.IsNullOrWhiteSpace(side) ? "Yes" : side,
        BookOdds = bookOdds,
        Decision = "INVALID",
        SourceImage = source,
        Notes = notes
    };

    public static SimpleMarketComparisonRow Row(string market, string segment, string selection, string opponent, string side, double? bookOdds, double? pairedBookOdds, double simulationProbability, string source, double minEdge, string notes, double? forcedRawProbability = null, double? forcedNoVigProbability = null)
    {
        var raw = forcedRawProbability ?? (bookOdds is > 1.0 ? SimpleMarketMath.Round(1.0 / bookOdds.Value) : (double?)null);
        double? noVig = forcedNoVigProbability;
        if (noVig is null && bookOdds is > 1.0 && pairedBookOdds is > 1.0)
        {
            var p = 1.0 / bookOdds.Value;
            var q = 1.0 / pairedBookOdds.Value;
            noVig = SimpleMarketMath.Round(p / (p + q));
        }
        var bookUsed = noVig ?? raw;
        var edge = bookUsed is null ? (double?)null : SimpleMarketMath.Round(simulationProbability - bookUsed.Value);
        var decision = edge is null ? "INVALID" : edge.Value >= minEdge ? "BET" : edge.Value >= minEdge / 2.0 ? "LEAN" : "NO_BET";
        return new SimpleMarketComparisonRow
        {
            Market = market,
            Segment = segment,
            Selection = selection,
            Opponent = opponent,
            Side = side,
            BookOdds = bookOdds,
            PairedBookOdds = pairedBookOdds,
            BookProbabilityRaw = raw,
            BookProbabilityNoVig = noVig,
            BookProbabilityUsed = bookUsed,
            SimulationProbability = SimpleMarketMath.Round(simulationProbability),
            FairOdds = SimpleMarketMath.FairOdds(simulationProbability),
            EdgeProbability = edge,
            EdgePercent = edge is null ? null : SimpleMarketMath.Round(edge.Value * 100.0),
            Decision = decision,
            SourceImage = source,
            Notes = notes
        };
    }
}

public static class SimpleMarketIo
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<Wc2026SimulationResultSet> ReadSimulationAsync(string modelsFolder, string commandName, CancellationToken cancellationToken)
    {
        var simulationPath = Path.Combine(modelsFolder, "simulation", "wc2026-simulation-summary.json");
        if (!File.Exists(simulationPath))
            throw new FileNotFoundException($"Simulation summary not found. Run run-simulation before {commandName}.", simulationPath);
        var json = await File.ReadAllTextAsync(simulationPath, cancellationToken);
        return JsonSerializer.Deserialize<Wc2026SimulationResultSet>(json, JsonOptions) ?? throw new InvalidOperationException($"Failed to deserialize simulation summary: {simulationPath}");
    }

    public static void ValidateOddsFile(string oddsFile, string optionName)
    {
        if (string.IsNullOrWhiteSpace(oddsFile)) throw new ArgumentException($"{optionName} is required.");
        if (!File.Exists(oddsFile)) throw new FileNotFoundException($"Odds CSV not found: {oddsFile}", oddsFile);
    }

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];
        var headers = SimpleCsv.ParseLine(lines[0]).Select(NormalizeHeader).ToList();
        var result = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = SimpleCsv.ParseLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++) row[headers[i]] = i < values.Count ? values[i] : string.Empty;
            result.Add(row);
        }
        return result;
    }

    public static async Task WriteResultAsync(SimpleMarketComparisonResult result, string outputFolder, string prefix, Func<SimpleMarketComparisonRow, bool> isStrictBet, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        await WriteJsonAsync(Path.Combine(outputFolder, $"{prefix}-comparison-summary.json"), result, overwrite, cancellationToken);
        await WriteRowsCsvAsync(Path.Combine(outputFolder, $"{prefix}-comparison.csv"), result.Rows, overwrite, cancellationToken);
        await WriteRowsCsvAsync(Path.Combine(outputFolder, $"{prefix}-comparison-bets-strict.csv"), result.Rows.Where(isStrictBet), overwrite, cancellationToken);
    }

    public static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    public static async Task WriteRowsCsvAsync(string path, IEnumerable<SimpleMarketComparisonRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("market,segment,selection,opponent,side,book_odds,paired_book_odds,book_probability_raw,book_probability_no_vig,book_probability_used,simulation_probability,fair_odds,edge_probability,edge_percent,decision,source_image,notes");
        foreach (var r in rows.OrderByDescending(x => x.EdgeProbability))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[] { r.Market, r.Segment, r.Selection, r.Opponent, r.Side, Format(r.BookOdds), Format(r.PairedBookOdds), Format(r.BookProbabilityRaw), Format(r.BookProbabilityNoVig), Format(r.BookProbabilityUsed), Format(r.SimulationProbability), Format(r.FairOdds), Format(r.EdgeProbability), Format(r.EdgePercent), r.Decision, r.SourceImage, r.Notes };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    public static void EnsureCanWrite(string path, bool overwrite)
    {
        if (File.Exists(path) && !overwrite) throw new IOException($"File already exists: {path}. Use --overwrite.");
    }

    public static string Get(Dictionary<string, string> row, string key) => row.TryGetValue(NormalizeHeader(key), out var value) ? value.Trim() : string.Empty;
    public static string GetAny(Dictionary<string, string> row, IReadOnlyList<string> keys) => keys.Select(k => Get(row, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    public static double? ParseDouble(string value) => double.TryParse((value ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    public static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string NormalizeHeader(string value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
}

public static class SimpleMarketMath
{
    public static double Round(double value) => Math.Round(value, 6);
    public static double RoundProbability(int count, int total) => total <= 0 ? 0 : Round(count / (double)total);
    public static double? FairOdds(double probability) => probability <= 0 ? null : Round(1.0 / probability);
    public static string NormalizeSide(string side) => (side ?? string.Empty).Trim().Equals("No", StringComparison.OrdinalIgnoreCase) || (side ?? string.Empty).Trim().Equals("Нет", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes";
    public static string OppositeSide(string side) => NormalizeSide(side).Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes";
    public static string NormalizeTeam(string value) => (value ?? string.Empty).Trim().ToUpperInvariant().Replace("&", "AND").Replace("TÜRKIYE", "TURKIYE").Replace("TURKEY", "TURKIYE").Replace("USA", "UNITED STATES");
}

public sealed class SimpleMarketComparisonResult
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public int SimulationIterations { get; init; }
    public double MinEdge { get; init; }
    public SimpleMarketComparisonSummary Summary { get; init; } = new();
    public List<SimpleMarketComparisonRow> Rows { get; init; } = [];
}

public sealed class SimpleMarketComparisonSummary
{
    public int Rows { get; init; }
    public int ValidRows { get; init; }
    public int InvalidRows { get; init; }
    public int BetRows { get; init; }
    public int LeanRows { get; init; }
    public int NoBetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public List<SimpleMarketTopEdge> TopEdges { get; init; } = [];
    public List<SimpleMarketTopEdge> StrictTopEdges { get; init; } = [];
}

public sealed class SimpleMarketTopEdge
{
    public string Market { get; init; } = string.Empty;
    public string Segment { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? SimulationProbability { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? EdgeProbability { get; init; }
    public string Decision { get; init; } = string.Empty;
}

public sealed class SimpleMarketComparisonRow
{
    public string Market { get; init; } = string.Empty;
    public string Segment { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Opponent { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? PairedBookOdds { get; init; }
    public double? BookProbabilityRaw { get; init; }
    public double? BookProbabilityNoVig { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? SimulationProbability { get; init; }
    public double? FairOdds { get; init; }
    public double? EdgeProbability { get; init; }
    public double? EdgePercent { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string SourceImage { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed class SimpleMarketStabilityReport
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public double MinEdge { get; init; }
    public string StrongStableDefinition { get; init; } = string.Empty;
    public string SoftStableDefinition { get; init; } = string.Empty;
    public List<SimpleMarketStabilityBlendResult> Blends { get; init; } = [];
    public int CandidateCount { get; init; }
    public int StrongStableStrictBetCount { get; init; }
    public int SoftStableStrictBetCount { get; init; }
    public List<SimpleMarketStabilityCandidate> Candidates { get; init; } = [];
    public List<SimpleMarketStabilityCandidate> StrongStableStrictBets { get; init; } = [];
    public List<SimpleMarketStabilityCandidate> SoftStableStrictBets { get; init; } = [];
}

public sealed class SimpleMarketStabilityBlendResult
{
    public string Label { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public int BetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string OutputFolder { get; init; } = string.Empty;
}

public sealed class SimpleMarketStabilityBlendRow
{
    public string BlendLabel { get; init; } = string.Empty;
    public double MarketWeight { get; init; }
    public double EloWeight { get; init; }
    public double EaWeight { get; init; }
    public bool IsStrictBet { get; init; }
    public SimpleMarketComparisonRow Row { get; init; } = new();
}

public sealed class SimpleMarketStabilityCandidate
{
    public string Market { get; init; } = string.Empty;
    public string Segment { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
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
