using System.IO;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuEngineEvidenceTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Date.AddDays(-1).AddHours(12), now.Offset);
        const string hardware = "gpu-evidence-synthetic-machine";
        bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < 1e-7;
        Sample Frame(double seconds, params (string Name, double? Value)[] sensors) => new()
        {
            Time = start, HardwareId = hardware, Seconds = seconds, Active = true, Scene = "合成引擎任务",
            ObservationVersion = ActivityRules.Version, GpuEngineVersion = GpuEngineEvidence.Version,
            GpuEngines = GpuEngineEvidence.Current(GpuLoadObservations.Normalize(sensors)),
            Cpu = 5, CoreMax = 10, Gpu = 0, MemoryTotalGb = 64, MemoryUsedGb = 8, CommitGb = 9
        };
        void InStore(Action<LocalStore> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "PerfCompass-gpu-evidence-" + Guid.NewGuid().ToString("N"));
            using var lease = DataRootLease.Acquire(path) ?? throw new IOException("Cannot lease isolated GPU test directory.");
            action(new LocalStore(path));
        }
        AnalysisReport Analyze(LocalStore store) => HistoryAnalysis.Analyze(store,
            WorkloadCoverage.Select(new(), true, hardware), 7, asOf: now, hardwareId: hardware);

        check("Known compute and video work activates a low-core background sample while missing engines remain unknown", () =>
        {
            foreach (string name in new[] { "D3D Compute_0", "GPU Video Engine", "D3D Video Encode", "D3D Video Decode", "D3D Copy" })
            {
                var sample = Frame(2, ("GPU Core", 0), (name, 92));
                double? knownLoad = sample.GpuEngines!.Max(e => e.Load);
                assert(ActivityRules.IsActive(900, sample.Cpu, sample.CoreMax, sample.Gpu, 0, 0, 0, knownLoad)
                    && !ActivityRules.IsActive(900, sample.Cpu, sample.CoreMax, sample.Gpu, 0, 0, 0),
                    "a busy non-core engine did not supply independent activity evidence");
                assert(GpuEngineEvidence.Valid(sample) && sample.Gpu == 0, "new engines changed the legacy core metric");
            }
            var missing = Frame(2, ("D3D Video Encode", null));
            assert(missing.GpuEngines!.Single().Load == null && !missing.GpuEngines!.Single().Complete
                && !ActivityRules.IsActive(900, 0, 0, 0, 0, 0, 0, missing.GpuEngines!.Max(e => e.Load)),
                "a missing engine manufactured zero load or activity");
            var partial = Frame(2, ("D3D Compute_0", 90), ("D3D Compute_1", null));
            assert(!partial.GpuEngines!.Single().Complete
                && ActivityRules.IsActive(900, 0, 0, 0, 0, 0, 0, partial.GpuEngines!.Single().Load),
                "known busy work was discarded merely because another node was missing");
        });
        check("GPU engine merging weights measured seconds and preserves short peaks and duty without filling gaps", () =>
        {
            var merged = SampleBatch.Merge([
                Frame(2, ("D3D Compute_0", 100)),
                Frame(3, ("D3D Compute_0", 20)) with { Time = start.AddSeconds(3) },
                Frame(5, ("D3D Compute_0", null)) with { Time = start.AddSeconds(8) }]);
            var engine = merged.GpuEngines!.Single();
            assert(merged.Seconds == 10 && merged.Gpu == 0 && Near(engine.Load, 52) && engine.Peak == 100
                && engine.ObservedSeconds == 5 && engine.CoverageSeconds == 5 && !engine.Complete
                && engine.HeavySeconds == 2 && engine.HighSeconds == 2 && GpuEngineEvidence.Valid(merged),
                "sample count or missing seconds changed the weighted mean, peak, or high-load exposure");
        });
        check("Partial node observations retain positive workload but only complete polls contribute category coverage", () =>
        {
            var merged = SampleBatch.Merge([
                Frame(2, ("D3D Compute_0", 90), ("D3D Compute_1", null)),
                Frame(3, ("D3D Compute_0", 20), ("D3D Compute_1", 10)) with { Time = start.AddSeconds(3) },
                Frame(5, ("D3D Compute_0", null), ("D3D Compute_1", null)) with { Time = start.AddSeconds(8) }]);
            var engine = merged.GpuEngines!.Single();
            assert(Near(engine.Load, 48) && engine.Peak == 90 && engine.ObservedSeconds == 5
                && engine.CoverageSeconds == 3 && !engine.Complete && engine.HeavySeconds == 2 && engine.HighSeconds == 0,
                "partial nodes gained complete coverage or erased known heavy work");
            assert(GpuEngineEvidence.Valid(merged), "valid mixed-coverage batch failed validation");
        });
        check("Merging already aggregated engines uses observed duration and preserves previously measured duty", () =>
        {
            var first = Frame(10, ("D3D Compute_0", 40));
            first = first with { GpuEngines = [first.GpuEngines!.Single() with
                { Complete = false, Peak = 80, ObservedSeconds = 2, CoverageSeconds = 2, HeavySeconds = .5, HighSeconds = 0 }] };
            var merged = SampleBatch.Merge([first, Frame(5, ("D3D Compute_0", 100)) with { Time = start.AddSeconds(5) }]);
            var engine = merged.GpuEngines!.Single();
            assert(Near(engine.Load, 580d / 7) && engine.Peak == 100 && engine.ObservedSeconds == 7
                && engine.CoverageSeconds == 7 && engine.HeavySeconds == 5.5 && engine.HighSeconds == 5
                && !engine.Complete && GpuEngineEvidence.Valid(merged),
                "a second merge replaced measured exposure with the batch mean or total sample duration");
        });
        check("Zero-duration interruptions and idle intervals cannot manufacture GPU observation time", () =>
        {
            var interruption = SampleBatch.Merge([Frame(0, ("D3D Compute_0", 100)) with { SamplingIssue = "clock-change" }]);
            var idle = SampleBatch.Merge([Frame(2, ("D3D Video Decode", 10)) with { Active = false },
                Frame(3, ("D3D Video Decode", 15)) with { Active = false, Time = start.AddSeconds(3) }]);
            foreach (var sample in new[] { interruption, idle })
            {
                var engine = sample.GpuEngines!.Single();
                assert(sample.Seconds == 0 && !sample.Active && engine.ObservedSeconds == 0 && engine.CoverageSeconds == 0
                    && engine.HeavySeconds == 0 && engine.HighSeconds == 0 && GpuEngineEvidence.Valid(sample),
                    "idle or interrupted observation acquired effective GPU duration");
            }
            InStore(store =>
            {
                store.Append(interruption); store.Append(idle with { Time = start.AddSeconds(30) });
                var report = Analyze(store);
                assert(!report.HistoryQuality.HasIssues && report.ActiveHours == 0 && report.SamplingGapCount == 1
                    && report.GpuEngineProfiles.All(p => p.ObservedHours == 0 && p.HeavyMinutes == 0 && p.P95 == null),
                    "history converted point observations into useful duration or pressure");
            });
        });
        check("All-missing engine batches survive persistence as unknown instead of a zero percentile", () =>
        {
            var merged = SampleBatch.Merge([Frame(2, ("D3D Video Encode", null)),
                Frame(3, ("D3D Video Encode", null)) with { Time = start.AddSeconds(3) }]);
            var engine = merged.GpuEngines!.Single();
            assert(engine.Load == null && engine.Peak == null && engine.ObservedSeconds == 0 && engine.CoverageSeconds == 0
                && !engine.Complete && GpuEngineEvidence.Valid(merged), "missing batch became known idle");
            InStore(store =>
            {
                store.Append(merged); var report = Analyze(store); var profile = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
                assert(report.SampleCount == 1 && !report.HistoryQuality.HasIssues && profile.P95 == null
                    && profile.Peak == null && profile.ObservedHours == 0 && profile.CompleteHours == 0
                    && profile.Sources.SequenceEqual(new[] { "D3D Video Encode" }), "actual history erased unknown-source evidence");
            });
        });
        check("Hardware, activity and GPU observation changes flush batches and reject mixed merges", () =>
        {
            var first = Frame(2, ("D3D Compute_0", 90));
            var changes = new[] { first with { HardwareId = "different-machine" },
                first with { ObservationVersion = "activity-1.1" }, first with { GpuEngineVersion = "future-engine-version" } };
            foreach (var next in changes)
            {
                assert(SampleBatch.MustFlushBefore([first], next), "a changed observation scope stayed in the batch");
                bool rejected = false; try { _ = SampleBatch.Merge([first, next]); } catch (ArgumentException) { rejected = true; }
                assert(rejected, "mixed observation scope was accepted by direct merge");
            }
            assert(!SampleBatch.MustFlushBefore([first], first with { Time = start.AddSeconds(2) })
                && SampleBatch.MustFlushBefore([first], first with { Seconds = 0 }), "ordinary poll or interruption flush changed");
        });
        check("Legacy activity history remains readable while its previous task-coverage confirmation expires", () =>
        {
            InStore(store =>
            {
                var legacy = Frame(10) with { ObservationVersion = "activity-1.1", GpuEngineVersion = "", GpuEngines = null };
                store.Append(legacy);
                var settings = new Settings(Representative: true) { RepresentativeHardwareId = hardware, RepresentativeObservationVersion = "activity-1.1" };
                var report = HistoryAnalysis.Analyze(store, settings, 7, asOf: now, hardwareId: hardware);
                assert(ActivityRules.KnownVersion("activity-1.1") && report.SampleCount == 1 && !report.HistoryQuality.HasIssues
                    && !report.RepresentativeConfirmed && !report.Ready && !WorkloadCoverage.For(settings, hardware).Confirmed,
                    "legacy history was rejected or old task coverage silently upgraded");
                assert(report.GpuEngineProfiles.All(p => p.P95 == null && p.ObservedHours == 0)
                    && !report.DemandTrends.Any(t => t.Key.StartsWith("gpu-engine/", StringComparison.Ordinal)),
                    "old core observations were rewritten as per-engine history");
                var renewed = WorkloadCoverage.Select(settings, true, hardware);
                assert(renewed.RepresentativeObservationVersion == ActivityRules.Version && WorkloadCoverage.For(renewed, hardware).Confirmed
                    && settings.RepresentativeObservationVersion == "activity-1.1", "explicit re-confirmation changed old settings or failed");
            });
        });
        check("GPU engine history rejects invalid versions, sources and durations with exact diagnostics and intact originals", () =>
        {
            var valid = Frame(10, ("D3D Compute_0", 90)); var engine = valid.GpuEngines!.Single();
            Sample With(GpuEngineSample value) => valid with { GpuEngines = [value] };
            var invalid = new[]
            {
                valid with { GpuEngineVersion = "unknown-future" }, valid with { GpuEngineVersion = "" },
                valid with { GpuEngines = null }, valid with { ObservationVersion = "unknown-activity" },
                valid with { GpuEngines = [engine, engine] },
                With(engine with { Kind = "encode" }), With(engine with { Sources = ["GPU Memory Controller"] }),
                With(engine with { Sources = ["D3D Compute_0", "GPU Bus"] }), With(engine with { Sources = [] }),
                With(engine with { Sources = [new string('x', 161)] }), With(engine with { ObservedSeconds = -1 }),
                With(engine with { ObservedSeconds = 11 }), With(engine with { CoverageSeconds = 11 }),
                With(engine with { HeavySeconds = 11 }), With(engine with { HighSeconds = 11 }),
                With(engine with { Complete = false, ObservedSeconds = 2, CoverageSeconds = 3 }),
                With(engine with { Complete = false, ObservedSeconds = 2, HeavySeconds = 3 }),
                With(engine with { Load = null, Complete = false, ObservedSeconds = 1 }),
                With(engine with { Load = null, Complete = true }), With(engine with { Peak = 80 }),
                valid with { Seconds = 16 }, valid with { Seconds = -1 }
            };
            InStore(store =>
            {
                string file = Path.Combine(store.Root, $"samples-{start.LocalDateTime:yyyy-MM-dd}.jsonl");
                foreach (var bad in invalid)
                {
                    File.WriteAllLines(file, [JsonSerializer.Serialize(valid), JsonSerializer.Serialize(bad with { Time = start.AddSeconds(20) })], new UTF8Encoding(false));
                    byte[] original = File.ReadAllBytes(file); var report = Analyze(store);
                    var issue = report.HistoryQuality.Issues.Single();
                    assert(report.SampleCount == 1 && report.HistoryQuality.IssueCount == 1 && issue.Line == 2
                        && issue.File == Path.GetFileName(file) && !report.Ready && File.ReadAllBytes(file).SequenceEqual(original),
                        "invalid engine row disappeared without a precise issue or modified original history");
                    assert(report.Cards.All(c => c.Status is not ("surplus" or "review" or "changed")),
                        "corrupted engine evidence retained a configuration downgrade conclusion");
                }
            });
        });
        check("Duplicate GPU engine JSON members cannot replace high load or change source and coverage declarations", () =>
        {
            InStore(store =>
            {
                var valid = Frame(10, ("D3D Compute_0", 90));
                string normal = JsonSerializer.Serialize(Frame(10, ("D3D Compute_0", 0)) with { Time = start.AddSeconds(20) });
                string file = Path.Combine(store.Root, $"samples-{start.LocalDateTime:yyyy-MM-dd}.jsonl");
                var duplicates = new[]
                {
                    normal.Replace("\"Load\":0", "\"Load\":100,\"Load\":0", StringComparison.Ordinal),
                    normal.Replace("\"Load\":0", "\"\\u004coad\":100,\"Load\":0", StringComparison.Ordinal),
                    normal.Replace("\"Complete\":true", "\"Complete\":false,\"Complete\":true", StringComparison.Ordinal),
                    normal.Replace("\"Sources\":[\"D3D Compute_0\"]", "\"Sources\":[\"GPU Memory Controller\"],\"Sources\":[\"D3D Compute_0\"]", StringComparison.Ordinal),
                    normal.Replace("\"Load\":0", "\"FutureEngineField\":1,\"FutureEngineField\":2,\"Load\":0", StringComparison.Ordinal)
                };
                foreach (string duplicate in duplicates)
                {
                    assert(duplicate != normal, "duplicate engine JSON fixture did not change the serialized row");
                    File.WriteAllLines(file, [JsonSerializer.Serialize(valid), duplicate], new UTF8Encoding(false));
                    byte[] original = File.ReadAllBytes(file); var report = Analyze(store);
                    var issue = report.HistoryQuality.Issues.Single();
                    assert(report.SampleCount == 1 && report.HistoryQuality.IssueCount == 1 && issue.Line == 2
                        && issue.File == Path.GetFileName(file) && issue.Reason == "显卡引擎对象存在重复字段"
                        && File.ReadAllBytes(file).SequenceEqual(original),
                        "duplicate engine field silently used its last value, lost its location or modified the original");
                    assert(report.GpuEngineProfiles.Single(p => p.Kind == "compute").P95 == 90,
                        "rejected low-load replacement contaminated the remaining engine evidence");
                }
                // The new validation is scoped to GpuEngines objects; existing
                // unknown nested metadata in legacy records keeps its old behavior.
                string legacy = JsonSerializer.Serialize(valid with { ObservationVersion = "activity-1.1", GpuEngineVersion = "", GpuEngines = null });
                File.WriteAllText(file, legacy[..^1] + ",\"LegacyDetails\":{\"Load\":100,\"Load\":0}}\n", new UTF8Encoding(false));
                var preserved = Analyze(store);
                assert(preserved.SampleCount == 1 && !preserved.HistoryQuality.HasIssues,
                    "GPU object validation changed unrelated legacy nested metadata handling");
            });
        });
        check("GPU engine duty must agree with peak thresholds and high load cannot exceed heavy load", () =>
        {
            var sample = Frame(10, ("D3D Compute_0", 0)); var engine = sample.GpuEngines!.Single();
            foreach (var contradiction in new[]
            {
                engine with { Peak = 79, HeavySeconds = 1 }, engine with { Peak = 94, HighSeconds = 1, HeavySeconds = 1 },
                engine with { Peak = 100, HeavySeconds = 1, HighSeconds = 2 },
                engine with { Peak = 100, HighSeconds = 1 }
            })
                assert(!GpuEngineEvidence.Valid(sample with { GpuEngines = [contradiction] }), "contradictory engine exposure passed validation");
            assert(GpuEngineEvidence.Valid(sample with { GpuEngines = [engine with { Load = 20, Peak = 100, HeavySeconds = 2, HighSeconds = 1 }] }),
                "a measured short peak was rejected merely because the batch average is low");
        });
        check("Same-name node counts survive merging and source changes cannot claim complete comparable coverage", () =>
        {
            var two = Frame(2, ("D3D Compute", 70), ("D3D Compute", 90));
            var stable = SampleBatch.Merge([two, two with { Time = start.AddSeconds(2) }]).GpuEngines!.Single();
            assert(stable.Sources.SequenceEqual(new[] { "D3D Compute", "D3D Compute" })
                && stable.CoverageSeconds == 4 && stable.Complete, "stable duplicate node labels lost their scope");
            foreach (var changed in new[] { Frame(2, ("D3D Compute", 80)), Frame(2, ("D3D Compute_0", 80)) })
            {
                var merged = SampleBatch.Merge([two, changed with { Time = start.AddSeconds(2) }]); var engine = merged.GpuEngines!.Single();
                assert(engine.Sources.Count(s => s == "D3D Compute") == 2 && engine.CoverageSeconds == 0
                    && !engine.Complete && engine.ObservedSeconds == 4 && engine.Peak == 90 && GpuEngineEvidence.Valid(merged),
                    "changed engine source multiplicity acquired comparable coverage or erased known observations");
            }
        });
        check("Actual history analysis reads persisted GPU engine averages peaks source coverage and duty", () =>
        {
            var merged = SampleBatch.Merge([
                Frame(2, ("D3D Compute_0", 100)),
                Frame(3, ("D3D Compute_0", 20)) with { Time = start.AddSeconds(3) },
                Frame(5, ("D3D Compute_0", null)) with { Time = start.AddSeconds(8) }]);
            InStore(store =>
            {
                store.Append(merged);
                var saved = store.Read(7, hardware).Single(); var savedEngine = saved.GpuEngines!.Single();
                assert(saved.GpuEngineVersion == GpuEngineEvidence.Version && saved.ObservationVersion == ActivityRules.Version
                    && savedEngine.ObservedSeconds == 5 && savedEngine.CoverageSeconds == 5 && savedEngine.HighSeconds == 2,
                    "serialization or the production history reader discarded new evidence fields");
                var report = Analyze(store); var profile = report.GpuEngineProfiles.Single(p => p.Kind == "compute");
                assert(!report.HistoryQuality.HasIssues && report.SampleCount == 1 && Near(report.ActiveHours, 10d / 3600)
                    && Near(profile.ObservedHours, 5d / 3600) && Near(profile.CompleteHours, 5d / 3600)
                    && Near(profile.P95, 52) && profile.Peak == 100 && Near(profile.HeavyMinutes, 2d / 60)
                    && profile.Sources.SequenceEqual(new[] { "D3D Compute_0" }), "actual report reconstructed different engine evidence");
                assert(report.History.Single().Gpu == 0 && report.Cards.Single(c => c.Key == "gpu").Status != "review",
                    "new engine load contaminated the old core chart or left a downgrade review");
                var encode = report.GpuEngineProfiles.Single(p => p.Kind == "encode");
                assert(encode.P95 == null && encode.ObservedHours == 0 && encode.Sources.Length == 0,
                    "an absent engine gained zero-load evidence");
            });
        });
    }
}
