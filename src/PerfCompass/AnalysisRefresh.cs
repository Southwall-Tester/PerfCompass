using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace PerfCompass;

public partial class MainWindow
{
    private TaskCompletionSource? analysisCompletion;
    private string? publishedAnalysisKey;
    private long experienceRevision;
    private const string AnalysisFailureNotice = "分析读取失败，请稍后刷新。错误已记录；原始数据和候选草稿保留。";
    private Task reportExportTask = Task.CompletedTask;
    private bool exportingReport;
    internal Task ReportExportCompletion => reportExportTask;
    internal Func<(string Path, bool Json)?>? ReportExportPathForTest { get; set; }
    internal Func<AnalysisReport, Task>? BeforeAnalysisPublishForTest { get; set; }
    internal Action<AnalysisReport>? AnalysisPublishedForTest { get; set; }

    // Capture values rather than a Settings object reference: confirmed fact
    // collections are mutable CLR objects even when their enclosing record is not.
    private string AnalysisInputKey() => JsonSerializer.Serialize(new
    { store.Settings, WindowDays = windowDays, Scene = analysisScene, Hardware = collector?.HardwareId, ExperienceRevision = experienceRevision });

    private async Task RefreshAnalysis()
    {
        string requestedKey = AnalysisInputKey();
        if (report != null && publishedAnalysisKey != requestedKey) RetireAnalysis(false);
        if (analyzing)
        {
            refreshRequested = true;
            await analysisCompletion!.Task;
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analysisCompletion = completion; analyzing = true;
        try
        {
            do
            {
                refreshRequested = false;
                analysisSchedule.Mark(Stopwatch.GetTimestamp());
                var settings = store.Settings; string? id = collector?.HardwareId;
                int days = windowDays; string? scene = analysisScene; string key = AnalysisInputKey();
                if (report == null || publishedAnalysisKey != key) RetireAnalysis(false);
                try
                {
                    var result = await Task.Run(() =>
                    {
                        var history = HistoryAnalysis.Analyze(store, settings, days, scene, hardwareId: id);
                        var experiences = store.ReadExperiencesChecked(days, id ?? "", history.Generated, scene);
                        return SemanticRequirements.Apply(RequirementParser.Apply(ExperienceAnalyzer.Attach(history, experiences), settings), settings);
                    });
                    if (BeforeAnalysisPublishForTest != null) await BeforeAnalysisPublishForTest(result);
                    // Requests arriving during a read share this completion task.
                    // Never publish an older pass before the queued latest read.
                    if (key != AnalysisInputKey() || refreshRequested) { refreshRequested = true; continue; }
                    report = result; publishedAnalysisKey = key;
                    PublishAnalysis(result, days, scene);
                    if (StatusLine.Text == AnalysisFailureNotice) StatusLine.Text = "当前范围分析已更新。";
                    AnalysisPublishedForTest?.Invoke(result);
                }
                catch (Exception error)
                {
                    store.Log(error); RetireAnalysis(true);
                    StatusLine.Text = AnalysisFailureNotice;
                }
            } while (refreshRequested && !exiting);
        }
        finally
        {
            analyzing = false; refreshRequested = false; analysisCompletion = null;
            completion.TrySetResult();
        }
    }

    private void RetireAnalysis(bool failed)
    {
        report = null; publishedAnalysisKey = null;
        string state = failed ? "分析读取失败，请刷新重试" : "正在更新当前范围的分析";
        string scope = $"所选 {windowDays} 天 · {analysisScene ?? "全部场景"}";
        AnalysisSummary.Text = state; CoverageLabel.Text = scope;
        CoverageNext.Text = "更新完成后显示配置判断；原始记录和候选草稿保留。";
        PurchaseGuidanceList.ItemsSource = Array.Empty<ConfigurationDecision>(); AdviceList.Items.Clear();
        SceneProfileList.ItemsSource = null; SceneProfileSummary.Text = state; SceneEmptyLabel.Visibility = Visibility.Collapsed;
        GpuEngineProfileList.ItemsSource = null; GpuEngineHistoryNotice.Text = state;
        DemandTrendList.ItemsSource = null; DemandTrendHeader.Text = state;
        HistoryChart.SetPoints([]); HistorySummary.Text = state + " · " + scope;
        HistoryPeriodCombo.ItemsSource = null; HistoryPeriodCombo.IsEnabled = false; HistoryPeriodDetails.Text = state;
        ExperienceList.ItemsSource = null; ExperienceSummary.Text = state + " · " + scope;
        SamplingGapLabel.Visibility = Visibility.Collapsed;
        HistoryQualityNotice.Visibility = Visibility.Collapsed;
        ExperienceQualityNotice.Visibility = ExperienceHistoryQualityNotice.Visibility = Visibility.Collapsed;
        ComparisonPage.SetAnalysisPending(state + " · " + scope);
    }

    private async Task ExportCurrentReport()
    {
        try
        {
            await RefreshAnalysis(); if (report == null) return;
            (string Path, bool Json)? target;
            if (ReportExportPathForTest != null) target = ReportExportPathForTest();
            else
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "HTML 报告|*.html|JSON 数据|*.json", FileName = $"PerfCompass-配置报告-{DateTime.Now:yyyyMMdd}" };
                target = dialog.ShowDialog(this) == true ? (dialog.FileName, dialog.FilterIndex == 2) : null;
            }
            if (target == null) return;
            // A modal picker pumps dispatcher work. Inputs or a background read
            // may have changed since it opened; take a current immutable result.
            if (analyzing || report == null || publishedAnalysisKey != AnalysisInputKey()) await RefreshAnalysis();
            var current = report; if (current == null) return;
            var currentLive = live?.Sample.HardwareId == current.HardwareId ? live : null;
            string content = target.Value.Json ? JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }) : ReportExporter.Html(current, currentLive);
            await System.IO.File.WriteAllTextAsync(target.Value.Path, content, new System.Text.UTF8Encoding(false));
            StatusLine.Text = "报告已导出：" + target.Value.Path;
        }
        catch (Exception error) { store.Log(error); StatusLine.Text = "报告未能导出，请检查目标目录并重试；原始记录保留。"; }
        finally { exportingReport = false; }
    }
}
