using System.IO;
using System.Net;
using System.Net.Http;

namespace PerfCompass;

public static partial class SelfTests
{
    internal const string RecordedProductUrl = "https://www.dell.com/zh-cn/shop/laptop-computers/spd/dell14sds14260/ds14260_reg_02";
    internal static string RecordedProductPage()
    {
        using var stream = typeof(SelfTests).Assembly.GetManifestResourceStream("PerfCompass.ProductPageDell.html")!;
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static void RunProductPageImportTests(Action<string, Action> check, Action<bool, string> assert)
    {
        string html = RecordedProductPage(); var url = new Uri(RecordedProductUrl);
        var when = DateTimeOffset.Parse("2026-10-25T10:00:00+08:00");
        ProductPagePreview Parse(string page) => ProductPageImport.Parse(page, url, when);
        bool Reject(string page) { try { Parse(page); return false; } catch (ArgumentException) { return true; } }
        check("Recorded official product SKU binds a fixed CNY offer and selected specifications rather than configurable alternatives", () =>
        {
            var p = Parse(html); var spec = SpecificationImport.Parse(p.SpecificationText);
            assert(p.Sku == "ds14260_reg_02" && p.PriceCny == 11422.65 && p.Source == url && p.RetrievedAt == when, "recorded offer binding lost");
            assert(spec.Cpu.Contains("322") && spec.MemoryGb == 16 && spec.StorageGb == 1000 && spec.VramGb == null
                && !p.SpecificationText.Contains("128GB"), "all-option or unrelated memory was used as installed configuration");
        });
        check("Stock currency quote identity and fixed-offer evidence are independent", () =>
        {
            foreach (string page in new[] { html.Replace("\"cny\"", "\"USD\""), html.Replace("schema.org/InStock", "schema.org/OutOfStock"),
                html.Replace("\"price\": \"11422.65\"", "\"price\": \"NaN\""), html.Replace("\"price\": \"11422.65\"", "\"price\": \"0\""),
                html.Replace("\"@type\": \"Offer\"", "\"@type\": \"AggregateOffer\""),
                html.Replace("\"priceCurrency\":", "\"priceValidUntil\":\"2026-10-24\",\"priceCurrency\":") })
                assert(Parse(page).PriceCny == null, "uncertain or unavailable offer became a qualified RMB quotation");
            assert(Reject(html.Replace("\"sku\": \"ds14260_reg_02\"", "\"sku\": \"wrong-sku\"")), "another configuration inherited this quote");
        });
        check("Dell marketing metadata cannot replace or conflict with a SKU's selected technical fields", () =>
        {
            string page = html.Replace("\"offers\":", "\"additionalProperty\":[{\"name\":\"CPU\",\"value\":\"Generic CPU help text, not a selected processor\"},{\"name\":\"Memory\",\"value\":\"128GB optional family upgrade\"}],\"offers\":")
                .Replace("16 GB LPDDR5x", "32 GB：2 x 16 GB，DDR5");
            var p = Parse(page); var spec = SpecificationImport.Parse(p.SpecificationText);
            assert(spec.Cpu.Contains("322") && spec.MemoryGb == 32 && !p.SpecificationText.Contains("Generic CPU")
                && !p.SpecificationText.Contains("128GB"), "family marketing became the selected configuration");
            var missing = Parse(page.Replace("id=tech-spec-container", "id=unavailable-tech-spec"));
            assert(SpecificationImport.Parse(missing.SpecificationText).Cpu.Length == 0
                && SpecificationImport.Parse(missing.SpecificationText).MemoryGb == null, "missing selected specs fell back to family marketing");
        });
        check("Ambiguous malformed and duplicated product fields do not select an arbitrary cheap price", () =>
        {
            assert(Reject(html + html.Replace("11422.65", "999")), "conflicting products selected cheapest");
            assert(Reject(html.Replace("\"price\": \"11422.65\"", "\"price\":\"99\",\"price\":\"11422.65\"")), "duplicate price was silently accepted");
            assert(Reject(html.Replace("application/ld+json>", "application/ld+json>invalid")), "malformed structured source silently accepted");
            assert(Parse(html + html).PriceCny == 11422.65, "identical repeated product blocks changed the interpretation");
        });
        check("Product URL boundary rejects accounts local endpoints and unrelated hosts", () =>
        {
            foreach (string value in new[] { "http://www.dell.com/zh-cn/shop/x", "https://127.0.0.1/x", "file:///C:/secret", "https://www.dell.com.example/zh-cn/shop/x",
                "https://user:pass@www.dell.com/zh-cn/shop/x", "https://www.dell.com:444/zh-cn/shop/x", "https://www.dell.com/zh-cn/shop/x#secret" })
            { bool rejected = false; try { ProductPageImport.ValidateUrl(value); } catch (ArgumentException) { rejected = true; } assert(rejected, "URL boundary accepted " + value); }
        });
        check("HTTP product reads retain bounds redirect validation and cancellation without executing HTML", () =>
        {
            async Task Fails(HttpResponseMessage response)
            {
                using var client = new HttpClient(new ProductResponseHandler(_ => response));
                bool failed = false; try { await ProductPageImport.ReadAsync(url, client, CancellationToken.None); }
                catch (Exception e) when (e is IOException or ArgumentException) { failed = true; }
                assert(failed, "invalid HTTP content was imported");
            }
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://127.0.0.1/secret");
            Fails(redirect).GetAwaiter().GetResult();
            Fails(new(HttpStatusCode.Forbidden)).GetAwaiter().GetResult();
            var large = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', ProductPageImport.MaximumPageBytes + 1)) };
            large.Content.Headers.ContentType = new("text/html"); Fails(large).GetAwaiter().GetResult();
            using var client = new HttpClient(new ProductResponseHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") }));
            assert(ProductPageImport.ReadAsync(url, client, CancellationToken.None).GetAwaiter().GetResult().PriceCny == 11422.65, "HTTP data was not parsed");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool cancelled = false; try { ProductPageImport.ReadAsync(url, client, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled, "cancelled read published a result");
        });
    }
    private sealed class ProductResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
