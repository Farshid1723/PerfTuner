using System.Diagnostics;
using PerfTuner.Client;
using PerfTuner.Contracts;
using PerfTuner.Platform.Windows.Tuning;

/// <summary>
/// End-to-end tuning test against a running service. Starts a harmless, window-less
/// "ping localhost" child process, applies every reversible action through the service,
/// verifies the effect with Win32 APIs, reverts it and verifies the original value is back.
/// Also checks the policy (protected processes refused) and the Changes/Revert-all flow.
/// </summary>
internal static class TuningSelfTest
{
    public static async Task<int> RunAsync(bool dev)
    {
        await using var client = await PerfTunerClient.ConnectAsync(new ClientOptions { DevMode = dev, ClientName = "perftuner-cli" });
        if (client.Session!.Role != ClientRole.Admin)
        {
            Console.WriteLine("tuning self-test needs the Admin role (elevated prompt, or --dev against your own console instance)");
            return 2;
        }

        var status = await client.GetStatusAsync();
        if (!status.TuningAllowed)
        {
            Console.WriteLine($"Circuit breaker is {status.Breaker}: {status.BreakerReason}");
            Console.WriteLine("Reversible tuning is paused by design (e.g. after an unclean shutdown). Wait for the cooldown or reset the breaker on the Changes tab, then rerun.");
            return 4;
        }

        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
            if (!ok) { failures++; }
        }

        using var target = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        var pid = target.Id;
        var create = ProcessInspector.GetCreateTime(pid) ?? 0;
        Console.WriteLine($"target: ping.exe pid {pid}");

        async Task<ActionResultResponse> Act(ProcessAction action, Func<ProcessActionRequest, ProcessActionRequest> configure)
        {
            await Task.Delay(2100); // respect the per-process manual rate limit
            return await client.ProcessActionAsync(configure(new ProcessActionRequest { Id = 0, Pid = pid, CreateTime = create, Action = action }));
        }

        async Task Revert(long? id)
        {
            if (id is { } changeId) { await client.RevertChangeAsync(changeId); }
        }

        // 1. priority
        var before = ProcessInspector.Priority(pid);
        var r = await Act(ProcessAction.SetPriority, t => t with { Priority = PriorityLevel.BelowNormal });
        Check("priority → Below normal", r.Success && ProcessInspector.Priority(pid) == "Below normal", r.Message);
        await Revert(r.ChangeId);
        Check("priority reverted", ProcessInspector.Priority(pid) == before, ProcessInspector.Priority(pid));

        // 2. efficiency mode
        var ptBefore = ProcessInspector.PowerThrottling(pid);
        r = await Act(ProcessAction.EfficiencyMode, t => t with { Enable = true });
        var pt = ProcessInspector.PowerThrottling(pid);
        Check("efficiency mode on (EcoQoS)", r.Success && pt is { State: 1 } && ProcessInspector.Priority(pid) == "Below normal", r.Message);
        await Revert(r.ChangeId);
        Check("efficiency mode reverted", ProcessInspector.PowerThrottling(pid) == ptBefore && ProcessInspector.Priority(pid) == before);

        // 3. CPU cores
        r = await Act(ProcessAction.CpuCores, t => t with { Cores = CpuCorePreset.HalfCores });
        if (!r.Success && r.Message.Contains("not available on this CPU", StringComparison.Ordinal))
        {
            // Small VMs (e.g. 2-core CI runners) have no meaningful "half": the product refuses, correctly.
            Console.WriteLine($"SKIP  CPU sets → half the cores  [{r.Message}]");
        }
        else
        {
            Check("CPU sets → half the cores", r.Success && ProcessInspector.CpuSetCount(pid) > 0, r.Message);
            await Revert(r.ChangeId);
            Check("CPU sets reverted", ProcessInspector.CpuSetCount(pid) == 0);
        }

        // 4. memory priority
        var mpBefore = ProcessInspector.MemoryPriority(pid);
        r = await Act(ProcessAction.MemoryPriority, t => t with { MemoryPriority = MemoryPriorityLevel.Low });
        Check("memory priority → low", r.Success && ProcessInspector.MemoryPriority(pid) == 2, r.Message);
        await Revert(r.ChangeId);
        Check("memory priority reverted", ProcessInspector.MemoryPriority(pid) == mpBefore);

        // 5. soft memory cap
        var wsBefore = ProcessInspector.WorkingSetLimits(pid);
        r = await Act(ProcessAction.SoftMemoryCap, t => t with { CapMb = 256 });
        Check("soft memory cap 256 MB", r.Success && ProcessInspector.WorkingSetLimits(pid)?.Max == 256UL * 1024 * 1024, r.Message);
        await Revert(r.ChangeId);
        Check("memory cap reverted", ProcessInspector.WorkingSetLimits(pid) == wsBefore);

        // 6. trim (one-shot)
        r = await Act(ProcessAction.TrimMemory, t => t);
        Check("trim memory", r.Success, r.Message);

        // 7. journal + revert all
        r = await Act(ProcessAction.SetPriority, t => t with { Priority = PriorityLevel.AboveNormal });
        var changes = await client.GetChangesAsync();
        Check("change listed on Changes", changes.Active.Any(c => c.Id == r.ChangeId));
        var all = await client.RevertAllAsync();
        Check("revert all restores original", all.Success && ProcessInspector.Priority(pid) == before, all.Message);

        // 8. policy: protected processes are refused
        var list = await client.GetProcessesAsync(ProtocolConstants.MaxProcessListCount);
        var protectedProc = list.Processes.FirstOrDefault(p => !p.Controllable && p.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            ?? list.Processes.First(p => !p.Controllable && p.Pid > 4);
        var refused = await client.ProcessActionAsync(new ProcessActionRequest
        {
            Id = 0, Pid = protectedProc.Pid, CreateTime = protectedProc.CreateTime, Action = ProcessAction.SetPriority, Priority = PriorityLevel.BelowNormal,
        });
        Check($"protected process refused ({protectedProc.Name})", !refused.Success, refused.Message);

        // 9. PID-reuse guard: a wrong creation time is refused
        var wrong = await client.ProcessActionAsync(new ProcessActionRequest { Id = 0, Pid = pid, CreateTime = create + 1, Action = ProcessAction.TrimMemory });
        Check("wrong process identity refused", !wrong.Success, wrong.Message);

        // 10. end process (irreversible, confirmed)
        r = await Act(ProcessAction.EndProcess, t => t with { Confirm = true });
        Check("end process (confirmed)", r.Success && target.WaitForExit(3000), r.Message);

        Console.WriteLine(failures == 0 ? "tuning self-test: all checks passed" : $"tuning self-test: {failures} check(s) FAILED");
        return failures == 0 ? 0 : 3;
    }
}
