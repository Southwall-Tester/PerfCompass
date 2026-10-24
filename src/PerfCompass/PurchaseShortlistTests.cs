namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunPurchaseShortlistTests(Action<string, Action> check, Action<bool, string> assert)
    {
        AnalysisReport Context() => new(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
        PurchaseWorkspace Workspace(AnalysisReport report) => new()
        {
            ContextStamp = CandidateComparison.Stamp(report), CoverageConfirmed = true,
            Candidates = [new() { Id = "a", Name = "合成甲", MemoryGb = 32, PriceCny = 6000, Source = "合成报价甲" },
                new() { Id = "b", Name = "合成乙", MemoryGb = 64, PriceCny = 9000, Source = "合成报价乙" },
                new() { Id = "c", Name = "合成不足", MemoryGb = 8, PriceCny = 1000, Source = "合成报价丙" }],
            Criteria = [new() { Id = "ram", Name = "必需容量", Metric = "memory", Target = 16, Unit = "GB", Reviewed = true }]
        };
        PurchaseCriterion TaskGoal(string id, string direction = "max", string condition = "") => new()
        { Id = id, Name = "任务" + id, Metric = "task", Target = direction is "max" or "lt" ? 20 : 50, Direction = direction, Unit = "秒", Procedure = "同一任务与设置", Condition = condition, Reviewed = true };
        void Value(PurchaseWorkspace w, PurchaseCriterion goal, string id, double value, TaskMeasurementResult? measurement = null)
        {
            var candidate = w.Candidates.Single(c => c.Id == id);
            w.Observations.RemoveAll(o => o.CandidateId == id && o.CriterionId == goal.Id);
            w.Observations.Add(new() { CandidateId = id, CriterionId = goal.Id, Value = value, Measurement = measurement,
                Comparable = true, ProofStamp = CandidateComparison.ObservationStamp(candidate, goal), Evidence = "合成任务记录，非真实硬件测量" });
        }
        PurchaseOption Option(PurchaseComparison result, string id) => result.Shortlists[0].Options.Single(o => o.CandidateId == id);
        check("Purchasing shortlist ranks only passing sourced quotes and preserves ties", () =>
        {
            var r = Context(); var w = Workspace(r); var result = CandidateComparison.Evaluate(w, r);
            assert(result.Shortlists.Length == 1 && result.Shortlists[0].Summary.Contains("合成甲")
                && Option(result, "b").Label == "尚无任务收益对照" && result.Shortlists[0].Options.All(o => o.CandidateId != "c"), "failing cheap quote entered shortlist or absent task evidence became redundant hardware");
            w.Candidates[1].PriceCny = 6000;
            result = CandidateComparison.Evaluate(w, r);
            assert(result.Shortlists[0].Options.Count(o => o.Label == "预算优先") == 2, "equal prices were arbitrarily ranked");
            w.Candidates[0].Source = "";
            assert(CandidateComparison.Evaluate(w, r).Shortlists[0].Summary.Contains("合成乙"), "unsourced quote remained a budget recommendation");
        });
        check("Purchasing shortlist differentiates task gains losses and no premium advantage", () =>
        {
            foreach (string direction in new[] { "min", "gt", "max", "lt" })
            {
                var r = Context(); var w = Workspace(r); var goal = TaskGoal("x", direction); w.Criteria.Add(goal);
                bool lower = direction is "max" or "lt";
                Value(w, goal, "a", lower ? 10 : 80); Value(w, goal, "b", lower ? 12 : 75);
                var result = CandidateComparison.Evaluate(w, r);
                assert(Option(result, "b").Label == "已记录任务未见加价优势" && Option(result, "b").Detail.Contains("3000") && Option(result, "b").Detail.Contains("手填任务值"), "goal direction or provenance lost: " + direction);
                Value(w, goal, "b", lower ? 8 : 100);
                assert(Option(CandidateComparison.Evaluate(w, r), "b").Label == "记录到任务优势", "better task outcome was ignored: " + direction);
                w.Candidates.Reverse();
                assert(Option(CandidateComparison.Evaluate(w, r), "b").Label == "记录到任务优势", "input order changed shopping recommendation");
            }
        });
        check("Purchasing shortlist keeps conflicting performance objectives distinct even with identical names", () =>
        {
            var r = Context(); var w = Workspace(r); var a = TaskGoal("one"); var b = TaskGoal("two"); b.Name = a.Name; b.Reviewed = true;
            w.Criteria.AddRange([a, b]); Value(w, a, "a", 10); Value(w, a, "b", 5); Value(w, b, "a", 10); Value(w, b, "b", 15);
            assert(Option(CandidateComparison.Evaluate(w, r), "b").Label == "加价有任务取舍", "identical task labels merged independent performance goals");
        });
        check("Purchasing shortlist separates conditional budgets and excludes outdated goals", () =>
        {
            var r = Context(); var w = Workspace(r); var goal = TaskGoal("conditional", condition: "接到剪辑项目时");
            w.Criteria.Add(goal); Value(w, goal, "a", 25); Value(w, goal, "b", 8);
            var result = CandidateComparison.Evaluate(w, r);
            assert(result.Shortlists.Length == 2 && result.Shortlists[0].Summary.Contains("合成甲") && result.Shortlists[1].Summary.Contains("合成乙")
                && !result.Shortlists[0].Options.Any(o => o.Detail.Contains("任务conditional")), "conditional performance leaked into base recommendation");
            goal.Reviewed = false;
            result = CandidateComparison.Evaluate(w, r);
            assert(result.Shortlists[0].Summary.Contains("合成甲") && result.Shortlists[1].Options.All(o => o.CandidateId.Length == 0), "outdated conditional goal erased base passes or admitted branch candidates");
            w.ContextStamp = "stale";
            assert(CandidateComparison.Evaluate(w, r).Shortlists.All(s => s.Options.All(o => o.CandidateId.Length == 0)), "outdated coverage produced buying advice");
        });
        check("Purchasing shortlist uses raw repeated ranges and keeps overlaps uncertain", () =>
        {
            var r = Context(); var w = Workspace(r); var am = MeasurementFixture(10, 11, 12); var bm = MeasurementFixture(9, 10, 11);
            var goal = TaskGoal("repeat"); goal.Procedure = am.Recipe.Procedure; goal.MeasurementProtocol = am.ProtocolFingerprint; goal.Reviewed = true; w.Criteria.Add(goal);
            Value(w, goal, "a", 11, am); Value(w, goal, "b", 10, bm);
            assert(Option(CandidateComparison.Evaluate(w, r), "b").Label == "加价收益待核对", "overlapping samples were promoted by their medians");
            bm = MeasurementFixture(6, 7, 8); Value(w, goal, "b", 7, bm);
            assert(Option(CandidateComparison.Evaluate(w, r), "b").Label == "记录到任务优势", "separated raw ranges were not compared");
            goal.Unit = "ms"; goal.Target = 20000; goal.Reviewed = true; Value(w, goal, "a", 11000, am); Value(w, goal, "b", 7000, bm);
            assert(Option(CandidateComparison.Evaluate(w, r), "b").Detail.Contains("6–8 秒"), "raw seconds were mislabeled as the displayed unit");
            bm = bm with { Machine = bm.Machine with { PowerSource = "Unknown" } }; Value(w, goal, "b", 7000, bm);
            assert(CandidateComparison.Evaluate(w, r).Shortlists[0].Options.All(o => o.CandidateId != "b"), "unknown power measurement entered buying order");
        });
        check("Purchasing shortlist rejects conflicting goals and escapes user labels in HTML", () =>
        {
            var r = Context(); var w = Workspace(r); w.Candidates[0].Name = "<合成甲&>";
            var result = CandidateComparison.Evaluate(w, r); string html = CandidateComparison.Html(w, result);
            assert(html.Contains("购机建议与加价取舍") && !html.Contains("<合成甲&>"), "shortlist was absent from HTML or source text was not escaped");
            w.Criteria.Add(new() { Name = "冲突容量", Metric = "memory", Target = 8, Direction = "max", Unit = "GB", Reviewed = true });
            result = CandidateComparison.Evaluate(w, r);
            assert(result.Shortlists[0].Summary == "先处理条件冲突" && result.Shortlists[0].Options.All(o => o.CandidateId.Length == 0), "conflicting input became a purchasing recommendation");
        });
    }
}
