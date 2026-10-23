using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunSamplingClockTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var origin = DateTimeOffset.Now.AddMinutes(-10);
        SamplingInstant At(double wall, double elapsed) => new(origin.AddSeconds(wall), (long)Math.Round(elapsed * Stopwatch.Frequency));
        check("Sampling uses both elapsed and calendar spans without inflating small clock drift", () =>
        {
            var clock = new SamplingClock(At(0, 0));
            var normal = clock.Next(At(2, 2)); var forward = clock.Next(At(4.1, 4)); var back = clock.Next(At(6, 6));
            assert(normal.Seconds == 2 && forward.Seconds == 2 && Math.Abs(back.Seconds - 1.9) < .000001
                && new[] { normal, forward, back }.All(t => t.Issue == ""), "wall-clock drift became additional measured time");
        });
        check("Short forward and backward clock steps cannot manufacture observed seconds", () =>
        {
            foreach (double jump in new[] { -3600d, -5, -1, .5, 5, 3600 })
            {
                var clock = new SamplingClock(At(0, 0)); var changed = clock.Next(At(2 + jump, 2));
                var recovered = clock.Next(At(4 + jump, 4));
                assert(changed.Seconds == 0 && changed.Issue == "clock-change" && recovered.Seconds == 2 && recovered.Issue == "", "clock step inherited time or prevented a new baseline");
            }
        });
        check("Small clock drift across many merged batches cannot create overlapping history", () =>
        {
            var clock = new SamplingClock(At(0, 0)); var audit = new HistoryAudit(); var pending = new List<Sample>();
            double wall = 0, seconds = 0, unique = 0;
            for (int i = 1; i <= 2000; i++)
            {
                wall += 2 + Math.Sin(i) * .0001; var tick = clock.Next(At(wall, i * 2));
                assert(tick.Issue == "" && tick.Seconds > 0, "ordinary small drift became a gap");
                pending.Add(new() { Time = tick.Time, Seconds = tick.Seconds, Active = true, HardwareId = "clock" }); seconds += tick.Seconds;
                if (pending.Count == 5)
                {
                    var covered = audit.Cover(SampleBatch.Merge(pending)); pending.Clear();
                    assert(!covered.Overlap, "duration rounding manufactured overlapping adjacent batches"); unique += covered.Total;
                }
            }
            assert(seconds <= 4000 && Math.Abs(unique - seconds) < .0001, "drift manufactured or erased measured duration");
        });
        check("Long polling gaps and invalid elapsed counters remain unobserved and recover", () =>
        {
            foreach (double gap in new[] { 15.01, 60, 7200 })
            {
                var clock = new SamplingClock(At(0, 0)); var missed = clock.Next(At(gap, gap)); var next = clock.Next(At(gap + 2, gap + 2));
                assert(missed.Seconds == 0 && missed.Issue == "poll-gap" && next.Seconds == 2, "suspend-like gap counted as load or idle");
            }
            var invalid = new SamplingClock(At(0, 2));
            assert(invalid.Next(At(2, 2)).Issue == "invalid-elapsed" && invalid.Next(At(4, 1)).Seconds == 0, "invalid counter accepted");
        });
        check("Reset and offset changes establish a fresh baseline without counting paused time", () =>
        {
            var clock = new SamplingClock(At(0, 0)); clock.Reset(At(600, 600));
            var differentOffset = origin.Offset == TimeSpan.Zero ? TimeSpan.FromHours(1) : TimeSpan.Zero;
            var tick = clock.Next(At(602, 602) with { Time = origin.AddSeconds(602).ToOffset(differentOffset) });
            assert(tick.Seconds == 2 && tick.Issue == "", "pause or a UTC-equivalent time-zone offset became elapsed usage");
        });
        check("Elapsed schedules keep their cadence independently of calendar clock changes", () =>
        {
            var schedule = new ElapsedSchedule(TimeSpan.FromSeconds(60));
            assert(schedule.Due(At(0, 0).Timestamp), "initial analysis was delayed"); schedule.Mark(At(0, 0).Timestamp);
            assert(!schedule.Due(At(7200, 59).Timestamp) && schedule.Due(At(-7200, 60).Timestamp), "calendar movement postponed or accelerated elapsed cadence");
            schedule.Mark(At(-7200, 60).Timestamp); assert(!schedule.Due(At(0, 61).Timestamp), "completed refresh did not reset cadence");
            assert(schedule.Due(At(0, 1).Timestamp), "an elapsed-counter reset postponed refresh indefinitely");
        });
        check("Sampling interruption records stay separate from both adjacent measured batches", () =>
        {
            var before = new Sample { Time = origin, Seconds = 2, Active = true }; var gap = before with { Seconds = 0, SamplingIssue = "poll-gap" };
            assert(SampleBatch.MustFlushBefore([before], gap) && SampleBatch.MustFlushBefore([gap], before), "gap blended into an adjacent batch");
            bool refused = false; try { SampleBatch.Merge([before, gap]); } catch (ArgumentException) { refused = true; }
            assert(refused && SampleBatch.Merge([gap]).SamplingIssue == "poll-gap", "merge hid interruption provenance");
        });
        check("Stored timing gaps preserve provenance without adding active or idle exposure", () =>
        {
            var store = new LocalStore(Path.Combine(Path.GetTempPath(), "PerfCompass-clock-" + Guid.NewGuid().ToString("N")));
            var normal = new Sample { Time = origin, HardwareId = "clock-test", Scene = "task", Seconds = 2, Active = true, Cpu = 5 };
            store.Append(normal); store.Append(normal with { Time = origin.AddSeconds(60), Seconds = 0, SamplingIssue = "poll-gap", Cpu = 100 });
            var report = HistoryAnalysis.Analyze(store, new(), 7, hardwareId: "clock-test");
            assert(report.ActiveHours == 2d / 3600 && report.SamplingGapCount == 1 && !report.HistoryQuality.HasIssues
                && report.Cards.All(c => c.Status != "pressure") && report.DemandTrends.All(t => t.RecentBusyMinutes == 0)
                && report.History.All(p => p.Cpu == 5), "gap manufactured exposure or chart load");
            assert(ReportExporter.Html(report, null).Contains("采样中断"), "HTML lost timing provenance");
            string csv = Path.Combine(store.Root, "export.csv"); ReportExporter.Csv(csv, store.Read(7));
            assert(File.ReadAllText(csv).Contains("sampling_issue") && File.ReadAllText(csv).Contains("poll-gap"), "CSV lost timing provenance");
            store.Append(normal with { Time = origin.AddSeconds(62), SamplingIssue = "clock-change" });
            var invalid = HistoryAnalysis.Analyze(store, new(), 7, hardwareId: "clock-test");
            assert(invalid.HistoryQuality.HasIssues && invalid.ActiveHours == 2d / 3600, "positive duration on an interruption was silently counted");
        });
    }
}
