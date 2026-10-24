using System;
using System.Windows;
using System.Windows.Threading;

namespace PerfCompass;

public partial class MainWindow
{
    private readonly CoalescedSignal liveUpdates;
    private sealed record LiveFrame(long Sequence, int Generation, LiveSnapshot? Snapshot);
    // Producer state and recent experience samples use recordingGate.
    private LiveFrame? latestLiveFrame;
    private long liveSequence, appliedLiveSequence, plottedLiveSequence;
    private int plottedGeneration = -1;
    private bool pendingProtectionRefresh, pendingTimingInterruption;
    private DateTimeOffset? displayGapAt;

    private bool PostLiveUpdate(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;
        try { Dispatcher.BeginInvoke(DispatcherPriority.Background, action); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private Sample[] RecentExperienceSamples()
    { lock (recordingGate) return experienceContext.ToArray(); }

    private void PublishLive(LiveSnapshot snapshot, int? expectedGeneration = null)
    {
        lock (recordingGate)
        {
            if (paused || exiting || expectedGeneration.HasValue && expectedGeneration != recordingGeneration) return;
            var sample = snapshot.Sample;
            if (sample.SamplingIssue.Length > 0)
            { experienceContext.Clear(); pendingTimingInterruption = true; }
            experienceContext.Add(sample);
            experienceContext.RemoveAll(p => p.Time < sample.Time.AddMinutes(-2));
            latestLiveFrame = new(++liveSequence, recordingGeneration, snapshot);
        }
        if (Dispatcher.CheckAccess()) ApplyLiveUpdates(); else liveUpdates.Signal();
    }
    // UI-originated updates use the same acceptance path.
    private void UpdateLive(LiveSnapshot snapshot) => PublishLive(snapshot);

    private void PublishCollectionFailure(int generation)
    {
        lock (recordingGate)
        {
            if (paused || exiting || generation != recordingGeneration) return;
            latestLiveFrame = new(++liveSequence, generation, null);
        }
        liveUpdates.Signal();
    }
    private void RequestProtectionRefresh()
    {
        lock (recordingGate) pendingProtectionRefresh = true;
        liveUpdates.Signal();
    }
    private void ApplyLiveUpdates()
    {
        if (exiting) return;
        LiveFrame? frame;
        bool protection, timing;
        lock (recordingGate)
        {
            protection = pendingProtectionRefresh; pendingProtectionRefresh = false;
            frame = latestLiveFrame;
            if (frame == null || frame.Sequence <= appliedLiveSequence || paused || frame.Generation != recordingGeneration) frame = null;
            timing = frame != null && pendingTimingInterruption;
            if (frame != null) { appliedLiveSequence = frame.Sequence; pendingTimingInterruption = false; }
        }
        if (protection) RefreshDataProtection();
        if (frame == null) return;
        if (timing) { livePoints.Clear(); displayGapAt = null; LiveDisplayGapBanner.Visibility = Visibility.Collapsed; }
        if (frame.Snapshot == null)
        {
            if (timing) { LiveChart.SetPoints(livePoints); _ = RefreshAnalysis(); }
            RecordState.Text = "● 采集重试中";
            StatusLine.Text = "本次采样失败，2 秒后重试。错误已写入 data/errors.log。";
            return;
        }
        var sample = frame.Snapshot.Sample;
        bool skipped = plottedLiveSequence > 0 && (frame.Sequence != plottedLiveSequence + 1 || frame.Generation != plottedGeneration);
        if (!timing && skipped && livePoints.Count > 0)
        {
            if (sample.Time < livePoints[^1].Time) livePoints.Clear();
            else livePoints.Add(new(sample.Time, null, null, null));
            displayGapAt = sample.Time;
        }
        plottedLiveSequence = frame.Sequence; plottedGeneration = frame.Generation;
        RenderLiveSnapshot(frame.Snapshot);
        if (displayGapAt < sample.Time.AddMinutes(-4)) displayGapAt = null;
        LiveDisplayGapBanner.Visibility = displayGapAt.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (timing && sample.SamplingIssue.Length == 0) _ = RefreshAnalysis();
    }
}
