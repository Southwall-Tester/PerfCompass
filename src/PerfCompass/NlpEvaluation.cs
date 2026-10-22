using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PerfCompass;

public static class NlpEvaluation
{
    private record Case(string Id, string Group, string Text, string[] Expected);
    public static int Run()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerfCompass.NlpCases.json")!;
        var cases = JsonSerializer.Deserialize<List<Case>>(source, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var rows = cases.Select(c =>
        {
            var parsed = RequirementParser.Parse(c.Text);
            string[] actual = parsed.Facts.Select(f => $"{f.Key}:{(f.Excluded ? "excluded" : "positive")}:{(f.Future ? "future" : "current")}").Distinct().Order().ToArray();
            string[] expected = c.Expected.Distinct().Order().ToArray();
            return new { c.Id, c.Group, c.Text, Expected = expected, Actual = actual, ExactFieldSet = actual.SequenceEqual(expected), Missing = expected.Except(actual).ToArray(), Extra = actual.Except(expected).ToArray() };
        }).ToList();
        Directory.CreateDirectory("artifacts");
        var groups = rows.GroupBy(c => c.Group).Select(g => new { Group = g.Key, Cases = g.Count(), ExactFieldSets = g.Count(c => c.ExactFieldSet) }).ToArray();
        File.WriteAllText("artifacts/nlp-evaluation.json", JsonSerializer.Serialize(new { Parser = RequirementParser.Version,
            Note = "Manually authored synthetic diagnostic cases. Field-set agreement is not real-user accuracy, nor model evaluation.",
            Cases = rows.Count, ExactFieldSets = rows.Count(c => c.ExactFieldSet), Groups = groups, Results = rows }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines("artifacts/nlp-evaluation.txt", new[] { $"Parser: {RequirementParser.Version}; exact field sets: {rows.Count(c => c.ExactFieldSet)}/{rows.Count}", "Synthetic diagnostic corpus; unsupported cases remain visible. No semantic model was evaluated." }
            .Concat(rows.Select(c => $"{(c.ExactFieldSet ? "MATCH" : "GAP")} {c.Id} [{c.Group}] {c.Text}\n  Missing: {string.Join(", ", c.Missing)}; Extra: {string.Join(", ", c.Extra)}")));
        return 0;
    }
}
