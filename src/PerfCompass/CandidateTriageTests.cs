using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunCandidateTriageTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 25, 4, 0, 0, TimeSpan.Zero);
        const string hardware = "synthetic-triage";
        var samples = Enumerable.Range(0, 60).Select(i => new Sample { Time = now.AddHours(-1).AddSeconds(i * 10), Seconds = 10,
            HardwareId = hardware, Active = true, Cpu = 12, CoreMax = 30, MemoryTotalGb = 32, MemoryUsedGb = 5, CommitGb = 7,
            Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core", ObservationVersion = ActivityRules.Version,
            DiskUsedGb = 600, DiskTotalGb = 1000, MaxDiskPercent = 60, DiskCapacityFieldsPresent = true }).ToArray();
        AnalysisReport Analyze(IEnumerable<Sample> rows, int days = 7) => AnalysisEngine.Analyze(rows, new(), days, null, now, hardware);
        var report = Analyze(samples);
        PurchaseCandidate Product(string name, string gpu = "核显 · 合成GPU", double storage = 1000, double price = 5000)
            => new() { Name = name, Cpu = "Synthetic CPU", Gpu = gpu, MemoryGb = 32, VramGb = 0, StorageGb = storage,
                PriceCny = price, Source = "Synthetic quote only", SpecificationSource = "Synthetic specifications only" };
        PurchaseCriterion Bound(string metric, double target, string direction = "min", string condition = "")
        {
            var c = new PurchaseCriterion { Name = metric + " goal", Metric = metric, Target = target, Direction = direction,
                Unit = metric == "price" ? "元" : "GB", Condition = condition, Evidence = "Synthetic reviewed goal" };
            c.Reviewed = true; return c;
        }
        check("Concrete candidate triage uses sourced quotes before graphics labels and routes when quotes are unknown", () =>
        {
            var discrete = Product("Discrete", "独显 · 合成GPU", price: 4000); var integrated = Product("Integrated", price: 6000);
            var result = CandidateTriage.Build(new() { Candidates = [discrete, integrated] }, report);
            assert(result.Cases[0].Candidates[0].Id == discrete.Id && result.Cases[0].Candidates.Single(c => c.Id == integrated.Id).ReasonText.Contains("整机报价"), "A more expensive integrated label displaced the lower sourced quote");
            assert(result.Cases.SelectMany(c => c.Candidates).All(c => !c.State.Contains("达标") && c.PendingText.Contains("实际任务")), "Quote or route became a suitability verdict");
            discrete.PriceCny = integrated.PriceCny = null;
            assert(CandidateTriage.Build(new() { Candidates = [discrete, integrated] }, report).Cases[0].Candidates[0].Id == integrated.Id,
                "Unknown quotes erased the measured integrated-first comparison route");
        });
        check("Observed storage footprint changes buying order while nominal RAM does not certify usable GiB", () =>
        {
            var small = Product("Small", storage: 512, price: 2000); var larger = Product("Larger");
            var rows = CandidateTriage.Build(new() { Candidates = [small, larger] }, report).Cases[0].Candidates;
            assert(rows[0].Id == larger.Id && rows.Single(r => r.Id == small.Id).IssueText.Contains("外迁"), "A below-footprint bargain displaced a larger comparison");
            assert(rows.All(r => r.PendingText.Contains("实际可用量")) && rows[0].IssueText.Length == 0, "32 GB was silently equated to observed RAM or declared inadequate from a nominal label");
            var occupied = Analyze(samples.Select(s => s with { MemoryUsedGb = 26, MemoryPeakGb = 27 }));
            var little = Product("16 GB"); little.MemoryGb = 16;
            var normal = Product("32 GB");
            var memoryRows = CandidateTriage.Build(new() { Candidates = [little, normal] }, occupied).Cases[0].Candidates;
            assert(memoryRows[0].Id == normal.Id && memoryRows.Single(r => r.Id == little.Id).IssueText.Contains("换页影响")
                && memoryRows[0].IssueText.Length == 0, "A nominal memory ceiling below the physical peak was ignored or the larger label was certified usable");
        });
        check("Only sourced quotes order equally scoped candidates and unknown amounts retain input order", () =>
        {
            var first = Product("No source high", price: 9000); first.Source = "";
            var second = Product("No source low", price: 1000); second.Source = "";
            var quoted = Product("Quoted", price: 6000);
            var rows = CandidateTriage.Build(new() { Candidates = [first, second, quoted] }, report).Cases[0].Candidates;
            assert(rows.Select(r => r.Id).SequenceEqual(new[] { quoted.Id, first.Id, second.Id }), "Unverified amounts changed quote order");
            assert(rows[1].Quote.Contains("来源待补") && rows[2].PendingText.Contains("报价来源"), "Unsourced amounts masqueraded as valid quotes");
        });
        check("Reviewed manual numeric constraints participate and modified unreviewed bounds cannot become facts", () =>
        {
            var c = Product("32 GB"); var goal = Bound("memory", 64);
            var workspace = new PurchaseWorkspace { Candidates = [c], Criteria = [goal] };
            assert(CandidateTriage.Build(workspace, report).Cases[0].Candidates[0].IssueText.Contains("至少 64"), "Reviewed manual goal was ignored");
            goal.Target = 16;
            var changed = CandidateTriage.Build(workspace, report);
            assert(changed.Cases[0].Candidates[0].Issues.Length == 0 && changed.Notice.Contains("已修改"), "A stale review stamp used the edited target");
        });
        check("Triage keeps independent trigger scenarios and numeric conflicts separate", () =>
        {
            var c = Product("Candidate");
            var workspace = new PurchaseWorkspace { Candidates = [c], Criteria = [Bound("memory", 64, condition: "训练"), Bound("memory", 16, "max", "出差")] };
            var result = CandidateTriage.Build(workspace, report);
            assert(result.Cases.Length == 3 && result.Cases.All(c => c.Notice.Length == 0)
                && result.Cases.Single(c => c.Label == "当前任务与必需条件").Candidates[0].Issues.Length == 0, "Independent triggers were merged into base requirements or a false conflict");
            workspace.Criteria.Add(Bound("memory", 32, "max", "训练"));
            assert(CandidateTriage.Build(workspace, report).Cases.Single(c => c.Label == "训练").Notice.Contains("共同可取值"), "An actual same-scenario conflict disappeared");
        });
        check("Confirmed automatic strict capacity goals participate before manual comparison review", () =>
        {
            var fact = new SemanticFact("内存", "resource", "required", "future", "大于64", "GB", "", "内存大于64GB") { DecisionUse = "require" };
            var plan = SemanticRequirements.Confirm(new(fact.Evidence, "synthetic", "local", [fact], []), [new SemanticFactEditor(fact) { Include = true }]);
            var needs = SemanticRequirements.Apply(report, new(FutureNeeds: fact.Evidence, SemanticRequirements: plan));
            var c = Product("64 GB"); c.MemoryGb = 64;
            var workspace = new PurchaseWorkspace { Candidates = [c] };
            assert(CandidateTriage.Build(workspace, needs).Cases[0].Candidates[0].IssueText.Contains("严格大于 64"), "Confirmed source goal was deferred or weakened to non-strict");
            assert(workspace.Criteria.Count == 0 && !workspace.CoverageConfirmed, "Automatic initial screening created reviewed conditions");
        });
        check("Missing specification sources and damaged history do not fabricate mismatches or light routes", () =>
        {
            var c = Product("Unknown specs", storage: 128); c.Source = c.SpecificationSource = "";
            var rows = CandidateTriage.Build(new() { Candidates = [c], Criteria = [Bound("memory", 64)] }, report).Cases[0].Candidates;
            assert(rows[0].Issues.Length == 0 && rows[0].State.Contains("补齐规格来源"), "Unsourced fields became known mismatches");
            using var d = new HistoryDiagnostics(7); d.Add("fixture.jsonl", 1, "Synthetic issue");
            var damaged = report with { HistoryQuality = d.Build() };
            var result = CandidateTriage.Build(new() { Candidates = [Product("Candidate")] }, damaged);
            assert(!result.Cases[0].Candidates[0].ReasonText.Contains("轻载") && !result.Cases[0].Candidates[0].ReasonText.Contains("已记录占用")
                && result.Notice.Contains("历史读取"), "Damaged history supplied observed buying preferences");
        });
        check("Explicit graphics labels and VRAM constraints do not infer card type from names or shared memory", () =>
        {
            foreach (string text in new[] { "RTX 5060", "无需独立显卡", "不使用核显", "核显 不需要", "可选核显/独显" })
                assert(CandidateTriage.ExplicitGraphicsKind(text) == "unknown", "Free text or a model name became a hardware classification");
            assert(CandidateTriage.ExplicitGraphicsKind(" 核显 · Test ") == "integrated", "Explicit graphics label lost");
            var c = Product("Integrated"); c.VramGb = 12;
            var row = CandidateTriage.Build(new() { Candidates = [c], Criteria = [Bound("vram", 12)] }, report).Cases[0].Candidates[0];
            assert(row.IssueText.Contains("核显共享内存"), "A claimed shared-memory number satisfied dedicated VRAM");
        });
        check("Read-only triage and HTML preserve editing state and escape candidate sources", () =>
        {
            var c = Product("<script>alert(1)</script>"); c.SpecificationSource += " <unsafe>";
            var workspace = new PurchaseWorkspace { Candidates = [c], Criteria = [Bound("memory", 16)], CoverageConfirmed = true, ContextStamp = "unchanged" };
            string before = JsonSerializer.Serialize(workspace); string html = CandidateTriage.Html(CandidateTriage.Build(workspace, report));
            assert(before == JsonSerializer.Serialize(workspace), "Triage mutated criteria reviews, observations, order or coverage");
            assert(html.Contains("&lt;script&gt;") && !html.Contains("<script>") && html.Contains("&lt;unsafe&gt;") && html.Contains("候选自动初筛"), "Report omitted screening or inserted raw source HTML");
        });
    }
}
