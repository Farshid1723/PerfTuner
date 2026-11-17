using System.Text.Json;
using System.Text.Json.Serialization;
using PerfTuner.Contracts;
using PerfTuner.Core.Safety;
using PerfTuner.Platform.Windows;
using PerfTuner.Platform.Windows.Tuning;

namespace PerfTuner.Service.Engine;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ChangeRecord[]))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;

/// <summary>
/// Owns the circuit breaker and the change journal and ties them to the service lifecycle:
/// <list type="bullet">
/// <item>Every journal mutation is persisted (state\journal.json, atomic replace).</item>
/// <item>Start: if the previous run did not shut down cleanly (marker file present), every
///   change recorded on disk is reverted FIRST, then the breaker starts tripped so no
///   tuning happens until the system has been stable for a cooldown.</item>
/// <item>Every interval: feed health signals; a trip rolls back every active change.</item>
/// <item>Stop (service stop, uninstall, reboot): roll back everything, remove the marker.</item>
/// </list>
/// </summary>
internal sealed class SafetyCoordinator
{
    private readonly ILogger<SafetyCoordinator> _logger;
    private readonly ProductPaths _paths;
    private readonly string _journalFile;
    private readonly Lock _breakerGate = new();

    public SafetyCoordinator(RuntimeState state, ProductPaths paths, ILogger<SafetyCoordinator> logger)
    {
        _logger = logger;
        _paths = paths;
        _journalFile = Path.Combine(paths.StateDirectory, "journal.json");
        Journal = new ChangeJournal(256, PersistJournal);
        Breaker = new CircuitBreaker(state.Config.CircuitBreaker, OnTrip);
    }

    public ChangeJournal Journal { get; }
    public CircuitBreaker Breaker { get; }
    public BreakerState State => Breaker.State;
    public bool AllowsTuning => Breaker.AllowsTuning;

    /// <summary>Raised (outside locks) whenever the breaker trips, so the UI history shows it.</summary>
    public event Action<string>? Tripped;

    public void OnServiceStarting()
    {
        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);
            if (File.Exists(_paths.RunningMarker))
            {
                // Only trip when the crashed run actually had changes in effect: a crash with an empty
                // journal cannot have been caused by tuning, and tripping would just block the user.
                var pending = ReplayPersistedJournal();
                if (pending > 0)
                {
                    ForceTrip($"previous run ended uncleanly with {pending} change(s) in effect (now undone)");
                }
                else
                {
                    _logger.LogWarning("Previous run ended uncleanly with no changes in effect; nothing to recover");
                }
            }
            else if (File.Exists(_journalFile))
            {
                ReplayPersistedJournal(); // belt and braces: a clean stop leaves no journal
            }
            File.WriteAllText(_paths.RunningMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot write run marker; starting with breaker tripped");
            ForceTrip("run marker unavailable");
        }
    }

    public void Evaluate(in HealthSignals signals, long nowMs)
    {
        lock (_breakerGate)
        {
            Breaker.Evaluate(signals, nowMs);
        }
    }

    public void ForceTrip(string reason)
    {
        lock (_breakerGate)
        {
            Breaker.ForceTrip(reason, Environment.TickCount64);
        }
    }

    public void ResetBreaker()
    {
        lock (_breakerGate)
        {
            Breaker.Reset();
        }
        _logger.LogInformation("Circuit breaker reset by administrator");
    }

    public void OnServiceStopping()
    {
        var report = Journal.RollbackAll();
        _logger.LogInformation(
            "Shutdown rollback: {Reverted} reverted, {Gone} targets gone, {Failed} failed",
            report.Reverted, report.TargetGone, report.Failed);
        TryDelete(_journalFile);
        TryDelete(_paths.RunningMarker);
    }

    private void OnTrip(string reason)
    {
        var report = Journal.RollbackAll();
        _logger.LogWarning(
            "Circuit breaker {State}: {Reason}. Rolled back {Reverted} change(s), {Gone} gone, {Failed} failed",
            Breaker.State, reason, report.Reverted, report.TargetGone, report.Failed);
        foreach (var failure in report.Failures)
        {
            _logger.LogError("Rollback failed for {Change}", failure);
        }
        Tripped?.Invoke($"Circuit breaker tripped: {reason}. Rolled back {report.Reverted} change(s).");
    }

    private void PersistJournal(IReadOnlyList<ChangeRecord> records)
    {
        try
        {
            if (records.Count == 0)
            {
                TryDelete(_journalFile);
                return;
            }
            var tmp = _journalFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize([.. records], JournalJsonContext.Default.ChangeRecordArray));
            File.Move(tmp, _journalFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting failed: stop tuning rather than hold changes we might not undo after a crash.
            _logger.LogError(ex, "Cannot persist change journal; tripping breaker");
            ForceTrip("change journal not writable");
        }
    }

    /// <summary>Reverts every change a previous run left behind; returns how many there were.</summary>
    private int ReplayPersistedJournal()
    {
        ChangeRecord[] records;
        try
        {
            var info = new FileInfo(_journalFile);
            if (!info.Exists) { return 0; }
            if (info.Length > 4 * 1024 * 1024) { throw new IOException("journal too large"); }
            records = JsonSerializer.Deserialize(File.ReadAllText(_journalFile), JournalJsonContext.Default.ChangeRecordArray) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Persisted journal unreadable; changes from the previous run may remain until those processes exit");
            TryDelete(_journalFile);
            return 1; // unknown content: assume the worst
        }

        int reverted = 0, gone = 0, failed = 0;
        for (var i = records.Length - 1; i >= 0; i--)
        {
            var change = ProcessChange.FromRecord(records[i]);
            var outcome = change?.Revert() ?? RevertOutcome.Failed;
            switch (outcome)
            {
                case RevertOutcome.Reverted: reverted++; break;
                case RevertOutcome.TargetGone: gone++; break;
                default: failed++; break;
            }
        }
        _logger.LogWarning("Crash recovery: {Reverted} change(s) from the previous run reverted, {Gone} targets gone, {Failed} failed",
            reverted, gone, failed);
        TryDelete(_journalFile);
        return records.Length;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {File}", Path.GetFileName(path));
        }
    }
}
