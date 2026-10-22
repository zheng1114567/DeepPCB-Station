using System.Windows;

namespace DeepPCBStation.Services;

/// <summary>提示级别：与 MessageBoxImage 一一对应，VM 侧不再出现 WPF 图标枚举。</summary>
public enum DialogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// VM 与"弹窗"之间的唯一接缝。
///
/// 为什么要有它：此前 6 个 VM 里散着 32 处 <c>MessageBox.Show</c>——
/// VM 直接依赖具体窗口实现，于是 (1) 任何一条分支都只能在真机上点一遍才能验证，
/// 测试里一碰就弹窗卡死；(2) 提示无法改道到页面内的通知条/日志；
/// (3) 后台线程调用时还会踩 WPF 的线程亲和性。收敛到接口后，
/// 生产注入 <see cref="WpfDialogService"/>，测试可注入记录型实现直接断言"说了什么、点了什么"。
/// </summary>
public interface IDialogService
{
    /// <summary>通知类提示（单按钮，无返回值）。</summary>
    void Notify(string message, string title, DialogLevel level = DialogLevel.Info);

    /// <summary>是非确认；默认"否"（危险操作不能靠默认回车通过）。</summary>
    bool Confirm(string message, string title);
}

/// <summary>语法糖：调用点写 <c>_dialogs.Warn(msg, title)</c>，接口本身只有两个方法。</summary>
public static class DialogServiceExtensions
{
    public static void Info(this IDialogService d, string message, string title)
        => d.Notify(message, title, DialogLevel.Info);

    public static void Warn(this IDialogService d, string message, string title)
        => d.Notify(message, title, DialogLevel.Warning);

    public static void Error(this IDialogService d, string message, string title)
        => d.Notify(message, title, DialogLevel.Error);
}

/// <summary>生产实现：直接转 MessageBox，图标/按钮与改造前逐项一致。</summary>
public sealed class WpfDialogService : IDialogService
{
    public static WpfDialogService Instance { get; } = new();

    public void Notify(string message, string title, DialogLevel level = DialogLevel.Info)
    {
        var icon = level switch
        {
            DialogLevel.Warning => MessageBoxImage.Warning,
            DialogLevel.Error => MessageBoxImage.Error,
            _ => MessageBoxImage.Information
        };
        MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;
}
