using System.Globalization;
using System.Text;

namespace Wc26.Betting.Core.Models;

public sealed record TeamConfederationInfo(string Team, string Confederation);

public static class TeamConfederationCatalog
{
    private static readonly IReadOnlyList<TeamConfederationInfo> Items =
    [
        new("Austria", "UEFA"),
        new("Belgium", "UEFA"),
        new("Bosnia & Herzegovina", "UEFA"),
        new("Croatia", "UEFA"),
        new("Czechia", "UEFA"),
        new("England", "UEFA"),
        new("France", "UEFA"),
        new("Germany", "UEFA"),
        new("Netherlands", "UEFA"),
        new("Norway", "UEFA"),
        new("Portugal", "UEFA"),
        new("Scotland", "UEFA"),
        new("Spain", "UEFA"),
        new("Sweden", "UEFA"),
        new("Switzerland", "UEFA"),
        new("Türkiye", "UEFA"),

        new("Argentina", "CONMEBOL"),
        new("Brazil", "CONMEBOL"),
        new("Colombia", "CONMEBOL"),
        new("Ecuador", "CONMEBOL"),
        new("Paraguay", "CONMEBOL"),
        new("Uruguay", "CONMEBOL"),

        new("Algeria", "CAF"),
        new("Cabo Verde", "CAF"),
        new("Côte d'Ivoire", "CAF"),
        new("DR Congo", "CAF"),
        new("Egypt", "CAF"),
        new("Ghana", "CAF"),
        new("Morocco", "CAF"),
        new("Senegal", "CAF"),
        new("South Africa", "CAF"),
        new("Tunisia", "CAF"),

        new("Australia", "AFC"),
        new("Iran", "AFC"),
        new("Iraq", "AFC"),
        new("Japan", "AFC"),
        new("Jordan", "AFC"),
        new("Qatar", "AFC"),
        new("Saudi Arabia", "AFC"),
        new("South Korea", "AFC"),
        new("Uzbekistan", "AFC"),

        new("Canada", "CONCACAF"),
        new("Curaçao", "CONCACAF"),
        new("Haiti", "CONCACAF"),
        new("Mexico", "CONCACAF"),
        new("Panama", "CONCACAF"),
        new("USA", "CONCACAF"),

        new("New Zealand", "OFC")
    ];

    private static readonly IReadOnlyDictionary<string, TeamConfederationInfo> ByTeam = Items
        .SelectMany(x => Aliases(x.Team).Select(alias => new { Key = NormalizeTeam(alias), Value = x }))
        .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TeamConfederationInfo> All => Items;

    public static TeamConfederationInfo? TryGetByTeam(string team)
        => ByTeam.TryGetValue(NormalizeTeam(team), out var value) ? value : null;

    public static string NormalizeConfederation(string value)
    {
        var v = NormalizeText(value);
        return v switch
        {
            "EUROPE" or "UEFA" => "UEFA",
            "SOUTHAMERICA" or "CONMEBOL" => "CONMEBOL",
            "AFRICA" or "CAF" => "CAF",
            "ASIA" or "AFC" => "AFC",
            "NORTHAMERICA" or "CONCACAF" => "CONCACAF",
            "OCEANIA" or "OFC" => "OFC",
            _ => value.Trim().ToUpperInvariant()
        };
    }

    public static string NormalizeTeam(string value)
    {
        var v = NormalizeText(value);
        return v switch
        {
            "UNITEDSTATES" or "UNITEDSTATESOFAMERICA" or "USA" => "USA",
            "TURKEY" or "TURKIYE" => "TURKIYE",
            "CZECHREPUBLIC" or "CZECHIA" => "CZECHIA",
            "KOREAREPUBLIC" or "SOUTHKOREA" => "SOUTHKOREA",
            "BOSNIAANDHERZEGOVINA" or "BOSNIAHERZEGOVINA" or "BOSNIA&HERZEGOVINA" => "BOSNIAHERZEGOVINA",
            "IVORYCOAST" or "COTEDIVOIRE" or "COTEIVOIRE" => "COTEDIVOIRE",
            "CONGODR" or "DRCONGO" or "DEMOCRATICREPUBLICOFCONGO" => "DRCONGO",
            "CAPEVERDE" or "CABOVERDE" or "CAPEVERDEISLANDS" => "CABOVERDE",
            "NETHERLANDS" or "HOLLAND" => "NETHERLANDS",
            "CURACAO" or "CURAÇAO" => "CURACAO",
            _ => v
        };
    }

    private static IEnumerable<string> Aliases(string team)
    {
        yield return team;
        if (team == "USA") { yield return "United States"; yield return "United States of America"; }
        if (team == "Türkiye") yield return "Turkey";
        if (team == "Czechia") yield return "Czech Republic";
        if (team == "South Korea") yield return "Korea Republic";
        if (team == "Bosnia & Herzegovina") yield return "Bosnia and Herzegovina";
        if (team == "Côte d'Ivoire") { yield return "Ivory Coast"; yield return "Cote d'Ivoire"; }
        if (team == "DR Congo") yield return "Congo DR";
        if (team == "Cabo Verde") { yield return "Cape Verde"; yield return "Cape Verde Islands"; }
        if (team == "Netherlands") yield return "Holland";
        if (team == "Curaçao") yield return "Curacao";
    }

    private static string NormalizeText(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(ch) || ch == '&')
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
