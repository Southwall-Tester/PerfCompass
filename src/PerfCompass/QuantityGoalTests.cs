using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunQuantityGoalTests(Action<string, Action> check, Action<bool, string> assert)
    {
        check("Quantity grammar matches the diverse reviewed-fact corpus", () =>
        {
            using var input = typeof(QuantityGoals).Assembly.GetManifestResourceStream("PerfCompass.QuantityCases.json")!;
            using var corpus = JsonDocument.Parse(input); var log = new List<string>(); var errors = new List<string>();
            foreach (var item in corpus.RootElement.EnumerateArray())
            {
                string Read(string key, string fallback = "") => item.TryGetProperty(key, out var p) ? p.GetString()! : fallback;
                var fact = new SemanticFact(Read("subject"), Read("kind"), Read("polarity", "required"), Read("timeframe", "current"), Read("value"), Read("unit"), Read("polarity") == "conditional" ? "选上课程" : "", Read("source"));
                var actual = QuantityGoals.Compile(fact); var expected = item.GetProperty("expected").EnumerateArray().ToArray();
                bool correct = actual.Count == expected.Length;
                if (correct) for (int i = 0; i < expected.Length; i++)
                    correct &= actual[i].Metric == expected[i][0].GetString() && actual[i].Direction == expected[i][1].GetString() && Math.Abs(actual[i].Target - expected[i][2].GetDouble()) < 1e-8 && actual[i].Unit == expected[i][3].GetString();
                log.Add((correct ? "PASS " : "FAIL ") + Read("id") + ": " + JsonSerializer.Serialize(actual));
                if (!correct) errors.Add(Read("id"));
            }
            File.WriteAllLines("artifacts/quantity-cases.txt", log); assert(errors.Count == 0, "Mismatched cases: " + string.Join(", ", errors));
        });
        check("Quantity compilation rejects changed scales numeric fragments and approximate targets", () =>
        {
            SemanticFact Fact(string source, string value, string unit, string kind = "resource", string subject = "内存") => new(subject, kind, "required", "current", value, unit, "", source);
            foreach (var fact in new[] { Fact("内存至少100Mbps", "至少100", "MB"), Fact("预算1e3元", "1", "元", "budget", "预算"), Fact("预算几十元", "10", "元", "budget", "预算"), Fact("内存至少64GB左右", "至少64", "GB"), Fact("预算大约6000到8000元", "6000到8000", "元", "budget", "预算"), Fact("内存至少64Gb", "至少64", "GB") })
                assert(QuantityGoals.Compile(fact).Count == 0, "fragment or approximation accepted: " + fact.Evidence);
        });
        check("Binary telemetry labels and CSV headers preserve raw values without decimal relabeling", () =>
        {
            var sample = new Sample { Time = DateTimeOffset.Now, Seconds = 10, Active = true, MemoryTotalGb = 32, MemoryUsedGb = 16, MemoryPeakGb = 16, CommitGb = 18, DiskTotalGb = 1024, DiskUsedGb = 512 };
            var report = AnalysisEngine.Analyze([sample], new(), 7);
            assert(report.Cards.Single(c => c.Key == "memory").Evidence.Contains("32.0 GiB") && report.Cards.Single(c => c.Key == "disk").Evidence.Contains("1024 GiB"), "binary telemetry mislabeled as decimal");
            string path = Path.GetFullPath("artifacts/binary-units.csv"); ReportExporter.Csv(path, [sample]);
            string[] lines = File.ReadAllLines(path); var header = lines[0].Split(','); var values = lines[1].Split(',');
            assert(header.Contains("memory_used_gib") && values[Array.IndexOf(header, "memory_used_gib")] == "16" && header.Contains("disk_read_mib_per_second") && !header.Contains("memory_used_gb"), "CSV units or values changed incorrectly");
        });
        check("Chinese quantities use place values without guessing omitted colloquial positions", () =>
        {
            assert(QuantityGoals.ReadNumber("两千五百六十") == 2560 && QuantityGoals.ReadNumber("一万零二") == 10002 && QuantityGoals.ReadNumber("十二") == 12 && QuantityGoals.ReadNumber("零点五") == .5, "place values wrong");
            assert(QuantityGoals.ReadNumber("一千二") == null && QuantityGoals.ReadNumber("一二十") == null && QuantityGoals.ReadNumber("十十") == null, "ambiguous or malformed numeral accepted");
        });
        check("Ranges create separate unreviewed bounds without promoting conditional or excluded goals", () =>
        {
            var fact = new SemanticFact("预算", "budget", "conditional", "future", "六千到八千", "元", "接到项目", "如果接到项目，预算六千到八千");
            var report = new AnalysisReport(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], fact.Evidence) { SemanticRequirements = new(fact.Evidence, "fixture", "fixture", SemanticRequirements.Version, [fact], DateTimeOffset.Now) };
            var goals = CandidateComparison.Drafts(report);
            assert(goals.Count == 2 && goals.Select(g => g.OriginId).Distinct().Count() == 2 && goals.All(g => g.Condition == "接到项目" && !g.Reviewed), "bound identity, confirmation or trigger lost");
            var workspace = new PurchaseWorkspace { ContextStamp = CandidateComparison.Stamp(report), CoverageConfirmed = true, Criteria = goals,
                Candidates = [new() { Name = "5000", PriceCny = 5000, Source = "fixture" }, new() { Name = "7000", PriceCny = 7000, Source = "fixture" }, new() { Name = "9000", PriceCny = 9000, Source = "fixture" }] };
            foreach (var g in goals) g.Reviewed = true;
            var evaluated = CandidateComparison.Evaluate(workspace, report);
            assert(evaluated.Candidates.All(c => c.State != "fail") && evaluated.Candidates[1].Checks.All(c => c.State == "pass") && evaluated.Candidates[0].Checks.Any(c => c.State == "fail") && evaluated.Candidates[2].Checks.Any(c => c.State == "fail"), "conditional bounds became mandatory or range not enforced");
        });
        check("Strict comparisons and repeated runs preserve equality boundaries", () =>
        {
            assert(!CandidateComparison.Meets(10, 10, "lt") && !CandidateComparison.Meets(10, 10, "gt") && CandidateComparison.Meets(10, 10, "min") && CandidateComparison.Meets(10, 10, "max"), "strict equality became passing");
            var record = MeasurementFixture(9, 10, 10); var goal = new PurchaseCriterion { Metric = "task", Unit = "秒", Target = 10, Direction = "lt", MeasurementProtocol = record.ProtocolFingerprint };
            assert(TaskMeasurements.Assess(record, goal, 10).State == "unknown", "mixed strict runs became passing");
            assert(TaskMeasurements.Assess(MeasurementFixture(10, 10, 10), goal, 10).State == "fail", "equal strict repeats passed");
            goal.Direction = "gt";
            assert(TaskMeasurements.Assess(MeasurementFixture(10, 11, 12), goal, 11).State == "unknown" && TaskMeasurements.Assess(MeasurementFixture(11, 12, 13), goal, 12).State == "pass", "strict lower bound mishandled");
        });
        check("Quantity preview updates live and changed facts invalidate the semantic review", () =>
        {
            bool changed = false; var editor = new SemanticFactEditor(new("延迟", "experience", "required", "current", "低于10", "毫秒", "", "延迟低于10毫秒"));
            editor.Changed = () => changed = true; int notifications = 0;
            editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(editor.QuantityPreview)) notifications++; };
            assert(editor.QuantityPreview.Contains("0.01 秒") && editor.QuantityPreview.Contains("严格小于"), "preview conversion wrong");
            editor.Value = "不超过10"; assert(changed && notifications == 1 && editor.QuantityPreview.Contains("逐项核对"), "changed boundary retained an old numeric preview");
        });
        check("Real provider noun prefixes and modal words compile without removing negation or context", () =>
        {
            SemanticFact Fact(string value, string evidence, string subject = "录音监听", string kind = "experience", string unit = "毫秒") => new(subject, kind, "required", "current", value, unit, "", evidence);
            var latency = QuantityGoals.Compile(Fact("延迟低于10毫秒", "录音监听延迟希望低于10毫秒"));
            assert(latency.Count == 1 && latency[0].Direction == "lt" && latency[0].Target == .01, "provider latency noun prefix lost");
            var memory = QuantityGoals.Compile(Fact("至少要64GB", "内存至少要64GB", "内存", "resource", "GB"));
            assert(memory.Count == 1 && memory[0].Direction == "min" && memory[0].Target == 64, "modal between bound and value lost");
            var range = QuantityGoals.Compile(Fact("预算：六千到八千", "预算六千到八千", "预算", "budget", "元"));
            assert(range.Count == 2 && range[0].Target == 6000 && range[1].Target == 8000, "positive quantity label prefix rejected");
            assert(QuantityGoals.Compile(Fact("不超过10毫秒", "监听延迟不要超过10毫秒")).Single().Direction == "max", "negated upper relation inverted");
            assert(QuantityGoals.Compile(Fact("至少64GB", "内存不能低于64GB", "内存", "resource", "GB")).Single().Direction == "min", "negated lower relation inverted");
            foreach (var fact in new[] { Fact("低于10毫秒", "不要求监听延迟低于10毫秒"), Fact("避免延迟低于10毫秒", "避免延迟低于10毫秒"), Fact("10轨时延迟低于10毫秒", "10轨时延迟低于10毫秒"), Fact("延迟可能低于10毫秒", "延迟可能低于10毫秒"), Fact("低于10毫秒", "延迟可能低于10毫秒"), Fact("低于10毫秒", "延迟不一定低于10毫秒"), Fact("延迟低于10毫秒或者不超过20毫秒", "延迟低于10毫秒或者不超过20毫秒") })
                assert(QuantityGoals.Compile(fact).Count == 0, "context or negation discarded: " + fact.Value);
        });
    }
}
