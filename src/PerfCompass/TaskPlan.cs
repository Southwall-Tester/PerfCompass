using System.IO;
using System.Text.Json;

namespace PerfCompass;

public record TaskPlan(string Version, TaskRecipe Recipe, string PowerSource, string PowerProfile)
{
    public string ProtocolFingerprint => TaskMeasurements.Fingerprint(Recipe, new("", "", null, "", PowerSource, PowerProfile),
        Version == TaskPlans.LegacyVersion ? TaskMeasurements.LegacyVersion : TaskMeasurements.Version);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UpgradedFromLegacy { get; init; }
}

/// <summary>A portable recipe and environment declaration, never a run result or a local executable path.</summary>
public static class TaskPlans
{
    public const string Version = "task-plan-1.1";
    public const string LegacyVersion = "task-plan-1.0";
    internal const int MaximumFileBytes = 131072;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 12, WriteIndented = true };
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new ArgumentException(message); }

    public static void Validate(TaskPlan plan)
    {
        Need(plan != null && plan.Version is Version or LegacyVersion && plan.Recipe != null, "任务方案版本或内容无效。");
        TaskMeasurements.ValidateRecipe(plan.Recipe);
        Need(plan.PowerSource is "Online" or "Offline", "方案电源状态须为已知的插电或电池状态；请核对后重新保存方案。");
        Need(!string.IsNullOrWhiteSpace(plan.PowerProfile) && plan.PowerProfile.Length <= 200, "请填写方案的电源 / 性能模式（最多 200 字）。");
    }

    public static async Task<TaskPlan> CreateAsync(TaskRunRequest request, TaskMachine machine, CancellationToken cancellation)
    {
        Need(machine != null, "尚未取得本机电源信息。");
        var recipe = await TaskMeasurements.ReadRecipeAsync(request, cancellation).ConfigureAwait(false);
        var plan = new TaskPlan(Version, recipe, machine.PowerSource, machine.PowerProfile);
        Validate(plan);
        return plan;
    }

    public static void Verify(TaskPlan plan, TaskRecipe recipe, TaskMachine machine)
    {
        Validate(plan); TaskMeasurements.ValidateRecipe(recipe);
        Need(plan.Version == Version, "旧方案需重新导入并核对本机环境，按当前只读方法复测；尚未启动任务。");
        Need(machine != null, "尚未取得本机电源信息。");
        var expected = plan.Recipe;
        var different = new List<string>();
        void Match(bool equal, string label) { if (!equal) different.Add(label); }
        Match(recipe.Name == expected.Name, "任务名称");
        Match(recipe.ExecutableName == expected.ExecutableName, "程序文件名");
        Match(recipe.ExecutableSha256 == expected.ExecutableSha256, "程序文件内容");
        Match(recipe.Arguments == expected.Arguments, "参数");
        Match(recipe.SoftwareVersion == expected.SoftwareVersion, "软件版本");
        Match(recipe.InputName == expected.InputName, "输入文件名");
        Match(recipe.InputSha256 == expected.InputSha256, "输入文件内容");
        Match(recipe.Procedure == expected.Procedure, "统一步骤");
        Match(recipe.Repeats == expected.Repeats, "正式重复次数");
        Match(recipe.Warmup == expected.Warmup, "预热设置");
        Match(recipe.TimeoutSeconds == expected.TimeoutSeconds, "单轮超时");
        Match(machine.PowerSource == plan.PowerSource, "插电 / 电池状态");
        Match(machine.PowerProfile == plan.PowerProfile, "电源 / 性能模式");
        Need(different.Count == 0, "与绑定方案不一致：" + string.Join("、", different) + "。请恢复方案字段并重新定位同名同内容的文件，或解除绑定后另存新方案；尚未启动任务。");
    }

    public static TaskPlan Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(stream.Length is > 0 and <= MaximumFileBytes, "方案或测量文件为空，或超过 128 KiB。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        Need(root.ValueKind == JsonValueKind.Object, "方案或测量文件必须是 JSON 对象。");
        RejectDuplicateMembers(root);
        Need(root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String, "方案或测量文件缺少版本。");
        TaskPlan plan;
        if (version.GetString() is Version or LegacyVersion)
        {
            string[] members = ["Version", "Recipe", "PowerSource", "PowerProfile", "ProtocolFingerprint"];
            Need(root.EnumerateObject().All(p => members.Contains(p.Name)), "任务方案包含不支持的字段，请使用当前版本重新导出。");
            string[] recipeMembers = ["Name", "ExecutableName", "ExecutableSha256", "Arguments", "SoftwareVersion", "InputName", "InputSha256", "Procedure", "Repeats", "Warmup", "TimeoutSeconds"];
            Need(root.TryGetProperty("Recipe", out var recipe) && recipe.ValueKind == JsonValueKind.Object
                && recipe.EnumerateObject().All(p => recipeMembers.Contains(p.Name))
                && recipeMembers.All(name => recipe.TryGetProperty(name, out _)),
                "方案中的任务字段不完整或不受支持，请重新导出。");
            plan = root.Deserialize<TaskPlan>(JsonOptions) ?? throw new ArgumentException("任务方案内容为空。");
        }
        else if (version.GetString() is TaskMeasurements.Version or TaskMeasurements.LegacyVersion)
        {
            var measurement = root.Deserialize<TaskMeasurementResult>(JsonOptions) ?? throw new ArgumentException("测量文件内容为空。");
            TaskMeasurements.Validate(measurement);
            // Only reuse the recipe and environment. Attempts, result state,
            // machine identity and old elapsed values never become plan fields.
            plan = new(measurement.Version == TaskMeasurements.LegacyVersion ? LegacyVersion : Version, measurement.Recipe, measurement.Machine.PowerSource, measurement.Machine.PowerProfile);
        }
        else throw new ArgumentException("不支持此文件版本，请选择任务方案或原始任务测量文件。");
        Validate(plan);
        if (root.TryGetProperty("ProtocolFingerprint", out var fingerprint))
            Need(fingerprint.ValueKind == JsonValueKind.String && fingerprint.GetString() == plan.ProtocolFingerprint,
                "文件中的协议指纹与内容不一致，请从原方案或测量记录重新导出。");
        // Check the ORIGINAL fingerprint before changing the measurement method.
        // Reuse task settings only; old timings are never promoted to this method.
        return plan.Version == LegacyVersion ? plan with { Version = Version, UpgradedFromLegacy = true } : plan;
    }

    internal static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Need(names.Add(property.Name), "方案或测量文件存在重复字段，请重新导出。");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateMembers(child);
    }

    public static void Save(string path, TaskPlan plan)
    {
        Validate(plan);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions);
        Need(bytes.Length <= MaximumFileBytes, "任务方案超过 128 KiB，未保存。");
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true; stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            // Same-directory rename publishes only a fully written plan. A failed
            // write or replacement leaves the existing target intact.
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (created)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
