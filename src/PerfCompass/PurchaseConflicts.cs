namespace PerfCompass;

public static partial class CandidateComparison
{
    private static PurchaseConflict[] FindConflicts(List<PurchaseCriterion> criteria)
    {
        // Only shared specification fields have an unambiguous identity. Two task goals
        // expressed in seconds can describe different workloads; do not merge them by unit.
        var numeric = criteria.Where(c => c.Target.HasValue && c.Metric is "price" or "memory" or "vram" or "storage").ToArray();
        var conflicts = new List<PurchaseConflict>();
        foreach (string key in new[] { "" }.Concat(numeric.Select(ScenarioKey).Where(c => c.Length > 0).Distinct().Order()))
        {
            string condition = key.Length == 0 ? "" : ScenarioLabel(numeric.Where(c => ScenarioKey(c) == key));
            foreach (var group in numeric.Where(c => ScenarioKey(c).Length == 0 || ScenarioKey(c) == key).GroupBy(c => c.Metric).OrderBy(g => g.Key))
            {
                // A base conflict already explains why every scenario is unavailable.
                if (condition.Length > 0 && conflicts.Any(c => c.Condition.Length == 0 && c.Metric == group.Key)) continue;
                var lower = group.Where(c => c.Direction is "min" or "gt").OrderByDescending(c => c.Target).ThenByDescending(c => c.Direction == "gt").ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
                var upper = group.Where(c => c.Direction is "max" or "lt").OrderBy(c => c.Target).ThenByDescending(c => c.Direction == "lt").ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();
                double floor = lower?.Target ?? 0;
                if (upper == null || floor < upper.Target || floor == upper.Target && lower?.Direction != "gt" && upper.Direction != "lt") continue;
                string Bound(PurchaseCriterion c) => $"「{c.Name}」：{DirectionLabel(c.Direction)} {c.Target:G} {c.Unit}";
                string scope = condition.Length == 0 ? "必需条件" : "情景「" + condition + "」成立时";
                string metric = Metrics.Single(m => m.Key == group.Key).Label;
                conflicts.Add(new(group.Key, condition, lower == null ? [upper.Id] : new[] { lower.Id, upper.Id },
                    scope + "的" + metric + "没有共同可取值。" + (lower == null ? "规格与报价不能为负数" : Bound(lower)) + "；" + Bound(upper) + "。请核对这两项是否同时适用。") { ScenarioKey = key });
            }
        }
        return conflicts.ToArray();
    }
}
