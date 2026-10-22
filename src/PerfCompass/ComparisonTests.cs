using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunComparisonTests(Action<string, Action> check, Action<bool, string> assert)
    {
        AnalysisReport Context() => new(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
        PurchaseCriterion Goal(string metric = "memory", double target = 32, string direction = "min", string unit = "GB") => new() { Name = "目标", Metric = metric, Target = target, Direction = direction, Unit = unit, Evidence = "用户目标", Reviewed = true };
        PurchaseCandidate Candidate(string name, double? price, double? ram) => new() { Name = name, MemoryGb = ram, PriceCny = price, Cpu = "测试CPU", Gpu = "测试GPU", Source = "合成配置/报价单 2026-10-22" };
        PurchaseWorkspace Workspace(AnalysisReport context, params PurchaseCriterion[] criteria) => new() { ContextStamp = CandidateComparison.Stamp(context), CoverageConfirmed = true, Candidates = [Candidate("A", 6000, 32), Candidate("B", 8500, 64)], Criteria = criteria.ToList() };
        PurchaseObservation Observation(PurchaseCandidate candidate, PurchaseCriterion criterion, double? value = null, string verdict = "pass") => new() { CandidateId = candidate.Id, CriterionId = criterion.Id, Value = value, Verdict = verdict, Evidence = "合成测试记录，相同版本与设置", Comparable = true, ProofStamp = CandidateComparison.ObservationStamp(candidate, criterion) };
        check("Candidate comparison reports savings only among proven required-condition passes", () =>
        {
            var context = Context(); var workspace = Workspace(context, Goal()); var result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates.All(c => c.State == "pass") && result.Summary.Contains("2500") && result.Summary.Contains("A"), "passing comparison or savings missing");
            assert(result.Tradeoffs.Single().Contains("32 → 64") && result.Tradeoffs.Single().Contains("2500"), "capacity premium not explained");
            workspace.Candidates[0].MemoryGb = 16;
            result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates[0].State == "fail" && !result.Summary.Contains("2500"), "failing cheap candidate suggested");
        });
        check("Missing specs sources quotes or task coverage never become a buying pass", () =>
        {
            var context = Context(); var workspace = Workspace(context, Goal());
            workspace.Candidates[0].MemoryGb = null; workspace.Candidates[1].Source = "";
            assert(CandidateComparison.Evaluate(workspace, context).Candidates.All(c => c.State == "unknown"), "missing evidence became pass");
            workspace = Workspace(context, Goal()); workspace.CoverageConfirmed = false;
            assert(CandidateComparison.Evaluate(workspace, context).Candidates.All(c => c.State == "unknown"), "coverage confirmation bypassed");
            workspace.CoverageConfirmed = true; workspace.Candidates.ForEach(c => c.PriceCny = null);
            assert(CandidateComparison.Evaluate(workspace, context).Summary.Contains("没有同时通过"), "unknown quote fabricated");
        });
        check("Conditional failures remain separate from the no-trigger scenario", () =>
        {
            var context = Context(); var conditional = Goal(target: 64); conditional.Condition = "接到本地训练项目"; conditional.Reviewed = true;
            var workspace = Workspace(context, Goal(), conditional); var result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates[0].State == "pass" && result.Candidates[0].Checks[1].State == "fail" && result.Summary.Contains("触发时"), "conditional branch collapsed");
            assert(result.ScenarioSummaries.Single().Contains("8500") && result.ScenarioSummaries.Single().Contains("B"), "trigger scenario inherited the cheap inadequate candidate");
        });
        check("Task results require matching protocol source units and explicit comparability", () =>
        {
            var context = Context(); var goal = Goal("task", 120, "max", "秒"); goal.Procedure = "同一源码干净编译三次，填中位数；同版本工具链、电源模式"; goal.Reviewed = true;
            var workspace = Workspace(context, goal);
            workspace.Observations = [Observation(workspace.Candidates[0], goal, 100), Observation(workspace.Candidates[1], goal, 140)];
            var result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates[0].State == "pass" && result.Candidates[1].State == "fail", "task direction wrong");
            workspace.Observations[0].Comparable = false;
            assert(CandidateComparison.Evaluate(workspace, context).Candidates[0].State == "unknown", "unconfirmed test passed");
            workspace.Observations[0].Comparable = true; goal.Procedure += "，改变并行度"; goal.Reviewed = true;
            assert(CandidateComparison.Evaluate(workspace, context).Candidates[0].State == "unknown", "old test survived changed procedure");
        });
        check("Editing a criterion or candidate invalidates old reviewed evidence", () =>
        {
            var context = Context(); var goal = Goal("check"); var workspace = Workspace(context, goal);
            workspace.Observations = workspace.Candidates.Select(c => Observation(c, goal)).ToList();
            assert(CandidateComparison.Evaluate(workspace, context).Candidates.All(c => c.State == "pass"), "initial check failed");
            workspace.Candidates[0].PriceCny = 6100;
            assert(CandidateComparison.Evaluate(workspace, context).Candidates[0].State == "pass", "quote-only edit invalidated performance proof");
            goal.Name = "必须支持指定插件";
            assert(CandidateComparison.Evaluate(workspace, context).Candidates.All(c => c.State == "unknown"), "edited condition kept review");
            goal.Reviewed = true; workspace.Observations = workspace.Candidates.Select(c => Observation(c, goal)).ToList();
            workspace.Candidates[0].Gpu = "另一显卡";
            assert(CandidateComparison.Evaluate(workspace, context).Candidates[0].State == "unknown", "changed machine inherited check");
        });
        check("New requirements and pressure signals invalidate previous comparison context", () =>
        {
            var context = Context(); var workspace = Workspace(context, Goal());
            var changed = context with { FutureNeeds = "新增光谱计算任务" };
            assert(CandidateComparison.Evaluate(workspace, changed).Candidates.All(c => c.State == "unknown"), "changed needs ignored");
            changed = context with { Cards = [new("gpu", "显卡", "changed", "新增负载", "", "", "")] };
            assert(CandidateComparison.Evaluate(workspace, changed).Summary.Contains("已改变"), "pressure change ignored");
        });
        check("Confirmed numeric drafts preserve upper lower units and exclusions", () =>
        {
            string source = "内存至少64GB，预算不超过1.2万元，存储至少2TB；不做视频剪辑；如果接到项目才训练；去年做过动画";
            var facts = new List<SemanticFact> {
                new("内存", "resource", "required", "future", "至少64", "GB", "", "内存至少64GB"),
                new("预算", "budget", "required", "current", "不超过1.2", "万元", "", "预算不超过1.2万元"),
                new("存储", "resource", "required", "future", "至少2TB", "", "", "存储至少2TB"),
                new("视频剪辑", "workload", "excluded", "future", "", "", "", "不做视频剪辑"),
                new("训练", "workload", "conditional", "future", "", "", "接到项目", "如果接到项目才训练"),
                new("动画", "workload", "required", "past", "", "", "", "去年做过动画") };
            var context = Context() with { FutureNeeds = source, SemanticRequirements = new(source, "test", "test", SemanticRequirements.Version, facts, DateTimeOffset.Now) };
            var drafts = CandidateComparison.Drafts(context);
            assert(drafts.Count == 5 && drafts[0].Metric == "memory" && drafts[0].Direction == "min" && drafts[0].Target == 64 && !drafts[0].Reviewed, "memory draft or review wrong");
            assert(drafts[1].Metric == "price" && drafts[1].Target == 12000 && drafts[1].Direction == "max", "budget normalization wrong");
            assert(drafts[2].Target == 2000 && drafts[3].Metric == "check" && drafts[4].Condition == "接到项目", "storage, exclusion or condition wrong");
        });
        check("Ambiguous numeric phrases remain checklists rather than invented bounds", () =>
        {
            var context = Context() with { SemanticRequirements = new("原文", "test", "test", SemanticRequirements.Version,
                [new("内存", "resource", "required", "future", "32起步，64更好", "GB", "", "原文"), new("显存", "resource", "required", "future", "16", "GB", "", "原文"),
                new("显卡内存", "resource", "required", "future", "至少16", "GB", "", "原文"), new("内存和显存", "resource", "required", "future", "至少16", "GB", "", "原文"),
                new("显卡预算", "budget", "required", "future", "不超过4000", "元", "", "原文")], DateTimeOffset.Now) };
            assert(CandidateComparison.Drafts(context).All(c => c.Metric == "check"), "ambiguous phrase became floor");
        });
        check("Observed scenes and raw unparsed needs are included in comparison drafts", () =>
        {
            var context = Context() with { FutureNeeds = "未来可能做陌生任务", SceneProfiles = [new("偶发渲染", 1, .001, 10, 20, 12, 80, 95, 1, 1)] };
            var drafts = CandidateComparison.Drafts(context);
            assert(drafts.Count == 2 && drafts.Any(c => c.Name.Contains("偶发渲染")) && drafts.Any(c => c.Evidence.Contains("陌生任务")), "rare scene or raw needs omitted");
        });
        check("Comparison rejects invalid numbers units duplicate measurements and oversized collections", () =>
        {
            var context = Context(); var workspace = Workspace(context, Goal());
            void Reject(Action change) { change(); bool rejected = false; try { CandidateComparison.Validate(workspace); } catch (ArgumentException) { rejected = true; } assert(rejected, "invalid comparison accepted"); }
            Reject(() => workspace.Candidates[0].PriceCny = double.NaN); workspace.Candidates[0].PriceCny = 1;
            Reject(() => workspace.Criteria[0].Unit = "TB"); workspace.Criteria[0].Unit = "GB";
            string originalName = workspace.Candidates[1].Name; Reject(() => workspace.Candidates[1].Name = workspace.Candidates[0].Name); workspace.Candidates[1].Name = originalName;
            var observation = Observation(workspace.Candidates[0], workspace.Criteria[0]);
            Reject(() => workspace.Observations = [observation, observation]); workspace.Observations.Clear();
            Reject(() => workspace.Candidates.AddRange([Candidate("C", 1, 1), Candidate("D", 1, 1)]));
        });
        check("Comparison is independent of candidate order and preserves tied lowest quotes", () =>
        {
            var context = Context(); var workspace = Workspace(context, Goal()); workspace.Candidates[1].PriceCny = 6000;
            var result = CandidateComparison.Evaluate(workspace, context); workspace.Candidates.Reverse();
            var reversed = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates.All(c => reversed.Candidates.Single(r => r.Id == c.Id).State == c.State) && reversed.Summary.Contains("A") && reversed.Summary.Contains("B"), "order or tied minimum wrong");
        });
        check("Comparison persistence retains review stamps and escapes all report text", () =>
        {
            var context = Context(); var goal = Goal("check"); var workspace = Workspace(context, goal); workspace.Candidates[0].Name = "<script>测试</script>";
            workspace.Observations = workspace.Candidates.Select(c => Observation(c, goal)).ToList();
            var root = Path.Combine(Path.GetTempPath(), "PerfCompass-comparison-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            CandidateComparison.Save(root, workspace); var reopened = CandidateComparison.Load(root); var result = CandidateComparison.Evaluate(reopened, context);
            assert(result.Candidates.All(c => c.State == "pass"), "persisted proof invalidated");
            var html = CandidateComparison.Html(reopened, result);
            assert(html.Contains("&lt;script&gt;") && !html.Contains("<script>测试"), "unsafe report text");
            File.WriteAllText(Path.Combine(root, "comparison.json"), "{broken"); bool failed = false;
            try { CandidateComparison.Load(root); } catch (JsonException) { failed = true; }
            assert(failed && File.ReadAllText(Path.Combine(root, "comparison.json")) == "{broken", "broken workspace discarded");
        });
    }
}
