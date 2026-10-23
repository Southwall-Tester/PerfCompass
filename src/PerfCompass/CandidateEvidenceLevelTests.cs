namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunCandidateEvidenceLevelTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var context = new AnalysisReport(DateTimeOffset.Now, 30, 1, 10, 3, true, [], [], [], "")
            { HardwareId = "candidate-evidence-synthetic", RepresentativeConfirmed = true };
        PurchaseCandidate Candidate(string name = "候选 A", double? price = null, double memory = 32) => new()
            { Name = name, Cpu = "合成 CPU", Gpu = "合成 GPU", MemoryGb = memory, PriceCny = price, Source = "合成规格与报价来源" };
        PurchaseCriterion Specification() => new()
            { Name = "内存目标", Metric = "memory", Target = 32, Direction = "min", Unit = "GB", Evidence = "合成目标", Reviewed = true };
        PurchaseCriterion ManualTask() => new()
            { Name = "手填任务", Metric = "task", Target = 10, Direction = "max", Unit = "秒", Procedure = "合成统一步骤", Reviewed = true };
        PurchaseCriterion RepeatedTask(TaskMeasurementResult result) => new()
        {
            Name = "重复任务", Metric = "task", Target = 10, Direction = "max", Unit = "秒",
            Procedure = result.Recipe.Procedure, MeasurementProtocol = result.ProtocolFingerprint, Reviewed = true
        };
        PurchaseWorkspace Workspace(params PurchaseCriterion[] criteria) => new()
        {
            ContextStamp = CandidateComparison.Stamp(context), CoverageConfirmed = true,
            Candidates = [Candidate()], Criteria = criteria.ToList()
        };
        PurchaseObservation Attach(PurchaseWorkspace workspace, PurchaseCriterion goal, double? value = null, TaskMeasurementResult? measurement = null)
        {
            var candidate = workspace.Candidates.Single();
            var observation = new PurchaseObservation
            {
                CandidateId = candidate.Id, CriterionId = goal.Id, Value = value, Verdict = "pass",
                Evidence = measurement == null ? "合成人工记录，非原始重复测量" : TaskMeasurements.Evidence(measurement),
                Measurement = measurement, Comparable = true, ProofStamp = CandidateComparison.ObservationStamp(candidate, goal)
            };
            workspace.Observations.Add(observation); return observation;
        }
        PurchaseAssessment Assess(PurchaseWorkspace workspace) => CandidateComparison.Evaluate(workspace, context).Candidates.Single();

        check("One candidate can satisfy a target without a quotation and cannot manufacture savings", () =>
        {
            var workspace = Workspace(Specification());
            var result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates.Single().State == "pass" && result.Summary.Contains("必需条件已通过")
                && result.Summary.Contains("报价未填写") && result.Tradeoffs.Length == 0 && !result.Summary.Contains("可少花"),
                "a single otherwise valid candidate required a second quotation or manufactured savings");
            workspace.Candidates[0].PriceCny = 6000;
            result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Candidates.Single().State == "pass" && result.Summary.Contains("6000")
                && result.Tradeoffs.Length == 0 && !result.Summary.Contains("可少花"), "one quoted candidate became a price comparison");
            workspace.Candidates[0].MemoryGb = 16;
            assert(Assess(workspace).State == "fail", "single-candidate validation bypassed a failed required target");
            workspace.Candidates[0].MemoryGb = null;
            assert(Assess(workspace).State == "unknown", "missing single-candidate specifications became a pass");
        });

        check("Specification manual checklist and manually entered task values retain distinct non-repeated evidence labels", () =>
        {
            var spec = Specification();
            var manual = new PurchaseCriterion { Name = "人工软件兼容核对", Metric = "check", Reviewed = true };
            var task = ManualTask(); var workspace = Workspace(spec, manual, task);
            Attach(workspace, manual); Attach(workspace, task, 9);
            var result = Assess(workspace);
            assert(result.State == "pass" && result.Checks.All(c => c.State == "pass")
                && result.Checks.Single(c => c.Name == spec.Name) is { EvidenceKind: "specification", EvidenceLabel: "规格 / 报价资料" }
                && result.Checks.Single(c => c.Name == manual.Name) is { EvidenceKind: "manual-check", EvidenceLabel: "人工核对" }
                && result.Checks.Single(c => c.Name == task.Name) is { EvidenceKind: "manual-measurement", EvidenceLabel: "手填任务数值" }
                && result.Checks.All(c => c.EvidenceLabel != "重复耗时测量达标"),
                "manually supplied or specification evidence was presented as a repeated task measurement");
            var legacy = new PurchaseCheck("旧结果", "unknown", "待核对", "", "");
            assert(legacy.EvidenceKind == "none" && legacy.EvidenceLabel == "待补依据", "existing result constructors acquired an unearned evidence type");
        });

        check("Only a passing protocol-bound original repeat result earns the repeated elapsed-time pass label", () =>
        {
            foreach (var fixture in new[]
            {
                (Result: MeasurementFixture(8, 9, 10), State: "pass", Label: "重复耗时测量达标"),
                (Result: MeasurementFixture(8, 10, 12), State: "unknown", Label: "重复耗时测量待核对"),
                (Result: MeasurementFixture(11, 12, 13), State: "fail", Label: "重复耗时测量未达标")
            })
            {
                var goal = RepeatedTask(fixture.Result); var workspace = Workspace(goal);
                Attach(workspace, goal, fixture.Result.MedianSeconds, fixture.Result);
                var checkResult = Assess(workspace).Checks.Single();
                assert(checkResult.State == fixture.State && checkResult.EvidenceKind == "repeated-measurement"
                    && checkResult.EvidenceLabel == fixture.Label && checkResult.Reading.Contains("范围") && checkResult.Reading.Contains("3 次"),
                    "evidence labeling bypassed the original per-attempt target assessment");
            }
        });

        check("Failed power-changed mismatched or edited repeated evidence cannot be relabeled as measured success", () =>
        {
            var original = MeasurementFixture(8, 9, 10);
            var failed = original with
            {
                Outcome = "timeout", Attempts = original.Attempts.Select((a, index) => index == 2
                    ? a with { Outcome = "timeout", ExitCode = null } : a).ToList()
            };
            var powerChanged = original with
                { Attempts = original.Attempts.Select((a, index) => index == 2 ? a with { PowerAfter = "Offline" } : a).ToList() };
            foreach (var result in new[] { failed, powerChanged })
            {
                var goal = RepeatedTask(result); var workspace = Workspace(goal);
                Attach(workspace, goal, result.MedianSeconds, result);
                assert(Assess(workspace).Checks.Single() is { State: "unknown", EvidenceLabel: "重复耗时测量待核对" },
                    "incomplete execution or changed power acquired a measured-success label");
            }
            var mismatched = RepeatedTask(original); mismatched.MeasurementProtocol = new string('F', 64); mismatched.Reviewed = true;
            var wrongProtocol = Workspace(mismatched); Attach(wrongProtocol, mismatched, original.MedianSeconds, original);
            assert(Assess(wrongProtocol).Checks.Single() is { State: "unknown", EvidenceLabel: "重复耗时测量待核对" },
                "a protocol mismatch retained a repeat-success label");
            var matching = RepeatedTask(original); var edited = Workspace(matching);
            Attach(edited, matching, original.MedianSeconds + .5, original);
            assert(Assess(edited).Checks.Single() is { State: "unknown", EvidenceLabel: "重复耗时测量待核对" },
                "an edited displayed value replaced the original repeated result");
            var missing = Workspace(matching); Attach(missing, matching, 9);
            assert(Assess(missing).Checks.Single() is { State: "unknown", EvidenceKind: "none", EvidenceLabel: "尚缺原始重复测量" },
                "a manual value substituted for a goal requiring original protocol-bound measurement");
        });

        check("Evidence labels preserve criterion candidate context and coverage invalidation rules", () =>
        {
            var result = MeasurementFixture(8, 9, 10); var goal = RepeatedTask(result); var workspace = Workspace(goal);
            Attach(workspace, goal, result.MedianSeconds, result);
            string stamp = CandidateComparison.Stamp(context);
            goal.Reviewed = false;
            assert(Assess(workspace).Checks.Single() is { State: "unknown", EvidenceLabel: "重复耗时测量待核对" },
                "a raw measurement bypassed unreviewed target conditions");
            goal.Reviewed = true; workspace.Candidates[0].Gpu = "changed GPU";
            assert(Assess(workspace).State == "unknown" && Assess(workspace).Checks.Single().EvidenceLabel != "重复耗时测量达标",
                "a changed candidate inherited its old measurement association");
            workspace.Candidates[0].Gpu = "合成 GPU"; workspace.CoverageConfirmed = false;
            assert(Assess(workspace).State == "unknown", "passed task evidence substituted for coverage review");
            workspace.CoverageConfirmed = true;
            var changedScope = CandidateComparison.Evaluate(workspace, context with { WindowDays = 7 });
            assert(changedScope.Candidates.Single().State == "unknown" && CandidateComparison.Stamp(context) == stamp,
                "result evidence properties changed stamps or bypassed a changed analysis scope");
            goal.Target = 20;
            assert(Assess(workspace).State == "unknown", "a revised goal retained its old review and observation proof");
        });

        check("Observation scope confirmation follows resource categories and sources rather than new durations or percentages", () =>
        {
            var observation = new ResourceObservation("light", "初步余量", "P95 8%", "补齐典型任务", "gpu:new-source", .5, .8);
            var card = new Advice("gpu", "显卡", "unknown", "待积累", "原有说明", "原有建议", "") { Observation = observation };
            var observed = context with { Cards = [card] };
            var workspace = Workspace(Specification()); workspace.ContextStamp = CandidateComparison.Stamp(observed);
            var refreshed = observed with { Cards = [card with { Observation = observation with
            {
                Title = "更新初步余量", Evidence = "P95 9%", Action = "更新建议", ObservedHours = 1.2, Coverage = .91
            } }] };
            assert(CandidateComparison.Stamp(observed) == CandidateComparison.Stamp(refreshed)
                && CandidateComparison.Evaluate(workspace, refreshed).Candidates.Single().State == "pass",
                "ordinary observation values or presentation text invalidated an unchanged reviewed scope");
            foreach (var changed in new ResourceObservation?[]
            {
                observation with { State = "busy" }, observation with { Scope = "gpu:another-source" }, null
            })
            {
                var changedReport = observed with { Cards = [card with { Observation = changed }] };
                assert(CandidateComparison.Stamp(observed) != CandidateComparison.Stamp(changedReport)
                    && CandidateComparison.Evaluate(workspace, changedReport).Candidates.Single().State == "unknown",
                    "a changed or removed observation category/source inherited an old coverage confirmation");
            }
            var noObservation = observed with { Cards = [card with { Observation = null }] };
            assert(CandidateComparison.Stamp(noObservation) != CandidateComparison.Stamp(observed),
                "new observation evidence inherited a pre-observation coverage confirmation");
        });

        check("Savings still require two quoted required-condition passes and exclude cheaper failing candidates", () =>
        {
            var workspace = Workspace(Specification());
            workspace.Candidates = [Candidate("A", 6000), Candidate("B", 8000, 64), Candidate("C", 3000, 16)];
            var result = CandidateComparison.Evaluate(workspace, context);
            assert(result.Summary.Contains("可少花 2000") && result.Tradeoffs.Length == 1
                && result.Candidates.Single(c => c.Name == "C").State == "fail" && !result.Summary.Contains("5000"),
                "an unqualified cheaper candidate changed the measured price difference");
            workspace.Candidates[1].Source = "";
            result = CandidateComparison.Evaluate(workspace, context);
            assert(!result.Summary.Contains("可少花") && result.Tradeoffs.Length == 0,
                "one qualified quote plus an unknown quote source manufactured savings");
        });

        check("Candidate HTML distinguishes all evidence types without promoting the overall pass to whole-machine validation", () =>
        {
            var measurement = MeasurementFixture(8, 9, 10);
            var spec = Specification(); var manual = new PurchaseCriterion { Name = "人工核对", Metric = "check", Reviewed = true };
            var task = ManualTask(); var repeated = RepeatedTask(measurement);
            var workspace = Workspace(spec, manual, task, repeated);
            Attach(workspace, manual); Attach(workspace, task, 9); Attach(workspace, repeated, measurement.MedianSeconds, measurement);
            var result = CandidateComparison.Evaluate(workspace, context); string html = CandidateComparison.Html(workspace, result);
            assert(result.Candidates.Single().State == "pass" && html.Contains("规格 / 报价资料") && html.Contains("人工核对")
                && html.Contains("手填任务数值") && html.Contains("重复耗时测量达标") && html.Contains("必需条件：满足已核对条件")
                && html.Contains("机器对应关系与实际产物仍由用户核对") && !html.Contains("整机实测已验证"),
                "HTML concealed evidence provenance or turned a required-condition pass into full machine validation");
            var assessment = result.Candidates.Single(); var first = assessment.Checks[0];
            var escaped = result with { Candidates = [assessment with { Checks = [first with { EvidenceLabel = "<script>source</script>" }] }] };
            string encoded = CandidateComparison.Html(workspace, escaped);
            assert(encoded.Contains("&lt;script&gt;source&lt;/script&gt;") && !encoded.Contains("<script>source</script>"),
                "a newly displayed evidence label bypassed HTML encoding");
        });
    }
}
