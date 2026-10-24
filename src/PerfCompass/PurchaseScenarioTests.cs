using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunPurchaseScenarioTests(Action<string, Action> check, Action<bool, string> assert)
    {
        AnalysisReport Context() => new(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
        PurchaseCriterion Goal(string name, string origin = "") => new() { Name = name, OriginId = origin, Condition = "需求尚未确定：工具", Reviewed = true };
        PurchaseWorkspace Workspace(AnalysisReport context, params PurchaseCriterion[] criteria) => new()
        {
            ContextStamp = CandidateComparison.Stamp(context), CoverageConfirmed = true,
            Candidates = [new() { Name = "合成甲", MemoryGb = 32, PriceCny = 6000, Source = "合成资料" }, new() { Name = "合成乙", MemoryGb = 64, PriceCny = 8000, Source = "合成资料" }],
            Criteria = [new() { Name = "共同内存", Metric = "memory", Target = 16, Unit = "GB", Reviewed = true }, .. criteria]
        };
        void Evidence(PurchaseWorkspace w)
        {
            w.Observations = w.Candidates.SelectMany((candidate, i) => w.Criteria.Where(c => c.Metric == "check").Select((criterion, j) =>
                new PurchaseObservation { CandidateId = candidate.Id, CriterionId = criterion.Id, Verdict = i == j ? "pass" : "fail", Evidence = "合成核对", Comparable = true, ProofStamp = CandidateComparison.ObservationStamp(candidate, criterion) })).ToList();
        }
        check("Distinct unknown semantic alternatives do not become joint requirements", () =>
        {
            foreach (var names in new[] { new[] { "只用引擎甲", "只用引擎乙" }, new[] { "移动采集", "远程开发" }, new[] { "光谱工具", "音频插件" } })
            {
                var context = Context(); var a = Goal(names[0], "semantic:" + new string('A', 64)); var b = Goal(names[1], "semantic:" + new string('B', 64));
                var workspace = Workspace(context, a, b); Evidence(workspace);
                var result = CandidateComparison.Evaluate(workspace, context);
                assert(result.Candidates.All(c => c.State == "pass") && result.ScenarioSummaries.Length == 2, "unknown branches merged or became mandatory");
                assert(result.ScenarioSummaries[0].Contains(names[0]) && result.ScenarioSummaries[0].Contains("6000") && !result.ScenarioSummaries[0].Contains("8000")
                    && result.ScenarioSummaries[1].Contains(names[1]) && result.ScenarioSummaries[1].Contains("8000"), "branch eligibility or readable labels lost");
            }
        });
        check("Manual unresolved criteria use distinct identities even with identical display text", () =>
        {
            var context = Context(); var w = Workspace(context, Goal("同名工具"), Goal("同名工具")); Evidence(w);
            assert(CandidateComparison.Evaluate(w, context).ScenarioSummaries.Length == 2, "manual unknown identities merged");
        });
        check("Explicit shared trigger continues to require all associated goals", () =>
        {
            var context = Context(); var a = Goal("插件甲"); var b = Goal("插件乙");
            foreach (var c in new[] { a, b }) { c.Condition = "接到同一个项目"; c.Reviewed = true; }
            var w = Workspace(context, a, b); Evidence(w);
            var result = CandidateComparison.Evaluate(w, context);
            assert(result.ScenarioSummaries.Length == 1 && result.ScenarioSummaries[0].Contains("当前没有通过"), "explicit simultaneous trigger split into independent successes");
        });
        check("Unknown numeric alternatives avoid false conflicts but same-fact bounds remain joint", () =>
        {
            var context = Context(); var a = Goal("便携模式", "semantic:" + new string('A', 64) + ":max");
            var b = Goal("大型任务", "semantic:" + new string('B', 64) + ":min");
            a.Metric = b.Metric = "memory"; a.Unit = b.Unit = "GB"; a.Direction = "max"; a.Target = 32; b.Target = 64; a.Reviewed = b.Reviewed = true;
            var w = Workspace(context, a, b);
            var result = CandidateComparison.Evaluate(w, context);
            assert(result.Conflicts.Length == 0 && result.ScenarioSummaries.Length == 2, "different uncertainty branches falsely conflict");
            b.OriginId = "semantic:" + new string('A', 64) + ":min"; b.Reviewed = true;
            result = CandidateComparison.Evaluate(w, context);
            assert(result.Conflicts.Length == 1 && result.ScenarioSummaries.Length == 1 && result.ScenarioSummaries[0].Contains("数值冲突"), "same-fact range split or conflict not attached to its scenario");
        });
        check("Real quantity compiler bounds keep one explicit conditional semantic scenario", () =>
        {
            var fact = new SemanticFact("内存", "resource", "conditional", "future", "32到64", "GB", "接到项目", "接到项目时内存32到64GB");
            var context = Context() with { SemanticRequirements = new SemanticPlan(fact.Evidence, "synthetic", "none", SemanticRequirements.Version, [fact], DateTimeOffset.Now) };
            var bounds = CandidateComparison.Drafts(context);
            assert(bounds.Count == 2 && bounds.All(c => c.Target.HasValue), "fixture did not exercise quantity compiler");
            foreach (var c in bounds) c.Reviewed = true;
            var result = CandidateComparison.Evaluate(Workspace(context, bounds.ToArray()), context);
            assert(result.ScenarioSummaries.Length == 1 && result.Conflicts.Length == 0, "real compiled range separated");
        });
        check("Changing unknown branch origin or manual identity revokes old reviews and evidence", () =>
        {
            foreach (bool semantic in new[] { true, false })
            {
                var c = Goal("工具", semantic ? "semantic:" + new string('A', 64) : "");
                string stamp = CandidateComparison.CriterionStamp(c); var candidate = new PurchaseCandidate();
                string proof = CandidateComparison.ObservationStamp(candidate, c);
                if (semantic) c.OriginId = "semantic:" + new string('B', 64); else c.Id = Guid.NewGuid().ToString("N");
                assert(stamp != CandidateComparison.CriterionStamp(c) && proof != CandidateComparison.ObservationStamp(candidate, c), "branch edit preserved former proof");
            }
        });
        check("Legacy uncertainty migration preserves source bytes and evidence while clearing confirmations", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-scenario-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var context = Context(); var c = Goal("旧待定工具");
                string legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { c.Name, c.Metric, c.Direction, c.Target, c.Unit, c.Condition, c.Evidence, c.Procedure }))));
                c.ReviewStamp = legacy;
                var w = Workspace(context, c); Evidence(w); w.Version = "comparison-1.2";
                string path = Path.Combine(root, "legacy.json"); File.WriteAllText(path, JsonSerializer.Serialize(w)); byte[] bytes = File.ReadAllBytes(path);
                var loaded = CandidateComparison.LoadFile(path);
                assert(loaded.Version == "comparison-1.3" && !loaded.CoverageConfirmed && !loaded.Criteria[1].Reviewed && loaded.Criteria[1].ReviewStamp == legacy, "legacy review improperly migrated");
                assert(loaded.Criteria[0].Reviewed && JsonSerializer.Serialize(w.Observations) == JsonSerializer.Serialize(loaded.Observations)
                    && bytes.SequenceEqual(File.ReadAllBytes(path)), "migration overwrote source or unrelated evidence");
            }
            finally { Directory.Delete(root, true); }
        });
        check("Current uncertainty schema roundtrip preserves freshly reviewed separate scenarios", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "PerfCompass-scenario-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var context = Context(); var w = Workspace(context, Goal("甲"), Goal("乙")); Evidence(w);
                CandidateComparison.SaveFile(path, w); var loaded = CandidateComparison.LoadFile(path);
                assert(loaded.Version == "comparison-1.3" && loaded.CoverageConfirmed && loaded.Criteria.All(c => c.Reviewed)
                    && CandidateComparison.Evaluate(loaded, context).ScenarioSummaries.Length == 2, "current saved checks lost or merged");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
    }
}
