using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Camera;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Handshake;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Core.Storage;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed class RecentResultItem
{
    public string Verdict { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? SidecarPath { get; set; }
    public string? OverlayPath { get; set; }
    public Brush VerdictBrush { get; set; } = Brushes.Gray;
}

/// <summary>缺陷明细行（右侧面板表格用）。全部字段来自 InspectionResult.Defects，不做二次推断。</summary>
public sealed class DefectRowVm
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public Brush TypeBrush { get; set; } = Brushes.Black;
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string ConfText { get; set; } = "—";
}

/// <summary>配方参数行（只读展示当前生效值）。</summary>
public sealed class ParamRowVm
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class RecipeOptionVm
{
    public string Id { get; set; } = "";
    public string Display { get; set; } = "";
}

public sealed partial class RunViewModel : ObservableObject, IDisposable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;
    private readonly AppConfig _config;
    private readonly InspectionPipeline _pipeline;
    private readonly RecipeStore _recipes;
    private readonly DispatcherTimer _tcpTimer;
    private readonly Queue<bool> _yieldWindow = new();
    private const int YieldWindowN = 100;
    private int _okInWindow;
    private int _validCount;

    public RunViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        _config = station.Config;
        _pipeline = station.Pipeline;
        _recipes = station.Recipes;
        _tcpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        RecipeOptions = new ObservableCollection<RecipeOptionVm>();
        RecentResults = new ObservableCollection<RecentResultItem>();
        LogLines = new ObservableCollection<string>();
        LastDefects = new ObservableCollection<DefectRowVm>();
        ParamRows = new ObservableCollection<ParamRowVm>();
        CameraText = string.Equals(_config.Camera.Mode, "Mock", StringComparison.OrdinalIgnoreCase)
            ? "模拟相机"
            : "文件回放";

        LastVerdict = "待检";
        LastElapsedText = "";
        LastIdsText = "partId — · inspectionId —";
        YieldText = "—";
        VerdictBrush = Brushes.Gray;
        OkLampBackground = Res("LampOffBrush", Brushes.DimGray);
        NgLampBackground = Res("LampOffBrush", Brushes.DimGray);
        var idleFg = Res("LampIdleFgBrush", Brushes.Black);
        OkLampForeground = idleFg;
        NgLampForeground = idleFg;
        FaultLampForeground = idleFg;
        FaultLampBackground = OkLampBackground;   // 未判定：三灯统一中性灰，避免 FAULT 看起来更"亮"
        IsIdle = true;
        IsRunning = false;
        PreviewPlaceholderVisibility = Visibility.Visible;
        TcpStateText = "—";
        TcpLampBrush = Res("LampOffBrush", Brushes.DimGray);
        ChangeoverText = "初始化中…";
        StatusText = "初始化…";
        PlaybackStatusText = string.Equals(_config.Camera.Mode, "Mock", StringComparison.OrdinalIgnoreCase)
            ? "模拟相机（非工业相机）"
            : "文件回放（非工业相机） · " + Path.GetFullPath(_config.Camera.PlaybackRootDir);
        EngineWarnText = _station.EngineWarn ?? "";

        _pipeline.FrameReady += OnFrameReady;
        _recipes.StoreChanged += OnStoreChanged;
        _station.Health.Changed += OnStorageHealthChanged;
        _station.Arbiter.Changed += OnArbiterChanged;
        StationBusyText = _station.Arbiter.HolderText;
        IsStationBusy = _station.Arbiter.IsBusy;
        RefreshStorageIssue();

        foreach (var r in _recipes.List())
            RecipeOptions.Add(new RecipeOptionVm { Id = r.Id, Display = $"{r.Name}  {r.Id}  v{r.Version}" });
        SelectedRecipeId = _recipes.Current.Id;
        RefreshRecipeMeta();
        PublishChangeoverState();

        TcpStateText = _config.Tcp.Enabled ? "监听中" : "已关闭";
        TcpLampBrush = _config.Tcp.Enabled
            ? Res("WarnBrush", Brushes.Orange)
            : Res("LampOffBrush", Brushes.DimGray);
        TcpInfo = _config.Tcp.Enabled
            ? $"TCP :{_config.Tcp.Port} · 超时 {_config.Tcp.TimeoutMs}ms · 指令 TRIG / LAST / STATUS / RESET"
            : "TCP 握手已关闭";

        StatusText = $"就绪 · 引擎 {_pipeline.EngineName} · 根={RepoRoot.Current}";
        RecipeHashText = _recipes.Current.ContentHash;

        AppendLog("工位就绪。cwd=" + Directory.GetCurrentDirectory());
        AppendLog("文件回放: " + _config.Camera.PlaybackRootDir);
        AppendLog("结果目录: " + Path.GetFullPath(_config.Storage.OutputRoot));
        AppendLog($"配方 {_recipes.Current.Id} v{_recipes.Current.Version} hash={_recipes.Current.ContentHash}");
        if (!string.IsNullOrEmpty(EngineWarnText))
            AppendLog(EngineWarnText);
        AppendLog("请先点「确认换型」再开检。");

        if (_station.Handshake != null)
        {
            _station.Handshake.Log += OnHandshakeLog;
            RefreshTcpState();
        }
        else if (_config.Tcp.Enabled && _station.HandshakeError != null)
        {
            TcpStateText = "Fault";
            TcpLampBrush = Res("NgBrush", Brushes.Red);
            TcpInfo = _station.HandshakeError;
            AppendLog(TcpInfo);
        }

        _tcpTimer.Tick += (_, _) => RefreshTcpState();
        _tcpTimer.Start();

        // 产量只计有效板，DB 为准；会话内增量 + 定期对账
        _countDate = DateTime.Now.Date;
        _ = Task.Run(() =>
        {
            try
            {
                var kpiToday = _station.Db.Summary(_countDate, null, null);
                var kpiTotal = _station.Db.Summary(null, null, null);
                var todayValid = kpiToday.Ok + kpiToday.Ng;
                var totalValid = kpiTotal.Ok + kpiTotal.Ng;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_disposed != 0) return;
                    TodayCount = todayValid;
                    DbTotalCount = totalValid;
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => AppendLog("产量统计失败: " + ex.Message));
            }
        });
    }

    private void OnStorageHealthChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed != 0) return;
        if (dispatcher.CheckAccess()) RefreshStorageIssue();
        else dispatcher.BeginInvoke(RefreshStorageIssue);
    }

    private void RefreshStorageIssue()
    {
        if (_disposed != 0) return;
        var health = _station.Health;
        HasStorageIssue = health.HasIssue;
        StorageIssueText = health.HasIssue
            ? $"检测记录未写入数据库（已失败 {health.FailCount} 次）：{health.LastError}"
            : "";
    }

    private void OnStoreChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed != 0) return;
        if (dispatcher.CheckAccess()) RefreshRecipeLists();
        else dispatcher.BeginInvoke(RefreshRecipeLists);
    }

    private void OnHandshakeLog(string msg)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _disposed != 0) return;
        // BeginInvoke：握手线程不等 UI；避免"界面等待握手、握手等界面"互卡
        dispatcher.BeginInvoke(() =>
        {
            if (_disposed != 0) return;
            AppendLog(msg);
            if (msg.IndexOf("TRIG", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("回复", StringComparison.OrdinalIgnoreCase) >= 0)
                TcpInfo = "最近: " + msg;
            RefreshTcpState();
        });
    }

    private static Brush Res(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources[key] is Brush b)
                return b;
        }
        catch { /* ignore */ }
        return fallback;
    }

    public ObservableCollection<RecipeOptionVm> RecipeOptions { get; }
    public ObservableCollection<RecentResultItem> RecentResults { get; }
    public ObservableCollection<string> LogLines { get; }

    public bool CanInspect => IsIdle && ChangeoverConfirmed && !IsStationBusy;

    /// <summary>触发权被占用（PLC 或另一个操作员）：开检按钮置灰并显示占用来源。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInspect))]
    private bool _isStationBusy;
    [ObservableProperty] private string _stationBusyText = "空闲";
    /// <summary>Esc 两段式停止：第一次只提示，避免误触打断整批。</summary>
    [ObservableProperty] private bool _isStopArmed;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _playbackStatusText = "";
    [ObservableProperty] private string _engineWarnText = "";
    [ObservableProperty] private string _selectedRecipeId = "";
    [ObservableProperty] private string _recipeMetaText = "";
    [ObservableProperty] private string _lastVerdict = "—";
    [ObservableProperty] private string _lastElapsedText = "—";
    [ObservableProperty] private string _lastIdsText = "";
    [ObservableProperty] private int _lastDefectCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _todayCount;
    [ObservableProperty] private int _dbTotalCount;
    [ObservableProperty] private string _yieldText = "—";
    [ObservableProperty] private Brush _verdictBrush = Brushes.Gray;
    [ObservableProperty] private Brush _okLampBackground = Brushes.Gray;
    [ObservableProperty] private Brush _ngLampBackground = Brushes.Gray;
    [ObservableProperty] private Brush _faultLampBackground = Brushes.Gray;
    [ObservableProperty] private Brush _okLampForeground = Brushes.Black;
    [ObservableProperty] private Brush _ngLampForeground = Brushes.Black;
    [ObservableProperty] private Brush _faultLampForeground = Brushes.Black;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private Visibility _previewPlaceholderVisibility = Visibility.Visible;
    [ObservableProperty] private string _tcpInfo = "";
    [ObservableProperty] private string _tcpStateText = "";
    [ObservableProperty] private Brush _tcpLampBrush = Brushes.Gray;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInspect))]
    private bool _isIdle = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInspect))]
    private bool _changeoverConfirmed;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _changeoverText = "";
    [ObservableProperty] private string _recipeHashText = "";
    [ObservableProperty] private string _partIdText = "MANUAL";
    /// <summary>数据库写失败：顶部红条（静默丢账是工业现场最贵的一种 bug）。</summary>
    [ObservableProperty] private bool _hasStorageIssue;
    [ObservableProperty] private string _storageIssueText = "";
    [ObservableProperty] private string _yieldTitle = "良率(N=0)";
    /// <summary>定数停，0=不限。</summary>
    [ObservableProperty] private string _targetCountText = "0";
    [ObservableProperty] private bool _stopOnNg;
    [ObservableProperty] private int _batchCount;
    [ObservableProperty] private string _beatText = "—";
    [ObservableProperty] private bool _isStopping;
    private readonly Queue<double> _beatWindow = new();
    private CancellationTokenSource? _cts;

    /// <summary>打开一条结果的证据包（ovl 优先，其次 sidecar）。</summary>
    [RelayCommand]
    private void OpenRecent(RecentResultItem? item)
    {
        if (item == null) return;
        try
        {
            var path = item.OverlayPath ?? item.SidecarPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AppendLog("结果文件不存在: " + path);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("打开结果失败: " + ex.Message);
        }
    }

    partial void OnSelectedRecipeIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // 同 Id 只刷新回显，避免 StoreChanged 互调
        if (string.Equals(value, _recipes.Current.Id, StringComparison.Ordinal)) return;

        var target = _recipes.List().FirstOrDefault(r => string.Equals(r.Id, value, StringComparison.Ordinal));
        if (target == null) return;

        var ask = _dialogs.Confirm(
            $"切换配方 → {target.Name} ({target.Id}@v{target.Version})？\n切换后换型确认将失效，需重新确认才能开检。",
            "确认换型");
        if (!ask)
        {
            // 取消后回显当前配方
            var cur = _recipes.Current.Id;
            SelectedRecipeId = cur;
            return;
        }

        try
        {
            _recipes.Select(target.Id);
            _pipeline.SelectRecipe(target.Id);
            RefreshRecipeMeta();
            PublishChangeoverState();
            AppendLog($"切换配方: {target.Id}@v{target.Version} → 需重新确认换型");
            Audit("RECIPE_SELECT", $"{target.Id}@{target.Version} · from run page");
        }
        catch (Exception ex)
        {
            AppendLog("切换配方失败: " + ex.Message);
        }
    }

    public string EngineNameText => _pipeline.EngineName;

    public ObservableCollection<DefectRowVm> LastDefects { get; }
    public ObservableCollection<ParamRowVm> ParamRows { get; }

    /// <summary>相机来源（文件回放/模拟）；工业相机接入后改这里。</summary>
    public string CameraText { get; } = "";

    public string StationIdText => _pipeline.StationId;

    /// <summary>当前班次（与落库的 shift 同一函数，避免显示与记录不一致）。</summary>
    public string ShiftText => InspectionTicket.ShiftOf(DateTime.Now);

    private void RefreshRecipeLists()
    {
        var list = _recipes.List();
        RecipeOptions.Clear();
        foreach (var r in list)
            RecipeOptions.Add(new RecipeOptionVm { Id = r.Id, Display = $"{r.Name}  {r.Id}  v{r.Version}" });

        var currentId = _recipes.Current.Id;
        if (SelectedRecipeId != currentId)
            SelectedRecipeId = currentId;

        RefreshRecipeMeta();
        PublishChangeoverState();
    }

    private void PublishChangeoverState()
    {
        var confirmed = _recipes.IsChangeoverConfirmed;
        ChangeoverConfirmed = confirmed;
        ChangeoverText = confirmed
            ? $"已确认 {_recipes.ConfirmedAt:HH:mm:ss} · {_recipes.Current.Id}@{_recipes.Current.Version}"
            : "换型未确认，禁止开检";
    }

    private void RefreshRecipeMeta()
    {
        var r = _recipes.Current;
        RecipeHashText = string.IsNullOrEmpty(r.ContentHash)
            ? RecipeStore.ComputeHashStatic(r)
            : r.ContentHash;
        var shortHash = RecipeHashText.Length > 8 ? RecipeHashText.Substring(0, 8) : RecipeHashText;
        RecipeMetaText = $"{r.Id}  v{r.Version}  hash={shortHash}…";
        RefreshParamRows(r);
    }

    /// <summary>只展示当前配方实际生效的关键参数（不做解释，避免与配方 JSON 不一致）。</summary>
    private void RefreshParamRows(RecipeConfig r)
    {
        ParamRows.Clear();
        void Add(string k, string v) => ParamRows.Add(new ParamRowVm { Key = k, Value = v });
        Add("diffThreshold", r.DiffThreshold.ToString());
        Add("minBlobArea", r.MinBlobArea.ToString());
        Add("growBoxScale", r.GrowBoxScale.ToString("F2"));
        Add("regionGrowDiff", r.RegionGrowDiff.ToString());
        Add("edgeExplainMin", r.EdgeExplainMin.ToString("F2"));
        Add("lopExplainMin", r.LopExplainMin.ToString("F2"));
        Add("typeTree", r.TypeTree ? "开" : "关");
        Add("featureConfidence", r.FeatureConfidence ? "开" : "关");
    }

    [RelayCommand]
    private void ConfirmChangeover()
    {
        _pipeline.ConfirmChangeover();
        RefreshRecipeMeta();
        PublishChangeoverState();
        AppendLog($"换型已确认 hash={RecipeHashText}");
        Audit("CHANGEOVER_CONFIRM", RecipeHashText);
    }

    private string _tcpAlarmState = "";

    private void RefreshTcpState()
    {
        var hs = _station.Handshake;
        if (hs == null || !_config.Tcp.Enabled)
        {
            TcpStateText = "已关闭";
            TcpLampBrush = Res("LampOffBrush", Brushes.DimGray);
            return;
        }

        if (!hs.IsRunning)
        {
            TcpStateText = "断开";
            TcpLampBrush = Res("NgBrush", Brushes.Red);
            RaiseTcpAlarm("DISCONNECTED", "TCP 握手服务已停止，上位机无法触发检测。");
            return;
        }

        switch (hs.State)
        {
            case StationState.Ready:
                TcpStateText = "Ready";
                TcpLampBrush = Res("MutedBrush", Brushes.Gray);
                // 回到 Ready 才能判定条件恢复；否则警报会一直挂着（确认≠恢复）
                if (!string.Equals(_tcpAlarmState, "OK", StringComparison.Ordinal))
                {
                    _station.Alarms.Clear("TCP", "STATE_FAULT");
                    _station.Alarms.Clear("TCP", "DISCONNECTED");
                    _station.Alarms.Clear("TCP", "LISTEN_FAILED");
                    _tcpAlarmState = "OK";
                }
                break;
            case StationState.Busy:
                TcpStateText = "Busy";
                TcpLampBrush = Res("WarnBrush", Brushes.Orange);
                _tcpAlarmState = "OK";
                break;
            case StationState.Fault:
                TcpStateText = "Fault";
                TcpLampBrush = Res("NgBrush", Brushes.Red);
                RaiseTcpAlarm("STATE_FAULT", "TCP 握手处于 Fault 状态，等待 RESET。");
                break;
            default:
                TcpStateText = "监听中";
                TcpLampBrush = Res("AccentBrush", Brushes.DodgerBlue);
                _tcpAlarmState = "OK";
                break;
        }
    }

    // 只在状态跃迁时告警，避免轮询重复计数
    private void RaiseTcpAlarm(string code, string message)
    {
        if (string.Equals(_tcpAlarmState, code, StringComparison.Ordinal)) return;
        _tcpAlarmState = code;
        _station.Alarms.Raise(AlarmSeverity.Critical, "TCP", code, message);
    }

    private void Audit(string action, string detail)
    {
        try { _station.Db.AddAudit(App.CurrentUser?.Name ?? "-", action, detail); }
        catch { /* 审计失败不阻塞 */ }
    }

    private void OnFrameReady(InspectionResult result, OverlayFrame overlay)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        // 像素转换在工作线程完成，UI 线程只赋值
        BitmapSource? img = null;
        try
        {
            img = BgrToBitmapSource(overlay.BgrPixels, overlay.Width, overlay.Height);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Run] 预览转换失败: " + ex.Message);
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed != 0) return;
            try
            {
                if (img != null)
                {
                    PreviewImage = img;
                    PreviewPlaceholderVisibility = Visibility.Collapsed;
                }
                ApplyStats(result);
            }
            catch (Exception ex)
            {
                AppendLog("UI 更新失败: " + ex.Message);
            }
        });
    }

    private void ApplyStats(InspectionResult r)
    {
        FillDefects(r);
        LastVerdict = r.Verdict;
        LastElapsedText = $"{r.ElapsedMs:F1} ms";
        LastDefectCount = r.DefectCount;
        LastIdsText = $"partId {r.PartId ?? "—"} · inspectionId {r.InspectionId}";

        var off = Res("LampOffBrush", Brushes.DimGray);
        var ok = Res("OkBrush", Brushes.LimeGreen);
        var ng = Res("NgBrush", Brushes.Red);
        var idleFg = Res("LampIdleFgBrush", Brushes.Black);

        if (r.IsFault)
        {
            VerdictBrush = Res("WarnBrush", Brushes.Orange);
            OkLampBackground = off;
            NgLampBackground = off;
            OkLampForeground = idleFg;
            NgLampForeground = idleFg;
            // 琥珀底配深字以保证对比度
            FaultLampForeground = idleFg;
            FaultLampBackground = Res("WarnBrush", Brushes.Orange);
            // 采集无效：不是设备故障，是这张图没拍好，处置=重拍（不计过杀、不计良率）。
            var isCaptureInvalid = r.Kind == VerdictKind.Invalid;
            StatusText = isCaptureInvalid
                ? $"采集无效 {r.InvalidCode} · {r.VerdictNote}"
                : $"故障 {r.FaultCode} · 引擎 {r.EngineName}";
            PushRecent(r, VerdictBrush);
            AppendLog($"{(isCaptureInvalid ? r.Verdict : "FAULT:" + r.FaultCode)} partId={r.PartId} id={r.InspectionId}");
            if (isCaptureInvalid)
                AppendLog("该板不计入过杀与良率，请按提示处置后重拍。");
            else if (string.Equals(r.FaultCode, "RECIPE_NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase))
                AppendLog("请先点击「确认换型」。");
            return;
        }

        _yieldWindow.Enqueue(r.IsOk);
        if (r.IsOk) _okInWindow++;
        _validCount++;
        while (_yieldWindow.Count > YieldWindowN)
        {
            if (_yieldWindow.Dequeue()) _okInWindow--;
        }
        TotalCount = _validCount;

        // 跨日归零；定期与 DB 对账
        var today = DateTime.Now.Date;
        if (today != _countDate)
        {
            _countDate = today;
            TodayCount = 0;
        }
        if (r.CountsTowardYield)
        {
            TodayCount++;
            DbTotalCount++; // 与今日同步增量，避免两次对账之间出现 今日>累计
        }
        if (_validCount % 20 == 0)
            ResyncDbCounters();

        var n = _yieldWindow.Count;
        YieldText = n == 0 ? "—" : $"{100.0 * _okInWindow / n:F1}%";
        YieldTitle = $"良率(N={n})";
        VerdictBrush = r.IsOk ? ok : ng;
        OkLampBackground = r.IsOk ? ok : off;
        NgLampBackground = r.IsOk ? off : ng;
        OkLampForeground = r.IsOk ? Brushes.White : idleFg;
        NgLampForeground = r.IsOk ? idleFg : Brushes.White;
        FaultLampForeground = idleFg;
        FaultLampBackground = off;
        StatusText = $"最近 {r.Verdict} · defects={r.DefectCount} · {_recipes.Current.Id}@{_recipes.Current.Version}";
        PushRecent(r, VerdictBrush);
        if (!string.IsNullOrEmpty(r.SidecarJsonPath))
            AppendLog($"证据包 {r.InspectionId}");
    }

    /// <summary>把本片缺陷填进右侧明细表（类型色沿用叠图图例，置信度取配方标定值）。</summary>
    private void FillDefects(InspectionResult r)
    {
        LastDefects.Clear();
        var brushFor = new Func<string, Brush>(code => code.ToUpperInvariant() switch
        {
            "OPEN" => Res("OpenLegendBrush", Brushes.Firebrick),
            "MOUSEBITE" => Res("MousebiteLegendBrush", Brushes.DarkOrange),
            _ => Res("OtherLegendBrush", Brushes.Purple)
        });
        var i = 1;
        // L4：置信度（标定 logistic，0~1）与未标定回落值（面积）是两种量纲，直接混排会让
        // 未标定行恒靠前、把真高置信缺陷挤到后面。按「有无置信度」分两组：有置信度的按
        // 置信度降序在前，未标定的按 Score 降序置底（ConfText 显示「—」，不与置信度行混淆）。
        foreach (var d in r.Defects.OrderByDescending(x => x.Confidence > 0)
                                   .ThenByDescending(x => x.Confidence > 0 ? x.Confidence : x.Score))
        {
            LastDefects.Add(new DefectRowVm
            {
                Index = i++,
                Type = d.TypeCode.ToLowerInvariant(),
                TypeBrush = brushFor(d.TypeCode),
                X = d.X, Y = d.Y, W = d.W, H = d.H,
                ConfText = d.Confidence > 0 ? d.Confidence.ToString("F2") : "—"
            });
        }
    }

    private DateTime _countDate = DateTime.Now.Date;

    private void ResyncDbCounters()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var today = DateTime.Now.Date;
                var kpiToday = _station.Db.Summary(today, null, null);
                var kpiTotal = _station.Db.Summary(null, null, null);
                var todayValid = kpiToday.Ok + kpiToday.Ng;
                var totalValid = kpiTotal.Ok + kpiTotal.Ng;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_disposed != 0) return;
                    TodayCount = todayValid;
                    DbTotalCount = totalValid;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Db] 对账失败: " + ex.Message);
            }
        });
    }

    private void PushRecent(InspectionResult r, Brush brush)
    {
        RecentResults.Insert(0, new RecentResultItem
        {
            Verdict = r.IsFault ? "FAULT" : (r.IsOk ? "OK" : "NG"),
            Summary = $"{r.ElapsedMs:F0}ms  {r.InspectionId}",
            SidecarPath = r.SidecarJsonPath,
            OverlayPath = r.OverlayImagePath,
            VerdictBrush = brush
        });
        while (RecentResults.Count > 40)
            RecentResults.RemoveAt(RecentResults.Count - 1);
    }

    private static BitmapSource BgrToBitmapSource(byte[] bgr, int width, int height)
    {
        // 源已是 24bpp BGR，直接按 Bgr24 写入：省掉每帧 1.6MB 的 BGRA 临时缓冲
        // 与逐像素转换循环（P2-⑤）。像素结果与原实现逐位一致。
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), bgr, width * 3, 0);
        wb.Freeze();
        return wb;
    }

    private CancellationTokenSource? _inspectOnceCts;

    [RelayCommand]
    private async Task InspectOnceAsync()
    {
        if (RejectIfCannotInspect()) return;
        if (!TryTakeLease(TriggerSource.Operator, out var deny)) { AppendLog(deny); return; }
        IsIdle = false;
        var cts = new CancellationTokenSource();
        _inspectOnceCts = cts;
        try
        {
            await _pipeline.RunOnceAsync(BuildTicket(TriggerSource.Operator), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog("单次检测已取消");
        }
        catch (Exception ex)
        {
            AppendLog("检测失败: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_inspectOnceCts, cts)) _inspectOnceCts = null;
            // CancellationTokenSource.Dispose 只在被并发 Dispose 时抛 ObjectDisposedException；
            // 写清楚类型，别把「取消源已释放」之外的异常也一起吞掉。
            try { cts.Dispose(); } catch (ObjectDisposedException) { }
            IsIdle = true;
            IsRunning = false;
            _station.Arbiter.Release(TriggerSource.Operator);
        }
    }

    /// <summary>UI 触发的工单：带登录人与班次，否则库里无法追溯到人。</summary>
    private InspectionTicket BuildTicket(TriggerSource source) => new()
    {
        PartId = PartIdText.Trim(),
        Operator = App.CurrentUser?.Name,
        Shift = InspectionTicket.ShiftOf(DateTime.Now),
        Trigger = source,
        StationId = _pipeline.StationId,
        RequireChangeover = true,
        Persist = true
    };

    /// <summary>先拿触发权再开检：拿不到就立即返回，不排队等闸门。</summary>
    private bool TryTakeLease(TriggerSource source, out string deny)
    {
        deny = "";
        var result = _station.Arbiter.TryAcquire(source, PartIdText.Trim(), out var reason);
        if (result == LeaseResult.Acquired) return true;
        deny = result == LeaseResult.Busy
            ? "开检被拒：" + reason + "，请等本次检测结束。"
            : "开检被拒：无登录会话。";
        RefreshStationBusy();
        return false;
    }

    private void RefreshStationBusy()
    {
        IsStationBusy = _station.Arbiter.IsBusy;
        StationBusyText = _station.Arbiter.HolderText;
    }

    // Arbiter 可能在 PLC 线程上变更，必须回 UI 线程
    private void OnArbiterChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) { if (_disposed == 0) RefreshStationBusy(); }
        else dispatcher.BeginInvoke((Action)(() => { if (_disposed == 0) RefreshStationBusy(); }));
    }

    [RelayCommand]
    private async Task StartContinuousAsync()
    {
        if (RejectIfCannotInspect()) return;
        if (!TryTakeLease(TriggerSource.Operator, out var deny)) { AppendLog(deny); return; }
        IsIdle = false;
        IsRunning = true;
        IsStopping = false;
        IsStopArmed = false;
        var target = int.TryParse(TargetCountText, out var tc) ? Math.Max(0, tc) : 0;
        BatchCount = 0;
        BeatText = "—";
        _beatWindow.Clear();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        AppendLog($"连续检测开始（定数停={target} · NG停={(StopOnNg ? "开" : "关")}）");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await _pipeline.RunOnceAsync(BuildTicket(TriggerSource.Operator), ct).ConfigureAwait(true);
                BatchCount++;
                _beatWindow.Enqueue(r.ElapsedMs);
                while (_beatWindow.Count > 5) _beatWindow.Dequeue();
                var avg = _beatWindow.Average();
                BeatText = $"{avg:F0} ms/片 · 已连跑 {BatchCount}";

                if (StopOnNg && !r.IsOk)
                {
                    AppendLog("NG 停机：检出 NG，连续检测停止。");
                    break;
                }
                if (target > 0 && BatchCount >= target)
                {
                    AppendLog($"定数停机：已达 {target} 片。");
                    break;
                }
                await Task.Delay(200, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendLog("连续检测异常: " + ex.Message);
        }
        finally
        {
            IsIdle = true;
            IsRunning = false;
            IsStopping = false;
            IsStopArmed = false;
            _station.Arbiter.Release(TriggerSource.Operator);
            AppendLog("连续检测停止");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        IsStopArmed = false;
        _cts?.Cancel();
        IsStopping = true; // "正在停止"：节拍内最后一片完成后才回 Idle
    }

    private DateTime _stopArmedAt;

    /// <summary>
    /// Esc 两段式停止：第一次只提示（防误操作把整批打断），3 秒内再按一次才真停。
    /// </summary>
    public void RequestStop()
    {
        if (!IsRunning) return;
        if (IsStopArmed && (DateTime.UtcNow - _stopArmedAt).TotalSeconds <= 3.0)
        {
            IsStopArmed = false;
            AppendLog("操作员确认停止。");
            Stop();
            return;
        }
        IsStopArmed = true;
        _stopArmedAt = DateTime.UtcNow;
        AppendLog("再按一次 Esc 确认停止（3 秒内有效）。");
    }

    private bool RejectIfCannotInspect()
    {
        if (!IsIdle) return true;
        if (IsStationBusy)
        {
            AppendLog("工位被占用（" + StationBusyText + "），本次开检被拒。");
            return true;
        }
        if (ChangeoverConfirmed) return false;
        AppendLog("换型未确认，禁止开检。请先点「确认换型」。");
        return true;
    }

    private void AppendLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Debug.WriteLine(line);
        void Write()
        {
            if (_disposed != 0) return;
            StatusText = msg.Length > 120 ? msg.Substring(0, 120) + "…" : msg;
            LogLines.Insert(0, line);
            while (LogLines.Count > 6)
                LogLines.RemoveAt(LogLines.Count - 1);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Write();
        else dispatcher.BeginInvoke((Action)Write);
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tcpTimer.Stop();
        try { _inspectOnceCts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Cancel();
        _pipeline.FrameReady -= OnFrameReady;
        _recipes.StoreChanged -= OnStoreChanged;
        _station.Health.Changed -= OnStorageHealthChanged;
        _station.Arbiter.Changed -= OnArbiterChanged;
        if (_station.Handshake != null)
            _station.Handshake.Log -= OnHandshakeLog;
    }
}
