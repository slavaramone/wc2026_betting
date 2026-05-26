using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class GroupMarketOddsComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task<GroupMarketComparisonResult> CompareFromFilesAsync(
        string modelsFolder,
        string? groupStageResultsOddsFile,
        string? finishHigherOddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");

        var simulationPath = Path.Combine(modelsFolder, "simulation", "wc2026-simulation-summary.json");
        if (!File.Exists(simulationPath))
            throw new FileNotFoundException("Simulation summary not found. Run run-simulation before compare-group-markets.", simulationPath);

        var simulationJson = await File.ReadAllTextAsync(simulationPath, cancellationToken);
        var simulation = JsonSerializer.Deserialize<Wc2026SimulationResultSet>(simulationJson, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize simulation summary: {simulationPath}");

        return await CompareFromSimulationAsync(
            modelsFolder,
            simulation,
            groupStageResultsOddsFile,
            finishHigherOddsFile,
            outputFolder,
            minEdge,
            overwrite,
            cancellationToken);
    }

    public async Task<GroupMarketComparisonResult> CompareFromSimulationAsync(
        string modelsFolder,
        Wc2026SimulationResultSet simulation,
        string? groupStageResultsOddsFile,
        string? finishHigherOddsFile,
        string outputFolder,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var rows = new List<GroupMarketComparisonRow>();

        if (!string.IsNullOrWhiteSpace(groupStageResultsOddsFile))
            rows.AddRange(CompareGroupStageResults(groupStageResultsOddsFile, simulation, minEdge));

        if (!string.IsNullOrWhiteSpace(finishHigherOddsFile))
            rows.AddRange(CompareFinishHigher(finishHigherOddsFile, simulation, minEdge));

        if (rows.Count == 0)
            throw new ArgumentException("At least one odds file must be provided: --group-results-odds-file and/or --finish-higher-odds-file.");

        var result = new GroupMarketComparisonResult
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

    private static IEnumerable<GroupMarketComparisonRow> CompareGroupStageResults(string oddsFile, Wc2026SimulationResultSet simulation, double minEdge)
    {
        var rows = ReadCsv(oddsFile);
        var teamByName = simulation.Teams.ToDictionary(x => NormalizeTeam(x.Team), x => x, StringComparer.OrdinalIgnoreCase);
        var groupProbByTeam = simulation.Groups
            .SelectMany(g => g.TeamProbabilities.Select(p => (g.GroupCode, p)))
            .ToDictionary(x => NormalizeTeam(x.p.Team), x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var team = Get(row, "team");
            var marketGroup = Get(row, "group_code");
            var source = Get(row, "source_image");
            var normalized = NormalizeTeam(team);

            if (!teamByName.TryGetValue(normalized, out var teamSummary) || !groupProbByTeam.TryGetValue(normalized, out var rankSummary))
            {
                yield return InvalidRow("GroupStageResults", marketGroup, team, "Yes", string.Empty, source, $"Team not found in simulation: {team}");
                continue;
            }

            foreach (var item in GroupResultMarkets(row, teamSummary, rankSummary.p))
            {
                if (item.BookOdds is null or <= 1.0)
                    continue;

                yield return CreateRow(
                    sourceMarketType: "GroupStageResults",
                    marketGroupCode: marketGroup,
                    simulationGroupCode: teamSummary.GroupCode,
                    market: item.Market,
                    selection: team,
                    side: item.Side,
                    opponent: string.Empty,
                    bookOdds: item.BookOdds,
                    pairedBookOdds: item.PairedBookOdds,
                    simulationProbability: item.SimulationProbability,
                    sourceImage: source,
                    minEdge: minEdge);
            }
        }
    }

    private static IEnumerable<(string Market, string Side, double? BookOdds, double? PairedBookOdds, double SimulationProbability)> GroupResultMarkets(
        IReadOnlyDictionary<string, string> row,
        Wc2026SimulationTeamSummary team,
        Wc2026SimulationGroupTeamProbability rank)
    {
        yield return ("GroupWinner", "Yes", ParseDouble(Get(row, "pos1_yes_odds")), ParseDouble(Get(row, "pos1_no_odds")), team.WinGroupProbability);
        yield return ("GroupWinner", "No", ParseDouble(Get(row, "pos1_no_odds")), ParseDouble(Get(row, "pos1_yes_odds")), 1.0 - team.WinGroupProbability);

        yield return ("Top2", "Yes", ParseDouble(Get(row, "top2_yes_odds")), ParseDouble(Get(row, "top2_no_odds")), team.TopTwoProbability);
        yield return ("Top2", "No", ParseDouble(Get(row, "top2_no_odds")), ParseDouble(Get(row, "top2_yes_odds")), 1.0 - team.TopTwoProbability);

        yield return ("ExactRank2", "Yes", ParseDouble(Get(row, "pos2_yes_odds")), ParseDouble(Get(row, "pos2_no_odds")), rank.Rank2Probability);
        yield return ("ExactRank2", "No", ParseDouble(Get(row, "pos2_no_odds")), ParseDouble(Get(row, "pos2_yes_odds")), 1.0 - rank.Rank2Probability);

        yield return ("ExactRank3", "Yes", ParseDouble(Get(row, "pos3_yes_odds")), ParseDouble(Get(row, "pos3_no_odds")), rank.Rank3Probability);
        yield return ("ExactRank3", "No", ParseDouble(Get(row, "pos3_no_odds")), ParseDouble(Get(row, "pos3_yes_odds")), 1.0 - rank.Rank3Probability);

        yield return ("ExactRank4", "Yes", ParseDouble(Get(row, "pos4_yes_odds")), ParseDouble(Get(row, "pos4_no_odds")), rank.Rank4Probability);
        yield return ("ExactRank4", "No", ParseDouble(Get(row, "pos4_no_odds")), ParseDouble(Get(row, "pos4_yes_odds")), 1.0 - rank.Rank4Probability);

        yield return ("QualifyFromGroup", "Yes", ParseDouble(Get(row, "qualify_yes_odds")), ParseDouble(Get(row, "qualify_no_odds")), team.QualifiedToRoundOf32Probability);
        yield return ("QualifyFromGroup", "No", ParseDouble(Get(row, "qualify_no_odds")), ParseDouble(Get(row, "qualify_yes_odds")), 1.0 - team.QualifiedToRoundOf32Probability);
    }

    private static IEnumerable<GroupMarketComparisonRow> CompareFinishHigher(string oddsFile, Wc2026SimulationResultSet simulation, double minEdge)
    {
        var rows = ReadCsv(oddsFile);
        var teamsByName = simulation.Teams.ToDictionary(x => NormalizeTeam(x.Team), x => x, StringComparer.OrdinalIgnoreCase);
        var pairByTeams = simulation.PairComparisons
            .ToDictionary(x => PairKey(x.Team1, x.Team2), x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var marketGroup = Get(row, "group_code");
            var team1 = Get(row, "team1");
            var team2 = Get(row, "team2");
            var source = Get(row, "source_image");
            var team1Odds = ParseDouble(Get(row, "team1_finish_higher_odds"));
            var team2Odds = ParseDouble(Get(row, "team2_finish_higher_odds"));

            if (!teamsByName.TryGetValue(NormalizeTeam(team1), out var simTeam1))
            {
                yield return InvalidRow("FinishHigher", marketGroup, team1, "Team1", team2, source, $"Team not found in simulation: {team1}");
                continue;
            }
            if (!teamsByName.TryGetValue(NormalizeTeam(team2), out _))
            {
                yield return InvalidRow("FinishHigher", marketGroup, team2, "Team2", team1, source, $"Team not found in simulation: {team2}");
                continue;
            }

            if (!pairByTeams.TryGetValue(PairKey(team1, team2), out var pair))
            {
                yield return InvalidRow("FinishHigher", marketGroup, team1, "Team1", team2, source, "Pair probabilities not found. Rerun run-simulation with patched solution.");
                continue;
            }

            var team1Probability = string.Equals(NormalizeTeam(pair.Team1), NormalizeTeam(team1), StringComparison.OrdinalIgnoreCase)
                ? pair.Team1FinishHigherProbability
                : pair.Team2FinishHigherProbability;
            var team2Probability = 1.0 - team1Probability;

            if (team1Odds is > 1.0)
                yield return CreateRow("FinishHigher", marketGroup, simTeam1.GroupCode, "FinishHigher", team1, "Team1", team2, team1Odds, team2Odds, team1Probability, source, minEdge);
            if (team2Odds is > 1.0)
                yield return CreateRow("FinishHigher", marketGroup, simTeam1.GroupCode, "FinishHigher", team2, "Team2", team1, team2Odds, team1Odds, team2Probability, source, minEdge);
        }
    }


    private static string ResolveReportGroupCode(string rawMarketGroupCode, string selection, string opponent, string simulationGroupCode)
    {
        var fromSelection = TryGetMarketGroupCode(selection);
        var fromOpponent = TryGetMarketGroupCode(opponent);

        if (!string.IsNullOrWhiteSpace(fromSelection) && !string.IsNullOrWhiteSpace(fromOpponent))
        {
            if (string.Equals(fromSelection, fromOpponent, StringComparison.OrdinalIgnoreCase))
                return fromSelection;

            // This should not happen for same-group markets. Keep the source value if it exists,
            // otherwise keep the inferred simulation group and make the issue visible in Notes.
            return !string.IsNullOrWhiteSpace(rawMarketGroupCode) ? rawMarketGroupCode : simulationGroupCode;
        }

        if (!string.IsNullOrWhiteSpace(fromSelection)) return fromSelection;
        if (!string.IsNullOrWhiteSpace(fromOpponent)) return fromOpponent;
        if (!string.IsNullOrWhiteSpace(rawMarketGroupCode)) return rawMarketGroupCode;
        return simulationGroupCode;
    }

    private static string BuildGroupCodeNotes(string rawMarketGroupCode, string reportGroupCode, string simulationGroupCode)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawMarketGroupCode) &&
            !string.Equals(rawMarketGroupCode, reportGroupCode, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"Source group {rawMarketGroupCode} normalized to report group {reportGroupCode}.");
        }

        if (!string.IsNullOrWhiteSpace(simulationGroupCode) &&
            !string.Equals(simulationGroupCode, reportGroupCode, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"Model inferred group {simulationGroupCode}; report uses market group {reportGroupCode}.");
        }

        return string.Join(' ', notes);
    }

    private static string? TryGetMarketGroupCode(string team)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;
        return MarketGroupCodeByTeam.TryGetValue(NormalizeTeam(team), out var groupCode) ? groupCode : null;
    }

    private static readonly IReadOnlyDictionary<string, string> MarketGroupCodeByTeam = BuildMarketGroupCodeByTeam();

    private static IReadOnlyDictionary<string, string> BuildMarketGroupCodeByTeam()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Add(dict, "A", "Mexico", "Czechia", "South Korea", "South Africa");
        Add(dict, "B", "Switzerland", "Canada", "Bosnia & Herzegovina", "Bosnia and Herzegovina", "Qatar");
        Add(dict, "C", "Brazil", "Morocco", "Scotland", "Haiti");
        Add(dict, "D", "USA", "Türkiye", "Turkiye", "Turkey", "Paraguay", "Australia");
        Add(dict, "E", "Germany", "Ecuador", "Côte d'Ivoire", "Cote d'Ivoire", "Curacao", "Curaçao");
        Add(dict, "F", "Netherlands", "Japan", "Sweden", "Tunisia");
        Add(dict, "G", "Belgium", "Egypt", "Iran", "New Zealand");
        Add(dict, "H", "Spain", "Uruguay", "Saudi Arabia", "Cabo Verde", "Cape Verde");
        Add(dict, "I", "France", "Norway", "Senegal", "Iraq");
        Add(dict, "J", "Argentina", "Austria", "Algeria", "Jordan");
        Add(dict, "K", "Portugal", "Colombia", "DR Congo", "Congo DR", "Uzbekistan");
        Add(dict, "L", "England", "Croatia", "Ghana", "Panama");

        return dict;
    }

    private static void Add(Dictionary<string, string> dict, string groupCode, params string[] teams)
    {
        foreach (var team in teams)
            dict[NormalizeTeam(team)] = groupCode;
    }

    private static GroupMarketComparisonRow CreateRow(
        string sourceMarketType,
        string marketGroupCode,
        string simulationGroupCode,
        string market,
        string selection,
        string side,
        string opponent,
        double? bookOdds,
        double? pairedBookOdds,
        double simulationProbability,
        string sourceImage,
        double minEdge)
    {
        var reportGroupCode = ResolveReportGroupCode(marketGroupCode, selection, opponent, simulationGroupCode);
        var groupNotes = BuildGroupCodeNotes(marketGroupCode, reportGroupCode, simulationGroupCode);

        if (bookOdds is null or <= 1.0)
        {
            return InvalidRow(sourceMarketType, marketGroupCode, selection, side, opponent, sourceImage, JoinNotes("Missing or invalid book odds.", groupNotes)) with
            {
                OriginalMarketGroupCode = marketGroupCode,
                ReportGroupCode = reportGroupCode,
                MarketGroupCode = reportGroupCode,
                SimulationGroupCode = simulationGroupCode,
                Market = market,
                SimulationProbability = Round(simulationProbability),
                FairOdds = FairOdds(simulationProbability)
            };
        }

        var raw = 1.0 / bookOdds.Value;
        double? noVig = null;
        if (pairedBookOdds is > 1.0)
        {
            var pairedRaw = 1.0 / pairedBookOdds.Value;
            var sum = raw + pairedRaw;
            if (sum > 0) noVig = raw / sum;
        }

        var bookUsed = noVig ?? raw;
        var edge = simulationProbability - bookUsed;
        var fairOdds = FairOdds(simulationProbability);
        var decision = edge >= minEdge ? "BET" : edge >= minEdge / 2.0 ? "LEAN" : "NO_BET";

        return new GroupMarketComparisonRow
        {
            SourceMarketType = sourceMarketType,
            OriginalMarketGroupCode = marketGroupCode,
            ReportGroupCode = reportGroupCode,
            MarketGroupCode = reportGroupCode,
            SimulationGroupCode = simulationGroupCode,
            Market = market,
            Selection = selection,
            Side = side,
            Opponent = opponent,
            BookOdds = Round(bookOdds.Value),
            PairedBookOdds = pairedBookOdds is > 1.0 ? Round(pairedBookOdds.Value) : null,
            BookProbabilityRaw = Round(raw),
            BookProbabilityNoVig = noVig is null ? null : Round(noVig.Value),
            BookProbabilityUsed = Round(bookUsed),
            SimulationProbability = Round(simulationProbability),
            FairOdds = fairOdds,
            EdgeProbability = Round(edge),
            EdgePercent = Round(edge * 100.0),
            Decision = decision,
            SourceImage = sourceImage,
            Notes = groupNotes
        };
    }

    private static GroupMarketComparisonRow InvalidRow(string sourceMarketType, string marketGroupCode, string selection, string side, string opponent, string sourceImage, string notes)
        => new()
        {
            SourceMarketType = sourceMarketType,
            OriginalMarketGroupCode = marketGroupCode,
            ReportGroupCode = marketGroupCode,
            MarketGroupCode = marketGroupCode,
            Market = sourceMarketType,
            Selection = selection,
            Side = side,
            Opponent = opponent,
            Decision = "INVALID",
            SourceImage = sourceImage,
            Notes = notes
        };

    private static GroupMarketComparisonSummary BuildSummary(IReadOnlyList<GroupMarketComparisonRow> rows)
    {
        var valid = rows.Where(x => x.Decision != "INVALID").ToList();
        var bets = valid.Where(x => x.Decision == "BET").OrderByDescending(x => x.EdgeProbability).Take(50).ToList();
        var strictBets = valid.Where(IsStrictBet).OrderByDescending(x => x.EdgeProbability).ToList();
        return new GroupMarketComparisonSummary
        {
            Rows = rows.Count,
            ValidRows = valid.Count,
            InvalidRows = rows.Count - valid.Count,
            BetRows = valid.Count(x => x.Decision == "BET"),
            LeanRows = valid.Count(x => x.Decision == "LEAN"),
            NoBetRows = valid.Count(x => x.Decision == "NO_BET"),
            StrictBetRows = strictBets.Count,
            StrictRules = "edge >= 0.05; book odds >= 1.15; paired no-vig probability required; exclude unpaired exact-rank longshots",
            TopEdges = bets.Select(ToTopEdge).ToList(),
            StrictTopEdges = strictBets.Take(50).Select(ToTopEdge).ToList()
        };
    }

    private static GroupMarketTopEdge ToTopEdge(GroupMarketComparisonRow x)
        => new()
        {
            Market = x.Market,
            ReportGroupCode = x.ReportGroupCode,
            MarketGroupCode = x.MarketGroupCode,
            OriginalMarketGroupCode = x.OriginalMarketGroupCode,
            SimulationGroupCode = x.SimulationGroupCode,
            Selection = x.Selection,
            Side = x.Side,
            Opponent = x.Opponent,
            BookOdds = x.BookOdds,
            SimulationProbability = x.SimulationProbability,
            BookProbabilityUsed = x.BookProbabilityUsed,
            EdgeProbability = x.EdgeProbability,
            Decision = x.Decision
        };

    private static bool IsStrictBet(GroupMarketComparisonRow row)
    {
        if (!string.Equals(row.Decision, "BET", StringComparison.OrdinalIgnoreCase)) return false;
        if (row.EdgeProbability is null or < 0.05) return false;
        if (row.BookOdds is null or < 1.15) return false;
        if (row.PairedBookOdds is null or <= 1.0) return false;
        if (row.BookProbabilityNoVig is null) return false;

        // Exact-rank longshots are only allowed in the strict report when both sides exist.
        // This avoids treating one-sided/unpaired longshot prices as reliable edges.
        if (row.Market.StartsWith("ExactRank", StringComparison.OrdinalIgnoreCase) && row.PairedBookOdds is null)
            return false;

        return true;
    }

    private static async Task WriteAsync(GroupMarketComparisonResult result, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        var jsonPath = Path.Combine(outputFolder, "group-market-comparison-summary.json");
        var csvPath = Path.Combine(outputFolder, "group-market-comparison.csv");
        var strictCsvPath = Path.Combine(outputFolder, "group-market-comparison-bets-strict.csv");

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

    private static async Task WriteRowsCsvAsync(string path, IEnumerable<GroupMarketComparisonRow> rows, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("source_market_type,report_group_code,market_group_code,original_market_group_code,simulation_group_code,market,selection,side,opponent,book_odds,paired_book_odds,book_probability_raw,book_probability_no_vig,book_probability_used,simulation_probability,fair_odds,edge_probability,edge_percent,decision,source_image,notes");
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                r.SourceMarketType, r.ReportGroupCode, r.MarketGroupCode, r.OriginalMarketGroupCode, r.SimulationGroupCode, r.Market, r.Selection, r.Side, r.Opponent,
                Format(r.BookOdds), Format(r.PairedBookOdds), Format(r.BookProbabilityRaw), Format(r.BookProbabilityNoVig), Format(r.BookProbabilityUsed),
                Format(r.SimulationProbability), Format(r.FairOdds), Format(r.EdgeProbability), Format(r.EdgePercent), r.Decision, r.SourceImage, r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Odds CSV not found: {path}", path);

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

    private static string JoinNotes(params string[] notes)
        => string.Join(' ', notes.Where(x => !string.IsNullOrWhiteSpace(x)));


    private static string PairKey(string team1, string team2)
    {
        var a = NormalizeTeam(team1);
        var b = NormalizeTeam(team2);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private static double? FairOdds(double probability) => probability > 0 ? Round(1.0 / probability) : null;
    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class GroupMarketComparisonResult
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public int SimulationIterations { get; init; }
    public double MinEdge { get; init; }
    public GroupMarketComparisonSummary Summary { get; init; } = new();
    public List<GroupMarketComparisonRow> Rows { get; init; } = [];
}

public sealed class GroupMarketComparisonSummary
{
    public int Rows { get; init; }
    public int ValidRows { get; init; }
    public int InvalidRows { get; init; }
    public int BetRows { get; init; }
    public int LeanRows { get; init; }
    public int NoBetRows { get; init; }
    public int StrictBetRows { get; init; }
    public string StrictRules { get; init; } = string.Empty;
    public List<GroupMarketTopEdge> TopEdges { get; init; } = [];
    public List<GroupMarketTopEdge> StrictTopEdges { get; init; } = [];
}

public sealed record GroupMarketComparisonRow
{
    public string SourceMarketType { get; init; } = string.Empty;
    public string ReportGroupCode { get; init; } = string.Empty;
    public string MarketGroupCode { get; init; } = string.Empty;
    public string OriginalMarketGroupCode { get; init; } = string.Empty;
    public string SimulationGroupCode { get; init; } = string.Empty;
    public string Market { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string Opponent { get; init; } = string.Empty;
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

public sealed class GroupMarketTopEdge
{
    public string Market { get; init; } = string.Empty;
    public string ReportGroupCode { get; init; } = string.Empty;
    public string MarketGroupCode { get; init; } = string.Empty;
    public string OriginalMarketGroupCode { get; init; } = string.Empty;
    public string SimulationGroupCode { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public string Opponent { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? SimulationProbability { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? EdgeProbability { get; init; }
    public string Decision { get; init; } = string.Empty;
}
