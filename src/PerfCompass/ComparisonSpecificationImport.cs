using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class ComparisonView
{
    private void BindSpecificationDraft()
    {
        var d = workspace.SpecificationDraft;
        SpecSourceBox.Text = d.Source; SpecTextBox.Text = d.Text;
        SpecNameBox.Text = d.Name; SpecCpuBox.Text = d.Cpu; SpecGpuBox.Text = d.Gpu;
        SpecMemoryBox.Text = d.Memory; SpecVramBox.Text = d.Vram; SpecStorageBox.Text = d.Storage;
        SpecificationState();
    }
    private void SpecificationState()
    {
        var d = workspace.SpecificationDraft;
        bool current = SpecificationImport.Current(d);
        SpecPreviewPanel.Visibility = d.ParsedStamp.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpecAddButton.IsEnabled = current;
        SpecPreviewButton.Content = d.ParsedStamp.Length > 0 ? "重新生成预览（替换下方字段）" : "生成规格预览";
        if (d.ParsedStamp.Length == 0) SpecImportStatus.Text = "草稿随候选比较保存；规格来源不充当报价来源。";
        else if (!current) SpecImportStatus.Text = "原文或来源已修改；旧预览字段仍保留，重新生成预览会替换它们。";
        else
        {
            var questions = SpecificationImport.Parse(d.Text).Questions;
            SpecImportStatus.Text = $"本机摘录于 {d.RecordedAt:yyyy-MM-dd HH:mm}；请核对具体配置。"
                + (questions.Length > 0 ? " " + string.Join(" ", questions) : " 未识别的字段不自动填写。")
                + " 报价留空，后续在候选表填写报价与其来源。";
        }
    }
    private void SpecInputChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || !loaded || SpecAddButton == null) return;
        workspace.SpecificationDraft.Source = SpecSourceBox.Text;
        workspace.SpecificationDraft.Text = SpecTextBox.Text;
        SpecificationState(); MarkDraftDirty();
    }
    private void SpecFieldsChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || !loaded || SpecStorageBox == null) return;
        var d = workspace.SpecificationDraft;
        d.Name = SpecNameBox.Text; d.Cpu = SpecCpuBox.Text; d.Gpu = SpecGpuBox.Text;
        d.Memory = SpecMemoryBox.Text; d.Vram = SpecVramBox.Text; d.Storage = SpecStorageBox.Text;
        MarkDraftDirty();
    }
    private void SpecPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var d = workspace.SpecificationDraft;
            if (string.IsNullOrWhiteSpace(d.Source)) throw new ArgumentException("请先填写规格资料来源。");
            var p = SpecificationImport.Parse(d.Text);
            string N(double? n) => n?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
            d.Name = p.Name; d.Cpu = p.Cpu; d.Gpu = p.Gpu; d.Memory = N(p.MemoryGb); d.Vram = N(p.VramGb); d.Storage = N(p.StorageGb);
            d.RecordedAt = DateTimeOffset.Now; d.ParsedStamp = SpecificationImport.Stamp(d.Source, d.Text);
            updating = true;
            try { BindSpecificationDraft(); } finally { updating = false; }
            MarkDraftDirty();
        }
        catch (ArgumentException ex) { SpecImportStatus.Text = ex.Message; }
    }
    private void SpecAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        if (workspace.Candidates.Count >= 3) { SpecImportStatus.Text = "最多比较三套配置；规格草稿仍保留。"; return; }
        try
        {
            var candidate = SpecificationImport.Candidate(workspace.SpecificationDraft);
            workspace.Candidates.Add(candidate);
            workspace.SpecificationDraft = new();
            Invalidate(); Bind();
            ComparisonStatus.Text = "已添加核对后的规格候选；报价、条件核对与任务结果仍单独填写。";
        }
        catch (ArgumentException ex) { SpecImportStatus.Text = ex.Message; }
    }
}
