using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfCompass;

public record QuantityGoal(string Metric, string Direction, double Target, string Unit);

/// <summary>Compiles already-reviewed facts. This is a bounded quantity grammar, not an NLP model.</summary>
public static class QuantityGoals
{
    private const string Number = @"(?:\d+(?:\.\d+)?|[零〇一二两兩三四五六七八九十百千万萬亿億]+(?:点[零〇一二两三四五六七八九]+)?)";
    private const string Units = @"(?:TiB|GiB|MiB|TB|GB|MB|万元|人民币元|CNY|RMB|元|毫秒|微秒|分钟|小时|秒|ms|us|seconds?|minutes?|hours?|s|min|h|公斤|千克|克|kg|g|瓦特|瓦|W|dB\(A\)|dBA|dB|分贝|FPS|fps|帧/秒|帧|%)";
    private const string Prefixes = @"(?:不得低于|不能低于|不要低于|不可低于|别低于|不低于|不小于|不少于|至少|起码|>=|≥|不得超过|不能超过|不要超过|不可超过|不得大于|不能大于|不高于|不大于|不多于|不超过|别超过|至多|最多|<=|≤|大于|高于|多于|超过|>|小于|低于|少于|不到|<|等于|正好|恰好|=)";
    private const string Modal = @"(?:\s*(?:需要|要有|达到|要|需|有|为))?";
    private const string Suffixes = @"(?:及以上|及以下|以上|以下|以内|起步)";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static Match Match(string value, string expression) => Regex.Match(value, expression, Options, TimeSpan.FromMilliseconds(100));
    private static bool IsMatch(string value, string expression) => Match(value, expression).Success;
    private static string Clean(string value) => value.Normalize(NormalizationForm.FormKC).Trim();
    private sealed record Unit(string Family, string Canonical, double Scale);
    private static Unit? ReadUnit(string text)
    {
        string clean = Clean(text);
        if (IsMatch(clean, @"^(?:[MGT]i?B)$") && !new[] { "GB", "TB", "MB", "GiB", "TiB", "MiB" }.Contains(clean, StringComparer.Ordinal)) return null;
        return clean.ToLowerInvariant() switch
    {
        "gb" => new("capacity", "GB", 1), "tb" => new("capacity", "GB", 1000), "mb" => new("capacity", "GB", .001),
        "gib" => new("capacity", "GB", 1073741824d / 1e9), "mib" => new("capacity", "GB", 1048576d / 1e9), "tib" => new("capacity", "GB", 1099511627776d / 1e9),
        "元" or "人民币元" or "cny" or "rmb" => new("money", "元", 1), "万元" => new("money", "元", 10000),
        "秒" or "s" or "second" or "seconds" => new("duration", "秒", 1),
        "毫秒" or "ms" => new("duration", "秒", .001), "微秒" or "us" => new("duration", "秒", .000001),
        "分钟" or "min" or "minute" or "minutes" => new("duration", "秒", 60),
        "小时" or "h" or "hour" or "hours" => new("duration", "秒", 3600),
        "公斤" or "千克" or "kg" => new("mass", "kg", 1), "克" or "g" => new("mass", "kg", .001),
        "瓦特" or "瓦" or "w" => new("power", "W", 1),
        "dba" or "db(a)" => new("noise-a", "dBA", 1), "db" or "分贝" => new("noise", "dB", 1),
        "fps" or "帧/秒" or "帧" => new("fps", "FPS", 1), "%" => new("ratio", "%", 1), _ => null
        };
    }
    internal static double? ReadNumber(string source)
    {
        string text = Clean(source).Replace('兩', '两').Replace('萬', '万').Replace('億', '亿').Replace('〇', '零');
        if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double arabic)) return double.IsFinite(arabic) && arabic >= 0 ? arabic : null;
        if (!IsMatch(text, "^" + Number + "$")) return null;
        if (IsMatch(text, "[十百千万亿]") && IsMatch(text, "[一二两三四五六七八九]{2}")) return null;
        // Colloquial 一千二 / 一万五 has multiple scales; do not guess the omitted place.
        if (IsMatch(text, "[百千万亿][一二两三四五六七八九]$")) return null;
        string digits = "零一二三四五六七八九";
        int Digit(char ch) => ch == '两' ? 2 : digits.IndexOf(ch);
        string[] split = text.Split('点'); if (split.Length > 2) return null;
        double number;
        if (split[0].All(c => Digit(c) >= 0))
        {
            number = 0; foreach (char c in split[0]) number = number * 10 + Digit(c);
        }
        else
        {
            double total = 0, section = 0, pending = 0; int previousSmall = 10000;
            foreach (char c in split[0])
            {
                int digit = Digit(c); if (digit >= 0) { pending = digit; continue; }
                int scale = c switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, '亿' => 100000000, _ => 0 };
                if (scale == 0) return null;
                if (scale < 10000)
                {
                    if (scale >= previousSmall) return null;
                    section += (pending == 0 ? 1 : pending) * scale; pending = 0; previousSmall = scale;
                }
                else
                {
                    section += pending; pending = 0;
                    if (scale == 100000000) { total = (total + section) * scale; section = 0; }
                    else { total += section * scale; section = 0; }
                    previousSmall = 10000;
                }
            }
            number = total + section + pending;
        }
        if (split.Length == 2)
        {
            double scale = .1;
            foreach (char c in split[1]) { int digit = Digit(c); if (digit < 0) return null; number += digit * scale; scale /= 10; }
        }
        return double.IsFinite(number) && number <= 1e12 ? number : null;
    }
    private static string? Direction(string prefix, string suffix)
    {
        string p = prefix.ToLowerInvariant();
        string? before = p switch
        {
            "不得低于" or "不能低于" or "不要低于" or "不可低于" or "别低于" or "不低于" or "不小于" or "不少于" or "至少" or "起码" or ">=" or "≥" => "min",
            "不得超过" or "不能超过" or "不要超过" or "不可超过" or "不得大于" or "不能大于" or "不高于" or "不大于" or "不多于" or "不超过" or "别超过" or "至多" or "最多" or "<=" or "≤" => "max",
            "大于" or "高于" or "多于" or "超过" or ">" => "gt", "小于" or "低于" or "少于" or "不到" or "<" => "lt",
            "等于" or "正好" or "恰好" or "=" => "equal", _ => null
        };
        string? after = suffix switch { "及以上" or "以上" or "起步" => "min", "及以下" or "以下" or "以内" => "max", _ => null };
        return before != null && after != null && before != after ? "invalid" : before ?? after;
    }
    private static string? Metric(SemanticFact fact, Unit unit)
    {
        string subject = fact.Subject;
        if (fact.Kind == "budget") return unit.Family == "money" && IsMatch(subject, @"^(整机|总|购机|电脑)?预算(上限|下限|范围)?$") ? "price" : null;
        if (fact.Kind == "resource" && unit.Family == "capacity")
        {
            bool vram = IsMatch(subject, @"显存|\bVRAM\b"), memory = IsMatch(subject, @"内存|\bRAM\b"), storage = IsMatch(subject, @"存储|硬盘|\bSSD\b");
            if (new[] { vram, memory, storage }.Count(x => x) != 1 || memory && IsMatch(subject, @"显卡|\bGPU\b")) return null;
            return vram ? "vram" : memory ? "memory" : "storage";
        }
        if (fact.Kind is "experience" or "portability" or "noise" or "power" && unit.Family is "duration" or "mass" or "power" or "noise" or "noise-a" or "fps" or "ratio") return "task";
        return null;
    }
    private static bool SupportedBySource(SemanticFact fact, double value, Unit unit, string direction)
    {
        string raw = Clean(fact.Evidence);
        if (IsMatch(raw, @"大约|大概|左右|上下|约\s*[0-9零一二两三四五六七八九十]|不要求|不需要|无需|不必|不用|不打算|不准备|并非|避免|防止|拒绝|排除|禁止|不能不|不得不|不一定|可能|也许|或许|未必")) return false;
        var matches = Regex.Matches(raw, "(?<![0-9A-Za-z.])(?<n>" + Number + @")\s*(?<u>" + Units + @"(?![A-Za-z/]))?", Options, TimeSpan.FromMilliseconds(100));
        for (int i = 0; i < matches.Count; i++)
        {
            var item = matches[i]; Unit? sourceUnit = ReadUnit(item.Groups["u"].Value);
            var before = Match(raw[..item.Index], "(?<p>" + Prefixes + ")" + Modal + @"\s*$");
            var after = Match(raw[(item.Index + item.Length)..], @"^\s*(?<s>" + Suffixes + ")");
            string? sourceDirection = Direction(before.Groups["p"].Value, after.Groups["s"].Value);
            if (sourceDirection == null && i + 1 < matches.Count && IsMatch(raw[(item.Index + item.Length)..matches[i + 1].Index], @"^\s*(到|至|[-–~～]|to)\s*$")) sourceDirection = "min";
            if (sourceDirection == null && i > 0 && IsMatch(raw[(matches[i - 1].Index + matches[i - 1].Length)..item.Index], @"^\s*(到|至|[-–~～]|to)\s*$")) sourceDirection = "max";
            sourceDirection ??= IsMatch(raw[..item.Index], @"下限\s*$") ? "min" : IsMatch(raw[..item.Index], @"上限\s*$") || fact.Kind == "budget" && IsMatch(raw[..item.Index], @"(?:预算(?:上限|下限|范围)?|budget)\s*$") ? "max" : null;
            if (sourceDirection != direction && sourceDirection != "equal") continue;
            if (sourceUnit == null)
            {
                // A trailing shared unit belongs to both endpoints of a simple range.
                if (i + 1 < matches.Count && IsMatch(raw[(item.Index + item.Length)..matches[i + 1].Index], @"^\s*(到|至|[-–~～]|to)\s*$")) sourceUnit = ReadUnit(matches[i + 1].Groups["u"].Value);
                if (sourceUnit == null && i > 0 && IsMatch(raw[(matches[i - 1].Index + matches[i - 1].Length)..item.Index], @"^\s*(到|至|[-–~～]|to)\s*$")) sourceUnit = ReadUnit(matches[i - 1].Groups["u"].Value);
                // A reviewed CNY budget may omit 元, but explicit foreign currency must never be converted as CNY.
                if (sourceUnit == null && fact.Kind == "budget" && unit.Family == "money" && IsMatch(raw, "预算|budget")
                    && IsMatch(raw[(item.Index + item.Length)..], @"^\s*(?:$|[，,。；;（(]|到|至|[-–~～]|以内|以上|以下)")
                    && !IsMatch(raw, @"\$|美元|欧元|港币|港元|日元|USD|EUR|HKD|JPY|€|£")) sourceUnit = new("money", "元", 1);
            }
            double? number = ReadNumber(item.Groups["n"].Value);
            if (sourceUnit?.Family == unit.Family && number.HasValue && Math.Abs(number.Value * sourceUnit.Scale - value) <= Math.Max(1e-10, Math.Abs(value) * 1e-12)) return true;
        }
        return false;
    }
    private static string QuantityText(string text)
    {
        // Models sometimes keep the measured object before the quantity. Lift only an entire,
        // single bound/range with a short, non-numeric, positive name prefix; never discard negation.
        string scalar = Prefixes + Modal + @"\s*" + Number + @"\s*(?:" + Units + @")?\s*(?:" + Suffixes + ")?";
        string range = Number + @"\s*(?:" + Units + @")?\s*(?:到|至|[-–~～]|to)\s*" + Number + @"\s*(?:" + Units + ")?";
        var match = Match(text, @"^(?<name>[^0-9零〇一二两兩三四五六七八九十百千万萬亿億<>≥≤=]{1,80}?)[：:\s]*(?<quantity>(?:" + scalar + "|" + range + "))$");
        if (!match.Success) return text;
        string name = match.Groups["name"].Value;
        if (IsMatch(name, @"不|无|未|没|否|别|可能|也许|或|约|大概|最好|若|如果|仅当|避免|防止|拒绝|排除|禁止|\b(?:not|no|or|maybe|approximately|if|unless|prefer)\b")) return text;
        return match.Groups["quantity"].Value;
    }
    public static List<QuantityGoal> Compile(SemanticFact fact)
    {
        if (fact.Polarity is not ("required" or "conditional") || fact.Timeframe is not ("current" or "future")) return [];
        string text = QuantityText(Clean(fact.Value)), suppliedUnit = Clean(fact.Unit);
        var range = Match(text, @"^(?<a>" + Number + @")\s*(?<au>" + Units + @")?\s*(?:到|至|[-–~～]|to)\s*(?<b>" + Number + @")\s*(?<bu>" + Units + @")?$");
        var rawBounds = new List<(string Number, string Unit, string Direction)>();
        if (range.Success)
        {
            string aUnit = range.Groups["au"].Value, bUnit = range.Groups["bu"].Value;
            rawBounds.Add((range.Groups["a"].Value, aUnit.Length > 0 ? aUnit : bUnit.Length > 0 ? bUnit : suppliedUnit, "min"));
            rawBounds.Add((range.Groups["b"].Value, bUnit.Length > 0 ? bUnit : aUnit.Length > 0 ? aUnit : suppliedUnit, "max"));
        }
        else
        {
            var scalar = Match(text, @"^(?<p>" + Prefixes + ")?" + Modal + @"\s*(?<n>" + Number + @")\s*(?<u>" + Units + @")?\s*(?<s>" + Suffixes + @")?$");
            if (!scalar.Success) return [];
            string? direction = Direction(scalar.Groups["p"].Value, scalar.Groups["s"].Value);
            if (direction == "invalid") return [];
            direction ??= fact.Subject.EndsWith("下限", StringComparison.Ordinal) ? "min" : fact.Subject.EndsWith("上限", StringComparison.Ordinal) ? "max" : fact.Kind == "budget" ? "max" : null;
            if (direction == null) return [];
            string u = scalar.Groups["u"].Success ? scalar.Groups["u"].Value : suppliedUnit;
            if (direction == "equal") { rawBounds.Add((scalar.Groups["n"].Value, u, "min")); rawBounds.Add((scalar.Groups["n"].Value, u, "max")); }
            else rawBounds.Add((scalar.Groups["n"].Value, u, direction));
        }
        var goals = new List<QuantityGoal>();
        foreach (var bound in rawBounds)
        {
            Unit? unit = ReadUnit(bound.Unit), declared = ReadUnit(suppliedUnit); double? number = ReadNumber(bound.Number);
            if (unit == null || number == null || suppliedUnit.Length > 0 && (declared == null || declared.Family != unit.Family || declared.Scale != unit.Scale)) return [];
            double value = number.Value * unit.Scale;
            string? metric = Metric(fact, unit);
            if (metric == null || value > 1e12 || !double.IsFinite(value) || !SupportedBySource(fact, value, unit, bound.Direction)) return [];
            goals.Add(new(metric, bound.Direction, value, unit.Canonical));
        }
        if (goals.Count == 2 && (goals[0].Metric != goals[1].Metric || goals[0].Unit != goals[1].Unit || goals[0].Target > goals[1].Target)) return [];
        return goals;
    }
}
