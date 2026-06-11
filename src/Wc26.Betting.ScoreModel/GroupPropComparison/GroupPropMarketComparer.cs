using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Utilities;
using Wc26.Betting.ScoreModel.GroupSimulation;

namespace Wc26.Betting.ScoreModel.GroupPropComparison;

public sealed class GroupPropMarketComparer
{
    public async Task<GroupPropMarketComparisonSet> CompareAndWriteAsync(
        string propProbabilitiesFile,
        string propOddsFile,
        string outputFolder,
        double edgeThreshold,
        double minOdds,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = await CompareAndWriteAsync(propProbabilitiesFile, propOddsFile, outputFolder, edgeThreshold, minOdds, statPropsOnly: false, overwrite, cancellationToken);
        return set;
    }

    public async Task<GroupPropMarketComparisonSet> CompareAndWriteAsync(
        string propProbabilitiesFile,
        string propOddsFile,
        string outputFolder,
        double edgeThreshold,
        double minOdds,
        bool statPropsOnly,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var set = await CompareAsync(propProbabilitiesFile, propOddsFile, edgeThreshold, minOdds, statPropsOnly, cancellationToken);
        Directory.CreateDirectory(outputFolder);

        await WriteJsonAsync(Path.Combine(outputFolder, "wc26-group-special-props-comparison.json"), set, overwrite, cancellationToken);
        await WriteComparisonCsvAsync(Path.Combine(outputFolder, "wc26-group-special-props-comparison.csv"), set, overwrite, cancellationToken);
        await WriteEdgesCsvAsync(Path.Combine(outputFolder, "wc26-group-special-props-edges.csv"), set, overwrite, cancellationToken);
        await WriteDiagnosticsCsvAsync(Path.Combine(outputFolder, "wc26-group-special-props-comparison-diagnostics.csv"), set, overwrite, cancellationToken);

        return set;
    }

    public async Task<GroupPropMarketComparisonSet> CompareAsync(
        string propProbabilitiesFile,
        string propOddsFile,
        double edgeThreshold,
        double minOdds,
        CancellationToken cancellationToken)
    {
        return await CompareAsync(propProbabilitiesFile, propOddsFile, edgeThreshold, minOdds, statPropsOnly: false, cancellationToken);
    }

    public async Task<GroupPropMarketComparisonSet> CompareAsync(
        string propProbabilitiesFile,
        string propOddsFile,
        double edgeThreshold,
        double minOdds,
        bool statPropsOnly,
        CancellationToken cancellationToken)
    {
        var modelRows = await ReadModelProbabilitiesAsync(propProbabilitiesFile, cancellationToken);
        var oddsRows = await ReadOddsRowsAsync(propOddsFile, cancellationToken);

        var modelByKey = modelRows.ToDictionary(
            x => ModelPropKey.Create(x.GroupCode, x.MarketType, x.Subject, x.Line, x.Side),
            x => x);

        var outputRows = new List<GroupPropMarketComparisonRow>();
        var warnings = new List<string>();
        var missing = 0;

        foreach (var odds in oddsRows.OrderBy(x => x.GroupCode).ThenBy(x => x.MarketOrder))
        {
            if (statPropsOnly && !IsScoreStatMarketType(odds.MarketType))
                continue;

            var mapped = MapMarketType(odds.MarketType);
            if (mapped is null)
            {
                warnings.Add($"Unsupported market type: {odds.GroupCode} {odds.MarketType}");
                continue;
            }

            AddSide(odds, mapped, "Over", odds.OverOdds, odds.OverNoVigProbability);
            AddSide(odds, mapped, "Under", odds.UnderOdds, odds.UnderNoVigProbability);
        }

        var diagnostics = BuildDiagnostics(oddsRows, outputRows);

        return new GroupPropMarketComparisonSet
        {
            SourcePropProbabilitiesFile = propProbabilitiesFile,
            SourcePropOddsFile = propOddsFile,
            OddsRows = oddsRows.Count,
            ComparedRows = outputRows.Count(x => string.IsNullOrWhiteSpace(x.Warning)),
            MissingModelRows = missing,
            Rows = outputRows,
            Diagnostics = diagnostics,
            Warnings = warnings
        };

        void AddSide(GroupPropOddsRow odds, MappedMarket mapped, string side, double bookOdds, double bookNoVigProbability)
        {
            if (bookOdds <= 0 || bookNoVigProbability <= 0)
                return;

            var key = ModelPropKey.Create(odds.GroupCode, mapped.ModelMarketType, mapped.Subject, odds.Line, side);
            if (!modelByKey.TryGetValue(key, out var model))
            {
                missing++;
                var warning = $"Missing model probability for {odds.GroupCode} {odds.MarketType} -> {mapped.ModelMarketType}/{mapped.Subject} line {odds.Line:0.####} {side}";
                warnings.Add(warning);
                outputRows.Add(new GroupPropMarketComparisonRow
                {
                    GroupCode = odds.GroupCode,
                    MarketOrder = odds.MarketOrder,
                    BookMarketType = odds.MarketType,
                    ModelMarketType = mapped.ModelMarketType,
                    MarketNameRu = odds.MarketNameRu,
                    Subject = mapped.Subject,
                    Line = odds.Line,
                    Side = side,
                    BookOdds = bookOdds,
                    BookNoVigProbability = bookNoVigProbability,
                    Decision = "NO_MODEL",
                    Warning = warning
                });
                return;
            }

            var edge = model.ModelProbability - bookNoVigProbability;
            var roi = (model.ModelProbability * bookOdds) - 1.0d;
            var decision = edge >= edgeThreshold && bookOdds >= minOdds && roi > 0 ? "BET" : "NO_BET";

            outputRows.Add(new GroupPropMarketComparisonRow
            {
                GroupCode = odds.GroupCode,
                MarketOrder = odds.MarketOrder,
                BookMarketType = odds.MarketType,
                ModelMarketType = mapped.ModelMarketType,
                MarketNameRu = odds.MarketNameRu,
                Subject = mapped.Subject,
                Line = odds.Line,
                Side = side,
                BookOdds = bookOdds,
                BookNoVigProbability = bookNoVigProbability,
                ModelProbability = model.ModelProbability,
                ModelFairOdds = model.FairOdds,
                Edge = edge,
                RoiAtBookOdds = roi,
                MeanValue = model.MeanValue,
                Decision = decision
            });
        }
    }

    public static bool IsScoreStatMarketType(string bookMarketType)
    {
        return bookMarketType.Trim() switch
        {
            "TeamsScoredZeroGoals" => true,
            "TeamsConcededZeroGoals" => true,
            "Draws" => true,
            "Score00" => true,
            "Score22" => true,
            "Score10Or01" => true,
            "Score21Or12" => true,
            "Score32Or23" => true,
            "GroupTotalGoals" => true,
            _ => false
        };
    }

    private static MappedMarket? MapMarketType(string bookMarketType)
    {
        return bookMarketType.Trim() switch
        {
            "Rank1Points" => new MappedMarket("RankPointsTotal", "1"),
            "Rank2Points" => new MappedMarket("RankPointsTotal", "2"),
            "Rank3Points" => new MappedMarket("RankPointsTotal", "3"),
            "Rank4Points" => new MappedMarket("RankPointsTotal", "4"),
            "TeamsScoredZeroGoals" => new MappedMarket("TeamsScoredZeroGoals", "Group"),
            "TeamsConcededZeroGoals" => new MappedMarket("TeamsConcededZeroGoals", "Group"),
            "Draws" => new MappedMarket("DrawCount", "Group"),
            "Score00" => new MappedMarket("ExactScoreCount_0_0", "Group"),
            "Score22" => new MappedMarket("ExactScoreCount_2_2", "Group"),
            "Score10Or01" => new MappedMarket("ScorePairCount_1_0_0_1", "Group"),
            "Score21Or12" => new MappedMarket("ScorePairCount_2_1_1_2", "Group"),
            "Score32Or23" => new MappedMarket("ScorePairCount_3_2_2_3", "Group"),
            "TeamsWith9Points" => new MappedMarket("TeamsWith9Points", "Group"),
            "TeamsWith0Points" => new MappedMarket("TeamsWith0Points", "Group"),
            "GroupTotalGoals" => new MappedMarket("GroupTotalGoals", "Group"),
            _ => null
        };
    }

    private static List<GroupPropComparisonDiagnostic> BuildDiagnostics(
        IReadOnlyList<GroupPropOddsRow> oddsRows,
        IReadOnlyList<GroupPropMarketComparisonRow> comparisonRows)
    {
        var groups = oddsRows.Select(x => x.GroupCode).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var result = new List<GroupPropComparisonDiagnostic>();
        foreach (var group in groups)
        {
            var rows = comparisonRows.Where(x => string.Equals(x.GroupCode, group, StringComparison.OrdinalIgnoreCase)).ToList();
            var validRows = rows.Where(x => string.IsNullOrWhiteSpace(x.Warning)).ToList();
            var betRows = validRows.Where(x => string.Equals(x.Decision, "BET", StringComparison.OrdinalIgnoreCase)).ToList();
            var missing = rows.Count(x => string.Equals(x.Decision, "NO_MODEL", StringComparison.OrdinalIgnoreCase));
            result.Add(new GroupPropComparisonDiagnostic
            {
                GroupCode = group,
                OddsRows = oddsRows.Count(x => string.Equals(x.GroupCode, group, StringComparison.OrdinalIgnoreCase)),
                ComparedRows = validRows.Count,
                MissingModelRows = missing,
                BetRows = betRows.Count,
                MaxEdge = validRows.Count == 0 ? 0 : validRows.Max(x => x.Edge),
                MaxRoiAtBookOdds = validRows.Count == 0 ? 0 : validRows.Max(x => x.RoiAtBookOdds),
                Status = missing == 0 ? "valid" : "warning",
                Warning = missing == 0 ? string.Empty : $"Missing {missing} model rows."
            });
        }

        return result;
    }

    private static async Task<List<GroupPropProbability>> ReadModelProbabilitiesAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(path);
            var set = await JsonSerializer.DeserializeAsync<GroupPropSimulationSet>(stream, cancellationToken: cancellationToken);
            return set?.PropProbabilities ?? throw new InvalidOperationException($"Could not deserialize prop simulation JSON: {path}");
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0)
            throw new InvalidOperationException($"Model probabilities CSV is empty: {path}");

        var headers = ReadHeaders(lines[0]);
        var rows = new List<GroupPropProbability>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var cells = SimpleCsv.ParseLine(lines[i]);
            rows.Add(new GroupPropProbability
            {
                GroupCode = Get(cells, headers, "Group"),
                MarketType = Get(cells, headers, "MarketType"),
                MarketName = Get(cells, headers, "MarketName"),
                Subject = Get(cells, headers, "Subject"),
                Line = GetDouble(cells, headers, "Line"),
                Side = Get(cells, headers, "Side"),
                ModelProbability = GetDouble(cells, headers, "ModelProbability"),
                FairOdds = GetDouble(cells, headers, "FairOdds"),
                MeanValue = GetDouble(cells, headers, "MeanValue")
            });
        }

        return rows;
    }

    private static async Task<List<GroupPropOddsRow>> ReadOddsRowsAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0)
            throw new InvalidOperationException($"Props odds CSV is empty: {path}");

        var headers = ReadHeaders(lines[0]);
        var rows = new List<GroupPropOddsRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var cells = SimpleCsv.ParseLine(lines[i]);
            rows.Add(new GroupPropOddsRow
            {
                GroupCode = Get(cells, headers, "Group"),
                MarketOrder = GetInt(cells, headers, "MarketOrder", i),
                MarketType = Get(cells, headers, "MarketType"),
                MarketNameRu = Get(cells, headers, "MarketNameRu"),
                Line = GetDouble(cells, headers, "Line"),
                OverOdds = GetDouble(cells, headers, "OverOdds"),
                UnderOdds = GetDouble(cells, headers, "UnderOdds"),
                OverNoVigProbability = GetDouble(cells, headers, "OverNoVigProbability"),
                UnderNoVigProbability = GetDouble(cells, headers, "UnderNoVigProbability")
            });
        }

        return rows;
    }

    private static async Task WriteJsonAsync(string path, GroupPropMarketComparisonSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        var json = JsonSerializer.Serialize(set, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static async Task WriteComparisonCsvAsync(string path, GroupPropMarketComparisonSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MarketOrder,BookMarketType,ModelMarketType,MarketNameRu,Subject,Line,Side,BookOdds,BookNoVigProbability,ModelProbability,ModelFairOdds,Edge,RoiAtBookOdds,MeanValue,Decision,Warning");
        foreach (var row in set.Rows.OrderBy(x => x.GroupCode).ThenBy(x => x.MarketOrder).ThenBy(x => x.Side))
            await writer.WriteLineAsync(FormatComparisonRow(row));
    }

    private static async Task WriteEdgesCsvAsync(string path, GroupPropMarketComparisonSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,MarketOrder,BookMarketType,ModelMarketType,MarketNameRu,Subject,Line,Side,BookOdds,BookNoVigProbability,ModelProbability,ModelFairOdds,Edge,RoiAtBookOdds,MeanValue,Decision,Warning");
        foreach (var row in set.Rows
                     .Where(x => string.Equals(x.Decision, "BET", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(x => x.Edge)
                     .ThenByDescending(x => x.RoiAtBookOdds))
            await writer.WriteLineAsync(FormatComparisonRow(row));
    }

    private static string FormatComparisonRow(GroupPropMarketComparisonRow row)
    {
        return string.Join(',', new[]
        {
            Csv(row.GroupCode), row.MarketOrder.ToString(CultureInfo.InvariantCulture), Csv(row.BookMarketType), Csv(row.ModelMarketType), Csv(row.MarketNameRu), Csv(row.Subject),
            D(row.Line), Csv(row.Side), D(row.BookOdds), D(row.BookNoVigProbability), D(row.ModelProbability), D(row.ModelFairOdds), D(row.Edge), D(row.RoiAtBookOdds), D(row.MeanValue), Csv(row.Decision), Csv(row.Warning)
        });
    }

    private static async Task WriteDiagnosticsCsvAsync(string path, GroupPropMarketComparisonSet set, bool overwrite, CancellationToken cancellationToken)
    {
        EnsureCanWrite(path, overwrite);
        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("Group,OddsRows,ComparedRows,MissingModelRows,BetRows,MaxEdge,MaxRoiAtBookOdds,Status,Warning");
        foreach (var d in set.Diagnostics.OrderBy(x => x.GroupCode))
        {
            await writer.WriteLineAsync(string.Join(',', new[]
            {
                Csv(d.GroupCode), d.OddsRows.ToString(CultureInfo.InvariantCulture), d.ComparedRows.ToString(CultureInfo.InvariantCulture), d.MissingModelRows.ToString(CultureInfo.InvariantCulture), d.BetRows.ToString(CultureInfo.InvariantCulture), D(d.MaxEdge), D(d.MaxRoiAtBookOdds), Csv(d.Status), Csv(d.Warning)
            }));
        }
    }

    private static Dictionary<string, int> ReadHeaders(string headerLine)
    {
        return SimpleCsv.ParseLine(headerLine.TrimStart('\uFEFF'))
            .Select((name, index) => new { Name = name.Trim(), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
    }

    private static string Get(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name, string defaultValue = "")
    {
        return headers.TryGetValue(name, out var index) && index >= 0 && index < cells.Count ? cells[index] : defaultValue;
    }

    private static double GetDouble(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name)
    {
        var value = Get(cells, headers, name).Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"CSV column '{name}' has invalid numeric value '{value}'.");
    }

    private static int GetInt(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> headers, string name, int defaultValue = 0)
    {
        if (!headers.ContainsKey(name))
            return defaultValue;

        var value = Get(cells, headers, name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"CSV column '{name}' has invalid integer value '{value}'.");
    }

    private static void EnsureCanWrite(string path, bool overwrite)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Output file already exists: {path}. Use --overwrite.");
    }

    private static string Csv(string? value) => SimpleCsv.Escape(value ?? string.Empty);

    private static string D(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
