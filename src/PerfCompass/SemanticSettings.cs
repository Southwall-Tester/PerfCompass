using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class MainWindow
{
    private SemanticDraft? semanticDraft;
    private CancellationTokenSource? semanticCancellation;
    private bool semanticLocalRequest;
    private Func<ModelRequirementExtractor> semanticExtractorFactory = () => new();
    private void InitializeSemanticSettings()
    {
        ModelEndpointBox.Text = store.Settings.ModelEndpoint;
        ModelNameBox.Text = store.Settings.ModelName;
        if (store.Settings.SemanticRequirements is { } plan && plan.Source == FutureBox.Text.Trim())
        {
            if (!SemanticUse.KnownPlan(plan.ContractVersion) || plan.Facts == null || plan.Facts.Count is < 1 or > 20 || plan.Facts.Any(f => f == null)) { SemanticStatus.Text = "已保存事项无效或版本已改变，请重新解析。"; return; }
            try { foreach (var fact in plan.Facts) SemanticRequirements.Validate(plan.Source, fact); } catch (ArgumentException) { SemanticStatus.Text = "已保存事项需要重新核对，请重新解析。"; return; }
            ShowSemanticDraft(new(plan.Source, plan.Model, plan.Provider, plan.Facts, []));
            SemanticEditors.ItemsSource = plan.Facts.Select(f => CreateSemanticEditor(f, true)).ToList();
            SemanticReviewConfirm.IsChecked = plan.ContractVersion == SemanticUse.PlanVersion && plan.Facts.All(f => f.DecisionUse != null);
            if (SemanticReviewConfirm.IsChecked != true) SemanticStatus.Text = "事项与原文已保留，请分别核对原文时间与购机用途后保存。";
        }
    }
    private void ResetSemanticDraft()
    {
        ClearSettingsFailure("semantic");
        semanticCancellation?.Cancel(); semanticDraft = null;
        SemanticEditors.ItemsSource = null; SemanticReviewConfirm.IsChecked = false;
        SemanticQuestions.Text = ""; SemanticStatus.Text = "描述已修改，旧候选结果已作废。";
        SemanticQuestionExpander.Visibility = Visibility.Collapsed;
        SemanticSourcePreview.Text = "";
    }
    private void ShowSemanticDraft(SemanticDraft draft)
    {
        semanticDraft = draft;
        SemanticSourcePreview.Text = draft.Source;
        ModelConnectionExpander.Header = $"模型连接 · {draft.Provider} · {draft.Model}";
        ModelConnectionExpander.IsExpanded = false;
        SemanticEditors.ItemsSource = draft.Facts.Select(f => CreateSemanticEditor(f)).ToList();
        SemanticReviewConfirm.IsChecked = false;
        SemanticQuestions.Text = draft.Questions.Length > 0 ? "模型提出的待澄清问题（可能重复或有误，请自行核对）：\n" + string.Join("\n", draft.Questions) : "";
        SemanticQuestionExpander.Visibility = draft.Questions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var issues = new List<string>();
        foreach (var fact in draft.Facts) { try { SemanticRequirements.Validate(draft.Source, fact); } catch (ArgumentException ex) { issues.Add(ex.Message); } }
        SemanticStatus.Text = $"收到 {draft.Facts.Count} 项候选，均未自动选用。请对照完整原文，补充遗漏、修改错误，并逐项选择。" + (issues.Count > 0 ? " 需修正：" + string.Join("；", issues.Distinct()) : "");
    }
    private void OpenSemantic_Click(object sender, RoutedEventArgs e) { SemanticPanel.IsExpanded = true; SemanticPanel.BringIntoView(); }
    private SemanticFactEditor CreateSemanticEditor(SemanticFact fact, bool include = false)
    {
        return new(fact) { Include = include, Changed = () => { SemanticReviewConfirm.IsChecked = false; SemanticStatus.Text = "候选事项已修改，请重新核对完整原文后保存。"; } };
    }
    private async void ExtractSemantic_Click(object sender, RoutedEventArgs e)
    {
        if (!ExtractSemanticButton.IsEnabled) return;
        if (measurementActive) { SemanticStatus.Text = "任务测量仍在运行，请结束后再解析需求。"; return; }
        string source = FutureBox.Text.Trim();
        try
        {
            var endpoint = ModelRequirementExtractor.ValidateEndpoint(ModelEndpointBox.Text.Trim());
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(ModelNameBox.Text)) { SemanticStatus.Text = "请填写上方的需求描述和模型名称。"; return; }
            if (endpoint.IsLoopback && !paused) TogglePause();
            semanticLocalRequest = endpoint.IsLoopback;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            semanticCancellation = cancellation; ExtractSemanticButton.IsEnabled = false; CancelSemanticButton.IsEnabled = true;
            SemanticStatus.Text = endpoint.IsLoopback ? "正在调用本机服务，监测已暂停。解析结束后请先停止本地模型服务，再继续监测。" : "正在解析。仅发送需求描述，不发送硬件、进程或历史采样。";
            using var extractor = semanticExtractorFactory();
            var draft = await extractor.ExtractAsync(source, endpoint.AbsoluteUri, ModelNameBox.Text.Trim(), ModelApiKey.Password, cancellation.Token);
            if (source == FutureBox.Text.Trim() && !cancellation.IsCancellationRequested) ShowSemanticDraft(draft);
        }
        catch (OperationCanceledException) { SemanticStatus.Text = "解析已取消或超时；未修改已保存的需求。"; }
        catch (ArgumentException ex) { SemanticStatus.Text = ex.Message; }
        catch (InvalidOperationException ex) { SemanticStatus.Text = ex.Message; }
        catch (Exception ex) { store.Log(new InvalidOperationException("Semantic request failed: " + ex.GetType().Name)); SemanticStatus.Text = ex is HttpRequestException ? "无法连接模型服务，请检查接口地址和服务状态。" : ex is JsonException ? "模型响应不符合字段约定，请更换模型或重试。" : "解析未完成；未修改已保存的需求。"; }
        finally
        {
            semanticCancellation = null; ExtractSemanticButton.IsEnabled = true; CancelSemanticButton.IsEnabled = false;
            if (semanticLocalRequest) StatusLine.Text = "本机模型会改变系统占用，监测保持暂停。停止该服务后，再点击继续记录。";
            semanticLocalRequest = false;
        }
    }
    private void CancelSemantic_Click(object sender, RoutedEventArgs e) => semanticCancellation?.Cancel();
    private void AddSemantic_Click(object sender, RoutedEventArgs e)
    {
        string source = FutureBox.Text.Trim();
        if (source.Length == 0) { SemanticStatus.Text = "请先填写需求描述。"; return; }
        semanticDraft ??= new(source, "用户补充", "本地", [], []);
        SemanticSourcePreview.Text = source;
        var editors = SemanticEditors.Items.Cast<SemanticFactEditor>().ToList();
        if (editors.Count >= 20) { SemanticStatus.Text = "最多保留 20 项。"; return; }
        editors.Add(CreateSemanticEditor(new("", "other", "uncertain", "unspecified", "", "", "", source))); SemanticEditors.ItemsSource = editors;
        SemanticReviewConfirm.IsChecked = false;
    }
    private async void SaveSemantic_Click(object sender, RoutedEventArgs e)
    {
        if (semanticDraft == null || semanticDraft.Source != FutureBox.Text.Trim()) { SemanticStatus.Text = "请先解析或补充当前描述的候选事项。"; return; }
        if (SemanticReviewConfirm.IsChecked != true) { SemanticStatus.Text = "请先核对完整原文，包括模型可能遗漏的内容。"; return; }
        try
        {
            var plan = SemanticRequirements.Confirm(semanticDraft, SemanticEditors.Items.Cast<SemanticFactEditor>());
            if (ModelEndpointBox.Text.Trim().Length > 0) ModelRequirementExtractor.ValidateEndpoint(ModelEndpointBox.Text.Trim());
            if (!TrySaveSettings("semantic", WorkloadCoverage.Select(store.Settings, RepresentativeCheck.IsChecked == true, collector?.HardwareId) with { FutureNeeds = plan.Source, SemanticRequirements = plan, Requirements = null,
                ModelEndpoint = ModelEndpointBox.Text.Trim(), ModelName = ModelNameBox.Text.Trim() }, "核对的事项未保存，候选与输入已保留。请检查文件占用、目录权限或磁盘空间后重试。"))
            { SemanticStatus.Text = settingsFailures["semantic"]; return; }
            ClearSettingsFailure("requirements");
            RequirementsConfirm.IsChecked = false;
            SemanticStatus.Text = "已保存事项及购机用途。必须满足、必须避免、不作为条件和用途待核对的内容分别处理。";
            await RefreshAnalysis();
        }
        catch (ArgumentException ex) { SemanticStatus.Text = ex.Message; }
        catch (Exception ex) { store.Log(ex); SemanticStatus.Text = "保存失败，请检查数据目录权限。"; }
    }
}
