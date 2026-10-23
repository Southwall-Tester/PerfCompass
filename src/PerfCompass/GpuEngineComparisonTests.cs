using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuEngineComparisonTests(Action<string, Action> check, Action<bool, string> assert)
    {
        GpuEngineProfile Profile(string kind = "compute", double observed = 10, double complete = 10,
            double p95 = 2, double peak = 5, double heavyMinutes = 0, string[]? sources = null) =>
            new(kind, "GPU " + kind, observed, complete, p95, peak, heavyMinutes, sources ?? ["D3D Compute_0"]);
        AnalysisReport Context(params GpuEngineProfile[] profiles) => new(
            new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero), 30, 1, 10, 3, true,
            ["既有场景"], [new("gpu", "显卡", "pressure", "近期GPU 编码存在持续高负载", "", "核对任务", "")], [], "")
            { HardwareId = "engine-comparison-machine", RepresentativeConfirmed = true, GpuEngineProfiles = profiles.ToList() };
        PurchaseWorkspace Workspace(AnalysisReport report) => new()
        {
            ContextStamp = CandidateComparison.Stamp(report), CoverageConfirmed = true,
            Criteria = [new() { Name = "内存至少32GB", Metric = "memory", Target = 32, Direction = "min", Unit = "GB", Evidence = "合成目标", Reviewed = true }],
            Candidates =
            [
                new() { Name = "A", Cpu = "测试CPU", Gpu = "测试GPU", MemoryGb = 32, PriceCny = 6000, Source = "合成规格报价" },
                new() { Name = "B", Cpu = "测试CPU", Gpu = "测试GPU", MemoryGb = 64, PriceCny = 8000, Source = "合成规格报价" }
            ]
        };
        void Invalidates(AnalysisReport before, AnalysisReport after, string reason)
        {
            var workspace = Workspace(before);
            assert(CandidateComparison.Evaluate(workspace, before).Candidates.All(c => c.State == "pass"), "fixture was not initially confirmed");
            var result = CandidateComparison.Evaluate(workspace, after);
            assert(CandidateComparison.Stamp(before) != CandidateComparison.Stamp(after)
                && result.Candidates.All(c => c.State == "unknown") && result.Summary.Contains("重新核对")
                && result.Tradeoffs.Length == 0 && !result.Summary.Contains("可少花"), reason);
        }
        check("A new heavy engine invalidates comparison even while another engine keeps the GPU pressure title unchanged", () =>
        {
            var encode = Profile("encode", p95: 98, peak: 100, heavyMinutes: 120, sources: ["D3D Video Encode"]);
            var low = Profile(); var before = Context(encode, low);
            Invalidates(before, Context(encode, low with { Peak = 99, HeavyMinutes = .02 }),
                "new occasional compute work inherited the old encoding-only coverage confirmation");
            Invalidates(before, Context(encode, low with { P95 = 96, Peak = 100, HeavyMinutes = 60 }),
                "new sustained compute work inherited an unchanged pressure card's coverage");
        });
        check("New GPU categories and changed named sources invalidate the confirmed comparison scope", () =>
        {
            var before = Context(Profile());
            Invalidates(before, Context(Profile(), Profile("encode", sources: ["D3D Video Encode"])), "new category reused old scope");
            Invalidates(before, Context(Profile(sources: ["D3D Compute_1"])), "changed sensor source reused old scope");
            Invalidates(before, Context(Profile(sources: ["D3D Compute_0", "D3D Compute_0"])), "duplicate node count was collapsed");
        });
        check("Distinct GPU source scopes cannot share a comparison stamp merely because their name union is equal", () =>
        {
            string[] sources = ["D3D Compute_0", "D3D Compute_1"];
            var joined = Profile(sources: sources) with { SourceScopes = [JsonSerializer.Serialize(sources)] };
            var split = joined with { SourceScopes = [JsonSerializer.Serialize(new[] { sources[0] }), JsonSerializer.Serialize(new[] { sources[1] })] };
            Invalidates(Context(joined), Context(split), "changing simultaneous source scope preserved the prior union-only stamp");
            var added = joined with { SourceScopes = joined.SourceScopes.Concat(split.SourceScopes).ToArray() };
            Invalidates(Context(joined), Context(added), "new source scope was hidden by existing source labels");
        });
        check("GPU missing partial and complete coverage are distinct even if report cards are unchanged", () =>
        {
            var missing = Context(Profile(observed: 0, complete: 0) with { P95 = null, Peak = null });
            var partial = Context(Profile(observed: 10, complete: 5)); var complete = Context(Profile());
            Invalidates(missing, partial, "new partial evidence retained missing-sensor confirmation");
            Invalidates(partial, complete, "complete evidence retained a partial-sensor scope");
            Invalidates(complete, partial, "partial evidence inherited complete-sensor coverage");
            Invalidates(complete, Context(Profile(observed: 5, complete: 5)),
                "missing category intervals were excluded from comparison coverage classification");
        });
        check("Routine GPU numeric and elapsed-time changes do not require repeated scope confirmation", () =>
        {
            var before = Context(Profile());
            var after = Context(Profile(observed: 11, complete: 11, p95: 12, peak: 40)) with { ActiveHours = 11, SampleCount = 50 };
            assert(CandidateComparison.Stamp(before) == CandidateComparison.Stamp(after)
                && CandidateComparison.Evaluate(Workspace(before), after).Candidates.All(c => c.State == "pass"),
                "routine readings or duration changed the semantic coverage scope");
            var partial = Context(Profile(observed: 8, complete: 5, peak: 99, heavyMinutes: .1));
            var morePartial = Context(Profile(observed: 9, complete: 6, p95: 60, peak: 100, heavyMinutes: 2));
            assert(CandidateComparison.Stamp(partial) == CandidateComparison.Stamp(morePartial), "existing partial heavy evidence invalidated on every numeric refresh");
        });
        check("GPU profile source and scope ordering is stable while a legacy manual profile can fall back to sources", () =>
        {
            string[] sources = ["D3D Compute_0", "D3D Compute_1"];
            var compute = Profile(sources: sources); var encode = Profile("encode", sources: ["D3D Video Encode"]);
            var explicitScope = compute with { Sources = sources.Reverse().ToArray(), SourceScopes = [JsonSerializer.Serialize(sources)] };
            assert(CandidateComparison.Stamp(Context(compute, encode)) == CandidateComparison.Stamp(Context(encode, explicitScope)),
                "profile ordering or equivalent fallback source scope changed the stamp");
            var scopes = compute with { SourceScopes = [JsonSerializer.Serialize(new[] { sources[0] }), JsonSerializer.Serialize(sources)] };
            assert(CandidateComparison.Stamp(Context(scopes)) == CandidateComparison.Stamp(Context(scopes with { SourceScopes = scopes.SourceScopes.Reverse().ToArray() })),
                "source-scope ordering changed comparison coverage");
        });
        check("GPU scope additions preserve existing hardware method representative and history protections", () =>
        {
            var before = Context(Profile());
            foreach (var after in new[]
            {
                before with { HardwareId = "other-machine" }, before with { MethodVersion = before.MethodVersion + "-changed" },
                before with { RepresentativeConfirmed = false },
                before with { HistoryQuality = new(60, 1, 1, "changed-history", [new("samples-test.jsonl", 1, "invalid synthetic row")]) },
                before with { Cards = [before.Cards[0] with { Status = "fit", Title = "已观察到引擎重负载，先核对任务需求" }] }
            }) Invalidates(before, after, "new GPU scope weakened a pre-existing context protection");
        });
        check("Busy engine draft checklists survive generic scene coverage without inventing numeric performance goals", () =>
        {
            var compute = Profile(peak: 99, heavyMinutes: .02, complete: 5);
            var encode = Profile("encode", peak: 100, heavyMinutes: 60, sources: ["D3D Video Encode"]);
            var idle = Profile("decode", sources: ["D3D Video Decode"]);
            var report = Context(compute, encode, idle) with { SceneProfiles = [new("既有场景", 1, 10, 5, 10, 8, 2, 3, 1, 1)] };
            var drafts = CandidateComparison.Drafts(report);
            var engines = drafts.Where(c => c.OriginId.StartsWith("gpu-engine:", StringComparison.Ordinal)).ToArray();
            assert(drafts.Any(c => c.OriginId == "scene:既有场景") && engines.Length == 2
                && engines.Any(c => c.OriginId == "gpu-engine:compute" && c.Evidence.Contains("D3D Compute_0") && c.Evidence.Contains("99%"))
                && engines.Any(c => c.OriginId == "gpu-engine:encode" && c.Evidence.Contains("D3D Video Encode"))
                && engines.All(c => c.Metric == "check" && c.Target == null && c.Unit.Length == 0 && !c.Reviewed
                    && c.Evidence.Contains("不证明瓶颈") && c.Evidence.Contains("不证明所有引擎覆盖完整")),
                "generic scene concealed engine tasks, or observations became automatic performance targets");
            var updated = CandidateComparison.Drafts(Context(compute with { Peak = 100, HeavyMinutes = 2 }, encode));
            assert(updated.Select(c => c.OriginId).Order().SequenceEqual(engines.Select(c => c.OriginId).Order()),
                "changing observed loads created new checklist identities for the same engine task");
            var manySources = compute with { Sources = Enumerable.Range(0, 256).Select(i => "D3D Compute_" + i).ToArray() };
            var bounded = CandidateComparison.Drafts(Context(manySources)).Single();
            assert(bounded.Evidence.Length <= 3000 && bounded.Evidence.Contains("248 个节点未在此展开"),
                "large node scope created an unusable or silently truncated comparison draft");
        });
    }
}
