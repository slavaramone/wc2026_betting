using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class StageReachMarketOddsComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<StageReachMarketComparisonResult> CompareFromFilesAsync(
        string modelsFolder,
        string stageReachOddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");

        if (string.IsNullOrWhiteSpace(stageReachOddsFile))
            throw new ArgumentException("--stage-reach-odds-file is required.");

        var simulationPath = Path.Combine(modelsFolder, "simulation", "wc2026-simulation-summary.json");
        if (!File.Exists(simulationPath))
            throw new FileNotFoundException("Simulation summary not found. Run run-simulation before compare-stage-reach-markets.", simulationPath);

        var simulationJson = await File.ReadAllTextAsync(simulationPath, cancellationToken);
        var simulation = JsonSerializer.Deserialize<Wc2026SimulationResultSet>(simulationJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize simulation summary: {simulationPath}");

        return await CompareFromSimulationAsync(modelsFolder, simulation, stageReachOddsFile, outputFolder, minEdge, overwrite, cancellationToken);
    }

    public async Task<StageReachMarketComparisonResult> CompareFromSimulationAsync(
        string modelsFolder,
        Wc2026SimulationResultSet simulation,
        string stageReachOddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var rows = CompareStageReach(stageReachOddsFile, simulation, minEdge).ToList();

        var result = new StageReachMarketComparisonResult
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

    private static IEnumerable<StageReachMarketComparisonRow> CompareStageReach(string oddsFile, Wc2026SimulationResultSet simulation, double minEdge)
    {
        var rawRows = ReadCsv(oddsFile);
        var teamsByName = simulation.Teams.ToDictionary(x => NormalizeTeam(x.Team), x => x, StringComparer.OrdinalIgnoreCase);
        var oddsByKey = rawRows
            .Select(x => new
            {
                Market = Get(x, "market"),
                Selection = Get(x, "selection"),
                Side = NormalizeSide(Get(x, "side")),
                Odds = ParseDouble(Get(x, "odds"))
            })
            .Where(x => x.Odds is > 1.0)
            .GroupBy(x => Key(x.Market, x.Selection, x.Side), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Odds, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rawRows)
        {
            var market = Get(row, "market");
            var selection = Get(row, "selection");
            var side = NormalizeSide(Get(row, "side"));
            var source = Get(row, "source_image");
            var bookOdds = ParseDouble(Get(row, "odds"));
            var pairedBookOdds = ParseDouble(Get(row, "paired_odds"));

            if (bookOdds is null or <= 1.0)
                continue;

            pairedBookOdds ??= oddsByKey.TryGetValue(Key(market, selection, OppositeSide(side)), out var paired) ? paired : null;

            if (!teamsByName.TryGetValue(NormalizeTeam(selection), out var team))
            {
                yield return InvalidRow(market, selection, side, bookOdds, pairedBookOdds, source, $"Team not found in simulation: {selection}");
                continue;
            }

            var yesProbability = GetReachProbability(team, market);
            if (yesProbability is null)
            {
                yield return InvalidRow(market, selection, side, bookOdds, pairedBookOdds, source, $"Unsupported stage-reach market: {market}");
                continue;
            }

            var simulationProbability = side.Equals("No", StringComparison.OrdinalIgnoreCase)
                ? 1.0 - yesProbability.Value
                : yesProbability.Value;

            yield return CreateRow(
                market: market,
                selection: selection,
                groupCode: team.GroupCode,
                side: side,
                bookOdds: bookOdds,
                pairedBookOdds: pairedBookOdds,
                simulationProbability: simulationProbability,
                sourceImage: source,
                minEdge: minEdge,
                notes: string.Empty);
        }
    }

    private static double? GetReachProbability(Wc2026SimulationTeamSummary team, string market)
    {
        return market switch
        {
            "Winner" => Clamp(team.WinnerProbability),
            "ReachFinal" => Clamp(team.ReachFinalProbability),
            "ReachSemiFinal" => Clamp(team.ReachSemiFinalProbability),
            "ReachQuarterFinal" => Clamp(team.ReachQuarterFinalProbability),
            "ReachRoundOf16" => Clamp(team.ReachRoundOf16Probability),
            "ReachRoundOf32" => Clamp(team.QualifiedToRoundOf32Probability),
            _ => null
        };
    }

    private static StageReachMarketComparisonRow CreateRow(
        string market,
        string selection,
        string groupCode,
        string side,
        double? bookOdds,
        double? pairedBookOdds,
        double? simulationProbability,
        string sourceImage,
        double minEdge,
        string notes)
    {
        var rawProbability = bookOdds is > 1.0 ? Round(1.0 / bookOdds.Value) : (double?)null;
        double? noVigProbability = null;
        if (bookOdds is > 1.0 && pairedBookOdds is > 1.0)
        {
            var p = 1.0 / bookOdds.Value;
            var q = 1.0 / pairedBookOdds.Value;
            noVigProbability = Round(p / (p + q));
        }

        var bookProbabilityUsed = noVigProbability ?? rawProbability;
        var edge = simulationProbability is not null && bookProbabilityUsed is not null
            ? Round(simulationProbability.Value - bookProbabilityUsed.Value)
            : (double?)null;

        var decision = edge is null
            ? "INVALID"
            : edge.Value >= minEdge
                ? "BET"
                : edge.Value >= minEdge / 2.0
                    ? "LEAN"
                    : "NO_BET";

        return new StageReachMarketComparisonRow
        {
            Market = market,
            Selection = selection,
            GroupCode = groupCode,
            Side = side,
            BookOdds = bookOdds,
            PairedBookOdds = pairedBookOdds,
            BookProbabilityRaw = rawProbability,
            BookProbabilityNoVig = noVigProbability,
            BookProbabilityUsed = bookProbabilityUsed,
            SimulationProbability = simulationProbability is null ? null : Round(simulationProbability.Value),
            FairOdds = simulationProbability is null ? null : FairOdds(simulationProbability.Value),
            EdgeProbability = edge,
            EdgePercent = edge is null ? null : Round(edge.Value * 100.0),
            Decision = decision,
            SourceImage = sourceImage,
            Notes = notes
        };
    }

    private static StageReachMarketComparisonRow InvalidRow(string market, string selection, string side, double? bookOdds, double? pairedBookOdds, string sourceImage, string notes)
        => new()
        {
            Market = market,
            Selection = selection,
            Side = string.IsNullOrWhiteSpace(side) ? "Yes" : side,
            BookOdds = bookOdds,
            PairedBookOdds = pairedBookOdds,
            Decision = "INVALID",
            SourceImage = sourceImage,
            Notes = notes
        };

    private static StageReachMarketComparisonSummary BuildSummary(IReadOnlyList<StageReachMarketComparisonRow> rows)
    {
        var valid = rows.Where(x => x.Decision != "INVALID").ToList();
        var strict = rows.Where(IsStrictBet).OrderByDescending(x => x.EdgeProbability).Take(50).Select(ToTopEdge).ToList();

        return new StageReachMarketComparisonSummary
        {
            Rows = rows.Count,
            ValidRows = valid.Count,
            InvalidRows = rows.Count - valid.Count,
            BetRows = rows.Count(x => x.Decision == "BET"),
            LeanRows = rows.Count(x => x.Decision == "LEAN"),
            NoBetRows = rows.Count(x => x.Decision == "NO_BET"),
            StrictBetRows = rows.Count(IsStrictBet),
            StrictRules = "edge >= 0.05; book odds >= 1.50; paired no-vig probability required; stage-reach YES only",
            TopEdges = rows
                .Where(x => x.Decision is "BET" or "LEAN")
                .OrderByDescending(x => x.EdgeProbability)
                .Take(50)
                .Select(ToTopEdge)
                .ToList(),
            StrictTopEdges = strict
        };
    }

    private static bool IsStrictBet(StageReachMarketComparisonRow row)
    {
        if (row.Decision != "BET") return false;
        if (row.EdgeProbability is null or < 0.05) return false;
        if (row.BookOdds is null or < 1.50) return false;
        if (row.PairedBookOdds is null or <= 1.0) return false;
        if (row.BookProbabilityNoVig is null) return false;
        if (!row.Side.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static StageReachMarketTopEdge ToTopEdge(StageReachMarketComparisonRow row)
        => new()
        {
            Market = row.Market,
            Selection = row.Selection,
            GroupCode = row.GroupCode,
            Side = row.Side,
            BookOdds = row.BookOdds,
            SimulationProbability = row.SimulationProbability,
            BookProbabilityUsed = row.BookProbabilityUsed,
            EdgeProbability = row.EdgeProbability,
            Decision = row.Decision
        };

    private static async Task WriteAsync(StageReachMarketComparisonResult result, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        var jsonPath = Path.Combine(outputFolder, "stage-reach-market-comparison-summary.json");
        var csvPath = Path.Combine(outputFolder, "stage-reach-market-comparison.csv");
        var strictCsvPath = Path.Combine(outputFolder, "stage-reach-market-comparison-bets-strict.csv");

        if (File.Exists(jsonPath) && !overwrite) throw new IOException($"File already exists: {jsonPath}. Use --overwrite.");
        if (File.Exists(csvPath) && !overwrite) throw new IOException($"File already exists: {csvPath}. Use --overwrite.");
        if (File.Exists(strictCsvPath) && !overwrite) throw new IOException($"File already exists: {strictCsvPath}. Use --overwrite.");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, JsonOptions), cancellationToken);

        await WriteRowsCsvAsync(csvPath, result.Rows
            .OrderByDescending(x => x.Decision == "BET")
            .ThenByDescending(x => x.EdgeProbability), cancellationToken);

        await WriteRowsCsvAsync(strictCsvPath, result.Rows
            .Where(IsStrictBet)
            .OrderByDescending(x => x.EdgeProbability), cancellationToken);
    }

    private static async Task WriteRowsCsvAsync(string path, IEnumerable<StageReachMarketComparisonRow> rows, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("market,selection,group_code,side,book_odds,paired_book_odds,book_probability_raw,book_probability_no_vig,book_probability_used,simulation_probability,fair_odds,edge_probability,edge_percent,decision,source_image,notes");
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                r.Market, r.Selection, r.GroupCode, r.Side,
                Format(r.BookOdds), Format(r.PairedBookOdds), Format(r.BookProbabilityRaw), Format(r.BookProbabilityNoVig), Format(r.BookProbabilityUsed),
                Format(r.SimulationProbability), Format(r.FairOdds), Format(r.EdgeProbability), Format(r.EdgePercent), r.Decision, r.SourceImage, r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Stage-reach odds CSV not found: {path}", path);

        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return [];

        var headers = SimpleCsv.ParseLine(lines[0]).Select(h => h.Trim().TrimStart('\ufeff')).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var cells = SimpleCsv.ParseLine(line);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
                dict[headers[i]] = i < cells.Count ? cells[i].Trim() : string.Empty;
            rows.Add(dict);
        }
        return rows;
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static double? ParseDouble(string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value) || value == "-") return null;
        value = value.Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string NormalizeTeam(string value)
    {
        value = value.Trim()
            .Replace("&", "and", StringComparison.OrdinalIgnoreCase)
            .Replace("’", "'", StringComparison.OrdinalIgnoreCase);

        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray();

        return new string(chars).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string NormalizeSide(string value)
        => value.Equals("No", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes";

    private static string OppositeSide(string side)
        => side.Equals("No", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";

    private static string Key(string market, string selection, string side)
        => $"{market.Trim()}|{NormalizeTeam(selection)}|{NormalizeSide(side)}";

    private static double Clamp(double value)
    {
        if (value < 0) return 0;
        if (value > 1) return 1;
        return value;
    }

    private static double? FairOdds(double probability) => probability > 0 ? Round(1.0 / probability) : null;
    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class StageReachMarketComparisonResult
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public int SimulationIterations { get; init; }
    public double MinEdge { get; init; }
    public StageReachMarketComparisonSummary Summary { get; init; } = new();
    public List<StageReachMarketComparisonRow> Rows { get; init; } = [];
}

public sealed class StageReachMarketComparisonSummary
{
    public int Rows { get; init; }
    public int ValidRows { get; init; }
    public int InvalidRows { get; init; }
    public int BetRows { get; init; }
    public int LeanRows { get; init; }
    public int NoBetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public List<StageReachMarketTopEdge> TopEdges { get; init; } = [];
    public List<StageReachMarketTopEdge> StrictTopEdges { get; init; } = [];
}

public sealed record StageReachMarketComparisonRow
{
    public string Market { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
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

public sealed class StageReachMarketTopEdge
{
    public string Market { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? SimulationProbability { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? EdgeProbability { get; init; }
    public string Decision { get; init; } = string.Empty;
}
