using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PerfCompass;

public record ProductPagePreview(Uri Source, DateTimeOffset RetrievedAt, string Name, string Sku,
    string SpecificationText, double? PriceCny, string QuoteEvidence, string Notice);

/// <summary>Reads public product metadata as data. Does not execute page scripts or use browser accounts.</summary>
public static class ProductPageImport
{
    public const int MaximumPageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(1);
    private static readonly Regex Scripts = new(@"<script\b(?<attrs>[^>]{0,4000})>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexLimit);
    private static readonly Regex Attributes = new("(?<key>[\\w:-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.CultureInvariant, RegexLimit);
    private static readonly Dictionary<string, string> SpecKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Processor"]="Processor", ["CPU"]="Processor", ["处理器"]="Processor", ["CPU型号"]="Processor",
        ["Graphics"]="Graphics", ["GPU"]="Graphics", ["显卡"]="Graphics", ["显卡类型"]="Graphics",
        ["Memory"]="Memory", ["RAM"]="Memory", ["内存"]="Memory", ["内存容量"]="Memory",
        ["Storage"]="Storage", ["硬盘"]="Storage", ["存储"]="Storage", ["硬盘容量"]="Storage",
        ["VRAM"]="VRAM", ["独立显存"]="VRAM", ["显存"]="VRAM"
    };
    public static Uri ValidateUrl(string text)
    {
        if (text.Length > 1000 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0
            || uri.IdnHost is not ("www.dell.com" or "item.lenovo.com.cn"))
            throw new ArgumentException("请使用 Dell 中国官网或联想商城的 HTTPS 商品链接；其他来源可继续使用规格摘录。网址不能含登录信息、端口或页内定位。");
        if (uri.IdnHost == "www.dell.com" && !uri.AbsolutePath.StartsWith("/zh-cn/shop/", StringComparison.Ordinal))
            throw new ArgumentException("请使用 Dell 中国官网的具体商品页。");
        if (uri.IdnHost == "item.lenovo.com.cn" && !Regex.IsMatch(uri.AbsolutePath, @"^/product/[0-9]+\.html$", RegexOptions.CultureInvariant, RegexLimit))
            throw new ArgumentException("请使用联想商城的具体商品页。");
        return uri;
    }
    public static async Task<ProductPagePreview> ReadAsync(string url, CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PerfCompass/0.8");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return await ReadAsync(ValidateUrl(url), client, deadline.Token).ConfigureAwait(false);
    }
    internal static async Task<ProductPagePreview> ReadAsync(Uri source, HttpClient client, CancellationToken cancellation)
    {
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            ValidateUrl(source.AbsoluteUri);
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirects == 3 || response.Headers.Location == null) throw new IOException("商品页跳转过多或缺少目标；请复制规格文本核对。");
                source = new Uri(source, response.Headers.Location);
                ValidateUrl(source.AbsoluteUri);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new IOException($"商品页返回 HTTP {(int)response.StatusCode}；可复制规格文本继续，不使用旧预览冒充新结果。");
            string? media = response.Content.Headers.ContentType?.MediaType;
            if (media is not ("text/html" or "application/xhtml+xml")) throw new IOException("返回的内容不是 HTML 商品页。");
            if (response.Content.Headers.ContentLength > MaximumPageBytes) throw new IOException("商品页超过 2 MiB，未导入。");
            using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaximumPageBytes) throw new IOException("商品页超过 2 MiB，未导入。");
                bytes.Write(buffer, 0, read);
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            catch (ArgumentException) { throw new IOException("商品页编码暂不支持，请复制规格文本继续。"); }
            return Parse(encoding.GetString(bytes.ToArray()), source, DateTimeOffset.Now);
        }
        throw new IOException("未取得商品页。");
    }
    public static ProductPagePreview Parse(string html, Uri source, DateTimeOffset retrievedAt)
    {
        ValidateUrl(source.AbsoluteUri);
        if (Encoding.UTF8.GetByteCount(html) > MaximumPageBytes) throw new ArgumentException("商品页超过 2 MiB。");
        var products = new List<JsonElement>();
        foreach (Match script in Scripts.Matches(html))
        {
            if (!string.Equals(Attribute(script.Groups["attrs"].Value, "type"), "application/ld+json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(script.Groups["body"].Value, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new ArgumentException("页面的结构化资料无法完整读取，请复制规格文本核对。"); }
            using (document) Collect(document.RootElement);
            if (products.Count > 8) throw new ArgumentException("页面含多套商品，请打开选定 SKU 的独立商品页。");
        }
        var distinct = products.GroupBy(p => p.GetRawText(), StringComparer.Ordinal).Select(g => g.First()).ToArray();
        if (distinct.Length != 1) throw new ArgumentException(distinct.Length == 0 ? "页面未提供可读取的单一商品资料；可复制规格文本继续。" : "页面含多套或互相冲突的商品资料，请打开选定 SKU 的独立商品页。");
        var product = distinct[0]; RejectDuplicateMembers(product);
        string name = OneLine(Text(product, "name")), sku = Text(product, "sku");
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(sku, @"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, RegexLimit) || name.Length + sku.Length + 3 > 150)
            throw new ArgumentException("页面缺少明确的商品名称或 SKU，请复制具体配置后核对。");
        if (source.IdnHost == "www.dell.com" && !source.AbsolutePath.TrimEnd('/').EndsWith("/" + sku, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("页面 SKU 与所选链接不一致，未采用该报价或配置。");
        var lines = new List<string> { "Model: " + name + " · " + sku };
        // Dell's Product.additionalProperty also contains feature-help prose
        // under labels such as "CPU". It describes the family, not this SKU.
        // For Dell only the selected technical-specification rows supply fields.
        if (source.IdnHost != "www.dell.com" && product.TryGetProperty("additionalProperty", out var props) && props.ValueKind == JsonValueKind.Array)
            foreach (var prop in props.EnumerateArray())
                if (SpecKeys.TryGetValue(Text(prop, "name"), out string? key) && Text(prop, "value") is { Length: > 0 } value)
                    lines.Add(key + ": " + OneLine(value));
        // Dell's configurable controls contain all alternatives. Only the selected
        // technical-specification list is associated with this page's SKU.
        if (source.IdnHost == "www.dell.com") lines.AddRange(DellSelectedSpecifications(html));
        string specifications = string.Join("\n", lines.Distinct(StringComparer.Ordinal));
        if (specifications.Length > 6000) throw new ArgumentException("商品规格过长，请改用精简的规格摘录。");
        double? price = null; string quote = "未取得对应 SKU 的固定人民币有货报价。";
        string notice = "页面资料须逐项核对；页面标示有货不保证所在地区可配送或最终结算价。";
        if (product.TryGetProperty("offers", out var offer))
        {
            if (offer.ValueKind == JsonValueKind.Array && offer.GetArrayLength() == 1) offer = offer[0];
            if (offer.ValueKind == JsonValueKind.Object && IsType(offer, "Offer"))
            {
                string amount = Text(offer, "price"), currency = Text(offer, "priceCurrency"), availability = Text(offer, "availability");
                string offerUrl = Text(offer, "url"), validUntil = Text(offer, "priceValidUntil");
                if (amount.Length > 100 || currency.Length > 16 || availability.Length > 200 || offerUrl.Length > 1000 || validUntil.Length > 32)
                    throw new ArgumentException("页面报价字段过长，未采用该资料。");
                bool samePage = Uri.TryCreate(offerUrl, UriKind.Absolute, out var quotedUri) && quotedUri.Scheme == "https"
                    && quotedUri.IdnHost == source.IdnHost && quotedUri.AbsolutePath.TrimEnd('/') == source.AbsolutePath.TrimEnd('/')
                    && quotedUri.Query == source.Query;
                bool current = validUntil.Length == 0 || DateOnly.TryParseExact(validUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                    && end >= DateOnly.FromDateTime(retrievedAt.Date);
                bool inStock = availability is "https://schema.org/InStock" or "http://schema.org/InStock";
                if (currency.Equals("CNY", StringComparison.OrdinalIgnoreCase) && samePage && current && inStock
                    && double.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) && n is > 0 and <= 1e12) price = n;
                quote = $"商品 SKU：{sku}\n页面金额：{amount} {currency}\n页面库存状态：{availability}\n报价对应链接：{offerUrl}\n页面报价有效期：{(validUntil.Length == 0 ? "未标明" : validUntil)}";
                if (!price.HasValue) notice += " 报价的币种、库存、有效期或配置对应关系未齐全，候选报价默认留空；页面原值仍保留。";
            }
            else notice += " 页面是多报价或起价，未选择最低数字作为具体配置报价。";
        }
        return new(source, retrievedAt, name, sku, specifications, price, quote, notice);

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) Collect(child); }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (IsType(element, "Product")) products.Add(element.Clone());
                if (element.TryGetProperty("@graph", out var graph)) Collect(graph);
            }
        }
    }
    private static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "" : "";
    private static bool IsType(JsonElement element, string type)
    {
        if (!element.TryGetProperty("@type", out var value)) return false;
        bool Match(string? text) => text == type || text == "https://schema.org/" + type || text == "http://schema.org/" + type;
        return value.ValueKind == JsonValueKind.String ? Match(value.GetString())
            : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && Match(v.GetString()));
    }
    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
            { if (!names.Add(member.Name)) throw new ArgumentException("商品资料包含重复字段，未猜测应采用哪个值。"); RejectDuplicateMembers(member.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }
    private static string Attribute(string text, string key) => Attributes.Matches(text).Cast<Match>()
        .Where(m => m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(m => WebUtility.HtmlDecode(m.Groups["value"].Value)).FirstOrDefault() ?? "";
    private static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, @"<[^>]*>", " ", RegexOptions.Singleline, RegexLimit)), @"\s+", " ", RegexOptions.CultureInvariant, RegexLimit).Trim();
    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, RegexLimit).Trim();
    private static IEnumerable<string> DellSelectedSpecifications(string html)
    {
        var anchor = Regex.Matches(html, @"<div\b(?<attrs>[^>]{0,2000})>", RegexOptions.IgnoreCase, RegexLimit).Cast<Match>()
            .FirstOrDefault(m => Attribute(m.Groups["attrs"].Value, "id") == "tech-spec-container");
        if (anchor == null) yield break;
        var list = Regex.Matches(html[anchor.Index..], @"<ul\b(?<attrs>[^>]{0,2000})>(?<body>.*?)</ul\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexLimit).Cast<Match>()
            .FirstOrDefault(m => Attribute(m.Groups["attrs"].Value, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("specs"));
        if (list == null) yield break;
        foreach (Match row in Regex.Matches(list.Groups["body"].Value, @"<li\b[^>]*>(?<body>.*?)</li\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexLimit))
        {
            var heading = Regex.Match(row.Groups["body"].Value, @"^\s*<(?<tag>div|h[1-6])\b[^>]*>(?<name>.*?)</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexLimit);
            if (!heading.Success) continue;
            string label = Plain(heading.Groups["name"].Value).TrimEnd(' ', '*');
            if (SpecKeys.TryGetValue(label, out var key))
            {
                string value = Plain(row.Groups["body"].Value[heading.Length..]);
                if (value.Length > 0) yield return key + ": " + value;
            }
        }
    }
}
