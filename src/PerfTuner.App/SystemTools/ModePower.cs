using System.IO;
using PerfTuner.Platform.Windows.Settings;

namespace PerfTuner.App.SystemTools;

/// <summary>The three performance modes. Exactly one (or none) is active at any time.</summary>
public enum PerfMode { Off, Turbo, Smart, Silent }

/// <summary>
/// The power-plan half of the Turbo and Silent modes (the process half runs in the engine).
/// Every change goes through the settings journal, and the ids are remembered so switching
/// mode or turning it off restores exactly what was there before.
/// <list type="bullet">
/// <item><b>Turbo:</b> the High / Ultimate Performance plan if Windows has one; otherwise the
///   active plan gets aggressive turbo boost and a 100 % maximum.</item>
/// <item><b>Silent:</b> turbo boost off and the processor capped at 80 % — the CPU stays far
///   cooler, so fans spin down. The biggest single change to heat and noise.</item>
/// </list>
/// </summary>
public static class ModePower
{
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private const uint BoostDisabled = 0;
    private const uint BoostAggressive = 2;
    public const uint SilentMaxPercent = 80;

    /// <summary>Restores the power settings changed for the previous mode, then applies the new one. Returns a short note.</summary>
    public static string Apply(PerfMode mode, SettingsJournal journal, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var note = Restore(journal);
        if (mode is not (PerfMode.Turbo or PerfMode.Silent))
        {
            return note;
        }
        if (!elevated)
        {
            return "Power part skipped: needs administrator (Unlock full power).";
        }

        var ids = new List<long>();
        try
        {
            if (mode == PerfMode.Turbo)
            {
                var plans = PowerSettings.List();
                var fast = plans.FirstOrDefault(p => p.Id == UltimatePerformance) ?? plans.FirstOrDefault(p => p.Id == HighPerformance);
                var active = PowerSettings.GetActive();
                if (fast is not null && fast.Id != active)
                {
                    PowerSettings.SetActive(fast.Id);
                    ids.Add(Record(journal, new SettingChange { Kind = SettingKind.PowerPlan, Scheme = active, Description = $"Turbo: power plan → {fast.Name}" }));
                    return Remember(ids, $"power plan: {fast.Name}");
                }
                var before = PowerSettings.ReadProcessor(active);
                PowerSettings.WriteProcessor(active, before with { BoostAc = BoostAggressive, MaxAc = 100 });
                ids.Add(RecordProcessor(journal, active, before, "Turbo: aggressive turbo boost, CPU max 100 % (plugged in)"));
                return Remember(ids, "aggressive turbo boost");
            }

            var scheme = PowerSettings.GetActive();
            var prev = PowerSettings.ReadProcessor(scheme);
            PowerSettings.WriteProcessor(scheme, prev with
            {
                BoostAc = BoostDisabled, BoostDc = BoostDisabled,
                MaxAc = Math.Min(prev.MaxAc, SilentMaxPercent), MaxDc = Math.Min(prev.MaxDc, SilentMaxPercent),
            });
            ids.Add(RecordProcessor(journal, scheme, prev, $"Silent: turbo boost off, CPU max {SilentMaxPercent} %"));
            return Remember(ids, $"turbo boost off, CPU capped at {SilentMaxPercent} %");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Remember(ids, "");
            return "Power part failed: " + ex.Message;
        }
    }

    /// <summary>Undoes the power changes of the current mode (on mode switch, Off, and uninstall).</summary>
    public static string Restore(SettingsJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var ids = GuiSettings.Current.ModePowerIds ?? [];
        if (ids.Length == 0) { return ""; }
        var restored = 0;
        foreach (var id in ids.Reverse())
        {
            try
            {
                if (journal.Entries.Any(e => e.Id == id)) { journal.Revert(id); restored++; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                // Left in the journal: still undoable from History.
            }
        }
        GuiSettings.Update(p => p with { ModePowerIds = [] });
        return restored > 0 ? "power settings restored" : "";
    }

    private static string Remember(List<long> ids, string note)
    {
        GuiSettings.Update(p => p with { ModePowerIds = [.. ids] });
        return note;
    }

    private static long Record(SettingsJournal journal, SettingChange change)
    {
        journal.Record(change);
        return change.Id;
    }

    private static long RecordProcessor(SettingsJournal journal, Guid scheme, ProcessorPower before, string description) =>
        Record(journal, new SettingChange
        {
            Kind = SettingKind.ProcessorPower, Scheme = scheme,
            ProcessorValues = [before.BoostAc, before.BoostDc, before.MinAc, before.MinDc, before.MaxAc, before.MaxDc],
            Description = description,
        });
}
