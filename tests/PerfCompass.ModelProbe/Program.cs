using System.IO;
using System.Text;
using System.Text.Json;
using PerfCompass;

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
