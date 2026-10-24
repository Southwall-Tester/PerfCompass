using System.IO;
using System.Text;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunComparisonDraftTests(Action<string, Action> check, Action<bool, string> assert)
    {
        void WithRoot(Action<string> action)
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-draft-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); } finally { Directory.Delete(root, true); }
        }
        PurchaseWorkspace Draft() => new() { Candidates = [new() { Name = "未填完的候选", MemoryGb = null }],
            Criteria = [new() { Name = "有条件的新任务", Condition = "接到新项目", Metric = "task", Unit = "秒", Target = null }] };
        check("Unfinished comparison draft and conditional unknown values round trip without invented confirmation", () => WithRoot(root =>
        {
            var draft = Draft(); CandidateComparison.Save(root, draft); var saved = CandidateComparison.Load(root);
            assert(saved.Candidates[0].MemoryGb == null && saved.Criteria[0].Target == null && saved.Criteria[0].Condition == "接到新项目"
                && !saved.Criteria[0].Reviewed && !saved.CoverageConfirmed, "unfinished values or confirmation changed");
        }));
        check("Comparison export and import preserve strict bounds and original review stamps", () => WithRoot(root =>
        {
            var draft = Draft(); var goal = draft.Criteria[0]; goal.Target = 120; goal.Direction = "lt"; goal.Reviewed = true;
            var path = Path.Combine(root, "export.json"); CandidateComparison.SaveFile(path, draft); var saved = CandidateComparison.LoadFile(path);
            assert(saved.Version == "comparison-1.2" && saved.Criteria[0].Direction == "lt" && saved.Criteria[0].Reviewed
                && saved.Criteria[0].ReviewStamp == goal.ReviewStamp, "strict target or reviewed evidence changed");
        }));
        check("Failed comparison replacement keeps the previous file and cleans only its own unique temporary", () => WithRoot(root =>
        {
            var draft = Draft(); string path = Path.Combine(root, "comparison.json"); CandidateComparison.Save(root, draft);
            byte[] original = File.ReadAllBytes(path); string foreign = path + ".tmp"; File.WriteAllText(foreign, "unrelated existing temporary");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                draft.Candidates[0].Name = "未保存的修改"; bool failed = false;
                try { CandidateComparison.Save(root, draft); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
                assert(failed && File.ReadAllBytes(path).SequenceEqual(original), "failed replacement damaged previous file");
            }
            assert(Directory.GetFiles(root, "*.tmp").SequenceEqual([foreign]) && File.ReadAllText(foreign) == "unrelated existing temporary", "temporary collision or leftover");
        }));
        check("Comparison path occupied by a directory fails without deleting it or leaking a temporary", () => WithRoot(root =>
        {
            string path = Path.Combine(root, "comparison.json"); Directory.CreateDirectory(path); bool failed = false;
            try { CandidateComparison.Save(root, Draft()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
            assert(failed && Directory.Exists(path) && Directory.GetFiles(root, "*.tmp").Length == 0, "directory replaced or temporary leaked");
            failed = false; try { CandidateComparison.Load(root); } catch (UnauthorizedAccessException) { failed = true; }
            assert(failed, "occupied path silently treated as an empty workspace");
        }));
        check("Invalid comparison export cannot replace an existing good file", () => WithRoot(root =>
        {
            var draft = Draft(); string path = Path.Combine(root, "copy.json"); CandidateComparison.SaveFile(path, draft); byte[] original = File.ReadAllBytes(path);
            draft.Candidates[0].MemoryGb = double.NaN; bool failed = false;
            try { CandidateComparison.SaveFile(path, draft); } catch (ArgumentException) { failed = true; }
            assert(failed && File.ReadAllBytes(path).SequenceEqual(original) && Directory.GetFiles(root, "*.tmp").Length == 0, "validation replaced a good export");
        }));
        check("Comparison imports reject oversize and unsupported files while preserving their original bytes", () => WithRoot(root =>
        {
            string path = Path.Combine(root, "large.json"); using (var file = File.Create(path)) file.SetLength(8388609);
            bool failed = false; try { CandidateComparison.LoadFile(path); } catch (InvalidDataException) { failed = true; }
            assert(failed && new FileInfo(path).Length == 8388609, "oversize import changed or accepted");
            string text = "{\"Version\":\"comparison-9.0\"}"; File.WriteAllText(path, text); failed = false;
            try { CandidateComparison.LoadFile(path); } catch (ArgumentException) { failed = true; }
            assert(failed && File.ReadAllText(path) == text, "future version accepted or changed");
        }));
        check("Comparison import supports UTF8 BOM and exports never execute embedded task text", () => WithRoot(root =>
        {
            var draft = Draft(); string marker = Path.Combine(root, "must-not-run.txt");
            draft.Criteria[0].Procedure = "cmd /c echo unexpected > " + marker;
            string path = Path.Combine(root, "bom.json"); CandidateComparison.SaveFile(path, draft);
            string text = File.ReadAllText(path); File.WriteAllText(path, text, new UTF8Encoding(true));
            assert(CandidateComparison.LoadFile(path).Criteria[0].Procedure == draft.Criteria[0].Procedure && !File.Exists(marker), "BOM or data-only import changed");
        }));
    }
}
