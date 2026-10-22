using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeepPCBStation.ViewModels;

namespace DeepPCBStation.Views;

public partial class RunPage : UserControl
{
    public RunPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FocusPartId();
        };
    }

    private RunViewModel? Vm => DataContext as RunViewModel;

    /// <summary>把图像缩放/平移复位（外壳工具栏「复位视图」调用）。</summary>
    public void ResetView() => Zoom.Reset();

    private void FitView_Click(object sender, RoutedEventArgs e) => Zoom.Reset();

    private void ResetView_Click(object sender, RoutedEventArgs e) => Zoom.Reset();

    private void ToggleCrosshair_Click(object sender, RoutedEventArgs e) =>
        Crosshair.Visibility = Crosshair.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void FocusPartId()
    {
        PartIdBox.Focus();
        PartIdBox.SelectAll();
    }

    // 扫码枪以 Enter 结尾：触发单次检测并保留焦点，便于连续扫码
    private void PartIdBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var vm = Vm;
        if (vm == null || !vm.CanInspect) return;
        vm.InspectOnceCommand.Execute(null);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PartIdBox.Focus();
            PartIdBox.SelectAll();
        }));
    }

    // F2 聚焦扫码框 / F5 单次 / F6 连续 / Esc 停止
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        switch (e.Key)
        {
            case Key.F2:
                FocusPartId();
                e.Handled = true;
                break;
            case Key.F5 when vm.CanInspect:
                vm.InspectOnceCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F6 when vm.CanInspect:
                vm.StartContinuousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when vm.IsRunning:
                // 两段式：第一次只提示，3 秒内再按一次才真停
                vm.RequestStop();
                e.Handled = true;
                break;
        }
    }
}
