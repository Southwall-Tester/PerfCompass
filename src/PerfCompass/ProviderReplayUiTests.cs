using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunProviderReplayUiTests(Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        IEnumerable<TextBlock> Texts(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var c = VisualTreeHelper.GetChild(parent, i); if (c is TextBlock t) yield return t; foreach (var item in Texts(c)) yield return item; }
        }
        var original = semanticExtractorFactory;
        try
        {
            foreach (string id in new[] { "12", "14" })
            {
                using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("PerfCompass.ProviderCase" + id + ".json")!;
                using var reader = new StreamReader(resource, Encoding.UTF8); using var record = JsonDocument.Parse(reader.ReadToEnd());
                var draft = record.RootElement.GetProperty("Draft").Deserialize<SemanticDraft>()!;
                Click(SettingsNav); FutureBox.Text = draft.Source; SemanticPanel.IsExpanded = true;
                ModelEndpointBox.Text = "https://unit.invalid/chat"; ModelNameBox.Text = "recorded-4b-provider-replay";
                string payload = JsonSerializer.Serialize(new { facts = draft.Facts.Select(f => new { subject = f.Subject, kind = f.Kind, polarity = f.Polarity, timeframe = f.Timeframe, value = f.Value, unit = f.Unit, condition = f.Condition, evidence = f.Evidence }), questions = draft.Questions });
                semanticExtractorFactory = () => new(new UiModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = payload } } } }), Encoding.UTF8, "application/json") })));
                Click(ExtractSemanticButton); await wait(() => ExtractSemanticButton.IsEnabled && SemanticEditors.Items.Count == draft.Facts.Count);
                UpdateLayout();
                string target = id == "12" ? "严格小于 0.01 秒" : "至少 64 GB";
                var preview = Texts(SemanticEditors).SingleOrDefault(t => t.Text.Contains(target));
                require(preview != null && SemanticEditors.Items.Cast<SemanticFactEditor>().All(e => !e.Include), "Recorded provider case " + id + " reaches numeric preview without auto-selection");
                preview!.BringIntoView(); await screenshot("24-provider-replay-" + id);
                if (id == "12")
                {
                    foreach (var editor in SemanticEditors.Items.Cast<SemanticFactEditor>()) editor.Include = true;
                    SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton); await wait(() => report!.SemanticRequirements?.Source == draft.Source);
                    Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible);
                    Click(ComparisonPage.ImportCriteriaButton);
                    require(ComparisonPage.Workspace.Criteria.Any(c => c.Metric == "task" && c.Target == .01 && c.Direction == "lt" && c.Evidence.Contains("低于10毫秒") && !c.Reviewed), "Recorded provider latency survives confirmation and imports as an unreviewed strict goal");
                }
                else require(SemanticReviewConfirm.IsChecked == false, "An incomplete provider case is not declared reviewed by its successful numeric conversion");
            }
        }
        finally { semanticExtractorFactory = original; }
    }
}
