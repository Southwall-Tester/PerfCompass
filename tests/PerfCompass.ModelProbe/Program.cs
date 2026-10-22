using System.IO;
using System.Text;
using System.Text.Json;
using PerfCompass;

if (args.Length == 6 && args[0] == "--batch")
{
    using var batchCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; batchCancellation.Cancel(); };
    try
    {
        if (!ModelRequirementExtractor.ValidateEndpoint(args[1]).IsLoopback) throw new ArgumentException("Batch research is restricted to a loopback endpoint.");
        string output = Path.GetFullPath(args[4]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Choose a new empty output directory.");
        Directory.CreateDirectory(output);
        using var corpus = JsonDocument.Parse(await File.ReadAllTextAsync(args[3], batchCancellation.Token));
        var selected = args[5].Split(',').ToHashSet(StringComparer.Ordinal);
        using var extractor = new ModelRequirementExtractor();
        var records = new List<object>();
        foreach (var item in corpus.RootElement.EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            if (!selected.Contains(id) && !selected.Contains("all")) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,50}$")) throw new ArgumentException("Invalid case identifier.");
            batchCancellation.Token.ThrowIfCancellationRequested();
            string source = item.GetProperty("text").GetString()!; var watch = System.Diagnostics.Stopwatch.StartNew();
            object record;
            try
            {
                var draft = await extractor.ExtractAsync(source, args[1], args[2], "", batchCancellation.Token);
                var validation = draft.Facts.Select(f =>
                {
                    string? issue = null; try { SemanticRequirements.Validate(source, f); } catch (ArgumentException e) { issue = e.Message; }
                    return new { Fact = f, ReviewIssue = issue, QuantityCandidates = QuantityGoals.Compile(f) };
                }).ToArray();
                record = new { Id = id, Source = source, Seconds = watch.Elapsed.TotalSeconds, Outcome = "received", Draft = draft, Validation = validation, Notice = "Unconfirmed synthetic input. Structural checks and quote matching do not establish semantic correctness. No purchase advice generated." };
            }
            catch (OperationCanceledException) when (batchCancellation.IsCancellationRequested) { throw; }
            catch (Exception error) { record = new { Id = id, Source = source, Seconds = watch.Elapsed.TotalSeconds, Outcome = "failed", ErrorType = error.GetType().Name }; }
            await File.WriteAllTextAsync(Path.Combine(output, "case-" + id + ".json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, batchCancellation.Token);
            records.Add(record); Console.WriteLine("Finished synthetic case " + id + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " seconds");
        }
        if (records.Count == 0) throw new ArgumentException("No selected cases exist.");
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, batchCancellation.Token);
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine("Batch probe failed: " + error.GetType().Name); return 1; }
}
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: ModelProbe <chat-endpoint> <model> <source-utf8-file> <output-json>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    string source = await File.ReadAllTextAsync(args[2], Encoding.UTF8, cancellation.Token);
    using var extractor = new ModelRequirementExtractor();
    var started = System.Diagnostics.Stopwatch.StartNew();
    var draft = await extractor.ExtractAsync(source.Trim(), args[0], args[1], Environment.GetEnvironmentVariable("PERFCOMPASS_MODEL_KEY") ?? "", cancellation.Token);
    await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new { Notice = "Provider protocol check only. Candidates are unconfirmed; no purchasing recommendation was produced.", Seconds = started.Elapsed.TotalSeconds, Draft = draft }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, cancellation.Token);
    Console.WriteLine($"Received {draft.Facts.Count} unconfirmed candidates. Output saved.");
    return 0;
}
catch (Exception error)
{
    // Never print request headers, provider response bodies, keys or full exception URLs.
    Console.Error.WriteLine("Model probe failed: " + error.GetType().Name);
    return 1;
}
