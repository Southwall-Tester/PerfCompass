using System.Text.Json;
using System.Windows;

namespace PerfCompass;

public partial class ComparisonView
{
    internal Func<TaskMeasurementResult?> CurrentTaskMeasurement = () => null;
    private record MeasurementHandoff(PurchaseObservation Observation, PurchaseCriterion Criterion,
        TaskMeasurementResult Result, string TargetStamp);
    private MeasurementHandoff? pendingMeasurementHandoff;

    private void ClearMeasurementHandoff()
    {
        pendingMeasurementHandoff = null;
        MeasurementHandoffPanel.Visibility = Visibility.Collapsed;
        MeasurementHandoffTarget.Text = MeasurementHandoffSource.Text = MeasurementHandoffEvidence.Text = "";
    }
    private string HandoffTargetStamp(PurchaseObservation observation, PurchaseCriterion criterion)
        => JsonSerializer.Serialize(new
        {
            Candidate = workspace.Candidates.SingleOrDefault(c => c.Id == observation.CandidateId),
            Criterion = CandidateComparison.CriterionStamp(criterion),
            observation.CandidateId, observation.CriterionId,
            observation.Value, observation.Verdict, observation.Evidence, observation.Measurement,
            observation.Comparable, observation.ProofStamp,
            Scope = context == null ? "" : CandidateComparison.Stamp(context)
        });

    private bool CanUseMeasurement(PurchaseCriterion criterion, TaskMeasurementResult result)
    {
        if (!TaskMeasurements.HasCompleteRoundsAndStablePower(result))
        { ComparisonStatus.Text = "测量未完整成功，或电源状态改变/未知；请检查后重新测量。"; return false; }
        if (criterion.MeasurementProtocol.Length > 0 && criterion.MeasurementProtocol != result.ProtocolFingerprint)
        { ComparisonStatus.Text = "程序、输入或测试协议不同，不能并入同一目标。请新建目标保留不同协议的测量。"; return false; }
        return true;
    }
    private void ApplyTaskMeasurement(PurchaseObservation observation, PurchaseCriterion criterion, TaskMeasurementResult result)
    {
        // Detach the mutable round list from the measurement page and its preview.
        result = result with { Attempts = [.. result.Attempts] };
        criterion.MeasurementProtocol = result.ProtocolFingerprint; criterion.Reviewed = false;
        observation.Measurement = result; observation.Value = result.MedianSeconds * (criterion.Unit is "毫秒" or "ms" ? 1000 : 1);
        observation.Evidence = TaskMeasurements.Evidence(result); observation.Comparable = false; observation.ProofStamp = "";
        Invalidate(); Bind();
        ComparisonStatus.Text = $"已导入 {result.Recipe.Name}。来源机器：{result.Machine.Cpu} / {result.Machine.Gpu}。请核对它对应所选候选、任务步骤与实际产物，重新确认目标及可比性。";
        if (result.Version == TaskMeasurements.LegacyVersion) ComparisonStatus.Text += " 旧测量仅作记录，不能作为当前重复耗时达标依据；请到任务测量页导入该文件复用方案，并重新测量。";
    }
    private void UseCurrentMeasurement_Click(object sender, RoutedEventArgs e)
    {
        ClearMeasurementHandoff();
        if (contextPending) { ComparisonStatus.Text = "请等待当前分析完成后，再核对本次测量的目标。"; return; }
        if (!Commit() || ObservationsGrid.SelectedItem is not PurchaseObservation observation)
        { ComparisonStatus.Text = "先在结果表中选择要填入的候选与任务行。"; return; }
        var criterion = workspace.Criteria.Single(c => c.Id == observation.CriterionId);
        if (criterion.Metric != "task" || criterion.Unit is not ("秒" or "s" or "seconds" or "毫秒" or "ms"))
        { ComparisonStatus.Text = "所选行需为实际任务测量，单位设为秒/s 或毫秒/ms。"; return; }
        try
        {
            var result = CurrentTaskMeasurement();
            if (result == null) { ComparisonStatus.Text = "当前没有已结束的测量。可先运行任务测量，或导入已有测量文件。"; return; }
            if (!CanUseMeasurement(criterion, result)) return;
            var candidate = workspace.Candidates.Single(c => c.Id == observation.CandidateId);
            result = result with { Attempts = [.. result.Attempts] };
            pendingMeasurementHandoff = new(observation, criterion, result, HandoffTargetStamp(observation, criterion));
            MeasurementHandoffTarget.Text = $"填入候选：{candidate.Name}\n目标：{criterion.Name} · {CandidateComparison.DirectionLabel(criterion.Direction)} {criterion.Target?.ToString("G") ?? "待补数值"} {criterion.Unit}"
                + (criterion.Condition.Length == 0 ? "" : "\n触发条件：" + criterion.Condition);
            MeasurementHandoffSource.Text = $"测量任务：{result.Recipe.Name} · {result.Started.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n来源 CPU：{result.Machine.Cpu}\n来源 GPU：{result.Machine.Gpu}\n来源内存：{result.Machine.MemoryGiB?.ToString("0.##") ?? "未知"} GiB\n{result.Summary}";
            MeasurementHandoffEvidence.Text = $"目标步骤：{criterion.Procedure}\n测量步骤：{result.Recipe.Procedure}\n{TaskMeasurements.Evidence(result)}";
            MeasurementHandoffPanel.Visibility = Visibility.Visible;
            ComparisonStatus.Text = "测量尚未填入，请先核对上方来源机器与目标。已有结果保持原样。";
            MeasurementHandoffPanel.BringIntoView();
        }
        catch (ArgumentException ex) { ComparisonStatus.Text = ex.Message; }
    }
    private void ConfirmMeasurementHandoff_Click(object sender, RoutedEventArgs e)
    {
        var pending = pendingMeasurementHandoff;
        if (pending == null || !Commit()) return;
        if (pendingMeasurementHandoff != pending || contextPending
            || !ReferenceEquals(ObservationsGrid.SelectedItem, pending.Observation)
            || !workspace.Observations.Contains(pending.Observation) || !workspace.Criteria.Contains(pending.Criterion)
            || HandoffTargetStamp(pending.Observation, pending.Criterion) != pending.TargetStamp)
        { ClearMeasurementHandoff(); ComparisonStatus.Text = "候选、目标或分析范围已变化，请重新选择并核对本次测量。"; return; }
        if (!CanUseMeasurement(pending.Criterion, pending.Result)) { ClearMeasurementHandoff(); return; }
        ApplyTaskMeasurement(pending.Observation, pending.Criterion, pending.Result);
    }
    private void CancelMeasurementHandoff_Click(object sender, RoutedEventArgs e)
    { ClearMeasurementHandoff(); ComparisonStatus.Text = "已取消填入，原结果和测量文件保持原样。"; }
}
