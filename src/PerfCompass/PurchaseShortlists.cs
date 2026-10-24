namespace PerfCompass;

public record PurchaseOption(string CandidateId, string Name, string Label, string Detail);
public record PurchaseShortlist(string Scope, string Summary, string Notice, PurchaseOption[] Options);

public static partial class CandidateComparison
{
    private static PurchaseShortlist[] BuildShortlists(PurchaseWorkspace workspace, List<PurchaseAssessment> assessments, PurchaseConflict[] conflicts)
    {
        var groups = new[] { (Key: "", Scope: "必需条件", Goals: workspace.Criteria.Where(c => string.IsNullOrWhiteSpace(c.Condition)).ToArray()) }
            .Concat(workspace.Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Condition)).GroupBy(ScenarioKey)
                .Select(g => (Key: g.Key, Scope: "条件成立时：" + ScenarioLabel(g), Goals: workspace.Criteria.Where(c => string.IsNullOrWhiteSpace(c.Condition)).Concat(g).ToArray())));
        var results = new List<PurchaseShortlist>();
        foreach (var group in groups)
        {
            const string notice = "只比较本组已核对目标。报价来源由用户提供；规格通过、手填任务数值与原始重复耗时分别使用。没有记录到任务收益不等于整机过剩，其他条件同时触发时需一起核对。";
            bool conflict = conflicts.Any(c => c.Condition.Length == 0 || c.ScenarioKey == group.Key);
            var eligible = assessments.Where(a => !conflict && a.State == "pass"
                && (group.Key.Length == 0 || a.Checks.Where(c => c.ScenarioKey == group.Key).All(c => c.State == "pass")))
                .Select(a => workspace.Candidates.Single(c => c.Id == a.Id)).Where(HasQuoteEvidence)
                .OrderBy(c => c.PriceCny).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
            if (eligible.Length == 0)
            {
                results.Add(new(group.Scope, conflict ? "先处理条件冲突" : "补齐目标与报价后给出购机顺序", notice,
                    [new("", "本组暂无可排序候选", "待补依据", conflict ? "查看冲突条件；不计算本组节省金额。" : "候选需通过本组目标并填写整机报价及来源；其他独立目标的通过结果仍保留。")]));
                continue;
            }
            var cheapest = eligible.Where(c => c.PriceCny == eligible[0].PriceCny).ToArray();
            var tasks = group.Goals.Where(c => c.Metric == "task").ToArray();
            var options = new List<PurchaseOption>();
            foreach (var candidate in eligible)
            {
                if (candidate.PriceCny == cheapest[0].PriceCny)
                {
                    options.Add(new(candidate.Id, candidate.Name, "预算优先", $"报价 {candidate.PriceCny:0.##} 元；本组条件通过。"
                        + (eligible.Length == 1 ? "本组只有一台报价依据齐全的候选，尚无价差可比较。" : cheapest.Length > 1
                            ? "同价方案并列保留，不以名称或录入顺序决定优劣。" : "本组最低已核对报价，其他候选的加价取舍见下方。")));
                    continue;
                }
                var comparisons = cheapest.Select(baseline => CompareTasks(baseline, candidate, tasks, workspace)).ToArray();
                int dominated = Array.FindIndex(comparisons, c => tasks.Length > 0 && c.Complete && c.Better == 0);
                int chosen = dominated >= 0 ? dominated : 0;
                var compared = comparisons[chosen]; var baseline = cheapest[chosen];
                string label = tasks.Length == 0 ? "尚无任务收益对照" : dominated >= 0 ? "已记录任务未见加价优势"
                    : !compared.Complete ? "加价收益待核对" : compared.Worse > 0 ? "加价有任务取舍" : "记录到任务优势";
                string detail = $"比 {baseline.Name} 多花 {candidate.PriceCny - baseline.PriceCny:0.##} 元。"
                    + (tasks.Length == 0 ? "本组没有任务性能目标；更高容量或型号不自动算作实际收益。" : string.Join("；", compared.Details) + "。")
                    + (dominated >= 0 ? $"按本组已记录的任务结果，可优先考虑 {baseline.Name}；额外规格的其他用途仍需确认。" : "是否值得加价取决于这些差异对你的任务是否有用。");
                options.Add(new(candidate.Id, candidate.Name, label, detail));
            }
            results.Add(new(group.Scope, "预算优先：" + string.Join("、", cheapest.Select(c => c.Name)) + $"（{cheapest[0].PriceCny:0.##} 元）", notice, options.ToArray()));
        }
        return results.ToArray();
    }

    private record TaskContrast(bool Complete, int Better, int Worse, string[] Details);
    private static TaskContrast CompareTasks(PurchaseCandidate baseline, PurchaseCandidate candidate, PurchaseCriterion[] goals, PurchaseWorkspace workspace)
    {
        int better = 0, worse = 0; bool complete = true; var details = new List<string>();
        foreach (var goal in goals)
        {
            var a = workspace.Observations.SingleOrDefault(o => o.CandidateId == baseline.Id && o.CriterionId == goal.Id);
            var b = workspace.Observations.SingleOrDefault(o => o.CandidateId == candidate.Id && o.CriterionId == goal.Id);
            bool Valid(PurchaseObservation? o, PurchaseCandidate pc) => o?.Value != null && o.Comparable
                && o.ProofStamp == ObservationStamp(pc, goal) && !string.IsNullOrWhiteSpace(o.Evidence)
                && goal.Reviewed && goal.ReviewStamp == CriterionStamp(goal) && !string.IsNullOrWhiteSpace(goal.Unit) && !string.IsNullOrWhiteSpace(goal.Procedure)
                && (o.Measurement == null ? goal.MeasurementProtocol.Length == 0 : TaskMeasurements.Assess(o.Measurement, goal, o.Value).State != "unknown");
            if (!Valid(a, baseline) || !Valid(b, candidate) || (a!.Measurement == null) != (b!.Measurement == null))
            { complete = false; details.Add(goal.Name + "：缺少同类且仍有效的任务依据"); continue; }
            bool lower = goal.Direction is "max" or "lt";
            string evidence;
            int sign;
            if (a.Measurement is { } am && b.Measurement is { } bm)
            {
                if (am.ProtocolFingerprint != bm.ProtocolFingerprint)
                { complete = false; details.Add(goal.Name + "：重复测量协议不同"); continue; }
                double amin = am.Attempts.Where(r => !r.Warmup).Min(r => r.Seconds), amax = am.Attempts.Where(r => !r.Warmup).Max(r => r.Seconds);
                double bmin = bm.Attempts.Where(r => !r.Warmup).Min(r => r.Seconds), bmax = bm.Attempts.Where(r => !r.Warmup).Max(r => r.Seconds);
                evidence = $"正式耗时范围 {amin:0.###}–{amax:0.###} → {bmin:0.###}–{bmax:0.###} 秒";
                if (amin == amax && bmin == bmax && amin == bmin) sign = 0;
                else if (bmax < amin) sign = lower ? 1 : -1;
                else if (bmin > amax) sign = lower ? -1 : 1;
                else { complete = false; details.Add(goal.Name + "：" + evidence + "，区间重叠，收益待核对"); continue; }
            }
            else
            {
                evidence = $"手填任务值 {a.Value:0.###} → {b.Value:0.###} {goal.Unit}";
                sign = b.Value == a.Value ? 0 : (b.Value < a.Value) == lower ? 1 : -1;
            }
            if (sign > 0) better++; if (sign < 0) worse++;
            details.Add(goal.Name + "：" + evidence + (sign > 0 ? "，数值更好" : sign < 0 ? "，数值更差" : "，数值相同"));
        }
        return new(complete, better, worse, details.ToArray());
    }
}
