using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class MainWindow
{
    private static StackPanel MemoryCapacityView(MemoryCapacityPreview preview)
    {
        var panel = new StackPanel { Tag = "memory-capacity-preview", Margin = new(0, 16, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "容量与已测峰值对照", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = preview.Baseline, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = preview.Period, Foreground = Brush("#677A87"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) });
        void Row(string capacity, string difference, string meaning, bool header = false)
        {
            var grid = new Grid { Margin = new(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new() { Width = new(100) }); grid.ColumnDefinitions.Add(new() { Width = new(150) });
            grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            int col = 0;
            foreach (string value in new[] { capacity, difference, meaning })
            {
                var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 8, 2),
                    FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(header ? "#192A36" : "#45606C") };
                Grid.SetColumn(text, col++); grid.Children.Add(text);
            }
            panel.Children.Add(grid);
        }
        Row("候选可用容量", "相对物理峰值", "如何使用这个差额", true);
        foreach (var row in preview.Rows) Row(row.CapacityLabel, row.DifferenceLabel, row.Meaning);
        panel.Children.Add(new TextBlock { Text = MemoryCapacityPreview.Notice, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#677A87"), Margin = new(0, 6, 0, 0) });
        return panel;
    }
}
