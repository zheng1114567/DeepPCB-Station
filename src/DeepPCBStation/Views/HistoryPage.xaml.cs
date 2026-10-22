using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.ViewModels;

namespace DeepPCBStation.Views;

public partial class HistoryPage : UserControl
{
    private HistoryViewModel? _vm;

    public HistoryPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm();
        OverlayImg.SizeChanged += (_, _) => RedrawHighlight();
    }

    private void HookVm()
    {
        if (_vm != null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as HistoryViewModel;
        if (_vm != null)
            _vm.PropertyChanged += Vm_PropertyChanged;
        RedrawHighlight();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HistoryViewModel.OverlayImage)
            or nameof(HistoryViewModel.SelectedDefect)
            or nameof(HistoryViewModel.HasDetail))
            Dispatcher.BeginInvoke(RedrawHighlight);
    }

    // Stretch=Uniform 的 letterbox 坐标映射
    private void RedrawHighlight()
    {
        OverlayCanvas.Children.Clear();
        if (_vm?.OverlayImage == null || _vm.OverlayPixelWidth <= 0 || _vm.OverlayPixelHeight <= 0)
            return;
        var cw = OverlayImg.ActualWidth;
        var ch = OverlayImg.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        var scale = Math.Min(cw / _vm.OverlayPixelWidth, ch / _vm.OverlayPixelHeight);
        var offX = (cw - _vm.OverlayPixelWidth * scale) / 2;
        var offY = (ch - _vm.OverlayPixelHeight * scale) / 2;

        foreach (var d in _vm.Defects)
        {
            var selected = _vm.SelectedDefect != null && d.Id == _vm.SelectedDefect.Id;
            var rect = new Rectangle
            {
                Stroke = selected ? new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)) : new SolidColorBrush(Color.FromRgb(0xB3, 0x4C, 0xC0)),
                StrokeThickness = selected ? 3 : 1,
                Opacity = selected ? 1 : 0.55,
                Fill = selected
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0x2F, 0x6F, 0xED))
                    : Brushes.Transparent
            };
            Canvas.SetLeft(rect, offX + d.X * scale);
            Canvas.SetTop(rect, offY + d.Y * scale);
            rect.Width = Math.Max(2, d.W * scale);
            rect.Height = Math.Max(2, d.H * scale);
            OverlayCanvas.Children.Add(rect);
        }

        if (_vm.SelectedDefect is DefectRow sel)
        {
            var tag = new TextBlock
            {
                Text = $"{sel.TypeCode} ({sel.X},{sel.Y})",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                Padding = new Thickness(3, 1, 3, 1)
            };
            Canvas.SetLeft(tag, offX + sel.X * scale);
            Canvas.SetTop(tag, Math.Max(0, offY + sel.Y * scale - 18));
            OverlayCanvas.Children.Add(tag);
        }
    }
}
