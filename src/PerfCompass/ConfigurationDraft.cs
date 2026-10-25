namespace PerfCompass;

public record CpuDemandEvidence(double? TotalP95, double? CoreP95, double? TotalPeak, double? CorePeak,
    double HighSeconds, double ObservedHours, double Coverage);
internal record ConfigurationScenario(string Key, string Label, PurchaseCriterion[] Criteria, PurchaseConflict[] Conflicts);
public record ConfigurationDraftRow(string Key, string Name, string Recommendation, string Spending, string Evidence, string Scope)
{
    public double? NominalCapacityGB { get; init; }
    public double? AvailableMemoryGiB { get; init; }
}
public record ConfigurationDraftCase(string Label, ConfigurationDraftRow[] Rows, string Notice)
{
    public string Source { get; init; } = "";
}
public record ConfigurationDraft(ConfigurationDraftCase Baseline, ConfigurationDraftCase[] Conditions, string Scope)
{
    public const string Notice = "这是优先比较的配置草案，未验证候选电脑的任务表现。内存可用容量与商品标称容量分别核对；已确认的新需求及未处理体验优先。";
    private const double GiBToGB = 1073741824d / 1_000_000_000;
    private static readonly double[] MemoryTiers = [8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024];
    private static readonly double[] StorageTiers = [256, 512, 1000, 2000, 4000, 8000, 16000, 32000, 64000];

    public static ConfigurationDraft Build(AnalysisReport report)
    {
        var decisions = PurchaseGuidance.Build(report);
        var memory = MemoryCapacityPreview.From(report)?.Evidence;
        var storage = StorageCapacityPreview.From(report)?.Evidence;
        var gpu = GpuPurchasePlan.Build(report);
        string scope = $"所选 {report.WindowDays} 天 · {report.SelectedScene ?? "全部场景"}";
        ConfigurationDraftCase BuildCase(ConfigurationScenario scenario)
        {
            var rows = new[] { Cpu(), Memory(scenario), Gpu(scenario), Storage(scenario) };
            string notice = string.Join(" ", scenario.Conflicts.Select(c => c.Message));
            return new(scenario.Label, rows, notice) { Source = scenario.Key.Length == 0 ? ""
                : "条件原文：" + string.Join("；", scenario.Criteria.Where(c => c.Condition.Length > 0).Select(c => c.Evidence).Distinct()) };
        }
        var cases = CandidateComparison.ConfigurationScenarios(report).Select(BuildCase).ToArray();
        return new(cases[0], cases.Skip(1).ToArray(), scope);

        ConfigurationDraftRow Fallback(string key)
        {
            var d = decisions.Single(d => d.Key == key);
            return new(key, d.Name, d.Direction, d.NextStep, d.Reason, d.Scope);
        }
        bool Protected(string key) => report.SemanticUseNotice.Length > 0 || report.ExperienceQuality.HasIssues
            || report.Cards.FirstOrDefault(c => c.Key == key)?.Status is "requirements" or "constraints" or "meaning" or "experience" or "experience-data" or "changed" or "pressure";
        ConfigurationDraftRow Cpu()
        {
            var card = report.Cards.FirstOrDefault(c => c.Key == "cpu");
            var observation = card?.Observation;
            var data = observation?.CpuDemand;
            if (Protected("cpu") || report.HistoryQuality.HasIssues || string.IsNullOrWhiteSpace(report.HardwareId)
                || card?.RetainedWorkload != null || data == null || data.ObservedHours < 300d / 3600 || data.Coverage < .95
                || !Percent(data.TotalP95) || !Percent(data.CoreP95) || !double.IsFinite(data.HighSeconds) || data.HighSeconds < 0) return Fallback("cpu");
            bool parallel = data.TotalP95 >= 80 || data.HighSeconds > 0;
            bool responseFirst = data.TotalP95 < 50 && !parallel;
            string recommendation = parallel ? "以当前多核任务表现为基准比较处理器"
                : responseFirst ? "单核响应优先，先比较主流处理器" : "兼顾单核响应与多核吞吐，按任务比较处理器";
            string spending = parallel ? "先用编译、渲染或其他实际并行任务比较耗时，再决定增加核心预算。"
                : responseFirst ? "当前没有足以单凭负载要求增加核心数的依据；较少核心、单核表现相近的候选值得优先比较。"
                : "整机已有明显负载，先比较实际任务耗时；核心数和处理器型号档位不能单独决定升级收益。";
            string evidence = $"CPU P95 {data.TotalP95:0.0}%；最忙逻辑核心 P95 {data.CoreP95:0.0}%、峰值 {data.CorePeak:0.0}%；整机 CPU ≥90% 累计 {data.HighSeconds:0.0} 秒。";
            return new("cpu", "处理器", recommendation, spending, evidence, observation!.Scope);
        }
        ConfigurationDraftRow Memory(ConfigurationScenario scenario)
        {
            var goals = Goals(scenario, "memory");
            var conflict = Conflict(scenario, "memory");
            if (conflict != null) return conflict;
            // Confirmed nominal specifications and measured usable RAM are separate
            // fields. Do not infer a product's usable RAM from its GB label.
            bool light = report.Cards.FirstOrDefault(c => c.Key == "memory")?.Observation?.State == "light";
            double? available = memory == null ? null : light && !Protected("memory")
                ? MemoryTiers.Cast<double?>().FirstOrDefault(n => n >= memory.PeakGiB) ?? memory.PeakGiB
                : memory.MaximumObservedCapacityGiB;
            string observed = memory == null ? "尚无可用于容量草案的完整物理内存观测。"
                : $"物理峰值 {memory.PeakGiB:0.0} GiB；当前可用容量 {memory.MinimumObservedCapacityGiB:0.0}–{memory.MaximumObservedCapacityGiB:0.0} GiB；提交量峰值 "
                    + (memory.CommitPeakGiB is double commit ? $"{commit:0.0} GiB。提交量不作必装容量。" : "未取得。");
            string measuredScope = memory == null ? scope : $"{memory.Scope} · {memory.FirstRecord.LocalDateTime:MM/dd HH:mm}–{memory.LastRecord.LocalDateTime:MM/dd HH:mm}";
            if (goals.Length > 0)
            {
                double? tier = FirstTier(MemoryTiers, goals, null);
                return new("memory", "内存", tier.HasValue ? $"商品内存先比较 {tier:0.##} GB 档" : "按已确认内存边界筛选商品",
                    Bounds(goals) + (available.HasValue ? $"；另保留约 {available:0.0} GiB 可用容量作为任务对照。" : "。") + "商品规格不能直接证明可用容量或任务达标。",
                    observed + Sources(goals), measuredScope) { NominalCapacityGB = tier, AvailableMemoryGiB = available };
            }
            if (available == null) return Fallback("memory");
            var fallback = Fallback("memory");
            double? roundedBaseline = MemoryTiers.Cast<double?>().FirstOrDefault(n => n >= available);
            double? next = MemoryTiers.Cast<double?>().FirstOrDefault(n => n > (roundedBaseline ?? available));
            if (report.Cards.FirstOrDefault(c => c.Key == "memory")?.Status == "pressure" && next.HasValue
                && memory!.PeakGiB >= memory.MaximumObservedCapacityGiB * .9)
                return new("memory", "内存", $"优先比较约 {next:0.##} GiB 可用内存",
                    $"已记录内存压力及接近容量的物理峰值；将更大容量与当前约 {available:0.0} GiB 的机器作任务对照，再判断卡顿改善与加价是否值得。",
                    observed + " " + fallback.Evidence, measuredScope) { AvailableMemoryGiB = next };
            if (Protected("memory")) return fallback with { Evidence = observed + " " + fallback.Evidence, AvailableMemoryGiB = available };
            return new("memory", "内存", light ? $"先比较约 {available:0.##} GiB 可用内存" : $"先保留约 {available:0.0} GiB 可用内存",
                (light ? "可比较较低容量；" : "以当前容量作基线；")
                    + (next.HasValue ? $"约 {next:0.##} GiB 可用容量作更大一档对照，" : "更大容量作对照，")
                    + "新增多任务、模型或数据规模再决定加钱。商品标称 GB 与这里的可用 GiB 分开核对。",
                observed, measuredScope) { AvailableMemoryGiB = available };
        }
        ConfigurationDraftRow Storage(ConfigurationScenario scenario)
        {
            var goals = Goals(scenario, "storage");
            var conflict = Conflict(scenario, "storage");
            if (conflict != null) return conflict;
            double? occupiedGB = storage?.OccupiedGiB * GiBToGB;
            var disk = report.Cards.FirstOrDefault(c => c.Key == "disk");
            // The disk card's pressure state is occupied space >=90%, not an
            // I/O performance signal. Compare the next capacity when preserving it.
            double? preference = disk?.Status == "pressure" && storage != null
                ? Math.Max(occupiedGB!.Value, storage.ObservedTotalGiB * GiBToGB + 1) : occupiedGB;
            double? first = FirstTier(StorageTiers, goals, preference);
            if (first == null && goals.Length == 0) return Fallback("disk");
            string measured = storage == null ? "没有已核对的固定卷合计占用观测。" : $"已取得固定卷合计占用 {storage.OccupiedGiB:0.0} GiB（约 {occupiedGB:0.0} GB）。";
            bool below = first.HasValue && occupiedGB.HasValue && first < occupiedGB;
            string spending = below ? "该档低于当前合计占用，需要释放或外迁文件；这是容量取舍，不能按原样全量迁移。"
                : "按保留这些卷的合计占用比较；另留系统预留与增长空间。低吞吐不构成购买低速 SSD 的依据。";
            if (Protected("disk") && goals.Length == 0) spending += " " + Fallback("disk").Spending;
            double? next = StorageTiers.Cast<double?>().FirstOrDefault(n => n > first && Fits(n.Value, goals));
            if (next.HasValue) spending += $" {StorageLabel(next.Value)} 作增长容量对照，是否加钱取决于要保留和新增的文件。";
            return new("disk", "存储", first.HasValue ? $"SSD 容量先比较 {StorageLabel(first.Value)}" : "按已确认存储边界筛选商品",
                (goals.Length > 0 ? Bounds(goals) + "。" : "") + spending, measured + Sources(goals), storage?.Scope ?? scope) { NominalCapacityGB = first };
        }
        ConfigurationDraftRow Gpu(ConfigurationScenario scenario)
        {
            var goals = Goals(scenario, "vram");
            var conflict = Conflict(scenario, "vram");
            if (conflict != null) return conflict with { Key = "gpu", Name = "显卡" };
            bool discrete = goals.Any(c => c.Target >= 0 && (c.Direction == "gt" || c.Direction == "min" && c.Target > 0));
            return new("gpu", "显卡", discrete ? "独显方案优先，按独立显存目标筛选" : gpu.Title,
                (goals.Length > 0 ? Bounds(goals) + "。" : "") + (discrete ? "核显共享内存不能充当已要求的独立显存；具体 GPU 性能仍按目标任务比较。" : gpu.NextStep),
                gpu.Evidence + Sources(goals), gpu.Scope);
        }
        ConfigurationDraftRow? Conflict(ConfigurationScenario scenario, string metric)
        {
            var conflict = scenario.Conflicts.FirstOrDefault(c => c.Metric == metric);
            return conflict == null ? null : new(metric, metric == "memory" ? "内存" : metric == "storage" ? "存储" : "独立显存",
                "容量条件冲突，先核对同时适用的边界", "更正条件后再生成该项档位；其他独立配置项仍可比较。", conflict.Message, scope);
        }
    }

    private static bool Percent(double? n) => n.HasValue && double.IsFinite(n.Value) && n >= 0 && n <= 100;
    private static PurchaseCriterion[] Goals(ConfigurationScenario scenario, string metric) => scenario.Criteria
        .Where(c => c.Metric == metric && c.Target.HasValue && double.IsFinite(c.Target.Value) && c.Target >= 0).ToArray();
    private static bool Fits(double value, PurchaseCriterion[] goals) => goals.All(c => CandidateComparison.Meets(value, c.Target!.Value, c.Direction));
    private static double? FirstTier(double[] tiers, PurchaseCriterion[] goals, double? preference)
    {
        var eligible = tiers.Where(n => Fits(n, goals)).ToArray();
        if (preference.HasValue)
            return eligible.Cast<double?>().FirstOrDefault(n => n >= preference) ?? eligible.Cast<double?>().LastOrDefault();
        return goals.Any(c => c.Direction is "min" or "gt" && c.Target > 0) ? eligible.Cast<double?>().FirstOrDefault() : null;
    }
    private static string Bounds(PurchaseCriterion[] goals) => string.Join("；", goals.Select(c => $"{CandidateComparison.DirectionLabel(c.Direction)} {c.Target:0.########} {c.Unit}").Distinct());
    private static string Sources(PurchaseCriterion[] goals) => goals.Length == 0 ? "" : " 本情景条件原文：" + string.Join("；", goals.Select(c => c.Evidence).Distinct());
    private static string StorageLabel(double gb) => gb >= 1000 ? $"{gb / 1000:0.##} TB" : $"{gb:0.##} GB";
}
