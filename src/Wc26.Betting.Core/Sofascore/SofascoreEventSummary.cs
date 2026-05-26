using System.Text.Json;

namespace Wc26.Betting.Core.Sofascore;

public sealed class SofascoreEventSummary
{
    public long EventId { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string HomeTeam { get; init; } = string.Empty;
    public string AwayTeam { get; init; } = string.Empty;
    public long? StartTimestamp { get; init; }
    public string StatusType { get; init; } = string.Empty;
    public string StatusDescription { get; init; } = string.Empty;
    public string TournamentName { get; init; } = string.Empty;
    public string TournamentSlug { get; init; } = string.Empty;
    public string SeasonName { get; init; } = string.Empty;
    public string SeasonYear { get; init; } = string.Empty;
    public int? Round { get; init; }

    public static IReadOnlyList<SofascoreEventSummary> FromCalendarJson(string calendarJson)
    {
        using var document = JsonDocument.Parse(calendarJson);
        if (!document.RootElement.TryGetProperty("events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
            return [];

        var events = new List<SofascoreEventSummary>();
        foreach (var eventElement in eventsElement.EnumerateArray())
        {
            var eventId = eventElement.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id)
                ? id
                : 0;

            if (eventId <= 0)
                continue;

            events.Add(new SofascoreEventSummary
            {
                EventId = eventId,
                Slug = GetString(eventElement, "slug"),
                HomeTeam = GetNestedString(eventElement, "homeTeam", "name"),
                AwayTeam = GetNestedString(eventElement, "awayTeam", "name"),
                StartTimestamp = GetNullableInt64(eventElement, "startTimestamp"),
                StatusType = GetNestedString(eventElement, "status", "type"),
                StatusDescription = GetNestedString(eventElement, "status", "description"),
                TournamentName = GetNestedString(eventElement, "tournament", "uniqueTournament", "name"),
                TournamentSlug = GetNestedString(eventElement, "tournament", "uniqueTournament", "slug"),
                SeasonName = GetNestedString(eventElement, "season", "name"),
                SeasonYear = GetNestedString(eventElement, "season", "year"),
                Round = GetNullableInt32(eventElement, "roundInfo", "round")
            });
        }

        return events;
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
                return string.Empty;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }

    private static long? GetNullableInt64(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var parsed))
            return parsed;

        return null;
    }

    private static int? GetNullableInt32(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (!current.TryGetProperty(part, out current))
                return null;
        }

        return current.TryGetInt32(out var parsed) ? parsed : null;
    }
}
