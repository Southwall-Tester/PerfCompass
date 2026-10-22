using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public sealed class ModelRequirementExtractor(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient client = handler == null ? new(new HttpClientHandler { AllowAutoRedirect = false }) : new(handler);
    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new ArgumentException("接口需使用 HTTPS；本机回环地址可用 HTTP。地址中不能包含密钥或查询参数。");
        return uri;
    }
    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerfCompass." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    public async Task<SemanticDraft> ExtractAsync(string source, string endpoint, string model, string apiKey, CancellationToken cancellation)
    {
        var uri = ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(source) || source.Length > 2000 || string.IsNullOrWhiteSpace(model) || model.Length > 150) throw new ArgumentException("请填写需求描述和模型名称。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        string schemaText = Resource("SemanticSchema.json"); using var schema = JsonDocument.Parse(schemaText);
        string prompt = Resource("SemanticPrompt.txt") + "\n输出字段定义：\n" + schemaText;
        var body = new { model, stream = false, temperature = 0, max_tokens = 2000, chat_template_kwargs = new { enable_thinking = false },
            response_format = new { type = "json_schema", json_schema = new { name = "computer_requirements", strict = true, schema = schema.RootElement } },
            messages = new[] { new { role = "system", content = prompt }, new { role = "user", content = source } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"模型接口返回 HTTP {(int)response.StatusCode}。请检查接口、模型、密钥及结构化输出支持情况。");
        using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096]; int count;
        while ((count = await incoming.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0) { if (buffer.Length + count > 131072) throw new InvalidOperationException("模型响应过大，已停止读取。"); await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token).ConfigureAwait(false); }
        using var envelope = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 20 });
        if (!envelope.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw new InvalidOperationException("模型响应格式不受支持。");
        var choice = choices[0];
        if (!choice.TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop") throw new InvalidOperationException("模型未完整生成结果，请调整模型或输出限制后重试。");
        string content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        return SemanticRequirements.Parse(source, model, uri.Host, content);
    }
    public void Dispose() => client.Dispose();
}
