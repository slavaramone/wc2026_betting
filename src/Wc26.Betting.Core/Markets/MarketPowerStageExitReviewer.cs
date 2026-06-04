using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Simulation;
using Wc26.Betting.Core.TeamRatings;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Markets;

public sealed class MarketPowerStageExitReviewer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly Wc2026SimulationWeights CurrentWeights = new(0.85, 0.12, 0.03);
    private static readonly Wc2026SimulationWeights MarketPowerOnlyWeights = new(0.0, 1.0, 0.0);

    private static readonly HashSet<string> WatchlistKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        Key("LoseInRoundOf16", "Brazil", "Yes"),
        Key("LoseInRoundOf16", "Argentina", "Yes"),
        Key("LoseInRoundOf32", "Mexico", "Yes")
    };

    public async Task<MarketPowerStageExitReviewReport> BuildAsync(
        string modelsFolder,
        string stageExitOddsFile,
        string outputFolder,
        int iterations,
        int seed,
        double minEdge,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modelsFolder))
            throw new DirectoryNotFoundException($"Models folder not found: {modelsFolder}");
        if (string.IsNullOrWhiteSpace(stageExitOddsFile))
            throw new ArgumentException("--stage-exit-odds-file is required.");
        if (iterations <= 0)
            throw new ArgumentException("--iterations must be greater than zero.");

        Directory.CreateDirectory(outputFolder);

        var groups = await ReadRequiredAsync<Wc2026GroupSet>(Path.Combine(modelsFolder, "calendar", "wc2026-groups.json"), cancellationToken);
        var calendar = await TryReadAsync<Wc2026CalendarSet>(Path.Combine(modelsFolder, "calendar", "wc2026-calendar.json"), cancellationToken);
        var odds = await ReadRequiredAsync<GameOddsSet>(Path.Combine(modelsFolder, "odds", "game-odds.json"), cancellationToken);
        var hardcodedElo = await ReadRequiredAsync<EloRatingSet>(Path.Combine(modelsFolder, "team-ratings", "hardcoded-elo-ratings.json"), cancellationToken);
        var seeds = await ReadRequiredAsync<List<NationRatingSeed>>(Path.Combine(modelsFolder, "player-ratings", "eafc26-nation-rating-seeds.json"), cancellationToken);

        var powerRatings = BuildMarketPowerRatings(groups, odds);
        var marketPowerElo = new EloRatingSet
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            AsOfDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Source = "Market-implied team power from WC2026 group 1X2 odds. Fitted as Elo-like ratings from no-vig win/loss probabilities; used as a knockout/reach-stage stress-test engine.",
            Teams = powerRatings.Select((x, index) => new EloTeamRating
            {
                Rank = index + 1,
                Team = x.Team,
                NormalizedTeam = HardcodedEloRatingsBuilder.NormalizeToEloName(x.Team),
                Rating = x.Rating
            }).ToList()
        };

        var runner = new Wc2026SimulationRunner();
        var comparer = new StageExitMarketOddsComparer();

        var currentSimulation = runner.Run(groups, odds, hardcodedElo, seeds, modelsFolder, iterations, seed, CurrentWeights, calendar);
        var currentComparison = await comparer.CompareFromSimulationAsync(
            modelsFolder,
            currentSimulation,
            stageExitOddsFile,
            Path.Combine(outputFolder, "current-85-12-3-comparison"),
            minEdge,
            overwrite,
            cancellationToken);

        var marketPowerSimulation = runner.Run(groups, odds, marketPowerElo, seeds, modelsFolder, iterations, seed, MarketPowerOnlyWeights, calendar);
        var marketPowerComparison = await comparer.CompareFromSimulationAsync(
            modelsFolder,
            marketPowerSimulation,
            stageExitOddsFile,
            Path.Combine(outputFolder, "market-power-only-comparison"),
            minEdge,
            overwrite,
            cancellationToken);

        var reviewRows = BuildReviewRows(currentComparison.Rows, marketPowerComparison.Rows)
            .OrderByDescending(x => x.IsWatchlist)
            .ThenByDescending(x => x.MarketPowerStrictBet)
            .ThenByDescending(x => x.CurrentStrictBet)
            .ThenByDescending(x => x.MinEdgeProbability)
            .ToList();

        var report = new MarketPowerStageExitReviewReport
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            ModelsFolder = modelsFolder,
            OutputFolder = outputFolder,
            Iterations = iterations,
            Seed = seed,
            MinEdge = minEdge,
            CurrentEngine = "current_85_market_12_elo_3_ea",
            MarketPowerEngine = "market_power_only_from_group_1x2_odds",
            RatingNotes = "MarketPower is fitted from no-vig group 1X2 odds as an Elo-like reusable team strength. This is mainly for knockout/stage/reach markets, not for replacing direct group-match odds.",
            RatingCount = powerRatings.Count,
            CurrentStrictBetRows = currentComparison.Summary.StrictBetRows,
            MarketPowerStrictBetRows = marketPowerComparison.Summary.StrictBetRows,
            ReviewRows = reviewRows,
            WatchlistRows = reviewRows.Where(x => x.IsWatchlist).ToList(),
            StableStrictRows = reviewRows.Where(x => x.CurrentStrictBet && x.MarketPowerStrictBet).ToList(),
            TopMarketPowerRatings = powerRatings.Take(20).ToList(),
            BottomMarketPowerRatings = powerRatings.OrderBy(x => x.Rating).Take(20).ToList()
        };

        await WriteReportAsync(report, powerRatings, outputFolder, overwrite, cancellationToken);
        return report;
    }

    private static List<MarketPowerRatingRow> BuildMarketPowerRatings(Wc2026GroupSet groups, GameOddsSet odds)
    {
        var teams = groups.Groups
            .SelectMany(g => g.Teams.Select(t => t.TeamName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ratings = teams.ToDictionary(x => x, _ => 1500.0, StringComparer.OrdinalIgnoreCase);
        var teamStats = teams.ToDictionary(x => x, _ => new MarketPowerFitStats(), StringComparer.OrdinalIgnoreCase);
        var usable = odds.Matches
            .Where(x => !string.IsNullOrWhiteSpace(x.HomeTeam)
                && !string.IsNullOrWhiteSpace(x.AwayTeam)
                && x.Odds1 is > 1.0
                && x.OddsX is > 1.0
                && x.Odds2 is > 1.0)
            .ToList();

        // Iterative Elo-like fitting from no-vig home/away win share, excluding draw.
        // The goal is not to price group matches directly, but to extract a reusable
        // market-implied team strength for hypothetical knockout matches.
        for (var pass = 0; pass < 250; pass++)
        {
            var k = 18.0 * (1.0 - (pass / 300.0));
            foreach (var match in usable)
            {
                if (!ratings.ContainsKey(match.HomeTeam) || !ratings.ContainsKey(match.AwayTeam))
                    continue;

                var p = ProbabilitiesFromOdds(match.Odds1!.Value, match.OddsX!.Value, match.Odds2!.Value);
                var targetHomeNoDraw = p.HomeWin + p.AwayWin > 0
                    ? p.HomeWin / (p.HomeWin + p.AwayWin)
                    : 0.5;
                var predictedHomeNoDraw = 1.0 / (1.0 + Math.Pow(10.0, -(ratings[match.HomeTeam] - ratings[match.AwayTeam]) / 400.0));
                var error = targetHomeNoDraw - predictedHomeNoDraw;

                ratings[match.HomeTeam] += k * error;
                ratings[match.AwayTeam] -= k * error;
            }
        }

        foreach (var match in usable)
        {
            if (!teamStats.ContainsKey(match.HomeTeam) || !teamStats.ContainsKey(match.AwayTeam))
                continue;

            var p = ProbabilitiesFromOdds(match.Odds1!.Value, match.OddsX!.Value, match.Odds2!.Value);
            var homeExpectedPoints = (3.0 * p.HomeWin) + p.Draw;
            var awayExpectedPoints = (3.0 * p.AwayWin) + p.Draw;

            teamStats[match.HomeTeam].Matches++;
            teamStats[match.HomeTeam].ExpectedPoints += homeExpectedPoints;
            teamStats[match.HomeTeam].NoVigWinProbabilitySum += p.HomeWin;
            teamStats[match.HomeTeam].NoVigDrawProbabilitySum += p.Draw;
            teamStats[match.HomeTeam].NoVigLossProbabilitySum += p.AwayWin;

            teamStats[match.AwayTeam].Matches++;
            teamStats[match.AwayTeam].ExpectedPoints += awayExpectedPoints;
            teamStats[match.AwayTeam].NoVigWinProbabilitySum += p.AwayWin;
            teamStats[match.AwayTeam].NoVigDrawProbabilitySum += p.Draw;
            teamStats[match.AwayTeam].NoVigLossProbabilitySum += p.HomeWin;
        }

        var average = ratings.Values.Average();
        var centered = ratings.ToDictionary(x => x.Key, x => x.Value - average + 1500.0, StringComparer.OrdinalIgnoreCase);

        return centered
            .Select(x =>
            {
                var stats = teamStats[x.Key];
                return new MarketPowerRatingRow
                {
                    Team = x.Key,
                    Rating = (int)Math.Round(x.Value),
                    GroupCode = FindGroupCode(groups, x.Key),
                    Matches = stats.Matches,
                    AvgExpectedPoints = Round(stats.Matches > 0 ? stats.ExpectedPoints / stats.Matches : 0),
                    AvgNoVigWinProbability = Round(stats.Matches > 0 ? stats.NoVigWinProbabilitySum / stats.Matches : 0),
                    AvgNoVigDrawProbability = Round(stats.Matches > 0 ? stats.NoVigDrawProbabilitySum / stats.Matches : 0),
                    AvgNoVigLossProbability = Round(stats.Matches > 0 ? stats.NoVigLossProbabilitySum / stats.Matches : 0)
                };
            })
            .OrderByDescending(x => x.Rating)
            .ThenBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MarketPowerStageExitReviewRow> BuildReviewRows(
        IReadOnlyList<StageExitMarketComparisonRow> currentRows,
        IReadOnlyList<StageExitMarketComparisonRow> marketPowerRows)
    {
        var marketPowerByKey = marketPowerRows
            .Where(x => !string.Equals(x.Decision, "INVALID", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<MarketPowerStageExitReviewRow>();
        foreach (var current in currentRows.Where(x => !string.Equals(x.Decision, "INVALID", StringComparison.OrdinalIgnoreCase)))
        {
            var key = Key(current);
            if (!marketPowerByKey.TryGetValue(key, out var marketPower))
                continue;

            var currentStrict = IsStrictBet(current);
            var marketPowerStrict = IsStrictBet(marketPower);
            var currentEdge = current.EdgeProbability ?? 0;
            var marketPowerEdge = marketPower.EdgeProbability ?? 0;
            var currentProbability = current.SimulationProbability ?? 0;
            var marketPowerProbability = marketPower.SimulationProbability ?? 0;

            if (!currentStrict && !marketPowerStrict && !WatchlistKeys.Contains(key))
                continue;

            rows.Add(new MarketPowerStageExitReviewRow
            {
                Market = current.Market,
                Selection = current.Selection,
                GroupCode = current.GroupCode,
                Side = current.Side,
                BookOdds = current.BookOdds,
                PairedBookOdds = current.PairedBookOdds,
                BookProbabilityUsed = current.BookProbabilityUsed,
                CurrentProbability = current.SimulationProbability,
                MarketPowerProbability = marketPower.SimulationProbability,
                ProbabilityDelta = Round(marketPowerProbability - currentProbability),
                CurrentEdgeProbability = current.EdgeProbability,
                MarketPowerEdgeProbability = marketPower.EdgeProbability,
                EdgeDelta = Round(marketPowerEdge - currentEdge),
                MinEdgeProbability = Round(Math.Min(currentEdge, marketPowerEdge)),
                CurrentDecision = current.Decision,
                MarketPowerDecision = marketPower.Decision,
                CurrentStrictBet = currentStrict,
                MarketPowerStrictBet = marketPowerStrict,
                StableStrictBet = currentStrict && marketPowerStrict,
                IsWatchlist = WatchlistKeys.Contains(key),
                Notes = WatchlistKeys.Contains(key) ? "current stage-exit watchlist" : string.Empty
            });
        }

        return rows;
    }

    private static bool IsStrictBet(StageExitMarketComparisonRow row)
    {
        if (!string.Equals(row.Decision, "BET", StringComparison.OrdinalIgnoreCase)) return false;
        if (row.EdgeProbability is null or < 0.05) return false;
        if (row.BookOdds is null or < 1.50) return false;
        if (row.PairedBookOdds is null or <= 1.0) return false;
        if (row.BookProbabilityNoVig is null) return false;
        if (!row.Side.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static OutcomeProbabilities ProbabilitiesFromOdds(double home, double draw, double away)
    {
        var ih = 1.0 / home;
        var id = 1.0 / draw;
        var ia = 1.0 / away;
        var sum = ih + id + ia;
        return new OutcomeProbabilities(ih / sum, id / sum, ia / sum);
    }

    private static string FindGroupCode(Wc2026GroupSet groups, string team)
        => groups.Groups.FirstOrDefault(g => g.Teams.Any(t => string.Equals(t.TeamName, team, StringComparison.OrdinalIgnoreCase)))?.GroupCode ?? string.Empty;

    private static string Key(StageExitMarketComparisonRow row) => Key(row.Market, row.Selection, row.Side);
    private static string Key(string market, string selection, string side) => string.Join('|', market.Trim(), selection.Trim(), string.IsNullOrWhiteSpace(side) ? "Yes" : side.Trim());

    private static async Task WriteReportAsync(
        MarketPowerStageExitReviewReport report,
        IReadOnlyList<MarketPowerRatingRow> ratings,
        string outputFolder,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        await WriteJsonAsync(Path.Combine(outputFolder, "market-power-stage-exit-review-summary.json"), report, overwrite, cancellationToken);
        await WriteRatingsCsvAsync(Path.Combine(outputFolder, "market-implied-team-power-ratings.csv"), ratings, overwrite, cancellationToken);
        await WriteReviewRowsCsvAsync(Path.Combine(outputFolder, "market-power-stage-exit-review.csv"), report.ReviewRows, overwrite, cancellationToken);
        await WriteReviewRowsCsvAsync(Path.Combine(outputFolder, "market-power-stage-exit-watchlist-review.csv"), report.WatchlistRows, overwrite, cancellationToken);
        await WriteReviewRowsCsvAsync(Path.Combine(outputFolder, "market-power-stage-exit-stable-strict-bets.csv"), report.StableStrictRows, overwrite, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
    }

    private static async Task WriteRatingsCsvAsync(string path, IEnumerable<MarketPowerRatingRow> ratings, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("rank,team,group_code,market_power_rating,matches,avg_expected_points,avg_no_vig_win_probability,avg_no_vig_draw_probability,avg_no_vig_loss_probability");
        var rank = 1;
        foreach (var r in ratings.OrderByDescending(x => x.Rating))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                rank++.ToString(CultureInfo.InvariantCulture), r.Team, r.GroupCode, r.Rating.ToString(CultureInfo.InvariantCulture), r.Matches.ToString(CultureInfo.InvariantCulture),
                Format(r.AvgExpectedPoints), Format(r.AvgNoVigWinProbability), Format(r.AvgNoVigDrawProbability), Format(r.AvgNoVigLossProbability)
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteReviewRowsCsvAsync(string path, IEnumerable<MarketPowerStageExitReviewRow> rows, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("market,selection,group_code,side,book_odds,paired_book_odds,book_probability_used,current_probability,market_power_probability,probability_delta,current_edge_probability,market_power_edge_probability,edge_delta,min_edge_probability,current_decision,market_power_decision,current_strict_bet,market_power_strict_bet,stable_strict_bet,is_watchlist,notes");
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[]
            {
                r.Market, r.Selection, r.GroupCode, r.Side,
                Format(r.BookOdds), Format(r.PairedBookOdds), Format(r.BookProbabilityUsed),
                Format(r.CurrentProbability), Format(r.MarketPowerProbability), Format(r.ProbabilityDelta),
                Format(r.CurrentEdgeProbability), Format(r.MarketPowerEdgeProbability), Format(r.EdgeDelta), Format(r.MinEdgeProbability),
                r.CurrentDecision, r.MarketPowerDecision,
                r.CurrentStrictBet.ToString(), r.MarketPowerStrictBet.ToString(), r.StableStrictBet.ToString(), r.IsWatchlist.ToString(), r.Notes
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
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

    private static double Round(double value) => Math.Round(value, 6);
    private static string Format(double? value) => value is null ? string.Empty : value.Value.ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record OutcomeProbabilities(double HomeWin, double Draw, double AwayWin);

    private sealed class MarketPowerFitStats
    {
        public int Matches { get; set; }
        public double ExpectedPoints { get; set; }
        public double NoVigWinProbabilitySum { get; set; }
        public double NoVigDrawProbabilitySum { get; set; }
        public double NoVigLossProbabilitySum { get; set; }
    }
}

public sealed class MarketPowerStageExitReviewReport
{
    public DateTimeOffset BuiltAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public string OutputFolder { get; init; } = string.Empty;
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public double MinEdge { get; init; }
    public string CurrentEngine { get; init; } = string.Empty;
    public string MarketPowerEngine { get; init; } = string.Empty;
    public string RatingNotes { get; init; } = string.Empty;
    public int RatingCount { get; init; }
    public int CurrentStrictBetRows { get; init; }
    public int MarketPowerStrictBetRows { get; init; }
    public List<MarketPowerStageExitReviewRow> ReviewRows { get; init; } = [];
    public List<MarketPowerStageExitReviewRow> WatchlistRows { get; init; } = [];
    public List<MarketPowerStageExitReviewRow> StableStrictRows { get; init; } = [];
    public List<MarketPowerRatingRow> TopMarketPowerRatings { get; init; } = [];
    public List<MarketPowerRatingRow> BottomMarketPowerRatings { get; init; } = [];
}

public sealed record MarketPowerRatingRow
{
    public string Team { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public int Rating { get; init; }
    public int Matches { get; init; }
    public double AvgExpectedPoints { get; init; }
    public double AvgNoVigWinProbability { get; init; }
    public double AvgNoVigDrawProbability { get; init; }
    public double AvgNoVigLossProbability { get; init; }
}

public sealed record MarketPowerStageExitReviewRow
{
    public string Market { get; init; } = string.Empty;
    public string Selection { get; init; } = string.Empty;
    public string GroupCode { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
    public double? BookOdds { get; init; }
    public double? PairedBookOdds { get; init; }
    public double? BookProbabilityUsed { get; init; }
    public double? CurrentProbability { get; init; }
    public double? MarketPowerProbability { get; init; }
    public double? ProbabilityDelta { get; init; }
    public double? CurrentEdgeProbability { get; init; }
    public double? MarketPowerEdgeProbability { get; init; }
    public double? EdgeDelta { get; init; }
    public double? MinEdgeProbability { get; init; }
    public string CurrentDecision { get; init; } = string.Empty;
    public string MarketPowerDecision { get; init; } = string.Empty;
    public bool CurrentStrictBet { get; init; }
    public bool MarketPowerStrictBet { get; init; }
    public bool StableStrictBet { get; init; }
    public bool IsWatchlist { get; init; }
    public string Notes { get; init; } = string.Empty;
}
