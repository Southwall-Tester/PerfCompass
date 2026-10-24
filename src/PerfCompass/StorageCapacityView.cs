using System.Windows;
using System.Windows.Controls;

namespace PerfCompass;

public partial class MainWindow
{
    private static StackPanel StorageCapacityView(StorageCapacityPreview preview)
    {
        var panel=new StackPanel { Tag="storage-capacity-preview",Margin=new(0,16,0,4) };
        panel.Children.Add(new TextBlock { Text="标称容量与已测卷占用",FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,8) });
        panel.Children.Add(new TextBlock { Text=preview.Baseline,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,4) });
        panel.Children.Add(new TextBlock { Text=preview.Period,Foreground=Brush("#677A87"),FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,10) });
        void Row(string nominal,string converted,string delta,string meaning,bool header=false)
        {
            var grid=new Grid { Margin=new(0,0,0,6) };
            foreach(double width in new[]{90d,120,130}) grid.ColumnDefinitions.Add(new(){Width=new(width)});
            grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});int col=0;
            foreach(string value in new[]{nominal,converted,delta,meaning})
            {
                var text=new TextBlock {Text=value,TextWrapping=TextWrapping.Wrap,Margin=new(0,2,8,2),Foreground=Brush(header?"#192A36":"#45606C"),FontWeight=header?FontWeights.SemiBold:FontWeights.Normal};
                Grid.SetColumn(text,col++);grid.Children.Add(text);
            }
            panel.Children.Add(grid);
        }
        Row("标称容量","换算容量","相对已测占用","保留这部分占用时",true);
        foreach(var row in preview.Rows)Row(row.NominalLabel,row.ConvertedLabel,row.DifferenceLabel,row.Meaning);
        panel.Children.Add(new TextBlock {Text=StorageCapacityPreview.Notice,FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=Brush("#677A87"),Margin=new(0,6,0,0)});
        return panel;
    }
}
