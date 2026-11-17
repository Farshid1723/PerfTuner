using System.Text;
using PerfTuner.Contracts;
using PerfTuner.Core.Safety;
using PerfTuner.Core.Tuning;

namespace PerfTuner.Core.Tests;

public class ProcessPolicyTests
{
    [Theory]
    [InlineData(4, "System", 0)]
    [InlineData(700, "svchost", 1)]
    [InlineData(800, "lsass", 1)]
    [InlineData(900, "explorer", 1)]
    [InlineData(1000, "chrome", 0)]   // session 0
    public void Refuses_protected_targets(int pid, string name, int session)
        => Assert.False(ProcessPolicy.CanControl(pid, name, session, selfPid: 1, out _));

    [Fact]
    public void Allows_user_apps() => Assert.True(ProcessPolicy.CanControl(1234, "chrome", 1, 1, out _));

    [Fact]
    public void Refuses_self() => Assert.False(ProcessPolicy.CanControl(1234, "anything", 1, 1234, out _));
}

public class ActionThrottleTests
{
    [Fact]
    public void Manual_actions_are_spaced_per_process()
    {
        var t = new ActionThrottle();
        Assert.True(t.TryAcquire(10, automatic: false, 0));
        Assert.False(t.TryAcquire(10, automatic: false, 1_000));
        Assert.True(t.TryAcquire(11, automatic: false, 1_000));
        Assert.True(t.TryAcquire(10, automatic: false, 2_000));
    }

    [Fact]
    public void Automatic_actions_are_globally_capped()
    {
        var t = new ActionThrottle();
        for (var pid = 1; pid <= 20; pid++)
        {
            Assert.True(t.TryAcquire(pid, automatic: true, 0));
        }
        Assert.False(t.TryAcquire(99, automatic: true, 0));
        Assert.True(t.TryAcquire(99, automatic: true, 61_000));
    }
}

public class ProcessActionValidationTests
{
    private static bool Decode(string json) => MessageCodec.TryDecodeRequest(Encoding.UTF8.GetBytes(json), out _, out _, out _);

    [Theory]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SetPriority","priority":"AboveNormal"}""")]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"EfficiencyMode","enable":true}""")]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SoftMemoryCap","capMb":512}""")]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"TrimMemory"}""")]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"EndProcess","confirm":true}""")]
    [InlineData("""{"type":"setTuningOptions","id":1,"mode":"Focus"}""")]
    [InlineData("""{"type":"setRules","id":1,"rules":[{"imageName":"OneDrive","efficiencyMode":true}]}""")]
    public void Accepts_well_formed(string json) => Assert.True(Decode(json));

    [Theory]
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SetPriority","priority":"Realtime"}""")]   // never
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SetPriority"}""")]                         // missing param
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SetPriority","priority":"Normal","capMb":64}""")] // extra param
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"SoftMemoryCap","capMb":1}""")]              // below floor
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"EndProcess"}""")]                          // no confirm
    [InlineData("""{"type":"processAction","id":1,"pid":1000,"createTime":5,"action":"EndProcess","confirm":false}""")]
    [InlineData("""{"type":"processAction","id":1,"pid":4,"createTime":5,"action":"TrimMemory"}""")]                             // System
    [InlineData("""{"type":"setTuningOptions","id":1,"autoCalmThresholdPercent":1}""")]
    [InlineData("""{"type":"setRules","id":1,"rules":[{"imageName":"..\\evil","efficiencyMode":true}]}""")]
    [InlineData("""{"type":"setRules","id":1,"rules":[{"imageName":"a"}]}""")]                                                   // no action
    [InlineData("""{"type":"setRules","id":1,"rules":[{"imageName":"a","priority":"Normal"},{"imageName":"A","priority":"Normal"}]}""")]
    public void Rejects_malformed(string json) => Assert.False(Decode(json));
}

public class AutomationPlannerTests
{
    private static ProcView P(int pid, double cpu, string name = "app", bool ok = true) => new(pid, 1, name + pid, cpu, ok);

    private static PlanInput Input(TuningState s, ProcView[] procs, int? fg, ManagedView[]? managed = null, long now = 0, bool allowed = true)
        => new(now, s, procs, fg, managed ?? [], allowed);

    [Fact]
    public void Safe_mode_applies_nothing_and_reverts_automatic_changes()
    {
        var s = new TuningState { SafeMode = true, Mode = TuningMode.Quiet };
        var plan = new AutomationPlanner().Plan(Input(s, [P(10, 50)], fg: 1,
            [new ManagedView(7, 10, ChangeOwners.Quiet), new ManagedView(8, 11, ChangeOwners.Manual)]));
        Assert.Single(plan);
        Assert.Equal(PlanKind.Revert, plan[0].Kind);
        Assert.Equal(7, plan[0].ChangeId);
    }

    [Fact]
    public void Focus_boosts_foreground_and_calms_busy_background()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Focus };
        var plan = new AutomationPlanner().Plan(Input(s, [P(10, 30), P(11, 5), P(12, 0.5), P(13, 20, ok: false)], fg: 11));
        Assert.Contains(plan, a => a.Kind == PlanKind.ApplyBoost && a.Process.Pid == 11);
        Assert.Contains(plan, a => a.Kind == PlanKind.ApplyEfficiency && a.Process.Pid == 10);
        Assert.DoesNotContain(plan, a => a.Process.Pid == 12);   // idle
        Assert.DoesNotContain(plan, a => a.Process.Pid == 13);   // protected
    }

    [Fact]
    public void Unknown_foreground_means_no_new_throttling()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Quiet };
        Assert.Empty(new AutomationPlanner().Plan(Input(s, [P(10, 50)], fg: null)));
    }

    [Fact]
    public void Throttle_is_released_when_app_comes_to_foreground()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Quiet };
        var plan = new AutomationPlanner().Plan(Input(s, [P(10, 50)], fg: 10, [new ManagedView(3, 10, ChangeOwners.Quiet)]));
        Assert.Contains(plan, a => a.Kind == PlanKind.Revert && a.ChangeId == 3);
        Assert.DoesNotContain(plan, a => a.Kind == PlanKind.ApplyEfficiency);
    }

    [Fact]
    public void Mode_off_reverts_mode_changes_only()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Off };
        var plan = new AutomationPlanner().Plan(Input(s, [], fg: 1,
            [new ManagedView(1, 10, ChangeOwners.FocusBackground), new ManagedView(2, 11, ChangeOwners.Manual)]));
        Assert.Single(plan);
        Assert.Equal(1, plan[0].ChangeId);
    }

    [Fact]
    public void Breaker_open_blocks_new_actions()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Quiet };
        Assert.Empty(new AutomationPlanner().Plan(Input(s, [P(10, 50)], fg: 1, allowed: false)));
    }

    [Fact]
    public void Quiet_mode_respects_cap()
    {
        var s = new TuningState { SafeMode = false, Mode = TuningMode.Quiet };
        var procs = Enumerable.Range(100, 100).Select(pid => P(pid, 10)).ToArray();
        var plan = new AutomationPlanner().Plan(Input(s, procs, fg: 1));
        Assert.Equal(AutomationPlanner.QuietCap, plan.Count(a => a.Kind == PlanKind.ApplyEfficiency));
    }

    [Fact]
    public void Auto_calm_needs_sustained_load_then_releases_after_quiet_period()
    {
        var s = new TuningState { SafeMode = false, AutoCalm = true, AutoCalmThresholdPercent = 25 };
        var planner = new AutomationPlanner();
        Assert.Empty(planner.Plan(Input(s, [P(10, 40)], fg: 1, now: 0)));
        Assert.Empty(planner.Plan(Input(s, [P(10, 40)], fg: 1, now: 29_000)));
        var plan = planner.Plan(Input(s, [P(10, 40)], fg: 1, now: 30_000));
        Assert.Contains(plan, a => a.Kind == PlanKind.ApplyEfficiency && a.Owner == ChangeOwners.AutoCalm);

        var managed = new[] { new ManagedView(5, 10, ChangeOwners.AutoCalm) };
        Assert.Empty(planner.Plan(Input(s, [P(10, 2)], fg: 1, managed, now: 40_000)));
        var release = planner.Plan(Input(s, [P(10, 2)], fg: 1, managed, now: 160_000));
        Assert.Contains(release, a => a.Kind == PlanKind.Revert && a.ChangeId == 5);
    }

    [Fact]
    public void Rules_apply_to_matching_apps_even_in_foreground()
    {
        var s = new TuningState { SafeMode = false, Rules = [new AppRule { ImageName = "app10", Priority = PriorityLevel.BelowNormal }] };
        var plan = new AutomationPlanner().Plan(Input(s, [P(10, 0)], fg: 10));
        Assert.Contains(plan, a => a.Kind == PlanKind.ApplyRule && a.Process.Pid == 10);
    }

    [Fact]
    public void Tuning_state_validation()
    {
        Assert.True(TuningState.IsValid(new TuningState(), out _));
        Assert.False(TuningState.IsValid(new TuningState { AutoCalmThresholdPercent = 0 }, out _));
    }
}
