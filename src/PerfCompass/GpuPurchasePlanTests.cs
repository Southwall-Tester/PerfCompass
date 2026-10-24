namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuPurchasePlanTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        const string hardware = "synthetic-gpu-shopping";
        IEnumerable<Sample> Rows() => Enumerable.Range(0, 60).Select(i => new Sample
        {
            Time = now.AddHours(-1).AddSeconds(i * 10), Seconds = 10, Active = true, HardwareId = hardware,
            Cpu = 5, CoreMax = 10, MemoryUsedGb = 5, MemoryTotalGb = 32, CommitGb = 7,
            Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core", ObservationVersion = ActivityRules.Version
        });
        AnalysisReport Analyze(IEnumerable<Sample> rows) => AnalysisEngine.Analyze(rows, new(), 30, null, now, hardware);
        var light = Analyze(Rows());
        check("GPU shopping compares integrated and lower discrete systems without certifying fit", () =>
        {
            var plan = GpuPurchasePlan.Build(light);
            assert(plan.Mode == "light" && plan.Routes[0].Name == "核显整机" && plan.Routes[1].Name == "较低档独显整机",
                "light observations failed to open a concrete lower-cost comparison route");
            assert(!light.Ready && light.Cards.Single(c => c.Key == "gpu").Status == "unknown"
                && plan.Routes.All(r => !r.Order.Contains("已达标")) && plan.NextStep.Contains("通过目标后"),
                "shopping priority incorrectly changed qualification or certified an integrated GPU");
            assert(PurchaseGuidance.Build(light).Single(d => d.Key == "gpu").ComparisonAction == plan.Title,
                "summary and GPU detail use different shopping plans");
        });
        check("GPU shopping separates media-only heavy engines from graphics and compute work", () =>
        {
            foreach (string kind in new[] { "encode", "decode", "processing", "video", "media" })
            {
                var p = new GpuEngineProfile(kind, GpuEngineEvidence.Label(kind), .1, .1, 96, 99, 1, ["synthetic"]);
                var report = light with { GpuEngineProfiles = [p] };
                var plan = GpuPurchasePlan.Build(report);
                assert(plan.Mode == "media" && plan.NextStep.Contains("同质量") && plan.NextStep.Contains("并发路数"), "media route lost actual output constraints: " + kind);
                foreach (string other in new[] { "core", "3d", "compute", "render-compute", "copy" })
                    assert(GpuPurchasePlan.Build(report with { GpuEngineProfiles = [p, p with { Kind = other }] }).Mode == "workload",
                        "a non-media heavy task was hidden by a media engine: " + other);
            }
        });
        check("GPU shopping preserves selected-period heavy tasks and confirmed new requirements", () =>
        {
            var heavy = Analyze(Rows().Append(Rows().First() with { Time = now.AddDays(-12), Gpu = 97, GpuPeak = 97 }));
            var plan = GpuPurchasePlan.Build(heavy);
            assert(plan.Mode == "workload" && plan.Routes[0].Name.Contains("当前级别") && plan.Evidence.Contains("97"),
                "recent light use erased earlier selected-period work");
            var media = heavy with { GpuEngineProfiles = [new("encode", "编码", .1, .1, 95, 99, 1, ["synthetic"])],
                Cards = heavy.Cards.Select(c => c.Key == "gpu" ? c with { Observation = new("busy", "media", "media", "media", "media", .1, 1) } : c).ToList() };
            assert(!media.Cards.Single(c => c.Key == "gpu").UsesRetainedWorkload && GpuPurchasePlan.Build(media).Mode == "workload",
                "a busy media observation hid retained earlier graphics work");
            const string source = "准备做本地AI推理";
            var settings = new Settings(FutureNeeds: source, Requirements: RequirementParser.Confirm(RequirementParser.Parse(source), "local"));
            var required = RequirementParser.Apply(light, settings);
            assert(GpuPurchasePlan.Build(required).Mode == "requirements" && GpuPurchasePlan.Build(required).Needs.Any(n => n.Contains(source)),
                "new confirmed work lost its source or priority");
        });
        check("GPU shopping retains separate conditional and omitted semantic requirements", () =>
        {
            const string source = "如果接到剪辑单就需要4K导出；不再玩游戏";
            var fact = new SemanticFact("4K导出", "workload", "conditional", "future", "", "", "接到剪辑单", "如果接到剪辑单就需要4K导出");
            var omit = new SemanticFact("游戏", "workload", "excluded", "past", "", "", "", "不再玩游戏") { DecisionUse = "omit" };
            var report = light with { SemanticRequirements = new(source, "synthetic", "synthetic", SemanticRequirements.Version, [fact, omit], now) };
            var plan = GpuPurchasePlan.Build(report);
            assert(plan.Needs.Length == 1 && plan.Needs[0].Contains("条件：接到剪辑单") && plan.Needs[0].Contains(fact.Evidence),
                "condition was made unconditional or an omitted fact became a requirement");
        });
        check("GPU shopping does not turn absent readings or mixed sources into cheaper-system qualification", () =>
        {
            assert(GpuPurchasePlan.Build(Analyze([])).Mode == "unknown", "empty history suggested light use");
            assert(GpuPurchasePlan.Build(light with { HardwareId = "" }).Mode == "unknown", "mixed hardware admitted");
            assert(GpuPurchasePlan.Build(light with { GpuMetricScopes = ["first", "second"] }).Mode == "unknown", "mixed GPU sources admitted");
            foreach (string state in new[] { "changed", "requirements", "meaning", "experience", "constraints" })
                assert(GpuPurchasePlan.Build(light with { Cards = light.Cards.Select(c => c.Key == "gpu" ? c with { Status = state } : c).ToList() }).Mode == "requirements",
                    "protected input was overwritten by light use: " + state);
        });
        check("GPU shopping report includes the same routes and escapes requirement evidence", () =>
        {
            const string source = "<project & export>";
            var fact = new SemanticFact(source, "workload", "required", "future", "", "", "", source);
            var report = light with { SemanticRequirements = new(source, "synthetic", "synthetic", SemanticRequirements.Version, [fact], now) };
            string html = ReportExporter.Html(report, null);
            assert(html.Contains("核显与独显的比较顺序") && html.Contains("&lt;project &amp; export&gt;") && !html.Contains("<project & export>"),
                "report lost the shopping worksheet or did not escape evidence");
        });
    }
}
