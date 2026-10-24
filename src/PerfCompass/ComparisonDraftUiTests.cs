using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunComparisonDraftUiTests(string output, Action<bool, string> require,
        Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (!paused) Click(PauseButton);
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var view = ComparisonPage;
        string originalPath = Path.Combine(store.Root, "comparison.json");
        string exportPath = Path.Combine(output, "recovered-draft.json");
        string importPath = Path.Combine(output, "initial-draft.json");
        var oldImport = view.DraftImportPathProvider; var oldExport = view.DraftExportPathProvider;
        var oldDiscard = DiscardUnsavedComparisonOnExit;
        FileStream? obstruction = null;
        bool finalExitPrepared = false;
        async Task Capture(string name)
        {
            view.SaveDraftButton.BringIntoView(); UpdateLayout(); await screenshot(name);
            var root = (FrameworkElement)Content;
            var bounds = view.DraftStatus.TransformToAncestor(root).TransformBounds(new Rect(view.DraftStatus.RenderSize));
            require(view.DraftStatus.IsVisible && bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight
                && view.SaveDraftButton.IsVisible && view.ExportDraftButton.IsEnabled, "Draft state and recovery controls render within the visible window: " + name);
        }
        try
        {
            require(view.Workspace.Candidates.Count == 0 && !File.Exists(originalPath) && !view.HasUnsavedDraft,
                "Draft flow starts from the native comparison entry with empty isolated data");
            Click(view.AddCandidateButton);
            await EditComparisonCell(view.CandidatesGrid, 0, 0, "自动保存的合成候选");
            await EditComparisonCell(view.CandidatesGrid, 0, 1, 6200);
            Click(view.AddCriterionButton);
            await EditComparisonCell(view.CriteriaGrid, 0, 1, "尚未核对的合成任务");
            await wait(() => File.Exists(originalPath) && !view.HasUnsavedDraft);
            require(CandidateComparison.Load(store.Root).Candidates[0].Name == "自动保存的合成候选" && view.Result == null
                && !view.CoverageConfirm.IsChecked.GetValueOrDefault(), "Native edits autosave an unfinished draft without creating a result or confirmation");
            await Capture("draft-autosaved");
            var typingGrid = view.CandidatesGrid; var typingItem = typingGrid.Items[0]; var typingColumn = typingGrid.Columns[0];
            typingGrid.CurrentCell = new(typingItem, typingColumn); typingGrid.ScrollIntoView(typingItem, typingColumn); typingGrid.UpdateLayout();
            if (!typingGrid.BeginEdit()) throw new Exception("Could not begin autosave editing continuity check.");
            var typingEditor = (TextBox)typingColumn.GetCellContent(typingItem); typingEditor.Text = "自动保存的合成候选";
            await Task.Delay(1600);
            require(ReferenceEquals(typingEditor, typingColumn.GetCellContent(typingItem)) && view.HasUnsavedDraft
                && view.DraftStatus.Text.Contains("仍在编辑"), "Autosave waits for a real active editor without ending typing or clearing unsaved protection");
            typingGrid.CommitEdit(DataGridEditingUnit.Cell, true); typingGrid.CommitEdit(DataGridEditingUnit.Row, true);
            await wait(() => !view.HasUnsavedDraft);
            require(CandidateComparison.Load(store.Root).Candidates[0].Name == "自动保存的合成候选", "Autosave resumes after the active cell is committed");
            await EditComparisonCell(view.CandidatesGrid, 0, 5, "合成 CPU（非商品）");
            Click(view.SaveDraftButton);
            require(!view.HasUnsavedDraft && CandidateComparison.Load(store.Root).Candidates[0].Cpu == "合成 CPU（非商品）", "Explicit draft save works independently of comparing");
            view.DraftExportPathProvider = () => importPath; Click(view.ExportDraftButton);
            require(CandidateComparison.LoadFile(importPath).Candidates.Count == 1 && view.Result == null, "Draft export uses the native button and retains incomplete task evidence");
            Click(view.CompareButton); view.UpdateLayout();
            require(view.Result?.Candidates.Single().State == "unknown" && !view.HasUnsavedDraft, "Saving and comparing still preserves unknown conditions");
            var result = view.Result; view.CandidatesGrid.BringIntoView(); await Task.Delay(100); view.UpdateLayout();
            require(ReferenceEquals(result, view.Result), "Autosave controls and display layout do not erase a computed comparison");
            byte[] previous = File.ReadAllBytes(originalPath);
            obstruction = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await EditComparisonCell(view.CandidatesGrid, 0, 0, "保存失败后仍保留的候选");
            await wait(() => view.DraftStatus.Text.Contains("保存失败"));
            require(view.HasUnsavedDraft && File.ReadAllBytes(originalPath).SequenceEqual(previous) && view.Result == null,
                "A real locked target causes autosave failure while preserving both old disk data and edited UI content");
            view.DraftExportPathProvider = () => output; Click(view.ExportDraftButton);
            require(view.HasUnsavedDraft && view.DraftStatus.Text.Contains("导出失败"), "A failed alternate export does not clear unsaved protection");
            view.DraftImportPathProvider = () => importPath; Click(view.ImportDraftButton);
            require(view.HasUnsavedDraft && view.Workspace.Candidates[0].Name == "保存失败后仍保留的候选" && view.DraftStatus.Text.Contains("导入已停止"),
                "Import cannot replace the only unsaved comparison when its normal save fails");
            int exitQuestions = 0; DiscardUnsavedComparisonOnExit = () => { exitQuestions++; return false; };
            Exit(); await wait(() => !exitPending);
            require(exitQuestions == 1 && !exiting && !stop.IsCancellationRequested && view.HasUnsavedDraft
                && view.Visibility == Visibility.Visible && StatusLine.Text.Contains("草稿已保留"), "The actual application exit path defaults to preserving an unsaved draft");
            await Capture("draft-exit-protected");
            view.DraftExportPathProvider = () => exportPath; Click(view.ExportDraftButton);
            require(!view.HasUnsavedDraft && CandidateComparison.LoadFile(exportPath).Candidates[0].Name == "保存失败后仍保留的候选"
                && File.ReadAllBytes(originalPath).SequenceEqual(previous), "A successful alternate export protects the unique edited draft without changing the blocked original");
            await EditComparisonCell(view.CandidatesGrid, 0, 6, "导出后继续修改");
            require(view.HasUnsavedDraft, "Any edit after export requires a new save or export");
            view.DraftImportPathProvider = () => null; Click(view.ImportDraftButton);
            require(view.HasUnsavedDraft && view.Workspace.Candidates[0].Gpu == "导出后继续修改", "Cancelling the import picker keeps the unique draft");
            string bad = Path.Combine(output, "invalid-draft.json"); File.WriteAllText(bad, "{broken");
            view.DraftImportPathProvider = () => bad; Click(view.ImportDraftButton);
            require(view.HasUnsavedDraft && view.Workspace.Candidates[0].Gpu == "导出后继续修改" && File.ReadAllText(bad) == "{broken", "Malformed import keeps current edits and the damaged original");
            obstruction.Dispose(); obstruction = null;
            Click(view.SaveDraftButton);
            require(!view.HasUnsavedDraft && CandidateComparison.Load(store.Root).Candidates[0].Gpu == "导出后继续修改"
                && Directory.GetFiles(store.Root, "comparison.json.*.tmp").Length == 0, "Retry after the real lock is released saves current edits and leaves no owned temporary");
            view.CoverageConfirm.IsChecked = true; Click(view.SaveDraftButton);
            view.DraftImportPathProvider = () => importPath; Click(view.ImportDraftButton);
            require(view.HasUnsavedDraft && view.Workspace.Candidates[0].Name == "自动保存的合成候选"
                && view.CoverageConfirm.IsChecked == false && view.Result == null, "Import through the real button resets coverage and displayed results");
            // Leave an invalid numeric editor open, as a user may do when closing.
            var grid = view.CandidatesGrid; var item = grid.Items[0]; var column = grid.Columns[1];
            grid.SelectedItem = item; grid.CurrentCell = new(item, column); grid.ScrollIntoView(item, column); grid.UpdateLayout();
            require(grid.BeginEdit(), "Numeric draft editing begins through the real candidate cell");
            var editor = (TextBox)column.GetCellContent(item); editor.Text = "不是数字";
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); Click(view.SaveDraftButton);
            require(view.HasUnsavedDraft && view.DraftStatus.Text.Contains("格式不正确") && editor.Text == "不是数字", "Invalid numeric text stays in the editor and cannot be silently dropped by saving");
            Exit(); await wait(() => !exitPending);
            require(exitQuestions == 2 && !exiting && editor.Text == "不是数字", "Actual exit also preserves uncommitted invalid cell text");
            editor.Text = "6800"; editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); Click(view.SaveDraftButton);
            require(!view.HasUnsavedDraft && CandidateComparison.Load(store.Root).Candidates[0].PriceCny == 6800, "Correcting the same invalid editor restores normal draft saving");
            await Capture("draft-recovered");
            // Final valid editor remains uncommitted; outer RunUiTests calls the real Exit.
            column = grid.Columns[0]; grid.CurrentCell = new(item, column); grid.ScrollIntoView(item, column); grid.UpdateLayout();
            require(grid.BeginEdit(), "Final exit test begins a real uncommitted text edit");
            editor = (TextBox)column.GetCellContent(item); editor.Text = "退出时提交的合成候选";
            File.WriteAllText(Path.Combine(output, "expected-exit-draft.json"), JsonSerializer.Serialize(new { DataRoot = store.Root, Name = editor.Text, PriceCny = 6800 }));
            require(view.HasUnsavedDraft && !exiting, "An outstanding valid editor reaches the real final exit save path");
            finalExitPrepared = true;
        }
        finally
        {
            obstruction?.Dispose();
            view.DraftImportPathProvider = oldImport; view.DraftExportPathProvider = oldExport;
            // Never silently discard a failed test's only state during cleanup.
            DiscardUnsavedComparisonOnExit = () => false;
            if (!finalExitPrepared && view.HasUnsavedDraft && !view.TrySaveDraft())
            {
                view.DraftExportPathProvider = () => Path.Combine(output, "emergency-draft-" + Guid.NewGuid().ToString("N") + ".json");
                Click(view.ExportDraftButton); view.DraftExportPathProvider = oldExport;
            }
            if (!view.HasUnsavedDraft) DiscardUnsavedComparisonOnExit = oldDiscard;
        }
    }
}
