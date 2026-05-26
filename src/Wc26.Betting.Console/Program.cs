using Wc26.Betting.Core.Sofascore;

var exitCode = await CliApplication.RunAsync(args, CancellationToken.None);
return exitCode;

internal static class CliApplication
{
    private static readonly IReadOnlyList<SofascoreRoundRequest> DefaultWc2026Rounds =
    [
        new(1, null),
        new(2, null),
        new(3, null),
        new(6, "round-of-32"),
        new(5, "round-of-16"),
        new(27, "quarterfinals"),
        new(28, "semifinals"),
        new(50, "match-for-3rd-place"),
        new(29, "final")
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var options = CliOptions.Parse(args.Skip(1));

        try
        {
            return command switch
            {
                "grab-sofascore" => await RunGrabSofascoreAsync(options, cancellationToken),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunGrabSofascoreAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var outputDir = options.GetAny(["destination-folder", "output"], Path.Combine("data", "raw", "sofascore"));

        // SofaScore URL shape is:
        // /unique-tournament/{uniqueTournamentId}/season/{seasonId}/events/...
        // For World Cup 2026: unique tournament id = 16, season id = 58210.
        var tournamentId = options.GetInt("tournament-id", 16);
        var seasonId = options.GetInt("season-id", 58210);
        var rounds = options.TryGet("rounds", out var roundsValue)
            ? ParseRounds(roundsValue)
            : DefaultWc2026Rounds;

        var request = new SofascoreGrabRequest(
            OutputDirectory: outputDir,
            TournamentId: tournamentId,
            SeasonId: seasonId,
            Rounds: rounds,
            Overwrite: options.GetBool("overwrite", false),
            DownloadIncidents: !options.GetBool("no-incidents", false),
            DownloadStatistics: !options.GetBool("no-statistics", false),
            SkipDetailsForNotStartedEvents: !options.GetBool("include-details-for-not-started", false),
            StrictEventDetails: options.GetBool("strict-event-details", false),
            Headless: !options.GetBool("headed", false),
            DelayMs: options.GetInt("delay-ms", 450),
            WarmupDelayMs: options.GetInt("warmup-delay-ms", 1000),
            WarmupUrl: options.Get("warmup-url", "https://www.sofascore.com"),
            UserAgent: options.Get("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"));

        var service = new SofascoreGrabber(Console.Out);
        var result = await service.GrabAsync(request, cancellationToken);

        Console.WriteLine();
        Console.WriteLine("SOFASCORE GRAB RESULT");
        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine($"Output: {result.OutputPath}");
        Console.WriteLine($"Rounds downloaded: {result.RoundsDownloaded}");
        Console.WriteLine($"Events discovered: {result.EventsDiscovered}");
        Console.WriteLine($"Files written: {result.FilesWritten}");
        Console.WriteLine($"Files skipped: {result.FilesSkipped}");
        Console.WriteLine($"Warnings: {result.Warnings.Count}");
        Console.WriteLine($"Failures: {result.Failures.Count}");
        Console.WriteLine(result.Message);

        if (result.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failures:");
            foreach (var failure in result.Failures.Take(20))
                Console.WriteLine($"- {failure}");
        }

        return result.Failures.Count == 0 ? 0 : 1;
    }

    private static IReadOnlyList<SofascoreRoundRequest> ParseRounds(string value)
    {
        // Format: 1,2,3,6:round-of-32,5:round-of-16,27:quarterfinals
        var result = new List<SofascoreRoundRequest>();
        foreach (var rawPart in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawPart.Split(':', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(parts[0], out var round) || round <= 0)
                throw new ArgumentException($"Invalid round value '{rawPart}'. Expected number or number:slug.");

            var slug = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
            result.Add(new SofascoreRoundRequest(round, slug));
        }

        if (result.Count == 0)
            throw new ArgumentException("--rounds must contain at least one round.");

        return result;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static bool IsHelp(string value)
        => value is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("wc26_betting");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  grab-sofascore    Download WC2026 SofaScore calendar JSONs and event JSONs");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore --destination-folder C:\\Temp\\wc26\\sofascore --overwrite");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore --rounds 3,6:round-of-32,5:round-of-16");
        Console.WriteLine();
        Console.WriteLine("Options for grab-sofascore:");
        Console.WriteLine("  --destination-folder <path>   Output directory. Alias: --output. Default: data/raw/sofascore");
        Console.WriteLine("  --tournament-id <id>          SofaScore unique tournament id. Default: 16");
        Console.WriteLine("  --season-id <id>              SofaScore season id. Default: 58210");
        Console.WriteLine("  --rounds <list>               Comma list: round or round:slug. Default: WC2026 known rounds");
        Console.WriteLine("  --overwrite                   Overwrite existing JSON files");
        Console.WriteLine("  --no-incidents                Do not download event incidents JSON");
        Console.WriteLine("  --no-statistics               Do not download event statistics JSON");
        Console.WriteLine("  --include-details-for-not-started  Try incidents/statistics even for not-started fixtures");
        Console.WriteLine("  --strict-event-details        Treat incidents/statistics failures as failures, not warnings");
        Console.WriteLine("  --headed                      Run Chromium visible instead of headless");
        Console.WriteLine("  --delay-ms <ms>               Delay between event detail calls. Default: 450");
        Console.WriteLine("  --warmup-delay-ms <ms>        Delay after SofaScore warmup page. Default: 1000");
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values;

    private CliOptions(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var items = args.ToArray();

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (!item.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{item}'. Options must start with --.");

            var key = item[2..];
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Empty option name.");

            if (i + 1 >= items.Length || items[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = "true";
                continue;
            }

            values[key] = items[++i];
        }

        return new CliOptions(values);
    }

    public string Get(string name, string defaultValue)
        => _values.TryGetValue(name, out var value) ? value : defaultValue;

    public string GetAny(IReadOnlyList<string> names, string defaultValue)
    {
        foreach (var name in names)
        {
            if (_values.TryGetValue(name, out var value))
                return value;
        }

        return defaultValue;
    }

    public int GetInt(string name, int defaultValue)
    {
        if (!_values.TryGetValue(name, out var value))
            return defaultValue;

        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Option '--{name}' requires integer value.");
    }

    public bool GetBool(string name, bool defaultValue)
    {
        if (!_values.TryGetValue(name, out var value))
            return defaultValue;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        throw new ArgumentException($"Option '--{name}' requires boolean value or no value.");
    }

    public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value!);
}
