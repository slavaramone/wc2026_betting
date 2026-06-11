using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Odds;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.ScoreModel.MarketXg;

public sealed class MarketImpliedXgBuilder
{
    private const double MinLambda = 0.05d;
    private const double MaxLambda = 8.00d;
    private const double MinShare = 0.03d;
    private const double MaxShare = 0.97d;

    private static readonly IReadOnlyDictionary<string, string> GroupCodeByTeam = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Mexico"] = "A", ["South Korea"] = "A", ["Czechia"] = "A", ["South Africa"] = "A",
        ["Canada"] = "B", ["Bosnia & Herzegovina"] = "B", ["Qatar"] = "B", ["Switzerland"] = "B",
        ["United States"] = "C", ["Paraguay"] = "C", ["Australia"] = "C", ["Turkey"] = "C",
        ["Brazil"] = "D", ["Morocco"] = "D", ["Haiti"] = "D", ["Scotland"] = "D",
        ["Germany"] = "E", ["Curaçao"] = "E", ["Côte d'Ivoire"] = "E", ["Ecuador"] = "E",
        ["Netherlands"] = "F", ["Japan"] = "F", ["Sweden"] = "F", ["Tunisia"] = "F",
        ["Spain"] = "G", ["Cabo Verde"] = "G", ["Saudi Arabia"] = "G", ["Uruguay"] = "G",
        ["Belgium"] = "H", ["Egypt"] = "H", ["Iran"] = "H", ["New Zealand"] = "H",
        ["France"] = "I", ["Senegal"] = "I", ["Iraq"] = "I", ["Norway"] = "I",
        ["Argentina"] = "J", ["Algeria"] = "J", ["Austria"] = "J", ["Jordan"] = "J",
        ["Portugal"] = "K", ["DR Congo"] = "K", ["Uzbekistan"] = "K", ["Colombia"] = "K",
        ["England"] = "L", ["Croatia"] = "L", ["Ghana"] = "L", ["Panama"] = "L"
    };

    public MarketImpliedXgSet BuildFromOddsFile(string oddsFile, int maxGoals = 12)
    {
        var importer = new GameOddsImporter();
        var odds = importer.Import(oddsFile);
        return Build(odds, maxGoals);
    }

    public MarketImpliedXgSet Build(GameOddsSet odds, int maxGoals = 12)
    {
        if (maxGoals < 6)
            throw new ArgumentOutOfRangeException(nameof(maxGoals), "Use at least 6 goals for score-grid calculation.");
        if (maxGoals > 20)
            throw new ArgumentOutOfRangeException(nameof(maxGoals), "Use at most 20 goals to avoid unnecessary runtime.");

        var fixtures = new List<MarketImpliedXgFixture>();
        var warnings = new List<string>();

        foreach (var match in odds.Matches)
        {
            try
            {
                fixtures.Add(BuildFixture(match, maxGoals));
            }
            catch (Exception ex)
            {
                var warning = $"{match.HomeTeam} - {match.AwayTeam}: {ex.Message}";
                warnings.Add(warning);
                fixtures.Add(BuildInvalidFixture(match, warning));
            }
        }

        var groupDiagnostics = BuildGroupDiagnostics(fixtures);
        var validationErrors = ValidateFixtures(fixtures, groupDiagnostics);
        warnings.AddRange(validationErrors);

        return new MarketImpliedXgSet
        {
            SourceOddsFile = odds.SourceFile,
            RowCount = odds.RowCount,
            ValidFixtureCount = fixtures.Count(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            InvalidFixtureCount = fixtures.Count(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
            MaxGoalsUsed = maxGoals,
            Fixtures = fixtures,
            GroupDiagnostics = groupDiagnostics,
            Warnings = warnings,
            ValidationErrors = validationErrors
        };
    }

    public async Task<MarketImpliedXgSet> BuildAndWriteAsync(
        string oddsFile,
        string outputFolder,
        int maxGoals,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = BuildFromOddsFile(oddsFile, maxGoals);
        Directory.CreateDirectory(outputFolder);

        await WriteJsonAsync(Path.Combine(outputFolder, "wc26-fixture-market-xg.json"), set, overwrite, cancellationToken);
        await WriteCsvAsync(Path.Combine(outputFolder, "wc26-fixture-market-xg.csv"), set, overwrite, cancellationToken);
        await WriteDiagnosticsCsvAsync(Path.Combine(outputFolder, "wc26-fixture-market-xg-diagnostics.csv"), set, overwrite, cancellationToken);
        await WriteGroupDiagnosticsCsvAsync(Path.Combine(outputFolder, "wc26-fixture-market-xg-group-diagnostics.csv"), set, overwrite, cancellationToken);

        return set;
    }

    private static MarketImpliedXgFixture BuildFixture(GameOddsMatch match, int maxGoals)
    {
        var odds1 = RequirePositive(match.Odds1, nameof(match.Odds1));
        var oddsX = RequirePositive(match.OddsX, nameof(match.OddsX));
        var odds2 = RequirePositive(match.Odds2, nameof(match.Odds2));
        var totalLine = RequireNonNegative(match.TotalLine, nameof(match.TotalLine));
        var overOdds = RequirePositive(match.OverOdds, nameof(match.OverOdds));
        var underOdds = RequirePositive(match.UnderOdds, nameof(match.UnderOdds));

        var rawP1 = 1.0d / odds1;
        var rawPX = 1.0d / oddsX;
        var rawP2 = 1.0d / odds2;
        var sum1X2 = rawP1 + rawPX + rawP2;
        var noVigP1 = rawP1 / sum1X2;
        var noVigPX = rawPX / sum1X2;
        var noVigP2 = rawP2 / sum1X2;

        var rawPOver = 1.0d / overOdds;
        var rawPUnder = 1.0d / underOdds;
        var sumTotal = rawPOver + rawPUnder;
        var noVigPOver = rawPOver / sumTotal;
        var noVigPUnder = rawPUnder / sumTotal;

        var totalLambda = SolveTotalLambda(totalLine, noVigPOver);
        var split = SolveTeamSplit(totalLambda, totalLine, noVigP1, noVigPX, noVigP2, noVigPOver, maxGoals);
        var group = ResolveGroupCode(match);

        return new MarketImpliedXgFixture
        {
            MatchKey = match.MatchKey,
            MatchDate = match.MatchDate,
            MatchTime = match.MatchTime,
            GroupCode = group.GroupCode,
            SourceGroupCode = match.SourceGroupCode,
            GroupSource = group.Source,
            MatchStatus = match.MatchStatus,
            CalendarEventId = match.CalendarEventId,
            TeamA = match.HomeTeam,
            TeamB = match.AwayTeam,
            NormalizedTeamA = match.NormalizedHomeTeam,
            NormalizedTeamB = match.NormalizedAwayTeam,
            Odds1 = odds1,
            OddsX = oddsX,
            Odds2 = odds2,
            RawP1 = rawP1,
            RawPX = rawPX,
            RawP2 = rawP2,
            Overround1X2 = sum1X2,
            NoVigP1 = noVigP1,
            NoVigPX = noVigPX,
            NoVigP2 = noVigP2,
            TotalLine = totalLine,
            OverOdds = overOdds,
            UnderOdds = underOdds,
            RawPOver = rawPOver,
            RawPUnder = rawPUnder,
            TotalOverround = sumTotal,
            NoVigPOver = noVigPOver,
            NoVigPUnder = noVigPUnder,
            TotalLambda = totalLambda,
            TeamAShare = split.ShareA,
            TeamAXg = split.LambdaA,
            TeamBXg = split.LambdaB,
            ModelP1 = split.ModelP1,
            ModelPX = split.ModelPX,
            ModelP2 = split.ModelP2,
            ModelPOver = split.ModelPOver,
            ModelPUnder = 1.0d - split.ModelPOver,
            ErrorP1 = split.ModelP1 - noVigP1,
            ErrorPX = split.ModelPX - noVigPX,
            ErrorP2 = split.ModelP2 - noVigP2,
            ErrorOver = split.ModelPOver - noVigPOver,
            ObjectiveError = split.ObjectiveError,
            Status = "valid"
        };
    }

    private static MarketImpliedXgFixture BuildInvalidFixture(GameOddsMatch match, string warning)
    {
        var group = ResolveGroupCode(match);
        return new MarketImpliedXgFixture
        {
            MatchKey = match.MatchKey,
            MatchDate = match.MatchDate,
            MatchTime = match.MatchTime,
            GroupCode = group.GroupCode,
            SourceGroupCode = match.SourceGroupCode,
            GroupSource = group.Source,
            MatchStatus = match.MatchStatus,
            CalendarEventId = match.CalendarEventId,
            TeamA = match.HomeTeam,
            TeamB = match.AwayTeam,
            NormalizedTeamA = match.NormalizedHomeTeam,
            NormalizedTeamB = match.NormalizedAwayTeam,
            Status = "invalid",
            Warning = warning
        };
    }

    private static SplitResult SolveTeamSplit(
        double totalLambda,
        double totalLine,
        double targetP1,
        double targetPX,
        double targetP2,
        double targetPOver,
        int maxGoals)
    {
        SplitResult? best = null;

        for (var i = (int)Math.Round(MinShare * 1000.0d); i <= (int)Math.Round(MaxShare * 1000.0d); i++)
        {
            var shareA = i / 1000.0d;
            var lambdaA = totalLambda * shareA;
            var lambdaB = totalLambda - lambdaA;
            var probs = CalculateMatchProbabilities(lambdaA, lambdaB, totalLine, maxGoals);

            // The total target is already matched by totalLambda. Keep a tiny total term as a diagnostic stabilizer only.
            var e1 = probs.P1 - targetP1;
            var ex = probs.PX - targetPX;
            var e2 = probs.P2 - targetP2;
            var eo = probs.POver - targetPOver;
            var objective = (e1 * e1) + (ex * ex) + (e2 * e2) + (0.05d * eo * eo);

            if (best is null || objective < best.ObjectiveError)
            {
                best = new SplitResult(
                    shareA,
                    lambdaA,
                    lambdaB,
                    probs.P1,
                    probs.PX,
                    probs.P2,
                    probs.POver,
                    objective);
            }
        }

        return best ?? throw new InvalidOperationException("Could not solve team xG split.");
    }

    private static MatchProbs CalculateMatchProbabilities(double lambdaA, double lambdaB, double totalLine, int maxGoals)
    {
        var pA = BuildPoissonProbabilities(lambdaA, maxGoals);
        var pB = BuildPoissonProbabilities(lambdaB, maxGoals);

        double p1 = 0;
        double px = 0;
        double p2 = 0;
        double pOver = 0;
        double mass = 0;

        for (var a = 0; a <= maxGoals; a++)
        {
            for (var b = 0; b <= maxGoals; b++)
            {
                var p = pA[a] * pB[b];
                mass += p;

                if (a > b)
                    p1 += p;
                else if (a == b)
                    px += p;
                else
                    p2 += p;

                if (a + b > totalLine)
                    pOver += p;
            }
        }

        if (mass <= 0)
            throw new InvalidOperationException("Score grid produced zero probability mass.");

        return new MatchProbs(p1 / mass, px / mass, p2 / mass, pOver / mass);
    }

    private static double SolveTotalLambda(double totalLine, double targetPOver)
    {
        if (targetPOver <= 0 || targetPOver >= 1)
            throw new ArgumentOutOfRangeException(nameof(targetPOver), "No-vig over probability must be between 0 and 1.");

        var low = MinLambda;
        var high = MaxLambda;

        while (PoissonOverProbability(high, totalLine) < targetPOver && high < 20.0d)
            high *= 1.5d;

        for (var i = 0; i < 90; i++)
        {
            var mid = (low + high) / 2.0d;
            var p = PoissonOverProbability(mid, totalLine);
            if (p < targetPOver)
                low = mid;
            else
                high = mid;
        }

        return (low + high) / 2.0d;
    }

    private static double PoissonOverProbability(double lambda, double totalLine)
    {
        var maxUnderGoals = (int)Math.Floor(totalLine);
        var pUnderOrEqual = 0.0d;
        var p = Math.Exp(-lambda);
        pUnderOrEqual += p;

        for (var k = 1; k <= maxUnderGoals; k++)
        {
            p *= lambda / k;
            pUnderOrEqual += p;
        }

        return Math.Clamp(1.0d - pUnderOrEqual, 0.0d, 1.0d);
    }

    private static double[] BuildPoissonProbabilities(double lambda, int maxGoals)
    {
        var result = new double[maxGoals + 1];
        result[0] = Math.Exp(-lambda);
        for (var i = 1; i <= maxGoals; i++)
            result[i] = result[i - 1] * lambda / i;
        return result;
    }

    private static GroupResolveResult ResolveGroupCode(GameOddsMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.SourceGroupCode))
            return new GroupResolveResult(match.SourceGroupCode.Trim().ToUpperInvariant(), "input-csv");

        if (!string.IsNullOrWhiteSpace(match.CalendarGroupCode))
            return new GroupResolveResult(match.CalendarGroupCode.Trim().ToUpperInvariant(), "calendar");

        var teamA = GameOddsImporter.NormalizeTeamName(match.NormalizedHomeTeam);
        var teamB = GameOddsImporter.NormalizeTeamName(match.NormalizedAwayTeam);

        if (GroupCodeByTeam.TryGetValue(teamA, out var groupA) &&
            GroupCodeByTeam.TryGetValue(teamB, out var groupB) &&
            string.Equals(groupA, groupB, StringComparison.OrdinalIgnoreCase))
        {
            return new GroupResolveResult(groupA, "team-map");
        }

        return new GroupResolveResult(string.Empty, "missing");
    }

    private static double RequirePositive(double? value, string name)
    {
        if (!value.HasValue || value.Value <= 1.0d)
            throw new InvalidOperationException($"{name} is missing or not a valid decimal odds value.");
        return value.Value;
    }

    private static double RequireNonNegative(double? value, string name)
    {
        if (!value.HasValue || value.Value < 0.0d)
            throw new InvalidOperationException($"{name} is missing or negative.");
        return value.Value;
    }

    private static async Task WriteJsonAsync(string path, MarketImpliedXgSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");

        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task WriteCsvAsync(string path, MarketImpliedXgSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync(string.Join(',', new[]
        {
            "MatchDate", "MatchTime", "Group", "GroupSource", "TeamA", "TeamB", "TeamAXg", "TeamBXg", "TotalLambda", "TeamAShare",
            "Odds1", "OddsX", "Odds2", "NoVigP1", "NoVigPX", "NoVigP2", "ModelP1", "ModelPX", "ModelP2",
            "TotalLine", "OverOdds", "UnderOdds", "NoVigPOver", "ModelPOver", "ErrorP1", "ErrorPX", "ErrorP2", "ErrorOver", "ObjectiveError", "Status", "Warning"
        }));

        foreach (var f in set.Fixtures)
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(f.MatchDate), Csv(f.MatchTime), Csv(f.GroupCode), Csv(f.GroupSource), Csv(f.TeamA), Csv(f.TeamB),
                D(f.TeamAXg), D(f.TeamBXg), D(f.TotalLambda), D(f.TeamAShare),
                D(f.Odds1), D(f.OddsX), D(f.Odds2), D(f.NoVigP1), D(f.NoVigPX), D(f.NoVigP2), D(f.ModelP1), D(f.ModelPX), D(f.ModelP2),
                D(f.TotalLine), D(f.OverOdds), D(f.UnderOdds), D(f.NoVigPOver), D(f.ModelPOver), D(f.ErrorP1), D(f.ErrorPX), D(f.ErrorP2), D(f.ErrorOver), D(f.ObjectiveError), Csv(f.Status), Csv(f.Warning)
            }));
        }
    }

    private static async Task WriteDiagnosticsCsvAsync(string path, MarketImpliedXgSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,GroupSource,TeamA,TeamB,NoVigP1,ModelP1,ErrorP1,NoVigPX,ModelPX,ErrorPX,NoVigP2,ModelP2,ErrorP2,NoVigPOver,ModelPOver,ErrorOver,TotalLambda,TeamAXg,TeamBXg,ObjectiveError,Status,Warning");

        foreach (var f in set.Fixtures.OrderByDescending(x => Math.Abs(x.ErrorP1) + Math.Abs(x.ErrorPX) + Math.Abs(x.ErrorP2)))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(f.GroupCode), Csv(f.GroupSource), Csv(f.TeamA), Csv(f.TeamB),
                D(f.NoVigP1), D(f.ModelP1), D(f.ErrorP1),
                D(f.NoVigPX), D(f.ModelPX), D(f.ErrorPX),
                D(f.NoVigP2), D(f.ModelP2), D(f.ErrorP2),
                D(f.NoVigPOver), D(f.ModelPOver), D(f.ErrorOver),
                D(f.TotalLambda), D(f.TeamAXg), D(f.TeamBXg), D(f.ObjectiveError), Csv(f.Status), Csv(f.Warning)
            }));
        }
    }


    private static List<MarketImpliedXgGroupDiagnostic> BuildGroupDiagnostics(IReadOnlyList<MarketImpliedXgFixture> fixtures)
    {
        return fixtures
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GroupCode) ? "<missing>" : x.GroupCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var valid = g.Where(x => string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)).ToList();
                var teams = g.SelectMany(x => new[] { x.NormalizedTeamA, x.NormalizedTeamB })
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var warnings = new List<string>();
                if (g.Key == "<missing>")
                    warnings.Add("group is missing");
                if (g.Count() != 6)
                    warnings.Add($"expected 6 fixtures, got {g.Count()}");
                if (teams.Count != 4)
                    warnings.Add($"expected 4 unique teams, got {teams.Count}");

                return new MarketImpliedXgGroupDiagnostic
                {
                    GroupCode = g.Key == "<missing>" ? string.Empty : g.Key,
                    FixtureCount = g.Count(),
                    ValidFixtureCount = valid.Count,
                    InvalidFixtureCount = g.Count(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)),
                    UniqueTeamCount = teams.Count,
                    Teams = string.Join(" | ", teams),
                    AverageTotalLambda = valid.Count == 0 ? 0.0d : valid.Average(x => x.TotalLambda),
                    SumTotalLambda = valid.Sum(x => x.TotalLambda),
                    Status = warnings.Count == 0 ? "valid" : "invalid",
                    Warning = string.Join("; ", warnings)
                };
            })
            .ToList();
    }

    private static List<string> ValidateFixtures(IReadOnlyList<MarketImpliedXgFixture> fixtures, IReadOnlyList<MarketImpliedXgGroupDiagnostic> groupDiagnostics)
    {
        var errors = new List<string>();

        foreach (var fixture in fixtures.Where(x => string.IsNullOrWhiteSpace(x.GroupCode)))
            errors.Add($"Missing group: {fixture.TeamA} - {fixture.TeamB}");

        foreach (var group in groupDiagnostics.Where(x => !string.Equals(x.Status, "valid", StringComparison.OrdinalIgnoreCase)))
        {
            var groupCode = string.IsNullOrWhiteSpace(group.GroupCode) ? "<missing>" : group.GroupCode;
            errors.Add($"Group {groupCode}: {group.Warning}");
        }

        return errors;
    }

    private static async Task WriteGroupDiagnosticsCsvAsync(string path, MarketImpliedXgSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,FixtureCount,ValidFixtureCount,InvalidFixtureCount,UniqueTeamCount,Teams,AverageTotalLambda,SumTotalLambda,Status,Warning");

        foreach (var g in set.GroupDiagnostics)
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(g.GroupCode), D(g.FixtureCount), D(g.ValidFixtureCount), D(g.InvalidFixtureCount), D(g.UniqueTeamCount), Csv(g.Teams),
                D(g.AverageTotalLambda), D(g.SumTotalLambda), Csv(g.Status), Csv(g.Warning)
            }));
        }
    }

    private static string Csv(string? value) => SimpleCsv.Escape(value ?? string.Empty);

    private static string D(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record GroupResolveResult(string GroupCode, string Source);

    private sealed record MatchProbs(double P1, double PX, double P2, double POver);

    private sealed record SplitResult(
        double ShareA,
        double LambdaA,
        double LambdaB,
        double ModelP1,
        double ModelPX,
        double ModelP2,
        double ModelPOver,
        double ObjectiveError);
}
