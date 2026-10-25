using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunProductPageUiTests(string output, Action<bool, string> require, Func<Func<bool>, Task> wait)
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        async Task Ready(Func<bool> predicate)
        {
            for (int i = 0; i < 650 && !predicate(); i++) await Task.Delay(50);
            if (!predicate()) throw new TimeoutException("Product preview did not reach its expected state.");
        }
        async Task Capture(ProductPageWindow window, string name)
        {
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            // The root has a 24-DIP parent margin. A VisualBrush normalizes that
            // offset, so the bitmap does not crop its right and bottom controls.
            var surface = new DrawingVisual();
            using (var drawing = surface.RenderOpen()) drawing.DrawRectangle(new VisualBrush(root), null, new Rect(root.RenderSize));
            bitmap.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        if (!paused) Click(PauseButton);
        Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
        var view = ComparisonPage; var factory = view.ProductPageFactory;
        bool live = Environment.GetCommandLineArgs().Contains("--ui-test-product-page-live");
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), live
            ? "Four explicit live HTTPS reads cover three official Dell SKUs. Intervening races, module layouts and modified quotations use labelled controlled input. No production history or candidate-machine performance measurement is used."
            : "Uses a recorded 2026-10-25 official SKU/Offer and selected technical rows, followed by controlled cancellation/failure and manual-price edits. This is not current stock or candidate performance verification. Independent empty data root only.");
        ProductPagePreview? firstPreview = null;
        void Open(Func<ProductPageWindow, Task> drive, Func<string, CancellationToken, Task<ProductPagePreview>> reader)
        {
            Exception? failure = null;
            view.ProductPageFactory = () =>
            {
                var window = new ProductPageWindow { Reader = reader, ConfirmDiscard = () => true };
                window.Loaded += async (_, _) =>
                {
                    try { await drive(window); }
                    catch (Exception error) { failure = error; window.ConfirmDiscard = () => true; window.Close(); }
                };
                return window;
            };
            Click(view.ImportProductPageButton);
            if (failure != null) throw failure;
        }
        Task<ProductPagePreview> Recorded(string _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(ProductPageImport.Parse(SelfTests.RecordedProductPage(), new Uri(SelfTests.RecordedProductUrl), DateTimeOffset.Now)); }
        try
        {
            Open(async window =>
            {
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl; Click(window.ReadPageButton);
                await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible || window.ProductStatus.Text.StartsWith("读取未完成"));
                require(window.ProductPreviewPanel.Visibility == Visibility.Visible && window.ProductCpuBox.Text.Contains("322")
                    && window.ProductMemoryBox.Text == "16" && window.ProductStorageBox.Text == "1000" && window.ProductVramBox.Text == "",
                    "Actual product controls show the selected SKU configuration without inventing dedicated VRAM or using option-list memory");
                require(firstPreview?.PriceCny > 0 && window.ProductPriceBox.Text == firstPreview.PriceCny.Value.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture)
                    && !window.ProductAddButton.IsEnabled, "Page quotation is visible but adding requires explicit product review");
                File.WriteAllText(Path.Combine(output, "first-product-read.json"), JsonSerializer.Serialize(firstPreview, new JsonSerializerOptions { WriteIndented = true }));
                await Capture(window, "product-selected-configuration");
                window.ProductReviewed.IsChecked = true; require(window.ProductAddButton.IsEnabled, "Explicit review enables the actual candidate add control");
                string original = window.ProductNameBox.Text; window.ProductNameBox.Text += " edited";
                require(window.ProductReviewed.IsChecked != true && !window.ProductAddButton.IsEnabled, "A real field edit withdraws the old product review");
                window.ConfirmDiscard = () => false; window.Close();
                require(window.IsVisible && window.ProductNameBox.Text.EndsWith("edited"), "Closing an edited product preview defaults to preserving its unique content");
                Exit();
                require(!exiting && !exitPending && window.IsVisible && window.ProductNameBox.Text.EndsWith("edited")
                    && window.ProductStatus.Text.Contains("再退出"), "The actual global exit path keeps an active product preview and its edits before stopping any collection or saving old candidate state");
                window.ProductNameBox.Text = original; window.ProductReviewed.IsChecked = true;
                window.ProductReviewed.BringIntoView(); window.UpdateLayout(); await Capture(window, "product-quote-review");
                Click(window.ProductAddButton);
            }, async (url, token) => firstPreview = live ? await ProductPageImport.ReadAsync(url, token) : await Recorded(url, token));
            require(view.Workspace.Candidates.Count == 1 && view.Workspace.Candidates[0].PriceCny == firstPreview!.PriceCny
                && view.Workspace.Candidates[0].Source.Contains(firstPreview.Sku) && view.Workspace.Candidates[0].SpecificationSource.Contains("Processor:")
                && view.CoverageConfirm.IsChecked != true && view.Workspace.Observations.Count == 0,
                "Actual import adds a sourced quote and specification candidate without task outcomes or coverage confirmation");
            await wait(() => view.Triage?.Cases.FirstOrDefault()?.Candidates.Any(c => c.Id == view.Workspace.Candidates[0].Id) == true);
            var importedTriage = view.Triage!.Cases[0].Candidates.Single(c => c.Id == view.Workspace.Candidates[0].Id);
            require(importedTriage.Name.Contains(firstPreview!.Sku) && importedTriage.Quote.Contains("有出处")
                && importedTriage.Pending.Any(p => p.Contains("实际任务")) && view.Workspace.Criteria.Count == 0,
                "An imported real SKU automatically reaches candidate triage with its sourced quote while actual task performance remains pending");

            var old = new TaskCompletionSource<ProductPagePreview>(); var newer = new TaskCompletionSource<ProductPagePreview>();
            Open(async window =>
            {
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl + "?read=old"; Click(window.ReadPageButton);
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl + "?read=new"; Click(window.ReadPageButton);
                old.SetResult(firstPreview! with { Name = "Late old response" }); await Task.Delay(50);
                require(window.ProductPreviewPanel.Visibility != Visibility.Visible && !window.ReadPageButton.IsEnabled,
                    "A cancelled late read cannot publish an old product or re-enable controls during a newer request");
                newer.SetResult(firstPreview! with { Name = "Controlled newer response" });
                await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible);
                require(window.ProductNameBox.Text.StartsWith("Controlled newer response"), "Only the latest completed product request publishes its preview");
                window.Close();
            }, (url, _) => url.EndsWith("old", StringComparison.Ordinal) ? old.Task : newer.Task);

            Open(async window =>
            {
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl; Click(window.ReadPageButton);
                await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible);
                window.ProductUrlBox.Text += "?changed=1"; window.ProductUrlBox.Text = SelfTests.RecordedProductUrl;
                window.ProductReviewed.IsChecked = true;
                require(!window.ProductAddButton.IsEnabled, "Returning to an old URL does not silently restore a stale preview permission");
                window.Reader = (_, _) => throw new IOException("Controlled page read failure"); Click(window.ReadPageButton);
                await Ready(() => window.ReadPageButton.IsEnabled); window.ProductReviewed.IsChecked = true;
                require(!window.ProductAddButton.IsEnabled && window.ProductStatus.Text.Contains("读取未完成"), "A failed re-read keeps original fields but cannot add them as a fresh product");
                window.Reader = Recorded; Click(window.ReadPageButton); await Ready(() => window.ProductStatus.Text.StartsWith("页面资料"));
                window.ProductReviewed.IsChecked = true; Click(window.ProductAddButton);
                require(window.IsVisible && window.ProductStatus.Text.Contains("名称已存在"), "A duplicate product name keeps the editable preview instead of dropping it or creating an invalid saved draft");
                window.ProductNameBox.Text = "手填报价检查（合成修改）"; window.ProductPriceBox.Text = "1";
                window.ProductReviewed.IsChecked = true; Click(window.ProductAddButton);
            }, Recorded);
            var edited = view.Workspace.Candidates.Last();
            require(edited.PriceCny == 1 && edited.Source == "" && edited.SpecificationSource.Contains("页面报价参考"),
                "A manually replaced quote is retained without inheriting the original page price source");
            await wait(() => view.Triage?.Cases[0].Candidates.Any(c => c.Id == edited.Id) == true);
            require(view.Triage!.Cases[0].Candidates.Single(c => c.Id == edited.Id).Quote.Contains("来源待补"),
                "Automatic triage exposes the edited quote's missing source instead of presenting it as an evidenced cheap option");
            Click(view.SaveDraftButton); await Ready(() => CandidateComparison.Load(store.Root).Candidates.Count == 2);
            var saved = CandidateComparison.Load(store.Root);
            require(saved.Candidates[0].Source == view.Workspace.Candidates[0].Source && saved.Candidates[0].SpecificationSource == view.Workspace.Candidates[0].SpecificationSource
                && saved.Candidates[1].Source == "", "Actual draft save and reload preserve product provenance and the missing-source boundary");
            var report = new AnalysisReport(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
            saved.ContextStamp = CandidateComparison.Stamp(report); saved.CoverageConfirmed = true;
            saved.Criteria = [new() { Name = "合成入口预算，不是实际购机预算", Metric = "price", Direction = "max", Target = 20000, Unit = "元", Reviewed = true }];
            var comparison = CandidateComparison.Evaluate(saved, report);
            require(comparison.Candidates[0].State == "pass" && comparison.Candidates[1].State == "unknown"
                && comparison.Shortlists[0].Options.All(o => o.CandidateId != edited.Id),
                "Budget evaluation can use the reviewed page quote but cannot recommend an edited cheaper amount without its own evidence");
            File.WriteAllText(Path.Combine(output, "product-comparison.html"), CandidateComparison.Html(saved, comparison));
            string modulePage = SelfTests.RecordedProductPage().Replace("\"offers\":",
                "\"additionalProperty\":[{\"name\":\"CPU\",\"value\":\"Generic CPU help text, not a selected processor\"}],\"offers\":")
                .Replace("16 GB LPDDR5x", "32 GB：2 x 16 GB，DDR5");
            Open(async window =>
            {
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl; Click(window.ReadPageButton);
                await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible);
                require(window.ProductMemoryBox.Text == "32" && window.ProductCpuBox.Text.Contains("322")
                    && !window.ProductCpuBox.Text.Contains("help text"),
                    "Native preview uses the selected CPU and a consistent total plus module layout rather than family help text");
                window.ProductNameBox.Text = "合成模块拆分入口检查（非实际商品配置）";
                await Capture(window, "product-module-layout");
                window.Reader = (_, _) => Task.FromResult(ProductPageImport.Parse(modulePage.Replace("2 x 16 GB", "2 x 8 GB"),
                    new Uri(SelfTests.RecordedProductUrl), DateTimeOffset.Now));
                window.ConfirmDiscard = () => true; Click(window.ReadPageButton);
                await Ready(() => window.ReadPageButton.IsEnabled);
                require(window.ProductMemoryBox.Text.Length == 0 && window.ProductStatus.Text.Contains("容量或配置不明确"),
                    "A contradictory declared total and module count remains unknown in the actual product preview");
                window.Close();
            }, (_, _) => Task.FromResult(ProductPageImport.Parse(modulePage, new Uri(SelfTests.RecordedProductUrl), DateTimeOffset.Now)));
            Open(async window =>
            {
                window.ProductUrlBox.Text = SelfTests.RecordedProductUrl; Click(window.ReadPageButton);
                await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible);
                window.ProductNameBox.Text = "规格修改检查（合成修改）"; window.ProductMemoryBox.Text = "64";
                window.ProductReviewed.IsChecked = true; Click(window.ProductAddButton);
            }, Recorded);
            var modified = view.Workspace.Candidates.Last();
            require(modified.MemoryGb == 64 && modified.PriceCny == 11422.65 && modified.Source.Length == 0,
                "Editing installed capacity cannot attach the unchanged price of the originally selected configuration");
            if (live)
            {
                // Explicit live mode also checks multiple real SKU shapes through
                // the same native read/review/add path. CI remains recorded-only.
                while (view.Workspace.Candidates.Count > 0)
                { view.CandidatesGrid.SelectedItem = view.Workspace.Candidates[0]; Click(view.RemoveCandidateButton); }
                var liveReads = new List<ProductPagePreview>();
                foreach (var item in new[]
                {
                    (Url: "https://www.dell.com/zh-cn/shop/laptop-computers/spd/xps13dx13260/dx13260_reg_03", Memory: "8", Cpu: "320"),
                    (Url: SelfTests.RecordedProductUrl, Memory: "16", Cpu: "322"),
                    (Url: "https://www.dell.com/zh-cn/shop/laptop-computers/spd/alienware18area51aa18250/aa18250_reg_01", Memory: "32", Cpu: "270HX")
                })
                {
                    Open(async window =>
                    {
                        window.ProductUrlBox.Text = item.Url; Click(window.ReadPageButton);
                        await Ready(() => window.ProductPreviewPanel.Visibility == Visibility.Visible || window.ProductStatus.Text.StartsWith("读取未完成"));
                        require(window.ProductPreviewPanel.Visibility == Visibility.Visible && window.ProductMemoryBox.Text == item.Memory
                            && window.ProductCpuBox.Text.Contains(item.Cpu) && liveReads.Last().PriceCny > 0,
                            "Live selected SKU reaches native preview with independently checked CPU and memory: " + new Uri(item.Url).Segments.Last());
                        window.ProductReviewed.IsChecked = true; Click(window.ProductAddButton);
                    }, async (url, token) => { var result = await ProductPageImport.ReadAsync(url, token); liveReads.Add(result); return result; });
                }
                require(view.Workspace.Candidates.Count == 3 && view.Workspace.Candidates.Select(c => c.MemoryGb).SequenceEqual(new double?[] { 8, 16, 32 })
                    && view.Workspace.Candidates.All(c => c.PriceCny > 0 && liveReads.Any(p => c.Source.Contains(p.Sku)))
                    && view.Workspace.Observations.Count == 0 && !view.Workspace.CoverageConfirmed,
                    "Three real SKU candidates retain selected capacities and quote provenance without invented task or coverage evidence");
                await wait(() => view.Triage?.Cases[0].Candidates.Length == 3);
                require(view.Triage!.Cases[0].Candidates.All(c => c.Pending.Any(p => p.Contains("实际任务"))),
                    "All live SKU candidates still require actual-machine task verification after automatic triage");
                File.WriteAllText(Path.Combine(output, "live-product-reads.json"), JsonSerializer.Serialize(liveReads, new JsonSerializerOptions { WriteIndented = true }));
                Click(view.SaveDraftButton);
            }
        }
        finally { view.ProductPageFactory = factory; }
    }
}
