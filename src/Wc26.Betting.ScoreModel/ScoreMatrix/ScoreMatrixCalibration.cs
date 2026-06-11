namespace Wc26.Betting.ScoreModel.ScoreMatrix;

public enum ScoreMatrixCalibrationMode
{
    None,
    DrawMatch,
    LowScoreBoost,
    DrawAndLowScore
}

public sealed class ScoreMatrixCalibrationOptions
{
    public ScoreMatrixCalibrationMode Mode { get; init; } = ScoreMatrixCalibrationMode.None;
    public double P00Multiplier { get; init; } = 1.10d;
    public double P11Multiplier { get; init; } = 1.08d;
    public double P10Or01Multiplier { get; init; } = 1.03d;
    public double P21Or12Multiplier { get; init; } = 0.98d;

    public bool UsesLowScoreBoost => Mode is ScoreMatrixCalibrationMode.LowScoreBoost or ScoreMatrixCalibrationMode.DrawAndLowScore;
    public bool UsesDrawMatch => Mode is ScoreMatrixCalibrationMode.DrawMatch or ScoreMatrixCalibrationMode.DrawAndLowScore;

    public static ScoreMatrixCalibrationOptions None { get; } = new();

    public static ScoreMatrixCalibrationMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ScoreMatrixCalibrationMode.None;

        var normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        return normalized switch
        {
            "none" or "base" => ScoreMatrixCalibrationMode.None,
            "drawmatch" or "draw" or "drawboost" => ScoreMatrixCalibrationMode.DrawMatch,
            "lowscoreboost" or "lowscore" => ScoreMatrixCalibrationMode.LowScoreBoost,
            "drawandlowscore" or "drawmatchandlowscore" or "drawlowscore" => ScoreMatrixCalibrationMode.DrawAndLowScore,
            _ => throw new ArgumentException($"Unknown score matrix calibration mode '{value}'. Use None, DrawMatch, LowScoreBoost or DrawAndLowScore.")
        };
    }
}
