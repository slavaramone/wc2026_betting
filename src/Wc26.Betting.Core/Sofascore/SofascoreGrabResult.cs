namespace Wc26.Betting.Core.Sofascore;

public sealed record SofascoreGrabResult(
    string Status,
    string OutputPath,
    string Message,
    int RoundsDownloaded,
    int EventsDiscovered,
    int FilesWritten,
    int FilesSkipped,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures);
