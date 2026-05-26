using Wc26.Betting.Core.Calendar;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Odds;
using Wc26.Betting.Core.Players;
using Wc26.Betting.Core.Sofascore;
using Wc26.Betting.Core.Simulation;
using Wc26.Betting.Core.TeamRatings;
using Wc26.Betting.Core.Validation;

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
                "build-models" => await RunBuildModelsAsync(options, cancellationToken),
                "validate-models" => await RunValidateModelsAsync(options, cancellationToken),
                "run-simulation" => await RunSimulationAsync(options, cancellationToken),
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


    private static async Task<int> RunBuildModelsAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var sofascoreFolder = options.Get("sofascore-folder", Path.Combine("data", "raw", "sofascore"));
        var playerRatingsFile = options.GetAny(["player-ratings-file", "ea-ratings-file"], string.Empty);
        var outputFolder = options.GetAny(["models-folder", "output-folder"], Path.Combine("data", "models"));
        var gameOddsFile = options.GetAny(["game-odds-file", "odds-file"], string.Empty);
        var overwrite = options.GetBool("overwrite", false);
        var skipCalendar = options.GetBool("skip-calendar", false);
        var skipPlayerRatings = options.GetBool("skip-player-ratings", false);
        var skipEloRatings = options.GetBool("skip-elo-ratings", false);
        var skipGameOdds = options.GetBool("skip-game-odds", false);

        Directory.CreateDirectory(outputFolder);

        Wc2026CalendarSet? builtCalendar = null;
        Wc2026GroupSet? builtGroups = null;

        if (!skipCalendar)
        {
            Console.WriteLine("Building WC2026 calendar model set...");
            var calendarBuilder = new Wc2026CalendarModelBuilder();
            var calendar = calendarBuilder.BuildFromSofascoreFolder(sofascoreFolder);
            await calendarBuilder.WriteAsync(calendar, outputFolder, overwrite, cancellationToken);
            var groups = calendarBuilder.BuildGroups(calendar);
            builtCalendar = calendar;
            builtGroups = groups;
            Console.WriteLine($"  Matches: {calendar.Matches.Count}");
            Console.WriteLine($"  Groups: {groups.Groups.Count}");
            Console.WriteLine($"  Output: {Path.Combine(outputFolder, "calendar")}");
        }

        if (!skipPlayerRatings)
        {
            if (string.IsNullOrWhiteSpace(playerRatingsFile))
                throw new ArgumentException("--player-ratings-file is required unless --skip-player-ratings is used.");

            Console.WriteLine("Building EAFC26 player-ratings model set...");
            var importer = new EaFcPlayerRatingsImporter();
            var ratings = importer.Import(playerRatingsFile);
            await importer.WriteAsync(ratings, outputFolder, overwrite, cancellationToken);
            Console.WriteLine($"  Rows read: {ratings.RowCount}");
            Console.WriteLine($"  Players imported: {ratings.Players.Count}");
            Console.WriteLine($"  Output: {Path.Combine(outputFolder, "player-ratings")}");
        }

        if (!skipEloRatings)
        {
            Console.WriteLine("Building hardcoded Elo ratings model set...");
            var eloBuilder = new HardcodedEloRatingsBuilder();
            var elo = eloBuilder.Build();
            await eloBuilder.WriteAsync(elo, outputFolder, overwrite, cancellationToken);
            Console.WriteLine($"  Teams: {elo.Teams.Count}");
            Console.WriteLine($"  As of: {elo.AsOfDate:yyyy-MM-dd}");
            Console.WriteLine($"  Output: {Path.Combine(outputFolder, "team-ratings")}");
        }


        if (!skipGameOdds && !string.IsNullOrWhiteSpace(gameOddsFile))
        {
            Console.WriteLine("Building game-odds model set...");
            var oddsImporter = new GameOddsImporter();
            var calendarForOdds = builtCalendar ?? await TryReadModelAsync<Wc2026CalendarSet>(Path.Combine(outputFolder, "calendar", "wc2026-calendar.json"), cancellationToken);
            var groupsForOdds = builtGroups ?? await TryReadModelAsync<Wc2026GroupSet>(Path.Combine(outputFolder, "calendar", "wc2026-groups.json"), cancellationToken);
            var odds = oddsImporter.Import(gameOddsFile, calendarForOdds, groupsForOdds);
            await oddsImporter.WriteAsync(odds, outputFolder, overwrite, cancellationToken);
            Console.WriteLine($"  Rows read: {odds.RowCount}");
            Console.WriteLine($"  Odds matches: {odds.Matches.Count}");
            Console.WriteLine($"  Matched to calendar: {odds.Matches.Count(x => x.CalendarEventId is not null)}");
            Console.WriteLine($"  Output: {Path.Combine(outputFolder, "odds")}");
        }

        if (options.GetBool("validate", true))
        {
            Console.WriteLine();
            var validator = new ModelsValidator();
            var report = await validator.ValidateAsync(outputFolder, writeReport: true, cancellationToken);
            PrintValidationSummary(report);
            return report.Errors.Count == 0 ? 0 : 1;
        }

        return 0;
    }


    private static async Task<T?> TryReadModelAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return default;

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }


    private static async Task<int> RunSimulationAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var modelsFolder = options.GetAny(["models-folder", "input-folder"], Path.Combine("data", "models"));
        var outputFolder = options.GetAny(["output-folder", "simulation-folder"], Path.Combine(modelsFolder, "simulation"));
        var iterations = options.GetInt("iterations", 10000);
        var seed = options.GetInt("seed", 2026);
        var overwrite = options.GetBool("overwrite", false);

        Console.WriteLine("Running WC2026 simulation skeleton...");
        var runner = new Wc2026SimulationRunner();
        var result = await runner.RunFromModelsFolderAsync(modelsFolder, iterations, seed, outputFolder, overwrite, cancellationToken);

        Console.WriteLine("WC2026 SIMULATION RESULT");
        Console.WriteLine($"Iterations: {result.Iterations}");
        Console.WriteLine($"Seed: {result.Seed}");
        Console.WriteLine($"Teams: {result.Teams.Count}");
        Console.WriteLine($"Groups: {result.Groups.Count}");
        Console.WriteLine($"Output: {outputFolder}");

        Console.WriteLine();
        Console.WriteLine("Top group-winner probabilities:");
        foreach (var team in result.Teams.OrderByDescending(x => x.WinGroupProbability).Take(12))
            Console.WriteLine($"  {team.GroupCode} | {team.Team}: win group {team.WinGroupProbability:P1}, qualify R32 {team.QualifiedToRoundOf32Probability:P1}");

        return 0;
    }

    private static async Task<int> RunValidateModelsAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var modelsFolder = options.GetAny(["models-folder", "input-folder"], Path.Combine("data", "models"));
        var validator = new ModelsValidator();
        var report = await validator.ValidateAsync(modelsFolder, writeReport: true, cancellationToken);
        PrintValidationSummary(report);
        return report.Errors.Count == 0 ? 0 : 1;
    }

    private static void PrintValidationSummary(ModelValidationReport report)
    {
        Console.WriteLine("MODEL VALIDATION RESULT");
        Console.WriteLine($"Status: {report.Status}");
        Console.WriteLine($"Errors: {report.Errors.Count}");
        Console.WriteLine($"Warnings: {report.Warnings.Count}");
        Console.WriteLine($"Info: {report.Info.Count}");

        foreach (var error in report.Errors.Take(20))
            Console.WriteLine($"ERROR: {error}");
        foreach (var warning in report.Warnings.Take(20))
            Console.WriteLine($"WARN: {warning}");
        foreach (var info in report.Info.Take(20))
            Console.WriteLine($"INFO: {info}");

        Console.WriteLine($"Report folder: {Path.Combine(report.ModelsFolder, "validation")}");
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
        Console.WriteLine("  build-models      Build file-based model sets from SofaScore JSONs, EAFC26 CSV/ZIP, Elo and odds CSV");
        Console.WriteLine("  validate-models   Run sanity checks on generated model sets");
        Console.WriteLine("  run-simulation    Run WC2026 group-stage Monte Carlo simulation skeleton");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore --destination-folder C:\\Temp\\wc26\\sofascore --overwrite");
        Console.WriteLine("  dotnet run --project src/Wc26.Betting.Console -- grab-sofascore --rounds 3,6:round-of-32,5:round-of-16");
        Console.WriteLine(@"  dotnet run --project src/Wc26.Betting.Console -- build-models --sofascore-folder C:\Temp\wc26\sofascore --player-ratings-file C:\Temp\wc26\EAFC26-Men.zip --game-odds-file C:\Temp\wc26\odds\wc2026-game-odds.csv --models-folder C:\Temp\wc26\models --overwrite");
        Console.WriteLine(@"  dotnet run --project src/Wc26.Betting.Console -- validate-models --models-folder C:\Temp\wc26\models");
        Console.WriteLine(@"  dotnet run --project src/Wc26.Betting.Console -- run-simulation --models-folder C:\Temp\wc26\models --iterations 10000 --overwrite");
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
        Console.WriteLine();
        Console.WriteLine("Options for build-models:");
        Console.WriteLine("  --sofascore-folder <path>      Folder with raw SofaScore JSONs. Default: data/raw/sofascore");
        Console.WriteLine("  --player-ratings-file <path>   EAFC26-Men.csv or EAFC26-Men.zip from Kaggle");
        Console.WriteLine("  --models-folder <path>         Output model folder. Alias: --output-folder. Default: data/models");
        Console.WriteLine("  --game-odds-file <path>        Optional parsed game odds CSV. Alias: --odds-file");
        Console.WriteLine("  --skip-game-odds              Do not build odds model even when odds file is provided");
        Console.WriteLine("  --overwrite                    Overwrite existing model files");
        Console.WriteLine("  --skip-calendar                Do not build calendar/group model sets");
        Console.WriteLine("  --skip-player-ratings          Do not build player-ratings model set");
        Console.WriteLine("  --skip-elo-ratings             Do not build hardcoded Elo ratings model set");
        Console.WriteLine("  --validate <true|false>        Run validation after build. Default: true");
        Console.WriteLine();
        Console.WriteLine("Options for validate-models:");
        Console.WriteLine("  --models-folder <path>         Model folder. Alias: --input-folder. Default: data/models");
        Console.WriteLine();
        Console.WriteLine("Options for run-simulation:");
        Console.WriteLine("  --models-folder <path>         Folder containing generated model sets. Default: data/models");
        Console.WriteLine("  --output-folder <path>         Simulation output folder. Default: <models-folder>/simulation");
        Console.WriteLine("  --iterations <n>               Monte Carlo iterations. Default: 10000");
        Console.WriteLine("  --seed <n>                     Random seed. Default: 2026");
        Console.WriteLine("  --overwrite                    Overwrite existing simulation files");
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
