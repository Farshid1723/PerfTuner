namespace PerfTuner.Core.Safety;

/// <summary>
/// A single tuning change that knows how to undo itself. Implementations capture the
/// ORIGINAL value at apply time and verify target identity (PID + process creation time)
/// before reverting, so a recycled PID is never touched.
/// </summary>
public interface IReversibleChange
{
    /// <summary>Stable machine-readable kind, e.g. "priority", "efficiency".</summary>
    string Kind { get; }

    /// <summary>Human-readable, log-safe description.</summary>
    string Description { get; }

    /// <summary>Target process id (0 if the change is not process-scoped).</summary>
    int TargetPid { get; }

    /// <summary>False once the target process has exited (the change died with it).</summary>
    bool IsTargetAlive();

    /// <summary>Restores the original state. Must be idempotent and never throw.</summary>
    RevertOutcome Revert();

    /// <summary>Serializable form, persisted so a crash can be rolled back on next start.</summary>
    ChangeRecord ToRecord();
}

public enum RevertOutcome
{
    Reverted,
    /// <summary>Target no longer exists (process exited) — nothing left to undo.</summary>
    TargetGone,
    Failed,
}

/// <summary>Persisted description of a change (enough to revert it after a crash).</summary>
public sealed record ChangeRecord
{
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public int Pid { get; set; }
    public long CreateTime { get; set; }
    public long OriginalA { get; set; }
    public long OriginalB { get; set; }
    public long OriginalC { get; set; }
    public uint[]? OriginalIds { get; set; }
}

public sealed record JournalEntry(long Id, IReversibleChange Change, string Owner, long CreatedUnixMs)
{
    public bool IsAutomatic => Owner != ChangeOwners.Manual;
}

/// <summary>Who applied a change. Automatic owners are reverted when their feature is turned off.</summary>
public static class ChangeOwners
{
    public const string Manual = "manual";
    public const string FocusForeground = "focus-foreground";
    public const string FocusBackground = "focus-background";
    public const string Quiet = "quiet";
    public const string AutoCalm = "auto-calm";
    public const string RulePrefix = "rule:";
    /// <summary>Memory governor (F3), Balanced+ profiles.</summary>
    public const string Governor = "governor";
    /// <summary>User-started experiment (F4); owned and finished by the experiment runner.</summary>
    public const string Experiment = "experiment";
    /// <summary>Autonomous experiment (Advanced profile); reverted when the profile drops below Advanced.</summary>
    public const string Autopilot = "autopilot";

    public static bool IsMode(string owner) => owner is FocusForeground or FocusBackground or Quiet;
}

public sealed record RollbackReport(int Reverted, int TargetGone, int Failed, IReadOnlyList<string> Failures);

/// <summary>
/// Bounded journal of active changes: the "last known safe state" is the state with the
/// journal empty. Full rollback undoes changes newest-first, the only order that is
/// always correct when changes overlap the same target. Every mutation invokes the
/// persistence callback so the on-disk copy is never behind reality.
/// </summary>
public sealed class ChangeJournal
{
    private readonly Lock _gate = new();
    private readonly List<JournalEntry> _active;
    private readonly int _capacity;
    private readonly Action<IReadOnlyList<ChangeRecord>>? _persist;
    private long _nextId;

    public ChangeJournal(int capacity = 256, Action<IReadOnlyList<ChangeRecord>>? persist = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _persist = persist;
        _active = new List<JournalEntry>(capacity);
    }

    public int Count
    {
        get { lock (_gate) { return _active.Count; } }
    }

    /// <summary>
    /// Records an applied change and returns its id, or -1 when the journal is full (the
    /// caller must then immediately revert the change — we never hold an un-undoable change).
    /// </summary>
    public long TryRecord(IReversibleChange change, string owner = ChangeOwners.Manual)
    {
        ArgumentNullException.ThrowIfNull(change);
        long id;
        lock (_gate)
        {
            if (_active.Count >= _capacity)
            {
                return -1;
            }
            id = ++_nextId;
            _active.Add(new JournalEntry(id, change, owner, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        Persist();
        return id;
    }

    public JournalEntry[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _active];
        }
    }

    public bool HasChange(int pid, string kind)
    {
        lock (_gate)
        {
            foreach (var e in _active)
            {
                if (e.Change.TargetPid == pid && e.Change.Kind == kind)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Reverts one change. Returns null if the id is unknown.</summary>
    public (RevertOutcome Outcome, string Description)? Revert(long id)
    {
        JournalEntry? entry = null;
        lock (_gate)
        {
            var index = _active.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return null;
            }
            entry = _active[index];
            _active.RemoveAt(index);
        }
        var outcome = SafeRevert(entry.Change);
        Persist();
        return (outcome, entry.Change.Description);
    }

    /// <summary>Undoes every active change, newest first. Never throws.</summary>
    public RollbackReport RollbackAll() => RollbackWhere(static _ => true);

    /// <summary>Undoes matching changes (e.g. everything owned by a mode being switched off), newest first.</summary>
    public RollbackReport RollbackWhere(Func<JournalEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<JournalEntry> selected = [];
        lock (_gate)
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                if (predicate(_active[i]))
                {
                    selected.Add(_active[i]);
                    _active.RemoveAt(i);
                }
            }
        }
        if (selected.Count == 0)
        {
            return new RollbackReport(0, 0, 0, []);
        }

        int reverted = 0, gone = 0, failed = 0;
        var failures = new List<string>();
        foreach (var entry in selected) // already newest-first
        {
            switch (SafeRevert(entry.Change))
            {
                case RevertOutcome.Reverted: reverted++; break;
                case RevertOutcome.TargetGone: gone++; break;
                default:
                    failed++;
                    failures.Add(entry.Change.Description);
                    break;
            }
        }
        Persist();
        return new RollbackReport(reverted, gone, failed, failures);
    }

    /// <summary>Drops entries whose target process has exited (their effect died with it).</summary>
    public int Prune()
    {
        JournalEntry[] snapshot = Snapshot();
        List<long> dead = [];
        foreach (var e in snapshot)
        {
            if (e.Change.TargetPid != 0 && !e.Change.IsTargetAlive())
            {
                dead.Add(e.Id);
            }
        }
        if (dead.Count == 0)
        {
            return 0;
        }
        lock (_gate)
        {
            _active.RemoveAll(e => dead.Contains(e.Id));
        }
        Persist();
        return dead.Count;
    }

    private static RevertOutcome SafeRevert(IReversibleChange change)
    {
        try
        {
            return change.Revert();
        }
        catch (Exception)
        {
            // Contract says Revert never throws; be defensive anyway.
            return RevertOutcome.Failed;
        }
    }

    private void Persist()
    {
        if (_persist is null)
        {
            return;
        }
        ChangeRecord[] records;
        lock (_gate)
        {
            records = new ChangeRecord[_active.Count];
            for (var i = 0; i < records.Length; i++)
            {
                records[i] = _active[i].Change.ToRecord();
            }
        }
        _persist(records);
    }
}
