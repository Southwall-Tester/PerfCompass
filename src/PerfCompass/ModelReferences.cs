namespace PerfCompass;

public record ModelReference(string Name, string Configuration, string Reason, string Source, string CheckedOn);
public record ModelReferenceList(ModelReference[] Items, string Notice);

// A small, dated editorial list. No storefront requests, prices, inventory or candidate imports.
public static class ModelReferences
{
    private record Entry(string Name, string Configuration, double MemoryGB, double StorageGB,
        double VramGB, string Source);
    private const string CheckedOn = "2026-10-25";
    private static readonly Entry[] Catalog =
    [
        new("Dell 14S DS14260", "笔记本 · Core Ultra 5 322 / 16 GB / 1 TB SSD / 集成显卡", 16, 1000, 0,
            "https://www.dell.com/zh-cn/shop/laptop-computers/spd/dell14sds14260/ds14260_reg_02"),
        new("联想小新 Pro 16", "笔记本 · Core Ultra 7 255H / 32 GB / 1 TB SSD / 集成显卡", 32, 1000, 0,
            "https://item.lenovo.com.cn/product/1044729.html"),
        new("联想拯救者 Y7000P 2025", "笔记本 · Core i9-14900HX / 32 GB / 1 TB SSD / RTX 5060 8 GB", 32, 1000, 8,
            "https://item.lenovo.com.cn/product/1048408.html"),
        new("联想拯救者 Y9000P 2026 至尊版", "笔记本 · Core Ultra 9 290HX Plus / 32 GB / 1 TB SSD / RTX 5070 Ti 12 GB", 32, 1000, 12,
            "https://m.lenovo.com.cn/wiki/product-1055960.html")
    ];

    internal static ModelReferenceList Build(AnalysisReport report, ConfigurationScenario scenario, string gpuMode)
    {
        if (scenario.Conflicts.Length > 0)
            return new([], "本情景的容量条件存在冲突，修正后再列接近的参考机型。");
        var goals = scenario.Criteria.Where(c => c.Metric is "memory" or "storage" or "vram"
            && c.Target.HasValue && double.IsFinite(c.Target.Value) && c.Target >= 0).ToArray();
        bool nominalMatch(Entry e) => goals.All(c => CandidateComparison.Meets(
            c.Metric == "memory" ? e.MemoryGB : c.Metric == "storage" ? e.StorageGB : e.VramGB,
            c.Target!.Value, c.Direction));
        bool discrete = goals.Any(c => c.Metric == "vram" && (c.Direction == "gt" || c.Direction == "min" && c.Target > 0));
        bool known = !report.HistoryQuality.HasIssues && !report.ExperienceQuality.HasIssues
            && !string.IsNullOrWhiteSpace(report.HardwareId) && report.SemanticUseNotice.Length == 0;
        var memory = known ? MemoryCapacityPreview.From(report)?.Evidence : null;
        // This is only an exclusion by byte count, never a promise of usable capacity.
        double? physicalPeakGB = memory?.PeakGiB * (1073741824d / 1_000_000_000);
        // A GPU-heavy observation starts with discrete examples, without claiming an equivalent tier.
        bool routeKnown = known && gpuMode is "light" or "workload";
        bool preferDiscrete = discrete || routeKnown && gpuMode == "workload";
        var eligible = Catalog.Where(nominalMatch).Where(e => !physicalPeakGB.HasValue || e.MemoryGB >= physicalPeakGB);
        if (discrete || routeKnown) eligible = eligible.Where(e => (e.VramGB > 0) == preferDiscrete);
        var items = eligible.Take(3).Select(e => new ModelReference(e.Name, e.Configuration,
            (discrete ? "独立显存规格接近已确认目标。" : routeKnown
                ? preferDiscrete ? "独显路线的参考，具体性能档位仍按任务选择。" : "核显路线的参考，可先比较这类整机。"
                : "路线尚未确定，供了解不同类型的配置。")
            + (goals.Length > 0 ? "此参考配置符合本情景已确认的标称容量边界。" : ""), e.Source, CheckedOn)).ToArray();
        return new(items, (items.Length == 0 ? "小型参考库暂没有接近这些边界的型号，可按上方配置方向查找其他产品。" : "")
            + "资料核对于 " + CheckedOn + "，仅含部分笔记本，无实时报价和库存。同系列配置不同；"
            + "路线、标称容量接近不表示性能等同。可用内存与新需求仍按上方依据选择，任务表现尚未验证。");
    }
}
