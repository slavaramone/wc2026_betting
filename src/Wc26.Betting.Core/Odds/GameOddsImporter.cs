using System.Globalization;
using System.Text.Json;
using Wc26.Betting.Core.Models;
using Wc26.Betting.Core.Utilities;

namespace Wc26.Betting.Core.Odds;

public sealed class GameOddsImporter
{
    public GameOddsSet Import(string sourcePath, Wc2026CalendarSet? calendar = null, Wc2026GroupSet? groups = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Game odds CSV file is required.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Game odds CSV file not found.", sourcePath);

        using var reader = new StreamReader(sourcePath);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
            throw new InvalidOperationException("Game odds CSV is empty.");

        var headers = SimpleCsv.ParseLine(headerLine)
            .Select((name, index) => new { name = NormalizeHeader(name), index })
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        var matchLookup = BuildCalendarMatchLookup(calendar, groups);
        var matches = new List<GameOddsMatch>();
        var rowCount = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            rowCount++;
            var values = SimpleCsv.ParseLine(line);
            var homeRaw = Get(values, headers, "home_team");
            var awayRaw = Get(values, headers, "away_team");
            var home = RussianToEnglishNationName(homeRaw);
            var away = RussianToEnglishNationName(awayRaw);
            var normalizedHome = NormalizeTeamName(home);
            var normalizedAway = NormalizeTeamName(away);

            var calendarMatch = FindCalendarMatch(matchLookup, normalizedHome, normalizedAway, Get(values, headers, "match_date"));

            matches.Add(new GameOddsMatch
            {
                MatchKey = Get(values, headers, "match_key"),
                MatchDate = Get(values, headers, "match_date"),
                MatchTime = Get(values, headers, "match_time"),
                HomeTeamRaw = homeRaw,
                AwayTeamRaw = awayRaw,
                HomeTeam = home,
                AwayTeam = away,
                NormalizedHomeTeam = normalizedHome,
                NormalizedAwayTeam = normalizedAway,
                CalendarEventId = calendarMatch?.Match.EventId,
                CalendarStage = calendarMatch?.Match.Stage ?? string.Empty,
                CalendarGroupCode = calendarMatch?.GroupCode ?? string.Empty,
                CalendarStartUtc = calendarMatch?.Match.StartUtc,
                MatchStatus = calendarMatch is null ? "unmatched" : "matched",
                Odds1 = GetDouble(values, headers, "odds_1"),
                OddsX = GetDouble(values, headers, "odds_x"),
                Odds2 = GetDouble(values, headers, "odds_2"),
                Odds1X2Overround = GetDouble(values, headers, "odds_1x2_overround"),
                Handicap1Line = GetDouble(values, headers, "handicap1_line"),
                Handicap1Odds = GetDouble(values, headers, "handicap1_odds"),
                Handicap2Line = GetDouble(values, headers, "handicap2_line"),
                Handicap2Odds = GetDouble(values, headers, "handicap2_odds"),
                TotalLine = GetDouble(values, headers, "total_line"),
                OverOdds = GetDouble(values, headers, "over_odds"),
                UnderOdds = GetDouble(values, headers, "under_odds"),
                TotalOverround = GetDouble(values, headers, "total_overround"),
                BttsYesOdds = GetDouble(values, headers, "btts_yes_odds"),
                BttsNoOdds = GetDouble(values, headers, "btts_no_odds"),
                BttsOverround = GetDouble(values, headers, "btts_overround"),
                SourceImage = Get(values, headers, "source_image")
            });
        }

        return new GameOddsSet
        {
            SourceFile = sourcePath,
            RowCount = rowCount,
            Matches = matches
                .OrderBy(x => x.MatchDate, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.MatchTime, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.HomeTeam, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public async Task WriteAsync(GameOddsSet set, string outputFolder, bool overwrite, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputFolder, "odds");
        Directory.CreateDirectory(folder);

        await WriteJsonAsync(Path.Combine(folder, "game-odds.json"), set, overwrite, cancellationToken);
        await WriteOddsCsvAsync(Path.Combine(folder, "game-odds.csv"), set, overwrite, cancellationToken);
        await WriteMatchMapCsvAsync(Path.Combine(folder, "game-odds-match-map.csv"), set, overwrite, cancellationToken);
    }

    public static string RussianToEnglishNationName(string value)
    {
        var v = value.Trim();
        return v switch
        {
            "Австралия" => "Australia",
            "Австрия" => "Austria",
            "Алжир" => "Algeria",
            "Англия" => "England",
            "Аргентина" => "Argentina",
            "Бельгия" => "Belgium",
            "Босния и Герцеговина" => "Bosnia & Herzegovina",
            "Бразилия" => "Brazil",
            "Гаити" => "Haiti",
            "Гана" => "Ghana",
            "Германия" => "Germany",
            "ДР Конго" => "DR Congo",
            "Египет" => "Egypt",
            "Иордания" => "Jordan",
            "Ирак" => "Iraq",
            "Иран" => "Iran",
            "Испания" => "Spain",
            "Кабо-Верде" => "Cabo Verde",
            "Канада" => "Canada",
            "Катар" => "Qatar",
            "Колумбия" => "Colombia",
            "Кот-д'Ивуар" => "Côte d'Ivoire",
            "Кот-д’Ивуар" => "Côte d'Ivoire",
            "Кот-д`Ивуар" => "Côte d'Ivoire",
            "Кюрасао" => "Curaçao",
            "Марокко" => "Morocco",
            "Мексика" => "Mexico",
            "Нидерланды" => "Netherlands",
            "Новая Зеландия" => "New Zealand",
            "Норвегия" => "Norway",
            "Панама" => "Panama",
            "Парагвай" => "Paraguay",
            "Португалия" => "Portugal",
            "Саудовская Аравия" => "Saudi Arabia",
            "Сенегал" => "Senegal",
            "США" => "United States",
            "Тунис" => "Tunisia",
            "Турция" => "Turkey",
            "Узбекистан" => "Uzbekistan",
            "Уругвай" => "Uruguay",
            "Франция" => "France",
            "Хорватия" => "Croatia",
            "Чехия" => "Czechia",
            "Швейцария" => "Switzerland",
            "Швеция" => "Sweden",
            "Шотландия" => "Scotland",
            "Эквадор" => "Ecuador",
            "ЮАР" => "South Africa",
            "Южная Корея" => "South Korea",
            "Япония" => "Japan",
            _ => v
        };
    }

    public static string NormalizeTeamName(string value)
    {
        var v = value.Trim();
        return v switch
        {
            "Bosnia and Herzegovina" => "Bosnia & Herzegovina",
            "Cape Verde Islands" => "Cabo Verde",
            "Cape Verde" => "Cabo Verde",
            "Congo DR" => "DR Congo",
            "Côte d’Ivoire" => "Côte d'Ivoire",
            "Ivory Coast" => "Côte d'Ivoire",
            "Curacao" => "Curaçao",
            "Holland" => "Netherlands",
            "USA" => "United States",
            "United States of America" => "United States",
            "Korea Republic" => "South Korea",
            "Korea, Republic of" => "South Korea",
            "IR Iran" => "Iran",
            "Türkiye" => "Turkey",
            "Czech Republic" => "Czechia",
            _ => v
        };
    }

    private sealed record CalendarMatchRef(Wc2026CalendarMatch Match, string GroupCode);

    private static Dictionary<string, List<CalendarMatchRef>> BuildCalendarMatchLookup(Wc2026CalendarSet? calendar, Wc2026GroupSet? groups)
    {
        var result = new Dictionary<string, List<CalendarMatchRef>>(StringComparer.OrdinalIgnoreCase);
        if (calendar is null)
            return result;

        var groupByEventId = groups?.Groups
            .SelectMany(g => g.Matches.Select(m => new { m.EventId, g.GroupCode }))
            .GroupBy(x => x.EventId)
            .ToDictionary(g => g.Key, g => g.First().GroupCode)
            ?? new Dictionary<long, string>();

        foreach (var match in calendar.Matches.Where(x => x.HasKnownTeams))
        {
            var groupCode = groupByEventId.TryGetValue(match.EventId, out var code) ? code : string.Empty;
            var reference = new CalendarMatchRef(match, groupCode);
            AddLookup(result, NormalizeTeamName(match.HomeTeam), NormalizeTeamName(match.AwayTeam), reference);
        }

        return result;
    }

    private static void AddLookup(Dictionary<string, List<CalendarMatchRef>> lookup, string home, string away, CalendarMatchRef reference)
    {
        var key = BuildPairKey(home, away);
        if (!lookup.TryGetValue(key, out var list))
        {
            list = new List<CalendarMatchRef>();
            lookup[key] = list;
        }

        list.Add(reference);
    }

    private static CalendarMatchRef? FindCalendarMatch(Dictionary<string, List<CalendarMatchRef>> lookup, string home, string away, string matchDate)
    {
        if (!lookup.TryGetValue(BuildPairKey(home, away), out var candidates))
            return null;

        if (DateTime.TryParse(matchDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            var byDate = candidates.FirstOrDefault(x => x.Match.StartUtc?.Date == parsedDate.Date);
            if (byDate is not null)
                return byDate;
        }

        return candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault();
    }

    private static string BuildPairKey(string home, string away)
        => $"{NormalizeTeamName(home)}|||{NormalizeTeamName(away)}";

    private static string NormalizeHeader(string value)
        => value.Trim().TrimStart('\uFEFF').ToLowerInvariant().Replace(" ", "_").Replace("-", "_");

    private static string Get(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> headers, string key)
    {
        if (!headers.TryGetValue(key, out var index) || index < 0 || index >= values.Count)
            return string.Empty;
        var value = values[index].Trim();
        return value == "-" ? string.Empty : value;
    }

    private static double? GetDouble(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> headers, string key)
    {
        var value = Get(values, headers, key);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Replace('%', ' ').Trim().Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, options), cancellationToken);
    }

    private static async Task WriteOddsCsvAsync(string path, GameOddsSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("match_key,match_date,match_time,home_team,away_team,home_team_raw,away_team_raw,event_id,stage,group_code,odds_1,odds_x,odds_2,odds_1x2_overround,handicap1_line,handicap1_odds,handicap2_line,handicap2_odds,total_line,over_odds,under_odds,total_overround,btts_yes_odds,btts_no_odds,btts_overround,source_image");
        foreach (var m in set.Matches)
        {
            var values = new[]
            {
                m.MatchKey,
                m.MatchDate,
                m.MatchTime,
                m.HomeTeam,
                m.AwayTeam,
                m.HomeTeamRaw,
                m.AwayTeamRaw,
                m.CalendarEventId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                m.CalendarStage,
                m.CalendarGroupCode,
                Format(m.Odds1),
                Format(m.OddsX),
                Format(m.Odds2),
                Format(m.Odds1X2Overround),
                Format(m.Handicap1Line),
                Format(m.Handicap1Odds),
                Format(m.Handicap2Line),
                Format(m.Handicap2Odds),
                Format(m.TotalLine),
                Format(m.OverOdds),
                Format(m.UnderOdds),
                Format(m.TotalOverround),
                Format(m.BttsYesOdds),
                Format(m.BttsNoOdds),
                Format(m.BttsOverround),
                m.SourceImage
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static async Task WriteMatchMapCsvAsync(string path, GameOddsSet set, bool overwrite, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && !overwrite)
            throw new IOException($"File already exists: {path}. Use --overwrite.");

        await using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("match_key,home_team,away_team,event_id,stage,group_code,calendar_start_utc,match_status,source_image");
        foreach (var m in set.Matches)
        {
            var values = new[]
            {
                m.MatchKey,
                m.HomeTeam,
                m.AwayTeam,
                m.CalendarEventId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                m.CalendarStage,
                m.CalendarGroupCode,
                m.CalendarStartUtc?.ToString("O") ?? string.Empty,
                m.MatchStatus,
                m.SourceImage
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(SimpleCsv.Escape)));
        }
    }

    private static string Format(double? value)
        => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
}
