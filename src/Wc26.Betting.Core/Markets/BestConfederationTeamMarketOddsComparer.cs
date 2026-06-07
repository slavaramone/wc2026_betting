using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class BestConfederationTeamMarketOddsComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<BestConfederationTeamMarketComparisonResult> CompareFromFilesAsync(
        string modelsFolder,
        string oddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(oddsFile))
            throw new ArgumentException("--best-confederation-odds-file is required.");
        if (!File.Exists(oddsFile))
            throw new FileNotFoundException($"Best-confederation-team odds CSV not found: {oddsFile}", oddsFile);

        var simulationPath = Path.Combine(modelsFolder, "simulation", "wc2026-simulation-summary.json");
        if (!File.Exists(simulationPath))
            throw new FileNotFoundException("Simulation summary not found. Run run-simulation before compare-best-confederation-team-markets.", simulationPath);

        var json = await File.ReadAllTextAsync(simulationPath, cancellationToken);
        var simulation = JsonSerializer.Deserialize<Wc2026SimulationResultSet>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize simulation summary: {simulationPath}");

        return await CompareFromSimulationAsync(modelsFolder, simulation, oddsFile, outputFolder, minEdge, overwrite, cancellationToken);
    }

    public async Task<BestConfederationTeamMarketComparisonResult> CompareFromSimulationAsync(
        string modelsFolder,
        Wc2026SimulationResultSet simulation,
        string oddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (simulation.BestConfederationTeams.Count == 0)
            throw new InvalidOperationException("Simulation summary has no best-confederation probabilities. Re-run run-simulation after applying this patch.");

        var rows = Compare(oddsFile, simulation, minEdge).ToList();
        var result = new BestConfederationTeamMarketComparisonResult
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            SimulationIterations = simulation.Iterations,
            MinEdge = minEdge,
            Rows = rows,
            Summary = BuildSummary(rows)
        };

        await WriteAsync(result, outputFolder, overwrite, cancellationToken);
        return result;
    }

    private static IEnumerable<BestConfederationTeamMarketComparisonRow> Compare(string oddsFile, Wc2026SimulationResultSet simulation, double minEdge)
    {
        var rawRows = ReadCsv(oddsFile).ToList();
        var simByKey = simulation.BestConfederationTeams.ToDictionary(
            x => Key(x.Confederation, x.Team), x => x, StringComparer.OrdinalIgnoreCase);

        var overroundByConfederation = rawRows
            .Select(x => new
            {
                Confederation = TeamConfederationCatalog.NormalizeConfederation(GetAny(x, ["confederation", "market_group", "group", "confed"])),
                Odds = ParseDouble(Get(x, "odds"))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Confederation) && x.Odds is > 1.0)
            .GroupBy(x => x.Confederation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(v => 1.0 / v.Odds!.Value), StringComparer.OrdinalIgnoreCase);

        foreach (var row in rawRows)
        {
            var market = Get(row, "market");
            if (string.IsNullOrWhiteSpace(market)) market = "BestConfederationTeam";
            var confederation = TeamConfederationCatalog.NormalizeConfederation(GetAny(row, ["confederation", "market_group", "group", "confed"]));
            var selectionRaw = Get(row, "selection");
            var source = Get(row, "source_image");
            var bookOdds = ParseDouble(Get(row, "odds"));

            if (bookOdds is null or <= 1.0)
                continue;

            if (string.IsNullOrWhiteSpace(confederation))
            {
                yield return InvalidRow(market, confederation, selectionRaw, bookOdds, source, "Missing confederation column/value.");
                continue;
            }

            var info = TeamConfederationCatalog.TryGetByTeam(selectionRaw);
            if (info is null)
            {
                yield return InvalidRow(market, confederation, selectionRaw, bookOdds, source, $"Selection team not found in confederation catalog: {selectionRaw}");
                continue;
            }

            if (!string.Equals(info.Confederation, confederation, StringComparison.OrdinalIgnoreCase))
            {
                yield return InvalidRow(market, confederation, selectionRaw, bookOdds, source, $"Selection team '{selectionRaw}' belongs to {info.Confederation}, not {confederation}.");
                continue;
            }

            if (!simByKey.TryGetValue(Key(confederation, info.Team), out var sim))
            {
                yield return InvalidRow(market, confederation, selectionRaw, bookOdds, source, $"Selection team not found in simulation best-confederation output: {selectionRaw}");
                continue;
            }

            var rawProbability = Round(1.0 / bookOdds.Value);
            var overround = overroundByConfederation.GetValueOrDefault(confederation);
            var noVigProbability = overround > 0 ? Round((1.0 / bookOdds.Value) / overround) : (double?)null;
            var bookProbabilityUsed = noVigProbability ?? rawProbability;
            var simulationProbability = sim.BestInConfederationProbability;
            var edge = Round(simulationProbability - bookProbabilityUsed);
            var decision = edge >= minEdge ? "BET" : edge >= minEdge / 2.0 ? "LEAN" : "NO_BET";

            yield return new BestConfederationTeamMarketComparisonRow
            {
                Market = market,
                Confederation = confederation,
                Selection = info.Team,
                GroupCode = sim.GroupCode,
                BookOdds = bookOdds,
                BookProbabilityRaw = rawProbability,
                BookProbabilityNoVig = noVigProbability,
                BookProbabilityUsed = bookProbabilityUsed,
                SimulationProbability = Round(simulationProbability),
                FairOdds = FairOdds(simulationProbability),
                EdgeProbability = edge,
                EdgePercent = Round(edge * 100.0),
                Decision = decision,
                SourceImage = source,
                Notes = string.Empty
            };
        }
    }

    private static BestConfederationTeamMarketComparisonRow InvalidRow(string market, string confederation, string selection, double? bookOdds, string source, string notes)
        => new()
        {
            Market = string.IsNullOrWhiteSpace(market) ? "BestConfederationTeam" : market,
            Confederation = confederation,
            Selection = selection,
            BookOdds = bookOdds,
            Decision = "INVALID",
            SourceImage = source,
            Notes = notes
        };

    private static BestConfederationTeamMarketComparisonSummary BuildSummary(IReadOnlyList<BestConfederationTeamMarketComparisonRow> rows)
    {
        var valid = rows.Where(x => x.Decision != "INVALID").ToList();
        return new BestConfederationTeamMarketComparisonSummary
        {
            Rows = rows.Count,
            ValidRows = valid.Count,
            InvalidRows = rows.Count - valid.Count,
            BetRows = rows.Count(x => x.Decision == "BET"),
            LeanRows = rows.Count(x => x.Decision == "LEAN"),
            NoBetRows = rows.Count(x => x.Decision == "NO_BET"),
            StrictBetRows = rows.Count(IsStrictBet),
            StrictRules = "edge >= 0.05; book odds >= 1.50; no-vig probability required within confederation market",
            TopEdges = rows.Where(x => x.Decision is "BET" or "LEAN").OrderByDescending(x => x.EdgeProbability).Take(50).Select(ToTopEdge).ToList(),
            StrictTopEdges = rows.Where(IsStrictBet).OrderByDescending(x => x.EdgeProbability).Take(50).Select(ToTopEdge).ToList()
        };
    }

    public static bool IsStrictBet(BestConfederationTeamMarketComparisonRow row)
    {
        if (row.Decision != "BET") return false;
        if (row.EdgeProbability is null or < 0.05) return false;
        if (row.BookOdds is null or < 1.50) return false;
        if (row.BookProbabilityNoVig is null) return false;
        return true;
    }

    private static BestConfederationTeamMarketTopEdge ToTopEdge(BestConfederationTeamMarketComparisonRow row)
        => new()
        {
            Market = row.Market,
            Confederation = row.Confederation,
            Selection = row.Selection,
            GroupCode = row.GroupCode,
            BookOdds = row.BookOdds,
            SimulationProbability = row.SimulationProbability,
            BookProbabilityUsed = row.BookProbabilityUsed,
            EdgeProbability = row.EdgeProbability,
            Decision = row.Decision
        };

    private static async Task WriteAsync(BestConfederationTeamMarketComparisonResult result, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        await WriteJsonAsync(Path.Combine(outputFolder, "best-confederation-team-market-comparison-summary.json"), result, overwrite, cancellationToken);
        await WriteCsvAsync(Path.Combine(outputFolder, "best-confederation-team-market-comparison.csv"), result.Rows, overwrite, cancellationToken);
        await WriteCsvAsync(Path.Combine(outputFolder, "best-confederation-team-market-comparison-bets-strict.csv"), result.Rows.Where(IsStrictBet), overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    private static async Task WriteCsvAsync(string path, IEnumerable<BestConfederationTeamMarketComparisonRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("market,confederation,selection,group_code,book_odds,book_probability_raw,book_probability_no_vig,book_probability_used,simulation_probability,fair_odds,edge_probability,edge_percent,decision,source_image,notes");
        foreach (var r in rows.OrderByDescending(x => x.EdgeProbability))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                r.Market, r.Confederation, r.Selection, r.GroupCode,
                Format(r.BookOdds), Format(r.BookProbabilityRaw), Format(r.BookProbabilityNoVig), Format(r.BookProbabilityUsed),
                Format(r.SimulationProbability), Format(r.FairOdds), Format(r.EdgeProbability), Format(r.EdgePercent),
                r.Decision, r.SourceImage, r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];
        var headers = SimpleCsv.ParseLine(lines[0]).Select(NormalizeHeader).ToList();
        var result = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SimpleCsv.ParseLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < cells.Count; i++) row[headers[i]] = cells[i].Trim();
            result.Add(row);
        }
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key) => row.TryGetValue(NormalizeHeader(key), out var value) ? value.Trim() : string.Empty;
    private static string GetAny(IReadOnlyDictionary<string, string> row, IReadOnlyList<string> keys) => keys.Select(k => Get(row, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    private static string NormalizeHeader(string value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    private static double? ParseDouble(string value) => double.TryParse((value ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string Key(string confederation, string team) => $"{TeamConfederationCatalog.NormalizeConfederation(confederation)}|{TeamConfederationCatalog.NormalizeTeam(team)}";
    private static double? FairOdds(double probability) => probability > 0 ? Round(1.0 / probability) : null;
    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class BestConfederationTeamMarketComparisonResult
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public int SimulationIterations { get; init; }
    public double MinEdge { get; init; }
    public BestConfederationTeamMarketComparisonSummary Summary { get; init; } = new();
    public List<BestConfederationTeamMarketComparisonRow> Rows { get; init; } = [];
}

public sealed class BestConfederationTeamMarketComparisonSummary
{
    public int Rows { get; init; }
    public int ValidRows { get; init; }
    public int InvalidRows { get; init; }
    public int BetRows { get; init; }
    public int LeanRows { get; init; }
    public int NoBetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public List<BestConfederationTeamMarketTopEdge> TopEdges { get; init; } = [];
    public List<BestConfederationTeamMarketTopEdge> StrictTopEdges { get; init; } = [];
}

public sealed record BestConfederationTeamMarketComparisonRow
{
    public string Market { get; init; } = string.Empty;
    public string Confederation { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
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

public sealed class BestConfederationTeamMarketTopEdge
{
    public string Market { get; init; } = string.Empty;
    public string Confederation { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? SimulationProbability { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? EdgeProbability { get; init; }
    public string Decision { get; init; } = string.Empty;
}
