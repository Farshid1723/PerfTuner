using System.Diagnostics;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Tuning;

/// <summary>
/// End-to-end A/B experiment test (F4/F5): runs a real "priority-below" experiment on a harmless
/// ping.exe child, checks the Baseline → Treatment → decision flow, that the change is really
/// applied during treatment, that the result lands in the audit trail, and that an experiment
/// whose target exits is aborted and rolled back. About 90 seconds.
/// </summary>
internal static class ExperimentSelfTest
{
    public static async Task<int> RunAsync(bool dev)
    {
        await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = dev, ClientName = "perftuner-cli" });
        if (client.Session!.Role != ClientRole.Admin)
        {
            Console.WriteLine("experiment self-test needs the Admin role");
            return 2;
        }
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
            if (!ok) { failures++; }
        }
        static Process Spawn() => Process.Start(new ProcessStartInfo("ping.exe", "-n 200 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;

        using var target = Spawn();
        var pid = target.Id;
        var create = ProcessInspector.GetCreateTime(pid) ?? 0;
        var before = ProcessInspector.Priority(pid);

        var bad = await client.StartExperimentAsync("trim-memory", pid, create);
        Check("non-experimentable contract refused", !bad.Success, bad.Message);

        var start = await client.StartExperimentAsync("priority-below", pid, create);
        Check("experiment started", start.Success, start.Message);
        var second = await client.StartExperimentAsync("priority-below", pid, create);
        Check("second concurrent experiment refused", !second.Success, second.Message);

        var sawTreatment = false;
        var appliedDuringTreatment = false;
        ExperimentsResponse exp = await client.GetExperimentsAsync();
        Check("baseline phase changes nothing", exp.Current?.State == ExperimentState.Baseline && ProcessInspector.Priority(pid) == before);
        // Poll often: on slow machines (CI runners) the engine samples less often, but the
        // treatment phase still lasts at least 30 s. "Applied" is checked both from the engine's
        // change list and from the process itself.
        for (var i = 0; i < 90 && exp.Current is not null; i++)
        {
            await Task.Delay(2000);
            exp = await client.GetExperimentsAsync();
            if (exp.Current?.State == ExperimentState.Treatment)
            {
                sawTreatment = true;
                var changes = await client.GetChangesAsync();
                appliedDuringTreatment |= changes.Active.Any(c => c.Owner == "experiment") || ProcessInspector.Priority(pid) != before;
            }
        }
        var rec = exp.History.FirstOrDefault();
        Check("treatment phase applied the change", sawTreatment && appliedDuringTreatment);
        Check("decision recorded in audit trail", rec is { State: ExperimentState.Accepted or ExperimentState.RolledBack, Kpis.Length: 5 },
            rec is null ? "none" : $"{rec.State}: {rec.Decision}");
        if (rec?.State == ExperimentState.RolledBack)
        {
            Check("rolled back restores original", ProcessInspector.Priority(pid) == before);
        }
        else
        {
            var changes = await client.GetChangesAsync();
            Check("accepted change is journaled (reversible)", changes.Active.Any(c => c.Owner == "experiment"));
        }
        await client.RevertAllAsync();
        Check("revert all restores original", ProcessInspector.Priority(pid) == before);

        using var victim = Spawn();
        var vpid = victim.Id;
        var r = await client.StartExperimentAsync("priority-below", vpid, ProcessInspector.GetCreateTime(vpid) ?? 0);
        Check("second experiment started", r.Success, r.Message);
        victim.Kill();
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(2000);
            exp = await client.GetExperimentsAsync();
            if (exp.Current is null) { break; }
        }
        Check("target exit aborts the experiment", exp.Current is null && exp.History.FirstOrDefault()?.State == ExperimentState.Aborted, exp.History.FirstOrDefault()?.Decision ?? "");

        target.Kill();
        Console.WriteLine(failures == 0 ? "experiment self-test: all checks passed" : $"experiment self-test: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 3;
    }
}
