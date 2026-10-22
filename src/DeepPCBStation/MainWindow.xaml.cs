using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Threading;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Services;
using DeepPCBStation.ViewModels;
using DeepPCBStation.Views;

namespace DeepPCBStation;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly AppSession _session = AppSession.Current;
    private readonly DispatcherTimer _idleTimer;
    private readonly int _autoLockMinutes;

    public MainWindow(UserAccount user)
    {
        InitializeComponent();
        var station = App.Station ?? throw new InvalidOperationException("工位服务未初始化");
        var shell = new ShellViewModel(user, station);
        _shell = shell;
        DataContext = shell;

        // 外壳与运行页共用同一个 RunViewModel：工具栏、灯条、底部页签都读这一份状态
        var run = new RunViewModel(station);
        shell.AttachRun(run);

        // 角色门控：操作员=运行监控；工程师=+历史/统计/配方/设置；管理员=+用户权限
        shell.AddNav(new NavItem
        {
            Title = "运行监控", Subtitle = "实时检测与运行控制", Glyph = "\uE768",
            Page = new RunPage { DataContext = run }
        });
        shell.AddNav(new NavItem
        {
            Title = "告警记录", Subtitle = "未确认告警与历史事件", Glyph = ((char)0xE7BA).ToString(),
            Page = new AlarmPage { DataContext = new AlarmHistoryViewModel(station) }
        });
        if (user.IsEngineer)
        {
            shell.AddNav(new NavItem
            {
                Title = "检测历史", Subtitle = "检测记录查询", Glyph = "\uE81C",
                Page = new HistoryPage { DataContext = new HistoryViewModel(station) }
            });
            shell.AddNav(new NavItem
            {
                Title = "统计报表", Subtitle = "产量、良率与缺陷分布", Glyph = "\uE9F9",
                Page = new StatsPage { DataContext = new StatsViewModel(station) }
            });
            shell.AddNav(new NavItem
            {
                Title = "配方管理", Subtitle = "配方参数与版本", Glyph = "\uE8F1",
                Page = new RecipePage { DataContext = new RecipeViewModel(station) }
            });
            shell.AddNav(new NavItem
            {
                Title = "系统设置", Subtitle = "设备、存储与数据库", Glyph = "\uE713",
                Page = new SettingsPage { DataContext = new SettingsViewModel(station) }
            });
        }
        if (user.IsAdmin)
        {
            shell.AddNav(new NavItem
            {
                Title = "用户权限", Subtitle = "账号与审计", Glyph = "\uE77B",
                Page = new UsersPage { DataContext = new UsersViewModel(station) }
            });
        }

        WindowPlacement.Restore(this, "main");
        Closed += (_, _) => WindowPlacement.Save(this, "main");

        Closed += (_, _) =>
        {
            foreach (var item in shell.NavItems)
            {
                if (item.Page is FrameworkElement element && element.DataContext is IDisposable disposable)
                    disposable.Dispose();
            }
            shell.Dispose();
        };

        // ---------- 会话：空闲锁屏（锁 UI，不停检测） ----------
        _autoLockMinutes = Math.Max(0, station.Config.Ui.AutoLockMinutes);
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        if (_autoLockMinutes > 0) _idleTimer.Start();

        _session.Changed += OnSessionChanged;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            _session.Changed -= OnSessionChanged;
        };

        // 活动采样：鼠标/键盘（含滚轮）都算人在工位
        PreviewMouseDown += (_, _) => _session.Touch();
        PreviewMouseWheel += (_, _) => _session.Touch();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void CheckIdle()
    {
        if (_autoLockMinutes <= 0) return;
        if (!_session.IsAuthenticated || _session.IsLocked) return;
        if (_session.Idle.TotalMinutes < _autoLockMinutes) return;

        _session.Lock();
        try
        {
            App.Station?.Db.AddAudit(_session.User?.Name ?? "-", "SESSION_LOCK",
                $"空闲超时 {_autoLockMinutes} 分钟自动锁屏",
                entity: "session/" + (_session.User?.Name ?? "-"),
                oldValue: "active", newValue: "locked", reason: "空闲超时");
        }
        catch { /* 审计失败不阻塞锁屏 */ }
    }

    private void OnSessionChanged()
    {
        // AppSession 已自行切回 UI 线程
        _shell.IsLocked = _session.IsLocked;
        if (_session.IsLocked)
        {
            _shell.RefreshLockInfo();
            UnlockPwd.Clear();
            UnlockError.Text = "";
            Dispatcher.BeginInvoke(new Action(() => UnlockPwd.Focus()));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 锁定态：吞掉全局快捷键（F11 等），只放行解锁输入框
        if (!_session.IsLocked)
        {
            _session.Touch();
            HandleF11(e);
            return;
        }

        if (ReferenceEquals(e.OriginalSource, UnlockPwd) || UnlockPwd.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter) TryUnlock();
            return;
        }
        e.Handled = true;
    }

    // F11 全屏记忆：进入前保存窗口状态/位置/尺寸，退出时还原（而非一律回 Normal）
    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private Rect _preFullscreenBounds = Rect.Empty;

    private void HandleF11(KeyEventArgs e)
    {
        if (e.Key != Key.F11) return;
        ToggleFullscreen();
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        // 自绘标题栏（WindowStyle=None + WindowChrome）：全屏＝最大化，不再切边框样式
        if (!_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenBounds = RestoreBounds;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            if (!_preFullscreenBounds.IsEmpty && _preFullscreenBounds.Width > 0 && _preFullscreenBounds.Height > 0)
            {
                Left = _preFullscreenBounds.Left;
                Top = _preFullscreenBounds.Top;
                Width = _preFullscreenBounds.Width;
                Height = _preFullscreenBounds.Height;
            }
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }
    }

    // ---------- 自绘标题栏 ----------

    /// <summary>
    /// WindowChrome 最大化时窗口会被系统按 resize 边框外扩，内容会被裁掉几像素；
    /// 用系统边框宽度做补偿（100% DPI 通常 4~8px），还原时归零。
    /// </summary>
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(b.Left, b.Top, b.Right, b.Bottom)
                                                           : new Thickness(0);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* 拖动中松开 */ }
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 工具栏 / 菜单：全部走既有命令与页面，不新增旁路状态 ----------

    private void GotoPage(string title)
    {
        var nav = _shell.NavItems.FirstOrDefault(n => n.Title == title);
        if (nav != null) _shell.SelectedNav = nav;
    }

    private void MenuInspectOnce_Click(object sender, RoutedEventArgs e) =>
        _shell.Run?.InspectOnceCommand.Execute(null);

    private void MenuStartContinuous_Click(object sender, RoutedEventArgs e) =>
        _shell.Run?.StartContinuousCommand.Execute(null);

    private void MenuStop_Click(object sender, RoutedEventArgs e) =>
        _shell.Run?.StopCommand.Execute(null);

    private void MenuConfirmChangeover_Click(object sender, RoutedEventArgs e) =>
        _shell.Run?.ConfirmChangeoverCommand.Execute(null);

    private void MenuOpenResults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = App.Station?.Config.Storage.OutputRoot;
            if (string.IsNullOrWhiteSpace(root)) return;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开结果目录失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToolResetView_Click(object sender, RoutedEventArgs e)
    {
        if (_shell.CurrentPage is RunPage page) page.ResetView();
    }

    private void MenuGotoHistory_Click(object sender, RoutedEventArgs e) => GotoPage("检测历史");

    private void MenuGotoStats_Click(object sender, RoutedEventArgs e) => GotoPage("统计报表");

    private void MenuGotoRecipe_Click(object sender, RoutedEventArgs e) => GotoPage("配方管理");

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var station = App.Station;
        var recipe = station?.Recipes.Current;
        MessageBox.Show(this,
            "DeepPCB-Station · PCB AOI 工位\n" +
            $"版本 {_shell.VersionText}\n" +
            (recipe == null ? "" : $"配方 {recipe.Id} v{recipe.Version}\n") +
            "模板差分 + 分块配准 + 边缘解释 + 拓扑分型\n" +
            "金标口径与证据见 docs/QUALITY_DEBT.md",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>刷新当前页：重新选中当前导航项，触达 IRefreshable.Refresh（与切页同一路径）。</summary>
    private void MenuRefresh_Click(object sender, RoutedEventArgs e)
    {
        var nav = _shell.SelectedNav;
        if (nav?.Page is FrameworkElement fe && fe.DataContext is IRefreshable r)
            r.Refresh();
    }

    private void MenuGotoRun_Click(object sender, RoutedEventArgs e)
    {
        var nav = _shell.NavItems.FirstOrDefault(n => n.Title == "运行监控");
        if (nav != null) _shell.SelectedNav = nav;
    }

    private void UnlockPwd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryUnlock();
            e.Handled = true;
        }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

    /// <summary>解锁逻辑与 UI 事件解耦：按钮 Click、密码框 Enter、全局 Enter 三条路径共用（无需伪造 RoutedEventArgs）。</summary>
    private void TryUnlock()
    {
        if (!_session.IsLocked) return;
        var user = _session.User;
        var db = App.Station?.Db;
        if (user == null || db == null)
        {
            UnlockError.Text = "会话异常，请重新登录。";
            return;
        }

        try
        {
            var svc = new UserService(db);
            if (svc.Validate(user.Name, UnlockPwd.Password, user, out var error))
            {
                UnlockError.Text = "";
                UnlockPwd.Clear();
                _session.Unlock();
                try
                {
                    db.AddAudit(user.Name, "SESSION_UNLOCK", "",
                        entity: "session/" + user.Name, oldValue: "locked", newValue: "active",
                        reason: "解锁成功");
                }
                // 解锁成功但审计写不进去：**不能**因为审计失败把解锁判失败
                // （人已经验过密码了），但必须留痕——审计缺一条是要能查的。
                catch (Exception ex)
                {
                    StationLog.Warn(nameof(MainWindow), "解锁审计写入失败: " + ex.Message);
                }
                return;
            }
            UnlockError.Text = error;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Unlock] " + ex);
            UnlockError.Text = "解锁失败：服务暂不可用。";
        }
        UnlockPwd.SelectAll();
        UnlockPwd.Focus();
    }
}
