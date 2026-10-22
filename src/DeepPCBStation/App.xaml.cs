using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Services;

namespace DeepPCBStation;

public partial class App : Application
{
    public static StationServices? Station { get; private set; }

    /// <summary>当前会话（含锁屏状态）。</summary>
    public static AppSession Session => AppSession.Current;

    /// <summary>只读转发：登录用户。登出后必须为 null，否则检测记录会带错人名。</summary>
    public static UserAccount? CurrentUser => AppSession.Current.User;

    private static Mutex? _singleInstanceMutex;

    public static bool ReloginRequested { get; private set; }

    /// <summary>UI 快照模式：只为渲染版式，不得往生产库写审计/会话记录。</summary>
    private static bool IsUiPreview { get; set; }

    public static void RequestRelogin()
    {
        ReloginRequested = true;
        Current?.MainWindow?.Close();
    }

    private static void ReleaseStation()
    {
        var station = Station;
        Station = null;
        station?.Dispose();
    }

    /// <summary>
    /// 结束会话：换人后配方换型确认必须失效（否则下一个人可直接改跑），并留审计。
    /// </summary>
    private static void EndSession(string reason)
    {
        if (IsUiPreview)
        {
            Session.End();
            return;
        }
        var user = Session.User;
        if (user != null)
        {
            try { Station?.Recipes.InvalidateConfirmation(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Session] " + ex); }
            try
            {
                Station?.Db.AddAudit(user.Name, "SESSION_END", reason,
                    entity: "session/" + user.Name, oldValue: "active", newValue: "logged_out",
                    reason: reason);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Session] " + ex); }
        }
        Session.End();
    }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        // 登录窗先于主窗关闭；默认 OnLastWindowClose 会直接结束进程
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 防双开抢 DB/端口
        _singleInstanceMutex = new Mutex(true, @"Local\DeepPCB-Station.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DeepPCB-Station 已在运行（请勿双开）。", "DeepPCB-Station",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        if (!RepoRoot.TryBootstrap())
        {
            Shutdown(1);
            return;
        }

        // 统一日志：调试输出（Debug 构建有用）+ 用户目录滚动文件。
        // 为什么必须装文件端点：上位机原来只有 Debug.WriteLine，Release 下被整段裁掉，
        // 现场「告警写库失败 / 检测异常」这类问题事后完全无迹可查。文件落在
        // %APPDATA%\DeepPCBStation\logs 而不是程序目录（构建会覆盖、只读部署写不进）。
        StationLog.SetSink(new StationLog.CompositeSink(
            new StationLog.DelegateSink((lv, src, msg) => System.Diagnostics.Debug.WriteLine($"[{lv}] {src}: {msg}")),
            new FileLogSink(Path.Combine(AppConfig.UserConfigDir(), "logs"))));

        try
        {
            Station = StationServices.Build();
        }
        catch (Exception ex)
        {
            MessageBox.Show(RepoRoot.FormatException(ex), "DeepPCB-Station · 服务初始化失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ReleaseStation();
            Shutdown(2);
            return;
        }

#if DEBUG
        // 开发用（非产线）：DPCB_DEV_UI_SNAPSHOT=<png 路径> 时跳过登录，直接渲染主窗并截图退出。
        // 只用于 UI 版式核对/回归，不参与任何检测与落库逻辑；不设该变量时行为完全不变。
        //
        // 之所以整块包在 #if DEBUG 里：这段会**跳过登录并以 Admin 起会话**。
        // 打进 Release 二进制后，任何能在机器上设环境变量的人就拿到了管理员会话——
        // 不需要密码、不写审计。交付构建（Release）里它根本不存在，不是"靠人别设变量"。
        var snapPath = Environment.GetEnvironmentVariable("DPCB_DEV_UI_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapPath))
        {
            var log = Path.ChangeExtension(snapPath, ".log");
            void Trace(string msg) => File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            try
            {
                Trace("begin");
                IsUiPreview = true;
                var fake = new UserAccount { Name = "snapshot", Role = "Admin" };
                Session.Begin(fake);
                Trace("session ok");
                var preview = new MainWindow(fake);
                var page = Environment.GetEnvironmentVariable("DPCB_DEV_UI_PAGE");
                if (!string.IsNullOrWhiteSpace(page) && preview.DataContext is ShellViewModel snapShell)
                {
                    var nav = snapShell.NavItems.FirstOrDefault(n => n.Title == page);
                    if (nav != null) snapShell.SelectedNav = nav;
                    Trace("page=" + page + (nav == null ? " (未找到，保持默认)" : ""));
                }
                Trace("window created");
                MainWindow = preview;
                preview.Show();
                Trace("shown");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    try { SaveWindowPng(preview, snapPath); Trace("saved"); }
                    catch (Exception ex) { Trace("save fail " + ex); }
                    Shutdown(0);
                };
                timer.Start();
                Trace("timer started");
            }
            catch (Exception ex)
            {
                Trace("fatal " + ex);
                Shutdown(3);
            }
            return;
        }
#endif

        while (true)
        {
            var login = new LoginWindow();
            if (login.ShowDialog() != true || login.CurrentUser == null)
                break;

            Session.Begin(login.CurrentUser);
            ReloginRequested = false;

            try
            {
                var shell = new MainWindow(login.CurrentUser);
                MainWindow = shell;
                shell.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(RepoRoot.FormatException(ex), "DeepPCB-Station · 主窗启动失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ReleaseStation();
                Shutdown(2);
                return;
            }

            // 会话结束：登出/退出都必须清空身份与锁定态
            EndSession(ReloginRequested ? "用户主动切换账号" : "关闭主窗退出");

            if (!ReloginRequested)
                break;
        }

        ReleaseStation();
        Shutdown(0);
    }

#if DEBUG
    // 仅供上面 #if DEBUG 的快照分支使用；Release 构建里不存在，不留死代码。
    private static void SaveWindowPng(Window w, string path)
    {
        if (w.Content is not FrameworkElement root) return;
        var width = (int)Math.Max(1, root.ActualWidth);
        var height = (int)Math.Max(1, root.ActualHeight);
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
#endif

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            RepoRoot.FormatException(e.Exception),
            "DeepPCB-Station · 未处理异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        EndSession("进程退出");
        ReleaseStation();
        base.OnExit(e);
    }
}

public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "Operator";

    public string RoleText => Role switch
    {
        "Admin" => "管理员",
        "Engineer" => "工程师",
        _ => "操作员"
    };

    public bool IsEngineer => Role is "Engineer" or "Admin";
    public bool IsAdmin => Role == "Admin";
}
