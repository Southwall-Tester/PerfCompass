using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private sealed class TestModelHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
    private static void RunSemanticTests(Action<string, Action> check, Action<bool, string> assert, Func<AnalysisReport> baseline)
    {
        SemanticFact Fact(string evidence, string kind = "workload", string polarity = "required", string time = "future") => new("陌生专业软件", kind, polarity, time, "待验证目标", "", polarity == "conditional" ? "选上课程" : "", evidence);
        string Payload(params SemanticFact[] facts) => JsonSerializer.Serialize(new { facts = facts.Select(f => new { subject = f.Subject, kind = f.Kind, polarity = f.Polarity, timeframe = f.Timeframe, value = f.Value, unit = f.Unit, condition = f.Condition, evidence = f.Evidence }), questions = Array.Empty<string>() });
        SemanticPlan Plan(string source, params SemanticFact[] facts) => SemanticRequirements.Confirm(new(source, "test-model", "test-provider", facts.ToList(), []), facts.Select(f => new SemanticFactEditor(f) { Include = true }));
        check("Semantic extraction preserves open workload names and temporal branches", () =>
        {
            string source = "过去录音，现在不录，明年做天文叠加";
            var facts = new[] { Fact("过去录音", time: "past") with { Subject = "多轨录音" }, Fact("现在不录", polarity: "excluded", time: "current"), Fact("明年做天文叠加") with { Subject = "天文叠加" } };
            var draft = SemanticRequirements.Parse(source, "model", "host", Payload(facts));
            assert(draft.Facts[0].Timeframe == "past" && draft.Facts[1].Polarity == "excluded" && draft.Facts[2].Subject == "天文叠加", "open terms or branches collapsed");
        });
        check("Unselected model facts cannot become confirmed requirements", () =>
        {
            string source = "明年做三维地质建模"; var draft = SemanticRequirements.Parse(source, "m", "p", Payload(Fact(source)));
            bool rejected = false; try { SemanticRequirements.Confirm(draft, draft.Facts.Select(f => new SemanticFactEditor(f))); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "model candidates auto-selected");
        });
        check("Ungrounded facts remain editable but cannot be confirmed", () =>
        {
            var draft = SemanticRequirements.Parse("用CAD", "m", "p", Payload(Fact("准备训练模型")));
            var editor = new SemanticFactEditor(draft.Facts.Single()) { Include = true };
            bool rejected = false; try { SemanticRequirements.Confirm(draft, [editor]); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "invented evidence accepted"); editor.Evidence = "用CAD"; editor.Subject = "CAD";
            assert(SemanticRequirements.Confirm(draft, [editor]).Facts.Single().Subject == "CAD", "review correction not retained");
        });
        check("Ambiguous source occurrence and missing conditions block confirmation", () =>
        {
            bool ambiguous = false; try { SemanticRequirements.Validate("做CAD，不做CAD", Fact("做CAD")); } catch (ArgumentException) { ambiguous = true; }
            bool condition = false; try { SemanticRequirements.Validate("如果选上才做CAD", Fact("如果选上才做CAD", polarity: "conditional") with { Condition = "" }); } catch (ArgumentException) { condition = true; }
            assert(ambiguous && condition, "ambiguous grounding or condition silently accepted");
        });
        check("Malformed semantic envelopes and enum values are rejected", () =>
        {
            foreach (string json in new[] { "{\"facts\":[],\"facts\":[]}", "{\"facts\":[],\"questions\":[],\"buy\":\"expensive\"}", Payload(Fact("CAD") with { Polarity = "maybe-buy" }) })
            { bool rejected = false; try { SemanticRequirements.Parse("CAD", "m", "p", json); } catch (ArgumentException) { rejected = true; } assert(rejected, "unsupported envelope accepted"); }
        });
        check("An explicit trigger cannot be confirmed as an unconditional requirement", () =>
        {
            var fact = Fact("如果选上才做CAD") with { Condition = "选上课程" };
            bool rejected = false; try { Plan(fact.Evidence, fact); } catch (ArgumentException) { rejected = true; }
            assert(rejected, "trigger promoted into an unconditional purchase need");
        });
        check("Only confirmed future workload constrains historical surplus", () =>
        {
            string source = "明年用专用地质软件"; var plan = Plan(source, Fact(source));
            var report = SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: source, SemanticRequirements: plan));
            assert(report.Cards.Single(c => c.Key == "memory").Status == "requirements" && report.Cards.Any(c => c.Key == "constraints"), "confirmed future work ignored");
            assert(SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: "不再需要", SemanticRequirements: plan)).SemanticRequirements == null, "old source confirmation reused");
        });
        check("Conditional excluded and past workloads are not mandatory future needs", () =>
        {
            foreach (var fact in new[] { Fact("如果选上才做CAD", polarity: "conditional"), Fact("不做CAD", polarity: "excluded"), Fact("以前做CAD", time: "past"), Fact("或许做CAD", polarity: "uncertain"), Fact("用CAD", time: "unspecified") })
            {
                var report = SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: fact.Evidence, SemanticRequirements: Plan(fact.Evidence, fact)));
                assert(report.Cards.Single(c => c.Key == "memory").Status == "surplus", "conditional or old task treated as mandatory future work");
            }
        });
        check("Explicit memory minimum does not manufacture a graphics requirement", () =>
        {
            string source = "内存至少64GB"; var fact = Fact(source, "resource", time: "current") with { Subject = "内存", Value = "至少64", Unit = "GB" };
            var report = SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: source, SemanticRequirements: Plan(source, fact)));
            assert(report.Cards.Single(c => c.Key == "memory").Status == "requirements" && report.Cards.Single(c => c.Key == "gpu").Status == "review", "resource scope leaked");
        });
        check("Budget and portability do not imply higher performance tiers", () =>
        {
            string source = "预算八千，重量不超过1.5公斤"; var plan = Plan(source, Fact("预算八千", "budget", time: "current"), Fact("重量不超过1.5公斤", "portability", time: "current"));
            var report = SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: source, SemanticRequirements: plan));
            assert(report.Cards.Single(c => c.Key == "memory").Status == "surplus" && report.Cards.Single(c => c.Key == "constraints").Evidence.Contains("重量"), "nonperformance constraints reinterpreted");
        });
        check("Semantic report preserves reviewed conditions and escapes source text", () =>
        {
            string source = "如果选上才做CAD <script>"; var plan = Plan(source, Fact(source, polarity: "conditional"));
            var report = SemanticRequirements.Apply(baseline(), new Settings(FutureNeeds: source, SemanticRequirements: plan));
            string html = ReportExporter.Html(report, null);
            assert(html.Contains("条件成立时需要") && html.Contains("&lt;script&gt;") && !html.Contains("CAD <script>"), "semantic report lost condition or escaping");
        });
        check("Model connection refuses credential URLs and insecure remote transport", () =>
        {
            foreach (string endpoint in new[] { "http://example.com/v1/chat/completions", "https://user:key@example.com/chat", "https://example.com/chat?key=secret", "file:///tmp/model" })
            { bool rejected = false; try { ModelRequirementExtractor.ValidateEndpoint(endpoint); } catch (ArgumentException) { rejected = true; } assert(rejected, "unsafe endpoint accepted"); }
            assert(ModelRequirementExtractor.ValidateEndpoint("http://127.0.0.1:18109/v1/chat/completions").IsLoopback, "local research endpoint unavailable");
        });
        check("Model request contains description only and respects typed response contract", () =>
        {
            string source = "用天文叠加软件 \"Beta\" <测试> 😀"; string result = Payload(Fact(source, time: "current")); bool requestChecked = false;
            using var extractor = new ModelRequirementExtractor(new TestModelHandler(async (request, cancellation) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                var messages = body.RootElement.GetProperty("messages");
                assert(messages.GetArrayLength() == 2 && messages[1].GetProperty("role").GetString() == "user" && messages[1].GetProperty("content").GetString() == source, "model-facing content differs from exact user input or includes other context");
                assert(request.Headers.Authorization?.Scheme == "Bearer" && body.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_schema", "authentication or schema missing"); requestChecked = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = result } } } }), Encoding.UTF8, "application/json") };
            }));
            var draft = extractor.ExtractAsync(source, "https://unit.invalid/chat", "test-model", "synthetic-unit-key", CancellationToken.None).GetAwaiter().GetResult();
            assert(requestChecked && draft.Facts.Count == 1 && draft.Provider == "unit.invalid", "response contract failed");
        });
        check("Model failures do not expose response bodies or accept truncation", () =>
        {
            using var failed = new ModelRequirementExtractor(new TestModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sensitive response body") })));
            try { failed.ExtractAsync("CAD", "https://unit.invalid/chat", "model", "", CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("failure ignored"); }
            catch (InvalidOperationException ex) { assert(ex.Message.Contains("401") && !ex.Message.Contains("sensitive"), "server body exposed"); }
            using var truncated = new ModelRequirementExtractor(new TestModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{}\"}}]}") })));
            bool rejected = false; try { truncated.ExtractAsync("CAD", "https://unit.invalid/chat", "model", "", CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            assert(rejected, "partial extraction accepted");
        });
        check("Model cancellation aborts a pending transport", () =>
        {
            using var extractor = new ModelRequirementExtractor(new TestModelHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); }));
            using var cancel = new CancellationTokenSource(50); bool stopped = false;
            try { extractor.ExtractAsync("CAD", "https://unit.invalid/chat", "model", "", cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
            assert(stopped, "cancellation ignored");
        });
    }
}
