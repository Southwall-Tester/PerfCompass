using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuObservationScopeTests(Action<string, Action> check, Action<bool, string> assert)
    {
        // All observations below are synthetic; no driver, hardware workload or user data is used.
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));
        const string hardware = "gpu-observation-scope-synthetic";
        const string oldVersion = "gpu-engines-1.0";
        var settings = WorkloadCoverage.Select(new(), true, hardware);
        Sample Row(int day, int index, double load, string version = GpuLoadObservations.Version, string source = "GPU Core") => new()
        {
            Time = now.AddDays(-day).AddSeconds(-index * 10), HardwareId = hardware,
            Scene = "合成来源隔离任务", Seconds = 10, Active = true, ObservationVersion = ActivityRules.Version,
            Cpu = 5, CoreMax = 10, MemoryTotalGb = 64, MemoryUsedGb = 8, CommitGb = 9,
            Gpu = load, GpuPeak = load, GpuMetricSource = source, GpuEngineVersion = version,
            GpuEngines = version.Length == 0 ? null : []
        };
        IEnumerable<Sample> Rows(int[] days, int perDay, double load,
            string version = GpuLoadObservations.Version, string source = "GPU Core") =>
            days.SelectMany(day => Enumerable.Range(0, perDay).Select(index => Row(day, index, load, version, source)));
        AnalysisReport Analyze(IEnumerable<Sample> rows, int days = 30) =>
            AnalysisEngine.Analyze(rows, settings, days, asOf: now, hardwareId: hardware);
        Advice Gpu(AnalysisReport report) => report.Cards.Single(c => c.Key == "gpu");
        DemandTrend Trend(AnalysisReport report, string key = "gpu") => report.DemandTrends.Single(t => t.Key == key && t.Days == 7);
        IEnumerable<Sample> WithEncode(IEnumerable<Sample> rows) => rows.Select(row => row with
        {
            Gpu = 3, GpuPeak = 3,
            GpuEngines = [new("encode", ["D3D Video Encode"], row.Gpu) { Complete = true, Peak = row.Gpu }]
        });
        void InStore(Action<LocalStore> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "PerfCompass-gpu-scope-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(path) ?? throw new IOException("Cannot lease isolated GPU source test directory.");
            action(new LocalStore(path));
        }
        AnalysisReport History(LocalStore store) => HistoryAnalysis.Analyze(store, settings, 30, asOf: now, hardwareId: hardware);

        check("GPU engine 1.1 exposes ten categories while valid 1.0 history retains its original eight-category scope", () =>
        {
            string[] names = ["GPU Core", "D3D 3D", "D3D Compute_0", "GPU Video Engine", "D3D Video Encode",
                "D3D Video Decode", "D3D Video Processing", "D3D Copy", "GPU Render/Compute", "GPU Media"];
            var current = Row(1, 0, 20) with
            {
                GpuEngines = GpuEngineEvidence.Current(GpuLoadObservations.Normalize(names.Select(name => (name, (double?)20))))
            };
            var legacy = current with
            {
                Time = current.Time.AddSeconds(-20), GpuEngineVersion = oldVersion,
                GpuEngines = current.GpuEngines!.Where(e => e.Kind is not ("render-compute" or "media")).ToList()
            };
            assert(GpuEngineEvidence.Version == "gpu-engines-1.1" && GpuEngineEvidence.Kinds.Length == 10
                && current.GpuEngines!.Select(e => e.Kind).Order().SequenceEqual(GpuEngineEvidence.Kinds.Order())
                && legacy.GpuEngines!.Count == 8 && GpuEngineEvidence.Valid(current) && GpuEngineEvidence.Valid(legacy),
                "current categories or readable legacy schema changed");
            foreach (string kind in new[] { "render-compute", "media" })
                assert(!GpuEngineEvidence.Valid(legacy with { GpuEngines = [current.GpuEngines!.Single(e => e.Kind == kind)] }),
                    "a new category was accepted under the old engine schema");
            InStore(store =>
            {
                store.Append(legacy); store.Append(current);
                var report = History(store);
                assert(report.SampleCount == 2 && !report.HistoryQuality.HasIssues && report.GpuEngineProfiles.Count == 10
                    && report.GpuEngineProfiles.Single(p => p.Kind == "media").ObservedHours == 10d / 3600
                    && report.GpuMetricScopes.Contains(oldVersion + "/GPU Core")
                    && report.GpuMetricScopes.Contains(GpuEngineEvidence.Version + "/GPU Core"),
                    "actual history rejected old records or rewrote their provenance as current");
                string file = Path.Combine(store.Root, $"samples-{current.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
                File.AppendAllText(file, JsonSerializer.Serialize(current with
                    { Time = current.Time.AddSeconds(20), GpuEngineVersion = oldVersion }) + Environment.NewLine, new UTF8Encoding(false));
                byte[] original = File.ReadAllBytes(file);
                var invalid = History(store);
                assert(invalid.SampleCount == 2 && invalid.HistoryQuality.IssueCount == 1
                    && invalid.HistoryQuality.Issues.Single().Line == 3 && File.ReadAllBytes(file).SequenceEqual(original),
                    "new categories bearing the legacy schema were not diagnosed in the real history path");
            });
        });

        check("GPU source and rule changes split collection batches and direct mixed merges are rejected", () =>
        {
            var first = Row(1, 0, 20, oldVersion) with { Seconds = 2 };
            foreach (var changed in new[]
            {
                first with { GpuMetricSource = "GPU D3D 3D" }, first with { GpuMetricSource = "D3D 3D" },
                first with { GpuMetricSource = "" }, first with { GpuEngineVersion = GpuEngineEvidence.Version }
            })
            {
                assert(SampleBatch.MustFlushBefore([first], changed), "a changed GPU observation scope stayed in one batch");
                bool rejected = false;
                try { _ = SampleBatch.Merge([first, changed]); } catch (ArgumentException) { rejected = true; }
                assert(rejected, "direct merge accepted different GPU source or rule versions");
            }
            var same = first with { Time = first.Time.AddSeconds(3), Seconds = 3, Gpu = 80, GpuPeak = 80 };
            var merged = SampleBatch.Merge([first, same]);
            assert(!SampleBatch.MustFlushBefore([first], same) && merged.Gpu == 56 && merged.Seconds == 5
                && merged.GpuMetricSource == "GPU Core" && merged.GpuEngineVersion == oldVersion && GpuEngineEvidence.Valid(merged),
                "unchanged legacy provenance prevented ordinary duration-weighted batching");
        });

        check("Core to D3D and old to new observation rules cannot masquerade as increasing or decreasing GPU demand", () =>
        {
            foreach (var scope in new[]
            {
                (OldVersion: oldVersion, OldSource: "GPU Core", NewVersion: GpuEngineEvidence.Version, NewSource: "GPU Core"),
                (OldVersion: "", OldSource: "", NewVersion: GpuEngineEvidence.Version, NewSource: "GPU Core"),
                (OldVersion: GpuEngineEvidence.Version, OldSource: "GPU Core", NewVersion: GpuEngineEvidence.Version, NewSource: "GPU D3D 3D")
            })
            foreach (bool rising in new[] { true, false })
            {
                var report = Analyze(Rows([8, 9], 180, rising ? 10 : 85, scope.OldVersion, scope.OldSource)
                    .Concat(Rows([1, 2], 180, rising ? 85 : 10, scope.NewVersion, scope.NewSource)));
                assert(Trend(report).Status == "insufficient" && Trend(report).PreviousP95 == (rising ? 10 : 85)
                    && Trend(report).RecentP95 == (rising ? 85 : 10) && Gpu(report).Status is not ("changed" or "review" or "surplus"),
                    "a collection source transition became a changed-demand or downgrade conclusion");
            }
            var control = Analyze(Rows([8, 9], 180, 10).Concat(Rows([1, 2], 180, 85)));
            assert(Trend(control).Status == "increased" && Gpu(control).Status == "changed",
                "same-source comparable periods no longer report a genuine synthetic demand increase");
        });

        check("Mixed recent GPU sources cannot create pooled pressure while a homogeneous recent period keeps its own heavy evidence", () =>
        {
            foreach (var second in new[]
            {
                Rows([2], 180, 100, GpuEngineEvidence.Version, "GPU D3D 3D"),
                Rows([2], 180, 100, oldVersion), Rows([2], 180, 100, "", "")
            })
            {
                var report = Analyze(Rows([1], 180, 100).Concat(second));
                assert(Gpu(report).Status is not ("pressure" or "review" or "surplus")
                    && report.GpuMetricScopes.Length == 2 && Trend(report).Status == "insufficient",
                    "incomparable sources were pooled into sustained GPU pressure");
            }
            var current = Analyze(Rows([8, 9], 180, 10, oldVersion)
                .Concat(Rows([1, 2], 180, 100, GpuEngineEvidence.Version, "GPU D3D 3D")));
            assert(Gpu(current).Status == "pressure" && Trend(current).Status == "insufficient",
                "old source differences erased valid current pressure or became a demand-change claim");
        });

        check("A single recent GPU source does not authorize downgrade review over a mixed thirty-day observation window", () =>
        {
            foreach (var older in new[]
            {
                Rows([8, 9], 1620, 10, oldVersion),
                Rows([8, 9], 1620, 10, GpuEngineEvidence.Version, "GPU D3D 3D"),
                Rows([8, 9], 1620, 10, "", "")
            })
            {
                var report = Analyze(older.Concat(Rows([1, 2], 180, 10)));
                assert(report.Ready && report.ActiveHours == 10 && report.GpuMetricScopes.Length == 2
                    && Gpu(report).Status == "unknown", "a homogeneous recent week licensed a pooled thirty-day GPU downgrade review");
            }
            var same = Analyze(Rows([8, 9], 1620, 10).Concat(Rows([1, 2], 180, 10)));
            assert(same.Ready && same.GpuMetricScopes.Length == 1 && Gpu(same).Status == "review",
                "the same-source control did not retain its explicitly conditional review direction");
            var staleChanged = Analyze(Rows([31, 32], 180, 100, oldVersion)
                .Concat(Rows([8, 9], 1620, 10)).Concat(Rows([1, 2], 180, 10)));
            assert(staleChanged.GpuMetricScopes.Length == 1 && Gpu(staleChanged).Status == "review",
                "an observation source outside the selected window incorrectly invalidated its homogeneous review scope");
        });

        check("An engine rule upgrade cannot become an encoding demand increase or pooled recent pressure", () =>
        {
            var report = Analyze(WithEncode(Rows([8, 9], 180, 10, oldVersion).Concat(Rows([1, 2], 180, 85))));
            var engine = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
            assert(Trend(report, "gpu-engine/encode").Status == "insufficient" && engine.SourceScopes.Length == 2
                && engine.Sources.SequenceEqual(new[] { "D3D Video Encode" })
                && Gpu(report).Status is not ("changed" or "pressure" or "review" or "surplus"),
                "an unchanged node label concealed a measurement-rule upgrade");
            var mixed = Analyze(WithEncode(Rows([1], 180, 100).Concat(Rows([2], 180, 100, oldVersion))));
            assert(Gpu(mixed).Status != "pressure" && Trend(mixed, "gpu-engine/encode").Status == "insufficient",
                "two engine schema versions were pooled into recent sustained pressure");
            var same = Analyze(WithEncode(Rows([8, 9], 180, 10).Concat(Rows([1, 2], 180, 85))));
            assert(Trend(same, "gpu-engine/encode").Status == "increased" && Gpu(same).Status == "changed",
                "same-rule engine observations lost comparable demand evidence");
            var recent = Analyze(WithEncode(Rows([8, 9], 180, 10, oldVersion).Concat(Rows([1, 2], 180, 100))));
            assert(Gpu(recent).Status == "pressure" && Trend(recent, "gpu-engine/encode").Status == "insufficient",
                "prior engine rules erased genuine recent same-rule pressure or manufactured a comparable baseline");
        });

        check("Changed core provenance invalidates confirmed candidate coverage without numerical refresh churn", () =>
        {
            var before = new AnalysisReport(now, 30, 1, 10, 4, true, [], [], [], "")
                { HardwareId = hardware, RepresentativeConfirmed = true, GpuMetricScopes = [oldVersion + "/GPU Core"] };
            var workspace = new PurchaseWorkspace
            {
                ContextStamp = CandidateComparison.Stamp(before), CoverageConfirmed = true,
                Criteria = [new() { Name = "Synthetic capacity target", Metric = "memory", Target = 32, Direction = "min", Unit = "GB", Reviewed = true }],
                Candidates = [new() { Name = "Synthetic candidate", Cpu = "CPU", Gpu = "GPU", MemoryGb = 32, PriceCny = 6000, Source = "Synthetic specifications" }]
            };
            assert(CandidateComparison.Evaluate(workspace, before).Candidates.Single().State == "pass", "comparison fixture was not initially confirmed");
            foreach (string scope in new[] { GpuEngineEvidence.Version + "/GPU Core", oldVersion + "/GPU D3D 3D", "legacy/core-or-3d-unspecified" })
            {
                var changed = before with { GpuMetricScopes = [scope] };
                var result = CandidateComparison.Evaluate(workspace, changed);
                assert(CandidateComparison.Stamp(before) != CandidateComparison.Stamp(changed)
                    && result.Candidates.Single().State == "unknown" && result.Tradeoffs.Length == 0,
                    "changed source provenance inherited a prior candidate coverage confirmation");
            }
            var mixed = before with { GpuMetricScopes = [oldVersion + "/GPU Core", GpuEngineEvidence.Version + "/GPU Core"] };
            assert(CandidateComparison.Stamp(mixed) == CandidateComparison.Stamp(mixed with { GpuMetricScopes = mixed.GpuMetricScopes.Reverse().ToArray() })
                && CandidateComparison.Stamp(before) == CandidateComparison.Stamp(before with { SampleCount = 4000, ActiveHours = 11 }),
                "provenance ordering or routine duration refresh changed candidate coverage");
        });

        check("Actual history diagnoses invalid GPU metric sources at their original line and preserves raw files", () =>
        {
            InStore(store =>
            {
                var valid = Row(1, 0, 5);
                string file = Path.Combine(store.Root, $"samples-{valid.Time.LocalDateTime:yyyy-MM-dd}.jsonl");
                foreach (string? source in new string?[] { "GPU Memory", "D3D Compute_0", "future-source", " GPU Core ", null })
                {
                    var bad = valid with { Time = valid.Time.AddSeconds(20), GpuMetricSource = source! };
                    File.WriteAllLines(file, [JsonSerializer.Serialize(valid), JsonSerializer.Serialize(bad)], new UTF8Encoding(false));
                    byte[] original = File.ReadAllBytes(file);
                    var report = History(store); var issue = report.HistoryQuality.Issues.Single();
                    assert(report.SampleCount == 1 && report.HistoryQuality.IssueCount == 1 && issue.Line == 2
                        && issue.File == Path.GetFileName(file) && issue.Reason.Contains("来源")
                        && !report.Ready && report.GpuMetricScopes.SequenceEqual(new[] { GpuEngineEvidence.Version + "/GPU Core" })
                        && File.ReadAllBytes(file).SequenceEqual(original)
                        && Gpu(report).Status is not ("review" or "surplus" or "changed"),
                        "invalid source was silently accepted, mislocated, pooled or destructively repaired");
                }
                var accepted = new[] { "", "GPU Core", "GPU D3D 3D", "D3D 3D" }
                    .Select((source, index) => valid with { Time = valid.Time.AddSeconds(index * 20), GpuMetricSource = source }).ToArray();
                File.WriteAllLines(file, accepted.Select(s => JsonSerializer.Serialize(s)), new UTF8Encoding(false));
                var readable = History(store);
                assert(readable.SampleCount == 4 && !readable.HistoryQuality.HasIssues && readable.GpuMetricScopes.Length == 4,
                    "known metric sources or the legacy unspecified source were rejected or conflated");
            });
        });

        check("CSV and HTML retain original GPU metric provenance and distinguish old missing scope from current named scope", () =>
        {
            InStore(store =>
            {
                var rows = new[] { Row(1, 0, 5, "", ""), Row(1, 2, 10, oldVersion), Row(1, 4, 15, GpuEngineEvidence.Version, "GPU D3D 3D") };
                string path = Path.Combine(store.Root, "synthetic-source-export.csv");
                ReportExporter.Csv(path, rows);
                using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
                parser.SetDelimiters(","); var header = parser.ReadFields()!;
                int version = Array.IndexOf(header, "gpu_engine_version"), source = Array.IndexOf(header, "gpu_metric_source"),
                    metric = Array.IndexOf(header, "gpu_metric_scope"), engines = Array.IndexOf(header, "gpu_engines_json");
                assert(version >= 0 && source >= 0 && metric >= 0 && engines >= 0, "CSV omitted GPU provenance columns");
                for (int i = 0; i < rows.Length; i++)
                {
                    var cells = parser.ReadFields()!;
                    assert(cells.Length == header.Length && cells[version] == rows[i].GpuEngineVersion
                        && cells[source] == rows[i].GpuMetricSource && cells[metric] == "core_or_3d"
                        && (i != 0 || cells[engines] == ""), "CSV rewrote original source, rules or missing engine evidence");
                }
                assert(parser.EndOfData, "CSV gained synthetic source rows");
                var report = Analyze(rows);
                string html = ReportExporter.Html(report, null);
                assert(report.GpuMetricScopes.All(scope => html.Contains(scope)) && html.Contains("核心/3D观测来源")
                    && html.Contains("旧版未标注来源") && html.Contains("采集规则或来源变化时不推断需求增减"),
                    "HTML omitted source scope or attributed collection changes to demand");
                string escaped = ReportExporter.Html(report with { GpuMetricScopes = ["<script>source</script>"] }, null);
                assert(escaped.Contains("&lt;script&gt;source&lt;/script&gt;") && !escaped.Contains("<script>source</script>"),
                    "HTML treated a provenance label as executable markup");
            });
        });

        check("Mixed core sources remove pooled percentiles and GPU history while preserving CPU memory peaks and raw CSV", () =>
        {
            InStore(store =>
            {
                var first = Row(1, 0, 10) with { Time = now.AddDays(-1).AddMinutes(5) };
                var second = first with { Time = first.Time.AddSeconds(20), Gpu = 90, GpuPeak = 90, GpuMetricSource = "GPU D3D 3D" };
                store.Append(first); store.Append(second);
                var report = History(store); var scene = report.SceneProfiles.Single();
                assert(report.SampleCount == 2 && !report.HistoryQuality.HasIssues && report.GpuHistoryHasMixedSources
                    && report.History.Count == 1 && report.History.All(p => p.Gpu == null && p.Cpu == 5 && p.Memory == 12.5)
                    && scene.GpuP95 == null && scene.GpuHasMixedSources && scene.GpuPeak == 90 && scene.CpuP95 == 5 && scene.MemoryPeakGb == 8
                    && Gpu(report).Evidence.Contains("未合并 P95") && Gpu(report).Evidence.Contains("峰值仅为"),
                    "different core metrics remained a pooled percentile or continuous GPU history, or removed independent observations");
                var same = Analyze([first, second with { GpuMetricSource = first.GpuMetricSource }]);
                assert(!same.GpuHistoryHasMixedSources && same.History.Single().Gpu == 50
                    && same.SceneProfiles.Single().GpuP95.HasValue && !same.SceneProfiles.Single().GpuHasMixedSources,
                    "same-source numeric changes were mistaken for a collection boundary");
                string csv = Path.Combine(store.Root, "mixed-sources-raw.csv");
                ReportExporter.Csv(csv, store.ReadChecked(30, hardware, now, null));
                using var parser = new TextFieldParser(csv) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
                parser.SetDelimiters(","); var header = parser.ReadFields()!;
                int valueColumn = Array.IndexOf(header, "gpu_percent"), sourceColumn = Array.IndexOf(header, "gpu_metric_source");
                var values = new List<(string Value, string Source)>();
                while (!parser.EndOfData) { var cells = parser.ReadFields()!; values.Add((cells[valueColumn], cells[sourceColumn])); }
                assert(values.Count == 2 && values.Contains(("10", "GPU Core")) && values.Contains(("90", "GPU D3D 3D")),
                    "withdrawing a pooled visualization erased original source-specific CSV observations");
            });
        });

        check("GPU source isolation applies to each scene and selected graph without hiding coherent neighboring scenes", () =>
        {
            var first = Row(1, 0, 10) with { Scene = "same source", Time = now.AddDays(-1).AddMinutes(5) };
            var rows = new[]
            {
                first, first with { Time = first.Time.AddSeconds(20), Gpu = 20, GpuPeak = 20 },
                first with { Time = first.Time.AddSeconds(40), Scene = "mixed source", Gpu = 30, GpuPeak = 30 },
                first with { Time = first.Time.AddSeconds(60), Scene = "mixed source", Gpu = 90, GpuPeak = 90, GpuMetricSource = "D3D 3D" }
            };
            var all = Analyze(rows);
            var same = all.SceneProfiles.Single(p => p.Name == "same source");
            var mixed = all.SceneProfiles.Single(p => p.Name == "mixed source");
            assert(all.GpuHistoryHasMixedSources && all.History.All(p => p.Gpu == null)
                && same.GpuP95.HasValue && same.GpuMetricScopes.Length == 1 && mixed.GpuP95 == null && mixed.GpuMetricScopes.Length == 2,
                "scene provenance was inferred from unrelated scenes or a mixed scene retained a percentile");
            var selected = AnalysisEngine.Analyze(rows, settings, 30, selectedScene: "same source", asOf: now, hardwareId: hardware);
            assert(!selected.GpuHistoryHasMixedSources && selected.History.Single().Gpu == 15
                && selected.GpuMetricScopes.Length == 1 && selected.SceneProfiles.Single(p => p.Name == "mixed source").GpuP95 == null,
                "selecting a homogeneous scene retained another scene's graph restriction or silently repaired the mixed scene");
        });

        check("Only actual GPU graph observations contribute graph provenance including separately observed idle samples", () =>
        {
            var first = Row(1, 0, 10) with { Time = now.AddDays(-1).AddMinutes(5) };
            var absent = first with { Time = first.Time.AddSeconds(20), GpuMetricSource = "D3D 3D", Gpu = null, GpuPeak = null };
            var uncovered = first with { Time = first.Time.AddSeconds(40), GpuMetricSource = "D3D 3D", GpuCoverage = 0 };
            var comparable = Analyze([first, absent, uncovered]);
            assert(!comparable.GpuHistoryHasMixedSources && comparable.History.Single().Gpu == 10
                && comparable.SceneProfiles.Single().GpuP95 == 10,
                "missing or zero-coverage source labels manufactured a mixed graph or percentile");
            var idle = first with { Time = first.Time.AddSeconds(20), Active = false, GpuMetricSource = "D3D 3D", Gpu = 80, GpuPeak = 80 };
            var mixedGraph = Analyze([first, idle]);
            assert(mixedGraph.GpuHistoryHasMixedSources && mixedGraph.History.All(p => p.Gpu == null)
                && mixedGraph.GpuMetricScopes.Length == 1 && mixedGraph.SceneProfiles.Single().GpuP95 == 10,
                "a plotted idle observation bridged incompatible GPU sources or polluted the active-task percentile");
        });

        check("Internally mixed trend periods hide pooled P95 and proportions while independently homogeneous periods keep their readings", () =>
        {
            var recentMixed = Analyze(Rows([8, 9], 180, 10).Concat(Rows([1], 180, 100)).Concat(Rows([2], 180, 100, source: "D3D 3D")));
            var recent = Trend(recentMixed);
            assert(recent.Status == "insufficient" && recent.RecentP95 == null && recent.PreviousP95 == 10
                && !recent.LoadStatisticsAvailable && recent.RecentBusyMinutes == 0 && recent.RecentHours == 1
                && recent.BusyLabel.Contains("来源或规则混合") && recent.SceneLabel.Length == 0,
                "the insufficient trend label still advertised a pooled recent percentile or workload share");
            var previousMixed = Analyze(Rows([8], 180, 100).Concat(Rows([9], 180, 100, oldVersion)).Concat(Rows([1, 2], 180, 10)));
            var previous = Trend(previousMixed);
            assert(previous.PreviousP95 == null && previous.RecentP95 == 10 && !previous.LoadStatisticsAvailable
                && previous.PreviousBusyMinutes == 0 && previous.PreviousHours == 1,
                "an internally mixed earlier period retained pooled statistics");
            var retained = HistoryAnalysis.Apply(recentMixed, new(60, 1, 1, "unrelated-malformed-line", [new("samples-test.jsonl", 2, "invalid row")]));
            assert(!Trend(retained).LoadStatisticsAvailable && Trend(retained).RecentP95 == null,
                "adding a non-overlap history diagnosis re-enabled source-incomparable workload statistics");
            var separate = Analyze(Rows([8, 9], 180, 10, oldVersion).Concat(Rows([1, 2], 180, 100)));
            assert(Trend(separate).Status == "insufficient" && Trend(separate).PreviousP95 == 10 && Trend(separate).RecentP95 == 100
                && Trend(separate).LoadStatisticsAvailable && Gpu(separate).Status == "pressure",
                "different but individually homogeneous periods lost independent readings or genuine recent pressure");
        });

        check("Engine profile and period P95 are withheld across rule or node-scope changes but positive observations survive", () =>
        {
            var differentRules = Analyze(WithEncode(Rows([8, 9], 180, 10, oldVersion).Concat(Rows([1, 2], 180, 100))));
            var profile = differentRules.GpuEngineProfiles.Single(p => p.Kind == "encode");
            assert(profile.HasMixedSources && profile.P95 == null && profile.Peak == 100 && profile.HeavyMinutes == 60
                && profile.ObservedHours == 2 && profile.ScopeNotice.Contains("未合并 P95") && Gpu(differentRules).Status == "pressure"
                && Trend(differentRules, "gpu-engine/encode").PreviousP95 == 10 && Trend(differentRules, "gpu-engine/encode").RecentP95 == 100,
                "a rule change created a pooled engine percentile or erased positive current-engine evidence");
            var renamed = WithEncode(Rows([2], 180, 100)).Select(row => row with
                { GpuEngines = [row.GpuEngines!.Single() with { Sources = ["D3D Video Encode_1"] }] });
            var mixedNodes = Analyze(WithEncode(Rows([1], 180, 100)).Concat(renamed));
            var nodeTrend = Trend(mixedNodes, "gpu-engine/encode");
            assert(mixedNodes.GpuEngineProfiles.Single(p => p.Kind == "encode") is { P95: null, Peak: 100, HeavyMinutes: 60 }
                && nodeTrend.RecentP95 == null && !nodeTrend.LoadStatisticsAvailable && Gpu(mixedNodes).Status != "pressure",
                "same-version node replacement remained a pooled profile, period percentile or sustained pressure");
            var same = Analyze(WithEncode(Rows([1, 2], 180, 100)));
            assert(same.GpuEngineProfiles.Single(p => p.Kind == "encode") is { P95: 100, HasMixedSources: false }
                && Trend(same, "gpu-engine/encode").RecentP95 == 100 && Gpu(same).Status == "pressure",
                "same-source engine evidence was suppressed along with mixed sources");
        });

        check("Mixed-source GPU peaks remain task clues and partial engine work cannot promote unknown sizing to fit", () =>
        {
            var baseline = Rows([8, 9], 1620, 10)
                .Concat(Rows([1, 2], 180, 10, source: "D3D 3D")).ToArray();
            var peak = Row(1, 181, 100, source: "D3D 3D");
            var core = Analyze(baseline.Append(peak));
            assert(core.Ready && core.GpuMetricScopes.Length == 2 && Gpu(core).Status == "unknown"
                && Gpu(core).Title.Contains("重负载") && !Gpu(core).Title.Contains("偶发")
                && Gpu(core).Suggestion.Contains("候选显卡") && Gpu(core).Evidence.Contains("未合并 P95")
                && core.SceneProfiles.Single() is { GpuP95: null, GpuPeak: 100 }
                && core.History.All(p => p.Gpu == null) && peak.Gpu == 100 && peak.GpuPeak == 100,
                "a real peak was erased, called infrequent without comparable P95, or treated as adequate mixed-source sizing");
            var partial = peak with
            {
                GpuEngines = [new("compute", ["D3D Compute_0"], 100) { Complete = false, Peak = 100 }]
            };
            var engine = Analyze(baseline.Append(partial));
            var profile = engine.GpuEngineProfiles.Single(p => p.Kind == "compute");
            assert(engine.Ready && engine.GpuMetricScopes.Length == 2 && Gpu(engine).Status == "unknown"
                && Gpu(engine).Title.Contains("重负载") && !Gpu(engine).Title.Contains("偶发")
                && Gpu(engine).Suggestion.Contains("候选显卡") && profile.Peak == 100
                && profile.ObservedHours > 0 && profile.CompleteHours == 0 && profile.HeavyMinutes > 0
                && engine.SceneProfiles.Single() is { GpuP95: null, GpuPeak: 100 },
                "a partial heavy engine upgraded a mixed-source unknown core conclusion to fit or lost its positive task clue");
        });

        check("Live GPU line segments break on source and rule transitions while independent CPU and memory segments continue", () =>
        {
            var first = new HistoryPoint(now, 10, 25, 10) { GpuScope = GpuEngineEvidence.MetricScope(Row(1, 0, 10)) };
            foreach (var sample in new[] { Row(1, 0, 90, source: "D3D 3D"), Row(1, 0, 90, oldVersion), Row(1, 0, 90, "", "") })
            {
                var changed = first with { Time = now.AddSeconds(2), Gpu = 90, GpuScope = GpuEngineEvidence.MetricScope(sample) };
                assert(!TrendChart.CanConnect(first, changed, 20, gpu: true) && TrendChart.CanConnect(first, changed, 20, gpu: false)
                    && changed.Cpu == 10 && changed.Memory == 25 && changed.Gpu == 90,
                    "a source transition bridged the GPU line, erased the new endpoint, or broke independent metrics");
                var next = changed with { Time = changed.Time.AddSeconds(2), Gpu = 95 };
                assert(TrendChart.CanConnect(changed, next, 20, gpu: true), "the first comparable pair after a transition never resumed");
            }
            assert(TrendChart.CanConnect(first, first with { Time = now.AddSeconds(2), Gpu = 90 }, 20, gpu: true)
                && !TrendChart.CanConnect(first, first with { Time = now.AddSeconds(2), Gpu = null }, 20, gpu: true)
                && !TrendChart.CanConnect(first, first with { Time = now.AddSeconds(21) }, 20, gpu: true),
                "same-source changes, missing values or offline gaps violated graph continuity");
        });
    }
}
