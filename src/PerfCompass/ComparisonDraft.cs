using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PerfCompass;

public partial class ComparisonView
{
    private DispatcherTimer draftSaveTimer = null!;
    private bool draftDirty, exportedDraft;
    internal bool HasUnsavedDraft => draftDirty && !exportedDraft;
    internal Action? DraftPersisted;
    internal Func<string?> DraftExportPathProvider = () =>
    {
        var dialog = new SaveFileDialog { Filter = "比较草稿 JSON|*.json", FileName = "PerfCompass-候选草稿.json" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    internal Func<string?> DraftImportPathProvider = () =>
    {
        var dialog = new OpenFileDialog { Filter = "比较草稿 JSON|*.json", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };
    private void InitializeDraftSaving()
    {
        draftSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.25) };
        draftSaveTimer.Tick += (_, _) =>
        {
            draftSaveTimer.Stop();
            if (new[] { CandidatesGrid, CriteriaGrid, ObservationsGrid }.Any(HasEditingCell))
            {
                DraftStatus.Text = "当前单元格仍在编辑；结束编辑后自动保存。";
                draftSaveTimer.Start(); return;
            }
            TrySaveDraft();
        };
    }
    private static bool HasEditingCell(DependencyObject parent)
    {
        if (parent is System.Windows.Controls.DataGridCell { IsEditing: true }) return true;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (HasEditingCell(System.Windows.Media.VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }
    private void MarkDraftDirty()
    {
        if (updating || !loaded) return;
        draftDirty = true; exportedDraft = false;
        DraftStatus.Text = "草稿有修改，正在等待自动保存。";
        draftSaveTimer.Stop(); draftSaveTimer.Start();
    }
    internal bool TrySaveDraft(bool force = false)
    {
        if (!loaded || !force && !draftDirty) return true;
        draftSaveTimer.Stop();
        if (loadFailed || store == null)
        { DraftStatus.Text = "原比较文件无法读取，禁止覆盖。请导出当前草稿到其他文件。"; return false; }
        if (!Commit())
        { DraftStatus.Text = "草稿尚未保存：请修正表格中格式不正确的数值，编辑内容仍保留在页面。"; return false; }
        try
        {
            workspace.CoverageConfirmed = CoverageConfirm.IsChecked == true;
            CandidateComparison.Save(store.Root, workspace);
            draftSaveTimer.Stop(); draftDirty = false; exportedDraft = false;
            DraftStatus.Text = "草稿已保存 · " + DateTimeOffset.Now.ToString("HH:mm:ss");
            DraftPersisted?.Invoke();
            return true;
        }
        catch (ArgumentException e) { DraftStatus.Text = "草稿尚未保存：" + e.Message; }
        catch (Exception e) { store.Log(e); DraftStatus.Text = "草稿保存失败，编辑内容仍在页面。请重试或导出到其他目录。"; }
        return false;
    }
    internal bool SaveDraftForExit() => !loaded || TrySaveDraft() || !HasUnsavedDraft;
    private void SaveDraft_Click(object sender, RoutedEventArgs e) => TrySaveDraft(force: true);
    private void ExportDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!loaded || !Commit()) return;
        string? path = DraftExportPathProvider(); if (path == null) return;
        // A blocked original must not be overwritten through the export picker.
        if (store != null && System.IO.Path.GetFullPath(path).Equals(System.IO.Path.Combine(store.Root, "comparison.json"), StringComparison.OrdinalIgnoreCase))
        { DraftStatus.Text = "请另选文件导出；保存到当前数据目录请使用“保存草稿”。"; return; }
        try
        {
            workspace.CoverageConfirmed = CoverageConfirm.IsChecked == true;
            CandidateComparison.SaveFile(path, workspace);
            draftSaveTimer.Stop(); exportedDraft = true;
            DraftStatus.Text = "草稿已导出。重启后可通过“导入草稿”打开；本地保存失败时也可安全退出。";
            DraftPersisted?.Invoke();
        }
        catch (ArgumentException ex) { DraftStatus.Text = "草稿未导出：" + ex.Message; }
        catch (Exception ex) { store?.Log(ex); DraftStatus.Text = "草稿导出失败，编辑内容仍在页面。请选择可写目录重试。"; }
    }
    private void ImportDraft_Click(object sender, RoutedEventArgs e)
    {
        string? path = DraftImportPathProvider(); if (path == null) return;
        PurchaseWorkspace imported;
        try { imported = CandidateComparison.LoadFile(path); }
        catch (Exception ex) { store?.Log(ex); DraftStatus.Text = "草稿文件无法读取或内容无效，当前编辑内容和原文件均保留。"; return; }
        if (!TrySaveDraft() && HasUnsavedDraft)
        { DraftStatus.Text = "当前草稿尚未保存，导入已停止。请先修正并保存，或导出到其他文件。"; return; }
        workspace = imported; workspace.CoverageConfirmed = false;
        DropResult(); Bind(); MarkDraftDirty();
        if (context != null) RefreshContextStatus(context);
        DraftStatus.Text = "草稿已导入，覆盖确认已清空。请核对当前需求、候选和来源后重新比较。";
    }
}
