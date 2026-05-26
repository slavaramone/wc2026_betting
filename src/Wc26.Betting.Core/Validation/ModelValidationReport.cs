namespace Wc26.Betting.Core.Validation;

public sealed class ModelValidationReport
{
    public DateTimeOffset ValidatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModelsFolder { get; init; } = string.Empty;
    public string Status => Errors.Count == 0 ? "passed" : "failed";
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Info { get; init; } = [];
}
