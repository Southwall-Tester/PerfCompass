namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunCpuCounterTests(Action<string, Action> check, Action<bool, string> assert, Func<List<Sample>> fixture)
    {
        CpuCounterFrame Frame(ProcessorTimes first, params ProcessorTimes[] remaining)
        { var values = new[] { first }.Concat(remaining).ToArray(); return new(values.Length, 1, values); }
        void Missing(CpuLoads load) => assert(load.Total == null && load.Maximum == null, "partial or invalid CPU counters became an observed load");
        check("CPU counter totals weight busy time across all complete processor intervals", () =>
        {
            var load = CpuLoads.Calculate(Frame(new ProcessorTimes(100, 200, 100), new ProcessorTimes(100, 200, 100)), Frame(new ProcessorTimes(100, 300, 100), new ProcessorTimes(400, 500, 100)));
            assert(load.Total == 25 && load.Maximum == 100, "per-core percentages were averaged despite different elapsed counter totals");
        });
        check("Complete CPU counters distinguish measured idle and busy from missing", () =>
        {
            var before = Frame(new ProcessorTimes(50, 100, 100));
            var idle = CpuLoads.Calculate(before, Frame(new ProcessorTimes(150, 200, 100)));
            var busy = CpuLoads.Calculate(before, Frame(new ProcessorTimes(50, 100, 200)));
            assert(idle.Total == 0 && idle.Maximum == 0 && busy.Total == 100 && busy.Maximum == 100, "real idle or busy became unknown");
            Missing(CpuLoads.Calculate(null, before)); Missing(CpuLoads.Calculate(before, null));
        });
        check("A processor without an elapsed counter cannot disappear from whole-machine CPU", () =>
        {
            var before = Frame(new ProcessorTimes(100, 100, 0), new ProcessorTimes(100, 100, 0));
            Missing(CpuLoads.Calculate(before, Frame(new ProcessorTimes(200, 200, 0), new ProcessorTimes(100, 100, 0))));
            Missing(CpuLoads.Calculate(before, Frame(new ProcessorTimes(100, 200, 0), new ProcessorTimes(100, 100, 0))));
        });
        check("Partial CPU groups cannot masquerade as all machine processors", () =>
        {
            var values = Enumerable.Repeat(new ProcessorTimes(100, 100, 0), 64).ToArray();
            var after = values.Select(v => v with { Kernel = 200, Idle = 150 }).ToArray();
            Missing(CpuLoads.Calculate(new(65, 2, values), new(65, 2, after)));
            var all = values.Append(new ProcessorTimes(100, 100, 0)).ToArray();
            var full = CpuLoads.Calculate(new(65, 2, all), new(65, 2, all.Select(v => v with { Kernel = 200, Idle = 150 }).ToArray()));
            assert(full.Total == 50 && full.Maximum == 50, "complete multi-group counters were silently truncated or rejected");
        });
        check("Changing or unknown CPU topology requires a fresh baseline", () =>
        {
            var before = Frame(new ProcessorTimes(100, 100, 0)); var after = Frame(new ProcessorTimes(150, 200, 0));
            Missing(CpuLoads.Calculate(before, after with { ProcessorGroups = 2 }));
            Missing(CpuLoads.Calculate(before, after with { ProcessorGroups = 0 }));
            Missing(CpuLoads.Calculate(before, after with { ExpectedProcessors = 0 }));
            Missing(CpuLoads.Calculate(before, Frame(new ProcessorTimes(150, 200, 0), new ProcessorTimes(150, 200, 0))));
        });
        check("CPU counter regressions cannot be clamped into zero or full load", () =>
        {
            var before = Frame(new ProcessorTimes(50, 100, 100));
            foreach (var after in new[] { new ProcessorTimes(49, 200, 200), new ProcessorTimes(100, 99, 200), new ProcessorTimes(100, 200, 99), new ProcessorTimes(-1, 200, 200) })
                Missing(CpuLoads.Calculate(before, Frame(after)));
            Missing(CpuLoads.Calculate(Frame(new ProcessorTimes(-1, 100, 100)), Frame(new ProcessorTimes(50, 200, 200))));
        });
        check("Impossible CPU idle time is unknown instead of a clipped percentage", () =>
        {
            Missing(CpuLoads.Calculate(Frame(new ProcessorTimes(50, 100, 100)), Frame(new ProcessorTimes(300, 200, 200))));
            Missing(CpuLoads.Calculate(Frame(new ProcessorTimes(50, 100, 100)), Frame(new ProcessorTimes(50, 100, 100))));
        });
        check("Large cumulative CPU counters are subtracted before summing", () =>
        {
            long large = long.MaxValue - 10000;
            var load = CpuLoads.Calculate(Frame(new ProcessorTimes(large - 10000, large, large)), Frame(new ProcessorTimes(large - 9000, large + 1000, large + 1000)));
            assert(load.Total == 50 && load.Maximum == 50, "cumulative counters overflowed before computing their small deltas");
        });
        check("A fresh monotonic CPU interval recovers after a rejected regression", () =>
        {
            var old = Frame(new ProcessorTimes(100, 100, 100)); var reset = Frame(new ProcessorTimes(10, 20, 10)); var next = Frame(new ProcessorTimes(60, 120, 10));
            Missing(CpuLoads.Calculate(old, reset));
            assert(CpuLoads.Calculate(reset, next).Total == 50, "invalid baseline contaminated the next complete interval");
        });
        check("Incomplete CPU counters reduce merged coverage without cancelling memory evidence", () =>
        {
            var before = Frame(new ProcessorTimes(100, 100, 0), new ProcessorTimes(100, 100, 0));
            var valid = CpuLoads.Calculate(before, Frame(new ProcessorTimes(150, 200, 0), new ProcessorTimes(150, 200, 0)));
            var incomplete = CpuLoads.Calculate(before, Frame(new ProcessorTimes(150, 200, 0), new ProcessorTimes(100, 100, 0)));
            var trace = fixture().Select(s => SampleBatch.Merge(Enumerable.Range(0, 5).Select(i => s with
            { Time = s.Time.AddSeconds(-i * 2), Seconds = 2, Cpu = i == 0 ? incomplete.Total : valid.Total, CoreMax = i == 0 ? incomplete.Maximum : valid.Maximum }).ToArray())).ToArray();
            assert(trace.All(s => s.Seconds == 10 && s.CpuCoverage == 8 && s.CoreCoverage == 8 && s.MemoryCoverage == 10), "unknown CPU gained coverage or suppressed measured memory");
            var report = AnalysisEngine.Analyze(trace, new Settings(Representative: true) { RepresentativeHardwareId = "test-machine", RepresentativeObservationVersion = ActivityRules.Version }, 7);
            assert(report.Cards.Single(c => c.Key == "cpu").Status == "unknown" && report.Cards.Single(c => c.Key == "memory").Status == "surplus", "partial CPU became whole-machine configuration evidence");
        });
    }
}
