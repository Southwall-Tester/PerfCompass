namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunComparisonDecisionIntegrityTests(Action<string, Action> check, Action<bool, string> assert)
    {
        var context = new AnalysisReport(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
        PurchaseCriterion Goal(string metric = "check", string condition = "") => new()
        {
            Name = metric == "check" ? "核对合成任务" : "合成任务耗时", Metric = metric,
            Target = metric == "task" ? 10 : null, Direction = "max", Unit = metric == "task" ? "秒" : "",
            Procedure = metric == "task" ? "同一软件与输入的合成任务" : "", Condition = condition, Reviewed = true
        };
        PurchaseWorkspace Workspace(params PurchaseCriterion[] goals) => new()
        {
            ContextStamp = CandidateComparison.Stamp(context), CoverageConfirmed = true, Criteria = goals.ToList(),
            Candidates =
            [
                new() { Name = "A", Cpu = "合成CPU", Gpu = "合成GPU", MemoryGb = 32, PriceCny = 6000, Source = "合成报价单A" },
                new() { Name = "B", Cpu = "合成CPU", Gpu = "合成GPU", MemoryGb = 32, PriceCny = 9000, Source = "合成报价单B" }
            ]
        };
        void Attach(PurchaseWorkspace workspace, PurchaseCriterion goal)
        {
            foreach (var candidate in workspace.Candidates)
                workspace.Observations.Add(new()
                {
                    CandidateId = candidate.Id, CriterionId = goal.Id, Verdict = "pass",
                    Value = goal.Metric == "task" ? candidate.Name == "A" ? 9 : 5 : null,
                    Evidence = "合成任务记录，非报价资料", Comparable = true,
                    ProofStamp = CandidateComparison.ObservationStamp(candidate, goal)
                });
        }
        PurchaseComparison Evaluate(PurchaseWorkspace workspace) => CandidateComparison.Evaluate(workspace, context);
        (PurchaseWorkspace Workspace, PurchaseCriterion Task) ConditionalTask()
        {
            var memory = new PurchaseCriterion { Name = "内存要求", Metric = "memory", Direction = "min", Target = 32, Unit = "GB", Reviewed = true };
            var task = Goal("task", "接到视频项目"); var workspace = Workspace(memory, task); Attach(workspace, task);
            return (workspace, task);
        }

        // Reproduces the original task-only path: every task passed, so bare price
        // numbers reached both price ranking and savings despite missing sources.
        check("Task or checklist passes cannot supply missing or whitespace quotation evidence", () =>
        {
            foreach (string metric in new[] { "check", "task" })
                foreach (string source in new[] { "", " \t\r\n" })
                {
                    var goal = Goal(metric); var workspace = Workspace(goal); Attach(workspace, goal);
                    workspace.Candidates.ForEach(c => c.Source = source);
                    var result = Evaluate(workspace);
                    assert(result.Candidates.All(c => c.State == "pass"), "missing quote evidence changed independently supported task results");
                    assert(!result.Summary.Contains("最低报价为") && !result.Summary.Contains("可少花") && result.Tradeoffs.Length == 0
                        && result.Summary.Contains("待补报价来源") && result.Summary.Contains("A") && result.Summary.Contains("B"),
                        "bare quotes acquired savings or disappeared without an actionable source notice");
                }
        });

        check("One evidenced quote permits only its own ranking and two evidenced quotes restore savings", () =>
        {
            var goal = Goal(); var workspace = Workspace(goal); Attach(workspace, goal);
            workspace.Candidates[0].Source = "";
            var one = Evaluate(workspace);
            assert(one.Candidates.All(c => c.State == "pass") && one.Summary.Contains("最低报价为 B（9000 元）")
                && one.Summary.Contains("待补报价来源：A") && !one.Summary.Contains("可少花") && one.Tradeoffs.Length == 0,
                "the cheaper unsupported quote undercut the only evidenced quotation");
            workspace.Candidates[0].Source = "合成报价单A";
            var two = Evaluate(workspace);
            assert(two.Summary.Contains("最低报价为 A（6000 元）") && two.Summary.Contains("可少花 3000")
                && two.Tradeoffs.Single().Contains("多花 3000") && !two.Summary.Contains("待补报价来源"),
                "adding the missing quotation evidence failed to restore the eligible price comparison");
        });

        check("A single task candidate remains valid without a quote or quote source", () =>
        {
            var goal = Goal("task"); var workspace = Workspace(goal); workspace.Candidates.RemoveAt(1); Attach(workspace, goal);
            workspace.Candidates[0].Source = ""; workspace.Candidates[0].PriceCny = null;
            var absent = Evaluate(workspace);
            assert(absent.Candidates.Single().State == "pass" && absent.Summary.Contains("报价未填写") && absent.Tradeoffs.Length == 0,
                "single task validation began requiring quotation evidence");
            workspace.Candidates[0].PriceCny = 6000;
            var unsourced = Evaluate(workspace);
            assert(unsourced.Candidates.Single().State == "pass" && unsourced.Summary.Contains("待补报价来源")
                && !unsourced.Summary.Contains("可少花") && unsourced.Tradeoffs.Length == 0,
                "one raw price either blocked the task pass or became an evidenced quotation");
        });

        check("Conditional quotation ranking independently excludes missing sources", () =>
        {
            var required = Goal(); var conditional = Goal("check", "接到视频项目");
            var workspace = Workspace(required, conditional); Attach(workspace, required); Attach(workspace, conditional);
            workspace.Candidates.ForEach(c => c.Source = "");
            var absent = Evaluate(workspace).ScenarioSummaries.Single();
            assert(absent.Contains("待补报价来源") && !absent.Contains("最低已核对报价为"), "conditional checks promoted unsupported quotations");
            workspace.Candidates[1].Source = "合成报价单B";
            string one = Evaluate(workspace).ScenarioSummaries.Single();
            assert(one.Contains("最低已核对报价为 B（9000 元）") && one.Contains("待补报价来源：A") && !one.Contains("6000"),
                "the conditional branch used an unsupported cheaper quotation");
            workspace.Candidates[0].Source = "合成报价单A";
            assert(Evaluate(workspace).ScenarioSummaries.Single().Contains("最低已核对报价为 A（6000 元）"),
                "the conditional branch failed to recover after quotation sources were supplied");
        });

        check("Original repeated task success is not quotation evidence", () =>
        {
            var first = MeasurementFixture(8, 9, 10); var second = MeasurementFixture(4, 5, 6);
            var goal = Goal("task"); goal.MeasurementProtocol = first.ProtocolFingerprint; goal.Procedure = first.Recipe.Procedure; goal.Reviewed = true;
            var workspace = Workspace(goal); Attach(workspace, goal); workspace.Candidates.ForEach(c => c.Source = "");
            workspace.Observations[0].Measurement = first; workspace.Observations[1].Measurement = second;
            var result = Evaluate(workspace);
            assert(result.Candidates.All(c => c.State == "pass" && c.Checks.Single().EvidenceLabel == "重复耗时测量达标")
                && result.Summary.Contains("待补报价来源") && !result.Summary.Contains("可少花") && result.Tradeoffs.Length == 0,
                "verified repeated elapsed time was reused as proof of the quoted prices");
        });

        // Reproduces the second audit: required memory still passed after a
        // conditional task was unchecked, but the old 9 -> 5 second comparison remained.
        check("Unreviewed conditional task values cannot remain in price premiums", () =>
        {
            var (workspace, task) = ConditionalTask(); task.Reviewed = false;
            var uncheckedResult = Evaluate(workspace);
            assert(uncheckedResult.Candidates.All(c => c.State == "pass")
                && uncheckedResult.Candidates.All(c => c.Checks.Single(k => k.Name == task.Name).State == "unknown")
                && !uncheckedResult.Tradeoffs.Single().Contains("9 → 5"), "unchecked conditional task values survived as an apparent upgrade benefit");
            task.Reviewed = true;
            string restored = Evaluate(workspace).Tradeoffs.Single();
            assert(restored.Contains("9 → 5 秒") && restored.Contains("（条件：接到视频项目）"),
                "rechecking the unchanged task failed to restore its explicitly conditional comparison");
        });

        check("Refreshing observation proof cannot replace a stale target review signature", () =>
        {
            var (workspace, task) = ConditionalTask(); task.Procedure += "，改用另一输入";
            foreach (var observation in workspace.Observations)
                observation.ProofStamp = CandidateComparison.ObservationStamp(workspace.Candidates.Single(c => c.Id == observation.CandidateId), task);
            assert(task.Reviewed && task.ReviewStamp != CandidateComparison.CriterionStamp(task)
                && !Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5"), "new observation proofs bypassed the target's stale review signature");
            task.Reviewed = true;
            assert(Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5 秒"), "explicitly reviewing the updated target did not recover its comparison");
            workspace.Observations[0].Comparable = false;
            assert(!Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5"), "target review bypassed observation comparability");
            workspace.Observations[0].Comparable = true; workspace.Observations[0].ProofStamp = "stale";
            assert(!Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5"), "target review bypassed candidate observation proof");
        });

        check("Reviewed conditional repeated comparisons still enforce protocol and original displayed values", () =>
        {
            var (workspace, task) = ConditionalTask(); var first = MeasurementFixture(8, 9, 10); var second = MeasurementFixture(4, 5, 6);
            task.Procedure = first.Recipe.Procedure; task.MeasurementProtocol = first.ProtocolFingerprint; task.Reviewed = true;
            workspace.Observations[0].Measurement = first; workspace.Observations[1].Measurement = second;
            foreach (var observation in workspace.Observations)
                observation.ProofStamp = CandidateComparison.ObservationStamp(workspace.Candidates.Single(c => c.Id == observation.CandidateId), task);
            assert(Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5 秒"), "valid original repeated evidence lost its conditional comparison");
            workspace.Observations[1].Measurement = second with { Recipe = second.Recipe with { SoftwareVersion = "different-version" } };
            assert(!Evaluate(workspace).Tradeoffs.Single().Contains("9 → 5"), "goal review allowed a mismatched repeated protocol");
            workspace.Observations[1].Measurement = second; workspace.Observations[1].Value = 4;
            assert(!Evaluate(workspace).Tradeoffs.Single().Contains("9 → 4"), "edited displayed median replaced original repeated results");
        });

        check("HTML retains raw quotes with missing-source notices and omits unreviewed task premiums", () =>
        {
            var goal = Goal(); var workspace = Workspace(goal); Attach(workspace, goal);
            workspace.Candidates[0].Name = "A<script>"; workspace.Candidates.ForEach(c => c.Source = "");
            string quotes = CandidateComparison.Html(workspace, Evaluate(workspace));
            assert(quotes.Contains("待补报价来源") && quotes.Contains("6000 元") && quotes.Contains("9000 元")
                && quotes.Contains("A&lt;script&gt;") && !quotes.Contains("A<script>") && !quotes.Contains("可少花"),
                "HTML hid raw quotation provenance, invented savings or skipped encoding");
            var (conditional, task) = ConditionalTask(); task.Reviewed = false;
            string uncheckedHtml = CandidateComparison.Html(conditional, Evaluate(conditional));
            assert(!uncheckedHtml.Contains("9 → 5"), "HTML retained an unreviewed task as a quoted upgrade benefit");
            task.Reviewed = true;
            string reviewedHtml = CandidateComparison.Html(conditional, Evaluate(conditional));
            assert(reviewedHtml.Contains("9 → 5 秒") && reviewedHtml.Contains("条件：接到视频项目"),
                "HTML lost the reviewed task result or its trigger condition");
        });
    }
}
