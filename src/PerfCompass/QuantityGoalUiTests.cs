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
    private async Task RunQuantityGoalUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot, Func<Func<bool>, Task> wait)
    {
        void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var c = VisualTreeHelper.GetChild(parent, i); if (c is T t) yield return t; foreach (var descendant in Children<T>(c)) yield return descendant; }
        }
        string source = "预算六千到八千，Cubase监听延迟低于10毫秒，重量不超过1.5公斤，续航至少六小时";
        SemanticFact[] facts = [new("预算", "budget", "required", "current", "六千到八千", "元", "", "预算六千到八千"),
            new("Cubase监听延迟", "experience", "required", "current", "低于10", "毫秒", "", "Cubase监听延迟低于10毫秒"),
            new("重量", "portability", "required", "current", "不超过1.5", "公斤", "", "重量不超过1.5公斤"),
            new("续航", "portability", "required", "current", "至少六", "小时", "", "续航至少六小时")];
        var factory = semanticExtractorFactory;
        try
        {
            Click(SettingsNav); FutureBox.Text = source; SemanticPanel.IsExpanded = true;
            ModelEndpointBox.Text = "https://unit.invalid/chat"; ModelNameBox.Text = "synthetic-quantity-flow";
            string payload = JsonSerializer.Serialize(new { facts = facts.Select(f => new { subject = f.Subject, kind = f.Kind, polarity = f.Polarity, timeframe = f.Timeframe, value = f.Value, unit = f.Unit, condition = f.Condition, evidence = f.Evidence }), questions = Array.Empty<string>() });
            semanticExtractorFactory = () => new(new UiModelHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = payload } } } }), Encoding.UTF8, "application/json") })));
            Click(ExtractSemanticButton); await wait(() => ExtractSemanticButton.IsEnabled && SemanticEditors.Items.Count == 4);
            UpdateLayout();
            require(Children<TextBlock>(SemanticEditors).Any(t => t.Text.Contains("严格小于 0.01 秒")) && Children<TextBlock>(SemanticEditors).Any(t => t.Text.Contains("至少 6000 元 且 不超过 8000 元")), "Native semantic review renders strict normalized targets and both budget bounds");
            var editors = SemanticEditors.Items.Cast<SemanticFactEditor>().ToArray();
            foreach (var box in Children<CheckBox>(SemanticEditors)) { box.SetCurrentValue(ToggleButton.IsCheckedProperty, true); box.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource(); }
            SemanticReviewConfirm.IsChecked = true;
            var valueBox = Children<TextBox>(SemanticEditors).Single(b => b.DataContext == editors[1] && b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(SemanticFactEditor.Value));
            valueBox.SetCurrentValue(TextBox.TextProperty, "不超过10"); valueBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            require(!SemanticReviewConfirm.IsChecked!.Value && editors[1].QuantityPreview.Contains("逐项核对"), "Editing the strict boundary updates preview and clears review through actual binding");
            valueBox.SetCurrentValue(TextBox.TextProperty, "低于10"); valueBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Children<TextBlock>(SemanticEditors).Single(t => t.Text.Contains("严格小于 0.01 秒")).BringIntoView(); await screenshot("22-quantity-review");
            SemanticReviewConfirm.IsChecked = true; Click(SaveSemanticButton); await wait(() => report!.SemanticRequirements?.Source == source);
            Click(ComparisonNav); await wait(() => ComparisonPage.Visibility == Visibility.Visible); var view = ComparisonPage;
            var oldIds = view.Workspace.Criteria.Select(c => c.Id).ToHashSet(); Click(view.ImportCriteriaButton);
            var added = view.Workspace.Criteria.Where(c => !oldIds.Contains(c.Id) && c.OriginId.StartsWith("semantic:")).ToArray();
            require(added.Length == 5 && added.Count(c => c.Metric == "price") == 2 && added.Single(c => c.Name.Contains("Cubase")).Direction == "lt" && added.All(c => !c.Reviewed), "Confirmed facts import as five unreviewed quantitative conditions through real comparison entry");
            // Replace this isolated workspace's earlier test conditions via the actual deletion handler.
            foreach (var criterion in view.Workspace.Criteria.Where(c => !added.Contains(c)).ToArray())
            { view.CriteriaGrid.SelectedItem = criterion; Click(view.RemoveCriterionButton); }
            for (int i = 0; i < 3; i++) await EditComparisonCell(view.CandidatesGrid, i, 1, new[] { 5000, 7000, 9000 }[i]);
            for (int i = 0; i < view.CriteriaGrid.Items.Count; i++)
            {
                if (((PurchaseCriterion)view.CriteriaGrid.Items[i]).Metric == "task") await EditComparisonCell(view.CriteriaGrid, i, 8, "统一软件、负载、设置与环境；合成测量，仅验证比较链路");
                await EditComparisonCell(view.CriteriaGrid, i, 0, true);
            }
            Click(view.PrepareMeasurementsButton);
            for (int i = 0; i < view.ObservationsGrid.Items.Count; i++)
            {
                var observation = (PurchaseObservation)view.ObservationsGrid.Items[i]; var goal = view.Workspace.Criteria.Single(c => c.Id == observation.CriterionId);
                double value = goal.Name.Contains("Cubase") ? .01 : goal.Unit == "kg" ? 1.4 : 22000;
                await EditComparisonCell(view.ObservationsGrid, i, 2, value); await EditComparisonCell(view.ObservationsGrid, i, 5, "合成量化测量；非商品性能"); await EditComparisonCell(view.ObservationsGrid, i, 4, true);
            }
            view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
            require(view.Result!.Candidates.All(c => c.State == "fail") && view.Result.Candidates[1].Checks.Single(c => c.Name.Contains("Cubase")).State == "fail", "Exact strict boundary fails in real candidate evaluator");
            for (int i = 0; i < view.ObservationsGrid.Items.Count; i++)
                if (((PurchaseObservation)view.ObservationsGrid.Items[i]).CriterionName.Contains("Cubase")) await EditComparisonCell(view.ObservationsGrid, i, 2, .009);
            view.CoverageConfirm.IsChecked = true; Click(view.CompareButton);
            require(view.Result!.Candidates[1].State == "pass" && view.Result.Candidates[0].State == "fail" && view.Result.Candidates[2].State == "fail" && view.Result.Summary.Contains("均衡方案"), "Both budget endpoints and measured latency constrain the final candidate result");
            string html = view.ExportHtml(); File.WriteAllText(Path.Combine(output, "quantity-comparison.html"), html);
            require(html.Contains("严格小于") && html.Contains("0.009") && html.Contains("6000") && html.Contains("8000") && CandidateComparison.Load(store.Root).Version == "comparison-1.2", "Export and persisted schema retain strict operators ranges and measurements");
            view.ComparisonResults.BringIntoView(); await screenshot("23-quantity-comparison");
        }
        finally { semanticExtractorFactory = factory; }
    }
}
