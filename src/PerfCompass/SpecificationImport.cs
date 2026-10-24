using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfCompass;

public sealed class SpecificationDraft
{
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Vram { get; set; } = "";
    public string Storage { get; set; } = "";
    public string ParsedStamp { get; set; } = "";
    public DateTimeOffset? RecordedAt { get; set; }
}
public record SpecificationPreview(string Name, string Cpu, string Gpu, double? MemoryGb, double? VramGb, double? StorageGb, string[] Questions);

public static class SpecificationImport
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Model"]="name", ["Model Code"]="name", ["SKU"]="name", ["型号"]="name", ["型号编号"]="name",
        ["CPU"]="cpu", ["Processor"]="cpu", ["处理器"]="cpu", ["处理器型号"]="cpu",
        ["GPU"]="gpu", ["Graphics"]="gpu", ["Graphics Card"]="gpu", ["显卡"]="gpu", ["显卡型号"]="gpu",
        ["Memory"]="memory", ["RAM"]="memory", ["Installed Memory"]="memory", ["内存"]="memory", ["已安装内存"]="memory",
        ["VRAM"]="vram", ["Video Memory"]="vram", ["Graphics Memory"]="vram", ["显存"]="vram", ["独立显存"]="vram",
        ["Storage"]="storage", ["SSD"]="storage", ["Installed Storage"]="storage", ["硬盘"]="storage", ["存储"]="storage", ["固态硬盘"]="storage"
    };
    private static readonly Regex Capacity = new(@"(?<![\d.,])(?<size>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>[Gg][Ii]?B|[Tt][Ii]?B)(?=$|[\s,;:/()（）+x×*]|(?i:DDR|LPDDR|GDDR|SSD|SODIMM|NVMe))", RegexOptions.CultureInvariant);
    public static string Stamp(string source, string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { source, text }))));
    public static bool Current(SpecificationDraft draft) => draft.RecordedAt.HasValue && draft.ParsedStamp == Stamp(draft.Source, draft.Text);
    public static SpecificationPreview Parse(string text)
    {
        if (text == null || text.Length is < 1 or > 8000) throw new ArgumentException("规格摘录应为 1–8000 字符。");
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        void Add(string key, string value)
        {
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var row = Regex.Match(lines[i], @"^([^:：|\t]+)\s*[:：|\t]\s*(.+)$");
            if (row.Success && Labels.TryGetValue(row.Groups[1].Value.Trim(), out var key)) Add(key, row.Groups[2].Value.Trim());
            else if (Labels.TryGetValue(lines[i], out key) && i + 1 < lines.Length && !Labels.ContainsKey(lines[i + 1])
                && !Regex.IsMatch(lines[i + 1], @"^[^:：|\t]+\s*[:：|\t]")) Add(key, lines[++i]);
        }
        var questions = new List<string>();
        string Field(string key)
        {
            if (!values.TryGetValue(key, out var list)) return "";
            if (list.Count != 1) { questions.Add($"{key} 有多条不同记录，请选定具体配置。"); return ""; }
            return list[0];
        }
        string Definite(string key)
        {
            string v = Field(key);
            if (Regex.IsMatch(v, @"\bor\b|可选|或", RegexOptions.IgnoreCase)) { questions.Add(key + " 含可选配置，请核对具体型号。"); return ""; }
            return v;
        }
        string name = Definite("name"), cpu = Definite("cpu"), gpu = Definite("gpu");
        double? Quantity(string key)
        {
            string value = Field(key);
            if (value.Length == 0) return null;
            var result = ParseCapacity(value);
            if (!result.HasValue) questions.Add(key + " 的容量或配置不明确，已保留原文，请手工核对。");
            return result;
        }
        double? memory = Quantity("memory"), vram = Quantity("vram"), storage = Quantity("storage");
        // Only an explicit GDDR capacity supplies an additional VRAM proposal.
        // Shared/system memory in a graphics description is not dedicated VRAM.
        if (!values.ContainsKey("vram") && Regex.IsMatch(gpu, @"\bGDDR\d", RegexOptions.IgnoreCase))
            vram = ParseCapacity(gpu);
        if (name.Length > 150) { name = ""; questions.Add("型号字段过长，请填写具体型号或 SKU。"); }
        if (cpu.Length > 200) { cpu = ""; questions.Add("处理器字段过长，请核对具体配置。"); }
        if (gpu.Length > 200) { gpu = ""; questions.Add("显卡字段过长，请核对具体配置。"); }
        return new(name, cpu, gpu, memory, vram, storage, questions.ToArray());
    }
    public static double? ParseCapacity(string value)
    {
        if (Regex.IsMatch(value, @"up\s+to|max(?:imum)?|optional|shared|最多|最高|可选|支持|共享|至少|不超过|[<>≤≥]|\d,\d|\d[eE][+-]?\d", RegexOptions.IgnoreCase)) return null;
        var matches = Capacity.Matches(value).Where(m => !Regex.IsMatch(value[(m.Index + m.Length)..], @"^\s*/\s*(?:seconds?|secs?|s|秒)(?:\b|$)", RegexOptions.IgnoreCase)).ToArray();
        if (matches.Length != 1) return null;
        var m = matches[0]; double size = double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
        double scale = m.Groups["unit"].Value.ToUpperInvariant() switch { "GB" => 1, "TB" => 1000, "GIB" => 1073741824d / 1e9, "TIB" => 1099511627776d / 1e9, _ => double.NaN };
        string prefix = value[..m.Index], suffix = value[(m.Index + m.Length)..];
        if (Regex.IsMatch(prefix, @"[-+]\s*$")) return null;
        var before = Regex.Match(prefix, @"(?<n>[0-9]+)\s*[x×*]\s*$");
        var after = Regex.Match(suffix, @"^\s*[x×*]\s*(?<n>[0-9]+)\b");
        if (before.Success && after.Success) return null;
        double count = before.Success ? double.Parse(before.Groups["n"].Value, CultureInfo.InvariantCulture) : after.Success ? double.Parse(after.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
        double total = size * scale * count;
        return count > 0 && double.IsFinite(total) && total is >= 0 and <= 1e12 ? total : null;
    }
    public static PurchaseCandidate Candidate(SpecificationDraft draft)
    {
        if (!Current(draft)) throw new ArgumentException("原文或来源已修改，请重新生成规格预览。");
        if (string.IsNullOrWhiteSpace(draft.Source) || string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("请填写资料来源和具体候选名称。");
        double? Number(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!double.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n is < 0 or > 1e12)
                throw new ArgumentException("容量请输入非负 GB 数值；不明确的容量可留空。");
            return n;
        }
        var candidate = new PurchaseCandidate { Name = draft.Name.Trim(), Cpu = draft.Cpu.Trim(), Gpu = draft.Gpu.Trim(),
            MemoryGb = Number(draft.Memory), VramGb = Number(draft.Vram), StorageGb = Number(draft.Storage), Source = "" };
        candidate.SpecificationSource = $"规格摘录（用户核对字段，未经联网认证）\n来源：{draft.Source}\n本机摘录时间：{draft.RecordedAt:O}\n核对字段：{candidate.Name}；CPU {candidate.Cpu}；GPU {candidate.Gpu}；内存 {candidate.MemoryGb} GB；独立显存 {candidate.VramGb} GB；存储 {candidate.StorageGb} GB\n原文：\n{draft.Text}";
        CandidateComparison.Validate(new PurchaseWorkspace { Candidates = [candidate] });
        return candidate;
    }
}
