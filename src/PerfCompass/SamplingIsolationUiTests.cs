using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PerfCompass;

public partial class MainWindow
{
    private sealed record IsolationRecords(int Count, DateTimeOffset Last);
    private IsolationRecords ReadIsolationRecords()
    {
        int count = 0; DateTimeOffset last = default;
        foreach (string file in Directory.EnumerateFiles(store.Root, "samples-*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string[] lines = reader.ReadToEnd().Split('\n');
            foreach (string line in lines.Take(lines.Length - 1).Where(s => s.Length > 0))
            {
                using var row = JsonDocument.Parse(line);
                var time = row.RootElement.GetProperty("Time").GetDateTimeOffset(); count++;
                if (time > last) last = time;
            }
        }
        return new(count, last);
    }

    private async Task RunSamplingIsolationUiTests(string output, Action<bool, string> require, Func<string, Task> screenshot)
    {
        var notice = "Production WPF entry and real collector on an empty independent root. Only this process's UI thread is deliberately stalled; an independent thread reads committed history and recent context while the stall is active. This is not a driver fault, physical accuracy test, long soak, or proof of the earlier 0.8.18 timeout's cause. Later fault/clock frames are synthetic and are not appended to history.";
        File.WriteAllText(Path.Combine(output, "NOTICE.txt"), notice);
        IsolationRecords before = ReadIsolationRecords();
        var ready = Stopwatch.StartNew();
        while (before.Count == 0 || (DateTimeOffset.Now - before.Last).TotalSeconds > 1.5)
        {
            if (ready.Elapsed.TotalSeconds > 35) throw new TimeoutException("No recent committed batch before UI stall");
            await Task.Delay(100); before = ReadIsolationRecords();
        }
        long beforeSequence; lock (recordingGate) beforeSequence = liveSequence;
        DateTimeOffset blockedAt = DateTimeOffset.UtcNow;
        var block = Stopwatch.StartNew();
        var observer = Task.Factory.StartNew(() =>
        {
            Thread.Sleep(18000);
            long sequence; Sample[] context;
            lock (recordingGate) { sequence = liveSequence; context = experienceContext.ToArray(); }
            return new { AtSeconds = block.Elapsed.TotalSeconds, Sequence = sequence,
                ContextCount = context.Length, ContextLatest = context.LastOrDefault()?.Time,
                Records = ReadIsolationRecords() };
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Thread.Sleep(22000);
        double blockSeconds = block.Elapsed.TotalSeconds;
        var during = await observer;
        File.WriteAllText(Path.Combine(output, "ui-block-evidence.json"), JsonSerializer.Serialize(new
        {
            Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), Notice = notice, BlockedAt = blockedAt,
            BlockSeconds = blockSeconds, BeforeSequence = beforeSequence, Before = before, During = during,
            ObservedInsideBlock = during.AtSeconds < blockSeconds,
            SavedRecordsAdvanced = during.Records.Count > before.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        require(during.AtSeconds < blockSeconds, "Independent observer sampled while the UI was still blocked");
        require(during.Sequence >= beforeSequence + 2, "Real collector accepted multiple samples during the UI stall");
        require(during.Records.Count > before.Count && during.Records.Last > before.Last, "Committed history advanced before the blocked UI recovered");
        require(during.ContextCount >= 2 && during.ContextLatest > before.Last, "Experience context retains real samples omitted from the stalled display");
        for (int i = 0; i < 60 && live!.Sample.Time < during.ContextLatest; i++) await Task.Delay(50);
        require(live!.Sample.Time >= during.ContextLatest, "Recovered UI displays a current accepted frame");
        require(LiveDisplayGapBanner.Visibility == Visibility.Visible && livePoints.Any(p => p.Cpu == null && p.Memory == null && p.Gpu == null),
            "Coalesced display frames leave a visible curve break rather than an invented continuous line");
        await screenshot("01-live-display-gap");
        LiveChart.BringIntoView();
        await screenshot("02-live-curve-break");

        // End the real-telemetry phase before deterministic synthetic frames.
        stop.Cancel();
        if (collectionTask != null) await collectionTask.WaitAsync(TimeSpan.FromSeconds(5));
        require(collectionTask?.IsCompletedSuccessfully == true, "Real collection phase stops before synthetic UI-only fault frames");
        int realRecordCount = ReadIsolationRecords().Count;

        if (!paused) TogglePause();
        int oldGeneration = recordingGeneration - 1;
        long accepted; lock (recordingGate) accepted = liveSequence;
        var healthy = live!;
        PublishLive(healthy, oldGeneration);
        require(RecentExperienceSamples().Length == 0 && liveSequence == accepted, "Paused producer rejects queued old-generation samples and clears experience context");
        TogglePause();
        PublishLive(healthy, oldGeneration);
        require(RecentExperienceSamples().Length == 0 && liveSequence == accepted, "Resume cannot revive samples from the earlier recording generation");

        var queued = healthy with { Sample = healthy.Sample with { Time = healthy.Sample.Time.AddMilliseconds(1) } };
        Task.Run(() => PublishLive(queued, recordingGeneration)).GetAwaiter().GetResult();
        long queuedSequence = liveSequence;
        TogglePause(); ApplyLiveUpdates(); TogglePause(); ApplyLiveUpdates();
        require(appliedLiveSequence < queuedSequence && RecentExperienceSamples().Length == 0 && live!.Sample.Time != queued.Sample.Time,
            "A frame accepted before pause cannot be applied by a pending callback after resume");

        // These synthetic accepted frames exercise coalescing only, not storage.
        long priorSequence = plottedLiveSequence;
        var interruption = healthy with { Sample = healthy.Sample with { Time = DateTimeOffset.Now, Seconds = 0, SamplingIssue = "clock-change", Cpu = null, CoreMax = null } };
        var recovered = healthy with { Sample = healthy.Sample with { Time = DateTimeOffset.Now.AddMilliseconds(1), SamplingIssue = "" } };
        Task.Run(() => { PublishLive(interruption, recordingGeneration); PublishLive(recovered, recordingGeneration); }).GetAwaiter().GetResult();
        ApplyLiveUpdates();
        require(plottedLiveSequence >= priorSequence + 2 && livePoints.Count == 1 && LiveDisplayGapBanner.Visibility == Visibility.Collapsed,
            "An overwritten timing interruption still resets the live curve");
        require(RecentExperienceSamples().Length == 2 && RecentExperienceSamples()[0].Seconds == 0,
            "Overwritten timing frame clears earlier experience context without losing subsequent frames");
        long errorSequence = liveSequence;
        Task.Run(() => PublishCollectionFailure(recordingGeneration)).GetAwaiter().GetResult(); ApplyLiveUpdates();
        require(RecordState.Text.Contains("采集重试") && liveSequence == errorSequence + 1, "Background collection errors reach the UI without awaiting it");
        UpdateLive(recovered);
        require(!RecordState.Text.Contains("采集重试") && LiveDisplayGapBanner.Visibility == Visibility.Visible,
            "Recovery after an error preserves the display discontinuity");
        if (!paused) TogglePause();
        long heldSequence = liveSequence;
        Task.Run(RequestProtectionRefresh).GetAwaiter().GetResult(); ApplyLiveUpdates();
        require(liveSequence == heldSequence && paused, "Data-protection refresh uses an independent signal while recording is paused");

        TogglePause();
        var now = DateTimeOffset.Now;
        var heavy = healthy with { Sample = healthy.Sample with { Time = now.AddSeconds(-3), Seconds = 2,
            Cpu = 100, CoreMax = 100, CpuCoverage = 2, CoreCoverage = 2, SamplingIssue = "" } };
        var light = heavy with { Sample = heavy.Sample with { Time = now.AddSeconds(-1), Cpu = 1, CoreMax = 1 } };
        Task.Run(() => { PublishLive(heavy, recordingGeneration); PublishLive(light, recordingGeneration); }).GetAwaiter().GetResult();
        ApplyLiveUpdates();
        require(live!.Sample.Cpu == 1 && RecentExperienceSamples().Length == 2,
            "Latest display is light while the omitted heavy frame remains in experience context");
        ExperienceNote.Text = "合成界面测试：省略显示的繁忙帧仍应保留为体验线索";
        string note = ExperienceNote.Text;
        RecordExperienceButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (int i = 0; i < 200 && !RecordExperienceButton.IsEnabled; i++) await Task.Delay(50);
        var saved = store.ReadExperiences(7, collector!.HardwareId).SingleOrDefault(e => e.Note == note);
        require(saved?.Clues.Single(c => c.Key == "cpu") is { Peak: 100, HighSeconds: >= 2, CoveredSeconds: >= 4 },
            "Real experience-save control persists CPU peak and duration from an omitted synthetic frame");
        require(ReadIsolationRecords().Count == realRecordCount,
            "UI-only synthetic frames and experience checks never append synthetic telemetry to real sampling history");
    }
}
