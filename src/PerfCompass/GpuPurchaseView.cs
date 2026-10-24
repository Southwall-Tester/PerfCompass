using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class MainWindow
{
    private StackPanel GpuPurchaseView(GpuPurchasePlan plan)
    {
        var panel = new StackPanel { Tag = "gpu-purchase-plan", Margin = new(0, 10, 0, 0) };
        void Text(string value, bool muted = false) => panel.Children.Add(new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new(0, 0, 0, 9),
            Foreground = Brush(muted ? "#677A87" : "#45606C")
        });
        Text(plan.NextStep);
        Text(plan.Scope, true);
        foreach (var route in plan.Routes)
        {
            Text(route.Name + " · " + route.Order);
            Text(route.Check, true);
        }
        foreach (var need in plan.Needs) Text(need, true);
        Text(GpuPurchasePlan.Notice, true);
        return panel;
    }
}
