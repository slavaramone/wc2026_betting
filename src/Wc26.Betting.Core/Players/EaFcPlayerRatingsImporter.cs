using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Players;

public sealed class EaFcPlayerRatingsImporter
{
    public EaFcPlayerRatingSet Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Player ratings source file is required.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Player ratings source file not found.", sourcePath);

        using var stream = OpenCsvStream(sourcePath, out var csvName);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);

        var headerLine = reader.ReadLine();
        if (headerLine is null)
            throw new InvalidOperationException("Player ratings CSV is empty.");

        var headers = SimpleCsv.ParseLine(headerLine).Select((name, index) => new { name, index })
            .ToDictionary(x => NormalizeHeader(x.name), x => x.index, StringComparer.OrdinalIgnoreCase);

        var players = new List<EaFcPlayerRating>();
        var rowCount = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            rowCount++;
            var values = SimpleCsv.ParseLine(line);
            var player = new EaFcPlayerRating
            {
                SourcePlayerId = Get(values, headers, "id"),
                Rank = GetInt(values, headers, "rank"),
                Name = Get(values, headers, "name"),
                Gender = Get(values, headers, "gender"),
                Overall = GetInt(values, headers, "ovr") ?? 0,
                Pace = GetInt(values, headers, "pac"),
                Shooting = GetInt(values, headers, "sho"),
                Passing = GetInt(values, headers, "pas"),
                Dribbling = GetInt(values, headers, "dri"),
                Defending = GetInt(values, headers, "def"),
                Physical = GetInt(values, headers, "phy"),
                Position = Get(values, headers, "position"),
                AlternativePositions = Get(values, headers, "alternativepositions"),
                Age = GetInt(values, headers, "age"),
                Nation = Get(values, headers, "nation"),
                League = Get(values, headers, "league"),
                Club = Get(values, headers, "team"),
                PreferredFoot = Get(values, headers, "preferredfoot"),
                Url = Get(values, headers, "url")
            };

            if (!string.IsNullOrWhiteSpace(player.SourcePlayerId) || !string.IsNullOrWhiteSpace(player.Name))
                players.Add(player);
        }

        return new EaFcPlayerRatingSet
        {
            SourceFile = sourcePath,
            SourceCsvName = csvName,
            RowCount = rowCount,
            Players = players
                .OrderByDescending(x => x.Overall)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task WriteAsync(EaFcPlayerRatingSet set, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputFolder, "player-ratings");
        Directory.CreateDirectory(folder);

        await WriteJsonAsync(Path.Combine(folder, "eafc26-men-player-ratings.json"), set, overwrite, cancellationToken);
        await WritePlayersCsvAsync(Path.Combine(folder, "eafc26-men-player-ratings.csv"), set, overwrite, cancellationToken);

        var nationSeeds = BuildNationRatingSeeds(set.Players);
        await WriteJsonAsync(Path.Combine(folder, "eafc26-nation-rating-seeds.json"), nationSeeds, overwrite, cancellationToken);
        await WriteNationSeedsCsvAsync(Path.Combine(folder, "eafc26-nation-rating-seeds.csv"), nationSeeds, overwrite, cancellationToken);
    }

    public static List<NationRatingSeed> BuildNationRatingSeeds(IEnumerable<EaFcPlayerRating> players)
    {
        return players
            .Where(x => !string.IsNullOrWhiteSpace(x.Nation) && x.Overall > 0)
            .GroupBy(x => x.Nation.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildNationSeed(g.Key, g.OrderByDescending(x => x.Overall).ToList()))
            .OrderByDescending(x => x.Top26AverageOverall)
            .ThenBy(x => x.Nation, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NationRatingSeed BuildNationSeed(string nation, List<EaFcPlayerRating> players)
    {
        var top26 = players.Take(26).ToList();
        var top11 = players.Take(11).ToList();
        var attackers = players.Where(x => x.PositionGroup == "ATT").Take(6).ToList();
        var mids = players.Where(x => x.PositionGroup == "MID").Take(8).ToList();
        var defenders = players.Where(x => x.PositionGroup == "DEF").Take(8).ToList();
        var keepers = players.Where(x => x.PositionGroup == "GK").Take(3).ToList();
        var bench = players.Skip(11).Take(15).ToList();

        var confidence = players.Count >= 26 && keepers.Count >= 2 && defenders.Count >= 5 && mids.Count >= 5 && attackers.Count >= 3
            ? "High"
            : players.Count >= 18 ? "Medium" : "Low";

        return new NationRatingSeed
        {
            Nation = nation,
            PlayerCount = players.Count,
            Top26Count = top26.Count,
            Top26AverageOverall = Average(top26),
            Top11AverageOverall = Average(top11),
            AttackRating = Average(attackers),
            MidfieldRating = Average(mids),
            DefenceRating = Average(defenders),
            GoalkeeperRating = Average(keepers),
            BenchRating = Average(bench),
            Confidence = confidence
        };
    }

    private static double Average(IReadOnlyCollection<EaFcPlayerRating> players)
        => players.Count == 0 ? 0 : Math.Round(players.Average(x => x.Overall), 3);

    private static Stream OpenCsvStream(string sourcePath, out string csvName)
    {
        if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var archive = ZipFile.OpenRead(sourcePath);
            var entry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Zip file does not contain a CSV file: {sourcePath}");
            csvName = entry.FullName;
            var entryStream = entry.Open();
            return new ArchiveStreamWrapper(archive, entryStream);
        }

        csvName = Path.GetFileName(sourcePath);
        return File.OpenRead(sourcePath);
    }

    private static string NormalizeHeader(string value)
        => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string Get(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> headers, string key)
    {
        if (!headers.TryGetValue(key, out var index) || index < 0 || index >= values.Count)
            return string.Empty;
        return values[index].Trim();
    }

    private static int? GetInt(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> headers, string key)
    {
        var value = Get(values, headers, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, options), cancellationToken);
    }

    private static async Task WritePlayersCsvAsync(string path, EaFcPlayerRatingSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("source_player_id,rank,name,gender,overall,pace,shooting,passing,dribbling,defending,physical,position,position_group,alternative_positions,age,nation,league,club,preferred_foot,url");
        foreach (var p in set.Players)
        {
            var values = new[]
            {
                p.SourcePlayerId,
                p.Rank?.ToString() ?? string.Empty,
                p.Name,
                p.Gender,
                p.Overall.ToString(CultureInfo.InvariantCulture),
                p.Pace?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Shooting?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Passing?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Dribbling?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Defending?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Physical?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Position,
                p.PositionGroup,
                p.AlternativePositions,
                p.Age?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                p.Nation,
                p.League,
                p.Club,
                p.PreferredFoot,
                p.Url
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteNationSeedsCsvAsync(string path, IReadOnlyList<NationRatingSeed> seeds, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("nation,player_count,top26_count,top26_avg,top11_avg,attack,midfield,defence,gk,bench,confidence");
        foreach (var s in seeds)
        {
            var values = new[]
            {
                s.Nation,
                s.PlayerCount.ToString(CultureInfo.InvariantCulture),
                s.Top26Count.ToString(CultureInfo.InvariantCulture),
                s.Top26AverageOverall.ToString(CultureInfo.InvariantCulture),
                s.Top11AverageOverall.ToString(CultureInfo.InvariantCulture),
                s.AttackRating.ToString(CultureInfo.InvariantCulture),
                s.MidfieldRating.ToString(CultureInfo.InvariantCulture),
                s.DefenceRating.ToString(CultureInfo.InvariantCulture),
                s.GoalkeeperRating.ToString(CultureInfo.InvariantCulture),
                s.BenchRating.ToString(CultureInfo.InvariantCulture),
                s.Confidence
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private sealed class ArchiveStreamWrapper : Stream
    {
        private readonly ZipArchive _archive;
        private readonly Stream _inner;

        public ArchiveStreamWrapper(ZipArchive archive, Stream inner)
        {
            _archive = archive;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _archive.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
