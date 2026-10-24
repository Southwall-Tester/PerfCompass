using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunExperienceReaderTests(Action<string, Action> check, Action<bool, string> assert, string root, Func<AnalysisReport> baselineFactory)
    {
        var now = DateTimeOffset.Now;
        var value = ExperienceAnalyzer.Create(now, "audit", "日常办公", "任务等待过久", "合成体验 <pending>", []);
        LocalStore Store() => new(Path.Combine(root, "experience-audit", Guid.NewGuid().ToString("N")));
        string PathFor(LocalStore s) => Path.Combine(s.Root, $"experience-{now.LocalDateTime:yyyy-MM-dd}.jsonl");
        string Json(ExperienceEvent e) => JsonSerializer.Serialize(e);
        check("Experience audit diagnoses a corrupt tail while retaining valid events and original bytes", () =>
        {
            var s = Store(); s.AppendExperience(value); File.AppendAllText(PathFor(s), "{cut");
            byte[] original = File.ReadAllBytes(PathFor(s)); var r = s.ReadExperiencesChecked(7, "audit", now);
            assert(r.Events.Count == 1 && r.Quality.IssueCount == 1 && r.Quality.Issues.Single().Line > 0, "tail silently lost or valid event discarded");
            assert(File.ReadAllBytes(PathFor(s)).SequenceEqual(original), "audit modified the original");
        });
        check("Experience audit does not interpret missing required fields as an empty clean history", () =>
        {
            foreach (string text in new[] { "null", "[]", "{}", "{\"Id\":\"x\"}", Json(value).Replace("\"HardwareId\":\"audit\"", "\"HardwareId\":null"), Json(value with { Clues = [] }).Replace("\"Clues\":[]", "\"Clues\":null") })
            {
                var s = Store(); File.WriteAllText(PathFor(s), text); var r = s.ReadExperiencesChecked(7, "audit", now);
                assert(r.Quality.HasIssues && r.Events.Count == 0, "missing fields accepted");
            }
        });
        check("Experience duplicate JSON members cannot silently override resolved state or resource evidence", () =>
        {
            var s = Store(); File.WriteAllText(PathFor(s), Json(value)[..^1] + ",\"Resolved\":true}");
            assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "duplicate state accepted");
            var withClue = value with { Clues = [new("cpu", "CPU", 95, 2, 4)] };
            File.WriteAllText(PathFor(s), Json(withClue).Replace("\"Peak\":95", "\"Peak\":95,\"Peak\":0"));
            assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "duplicate nested member accepted");
        });
        check("Experience audit rejects contradictory finite and missing resource measurements", () =>
        {
            foreach (var c in new ResourceClue[] { new("cpu", "CPU", -1, 0, 2), new("cpu", "CPU", null, 0, 2), new("gpu", "GPU", 3, 2, 2), new("cpu", "CPU", 95, 5, 2), new("cpu", "CPU", 95, 0, 121), new("cpu", "CPU", 95, 0, 0) })
            {
                var s = Store(); s.AppendExperience(value with { Clues = [c] });
                assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "contradictory clue accepted");
            }
        });
        check("Experience audit accepts resolution updates and exact copies without double counting", () =>
        {
            var s = Store(); s.AppendExperience(value); s.AppendExperience(value); s.AppendExperience(value with { Resolved = true });
            var r = s.ReadExperiencesChecked(7, "audit", now);
            assert(!r.Quality.HasIssues && r.Events.Single().Resolved, "legitimate state update rejected");
        });
        check("Conflicting experience identities never let a later resolved row erase a different event", () =>
        {
            var s = Store(); s.AppendExperience(value); s.AppendExperience(value with { Note = "different", Resolved = true });
            s.AppendExperience(value); var r = s.ReadExperiencesChecked(7, "audit", now);
            assert(r.Quality.HasIssues && r.Events.Count == 0 && r.Quality.Issues.Length == 2, "conflict silently resolved by line order");
        });
        check("Experience audit scopes valid foreign hardware scenes and times without importing their state", () =>
        {
            var s = Store(); s.AppendExperience(value with { HardwareId = "other", Resolved = true });
            s.AppendExperience(value with { Scene = "其他场景", Resolved = true });
            s.AppendExperience(value with { Time = now.AddMinutes(1), Resolved = true }); s.AppendExperience(value);
            var r = s.ReadExperiencesChecked(7, "audit", now, "日常办公");
            assert(!r.Quality.HasIssues && r.Events.Count == 1 && !r.Events[0].Resolved, "context boundary lost");
        });
        check("Experience read failures invalid UTF8 and oversized rows remain visible and preserve subsequent records", () =>
        {
            var s = Store(); File.WriteAllBytes(PathFor(s), [0xff, 0x0a]); s.AppendExperience(value);
            assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "UTF8 failure hidden");
            File.WriteAllText(PathFor(s), new string('x', LocalStore.MaximumHistoryLineBytes + 1) + "\n" + Json(value));
            var r = s.ReadExperiencesChecked(7, "audit", now); assert(r.Quality.IssueCount == 1 && r.Events.Count == 1, "oversized row swallowed following event");
            using var held = new FileStream(PathFor(s), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "unreadable file hidden");
        });
        check("Experience diagnostics bind changed damaged bytes to candidate scope and clear only after a clean read", () =>
        {
            var s = Store(); File.WriteAllText(PathFor(s), "{first"); var baseline = baselineFactory();
            var first = ExperienceAnalyzer.Attach(baseline, s.ReadExperiencesChecked(7, "audit", now));
            File.WriteAllText(PathFor(s), "{second"); var second = ExperienceAnalyzer.Attach(baseline, s.ReadExperiencesChecked(7, "audit", now));
            assert(CandidateComparison.Stamp(first) != CandidateComparison.Stamp(second), "damage changes did not revoke scope");
            File.WriteAllText(PathFor(s), "\n"); var clean = ExperienceAnalyzer.Attach(baseline, s.ReadExperiencesChecked(7, "audit", now));
            assert(!clean.ExperienceQuality.HasIssues && CandidateComparison.Stamp(clean) == CandidateComparison.Stamp(baseline), "clean scope not restored independently");
        });
        check("Experience reader fixes a committed prefix and does not expose an append from later in the same read", () =>
        {
            var s = Store(); s.AppendExperience(value);
            s.ExperiencePrefixCapturedForTest = () => { s.ExperiencePrefixCapturedForTest = null; s.AppendExperience(value with { Resolved = true }); };
            var first = s.ReadExperiencesChecked(7, "audit", now); var second = s.ReadExperiencesChecked(7, "audit", now);
            assert(!first.Quality.HasIssues && !first.Events.Single().Resolved && second.Events.Single().Resolved, "read crossed its captured prefix");
        });
        check("Experience reader reports truncation after prefix capture without claiming a clean empty history", () =>
        {
            var s = Store(); s.AppendExperience(value);
            s.ExperiencePrefixCapturedForTest = () => { s.ExperiencePrefixCapturedForTest = null; File.WriteAllText(PathFor(s), ""); };
            assert(s.ReadExperiencesChecked(7, "audit", now).Quality.HasIssues, "concurrent truncation hidden");
        });
        check("Damaged experience history blocks downgrade while preserving telemetry observations and explicit report evidence", () =>
        {
            var s = Store(); File.WriteAllText(PathFor(s), "{missing"); var baseline = baselineFactory();
            var result = ExperienceAnalyzer.Attach(baseline, s.ReadExperiencesChecked(7, "audit", now));
            assert(!result.Ready && result.History.SequenceEqual(baseline.History) && result.DemandTrends.SequenceEqual(baseline.DemandTrends), "experience issue destroyed independent telemetry");
            assert(result.Cards.All(c => c.Status is not ("surplus" or "review")) && PurchaseGuidance.Build(result).All(d => !d.Direction.Contains("较低档")), "downgrade leaked through damaged experiences");
            string html = ReportExporter.Html(result, null);
            assert(html.Contains("体验记录读取异常") && html.Contains(Path.GetFileName(PathFor(s))) && html.Contains("第 1 行"), "report omitted diagnosis");
        });
        check("Candidate experience scope binds the actual event content rather than only its identifier", () =>
        {
            var baseline = baselineFactory();
            var first = baseline with { ExperienceEvents = [value] };
            var second = baseline with { ExperienceEvents = [value with { Note = "changed independently" }] };
            assert(CandidateComparison.Stamp(first) != CandidateComparison.Stamp(second), "changed experience content reused its old scope");
        });
        check("Experience read damage preserves independently retained heavy tasks and currently busy observations", () =>
        {
            var quality = new ExperienceReadQuality(7, 1, 1, "synthetic-issue", [new("synthetic.jsonl", 1, "损坏")]);
            var light = new ResourceObservation("light", "轻载", "recent low", "核对", "最近7天", 1, 1);
            var heavy = new ResourceObservation("busy", "重任务", "older heavy", "保留", "所选90天", 1, 1);
            var c = new Advice("gpu", "显卡", "unknown", "", "", "", "") { Observation = light, RetainedWorkload = heavy };
            var retained = ExperienceAnalyzer.Attach(baselineFactory() with { Cards = [c] }, new ExperienceReadResult([], quality));
            var decision = PurchaseGuidance.Build(retained).Single(d => d.Key == "gpu");
            assert(retained.Cards.Single().UsesRetainedWorkload && decision.Reason.Contains("older heavy") && !decision.Direction.Contains("较低档"), "retained measured task hidden or became downgrade permission");
            var busy = ExperienceAnalyzer.Attach(baselineFactory() with { Cards = [c with { Observation = heavy, RetainedWorkload = null }] }, new ExperienceReadResult([], quality));
            assert(PurchaseGuidance.Build(busy).Single(d => d.Key == "gpu").Reason.Contains("older heavy"), "busy measurement hidden by experience damage");
        });
    }
}
