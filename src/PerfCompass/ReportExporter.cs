using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfCompass;

public static class ReportExporter
{
    public static string Html(AnalysisReport report, LiveSnapshot? live)
    {
        string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        string samplingSection = report.SamplingGapCount > 0 ? $"<p>{E(report.SamplingGapSummary)}</p>" : "";
        string qualitySection = report.HistoryQuality.HasNotices ? $"<section><h2>{(report.HistoryQuality.HasIssues ? "历史记录异常" : "历史重复记录已排除")}</h2><p>{E(report.HistoryQuality.Message)}</p><p class='evidence'>{E(report.HistoryQuality.Details).Replace("\n", "<br>")}</p><p>原文件保留。可在记录设置备份，并从已核对的备份恢复到新目录。</p></section>" : "";
        string Observation(Advice advice) => advice.Observation is { } o ? $"<p>{E(o.Evidence)}</p><p class='muted'>{E(o.Scope)} · 有效观测 {E(DurationText.FromHours(o.ObservedHours))} · 读数覆盖 {o.Coverage:P0}</p>" : "";
        var cards = string.Join("", report.Cards.Select(c => $"<section><small>{E(c.Name)} · {E(c.StatusLabel)}</small><h2>{E(c.DisplayTitle)}</h2>{Observation(c)}<p>{E(c.DisplaySuggestion)}</p><p class='evidence'>{E(c.Evidence)}</p><p class='muted'>{E(c.Limits)}</p></section>"));
        string P(double? value, string unit = "%") => value.HasValue ? $"{value:0.0}{unit}" : "—";
        var sceneRows = string.Join("", report.SceneProfiles.Select(p => $"<tr><td>{E(p.Name)}</td><td>{E(p.DurationLabel)}</td><td>{P(p.CpuP95)}</td><td>{P(p.MemoryPeakGb, " GiB")}</td><td>{(p.GpuHasMixedSources ? "来源混合，未合并" : P(p.GpuP95))}</td><td>{P(p.GpuPeak)}</td></tr>"));
        string sceneTable = report.SceneProfiles.Count > 0 ? $"<section><h2>场景明细</h2><p class='muted'>当前时间范围内的全部场景 · 有效时长加权{(report.HistoryQuality.HasIntervalConflicts ? "；场景时段互有重叠，不能相加，负载值暂不解释" : "")}</p><div class='table-wrap'><table><thead><tr><th>场景</th><th>有效时长</th><th>CPU P95</th><th>内存峰值</th><th>GPU 核心/3D P95</th><th>GPU 核心/3D 峰值</th></tr></thead><tbody>{sceneRows}</tbody></table></div></section>" : "";
        var engineRows = string.Join("", report.GpuEngineProfiles
            .OrderByDescending(p => p.ObservedHours > 0 || p.Sources.Length > 0)
            .ThenByDescending(p => p.HeavyMinutes > 0 || p.Peak >= 80)
            .ThenByDescending(p => p.HeavyMinutes).ThenByDescending(p => p.Peak).ThenByDescending(p => p.ObservedHours)
            .Select(p => $"<tr><td>{E(p.Name)}<br><small>{E(p.Kind)}</small></td><td>{E(DurationText.FromHours(p.ObservedHours))}</td><td>{E(DurationText.FromHours(p.CompleteHours))}</td><td>{(p.HasMixedSources ? "来源混合，未合并" : P(p.P95))}</td><td>{P(p.Peak)}</td><td>{p.HeavyMinutes:0.0}</td><td class='source'>{E(p.Sources.Length == 0 ? "未取得该类别记录" : string.Join("、", p.Sources))}</td></tr>"));
        string engineSection = report.GpuEngineProfiles.Count > 0 ? $"<section class='trend-section'><h2>GPU 分引擎历史</h2><p>仅统计已暴露节点；分引擎记录规则 {E(GpuEngineEvidence.Version)}。已观察 P95 按取得读数的时长加权，部分读数可能低估同类最大负载；类别完整时长不等于全GPU覆盖。时期对照仅使用同来源且完整的读数，可能与本表 P95 不同。来源或规则混合时不合并P95；峰值与重载时长保留为已观察线索。缺失保留未知，不同引擎占用不能相加。</p><div class='table-wrap'><table><thead><tr><th>类别</th><th>已观察时长</th><th>类别完整时长</th><th>已观察 P95</th><th>峰值</th><th>≥80% 分钟</th><th>原始传感器来源</th></tr></thead><tbody>{engineRows}</tbody></table></div></section>" : "";
        string liveEngineSection = live == null ? "" : $"<section><h2>导出时实时分引擎快照</h2><p class='muted'>{live.Sample.Time.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {E(live.GpuName)} · 规则 {E(string.IsNullOrWhiteSpace(live.Sample.GpuEngineVersion) ? "未记录" : live.Sample.GpuEngineVersion)}；此快照与所选历史范围分别展示。</p><p class='evidence'>{E(GpuEngineEvidence.Describe(live.Sample)).Replace("\n", "<br>")}</p></section>";
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
        <style>body{font:15px/1.8 system-ui,'Microsoft YaHei',sans-serif;background:#f4f7f8;color:#192a36;margin:0}main{max-width:900px;margin:50px auto;padding:24px}h1{font-size:32px}small{color:#007f73}section{background:white;border:1px solid #e3eaec;border-radius:12px;padding:26px;margin:18px 0}h2{font-size:20px}.muted{color:#677a87;font-size:13px}.evidence{padding:14px;background:#eef5f4}.table-wrap{overflow:auto}table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:10px;border-bottom:1px solid #e3eaec;white-space:nowrap}td.source{white-space:normal;overflow-wrap:anywhere;min-width:140px}.demand{border-bottom:1px solid #e3eaec;padding:12px 0;break-inside:avoid}.demand h3{font-size:15px}@media print{.trend-section{break-inside:auto}body{background:white}main{margin:0}section{break-inside:avoid} }</style>
        <main><small>PERFCOMPASS / 配置指南</small><h1>{{E(report.Summary)}}</h1><p>{{report.Generated.LocalDateTime:yyyy-MM-dd HH:mm}} · 最近 {{report.WindowDays}} 天 · {{report.SampleCount:N0}} 条记录</p>
        <p>有效使用 {{E(report.DurationLabel)}} / {{report.ActiveDays}} 天 · 证据状态：{{E(report.Level)}}</p><p>{{E(report.CoverageMessage)}}</p><p>{{E(live?.CpuName)}}<br>{{E(live?.GpuName)}}</p>
        <p>当前建议范围：{{E(report.SelectedScene ?? "全部场景")}} · 已记录场景：{{E(string.Join("、", report.Scenes))}}</p><p class="muted">核心/3D观测来源：{{E(report.GpuMetricScopes.Length == 0 ? "未取得" : string.Join("；", report.GpuMetricScopes))}}。旧版未标注来源的读数保留原口径；采集规则或来源变化时不推断需求增减。</p>{{samplingSection}}{{qualitySection}}{{requirementsSection}}{{cards}}{{trendSection}}{{experienceSection}}{{sceneTable}}{{engineSection}}{{liveEngineSection}}<section><h2>下一台电脑的新需求</h2><p>{{E(string.IsNullOrWhiteSpace(report.FutureNeeds) ? "尚未填写" : report.FutureNeeds).Replace("\n", "<br>")}}</p></section>
        <p class="muted">规则版本 {{E(report.MethodVersion)}}。P95 按有效采样时长加权，短时峰值另行保留。初步轻负载判断使用最近任务至少5分钟的有效读数，繁忙线索可提前显示。配置降档推断继续要求当前范围至少10小时、3天、典型任务确认及最近7天至少30分钟、2天的有效记录。观测阈值为透明启发式，不是统计置信度；候选是否满足要求按具体任务和测量证据分别判断。本报告由本地记录生成。</p></main></html>
        """;
    }
    public static void Csv(string path, IEnumerable<Sample> samples)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp,hardware_id,scene,active,seconds,cpu_percent,core_max_percent,memory_used_gib,memory_peak_gib,memory_total_gib,commit_gib,gpu_percent,vram_used_gib,vram_total_gib,disk_read_mib_per_second,disk_write_mib_per_second,disk_busy_percent,disk_used_gib,disk_total_gib,max_partition_percent,cpu_temp_c,gpu_temp_c,cpu_coverage_seconds,core_coverage_seconds,memory_coverage_seconds,commit_coverage_seconds,gpu_coverage_seconds,gpu_peak_percent,cpu_high_seconds,gpu_heavy_seconds,gpu_high_seconds,memory_high_seconds,sampling_issue,observation_version,gpu_engine_version,gpu_engines_json,gpu_metric_scope,gpu_metric_source");
        var jsonOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        static string Text(string? value, bool spreadsheetText = true)
        {
            value ??= "";
            if (spreadsheetText && value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        foreach (var s in samples)
        {
            string D(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
            string engines = s.GpuEngines == null ? "" : JsonSerializer.Serialize(s.GpuEngines, jsonOptions);
            writer.WriteLine(string.Join(",", s.Time.ToString("O"), Text(s.HardwareId), Text(s.Scene), s.Active ? "1" : "0", D(s.Seconds), D(s.Cpu), D(s.CoreMax), D(s.MemoryUsedGb), D(s.MemoryPeakGb), D(s.MemoryTotalGb), D(s.CommitGb), D(s.Gpu), D(s.VramUsedGb), D(s.VramTotalGb), D(s.DiskReadMbps), D(s.DiskWriteMbps), D(s.DiskBusy), D(s.DiskUsedGb), D(s.DiskTotalGb), D(s.MaxDiskPercent), D(s.CpuTemp), D(s.GpuTemp), D(s.CpuCoverage), D(s.CoreCoverage), D(s.MemoryCoverage), D(s.CommitCoverage), D(s.GpuCoverage), D(s.GpuPeak), D(s.CpuHighSeconds), D(s.GpuHeavySeconds), D(s.GpuHighSeconds), D(s.MemoryHighSeconds), SamplingClock.KnownIssue(s.SamplingIssue) ? s.SamplingIssue : "", ActivityRules.KnownVersion(s.ObservationVersion) ? s.ObservationVersion : "", Text(s.GpuEngineVersion), Text(engines, spreadsheetText: false), "core_or_3d", Text(s.GpuMetricSource)));
        }
    }
}
