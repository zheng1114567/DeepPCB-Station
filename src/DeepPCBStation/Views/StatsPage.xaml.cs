using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeepPCBStation.Views;

public partial class StatsPage : UserControl
{
    public StatsPage()
    {
        InitializeComponent();
    }

    // 图表 + 每日明细渲染 PNG
    private void OnExportChart(object sender, RoutedEventArgs e)
    {
        try
        {
            ChartHost.UpdateLayout();
            var w = (int)Math.Ceiling(ChartHost.ActualWidth);
            var h = (int)Math.Ceiling(ChartHost.ActualHeight);
            if (w <= 0 || h <= 0) return;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(ChartHost), null, new Rect(4, 4, w - 8, h - 8));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var root = App.Station?.Config.Storage.OutputRoot ?? Directory.GetCurrentDirectory();
            var dir = Path.Combine(root, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"stats_chart_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) encoder.Save(fs);

            MessageBox.Show("已导出图表：\n" + path, "统计报表",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出图表失败: " + ex.Message, "统计报表",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
