using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class ProductPageWindow : Window
{
    internal Func<string, CancellationToken, Task<ProductPagePreview>> Reader = ProductPageImport.ReadAsync;
    internal Func<bool> ConfirmDiscard;
    internal Func<PurchaseCandidate, string?> CandidateError = _ => null;
    private ProductPagePreview? preview;
    private string fetchedUrl = "";
    private CancellationTokenSource? reading;
    private int generation;
    private bool updating, dirty, finished, previewCurrent;
    internal PurchaseCandidate? Candidate { get; private set; }
    public ProductPageWindow()
    {
        ConfirmDiscard = () => System.Windows.MessageBox.Show(this, "本次修改尚未添加到候选草稿。放弃这些修改并返回？", "保留商品预览", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        InitializeComponent();
    }
    private void UrlChanged(object sender, TextChangedEventArgs e)
    {
        if (ProductAddButton == null || updating) return;
        generation++; reading?.Cancel(); previewCurrent = false;
        ProductReviewed.IsChecked = false; ProductAddButton.IsEnabled = false;
        if (preview != null) { dirty = true; ProductStatus.Text = "网址已修改，原预览保留；重新读取成功并核对后才能添加。"; }
        ReadPageButton.IsEnabled = true;
    }
    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || ProductReviewed == null) return;
        dirty = true; ProductReviewed.IsChecked = false; UpdateAddState();
    }
    private void ReviewChanged(object sender, RoutedEventArgs e) => UpdateAddState();
    private void UpdateAddState()
    {
        if (ProductAddButton != null) ProductAddButton.IsEnabled = previewCurrent && preview != null && reading == null
            && fetchedUrl == ProductUrlBox.Text.Trim() && ProductReviewed.IsChecked == true;
    }
    private async void ReadPage_Click(object sender, RoutedEventArgs e)
    {
        string url = ProductUrlBox.Text.Trim();
        try { ProductPageImport.ValidateUrl(url); }
        catch (ArgumentException error) { ProductStatus.Text = error.Message; return; }
        if (dirty && !ConfirmDiscard()) return;
        reading?.Cancel(); var operation = new CancellationTokenSource(); reading = operation; int request = ++generation; previewCurrent = false;
        ProductReviewed.IsChecked = false; ReadPageButton.IsEnabled = false; ProductAddButton.IsEnabled = false;
        ProductPreviewPanel.IsEnabled = false; ProductStatus.Text = "正在读取商品页；可以修改网址或返回以取消。";
        try
        {
            var result = await Reader(url, operation.Token);
            if (operation.IsCancellationRequested || request != generation || finished) return;
            var spec = SpecificationImport.Parse(result.SpecificationText);
            updating = true;
            try
            {
                preview = result; fetchedUrl = url; previewCurrent = true;
                ProductNameBox.Text = result.Name + " · " + result.Sku;
                ProductCpuBox.Text = spec.Cpu; ProductGpuBox.Text = spec.Gpu;
                string Number(double? n) => n?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
                ProductMemoryBox.Text = Number(spec.MemoryGb); ProductVramBox.Text = Number(spec.VramGb); ProductStorageBox.Text = Number(spec.StorageGb);
                ProductPriceBox.Text = Number(result.PriceCny);
                ProductIdentity.Text = $"页面 SKU：{result.Sku} · 本机读取 {result.RetrievedAt:yyyy-MM-dd HH:mm}";
                ProductEvidence.Text = Evidence(result);
                ProductPreviewPanel.Visibility = Visibility.Visible;
                ProductStatus.Text = result.Notice + (spec.Questions.Length > 0 ? " " + string.Join(" ", spec.Questions) : "");
                dirty = false;
            }
            finally { updating = false; }
        }
        catch (OperationCanceledException) { if (request == generation && !finished) ProductStatus.Text = "读取已取消或超时，未采用新结果；原预览保留。"; }
        catch (Exception error) when (error is ArgumentException or IOException or System.Net.Http.HttpRequestException or System.Text.DecoderFallbackException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { if (request == generation && !finished) ProductStatus.Text = "读取未完成：" + error.Message + " 可返回使用规格摘录。"; }
        finally
        {
            if (ReferenceEquals(reading, operation)) reading = null;
            operation.Dispose();
            if (!finished && reading == null) { ReadPageButton.IsEnabled = true; ProductPreviewPanel.IsEnabled = true; UpdateAddState(); }
        }
    }
    private static string Evidence(ProductPagePreview p) => $"商品来源：{p.Source}\n本机读取时间：{p.RetrievedAt:O}\n{p.QuoteEvidence}\n规格摘录：\n{p.SpecificationText}";
    private void AddProduct_Click(object sender, RoutedEventArgs e)
    {
        if (preview == null || !ProductAddButton.IsEnabled) return;
        try
        {
            var draft = new SpecificationDraft { Source = preview.Source.AbsoluteUri, Text = preview.SpecificationText,
                Name = ProductNameBox.Text, Cpu = ProductCpuBox.Text, Gpu = ProductGpuBox.Text,
                Memory = ProductMemoryBox.Text, Vram = ProductVramBox.Text, Storage = ProductStorageBox.Text, RecordedAt = preview.RetrievedAt };
            draft.ParsedStamp = SpecificationImport.Stamp(draft.Source, draft.Text);
            var candidate = SpecificationImport.Candidate(draft);
            if (!string.IsNullOrWhiteSpace(ProductPriceBox.Text))
            {
                if (!double.TryParse(ProductPriceBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double price)
                    || !double.IsFinite(price) || price is <= 0 or > 1e12) throw new ArgumentException("报价请输入大于零的人民币元数值；不明确时可留空。");
                // A replaced amount cannot inherit a snapshot of a different quote.
                candidate.PriceCny = price;
                var original = SpecificationImport.Parse(preview.SpecificationText);
                bool sameConfiguration = candidate.Cpu == original.Cpu && candidate.Gpu == original.Gpu
                    && candidate.MemoryGb == original.MemoryGb && candidate.VramGb == original.VramGb && candidate.StorageGb == original.StorageGb;
                candidate.Source = sameConfiguration && preview.PriceCny.HasValue && Math.Abs(preview.PriceCny.Value - price) < .0000001
                    ? $"商品页报价（用户已核对，非结算或配送保证）\n网址：{preview.Source}\n本机读取：{preview.RetrievedAt:O}\n页面 SKU：{preview.Sku}\n页面金额：{price.ToString("0.#########", CultureInfo.InvariantCulture)} CNY\n页面标示有货；原始报价字段见独立规格资料。" : "";
            }
            candidate.SpecificationSource += "\n页面报价参考（不替代独立任务测量）：\n" + preview.QuoteEvidence;
            CandidateComparison.Validate(new PurchaseWorkspace { Candidates = [candidate] });
            if (CandidateError(candidate) is string issue) throw new ArgumentException(issue);
            Candidate = candidate; dirty = false; finished = true; DialogResult = true;
        }
        catch (ArgumentException error) { ProductStatus.Text = error.Message; }
    }
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!finished && dirty && !ConfirmDiscard()) { e.Cancel = true; return; }
        finished = true; generation++; reading?.Cancel();
    }
}
