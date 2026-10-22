using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Services;
using DeepPCBStation.ViewModels;

namespace DeepPCBStation;

public sealed class NavItem
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Glyph { get; set; } = "";
    public object Page { get; set; } = null!;
    public bool Allowed { get; set; } = true;
}

public interface IRefreshable
{
    void Refresh();
}

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _flash;
    private readonly RecipeStore? _recipes;
    private readonly AlarmService? _alarms;
    private readonly StationServices? _station;

    public ShellViewModel(UserAccount user) : this(user, null) { }

    public ShellViewModel(UserAccount user, StationServices? station)
    {
        CurrentUser = user;
        NavItems = new ObservableCollection<NavItem>();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            LockClockText = DateTime.Now.ToString("HH:mm:ss");
            ShiftText = InspectionTicket.ShiftOf(DateTime.Now);
        };
        _clock.Start();
        ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        LockClockText = DateTime.Now.ToString("HH:mm:ss");
        LockDateText = DateTime.Now.ToString("yyyy年M月d日 dddd");

        _flash = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(560) };
        _flash.Tick += (_, _) => AlarmFlashOpacity = AlarmFlashOpacity > 0.5 ? 0.25 : 1.0;

        VersionText = "v" + (System.Reflection.Assembly.GetEntryAssembly()?
            .GetName().Version?.ToString(3) ?? "?");
        if (station != null)
        {
            _station = station;
            _recipes = station.Recipes;
            _recipes.StoreChanged += OnStoreChanged;
            _alarms = station.Alarms;
            _alarms.Changed += OnAlarmsChanged;
        }
        RefreshStationStatus();
        RefreshAlarms();
    }

    /// <summary>锁屏信息：大时钟以外的班次/设备/产量上下文，给回到工位的人一眼看懂。</summary>
    public void RefreshLockInfo()
    {
        LockUserText = $"{CurrentUser.Name} · {CurrentUser.RoleText}";
        LockDateText = DateTime.Now.ToString("yyyy年M月d日 dddd");

        var tcp = "PLC 未启用";
        var hs = _station?.Handshake;
        if (hs != null)
            tcp = hs.IsRunning ? $"PLC {hs.State}" : "PLC 已停止";
        var holder = _station?.Arbiter.HolderText ?? "空闲";
        LockStationText = $"{tcp} · {holder}";

        try
        {
            var today = DateTime.Today;
            var kpi = _station?.Db.Summary(today, null, null);
            LockTodayText = kpi == null
                ? ""
                : $"今日 {kpi.Ok + kpi.Ng} 片 · OK {kpi.Ok} · NG {kpi.Ng}";
        }
        catch (Exception ex)
        {
            StationLog.Warn(nameof(ShellViewModel), "今日产量查询失败: " + ex.Message);
            LockTodayText = "";
        }
    }

    private void OnAlarmsChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) RefreshAlarms();
        else d.BeginInvoke(RefreshAlarms);
    }

    private void RefreshAlarms()
    {
        var top = _alarms?.HighestActive;
        var count = _alarms?.UnackedCount ?? 0;
        AlarmUnackedCount = count;
        AlarmCanAckAll = count > 0;
        HasUnackedAlarm = top != null;

        if (top == null)
        {
            AlarmSeverityText = "";
            AlarmSourceText = "";
            AlarmMessageText = "";
            AlarmTimeText = "";
            AlarmCountText = "";
            AlarmStateText = "";
            AlarmCanAck = false;
            AlarmAckError = "";
            _flash.Stop();
            AlarmFlashOpacity = 1.0;
            return;
        }

        var critical = top.Severity >= (int)AlarmSeverity.Critical;
        AlarmSeverityText = critical ? "严重告警" : "警告";
        AlarmSourceText = top.Source + " · " + top.Code + (top.Occurrences > 1 ? $" ×{top.Occurrences}" : "");
        AlarmMessageText = top.Message;
        AlarmTimeText = FormatTs(top.LastTs);
        AlarmCountText = count > 1 ? $"另有 {count - 1} 条未确认" : "";
        AlarmStateText = top.Acked ? "已确认 · 未恢复" : "未确认";
        AlarmCanAck = !top.Acked;
        AlarmBrush = BrushRes(critical ? "NgBrush" : "WarnBrush", Brushes.Red);
        AlarmSoftBrush = BrushRes(critical ? "NgSoftBrush" : "WarnSoftBrush", Brushes.MistyRose);
        AlarmBorderBrush = BrushRes(critical ? "NgBorderBrush" : "WarnBorderBrush", Brushes.IndianRed);

        // ISA-101：只有“最高级 + 未确认”才闪烁；已确认未恢复只能常亮，否则等于噪声
        if (critical && !top.Acked)
        {
            if (!_flash.IsEnabled)
            {
                AlarmFlashOpacity = 1.0;
                _flash.Start();
            }
            PlayAlarmSoundOnce(top);
        }
        else
        {
            _flash.Stop();
            AlarmFlashOpacity = 1.0;
        }
    }

    /// <summary>只在“新的一次严重告警”响一声；同一事件不重复响。</summary>
    private void PlayAlarmSoundOnce(AlarmRecord top)
    {
        if (_station == null || !_station.Config.Ui.AlarmSound) return;
        var key = top.Id + "#" + top.Occurrences;
        if (string.Equals(key, _lastSoundKey, StringComparison.Ordinal)) return;
        _lastSoundKey = key;
        try { System.Media.SystemSounds.Hand.Play(); }
        catch (Exception ex) { StationLog.Warn(nameof(ShellViewModel), "告警声音播放失败: " + ex.Message); }
    }

    private static string FormatTs(string ts) =>
        DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToString("HH:mm:ss")
            : "";

    private static Brush BrushRes(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private void OnStoreChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) RefreshStationStatus();
        else d.BeginInvoke(RefreshStationStatus);
    }

    private void RefreshStationStatus()
    {
        var r = _recipes?.Current;
        RecipeText = r == null ? "" : $"{r.Id}@v{r.Version}";
        var confirmed = _recipes?.IsChangeoverConfirmed ?? false;
        ChangeoverChip = confirmed ? "换型已确认" : "换型未确认";
        TitleText = $"DeepPCB-Station · PCB AOI 工位 · {VersionText}" +
                    (r == null ? "" : $" · {r.Id}@v{r.Version}" + (confirmed ? " · 已确认" : " · 换型未确认"));
    }

    public UserAccount CurrentUser { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>运行页 VM：外壳的工具栏/灯条/底部页签与运行页共用同一实例，避免双份状态。</summary>
    [ObservableProperty] private RunViewModel? _run;
    [ObservableProperty] private string _stationText = "";
    [ObservableProperty] private string _shiftText = "";

    public void AttachRun(RunViewModel run)
    {
        Run = run;
        StationText = run.StationIdText;
        ShiftText = run.ShiftText;
        OnPropertyChanged(nameof(ShiftText));
    }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _recipeText = "";
    [ObservableProperty] private string _changeoverChip = "";
    [ObservableProperty] private string _titleText = "DeepPCB-Station · PCB AOI 工位";
    [ObservableProperty] private string _pageSubtitle = "";

    // ---------- 锁屏 ----------
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _lockClockText = "";
    [ObservableProperty] private string _lockDateText = "";
    [ObservableProperty] private string _lockUserText = "";
    [ObservableProperty] private string _lockStationText = "";
    [ObservableProperty] private string _lockTodayText = "";

    [ObservableProperty] private bool _hasUnackedAlarm;
    [ObservableProperty] private int _alarmUnackedCount;
    [ObservableProperty] private string _alarmSeverityText = "";
    [ObservableProperty] private string _alarmSourceText = "";
    [ObservableProperty] private string _alarmMessageText = "";
    [ObservableProperty] private string _alarmTimeText = "";
    [ObservableProperty] private string _alarmCountText = "";
    /// <summary>“未确认” / “已确认 · 未恢复”。</summary>
    [ObservableProperty] private string _alarmStateText = "";
    [ObservableProperty] private bool _alarmCanAck;
    /// <summary>是否还有未确认告警（决定「全部确认」按钮可用态与空态反馈）。</summary>
    [ObservableProperty] private bool _alarmCanAckAll;
    [ObservableProperty] private string _alarmAckError = "";
    [ObservableProperty] private double _alarmFlashOpacity = 1.0;
    [ObservableProperty] private Brush _alarmBrush = Brushes.Red;
    [ObservableProperty] private Brush _alarmSoftBrush = Brushes.MistyRose;
    [ObservableProperty] private Brush _alarmBorderBrush = Brushes.IndianRed;
    private string _lastSoundKey = "";

    public string UserText => $"{CurrentUser.Name} · {CurrentUser.RoleText}";

    public string UserInitial
    {
        get
        {
            var name = CurrentUser.Name?.Trim() ?? "";
            return name.Length == 0 ? "U" : name.Substring(0, 1).ToUpperInvariant();
        }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        CurrentPage = value?.Page;
        PageSubtitle = value?.Subtitle ?? "";
        if (value?.Page is System.Windows.FrameworkElement fe && fe.DataContext is IRefreshable r)
            r.Refresh();
    }

    public void AddNav(NavItem item)
    {
        NavItems.Add(item);
        if (SelectedNav == null && item.Allowed)
            SelectedNav = item;
    }

    [RelayCommand]
    private void Logout() => App.RequestRelogin();

    [RelayCommand]
    private void AckCurrent()
    {
        if (_alarms == null) return;
        var top = _alarms.HighestActive;
        if (top == null) return;
        if (!_alarms.Acknowledge(top.Id, CurrentUser.Name ?? "-", CurrentUser.Role, out var error))
        {
            AlarmAckError = error;
            return;
        }
        AlarmAckError = "";
        Audit("ALARM_ACK", $"id={top.Id} · {top.Source}/{top.Code} · sev={top.Severity}");
        RefreshAlarms();
    }

    [RelayCommand]
    private void AckAllAlarms()
    {
        if (_alarms == null) return;
        var n = _alarms.AcknowledgeAll(CurrentUser.Name ?? "-", CurrentUser.Role,
            out var skipped, out var error);
        AlarmAckError = error;
        if (n > 0) Audit("ALARM_ACK_ALL", $"acked={n} skipped={skipped}");
        RefreshAlarms();
    }

    private void Audit(string action, string detail)
    {
        try { App.Station?.Db.AddAudit(CurrentUser.Name ?? "-", action, detail); }
        catch { /* 审计失败不阻塞 */ }
    }

    [RelayCommand]
    private void OpenAlarms()
    {
        var nav = NavItems.FirstOrDefault(n => n.Title == "告警记录");
        if (nav != null) SelectedNav = nav;
    }

    public void Dispose()
    {
        _clock.Stop();
        _flash.Stop();
        if (_recipes != null)
            _recipes.StoreChanged -= OnStoreChanged;
        if (_alarms != null)
            _alarms.Changed -= OnAlarmsChanged;
    }
}
