using System.Windows;

namespace PerfCompass;

public partial class ComparisonView
{
    internal Func<ProductPageWindow> ProductPageFactory = () => new ProductPageWindow();
    private ProductPageWindow? activeProductPreview;
    internal bool ProductPreviewOpen => activeProductPreview != null;
    internal void FocusProductPreview()
    {
        if (activeProductPreview is not { } dialog) return;
        dialog.ProductStatus.Text = "请先完成添加，或返回候选页处理本次预览，再退出程序。当前内容仍保留。";
        dialog.Activate();
    }
    private void ImportProductPage_Click(object sender, RoutedEventArgs e)
    {
        if (!Commit()) return;
        if (workspace.Candidates.Count >= 3) { ComparisonStatus.Text = "最多比较三套配置；请先删除不再需要的候选。"; return; }
        var dialog = ProductPageFactory(); dialog.Owner = Window.GetWindow(this);
        dialog.CandidateError = candidate => workspace.Candidates.Any(c => c.Name.Trim().Equals(candidate.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "候选名称已存在，请使用不同的备注名称，或返回候选页核对原记录。本次预览仍保留。" : null;
        activeProductPreview = dialog;
        try
        {
            if (dialog.ShowDialog() != true || dialog.Candidate == null) return;
            workspace.Candidates.Add(dialog.Candidate); Invalidate(); Bind();
            ComparisonStatus.Text = "已添加核对后的商品候选；原始页面资料与读取时间已保留。任务表现和条件仍需核对。"
                + (dialog.Candidate.PriceCny.HasValue && string.IsNullOrWhiteSpace(dialog.Candidate.Source) ? " 修改后的报价待补独立来源，暂不参与预算比较。" : "");
        }
        finally { activeProductPreview = null; }
    }
}
