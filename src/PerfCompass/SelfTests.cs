using System.IO;

namespace PerfCompass;

public static partial class SelfTests
{
    public static int Run()
    {
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/self-tests.txt", "RUNNING " + DateTimeOffset.Now.ToString("O") + "\n");
        var log = new List<string>();
        void Check(string name, Action action)
        {
            File.AppendAllText("artifacts/self-tests.txt", "RUN " + name + "\n");
            try { action(); log.Add("PASS " + name); }
            catch (Exception e) { log.Add("FAIL " + name + ": " + e.Message); }
            File.AppendAllText("artifacts/self-tests.txt", log[^1] + "\n");
        }
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        List<Sample> Fixture()
        {
            var fixtureNow = DateTimeOffset.Now;
            return Enumerable.Range(0, 3600).Select(i => new Sample
        {
            Time = fixtureNow.AddDays(-(i / 1200)).AddSeconds(-(i % 1200) * 10),
            HardwareId = "test-machine", Seconds = 10, Active = true,
            Cpu = 8, CoreMax = 25, MemoryTotalGb = 64, MemoryUsedGb = 9, CommitGb = 11,
            Gpu = 5, VramUsedGb = 1, DiskTotalGb = 1024, DiskUsedGb = 200, MaxDiskPercent = 20
        }).OrderBy(s => s.Time).ToList();
        }
        AnalysisReport Analyze(IEnumerable<Sample> samples, bool confirmed = true) => AnalysisEngine.Analyze(samples, new Settings(Representative: confirmed) { RepresentativeHardwareId = "test-machine", RepresentativeObservationVersion = ActivityRules.Version }, 7);
        RunWorkloadCoverageTests(Check, Assert, () => Fixture());
        Check("Empty history never recommends downgrade", () => Assert(Analyze([]).Cards.All(c => c.Status == "unknown"), "empty classified"));
        Check("Short observation never recommends downgrade", () => Assert(Analyze(Fixture().Take(10)).Cards.All(c => c.Status != "surplus"), "short classified"));
        Check("Representative workload confirmation required", () => Assert(!Analyze(Fixture(), false).Ready, "confirmation ignored"));
        Check("Idle time excluded", () => Assert(Analyze(Fixture().Select(s => s with { Active = false })).ActiveHours == 0, "idle counted"));
        Check("Offline gaps are not elapsed observation", () => Assert(Analyze([Fixture()[0], Fixture()[^1]]).ActiveHours < .01, "wall clock counted"));
        Check("Low load with complete evidence produces RAM candidate", () => Assert(Analyze(Fixture()).Cards.Single(c => c.Key == "memory").Status == "surplus", "candidate missing"));
        Check("Busy single core prevents CPU downgrade", () => Assert(Analyze(Fixture().Select(s => s with { CoreMax = 100 })).Cards.Single(c => c.Key == "cpu").Status != "surplus", "single core ignored"));
        Check("Missing core data prevents CPU downgrade", () => Assert(Analyze(Fixture().Select(s => s with { CoreMax = null })).Cards.Single(c => c.Key == "cpu").Status != "surplus", "missing core treated as zero"));
        Check("Sparse core coverage cannot be promoted to complete", () => Assert(Analyze(Fixture().Select(s => s with { CoreCoverage = 2 })).Cards.Single(c => c.Key == "cpu").Status != "surplus", "coverage inflated"));
        Check("Missing commit data prevents RAM downgrade", () => Assert(Analyze(Fixture().Select(s => s with { CommitGb = null })).Cards.Single(c => c.Key == "memory").Status != "surplus", "commit absent"));
        Check("Rare RAM spike preserved", () => { var f = Fixture(); f[99] = f[99] with { MemoryUsedGb = 60, CommitGb = 62 }; Assert(Analyze(f).Cards.Single(c => c.Key == "memory").Status != "surplus", "peak discarded"); });
        Check("Missing GPU remains unknown", () => Assert(Analyze(Fixture().Select(s => s with { Gpu = null })).Cards.Single(c => c.Key == "gpu").Status == "unknown", "GPU absence treated as zero"));
        Check("Sparse high GPU reading cannot establish sustained pressure", () => Assert(Analyze(Fixture().Select(s => s with { Gpu = 100, GpuCoverage = .1 })).Cards.Single(c => c.Key == "gpu").Status == "unknown", "sparse reading promoted"));
        Check("Sparse CPU reading cannot establish sustained pressure", () => Assert(Analyze(Fixture().Select(s => s with { Cpu = 100, CpuCoverage = .1 })).Cards.Single(c => c.Key == "cpu").Status == "unknown", "sparse reading promoted"));
        Check("Low GPU load requires candidate validation", () => Assert(Analyze(Fixture()).Cards.Single(c => c.Key == "gpu").Status == "review", "GPU overclaim"));
        Check("Sustained CPU load produces pressure signal", () => Assert(Analyze(Fixture().Select(s => s with { Cpu = 99, CoreMax = 100 })).Cards.Single(c => c.Key == "cpu").Status == "pressure", "pressure missing"));
        Check("Sustained memory pressure detected", () => Assert(Analyze(Fixture().Select(s => s with { MemoryUsedGb = 61 })).Cards.Single(c => c.Key == "memory").Status == "pressure", "pressure missing"));
        Check("P95 interpolation", () => Assert(Math.Abs(AnalysisEngine.Percentile([0, 100], .95)!.Value - 95) < .001, "percentile mismatch"));
        Check("P95 weights observed duration instead of sample count", () =>
        {
            var series = new WeightedSeries(); series.Add(100, 1); series.Add(5, 99);
            Assert(series.Quantile(.95) == 5, "short reading outweighed long observation");
        });
        Check("Splitting an interval preserves weighted quantiles", () =>
        {
            var grouped = new WeightedSeries(); grouped.Add(10, 90); grouped.Add(99, 10);
            var split = new WeightedSeries(); for (int i = 0; i < 100; i++) split.Add(i < 90 ? 10 : 99, 1);
            Assert(grouped.Quantile(.95) == split.Quantile(.95) && grouped.TimeAbove(90) == split.TimeAbove(90), "interval split changed evidence");
        });
        Check("Nonfinite readings do not create weighted evidence", () =>
        {
            var series = new WeightedSeries(); series.Add(double.NaN, 10); series.Add(1, double.PositiveInfinity); series.Add(null, 100);
            Assert(series.Seconds == 0 && series.Maximum == null && series.Quantile(.95) == null, "invalid evidence accepted");
        });
        Check("Occasional heavy GPU task survives office dominated history", () =>
        {
            var f = Fixture(); f.Add(f[^1] with { Scene = "游戏娱乐", Gpu = 100, Time = f[^1].Time.AddSeconds(-1) });
            var report = Analyze(f);
            Assert(report.Cards.Single(c => c.Key == "gpu").Status == "fit", "rare GPU task discarded");
            var gaming = report.SceneProfiles.Single(p => p.Name == "游戏娱乐");
            Assert(gaming.GpuPeak == 100 && gaming.ActiveHours > 0 && gaming.SampleCount == 1, "rare scene missing");
        });
        Check("Scene analysis uses its own coverage and observation hours", () =>
        {
            var f = Fixture(); f.Add(f[^1] with { Scene = "游戏娱乐", Gpu = 100, Time = f[^1].Time.AddSeconds(-1) });
            var report = AnalysisEngine.Analyze(f, new Settings(Representative: true) { RepresentativeHardwareId = "test-machine", RepresentativeObservationVersion = ActivityRules.Version }, 7, "游戏娱乐");
            Assert(!report.Ready && report.SampleCount == 1 && report.ActiveHours < .01, "office hours leaked into scene");
            Assert(report.SceneProfiles.Count == 2 && report.SelectedScene == "游戏娱乐", "scene detail lost");
        });
        Check("Unknown scene returns empty analysis without borrowing evidence", () =>
        {
            var report = AnalysisEngine.Analyze(Fixture(), new Settings(Representative: true) { RepresentativeHardwareId = "test-machine", RepresentativeObservationVersion = ActivityRules.Version }, 7, "未运行的任务");
            Assert(report.SampleCount == 0 && report.Cards.All(c => c.Status == "unknown") && !report.Ready, "unknown scene borrowed evidence");
        });
        Check("Input order does not change current hardware capacity", () =>
        {
            var f = Fixture(); f[0] = f[0] with { MemoryTotalGb = 8, MemoryUsedGb = 6, CommitGb = 7 };
            var a = Analyze(f); var b = Analyze(f.AsEnumerable().Reverse());
            Assert(a.Cards.Single(c => c.Key == "memory") == b.Cards.Single(c => c.Key == "memory"), "append order changed capacity");
        });
        Check("Invalid resource readings remain unknown", () =>
        {
            var report = Analyze(Fixture().Select(s => s with { Cpu = double.NaN, CoreMax = 101, Gpu = -1, MemoryUsedGb = 900 }));
            Assert(report.Cards.Where(c => c.Key != "disk").All(c => c.Status == "unknown"), "invalid metrics created recommendations");
        });
        var temporalNow = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(8));
        IEnumerable<Sample> Days(int[] offsets, double? gpu, int samplesPerDay = 180, string scene = "日常办公", double memoryGb = 9)
        {
            foreach (int day in offsets)
                for (int i = 0; i < samplesPerDay; i++)
                    yield return new Sample { Time = temporalNow.AddDays(-day).AddSeconds(-i * 10), HardwareId = "temporal-test", Scene = scene,
                        Seconds = 10, Active = true, Cpu = 8, CoreMax = 25, Gpu = gpu, MemoryTotalGb = 64, MemoryUsedGb = memoryGb, CommitGb = memoryGb + 2,
                        DiskUsedGb = 200, DiskTotalGb = 1024, MaxDiskPercent = 20 };
        }
        AnalysisReport Temporal(IEnumerable<Sample> data, string? scene = null) => AnalysisEngine.Analyze(data, new Settings(Representative: true) { RepresentativeHardwareId = "temporal-test", RepresentativeObservationVersion = ActivityRules.Version }, 90, scene, temporalNow);
        Check("New weekly GPU demand overrides a large idle historical baseline", () =>
        {
            var old = Days(Enumerable.Range(8, 52).ToArray(), 2, 240);
            var recent = Days([1, 2], 85, 180, "AI 与虚拟机"); var report = Temporal(old.Concat(recent));
            var trend = report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7);
            Assert(trend.Status == "increased" && trend.RecentP95 == 85 && trend.PreviousP95 == 2, "weekly shift missed");
            Assert(report.Cards.Single(c => c.Key == "gpu").Status == "changed", "historical idle overrode recent need");
            Assert(trend.RecentHeavyScenes.Contains("AI 与虚拟机"), "new heavy task untraceable");
        });
        Check("Monthly GPU change is recognized without a weekly baseline", () =>
        {
            var report = Temporal(Days([31, 32, 33], 2, 360).Concat(Days([1, 2, 3], 85, 360, "设计剪辑")));
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 30).Status == "increased", "monthly change missed");
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "insufficient", "weekly baseline invented");
        });
        Check("A single heavy day is not promoted to a confirmed trend", () =>
        {
            var report = Temporal(Days([8, 9], 2).Concat(Days([1], 85, 360, "AI 与虚拟机")));
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "insufficient", "single day overinterpreted");
        });
        Check("A decrease does not automatically erase past heavy tasks", () =>
        {
            var report = Temporal(Days([8, 9], 100).Concat(Days([1, 2], 2)));
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "decreased", "decrease missed");
            Assert(report.Cards.Single(c => c.Key == "gpu").Status != "pressure", "old saturation treated as current");
            Assert(report.Cards.Single(c => c.Key == "gpu").Status != "surplus", "past task erased");
        });
        Check("Missing historical GPU readings never become an idle baseline", () =>
        {
            var report = Temporal(Days([8, 9], null).Concat(Days([1, 2], 85)));
            var trend = report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7);
            Assert(trend.Status == "insufficient" && trend.PreviousP95 == null, "missing history fabricated as zero");
        });
        Check("Longer computer use alone does not mean higher task intensity", () =>
        {
            var report = Temporal(Days([8, 9], 85).Concat(Days([1, 2], 85, 720)));
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "similar", "more hours confused with intensity");
        });
        Check("Scene-specific trends do not borrow an unrelated scene baseline", () =>
        {
            var report = Temporal(Days([8, 9], 2).Concat(Days([1, 2], 85, scene: "AI 与虚拟机")), "AI 与虚拟机");
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "insufficient", "unrelated baseline borrowed");
        });
        Check("Stale history cannot establish current spare capacity", () =>
        {
            var report = Temporal(Days([10, 11, 12], 2, 1200));
            Assert(!report.Ready && report.Cards.All(c => c.Status != "surplus"), "old history treated as current");
        });
        Check("Fresh GPU sensor failure cannot be hidden by old sensor coverage", () =>
        {
            var report = Temporal(Days([8, 9, 10], 2, 1200).Concat(Days([1, 2], null)));
            Assert(report.Cards.Single(c => c.Key == "gpu").Status == "unknown", "historical readings hid current failure");
        });
        Check("Increasing recent RAM demand blocks an old low-demand sizing conclusion", () =>
        {
            var report = Temporal(Days([8, 9], 2, memoryGb: 4).Concat(Days([1, 2], 2, memoryGb: 32)));
            Assert(report.DemandTrends.Single(t => t.Key == "memory" && t.Days == 7).Status == "increased", "memory shift missed");
            Assert(report.Cards.Single(c => c.Key == "memory").Status == "changed", "old RAM sizing survived new demand");
        });
        Check("Trend windows assign boundaries without overlap", () =>
        {
            var detector = new TemporalDemandAnalyzer(temporalNow);
            var s = Days([7], 50, 1).Single(); detector.Add(s); detector.Add(s with { Time = temporalNow.AddDays(-14) });
            var trend = detector.Build().Single(t => t.Key == "gpu" && t.Days == 7);
            Assert(trend.RecentHours == 0 && Math.Abs(trend.PreviousHours - 10d / 3600) < .000001, "window overlap");
        });
        Check("Insufficient sensor coverage cannot establish a period change", () =>
        {
            var recent = Days([1, 2], 85, 720).Select((s,i) => i % 4 == 0 ? s : s with { Gpu = null });
            var report = Temporal(Days([8, 9], 2).Concat(recent));
            Assert(report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).Status == "insufficient", "sparse coverage accepted");
        });
        Check("A future dated trace cannot manufacture a current trend", () =>
        {
            var report = Temporal(Days([8, 9], 2).Concat(Days([-1, -2], 85)));
            Assert(report.FreshActiveHours == 0 && report.DemandTrends.All(t => t.Status != "increased"), "future observations counted");
        });
        Check("Observation range and comparison context remain distinct", () =>
        {
            var data = Days([8, 9], 2).Concat(Days([1, 2], 85));
            var report = AnalysisEngine.Analyze(data, new Settings(Representative: true), 7, asOf: temporalNow);
            Assert(report.SampleCount == 360 && report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7).PreviousHours > 0, "context polluted selected period or vanished");
        });
        Check("Ten-second aggregation preserves a two-second RAM spike", () =>
        {
            var samples = Fixture().Take(5).Select(s => s with { Seconds = 2 }).ToList(); samples[1] = samples[1] with { MemoryUsedGb = 59 };
            var merged = SampleBatch.Merge(samples); Assert(merged.MemoryPeakGb == 59 && merged.MemoryUsedGb < 59, "spike lost");
        });
        Check("Aggregation preserves GPU peaks and measured heavy exposure", () =>
        {
            var raw = Fixture().Take(5).Select((s,i) => s with { Seconds = 2, Gpu = i == 0 ? 100 : 0 }).ToList();
            var merged = SampleBatch.Merge(raw);
            Assert(merged.Gpu == 20 && merged.GpuPeak == 100 && merged.GpuHeavySeconds == 2 && merged.GpuHighSeconds == 2, "GPU peak or exposure lost");
            var history = Fixture(); history[0] = merged;
            var report = Analyze(history);
            Assert(report.Cards.Single(c => c.Key == "gpu").Status == "fit", "compressed short peak permitted a downgrade review");
            var trend = report.DemandTrends.Single(t => t.Key == "gpu" && t.Days == 7);
            Assert(Math.Abs(trend.RecentBusyMinutes - 2d / 60) < .000001, "exposure counted as entire bucket");
        });
        Check("Mixed idle/active bucket counts actual active seconds", () =>
        {
            var samples = Fixture().Take(5).Select((s,i) => s with { Seconds = 2, Active = i == 0 }).ToList();
            Assert(SampleBatch.Merge(samples).Seconds == 2, "idle bucket inflated");
        });
        Check("HTML escapes untrusted labels", () => Assert(ReportExporter.Html(Analyze(Fixture()) with { FutureNeeds = "<script>alert(1)</script>" }, null).Contains("&lt;script&gt;"), "HTML not escaped"));
        string root = Path.Combine(Path.GetTempPath(), "PerfCompass-tests-" + Guid.NewGuid().ToString("N"));
        Check("Persistence survives reopening and isolates hardware", () =>
        {
            var store = new LocalStore(root);
            store.SaveSettings(new Settings("开发编译", true, "更多虚拟机"));
            store.Append(Fixture()[^1]); store.Append(Fixture()[^1] with { HardwareId = "other" });
            var reopened = new LocalStore(root);
            Assert(reopened.Settings.FutureNeeds == "更多虚拟机", "settings lost");
            Assert(reopened.Read(7, "test-machine").Count() == 1, "hardware mixed");
        });
        Check("Truncated record does not destroy readable history", () =>
        {
            var store = new LocalStore(root);
            File.AppendAllText(Directory.GetFiles(root, "samples-*.jsonl")[0], "{broken\n");
            Assert(store.Read(7).Count() == 2, "valid history lost");
        });
        Check("Appending after crash truncation recovers record boundary", () =>
        {
            var store = new LocalStore(root); var file = Directory.GetFiles(root, "samples-*.jsonl")[0];
            File.AppendAllText(file, "{partial"); store.Append(Fixture()[^1]);
            Assert(store.Read(7).Count() == 3, "new row merged with broken tail");
        });
        Check("CSV export preserves Unicode and neutralizes formulas", () =>
        {
            var path = Path.Combine(root, "export.csv");
            ReportExporter.Csv(path, [Fixture()[0] with { Scene = "=计算公式" }]);
            Assert(File.ReadAllText(path).Contains("'=计算公式"), "formula not neutralized");
        });
        Check("Storage retries after a transient file lock", () =>
        {
            var store = new LocalStore(root); var sample = Fixture()[^1];
            string file = Path.Combine(root, $"samples-{sample.Time.LocalDateTime:yyyy-MM-dd}.jsonl"); bool failed = false;
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                try { store.Append(sample); } catch (IOException) { failed = true; }
            Assert(failed, "lock failure was hidden");
            int count = store.Read(7).Count(); store.Append(sample);
            Assert(store.Read(7).Count() == count + 1, "retry failed");
        });
        Check("Retention preserves recent samples and unrelated files", () =>
        {
            var store = new LocalStore(root); var old = Fixture()[0] with { Time = DateTimeOffset.Now.AddDays(-100) }; store.Append(old);
            var monthly = old with { Time = DateTimeOffset.Now.AddDays(-40) }; store.Append(monthly);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "keep"); store.Cleanup();
            Assert(!File.Exists(Path.Combine(root, $"samples-{old.Time.LocalDateTime:yyyy-MM-dd}.jsonl")), "expired trace retained");
            Assert(File.Exists(Path.Combine(root, "notes.txt")) && store.Read(7).Any(), "unrelated data removed");
            Assert(File.Exists(Path.Combine(root, $"samples-{monthly.Time.LocalDateTime:yyyy-MM-dd}.jsonl")), "month comparison history discarded");
        });
        Check("Concurrent settings writes remain readable", () =>
        {
            var store = new LocalStore(root); Parallel.For(0, 12, i => store.SaveSettings(new Settings(FutureNeeds: i.ToString())));
            Assert(int.TryParse(new LocalStore(root).Settings.FutureNeeds, out _), "settings corrupted");
        });
        Check("Default context retention grows while existing retention is preserved", () =>
        {
            string legacyRoot = Path.Combine(root, "legacy-retention"); var legacy = new LocalStore(legacyRoot);
            legacy.SaveSettings(new Settings(RetentionDays: 30));
            Assert(new LocalStore(legacyRoot).Settings.RetentionDays == 30, "existing retention overwritten");
            Assert(new LocalStore(Path.Combine(root, "new-retention")).Settings.RetentionDays == 90, "new context retention missing");
        });
        Check("Future AI stays ambiguous until its execution mode is chosen", () =>
        {
            var parsed = RequirementParser.Parse("准备做 AI");
            Assert(parsed.NeedsAiChoice && parsed.Facts.Single().Future && parsed.Facts.Single().Evidence == "准备做 AI", "intent or provenance lost");
            bool refused = false; try { RequirementParser.Confirm(parsed, "unspecified"); } catch (ArgumentException) { refused = true; }
            Assert(refused, "ambiguous AI was confirmed silently");
        });
        Check("Task negation does not become a graphics requirement", () =>
        {
            var parsed = RequirementParser.Parse("不玩游戏，只做办公");
            Assert(parsed.Facts.Single(f => f.Key == "game").Excluded && !parsed.Facts.Single(f => f.Key == "office").Excluded, "negation leaked across clauses");
            Assert(!RequirementParser.Parse("不只是游戏，还准备做剪辑").Facts.Single(f => f.Key == "game").Excluded, "inclusive negation treated as exclusion");
        });
        Check("Cloud AI does not invent a local GPU or memory requirement", () =>
        {
            string text = "准备在线用AI"; var plan = RequirementParser.Confirm(RequirementParser.Parse(text), "cloud");
            var report = RequirementParser.Apply(Analyze(Fixture()), new Settings(FutureNeeds: text, Requirements: plan));
            Assert(report.Cards.Single(c => c.Key == "memory").Status == "surplus" && report.Cards.Single(c => c.Key == "gpu").Status == "review", "cloud inferred a GPU need");
        });
        Check("Confirmed local AI protects sizing while source edits invalidate confirmation", () =>
        {
            string text = "准备运行本地模型"; var plan = RequirementParser.Confirm(RequirementParser.Parse(text), "local");
            var settings = new Settings(FutureNeeds: text, Requirements: plan);
            var report = RequirementParser.Apply(Analyze(Fixture()), settings);
            Assert(report.Cards.Single(c => c.Key == "memory").Status == "requirements" && report.Cards.Single(c => c.Key == "gpu").Status == "requirements", "new local tasks ignored");
            Assert(RequirementParser.Apply(Analyze(Fixture()), settings with { FutureNeeds = "现在仅办公" }).ConfirmedRequirements == null, "old confirmation survived an edit");
        });
        Check("Slow compilation is a task and symptom rather than a CPU diagnosis", () =>
        {
            var parsed = RequirementParser.Parse("刚才编译很慢");
            Assert(parsed.Facts.Any(f => f.Key == "compile") && parsed.Facts.Any(f => f.Key == "latency") && parsed.Facts.All(f => !f.Future), "symptom confused with future intent");
        });
        Check("Display targets and unsupported descriptions remain explicit", () =>
        {
            var parsed = RequirementParser.Parse("准备玩游戏，目标144帧，升级4K显示器");
            Assert(parsed.Facts.Any(f => f.Key == "fps") && parsed.Facts.Any(f => f.Key == "display"), "display target lost");
            Assert(RequirementParser.Parse("随便选个差不多的").Facts.Count == 0, "unknown text fabricated requirements");
        });
        Check("Experience window clips and deduplicates sampled duration", () =>
        {
            var now = DateTimeOffset.Now;
            var s = Fixture()[0] with { Time = now, Seconds = 10, Cpu = 99, Scene = "开发编译" };
            var value = ExperienceAnalyzer.Create(now, s.HardwareId, s.Scene, "操作卡顿", "刚才编译很慢", [s, s, s with { Time = now.AddSeconds(-115), Seconds = 10 }]);
            Assert(value.Clues.Single(c => c.Key == "cpu").CoveredSeconds == 15 && value.Clues.Single(c => c.Key == "cpu").HighSeconds == 15, "overlap or window boundary overcounted");
            Assert(value.ParsedDescription.Contains("开发编译"), "description parsing not connected");
        });
        Check("Experience context excludes other scenes hardware future and stale telemetry", () =>
        {
            var now = DateTimeOffset.Now; var s = Fixture()[0] with { Time = now, Seconds = 2 };
            var value = ExperienceAnalyzer.Create(now, s.HardwareId, s.Scene, "操作卡顿", "", [s with { HardwareId = "other" }, s with { Scene = "other" }, s with { Time = now.AddSeconds(1) }, s with { Time = now.AddSeconds(-7) }]);
            Assert(value.Clues.All(c => c.CoveredSeconds == 0) && value.Conclusion.Contains("没有近期"), "borrowed stale or unrelated evidence");
        });
        Check("Missing and invalid experience readings never become zero load", () =>
        {
            var now = DateTimeOffset.Now; var s = Fixture()[0] with { Time = now, Cpu = double.NaN, Gpu = null, CpuCoverage = 2, Seconds = 2 };
            var value = ExperienceAnalyzer.Create(now, s.HardwareId, s.Scene, "任务等待过久", "", [s]);
            Assert(value.Clues.Single(c => c.Key == "cpu").Peak == null && value.Clues.Single(c => c.Key == "gpu").CoveredSeconds == 0, "missing values became idle");
        });
        Check("Pending recent experience protects surplus without inventing a cause", () =>
        {
            var now = DateTimeOffset.Now; var value = ExperienceAnalyzer.Create(now, "test-machine", "日常办公", "操作卡顿", "", []);
            var baseline = Analyze(Fixture()) with { Generated = now };
            var report = ExperienceAnalyzer.Attach(baseline, [value]);
            Assert(report.Cards.All(c => c.Status != "surplus" && c.Status != "review") && report.Cards.Any(c => c.Status == "experience"), "symptom ignored");
            Assert(ExperienceAnalyzer.Attach(baseline, [value with { Resolved = true }]).Cards.Single(c => c.Key == "memory").Status == "surplus", "resolution does not restore independent analysis");
            Assert(ExperienceAnalyzer.Attach(baseline with { SelectedScene = "开发编译" }, [value]).ExperienceEvents.Count == 0, "event crossed scene boundary");
        });
        Check("Experience persistence handles crash tails resolution and hardware isolation", () =>
        {
            var storage = new LocalStore(Path.Combine(root, "experience")); var value = ExperienceAnalyzer.Create(DateTimeOffset.Now, "mine", "日常办公", "操作卡顿", "描述 <script>", []);
            storage.AppendExperience(value); storage.AppendExperience(value with { Resolved = true });
            Assert(new LocalStore(storage.Root).ReadExperiences(7, "mine").Single().Resolved && storage.ReadExperiences(7, "other").Count == 0, "state or isolation lost");
            string path = Directory.GetFiles(storage.Root, "experience-*.jsonl").Single(); File.AppendAllText(path, "{truncated");
            storage.AppendExperience(value with { Resolved = false });
            Assert(storage.ReadExperiences(7, "mine").Single().Resolved == false, "crash tail consumed following record");
            var report = ExperienceAnalyzer.Attach(Analyze(Fixture()), storage.ReadExperiences(7, "mine"));
            Assert(ReportExporter.Html(report, null).Contains("&lt;script&gt;"), "experience HTML not escaped");
        });
        Check("Experience files obey retention without touching unrelated files", () =>
        {
            var storage = new LocalStore(Path.Combine(root, "experience-retention"));
            File.WriteAllText(Path.Combine(storage.Root, $"experience-{DateTime.Today.AddDays(-100):yyyy-MM-dd}.jsonl"), "old");
            File.WriteAllText(Path.Combine(storage.Root, "experience-not-a-date.jsonl"), "keep"); storage.Cleanup();
            Assert(Directory.GetFiles(storage.Root, "experience-*.jsonl").Length == 1, "retention did not isolate dated events");
        });
        Check("Thirty-day stream remains bounded and supports full analysis", () =>
        {
            var prototype = Fixture()[0];
            var stream = Enumerable.Range(0, 259200).Select(i => prototype with { Time = DateTimeOffset.Now.AddDays(-30).AddSeconds(i * 10) });
            var timer = System.Diagnostics.Stopwatch.StartNew(); var report = AnalysisEngine.Analyze(stream, new Settings(Representative: true), 30);
            Assert(report.SampleCount == 259200 && report.History.Count <= 722, "unbounded history points or missing samples");
            log.Add($"METRIC 30-day analysis: {timer.Elapsed.TotalMilliseconds:0} ms, {report.History.Count} chart bins");
        });
        Task.Run(() => RunSemanticTests(Check, Assert, () => Analyze(Fixture()))).GetAwaiter().GetResult();
        RunComparisonTests(Check, Assert);
        RunPurchaseScenarioTests(Check, Assert);
        RunComparisonDraftTests(Check, Assert);
        RunQuantityGoalTests(Check, Assert);
        RunDataProtectionTests(Check, Assert);
        RunBackgroundSettingsTests(Check, Assert);
        RunHardwareSnapshotTests(Check, Assert);
        RunCpuCounterTests(Check, Assert, Fixture);
        RunGpuLoadObservationsTests(Check, Assert);
        RunD3DNodeTests(Check, Assert);
        RunGpuVendorPollTests(Check, Assert);
        RunIntelTelemetryTests(Check, Assert);
        RunGpuObservationScopeTests(Check, Assert);
        RunResourceObservationTests(Check, Assert);
        RunMemoryCapacityTests(Check, Assert);
        RunConfigurationDraftTests(Check, Assert);
        RunCandidateTriageTests(Check, Assert);
        RunStorageCapacityTests(Check, Assert);
        RunGpuPurchasePlanTests(Check, Assert);
        RunPurchaseShortlistTests(Check, Assert);
        RunSpecificationImportTests(Check, Assert);
        RunPurchaseGuidanceTests(Check, Assert);
        RunCandidateEvidenceLevelTests(Check, Assert);
        RunHistoryAggregationTests(Check, Assert);
        RunComparisonDecisionIntegrityTests(Check, Assert);
        RunGpuEngineEvidenceTests(Check, Assert);
        RunGpuEngineAnalysisTests(Check, Assert);
        RunGpuEngineExportTests(Check, Assert);
        RunGpuEngineComparisonTests(Check, Assert);
        RunHistoryQualityTests(Check, Assert, Fixture);
        RunExperienceReaderTests(Check, Assert, root, () => Analyze(Fixture()));
        RunSemanticUseTests(Check, Assert, root, () => Analyze(Fixture()));
        RunHistoryIntervalTests(Check, Assert, Fixture);
        RunDataRootLeaseTests(Check, Assert);
        RunSamplingClockTests(Check, Assert);
        RunRetentionSafetyTests(Check, Assert);
        RunValidationRunTests(Check, Assert);
        RunLiveUpdateTests(Check, Assert);
        RunActivityRulesTests(Check, Assert, Fixture);
        Task.Run(() => RunTaskMeasurementTests(Check, Assert)).GetAwaiter().GetResult();
        Task.Run(() => RunTaskFileProtectionTests(Check, Assert)).GetAwaiter().GetResult();
        Task.Run(() => RunTaskPlanTests(Check, Assert)).GetAwaiter().GetResult();
        Directory.CreateDirectory("artifacts");
        File.WriteAllLines("artifacts/self-tests.txt", log);
        return log.Any(x => x.StartsWith("FAIL")) ? 1 : 0;
    }
}
