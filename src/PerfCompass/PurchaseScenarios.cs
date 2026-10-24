namespace PerfCompass;

public static partial class CandidateComparison
{
    // A generated placeholder describes missing information, not a shared trigger.
    // Keep bounds from one fact together, but never infer that unrelated unknowns
    // happen simultaneously merely because their subject/display text matches.
    internal static bool IsUnresolvedCondition(PurchaseCriterion criterion) =>
        criterion.Condition.Trim().StartsWith("需求尚未确定：", StringComparison.Ordinal);

    private static string UncertaintyIdentity(PurchaseCriterion criterion)
    {
        string origin = criterion.OriginId;
        if (origin.StartsWith("semantic:", StringComparison.Ordinal))
        {
            string value = origin[9..];
            if (value.Length >= 64 && value[..64].All(Uri.IsHexDigit)
                && (value.Length == 64 || value[64..] is ":min" or ":max" or ":gt" or ":lt"))
                return "semantic:" + value[..64].ToUpperInvariant();
        }
        return "criterion:" + criterion.Id;
    }

    private static string ScenarioKey(PurchaseCriterion criterion) => string.IsNullOrWhiteSpace(criterion.Condition) ? ""
        : Hash(new { Condition = criterion.Condition.Trim(), Uncertainty = IsUnresolvedCondition(criterion) ? UncertaintyIdentity(criterion) : "" });

    private static string ScenarioLabel(IEnumerable<PurchaseCriterion> criteria)
    {
        var items = criteria.ToArray();
        string condition = items[0].Condition.Trim();
        return IsUnresolvedCondition(items[0])
            ? condition + "（" + string.Join("；", items.Select(c => c.Name).Distinct(StringComparer.Ordinal)) + "）"
            : condition;
    }
}
