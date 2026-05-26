using System.Text.Json;

namespace Wc26.Betting.Core.Sofascore;

public sealed class SofascoreJsonFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<FileWriteResult> WriteJsonAsync(string path, string json, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        if (File.Exists(path) && !overwrite)
            return FileWriteResult.Skipped(path);

        await File.WriteAllTextAsync(path, PrettyPrintJson(json), cancellationToken);
        return FileWriteResult.Written(path);
    }

    public async Task<FileWriteResult> WriteObjectAsync<T>(string path, T value, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        if (File.Exists(path) && !overwrite)
            return FileWriteResult.Skipped(path);

        var json = JsonSerializer.Serialize(value, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return FileWriteResult.Written(path);
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

public sealed record FileWriteResult(string Path, bool WasWritten)
{
    public static FileWriteResult Written(string path) => new(path, true);
    public static FileWriteResult Skipped(string path) => new(path, false);
}
