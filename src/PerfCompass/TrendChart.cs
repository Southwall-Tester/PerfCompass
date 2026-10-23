using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PerfCompass;

public sealed class TrendChart : FrameworkElement
{
    private List<HistoryPoint> points = [];
    public double MaxGapSeconds { get; set; } = 20;
    public void SetPoints(IEnumerable<HistoryPoint> value) { points = value.ToList(); InvalidateVisual(); }
    internal static bool CanConnect(HistoryPoint previous, HistoryPoint current, double maxGapSeconds, bool gpu) =>
        (current.Time - previous.Time).TotalSeconds is >= 0 && (current.Time - previous.Time).TotalSeconds <= maxGapSeconds
        && (!gpu || previous.Gpu.HasValue && current.Gpu.HasValue && previous.GpuScope == current.GpuScope);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double left = 32, top = 8, w = Math.Max(1, ActualWidth - 38), h = Math.Max(1, ActualHeight - 34);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(232, 238, 240)), 1);
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromRgb(119, 139, 149)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        foreach (int value in new[] { 0, 25, 50, 75, 100 }) { double y = top + h * (1 - value / 100d); dc.DrawLine(gridPen, new Point(left, y), new Point(left + w, y)); Label(value.ToString(), 0, y - 6); }
        if (points.Count == 0) { Label("等待采样", left + w / 2 - 25, top + h / 2); return; }
        var min = points[0].Time; var max = points[^1].Time;
        double span = Math.Max(1, (max - min).TotalSeconds);
        void Draw(Func<HistoryPoint, double?> selector, string color, bool gpu = false)
        {
            var pen = new Pen((SolidColorBrush)new BrushConverter().ConvertFromString(color)!, 2);
            Point? last = null; HistoryPoint? lastObservation = null;
            foreach (var p in points)
            {
                var value = selector(p);
                if (value == null) { last = null; continue; }
                var current = new Point(left + (p.Time - min).TotalSeconds / span * w, top + h * (1 - Math.Clamp(value.Value, 0, 100) / 100));
                // A provider/rule transition is a GPU-only discontinuity, even if
                // both endpoints are valid. CPU and memory remain independent.
                if (last.HasValue && lastObservation != null && CanConnect(lastObservation, p, MaxGapSeconds, gpu)) dc.DrawLine(pen, last.Value, current);
                else dc.DrawEllipse(pen.Brush, null, current, 2.5, 2.5);
                last = current; lastObservation = p;
            }
        }
        Draw(p => p.Cpu, "#007F73"); Draw(p => p.Memory, "#6378C8"); Draw(p => p.Gpu, "#C38C37", gpu: true);
        Label(min.ToLocalTime().ToString(span > 86400 ? "MM/dd HH:mm" : "HH:mm:ss"), left, top + h + 9);
        Label(max.ToLocalTime().ToString(span > 86400 ? "MM/dd HH:mm" : "HH:mm:ss"), Math.Max(left, left + w - 85), top + h + 9);
    }
}
