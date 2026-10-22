using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IRefreshable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        var cfg = station.Config;
        CameraMode = cfg.Camera.Mode ?? "PairedFilePlayback";
        CameraModes = new List<string> { "PairedFilePlayback", "Mock" };
        Engine = cfg.Detection.Engine;
        Engines = new List<string> { "Halcon", "Stub" };
        TcpPort = cfg.Tcp.Port.ToString();
        TcpEnabled = cfg.Tcp.Enabled;
        TcpTimeoutMs = cfg.Tcp.TimeoutMs.ToString();
        SaveImages = cfg.Storage.SaveImages;
        AlarmSound = cfg.Ui.AlarmSound;
        OutputRootText = cfg.Storage.OutputRoot;
        DbPathText = station.Db.DbPath;

        CanEdit = App.CurrentUser?.IsEngineer ?? false;
        // L7：保存目标是用户级配置（%APPDATA%），不是程序目录——
        // 程序目录那一份由构建从源码树复制，写入它会在下次构建被静默覆盖，
        // 且部署到只读目录（Program Files）时根本写不进去。
        WarnText = CanEdit
            ? "保存写入用户级配置（%APPDATA%\\DeepPCBStation\\appsettings.json），覆盖出厂默认，重启后生效。"
            : "当前角色无设置权限（需工程师及以上）。";
        ConfigPathText = AppConfig.UserConfigPath();
        RefreshDiskSpace();

        _ = RefreshDbStatsAsync();
    }

    public IReadOnlyList<string> CameraModes { get; }
    public IReadOnlyList<string> Engines { get; }
    public bool CanEdit { get; }

    [ObservableProperty] private string _cameraMode = "";
    [ObservableProperty] private string _engine = "";
    [ObservableProperty] private string _tcpPort = "";
    [ObservableProperty] private bool _tcpEnabled;
    [ObservableProperty] private string _tcpTimeoutMs = "";
    [ObservableProperty] private bool _saveImages;
    /// <summary>严重告警声音（默认关，保存后立即生效）。</summary>
    [ObservableProperty] private bool _alarmSound;
    [ObservableProperty] private string _outputRootText = "";
    [ObservableProperty] private string _dbPathText = "";
    [ObservableProperty] private string _warnText = "";

    [ObservableProperty] private string _dbCountsText = "—";
    [ObservableProperty] private string _importStatusText = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _canImport = true;
    [ObservableProperty] private string _configPathText = "";
    [ObservableProperty] private string _diskSpaceText = "—";

    partial void OnIsImportingChanged(bool value) => CanImport = !value;

    public void Refresh()
    {
        RefreshDiskSpace();
        _ = RefreshDbStatsAsync();
    }

    private void RefreshDiskSpace()
    {
        try
        {
            var full = Path.GetFullPath(_station.Config.Storage.OutputRoot);
            var root = Path.GetPathRoot(full) ?? "C:\\";
            var drive = new DriveInfo(root);
            DiskSpaceText = $"磁盘 {root} 剩余 {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:F1} GB / 共 {drive.TotalSize / 1024.0 / 1024 / 1024:F0} GB";
        }
        catch
        {
            DiskSpaceText = "—";
        }
    }

    [RelayCommand]
    private void TestDbWrite()
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(_station.Db.DbPath) ?? ".", $"db_write_test_{DateTime.Now:HHmmss}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            _ = _station.Db.RowCounts();
            _dialogs.Info("数据库读写正常（目录可写 + 连接可查询）。", "系统设置");
        }
        catch (Exception ex)
        {
            _dialogs.Warn("数据库写测试失败：" + ex.Message, "系统设置");
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!CanEdit) return;
        try
        {
            var cfg = _station.Config;

            if (!int.TryParse(TcpPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1 || port > 65535)
            {
                _dialogs.Warn("TCP 端口须为 1–65535 的整数。", "系统设置");
                return;
            }
            if (!int.TryParse(TcpTimeoutMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmo)
                || tmo < 100)
            {
                _dialogs.Warn("TCP 超时须为不小于 100 ms 的整数。", "系统设置");
                return;
            }

            var changes = new List<string>();
            if (!string.Equals(cfg.Camera.Mode, CameraMode, StringComparison.Ordinal))
                changes.Add($"相机  {cfg.Camera.Mode} → {CameraMode}");
            if (!string.Equals(cfg.Detection.Engine, Engine, StringComparison.Ordinal))
                changes.Add($"检测引擎  {cfg.Detection.Engine} → {Engine}");
            if (cfg.Tcp.Port != port) changes.Add($"TCP 端口  {cfg.Tcp.Port} → {port}");
            if (cfg.Tcp.Enabled != TcpEnabled) changes.Add($"TCP 启用  {cfg.Tcp.Enabled} → {TcpEnabled}");
            if (cfg.Tcp.TimeoutMs != tmo) changes.Add($"TCP 超时  {cfg.Tcp.TimeoutMs} → {tmo} ms");
            if (cfg.Storage.SaveImages != SaveImages) changes.Add($"保存图像  {cfg.Storage.SaveImages} → {SaveImages}");
            if (cfg.Ui.AlarmSound != AlarmSound) changes.Add($"告警声音  {cfg.Ui.AlarmSound} → {AlarmSound}");

            if (changes.Count == 0)
            {
                _dialogs.Info("设置没有变化。", "系统设置");
                return;
            }

            // 相机/引擎/TCP 变更需重启
            var ask = _dialogs.Confirm(
                "将保存以下设置：\n\n" + string.Join("\n", changes) +
                "\n\n相机/引擎/TCP 变更需重启应用后生效。确认保存？",
                "确认保存设置");
            if (!ask) return;

            cfg.Camera.Mode = CameraMode;
            cfg.Detection.Engine = Engine;
            cfg.Tcp.Port = port;
            cfg.Tcp.Enabled = TcpEnabled;
            cfg.Tcp.TimeoutMs = tmo;
            cfg.Storage.SaveImages = SaveImages;
            cfg.Ui.AlarmSound = AlarmSound;

            var cfgPath = AppConfig.UserConfigPath();
            cfg.Save(cfgPath);
            Audit("SETTINGS_SAVE", string.Join("; ", changes));
            _dialogs.Info("已保存。相机/引擎/TCP 等在重启应用后生效。", "系统设置");
        }
        catch (Exception ex)
        {
            _dialogs.Warn("保存失败: " + ex.Message, "系统设置");
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshDbStatsAsync()
    {
        try
        {
            var c = await Task.Run(() => _station.Db.RowCounts()).ConfigureAwait(true);
            DbCountsText = $"inspections {c.Inspections} · defects {c.Defects} · 配方快照 {c.Snapshots} · 告警 {c.Alarms} · 用户 {c.Users} · 审计 {c.Audit}";
        }
        catch (Exception ex)
        {
            DbCountsText = "统计失败: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportHistoryAsync()
    {
        if (IsImporting) return;
        IsImporting = true;
        ImportStatusText = "扫描 results\\{OK,NG,FAULT}\\<日期>\\<id>.json …";
        try
        {
            var root = Path.GetFullPath(_station.Config.Storage.OutputRoot);
            var files = new List<string>();
            foreach (var bucket in new[] { "OK", "NG", "FAULT" })
            {
                var dir = Path.Combine(root, bucket);
                if (!Directory.Exists(dir)) continue;
                foreach (var day in Directory.EnumerateDirectories(dir))
                    files.AddRange(Directory.EnumerateFiles(day, "*.json"));
            }

            var added = 0;
            var skipped = 0;
            var bad = 0;
            var total = files.Count;
            await Task.Run(() =>
            {
                for (var i = 0; i < files.Count; i++)
                {
                    try
                    {
                        if (TryReadSidecar(files[i], out var row, out var defects))
                        {
                            if (_station.Db.InsertInspection(row, defects)) Interlocked.Increment(ref added);
                            else Interlocked.Increment(ref skipped);
                        }
                        else
                        {
                            Interlocked.Increment(ref bad);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref bad);
                    }
                    if (i % 40 == 0)
                    {
                        var a = added;
                        var s = skipped;
                        var b = bad;
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                            ImportStatusText = $"进度 {i + 1}/{total} · 新增 {a} · 已存在 {s} · 跳过 {b}");
                    }
                }
            }).ConfigureAwait(true);

            ImportStatusText = $"导入完成：共 {total} · 新增 {added} · 已存在 {skipped} · 跳过 {bad}";
            Audit("IMPORT_HISTORY", $"added={added} skipped={skipped} bad={bad}");
            await RefreshDbStatsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ImportStatusText = "导入失败: " + ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    internal static bool TryReadSidecar(string path, out InspectionRow row, out List<DefectRow> defects)
    {
        row = new InspectionRow();
        defects = new List<DefectRow>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        string? Id() => Str(root, "inspectionId");
        var id = Id();
        if (string.IsNullOrEmpty(id)) return false;
        var tsStr = Str(root, "timestamp");
        if (!DateTime.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
            return false;

        row = new InspectionRow
        {
            Id = id!,
            PartId = Str(root, "partId"),
            Ts = ts.ToString("O"),
            Verdict = Str(root, "verdict") ?? "NG",
            IsOk = Bool(root, "isOk"),
            FaultCode = Str(root, "faultCode"),
            EngineMs = Num(root, "elapsedMs"),
            DefectCount = (int)Num(root, "defectCount"),
            RecipeId = Str(root, "recipeId") ?? "",
            RecipeVersion = Str(root, "recipeVersion") ?? "",
            RecipeHash = Str(root, "recipeHash") ?? "",
            Engine = Str(root, "engine") ?? "",
            CountsTowardYield = Bool(root, "countsTowardYield"),
            // 归属字段：早期版本丢读，导致导入库后 operator/trigger 等全空（P2-④）
            Operator = Str(root, "operatorName"),
            Shift = Str(root, "shift"),
            TriggerSource = Str(root, "triggerSource") ?? "AUTO",
            StationId = Str(root, "stationId"),
            IsVoid = Bool(root, "isVoid"),
            VoidReason = Str(root, "voidReason"),
            RawPath = Str(root, "rawImagePath"),
            OvlPath = Str(root, "overlayImagePath"),
            SidecarPath = path
        };

        if (root.TryGetProperty("defects", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in arr.EnumerateArray())
            {
                defects.Add(new DefectRow
                {
                    InspectionId = id!,
                    TypeCode = StrP(d, "TypeCode") ?? "",
                    X = (int)NumP(d, "X"),
                    Y = (int)NumP(d, "Y"),
                    W = (int)NumP(d, "W"),
                    H = (int)NumP(d, "H"),
                    Score = NumP(d, "Score")
                });
            }
        }
        return true;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? StrP(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    private static double NumP(JsonElement e, string name) => Num(e, name);

    [RelayCommand]
    private void OpenResultsFolder() => OpenDir(_station.Config.Storage.OutputRoot);

    /// <summary>关于：版本、登录身份、数据来源与质量债说明（原运行页弹窗迁来）。</summary>
    [RelayCommand]
    private void ShowAbout()
    {
        // 与 ShellViewModel.VersionText 同口径：入口程序集优先、回退自身程序集，三段式版本
        var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
        var ver = asm.GetName().Version?.ToString(3) ?? "—";
        var user = App.CurrentUser;
        _dialogs.Info(
            "DeepPCB-Station · PCB AOI 工位（WPF）\n" +
            $"版本 {ver} · .NET Framework 4.8 · schema v3\n\n" +
            $"当前登录：{user?.Name ?? "-"}（{user?.Role ?? "-"}）\n" +
            "配方默认冻结见 docs/QUALITY_DEBT.md。\n" +
            "数据：DeepPCB（非商用可用；商用须联系作者）。\n" +
            "简历禁写论文 ≈89% mAP 为己方成绩。",
            "About · DeepPCB-Station");
    }

    [RelayCommand]
    private void OpenReportsFolder() => OpenDir("reports");

    [RelayCommand]
    private void OpenDbFolder() => OpenDir(Path.GetDirectoryName(_station.Db.DbPath) ?? ".");

    private void OpenDir(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Warn("无法打开目录: " + ex.Message, "系统设置");
        }
    }

    private void Audit(string action, string detail)
    {
        try
        {
            _station.Db.AddAudit(App.CurrentUser?.Name ?? "-", action, detail);
        }
        catch { /* 审计失败不阻塞 */ }
    }
}
