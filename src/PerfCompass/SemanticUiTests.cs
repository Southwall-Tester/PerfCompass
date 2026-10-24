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
    private sealed class UiModelHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
    private async Task RunSemanticUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        List<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            var found = new List<T>();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) found.Add(value); found.AddRange(Children<T>(child)); }
            return found;
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var originalFactory = semanticExtractorFactory;
        try
        {
            Width = 1240; Height = 820; Click(SettingsNav);
            string source = "下学期选上地质课才用GeoMystic，现在只办公，内存至少64GB";
            FutureBox.Text = source; SemanticPanel.IsExpanded = true;
            ModelEndpointBox.Text = "https://unit.invalid/chat"; ModelNameBox.Text = "synthetic-ui-model"; ModelApiKey.Password = "synthetic-ui-secret";
            var extraction = new { facts = new[] {
                new { subject = "GeoMystic", kind = "workload", polarity = "conditional", timeframe = "future", value = "地质建模", unit = "", condition = "选上地质课", evidence = "下学期选上地质课才用GeoMystic" },
                new { subject = "内存", kind = "resource", polarity = "required", timeframe = "current", value = "至少64", unit = "GB", condition = "", evidence = "内存至少64GB" } }, questions = Array.Empty<string>() };
            semanticExtractorFactory = () => new(new UiModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(extraction) } } } }), Encoding.UTF8, "application/json") })));
            Click(ExtractSemanticButton);
            await wait(() => ExtractSemanticButton.IsEnabled && SemanticEditors.Items.Count == 2);
            require(SemanticEditors.Items.Cast<SemanticFactEditor>().All(e => !e.Include), "Native model flow leaves every candidate unselected");
            SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton);
            require(store.Settings.SemanticRequirements == null && SemanticStatus.Text.Contains("至少"), "Unselected candidates cannot be saved through the UI");
            UpdateLayout();
            var subjectBoxes = Children<TextBox>(SemanticEditors).Where(b => System.Windows.Automation.AutomationProperties.GetName(b) == "候选事项名称").ToList();
            require(subjectBoxes.Count == 2, "Open workload and resource fields render as editable controls");
            subjectBoxes[0].SetCurrentValue(TextBox.TextProperty, "GeoMystic 地质建模"); subjectBoxes[0].GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var includes = Children<CheckBox>(SemanticEditors); foreach (var box in includes) { box.SetCurrentValue(ToggleButton.IsCheckedProperty, true); box.GetBindingExpression(ToggleButton.IsCheckedProperty)!.UpdateSource(); }
            require(SemanticEditors.Items.Cast<SemanticFactEditor>().First().Subject == "GeoMystic 地质建模" && SemanticEditors.Items.Cast<SemanticFactEditor>().All(e => e.Include), "Real review controls update chosen facts and corrected names");
            require(SemanticReviewConfirm.IsChecked == false && !SemanticStatus.Text.Contains("至少"), "Review edits invalidate old acknowledgement and clear stale validation errors");
            foreach (var use in Children<ComboBox>(SemanticEditors).Where(b => System.Windows.Automation.AutomationProperties.GetName(b) == "购机用途"))
            { use.SetCurrentValue(Selector.SelectedValueProperty, "require"); use.GetBindingExpression(Selector.SelectedValueProperty)!.UpdateSource(); }
            SemanticReviewConfirm.IsChecked = true;
            SemanticPanel.BringIntoView(); await screenshot("11-semantic-review");
            Click(SaveSemanticButton); await wait(() => report!.SemanticRequirements?.Facts.Count == 2);
            require(report!.SemanticRequirements!.Facts.First().Polarity == "conditional" && report.Cards.Single(c => c.Key == "memory").Status == "requirements", "Confirmed condition stays conditional while explicit memory goal constrains sizing");
            require(!File.ReadAllText(Path.Combine(store.Root, "settings.json")).Contains("synthetic-ui-secret") && new LocalStore(store.Root).Settings.SemanticRequirements?.Facts.Count == 2, "Reviewed facts persist without writing the API key");
            string html = ReportExporter.Html(report, live); File.WriteAllText(Path.Combine(output, "semantic-report.html"), html);
            require(html.Contains("逐项核对的购机条件") && html.Contains("GeoMystic 地质建模") && html.Contains("条件陈述") && html.Contains("选上地质课") && html.Contains("购机用途"), "Report includes corrected open workload and conditional meaning");
            Click(AdviceNav); await wait(() => report!.Cards.Any(c => c.Key == "constraints")); await screenshot("12-semantic-advice");
            await RunComparisonUiTests(output, require, screenshot, wait);
            Click(SettingsNav); FutureBox.Text = "现在只办公";
            require(semanticDraft == null && SemanticEditors.Items.Count == 0 && SemanticReviewConfirm.IsChecked == false, "Editing source invalidates the semantic review");
            Click(SaveRequirementsButton); await wait(() => report!.SemanticRequirements == null);
            require(store.Settings.SemanticRequirements == null, "Saving changed source removes the old semantic constraints");
            bool cancelled = false;
            semanticExtractorFactory = () => new(new UiModelHandler(async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { cancelled = true; throw; } return new(HttpStatusCode.OK); }));
            FutureBox.Text = "准备做天文叠加"; Click(ExtractSemanticButton); FutureBox.Text = "描述已经更改";
            await wait(() => ExtractSemanticButton.IsEnabled && cancelled);
            require(semanticDraft == null && SemanticEditors.Items.Count == 0, "Source edits cancel a pending request and discard obsolete output");
        }
        finally { semanticCancellation?.Cancel(); semanticExtractorFactory = originalFactory; ModelApiKey.Clear(); }
    }
}
