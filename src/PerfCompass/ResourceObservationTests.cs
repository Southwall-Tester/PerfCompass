using System.IO;
using System.Text;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunResourceObservationTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        const string hardware = "resource-observation-synthetic-machine";
        Sample Row(int index, int day = 1) => new()
        {
            Time = now.AddDays(-day).AddSeconds(index * 10), Seconds = 10, Active = true,
            HardwareId = hardware, Scene = "合成办公", ObservationVersion = ActivityRules.Version,
            Cpu = 5, CoreMax = 20, MemoryUsedGb = 8, MemoryPeakGb = 8, MemoryTotalGb = 64, CommitGb = 10,
            Gpu = 3, GpuPeak = 3, GpuMetricSource = "GPU Core"
        };
        List<Sample> Rows(int count = 30, int day = 1) => Enumerable.Range(0, count).Select(i => Row(i, day)).ToList();
        Sample Engine(Sample s, string kind, double? load, bool complete = true, string? source = null) => s with
        {
            GpuEngineVersion = GpuEngineEvidence.Version,
            GpuEngines = [new(kind, [source ?? (kind == "compute" ? "D3D Compute_0" : "D3D Video Encode")], load) { Complete = complete }]
        };
        AnalysisReport Analyze(IEnumerable<Sample> samples, Settings? settings = null, int days = 30, string? scene = null, string? id = hardware)
            => AnalysisEngine.Analyze(samples, settings ?? new(), days, scene, now, id);
        static Advice Card(AnalysisReport report, string key) => report.Cards.Single(c => c.Key == key);
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-7;
        void InStore(Action<LocalStore> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "PerfCompass-resource-observation-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(path) ?? throw new IOException("Cannot lease isolated observation test directory.");
            action(new LocalStore(path));
        }
        AnalysisReport History(LocalStore store) => HistoryAnalysis.Analyze(store, new(), 30, asOf: now, hardwareId: hardware);

        check("Five minutes of one-day readings gives scoped observations without purchase eligibility", () =>
        {
            var shortReport = Analyze(Rows(29));
            assert(shortReport.Cards.All(c => c.Observation == null), "less than five minutes acquired a low-load observation");
            var report = Analyze(Rows());
            assert(!report.Ready && !report.RepresentativeConfirmed && report.ActiveDays == 1,
                "a short observation bypassed representative use or the multi-day purchase gate");
            foreach (string key in new[] { "cpu", "memory", "gpu" })
            {
                var card = Card(report, key); var observation = card.Observation;
                assert(card.Status == "unknown" && observation?.State == "light" && Near(observation.ObservedHours, 300d / 3600)
                    && observation.Coverage == 1 && observation.Scope.Contains("最近 7 天"), "five-minute evidence was hidden behind the purchase gate or used the wrong scope");
            }
            assert(Card(report, "gpu").Observation!.Title.Contains("核心/3D") && Card(report, "gpu").Observation!.Scope.Contains("其他引擎另核对"),
                "low core readings became a whole-GPU surplus claim");
            assert(!Card(report, "memory").Observation!.Action.Contains("32") && Card(report, "memory").Status != "surplus",
                "initial memory headroom recommended a lower capacity");
            assert(Card(report, "disk").Observation == null, "CPU-style utilization invented an SSD headroom conclusion");
        });
        check("A single busy logical core or measured CPU burst survives a low aggregate mean", () =>
        {
            foreach (var busy in new[]
            {
                Row(0) with { CoreMax = 100 },
                Row(0) with { CpuHighSeconds = .2 },
                Row(0) with { Cpu = null, CoreMax = 95 },
                Row(0) with { Cpu = 85 }
            })
            {
                var report = Analyze([busy]); var observation = Card(report, "cpu").Observation;
                assert(observation?.State == "busy" && !observation.Title.Contains("持续") && Card(report, "cpu").Status != "pressure",
                    "short/core-only CPU work was hidden, declared low or mislabeled sustained pressure");
            }
            assert(Card(Analyze(Rows().Select(s => s with { CoreMax = 75 })), "cpu").Observation?.State == "observed",
                "moderate core peaks were collapsed into either idle or sustained pressure");
            assert(Card(Analyze([Row(0) with { Cpu = null, CoreMax = null, CpuHighSeconds = 1 }]), "cpu").Observation == null,
                "duration metadata without a valid measured CPU value invented busy evidence");
            var subsecond = Card(Analyze([Row(0) with { Cpu = 100, Seconds = .25 }]), "cpu").Observation;
            assert(subsecond?.State == "busy" && subsecond.Coverage == 1 && Near(subsecond.ObservedHours, .25 / 3600),
                "a subsecond reading was assigned an invented one-second coverage denominator");
        });
        check("Memory observations preserve short peaks, commit constraints and each sample's physical capacity", () =>
        {
            var peakRows = Rows(); peakRows[^1] = peakRows[^1] with { MemoryPeakGb = 62 };
            var peak = Card(Analyze(peakRows), "memory");
            assert(peak.Observation?.State == "busy" && peak.Observation.Evidence.Contains("62.0") && peak.Status != "pressure",
                "a short physical-memory peak was averaged away or called sustained");
            var commit = Card(Analyze(Rows().Select(s => s with { CommitGb = 60 })), "memory");
            assert(commit.Observation?.State == "observed" && commit.Limits.Contains("提交量不等于物理需求"),
                "high commitment became physical capacity surplus or a proven memory bottleneck");
            var capacities = Rows().Select((s, i) => i < 15 ? s with { MemoryTotalGb = 16, MemoryUsedGb = 12, MemoryPeakGb = 12, CommitGb = 13 } : s).ToList();
            var scoped = Card(Analyze(capacities), "memory").Observation;
            assert(scoped?.State == "observed" && scoped.Evidence.Contains("4.0 GiB"),
                "the last larger total was used to erase an earlier limited physical headroom");
            assert(Card(Analyze(Rows().Select(s => s with { CommitGb = null })), "memory").Observation == null,
                "missing commit data supplied a low-memory conclusion");
        });
        check("Known compute and encode work supersedes quiet GPU core even with partial or very brief node readings", () =>
        {
            foreach (string kind in new[] { "compute", "encode" })
                foreach (bool complete in new[] { true, false })
                {
                    var rows = Rows().Select(s => s with { GpuEngineVersion = GpuEngineEvidence.Version, GpuEngines = new List<GpuEngineSample>() }).ToList();
                    rows[^1] = Engine(rows[^1], kind, 96, complete) with { Seconds = .25 };
                    var report = Analyze(rows); var observation = Card(report, "gpu").Observation;
                    assert(observation?.State == "busy" && observation.Evidence.Contains(GpuEngineEvidence.Label(kind))
                        && !observation.Title.Contains("持续") && Card(report, "gpu").Status != "pressure",
                        "a partial or short heavy engine was diluted by quiet legacy GPU readings");
                    assert(report.History.All(h => h.Gpu == 3), "engine expansion rewrote legacy core/3D history");
                    var mixed = Analyze(rows.Select((s, i) => i < rows.Count - 1 ? s with { GpuEngineVersion = "", GpuEngines = null } : s));
                    assert(mixed.GpuHistoryHasMixedSources && mixed.History.All(h => h.Gpu == null)
                        && Card(mixed, "gpu").Observation?.State == "busy",
                        "a legacy/current protocol mixture retained a combined core curve or erased a valid heavy-engine clue");
                }
            var partialOnly = Rows().Select(s => Engine(s with { Gpu = null, GpuPeak = null }, "compute", 2, false));
            assert(Card(Analyze(partialOnly), "gpu").Observation == null, "partial low nodes claimed whole-category light load");
        });
        check("Long idle history cannot erase new heavy work and old engines do not masquerade as recent readings", () =>
        {
            var old = Rows(4000, 12);
            var recent = new[] { Engine(Row(0), "encode", 100, false) };
            var report = Analyze(old.Concat(recent));
            assert(Card(report, "gpu").Observation?.State == "busy" && report.GpuEngineProfiles.Single(p => p.Kind == "encode").Peak == 100,
                "old idle records cancelled a newly observed heavy task");
            var oldHeavy = old.Select((s, i) => i == 0 ? Engine(s, "compute", 100) : s);
            var preserved = Card(Analyze(oldHeavy.Concat(Rows())), "gpu").Observation;
            assert(preserved?.State == "busy" && preserved.Scope.Contains("所选 30 天") && !preserved.Scope.Contains("最近 7 天"),
                "historical engine evidence was discarded or silently relabeled as recent");
            assert(Analyze(old).Cards.All(c => c.Observation == null), "stale quiet readings created fresh low-load observations");
        });
        check("Current observations need no fabricated previous period and remain scoped to the selected scene", () =>
        {
            var report = Analyze(Rows());
            assert(report.DemandTrends.All(t => t.Status == "insufficient" && t.PreviousHours == 0 && t.PreviousP95 == null),
                "new observations fabricated a prior idle period or changed the comparison gates");
            var all = Rows().Concat(Rows(2).Select(s => s with { Scene = "合成编译", Cpu = 100 }));
            var office = Analyze(all, scene: "合成办公"); var compiling = Analyze(all, scene: "合成编译");
            assert(Card(office, "cpu").Observation?.State == "light" && Card(office, "cpu").Observation!.Scope.Contains("合成办公")
                && Card(compiling, "cpu").Observation?.State == "busy" && Card(compiling, "gpu").Observation == null,
                "another scene supplied low-load duration or erased the selected task's busy evidence");
        });
        check("GPU source or protocol mixtures cannot produce light observations but retain known high-node clues", () =>
        {
            var mixedCore = Rows().Select((s, i) => i < 15 ? s : s with { GpuMetricSource = "D3D 3D" }).ToList();
            var report = Analyze(mixedCore);
            assert(Card(report, "gpu").Observation == null && report.GpuMetricScopes.Length == 2,
                "mixed GPU core/3D definitions yielded a light observation");
            var mixedEngine = Rows().Select((s, i) => Engine(s, "compute", 2, source: i < 15 ? "D3D Compute_0" : "D3D Compute_1")).ToList();
            report = Analyze(mixedEngine);
            assert(Card(report, "gpu").Observation == null && report.GpuEngineProfiles.Single(p => p.Kind == "compute").P95 == null,
                "changing exposed nodes retained a low-load conclusion or combined P95");
            mixedEngine[^1] = Engine(mixedEngine[^1], "compute", 99, false, "D3D Compute_1");
            assert(Card(Analyze(mixedEngine), "gpu").Observation?.State == "busy", "mixed-source protection erased known heavy-node evidence");
            var protocol = Rows().Select((s, i) => s with { GpuEngineVersion = i < 15 ? "gpu-engines-1.0" : GpuEngineEvidence.Version, GpuEngines = [] });
            assert(Card(Analyze(protocol), "gpu").Observation == null, "protocol changes bypassed GPU source isolation");
        });
        check("Measured duration and component-specific coverage govern low observations while missing intervals cannot erase a busy clue", () =>
        {
            var full = Rows(34);
            var enough = Analyze(full.Select(s => s with { CpuCoverage = 9.5, CoreCoverage = 9.5, MemoryCoverage = 9.5, CommitCoverage = 9.5, GpuCoverage = 9 }));
            assert(new[] { "cpu", "memory", "gpu" }.All(k => Card(enough, k).Observation?.State == "light"),
                "the documented 95/95/90 percent component thresholds rejected sufficiently long readings");
            var missing = Analyze(full.Select(s => s with { CpuCoverage = 9.4, CoreCoverage = 9.4, MemoryCoverage = 9.4, CommitCoverage = 9.4, GpuCoverage = 8.9 }));
            assert(missing.Cards.All(c => c.Observation == null), "insufficient component coverage was filled with zero load");
            var onlyActiveFive = Analyze(Rows().Select(s => s with { CpuCoverage = 9.5, CoreCoverage = 9.5, MemoryCoverage = 9.5, CommitCoverage = 9.5, GpuCoverage = 9 }));
            assert(onlyActiveFive.Cards.All(c => c.Observation == null), "active duration replaced the required five minutes of actual component readings");
            var gap = full.Select((s, i) => i == 0 ? s with { Gpu = 97, GpuPeak = 97 } : s with { Gpu = null, GpuPeak = null });
            assert(Card(Analyze(gap), "gpu").Observation?.State == "busy", "missing GPU intervals erased a valid short heavy reading");
        });
        check("Actual history entry preserves observations, rejects corrupted surplus evidence and clears overlapping observations", () =>
        {
            InStore(store =>
            {
                foreach (var sample in Rows()) store.Append(sample);
                var initial = History(store);
                assert(!initial.HistoryQuality.HasIssues && Card(initial, "cpu").Observation?.State == "light",
                    "the production history path failed to retain five-minute observation evidence");
                string file = Path.Combine(store.Root, $"samples-{Row(0).Time.LocalDateTime:yyyy-MM-dd}.jsonl");
                File.AppendAllText(file, "{broken-observation\n", new UTF8Encoding(false));
                byte[] bytes = File.ReadAllBytes(file); var damaged = History(store);
                assert(damaged.HistoryQuality.IssueCount == 1 && damaged.HistoryQuality.Issues.Single().Line == 31
                    && damaged.Cards.All(c => c.Observation?.State != "light") && File.ReadAllBytes(file).SequenceEqual(bytes),
                    "a damaged history kept light observations, lost its exact location or rewrote the original");
                var busy = Engine(Row(31), "compute", 100, false); store.Append(busy);
                assert(Card(History(store), "gpu").Observation?.State == "busy", "history corruption erased separately valid positive task evidence");
            });
            InStore(store =>
            {
                foreach (var sample in Rows()) store.Append(sample);
                store.Append(Row(1) with { Time = Row(1).Time.AddSeconds(-5), Cpu = 100, Gpu = 99, GpuPeak = 99 });
                var report = History(store);
                assert(report.HistoryQuality.HasIntervalConflicts && report.Cards.All(c => c.Observation == null),
                    "overlapping records retained an observation with unassignable load shares");
            });
            InStore(store =>
            {
                foreach (var sample in Rows(20)) { store.Append(sample); store.Append(sample); }
                var report = History(store);
                assert(report.HistoryQuality.DuplicateCount == 20 && Near(report.ActiveHours, 200d / 3600)
                    && report.Cards.All(c => c.Observation == null), "duplicate rows manufactured the five-minute observation threshold");
            });
        });
        check("Mixed or absent hardware identity, zero intervals and inactive records cannot manufacture observations", () =>
        {
            var mixed = Rows().Concat([Row(31) with { HardwareId = "other-machine", Cpu = 100 }]);
            assert(Analyze(mixed, id: null).Cards.All(c => c.Observation == null), "mixed hardware generated an observation");
            assert(Analyze(Rows().Select(s => s with { HardwareId = "" }), id: null).Cards.All(c => c.Observation == null),
                "unknown hardware generated an observation");
            foreach (var rows in new[] { Rows().Select(s => s with { Seconds = 0, Cpu = 100, Gpu = 100 }), Rows().Select(s => s with { Active = false, Cpu = 100, Gpu = 100 }) })
                assert(Analyze(rows).Cards.All(c => c.Observation == null), "inactive or zero-duration readings carried observation evidence");
            assert(Card(Analyze(mixed), "cpu").Observation?.State == "light", "explicit hardware isolation failed to retain the selected machine's observations");
        });
        check("Representative multi-day coverage retains the existing formal advice gates independently of preliminary observations", () =>
        {
            var rows = Enumerable.Range(1, 3).SelectMany(day => Rows(1440, day)).ToList();
            var report = Analyze(rows, WorkloadCoverage.Select(new(), true, hardware));
            assert(report.Ready && Card(report, "cpu").Status == "surplus" && Card(report, "memory").Status == "surplus"
                && Card(report, "gpu").Status == "review" && Card(report, "gpu").Observation?.State == "light",
                "the observation axis changed established representative-workload purchase statuses");
            var unconfirmed = Analyze(rows);
            assert(!unconfirmed.Ready && unconfirmed.Cards.All(c => c.Status is not ("surplus" or "review"))
                && Card(unconfirmed, "cpu").Observation?.State == "light", "unconfirmed task coverage hid valid observations or authorized downgrade advice");
        });
        check("Observation titles and candidate stamps stay stable under numeric drift but react to changed observed behavior", () =>
        {
            var report = Analyze(Rows()); var next = Analyze(Rows(31).Select(s => s with { Cpu = 6, CoreMax = 22, Gpu = 4, GpuPeak = 4 }));
            assert(report.Cards.Select(c => c.Observation?.Title).SequenceEqual(next.Cards.Select(c => c.Observation?.Title)),
                "percentages or elapsed time made observation titles unstable");
            assert(CandidateComparison.Stamp(report) == CandidateComparison.Stamp(next),
                "unchanged observation scope and state invalidated candidate review on each numeric update");
            var changed = Analyze(Rows().Select((s, i) => i == 0 ? s with { CoreMax = 100 } : s));
            assert(Card(changed, "cpu").Status == Card(report, "cpu").Status && CandidateComparison.Stamp(report) != CandidateComparison.Stamp(changed),
                "a new busy observation with unchanged purchase status failed to invalidate candidate scope");
        });
        check("Pending experience and confirmed future requirements take display priority over short low-load observations", () =>
        {
            var report = Analyze(Rows());
            var pending = new ExperienceEvent("observation-pending", now.AddHours(-1), hardware, "合成办公", "任务等待过久", "合成体验核对", []);
            var experience = ExperienceAnalyzer.Attach(report, [pending]);
            foreach (string key in new[] { "cpu", "memory", "gpu" })
            {
                var card = Card(experience, key);
                assert(card.Status == "experience" && card.Observation?.State == "light" && !card.UsesObservation
                    && card.DisplayTitle == card.Title && card.DisplaySuggestion == card.Suggestion,
                    "the preliminary low title hid an unresolved experience or erased the observed facts");
            }
            var resolved = ExperienceAnalyzer.Attach(report, [pending with { Resolved = true }]);
            assert(Card(resolved, "gpu").UsesObservation, "a resolved experience permanently hid preliminary observations");
            const string source = "准备做本地AI推理";
            var settings = new Settings(FutureNeeds: source, Requirements: RequirementParser.Confirm(RequirementParser.Parse(source), "local"));
            var requirements = RequirementParser.Apply(report, settings);
            foreach (string key in new[] { "gpu", "memory" })
            {
                var card = Card(requirements, key);
                assert(card.Status == "requirements" && card.Observation?.State == "light" && !card.UsesObservation
                    && card.DisplayTitle == card.Title && card.DisplaySuggestion == card.Suggestion,
                    "short low readings displaced a confirmed new task in the visible main conclusion");
            }
            // Match the production ordering: preserved light facts must not let
            // the later requirement pass replace an unresolved experience title.
            var combined = SemanticRequirements.Apply(RequirementParser.Apply(ExperienceAnalyzer.Attach(report, [pending]), settings), settings);
            foreach (string key in new[] { "gpu", "memory" })
            {
                var card = Card(combined, key);
                assert(card.Status == "experience" && card.Observation?.State == "light" && !card.UsesObservation
                    && card.DisplayTitle == Card(experience, key).DisplayTitle && card.Evidence.Contains("已确认需求")
                    && combined.Summary.Contains("体验问题"),
                    "the real experience-then-requirement pipeline lost its higher-priority unresolved experience");
                string html = ReportExporter.Html(combined, null);
                assert(html.Contains("<h2>" + System.Net.WebUtility.HtmlEncode(card.DisplayTitle) + "</h2>")
                    && !html.Contains("<h2>" + System.Net.WebUtility.HtmlEncode(card.Observation!.Title) + "</h2>"),
                    "HTML restored a low-load main heading hidden by the native protected advice");
            }
            foreach (string state in new[] { "pressure", "changed", "experience", "requirements", "constraints" })
            {
                var higher = report with { Cards = report.Cards.Select(c => c with { Status = state, Title = "优先主判定-" + state }).ToList() };
                var retained = RequirementParser.Apply(ExperienceAnalyzer.Attach(higher, [pending]), settings);
                var gpu = Card(retained, "gpu");
                assert(gpu.Status == state && gpu.DisplayTitle == "优先主判定-" + state && !gpu.UsesObservation
                    && gpu.Observation?.State == "light" && gpu.Evidence.Contains("已确认需求"),
                    "a retained light observation overwrote an existing higher-priority status or discarded the new requirement facts");
            }
        });
        check("Conditional and uncertain semantic requirements remain visible beside preliminary actions without becoming mandatory", () =>
        {
            var report = Analyze(Rows());
            foreach (var fact in new[]
            {
                new SemanticFact("本地训练", "workload", "conditional", "future", "本地训练", "", "以后转到本地训练", "如果以后转到本地训练，再增加显卡配置。"),
                new SemanticFact("本地训练", "workload", "uncertain", "future", "本地训练", "", "", "以后是否本地训练还没确定。")
            })
            {
                var settings = new Settings(FutureNeeds: fact.Evidence, SemanticRequirements:
                    new(fact.Evidence, "synthetic-confirmed-model", "synthetic-test", SemanticRequirements.Version, [fact], now));
                var conditional = SemanticRequirements.Apply(report, settings);
                assert(conditional.SemanticRequirements?.Facts.Single().Polarity == fact.Polarity
                    && conditional.Cards.All(c => c.Status != "requirements"), "a conditional-only plan became an unconditional resource requirement");
                string html = ReportExporter.Html(conditional, null);
                foreach (string key in new[] { "cpu", "memory", "gpu" })
                {
                    var card = Card(conditional, key);
                    assert(card.Status == "unknown" && card.Observation?.State == "light" && card.UsesObservation
                        && card.DisplayTitle == Card(report, key).DisplayTitle
                        && card.DisplaySuggestion.StartsWith(Card(report, key).DisplaySuggestion, StringComparison.Ordinal)
                        && card.DisplaySuggestion.Contains("分别比较条件需求成立与不成立"),
                        "observation actions hid the conditional branch or replaced the valid current-load observation");
                    assert(html.Contains(System.Net.WebUtility.HtmlEncode(card.DisplaySuggestion)),
                        "HTML dropped the conditional branch retained by the native display suggestion");
                }
                var drafts = CandidateComparison.Drafts(conditional).Where(c => c.OriginId.StartsWith("semantic:", StringComparison.Ordinal)).ToList();
                assert(drafts.Count > 0 && drafts.All(c => !string.IsNullOrWhiteSpace(c.Condition)),
                    "conditional observation context generated an unconditional candidate criterion");
            }
        });
    }
}
