using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunGpuEngineUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> waitForAnalysis)
    {
        var original = collector!.GpuReadOverride;
        var healthy = live!;
        async Task WaitFor(Func<bool> ready)
        {
            for (int i = 0; i < 150; i++) { if (ready()) return; await Task.Delay(100); }
            throw new TimeoutException("Live collector did not reach the expected GPU-engine state.");
        }
        try
        {
            collector.GpuReadOverride = () => GpuLoadObservations.Normalize([
                ("D3D Compute_0", 96d), ("D3D Video Encode", 88d), ("GPU Power", 100d)]);
            await WaitFor(() => live!.Sample.Time > healthy.Sample.Time && live.Sample.GpuEngines?.Any(e => e.Kind == "compute" && e.Load == 96) == true);
            require(live!.Sample.GpuEngines is { Count: 2 } engines && engines.Single(e => e.Kind == "compute").Load == 96
                && engines.Single(e => e.Kind == "encode").Load == 88 && live.Sample.Cpu.HasValue && live.Sample.MemoryTotalGb > 0,
                "Production Collector preserves separate injected compute/encoding engines and excludes unrelated power load while real CPU/memory continue");
            require(GpuMetricLabel.Text.Contains("核心/3D") && GpuValue.Text == Percent(live.Sample.Gpu)
                && GpuEngineLiveDetails.Text.Contains("D3D Compute_0") && GpuEngineLiveDetails.Text.Contains("D3D Video Encode")
                && GpuEngineLiveDetails.Text.Contains("96.0%") && GpuEngineLiveDetails.Text.Contains("88.0%")
                && GpuEngineLiveDetails.Text.Contains("不同引擎占用不能相加") && !GpuEngineLiveDetails.Text.Contains("GPU Power"),
                "Native live controls display engine percentages, raw sources and scope separately from the unchanged actual core/3D metric");
            LiveNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            GpuEngineLiveExpander.IsExpanded = true; GpuEngineLiveExpander.BringIntoView(new Rect(0, 0, 1, 1));
            await screenshot("45-gpu-engines-injected");
            var completeAt = live.Sample.Time;
            collector.GpuReadOverride = () => GpuLoadObservations.Normalize([("D3D Compute_0", 96d), ("D3D Compute_1", (double?)null)]);
            await WaitFor(() => live!.Sample.Time > completeAt && live.Sample.GpuEngines is { Count: 1 } partial && !partial[0].Complete);
            require(live!.Sample.GpuEngines!.Single().Load == 96 && GpuEngineLiveDetails.Text.Contains("部分读数缺失")
                && GpuEngineLiveDetails.Text.Contains("D3D Compute_1"), "A missing node remains visibly incomplete without erasing the known busy node");
            await screenshot("46-gpu-engines-partial");
        }
        finally { collector.GpuReadOverride = original; }
        var injectedAt = live!.Sample.Time;
        await WaitFor(() => live!.Sample.Time > injectedAt);
        require(GpuEngineLiveDetails.Text == GpuEngineEvidence.Describe(live!.Sample) && live.Sample.GpuEngineVersion == GpuEngineEvidence.Version
            && live.Sample.Cpu.HasValue && live.Sample.MemoryTotalGb > 0,
            "Restoring the real GPU sensor path refreshes native controls without retaining injected engine values");
        File.WriteAllText(Path.Combine(output, "actual-gpu-engine-inventory.json"), JsonSerializer.Serialize(new
        {
            live.GpuName, live.Sample.Time, live.Sample.Gpu, live.Sample.GpuEngineVersion, live.Sample.GpuEngines, live.SensorStatus,
            Notice = "Actual post-restore GPU sensor inventory; exposed sensors do not prove all hardware engines or driver freshness."
        }, new JsonSerializerOptions { WriteIndented = true }));
        await screenshot("47-gpu-engines-actual-restored");

        if (!paused) TogglePause();
        const string scene = "GPU 引擎核对（合成）";
        var sampleTime = DateTimeOffset.Now.AddHours(-12);
        await Task.Run(() =>
        {
            foreach (int day in new[] { 9, 8, 2, 1 })
                for (int index = 0; index < 180; index++)
                    store.Append(new Sample
                    {
                        Time = sampleTime.AddDays(-day).AddSeconds(-index * 10), HardwareId = collector.HardwareId,
                        Scene = scene, Seconds = 10, Active = true, ObservationVersion = ActivityRules.Version,
                        Cpu = 5, CoreMax = 15, MemoryUsedGb = 9, MemoryTotalGb = 64, CommitGb = 11, Gpu = 3, GpuPeak = 3,
                        GpuEngineVersion = day < 7 ? GpuEngineEvidence.Version : "",
                        GpuEngines = day < 7 ? [new("compute", ["D3D Compute_0"], 96) { Complete = true },
                            new("encode", ["D3D Video Encode"], 88) { Complete = false }] : null
                    });
        });
        AdviceNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await RefreshAnalysis();
        await waitForAnalysis(() => report!.SceneProfiles.Any(p => p.Name == scene));
        AnalysisSceneCombo.SelectedItem = AnalysisSceneCombo.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == scene);
        await waitForAnalysis(() => report!.SelectedScene == scene && report.GpuEngineProfiles.Any(p => p.Kind == "compute" && p.P95 == 96));
        require(report!.Cards.Single(c => c.Key == "gpu").Status == "pressure"
            && report.SceneProfiles.Single(p => p.Name == scene).GpuP95 == 3
            && report.GpuEngineProfiles.Single(p => p.Kind == "encode").CompleteHours == 0,
            "Isolated synthetic history reaches production analysis with busy compute, partial encoding and the original low core/3D scope intact");
        GpuEngineHistoryExpander.IsExpanded = true; UpdateLayout();
        var rendered = new List<string>();
        void ReadRenderedText(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock text) rendered.Add(text.Text);
                ReadRenderedText(child);
            }
        }
        ReadRenderedText(GpuEngineProfileList);
        require(GpuEngineProfileList.Items.Count == GpuEngineEvidence.Kinds.Length && rendered.Any(t => t.Contains("D3D Compute_0"))
            && rendered.Any(t => t.Contains("类别完整 0 分钟")) && rendered.Any(t => t.Contains("已观察 P95 96.0%"))
            && GpuEngineHistoryNotice.Text.Contains("不等于全GPU覆盖") && GpuEngineHistoryNotice.Text.Contains("部分读数可能低估"),
            "Rendered native history controls expose weighted values, separate observed/complete durations, missing categories and source scope");
        require(report.DemandTrends.Single(t => t.Key == "gpu-engine/compute" && t.Days == 7) is { Status: "insufficient", PreviousP95: null }
            && DemandTrendList.Items.Count == report.DemandTrends.Count,
            "The actual history UI preserves an unknown earlier engine baseline instead of claiming a zero-to-high demand change");
        GpuEngineHistoryExpander.BringIntoView(new Rect(0, 0, 1, 1));
        await screenshot("48-gpu-engines-history");
        double originalWidth = Width, originalHeight = Height;
        try { Width = 960; Height = 640; GpuEngineHistoryExpander.BringIntoView(new Rect(0, 0, 1, 1)); await screenshot("49-gpu-engines-minimum-window"); }
        finally { Width = originalWidth; Height = originalHeight; }
        string html = ReportExporter.Html(report, live);
        File.WriteAllText(Path.Combine(output, "gpu-engine-report.html"), html);
        require(html.Contains("GPU 分引擎历史") && html.Contains("D3D Compute_0") && html.Contains("类别完整时长不等于全GPU覆盖")
            && html.Contains("GPU 核心/3D P95") && html.Contains(GpuEngineEvidence.Version),
            "HTML export retains original GPU scope, engine provenance and the complete-category limitation");
        string csv = Path.Combine(output, "gpu-engine-history.csv");
        ReportExporter.Csv(csv, store.Read(60, collector.HardwareId).Where(s => s.Scene == scene));
        using var parser = new TextFieldParser(csv) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); var header = parser.ReadFields()!; var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        int versionColumn = Array.IndexOf(header, "gpu_engine_version"), engineColumn = Array.IndexOf(header, "gpu_engines_json");
        require(rows.Count == 720 && rows.All(r => r.Length == header.Length) && rows.Count(r => r[versionColumn] == GpuEngineEvidence.Version) == 360
            && rows.Where(r => r[versionColumn] == "").All(r => r[engineColumn] == "")
            && rows.Where(r => r[versionColumn] == GpuEngineEvidence.Version).All(r => JsonSerializer.Deserialize<List<GpuEngineSample>>(r[engineColumn])!.Count == 2),
            "CSV round-trips engine version and per-node JSON while old unknown engine rows remain empty");
        File.WriteAllText(Path.Combine(output, "gpu-engine-NOTICE.txt"),
            "The production Collector receives injected named GPU-engine readings for the first two captures. CPU, memory and the legacy GPU core/3D path continue to use actual hardware. The injection is restored before actual-gpu-engine-inventory.json is saved. Historical analysis and exports use explicitly labelled synthetic rows in the empty isolated UI-test data directory; they do not describe this computer's needs. Native WPF controls, source labels, partial coverage, history analysis and export are exercised. No GPU workload, model service, driver fault or physical hardware change is used.");
    }
}

public static partial class SelfTests
{
    private static void RunGpuEngineExportTests(Action<string, Action> check, Action<bool, string> assert)
    {
        static (string[] Header, List<string[]> Rows) Read(string path)
        {
            using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(","); var header = parser.ReadFields()!; var rows = new List<string[]>();
            while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
            return (header, rows);
        }
        check("CSV retains activity-1.1 provenance and correctly quotes nested engine JSON and arbitrary raw text", () =>
        {
            string path = Path.GetFullPath("artifacts/gpu-engine-export.csv");
            var row = new Sample
            {
                Time = DateTimeOffset.Now, Seconds = 10, Active = true, Gpu = 3, HardwareId = "test,\"machine\"", Scene = "=合成,\"场景\"\n第二行",
                ObservationVersion = "activity-1.1", GpuEngineVersion = GpuEngineEvidence.Version,
                GpuEngines = [new("compute", ["D3D Compute_0", "raw,\"source\"\n<script>"], 96) { Complete = false, Peak = 99, ObservedSeconds = 10, CoverageSeconds = 0, HeavySeconds = 10, HighSeconds = 5 }]
            };
            ReportExporter.Csv(path, [row]); var parsed = Read(path); var values = parsed.Rows.Single();
            string Value(string name) => values[Array.IndexOf(parsed.Header, name)];
            assert(values.Length == parsed.Header.Length && Value("hardware_id") == row.HardwareId && Value("scene") == "'" + row.Scene
                && Value("observation_version") == "activity-1.1" && Value("gpu_engine_version") == GpuEngineEvidence.Version
                && Value("gpu_metric_scope") == "core_or_3d" && Value("gpu_percent") == "3", "CSV quoting, activity provenance or legacy metric scope changed");
            var restored = JsonSerializer.Deserialize<List<GpuEngineSample>>(Value("gpu_engines_json"))!.Single();
            assert(restored.Sources.SequenceEqual(row.GpuEngines![0].Sources) && restored.Load == 96 && restored.Peak == 99
                && !restored.Complete && restored.CoverageSeconds == 0 && restored.HighSeconds == 5, "nested JSON lost raw source labels or partial-duration evidence");
        });
        check("CSV missing engines remain empty while an unknown version is preserved as raw provenance", () =>
        {
            string path = Path.GetFullPath("artifacts/gpu-engine-missing.csv");
            ReportExporter.Csv(path, [new Sample(), new Sample { GpuEngineVersion = "future-engine-version" }]);
            var parsed = Read(path); int version = Array.IndexOf(parsed.Header, "gpu_engine_version"), json = Array.IndexOf(parsed.Header, "gpu_engines_json");
            assert(parsed.Rows[0][version] == "" && parsed.Rows[1][version] == "future-engine-version" && parsed.Rows.All(row => row[json] == ""),
                "missing engine data became a current-rule zero reading or lost its unknown-version provenance");
        });
        check("HTML encodes GPU source labels and distinguishes observed category coverage from whole-card evidence", () =>
        {
            var now = DateTimeOffset.Now;
            var report = new AnalysisReport(now, 7, 1, 1, 1, false, [], [], [], "")
            {
                HardwareId = "test-machine",
                GpuEngineProfiles = [new("compute", "GPU 计算", 1, .25, 96, 100, 10, ["D3D Compute_0", "<script>source</script> & \"node\""])],
                SceneProfiles = [new("合成", 1, 1, 5, 10, 9, 3, 3, 1, 1)]
            };
            var live = new LiveSnapshot(new Sample { Time = now, GpuEngineVersion = GpuEngineEvidence.Version,
                GpuEngines = [new("compute", ["D3D Compute_0"], 96) { Complete = true }] }, "CPU", "<b>GPU</b>", "test", [], []);
            string html = ReportExporter.Html(report, live);
            assert(!html.Contains("<script>source</script>") && html.Contains("&lt;script&gt;source&lt;/script&gt;") && html.Contains("&lt;b&gt;GPU&lt;/b&gt;")
                && html.Contains("类别完整时长不等于全GPU覆盖") && html.Contains("原始传感器来源") && html.Contains("GPU 核心/3D P95")
                && html.Contains("导出时实时分引擎快照") && html.Contains(GpuEngineEvidence.Version), "HTML trusted source labels or overstated complete-category coverage");
        });
    }
}
