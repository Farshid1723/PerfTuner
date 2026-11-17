using PerfTuner.Contracts;

namespace PerfTuner.Core.Tuning;

/// <summary>
/// The user's DESIRED tuning state (persisted). Actual changes are always reversible and
/// are rolled back whenever the service stops; on start the service re-applies this
/// desired state (if Safe Mode allows).
/// </summary>
public sealed record TuningState
{
    /// <summary>Safe Mode: nothing happens automatically — only changes the user clicks.</summary>
    public bool SafeMode { get; set; } = true;

    /// <summary>Safe ⇔ SafeMode; Balanced enables automation and the memory governor; Advanced adds autonomous experiments.</summary>
    public TuningProfile Profile { get; set; } = TuningProfile.Safe;
    public TuningMode Mode { get; set; } = TuningMode.Off;
    public bool AutoCalm { get; set; }
    public int AutoCalmThresholdPercent { get; set; } = 25;
    public List<AppRule> Rules { get; set; } = [];

    public static bool IsValid(TuningState? s, out string error)
    {
        if (s is null)
        {
            error = "null";
            return false;
        }
        if (!Enum.IsDefined(s.Mode) || !Enum.IsDefined(s.Profile))
        {
            error = "unknown mode";
            return false;
        }
        if (s.AutoCalmThresholdPercent < ProtocolConstants.MinAutoCalmThresholdPercent || s.AutoCalmThresholdPercent > ProtocolConstants.MaxAutoCalmThresholdPercent)
        {
            error = "autoCalmThresholdPercent out of range";
            return false;
        }
        return RequestValidator.TryValidateRules([.. s.Rules ?? []], out error);
    }
}
