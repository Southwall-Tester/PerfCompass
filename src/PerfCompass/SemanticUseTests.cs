using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunSemanticUseTests(Action<string, Action> check, Action<bool, string> assert, string root, Func<AnalysisReport> factory)
    {
        var basis = factory();
        SemanticFact Fact(string text, string kind = "experience", string polarity = "required", string time = "past", string? use = null)
            => new("开放事项", kind, polarity, time, text, "", "", text) { DecisionUse = use };
        SemanticPlan Review(params SemanticFact[] facts)
        {
            string source = string.Join("；", facts.Select(f => f.Evidence));
            return SemanticRequirements.Confirm(new(source, "synthetic", "local", facts.ToList(), []), facts.Select(f => new SemanticFactEditor(f) { Include = true }));
        }
        AnalysisReport Apply(SemanticPlan plan) => SemanticRequirements.Apply(basis, new Settings(FutureNeeds: plan.Source, SemanticRequirements: plan));
        List<PurchaseCriterion> Goals(AnalysisReport report) => CandidateComparison.Drafts(report).Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal)).ToList();
        check("Past and negative source statements require a purchasing-use decision instead of silently disappearing", () =>
        {
            foreach (var f in new[] { Fact("昨天混音时卡顿"), Fact("我不允许直播丢帧", polarity: "excluded", time: "current") })
            {
                var plan = new SemanticPlan(f.Evidence, "legacy", "local", SemanticRequirements.Version, [f], DateTimeOffset.Now);
                var r = Apply(plan); var goals = Goals(r);
                assert(r.SemanticUseNotice.Length > 0 && !r.Ready && r.Cards.Single(c => c.Key == "memory").Status == "meaning", "ambiguous use was treated as no requirement");
                assert(goals.Count == 1 && goals[0].Condition.Length > 0 && !goals[0].Reviewed, "past or negative source lost its review branch");
            }
        });
        check("An explicitly selected past experience remains chronological past and constrains candidate validation", () =>
        {
            var f = Fact("昨天音频混音卡顿，下一台要解决", use: "avoid"); var r = Apply(Review(f));
            assert(r.SemanticRequirements!.Facts[0].Timeframe == "past" && r.SemanticRequirements.Facts[0].DecisionUse == "avoid", "chronology rewritten as current");
            assert(r.Cards.Single(c => c.Key == "gpu").Status == "requirements" && Goals(r).Single().Condition == "", "explicit ongoing goal not connected");
            var empty = basis with { SampleCount = 0, Ready = false, Cards = [new("gpu", "显卡", "unknown", "", "", "", "")], History = [] };
            var needs = SemanticRequirements.Apply(empty, new(FutureNeeds: f.Evidence, SemanticRequirements: Review(f)));
            assert(needs.Cards.Single(c => c.Key == "gpu").Status == "requirements" && !needs.Ready && Goals(needs).Count == 1, "explicit goals disappeared while waiting for telemetry");
        });
        check("The same excluded statement can be forbidden waived or awaiting review without guessing from its words", () =>
        {
            var f = Fact("离线渲染不要求快", polarity: "excluded", time: "current");
            var avoid = Apply(Review(f with { DecisionUse = "avoid" }));
            var omit = Apply(Review(f with { DecisionUse = "omit" }));
            var unclear = Apply(Review(f with { DecisionUse = "clarify" }));
            assert(Goals(avoid).Single().Name.Contains("必须避免") && Goals(avoid).Single().Metric == "check", "negative statement became a positive numeric bound");
            assert(Goals(omit).Count == 0 && !omit.Cards.Any(c => c.Key == "constraints") && omit.SemanticRequirements!.Facts.Count == 1, "waived source became a mandatory checklist or was deleted");
            assert(Goals(unclear).Single().Condition.Length > 0 && unclear.SemanticUseNotice.Length > 0, "unknown use became mandatory");
        });
        check("Open task names and languages use the same past-experience projection", () =>
        {
            foreach (string name in new[] { "望远镜叠加", "管风琴采样", "GeoMystic", "FPGA place-and-route", "线阵相机", "有限元求解", "字幕烧录", "远程GIS", "数字病理阅片", "MIDI orchestration" })
            {
                var f = Fact("昨天使用" + name + "时等待很久") with { Subject = name, DecisionUse = "require" };
                var r = Apply(Review(f));
                assert(Goals(r).Count == 1 && Goals(r)[0].Name.Contains(name) && r.Cards.Single(c => c.Key == "memory").Status == "requirements", "unfamiliar task required an implementation keyword");
            }
        });
        check("Past background and explicitly waived goals preserve measurements without manufacturing needs", () =>
        {
            foreach (var f in new[] { Fact("以前做三维建模，现在不再做", "workload", use: "omit"), Fact("显示器已经买过，不纳入这次预算", "budget", "excluded", "current", "omit") })
            {
                var r = Apply(Review(f));
                assert(Goals(r).Count == 0 && r.Cards.SequenceEqual(basis.Cards) && r.History.SequenceEqual(basis.History), "background changed independent load evidence");
                assert(r.SemanticRequirements!.Facts.Single().Evidence == f.Evidence, "background source was discarded");
            }
        });
        check("Conditions remain conditional for both required and forbidden uses", () =>
        {
            foreach (string use in new[] { "require", "avoid" })
            {
                var f = Fact("如果选上课才使用天文叠加", "workload", "conditional", "future", use) with { Condition = "选上课" };
                var r = Apply(Review(f));
                assert(r.Cards.All(c => c.Status != "requirements") && Goals(r).Single().Condition == "选上课", "conditional use became unconditional");
            }
        });
        check("Uncertain purposes retain separate branches and cannot inherit an affirmative hardware target", () =>
        {
            var f = new SemanticFact("内存", "resource", "required", "current", "至少64", "GB", "", "内存至少64GB") { DecisionUse = "clarify" };
            var r = Apply(Review(f)); var c = Goals(r).Single();
            assert(c.Metric == "check" && c.Target == null && c.Condition.Length > 0 && r.Cards.Single(c => c.Key == "memory").Status == "meaning", "unknown purpose compiled an affirmative floor");
            var conditional = f with { Polarity = "conditional", Condition = "签约", Evidence = "如果签约则内存至少64GB" };
            var branch = Goals(Apply(Review(conditional))).Single();
            assert(branch.Condition.StartsWith("需求尚未确定：", StringComparison.Ordinal) && branch.Condition.Contains("签约") && CandidateComparison.IsUnresolvedCondition(branch), "a known trigger erased the independently unresolved purchasing use");
        });
        check("Explicit past capacity goals compile only after their purchasing use is confirmed", () =>
        {
            var f = new SemanticFact("内存", "resource", "required", "past", "至少32", "GB", "", "以前这项任务要求内存至少32GB");
            assert(QuantityGoals.Compile(f).Count == 0, "past source became active without review");
            var r = Apply(Review(f with { DecisionUse = "require" })); var c = Goals(r).Single();
            assert(c.Metric == "memory" && c.Direction == "min" && c.Target == 32 && c.Condition == "", "explicitly retained past target failed to compile");
        });
        check("Avoid and omit purposes never invert quantitative targets or prices", () =>
        {
            foreach (var f in new[] { new SemanticFact("预算", "budget", "excluded", "current", "超过8000", "元", "", "预算不能超过8000元"), new SemanticFact("内存", "resource", "required", "current", "至少32", "GB", "", "内存至少32GB") })
            {
                assert(QuantityGoals.Compile(f with { DecisionUse = "avoid" }).Count == 0 && QuantityGoals.Compile(f with { DecisionUse = "omit" }).Count == 0, "purpose caused a numeric inversion");
                assert(Goals(Apply(Review(f with { DecisionUse = "avoid" }))).Single().Metric == "check", "forbidden statement became a positive bound");
            }
        });
        check("Every fact edit resets purchasing use while inclusion changes preserve an unchanged decision", () =>
        {
            var f = Fact("我不允许直播丢帧", polarity: "excluded", time: "current", use: "avoid");
            var changes = new Action<SemanticFactEditor>[] { e => e.Subject += "一", e => e.Kind = "other", e => e.Polarity = "required", e => e.Timeframe = "future", e => e.Value += "一", e => e.Unit = "FPS", e => e.Condition = "夜间", e => e.Evidence += "一" };
            foreach (var change in changes) { var e = new SemanticFactEditor(f); bool changed = false; e.Changed = () => changed = true; change(e); assert(changed && e.DecisionUse == "clarify", "edited fact kept an old purchasing-use decision"); }
            var include = new SemanticFactEditor(f); include.Include = true; assert(include.DecisionUse == "avoid", "selection erased an unchanged decision");
        });
        check("Provider JSON cannot supply the human purchasing-use decision", () =>
        {
            string payload = JsonSerializer.Serialize(new { facts = new[] { new { subject = "直播", kind = "experience", polarity = "excluded", timeframe = "current", value = "丢帧", unit = "", condition = "", evidence = "不允许丢帧", decisionUse = "omit" } }, questions = Array.Empty<string>() });
            bool rejected = false; try { SemanticRequirements.Parse("不允许丢帧", "m", "p", payload); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "provider could waive a goal through review metadata");
        });
        check("New confirmations persist a distinct plan contract and the selected use across reopening", () =>
        {
            var unchosen = new SemanticFactEditor(Fact("昨天混音时卡顿")) { Include = true, DecisionUse = null! };
            bool rejected = false; try { SemanticRequirements.Confirm(new("昨天混音时卡顿", "synthetic", "local", [], []), [unchosen]); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "new confirmation omitted its purchasing-use metadata");
            var plan = Review(Fact("昨天混音时卡顿", use: "avoid"));
            assert(plan.ContractVersion == SemanticUse.PlanVersion && plan.Facts.Single().DecisionUse == "avoid", "new review mislabeled as old extraction contract");
            var store = new LocalStore(Path.Combine(root, "semantic-use-settings")); store.SaveSettings(store.Settings with { FutureNeeds = plan.Source, SemanticRequirements = plan });
            var read = new LocalStore(store.Root).Settings.SemanticRequirements!;
            assert(read.Facts.Single().DecisionUse == "avoid" && Apply(read).Cards.Single(c => c.Key == "memory").Status == "requirements", "role lost on settings reopen");
        });
        check("Missing unknown or misversioned purpose metadata cannot look like an empty clean plan", () =>
        {
            var plan = Review(Fact("昨天混音时卡顿", use: "avoid"));
            foreach (var invalid in new[] { plan with { ContractVersion = "future-plan" }, plan with { Facts = [plan.Facts[0] with { DecisionUse = null }] }, plan with { Facts = [plan.Facts[0] with { DecisionUse = "automatic-buy" }] }, plan with { ContractVersion = SemanticRequirements.Version } })
            { var r = Apply(invalid); assert(!r.Ready && r.SemanticUseNotice.Length > 0 && r.SemanticRequirements == null && r.Cards.Single(c => c.Key == "memory").Status == "meaning", "unsupported semantics were silently dropped"); }
            var unknown = plan with { ContractVersion = "future-plan" };
            assert(CandidateComparison.Stamp(Apply(unknown)) != CandidateComparison.Stamp(Apply(unknown with { Facts = [unknown.Facts[0] with { Value = "changed" }] })), "changed unsupported content reused its old scope");
            assert(Apply(plan with { Facts = null! }).SemanticUseSignature.Length > 0 && Apply(plan with { Facts = [null!] }).SemanticUseSignature.Length > 0, "incomplete metadata was not safely fingerprinted");
            assert(CandidateComparison.OutdatedSemanticCriteria(new() { Criteria = Goals(Apply(plan)) }, Apply(unknown)).Count > 0, "an unreadable local plan was treated as no local source during review");
        });
        check("Ordinary positive current and future source identities remain compatible with old imported goals", () =>
        {
            foreach (string time in new[] { "current", "future" })
            {
                var f = new SemanticFact("内存", "resource", "required", time, "至少32", "GB", "", "内存至少32GB");
                var old = new SemanticPlan(f.Evidence, "legacy", "local", SemanticRequirements.Version, [f], DateTimeOffset.Now);
                var oldGoals = Goals(Apply(old)); var reviewed = Apply(Review(f));
                assert(oldGoals.Single().OriginId == Goals(reviewed).Single().OriginId, "unchanged positive goal needlessly acquired a new source identity");
                assert(CandidateComparison.OutdatedSemanticCriteria(new() { Criteria = oldGoals }, reviewed).Count == 0, "unchanged imported goal became stale");
            }
        });
        check("Changing a fact's purchasing use changes candidate scope without changing the original quote", () =>
        {
            var f = Fact("离线渲染不要求快", polarity: "excluded", time: "current");
            var first = Apply(Review(f with { DecisionUse = "avoid" })); var second = Apply(Review(f with { DecisionUse = "omit" }));
            assert(CandidateComparison.Stamp(first) != CandidateComparison.Stamp(second) && first.SemanticRequirements!.Source == second.SemanticRequirements!.Source, "changed purpose reused old coverage or rewrote the quote");
        });
        check("A waived imported goal is an input-review problem instead of silently forcing all candidates to fail", () =>
        {
            var f = new SemanticFact("内存", "resource", "required", "current", "至少32", "GB", "", "内存至少32GB");
            var first = Apply(Review(f)); var second = Apply(Review(f with { DecisionUse = "omit" }));
            var goal = Goals(first).Single(); goal.Reviewed = true;
            var workspace = new PurchaseWorkspace { Candidates = [new() { Name = "合成候选", MemoryGb = 32, Source = "合成规格" }], Criteria = [goal], ContextStamp = CandidateComparison.Stamp(first), CoverageConfirmed = true };
            bool rejected = false; try { CandidateComparison.Evaluate(workspace, second); } catch (ArgumentException) { rejected = true; }
            assert(rejected && goal.Target == 32 && goal.Reviewed && workspace.Criteria.Count == 1, "outdated target was evaluated or destructively removed");
            goal.OriginId = ""; goal.Reviewed = false; workspace.ContextStamp = CandidateComparison.Stamp(second);
            assert(CandidateComparison.Evaluate(workspace, second).Candidates.Single().State == "unknown", "detached target inherited old confirmation");
            goal.Reviewed = true;
            assert(CandidateComparison.Evaluate(workspace, second).Candidates.Single().State == "pass", "explicit independent target could not be reviewed");
        });
        check("Portable manually reviewed goals stay usable where no local semantic plan exists", () =>
        {
            var f = new SemanticFact("内存", "resource", "required", "current", "至少32", "GB", "", "内存至少32GB");
            var imported = Goals(Apply(Review(f))); imported[0].Reviewed = true;
            var workspace = new PurchaseWorkspace { Candidates = [new() { Name = "合成候选", MemoryGb = 32, Source = "合成规格" }], Criteria = imported, ContextStamp = CandidateComparison.Stamp(basis), CoverageConfirmed = true };
            assert(CandidateComparison.OutdatedSemanticCriteria(workspace, basis).Count == 0 && CandidateComparison.Evaluate(workspace, basis).Candidates.Single().State == "pass", "portable independent review required importing a foreign local plan");
        });
        check("Use review preserves busy and retained task observations rather than replacing them with low-load ambiguity", () =>
        {
            var f = Fact("昨天混音时卡顿"); var plan = Review(f);
            var heavy = new ResourceObservation("busy", "已测重任务", "synthetic heavy", "核对重任务", "所选90天", 1, 1);
            foreach (bool retained in new[] { false, true })
            {
                var c = new Advice("gpu", "显卡", "unknown", "", "", "", "") { Observation = retained ? null : heavy, RetainedWorkload = retained ? heavy : null };
                var r = SemanticRequirements.Apply(basis with { Cards = [c] }, new(FutureNeeds: plan.Source, SemanticRequirements: plan));
                assert(r.Cards.Single(c => c.Key == "gpu").Status == "unknown" && PurchaseGuidance.Build(r).Single(d => d.Key == "gpu").Reason.Contains("synthetic heavy"), "known heavy evidence hidden by unknown goal use");
            }
        });
        check("Reports keep chronology original quotation and purchasing use distinct", () =>
        {
            var f = Fact("昨天 <mix> 卡顿", use: "avoid"); var r = Apply(Review(f)); string html = ReportExporter.Html(r, null);
            assert(html.Contains("过去") && html.Contains("购机用途：必须避免") && html.Contains("&lt;mix&gt;"), "report lost source tense purpose or escaping");
        });
    }
}
