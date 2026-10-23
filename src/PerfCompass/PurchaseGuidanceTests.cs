using System.IO;
using System.Text;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunPurchaseGuidanceTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        const string hardware = "purchase-guidance-synthetic-machine";
        const string scene = "合成取舍任务";
        Sample Row(int index, int day = 1) => new()
        {
            Time = now.AddDays(-day).AddSeconds(index * 10), Seconds = 10, Active = true,
            HardwareId = hardware, Scene = scene, ObservationVersion = ActivityRules.Version,
            Cpu = 5, CoreMax = 20, MemoryUsedGb = 8, MemoryPeakGb = 8, MemoryTotalGb = 64, CommitGb = 10,
            Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core", DiskUsedGb = 150, DiskTotalGb = 1024, MaxDiskPercent = 20
        };
        IEnumerable<Sample> Rows(int count = 30, int day = 1) => Enumerable.Range(0, count).Select(i => Row(i, day));
        AnalysisReport Analyze(IEnumerable<Sample> samples, int days = 30, string? selected = scene, Settings? settings = null, string? id = hardware)
            => AnalysisEngine.Analyze(samples, settings ?? new(), days, selected, now, id);
        static Advice Card(AnalysisReport report, string key) => report.Cards.Single(c => c.Key == key);
        static ConfigurationDecision Decision(AnalysisReport report, string key)
            => PurchaseGuidance.Build(report).Single(d => d.Key == key);
        static bool DirectDowngrade(ConfigurationDecision d)
            => d.Direction.StartsWith("优先比较较低档", StringComparison.Ordinal)
                || d.Direction.StartsWith("优先比较较少核心", StringComparison.Ordinal)
                || d.Direction.StartsWith("比较较低容量", StringComparison.Ordinal);
        void InStore(Action<LocalStore> action)
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-purchase-guidance-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(root) ?? throw new IOException("Cannot lease isolated purchase-guidance test data.");
            action(new LocalStore(root));
        }
        AnalysisReport History(LocalStore store) => HistoryAnalysis.Analyze(store, new(), 30, scene, now, hardware);
        AnalysisReport WithConditional(AnalysisReport report)
        {
            const string source = "如果以后转到本地训练，再增加显卡配置。";
            var fact = new SemanticFact("本地训练", "workload", "conditional", "future", "本地训练", "", "以后转到本地训练", source);
            return SemanticRequirements.Apply(report, new(FutureNeeds: source, SemanticRequirements:
                new(source, "synthetic-confirmed-model", "synthetic-test", SemanticRequirements.Version, [fact], now)));
        }

        check("Purchase guidance turns measured light use into scoped comparison actions without changing eligibility", () =>
        {
            var report = Analyze(Rows()); var decisions = PurchaseGuidance.Build(report);
            assert(!report.Ready && report.ActiveDays == 1 && !report.RepresentativeConfirmed,
                "the fixture unexpectedly passed the representative purchase gate");
            assert(Decision(report, "gpu").Direction == "优先比较较低档显卡"
                && Decision(report, "cpu").Direction.Contains("单核性能相近")
                && Decision(report, "memory").Direction == "按已测峰值筛选内存容量",
                "usable short observations stayed generic unknown or invented a memory tier");
            foreach (string key in new[] { "cpu", "memory", "gpu" })
            {
                var observation = Card(report, key).Observation!; var decision = Decision(report, key);
                assert(decision.Reason == observation.Evidence && decision.Scope == observation.Scope
                    && decision.NextStep == Card(report, key).DisplaySuggestion && decision.Level == "已测线索",
                    "the purchasing summary changed the scope, evidence or required verification");
            }
            assert(Decision(report, "gpu").Scope.Contains("其他引擎另核对")
                && decisions.Select(d => d.Priority).SequenceEqual(decisions.Select(d => d.Priority).Order()),
                "unmeasured engines became zero load or actionable decisions were not ordered");
            var deceptive = report with { Cards = report.Cards.Select(c => c with
                { Evidence = "这段文字含近期增加、压力、过剩等词，但没有结构化状态变化。" }).ToList() };
            assert(Decision(deceptive, "gpu") == Decision(report, "gpu"), "Chinese evidence text was parsed as a business state");
        });

        check("Purchase guidance preserves new compute work, busy cores and memory peaks over old light use", () =>
        {
            var heavy = Row(31) with
            {
                CoreMax = 97, MemoryPeakGb = 62, GpuEngineVersion = GpuEngineEvidence.Version,
                GpuEngines = [new("compute", ["D3D Compute_0"], 96) { Complete = false }]
            };
            var report = Analyze(Rows(300, 12).Concat(Rows()).Append(heavy));
            var gpu = Decision(report, "gpu"); var cpu = Decision(report, "cpu"); var memory = Decision(report, "memory");
            assert(Card(report, "gpu").Observation?.State == "busy" && gpu.Direction == "保留已测重任务，核对候选显卡表现"
                && gpu.Reason.Contains("计算") && gpu.Scope == Card(report, "gpu").Observation!.Scope,
                "quiet old/core readings hid a newly observed partial compute engine");
            assert(cpu.Direction == "优先核对繁忙任务的处理器性能" && memory.Direction.Contains("峰值任务")
                && !PurchaseGuidance.Build(report).Any(DirectDowngrade),
                "busy single-core or short memory work became a downgrade direction");
            var observedMemory = Analyze(Rows().Select(s => s with { CommitGb = 60 }));
            assert(Card(observedMemory, "memory").Observation?.State == "observed"
                && Decision(observedMemory, "memory").Direction == "按已测峰值筛选内存容量",
                "measured capacity demand disappeared or became an invented GB tier");
        });

        check("Purchase guidance gives final temporal and protected statuses priority over a retained light observation", () =>
        {
            var old = Rows(180, 8).Concat(Rows(180, 9));
            var recent = Rows(180, 1).Concat(Rows(180, 2)).Select(s => s with { Gpu = 85, GpuPeak = 85 });
            var report = Analyze(old.Concat(recent), 90); var gpu = Decision(report, "gpu");
            assert(Card(report, "gpu").Status == "changed" && gpu.Direction == "优先核对近期增加的任务需求"
                && gpu.Scope == "所选 90 天 · " + scene && gpu.Reason == Card(report, "gpu").Evidence,
                "a large low-use baseline erased the real recent increase or changed its evidence scope");
            var light = Analyze(Rows());
            foreach (string status in new[] { "pressure", "changed", "experience", "requirements", "constraints" })
            {
                var changed = light with { Cards = light.Cards.Select(c => c.Key == "gpu" ? c with
                    { Status = status, Evidence = "final-evidence-" + status, Suggestion = "final-action-" + status } : c).ToList() };
                var selected = Decision(changed, "gpu");
                assert(!DirectDowngrade(selected) && selected.Reason == "final-evidence-" + status
                    && selected.NextStep == "final-action-" + status && selected.Scope == "所选 30 天 · " + scene
                    && selected.Priority < Decision(changed, "cpu").Priority,
                    "a stale seven-day light observation leaked through the final protected state " + status);
            }
        });

        check("Purchase guidance follows real experience and requirement application while retaining conditional checks", () =>
        {
            var report = Analyze(Rows());
            var experience = ExperienceAnalyzer.Attach(report,
                [new("guidance-experience", now.AddHours(-1), hardware, scene, "任务等待过久", "合成体验", [])]);
            assert(Card(experience, "gpu").Status == "experience" && !DirectDowngrade(Decision(experience, "gpu"))
                && Decision(experience, "gpu").Reason == Card(experience, "gpu").Evidence,
                "a real unresolved experience was reduced to the old low-load observation");
            const string source = "准备做本地AI推理";
            var settings = new Settings(FutureNeeds: source, Requirements: RequirementParser.Confirm(RequirementParser.Parse(source), "local"));
            var requirements = RequirementParser.Apply(report, settings);
            assert(Card(requirements, "gpu").Status == "requirements" && !DirectDowngrade(Decision(requirements, "gpu"))
                && Decision(requirements, "gpu").NextStep == Card(requirements, "gpu").Suggestion,
                "a confirmed future task failed to constrain the comparison direction");
            var conditional = WithConditional(report); var decision = Decision(conditional, "gpu");
            assert(DirectDowngrade(decision) && decision.NextStep == Card(conditional, "gpu").DisplaySuggestion
                && decision.NextStep.Contains("分别比较条件需求成立与不成立")
                && PurchaseGuidance.Build(conditional)[0].Key == "constraints",
                "conditional requirements were silently dropped or turned into unconditional demand");

            // No recent reading: the selected-window task still displays through
            // RetainedWorkload. New confirmed needs and unresolved experience
            // must replace that action, not merely append a hidden Suggestion.
            var retainedOnly = Analyze([Row(0, 8) with { Gpu = 95, GpuPeak = 95 }]);
            assert(Card(retainedOnly, "gpu") is { Observation: null, UsesRetainedWorkload: true, Status: "unknown" },
                "the fixture did not isolate retained-only historical task evidence");
            var retainedExperience = ExperienceAnalyzer.Attach(retainedOnly,
                [new("guidance-retained-experience", now.AddHours(-1), hardware, scene, "任务等待过久", "合成体验", [])]);
            var retainedRequirements = RequirementParser.Apply(retainedOnly, settings);
            foreach (var (applied, status) in new[] { (retainedExperience, "experience"), (retainedRequirements, "requirements") })
            {
                var gpu = Card(applied, "gpu"); var summary = Decision(applied, "gpu");
                assert(gpu.Status == status && gpu.Observation == null && gpu.RetainedWorkload != null
                    && !gpu.UsesRetainedWorkload && summary.Reason == gpu.Evidence && summary.NextStep == gpu.Suggestion
                    && summary.NextStep != gpu.RetainedWorkload.Action && summary.Scope == "所选 30 天 · " + scene,
                    "retained-only work hid a real " + status + " action when recent observation was absent");
            }
            var combined = RequirementParser.Apply(retainedExperience, settings);
            assert(Card(combined, "gpu").Status == "experience" && Decision(combined, "gpu").NextStep == Card(combined, "gpu").Suggestion,
                "the rule requirement pass displaced an existing unresolved experience");
            var retainedConditional = WithConditional(retainedOnly);
            assert(Card(retainedConditional, "gpu").Status == "unknown" && Card(retainedConditional, "gpu").UsesRetainedWorkload
                && Decision(retainedConditional, "gpu").NextStep.Contains("分别比较条件需求成立与不成立"),
                "retained-only conditional work became mandatory or lost its separate verification");
        });

        check("Purchase guidance retains older selected-window heavy work and lets newer engine work take display priority", () =>
        {
            var report = Analyze(Rows().Append(Row(0, 12) with { Gpu = 97, GpuPeak = 97 }));
            var card = Card(report, "gpu"); var decision = Decision(report, "gpu");
            assert(card.Observation?.State == "light" && card.UsesRetainedWorkload && !DirectDowngrade(decision)
                && decision.Scope == card.RetainedWorkload!.Scope && decision.Reason == card.RetainedWorkload.Evidence
                && decision.NextStep == card.DisplaySuggestion && decision.Direction.Contains("保留所选时段"),
                "recent quiet use erased an older measured task still inside the selected window");
            var conditional = WithConditional(report);
            assert(Decision(conditional, "gpu").NextStep == Card(conditional, "gpu").DisplaySuggestion
                && Decision(conditional, "gpu").NextStep.Contains("条件"), "retained work lost conditional verification");
            var busy = Analyze(Rows().Append(Row(0, 12) with { Gpu = 97, GpuPeak = 97 }).Append(Row(31) with
                { GpuEngineVersion = GpuEngineEvidence.Version, GpuEngines = [new("encode", ["D3D Video Encode"], 96) { Complete = false }] }));
            assert(Card(busy, "gpu").Observation?.State == "busy" && Decision(busy, "gpu").Reason == Card(busy, "gpu").Observation!.Evidence,
                "retained core work hid more recent measured engine work");
        });

        check("Purchase guidance withdraws savings on real damaged or overlapping history but retains independent confirmed conditions", () =>
        {
            InStore(store =>
            {
                foreach (var row in Rows()) store.Append(row);
                assert(DirectDowngrade(Decision(History(store), "gpu")), "valid history did not establish the comparison direction");
                string file = Path.Combine(store.Root, $"samples-{Row(0).Time.LocalDateTime:yyyy-MM-dd}.jsonl");
                File.AppendAllText(file, "{broken-guidance\n", new UTF8Encoding(false));
                byte[] original = File.ReadAllBytes(file); var damaged = History(store);
                assert(damaged.HistoryQuality.Issues.Single().Line == 31 && !PurchaseGuidance.Build(damaged).Any(DirectDowngrade)
                    && Decision(damaged, "gpu").Direction.Contains("历史异常") && File.ReadAllBytes(file).SequenceEqual(original),
                    "damaged history kept a savings direction, lost its location or changed the source");
                var conditional = WithConditional(damaged);
                assert(Decision(conditional, "constraints").Level == "已确认条件"
                    && Decision(conditional, "constraints").Reason == Card(conditional, "constraints").Evidence,
                    "history protection hid independently confirmed conditions");
                store.Append(Row(31) with { Gpu = 96, GpuPeak = 96 });
                var observed = History(store);
                assert(Card(observed, "gpu").Observation?.State == "busy" && Decision(observed, "gpu").Level == "已测线索",
                    "one damaged row hid separately valid observed heavy work");
            });
            InStore(store =>
            {
                foreach (var row in Rows()) store.Append(row);
                store.Append(Row(1) with { Time = Row(1).Time.AddSeconds(-5), Gpu = 98, GpuPeak = 98 });
                var report = History(store);
                assert(report.HistoryQuality.HasIntervalConflicts && !PurchaseGuidance.Build(report).Any(DirectDowngrade)
                    && Decision(report, "gpu").Level == "待核对", "overlapping intervals produced a savings or observed-pressure conclusion");
            });
        });

        check("Purchase guidance gives specific missing-data tasks and never borrows unidentified hardware", () =>
        {
            var empty = Analyze([]); var decisions = PurchaseGuidance.Build(empty);
            assert(decisions.Count == 4 && decisions.All(d => d.Level == "待补测" && d.Scope == "所选 30 天 · " + scene)
                && decisions.Select(d => d.NextStep).Distinct().Count() == 4 && !decisions.Any(DirectDowngrade)
                && Decision(empty, "disk").Reason == "当前时间范围内尚无记录。",
                "empty history manufactured disk capacity, generic unknown actions or a savings claim");
            var noCards = PurchaseGuidance.Build(empty with { Cards = [] });
            assert(noCards.Count == 4 && noCards.All(d => d.Level == "待补测"), "a cardless empty report left no actionable entry point");
            var unknownHardware = Analyze(Rows().Select(s => s with { HardwareId = "" }), id: null);
            assert(string.IsNullOrWhiteSpace(unknownHardware.HardwareId)
                && PurchaseGuidance.Build(unknownHardware).All(d => d.Direction == "先隔离当前硬件配置的记录")
                && !PurchaseGuidance.Build(unknownHardware).Any(DirectDowngrade), "unidentified hardware created a purchase scope");
            var sparse = Analyze(Rows(29));
            assert(Card(sparse, "gpu").Observation == null && Decision(sparse, "gpu").Level == "待补测",
                "the summary invented an observation below the existing measurement gate");
        });

        check("Purchase guidance keeps selected scenes and GPU source boundaries without scanning other scene profiles", () =>
        {
            var selected = Rows();
            var other = Rows().Select(s => s with { Scene = "另一个合成场景", Gpu = 99, GpuPeak = 99, GpuMetricSource = "D3D 3D" });
            var report = Analyze(selected.Concat(other));
            assert(report.SceneProfiles.Count == 2 && DirectDowngrade(Decision(report, "gpu"))
                && Decision(report, "gpu").Scope == Card(report, "gpu").Observation!.Scope
                && !Decision(report, "gpu").Scope.Contains("另一个合成场景"),
                "another scene's heavy workload or source replaced the selected scene's scope");
            var mixed = Analyze(Rows().Select((s, i) => i < 15 ? s : s with { GpuMetricSource = "D3D 3D" }));
            assert(mixed.GpuMetricScopes.Length == 2 && !DirectDowngrade(Decision(mixed, "gpu"))
                && Decision(mixed, "gpu").Direction.Contains("同一采集来源"), "mixed source values were pooled into a GPU savings claim");
        });

        check("Purchase guidance preserves established representative surplus and review evidence without deriving new capacity numbers", () =>
        {
            var rows = Enumerable.Range(1, 3).SelectMany(day => Rows(1440, day));
            var report = Analyze(rows, settings: WorkloadCoverage.Select(new(), true, hardware));
            assert(report.Ready && Card(report, "memory").Status == "surplus" && Card(report, "gpu").Status == "review",
                "the representative fixture did not reach the existing formal statuses");
            foreach (string key in new[] { "cpu", "memory", "gpu" })
            {
                var card = Card(report, key); var decision = Decision(report, key);
                assert(DirectDowngrade(decision) && decision.Level == card.StatusLabel && decision.Reason == card.Evidence
                    && decision.NextStep == card.Suggestion && decision.Scope == "所选 30 天 · " + scene,
                    "the summary replaced established advice with a new threshold or numeric capacity estimate");
            }
        });
    }
}
