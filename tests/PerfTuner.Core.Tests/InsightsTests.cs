using System.Text;
using PerfTuner.Contracts;
using PerfTuner.Core.Insights;

namespace PerfTuner.Core.Tests;

public class LeakDetectorTests
{
    private static void Feed(LeakDetector d, int minutes, Func<int, double> privateMb, string name = "app")
    {
        for (var m = 0; m <= minutes; m++)
        {
            d.Observe(m, [(100, 1L, name, privateMb(m))]);
        }
    }

    [Fact]
    public void Steady_linear_growth_is_flagged()
    {
        var d = new LeakDetector();
        Feed(d, 30, m => 200 + (m * 2.0)); // 120 MB/hour
        var s = Assert.Single(d.Suspects());
        Assert.InRange(s.GrowthMbPerHour, 110, 130);
        Assert.True(s.RSquared > 0.99);
    }

    [Fact]
    public void Flat_usage_is_not_flagged()
    {
        var d = new LeakDetector();
        Feed(d, 60, m => 500 + ((m % 3) * 0.3));
        Assert.Empty(d.Suspects());
    }

    [Fact]
    public void Warm_up_then_plateau_is_not_flagged()
    {
        var d = new LeakDetector();
        Feed(d, 60, m => m < 20 ? 200 + (m * 10) : 400);
        Assert.Empty(d.Suspects());
    }

    [Fact]
    public void Sawtooth_garbage_collection_is_not_flagged()
    {
        var d = new LeakDetector();
        Feed(d, 60, m => 300 + ((m % 10) * 20) + (m * 0.5));
        Assert.Empty(d.Suspects());
    }

    [Fact]
    public void Too_short_observation_is_not_flagged()
    {
        var d = new LeakDetector();
        Feed(d, 10, m => 200 + (m * 10));
        Assert.Empty(d.Suspects());
    }

    [Fact]
    public void Exited_processes_are_forgotten_and_small_ones_ignored()
    {
        var d = new LeakDetector();
        d.Observe(0, [(1, 1L, "big", 100), (2, 1L, "small", 5)]);
        Assert.Equal(1, d.TrackedCount);
        d.Observe(1, []);
        Assert.Equal(0, d.TrackedCount);
    }

    [Fact]
    public void Limit_tracker_reports_share()
    {
        var t = new LimitTracker(window: 4);
        t.Observe(100);
        t.Observe(80);
        t.Observe(80);
        t.Observe(100);
        Assert.Equal(50, t.LimitedSharePercent);
        t.Observe(100); // evicts one "100"
        t.Observe(100); // evicts one "80"
        Assert.Equal(25, t.LimitedSharePercent);
    }

    [Theory]
    [InlineData("""{"type":"startLatencyScan","id":1,"seconds":20}""", true)]
    [InlineData("""{"type":"startLatencyScan","id":1,"seconds":1}""", false)]
    [InlineData("""{"type":"startLatencyScan","id":1,"seconds":600}""", false)]
    [InlineData("""{"type":"getInsights","id":1}""", true)]
    [InlineData("""{"type":"getBootReport","id":1}""", true)]
    public void Insight_requests_validate(string json, bool ok)
        => Assert.Equal(ok, MessageCodec.TryDecodeRequest(Encoding.UTF8.GetBytes(json), out _, out _, out _));
}
