using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace PerfCompass;

public static class ReportExporter
{
    public static string Html(AnalysisReport report, LiveSnapshot? live)
    {
        string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var cards = string.Join("", report.Cards.Select(c => $"<section><small>{E(c.Name)}</small><h2>{E(c.Title)}</h2><p>{E(c.Suggestion)}</p><p class='evidence'>{E(c.Evidence)}</p><p class='muted'>{E(c.Limits)}</p></section>"));
        string P(double? value, string unit = "%") => value.HasValue ? $"{value:0.0}{unit}" : "—";
        var sceneRows = string.Join("", report.SceneProfiles.Select(p => $"<tr><td>{E(p.Name)}</td><td>{E(p.DurationLabel)}</td><td>{P(p.CpuP95)}</td><td>{P(p.MemoryPeakGb, " GB")}</td><td>{P(p.GpuP95)}</td><td>{P(p.GpuPeak)}</td></tr>"));
        string sceneTable = report.SceneProfiles.Count > 0 ? $"<section><h2>场景明细</h2><p class='muted'>当前时间范围内的全部场景 · 有效时长加权</p><div class='table-wrap'><table><thead><tr><th>场景</th><th>有效时长</th><th>CPU P95</th><th>内存峰值</th><th>GPU P95</th><th>GPU 峰值</th></tr></thead><tbody>{sceneRows}</tbody></table></div></section>" : "";
        var trendItems = string.Join("", report.DemandTrends.Select(t => $"<article class='demand'><h3>{E(t.Name)} · {E(t.ComparisonLabel)} — {E(t.StatusLabel)}</h3><p>{E(t.ReadingsLabel)}</p><p class='muted'>{E(t.BusyLabel)}</p><p class='muted'>{E(t.SceneLabel)}</p><p>{E(t.Suggestion)}</p></article>"));
        string trendSection = report.DemandTrends.Count > 0 ? $"<section class='trend-section'><h2>近期需求变化</h2><p>近期反复出现的任务优先约束配置建议；较早记录用于核对偶发任务。时期对照使用可用的最近 60 天记录，并跟随当前所选场景。</p>{trendItems}</section>" : "";
        string experienceItems = string.Join("", report.ExperienceEvents.Select(e => $"<article class='demand'><h3>{E(e.Title)} · {E(e.StateLabel)}</h3><p>{E(e.Note)}</p><p>{E(e.ParsedDescription)}</p><p class='muted'>{E(e.EvidenceLabel).Replace("\n", "<br>")}</p><p>{E(e.Conclusion)}</p></article>"));
        string experienceSection = report.ExperienceEvents.Count > 0 ? $"<section class='trend-section'><h2>体验与负载线索</h2><p>用户记录的卡顿或等待，以及此前两分钟采样。两者同时出现不能证明原因。近期未处理的体验问题会约束降档建议。</p>{experienceItems}</section>" : "";
        string requirementsSection = report.ConfirmedRequirements is { } plan ? $"<section><h2>已确认的结构化需求</h2><p>{E(plan.Summary)}</p><p>AI 使用方式：{E(plan.AiModeLabel)}</p><p class='muted'>解析规则 {E(plan.RuleVersion)}；每项依据保留在配置建议中。需求仅约束候选验证，不据此推导具体型号。</p></section>" : "";
        if (report.SemanticRequirements is { } semantic)
            requirementsSection += $"<section class='trend-section'><h2>逐项核对的购机条件</h2><p class='muted'>提取来源：{E(semantic.Provider)} / {E(semantic.Model)}；{semantic.ConfirmedAt.LocalDateTime:yyyy-MM-dd HH:mm} 用户确认。确认并不证明模型理解或候选配置已被实测。</p>"
                + string.Join("", semantic.Facts.Select(f => $"<article class='demand'><h3>{E(f.Label)}</h3><p>{E(SemanticRequirements.Label(f.Kind))}：{E(f.Detail)}</p><p class='evidence'>原文：{E(f.Evidence)}</p></article>")) + "</section>";
        return $$"""
        <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PerfCompass 配置报告</title>
        <style>body{font:15px/1.8 system-ui,'Microsoft YaHei',sans-serif;background:#f4f7f8;color:#192a36;margin:0}main{max-width:900px;margin:50px auto;padding:24px}h1{font-size:32px}small{color:#007f73}section{background:white;border:1px solid #e3eaec;border-radius:12px;padding:26px;margin:18px 0}h2{font-size:20px}.muted{color:#677a87;font-size:13px}.evidence{padding:14px;background:#eef5f4}.table-wrap{overflow:auto}table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:10px;border-bottom:1px solid #e3eaec;white-space:nowrap}.demand{border-bottom:1px solid #e3eaec;padding:12px 0;break-inside:avoid}.demand h3{font-size:15px}@media print{.trend-section{break-inside:auto}body{background:white}main{margin:0}section{break-inside:avoid} }</style>
        <main><small>PERFCOMPASS / 配置指南</small><h1>{{E(report.Summary)}}</h1><p>{{report.Generated.LocalDateTime:yyyy-MM-dd HH:mm}} · 最近 {{report.WindowDays}} 天 · {{report.SampleCount:N0}} 条记录</p>
        <p>有效使用 {{E(report.DurationLabel)}} / {{report.ActiveDays}} 天 · 证据状态：{{E(report.Level)}}</p><p>{{E(live?.CpuName)}}<br>{{E(live?.GpuName)}}</p>
        <p>当前建议范围：{{E(report.SelectedScene ?? "全部场景")}} · 已记录场景：{{E(string.Join("、", report.Scenes))}}</p>{{requirementsSection}}{{trendSection}}{{experienceSection}}{{sceneTable}}{{cards}}<section><h2>下一台电脑的新需求</h2><p>{{E(string.IsNullOrWhiteSpace(report.FutureNeeds) ? "尚未填写" : report.FutureNeeds).Replace("\n", "<br>")}}</p></section>
        <p class="muted">规则版本 {{E(report.MethodVersion)}}。P95 按有效采样时长加权，短时峰值另行保留。余量判断需要当前建议范围至少 10 小时、3 天及典型任务确认，并有最近 7 天至少 30 分钟、2 天的有效记录。时期变化的阈值为透明启发式设置。具体型号与价格需结合候选机器实测。本报告由本地记录生成。</p></main></html>
        """;
    }
    public static void Csv(string path, IEnumerable<Sample> samples)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp,hardware_id,scene,active,seconds,cpu_percent,core_max_percent,memory_used_gb,memory_peak_gb,memory_total_gb,commit_gb,gpu_percent,vram_used_gb,vram_total_gb,disk_read_mbps,disk_write_mbps,disk_busy_percent,disk_used_gb,disk_total_gb,max_partition_percent,cpu_temp_c,gpu_temp_c,cpu_coverage_seconds,core_coverage_seconds,memory_coverage_seconds,commit_coverage_seconds,gpu_coverage_seconds,gpu_peak_percent,cpu_high_seconds,gpu_heavy_seconds,gpu_high_seconds,memory_high_seconds");
        foreach (var s in samples)
        {
            string D(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
            string scene = s.Scene; if (scene.Length > 0 && "=+-@".Contains(scene[0])) scene = "'" + scene;
            writer.WriteLine(string.Join(",", s.Time.ToString("O"), s.HardwareId, "\"" + scene.Replace("\"", "\"\"") + "\"", s.Active ? "1" : "0", D(s.Seconds), D(s.Cpu), D(s.CoreMax), D(s.MemoryUsedGb), D(s.MemoryPeakGb), D(s.MemoryTotalGb), D(s.CommitGb), D(s.Gpu), D(s.VramUsedGb), D(s.VramTotalGb), D(s.DiskReadMbps), D(s.DiskWriteMbps), D(s.DiskBusy), D(s.DiskUsedGb), D(s.DiskTotalGb), D(s.MaxDiskPercent), D(s.CpuTemp), D(s.GpuTemp), D(s.CpuCoverage), D(s.CoreCoverage), D(s.MemoryCoverage), D(s.CommitCoverage), D(s.GpuCoverage), D(s.GpuPeak), D(s.CpuHighSeconds), D(s.GpuHeavySeconds), D(s.GpuHighSeconds), D(s.MemoryHighSeconds)));
        }
    }
}
