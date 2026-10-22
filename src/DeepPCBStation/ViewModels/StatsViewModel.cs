using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed class DailyBarVm
{
    public string Day { get; set; } = "";
    public string DayText { get; set; } = "";
    public int Total { get; set; }
    public int Ok { get; set; }
    public double YieldPct { get; set; }
    public double CountPct { get; set; }
    /// <summary>目标良率（来自 StatsConfig，不再硬编码 95）。</summary>
    public double TargetYieldPct { get; set; } = 95;
    public bool BelowTarget => YieldPct < TargetYieldPct;
    public string YieldText => $"{YieldPct:F1}%";
    public string Tip => $"{DayText} · 检测 {Total} · 良率 {YieldPct:F1}% · OK {Ok}";
}

public sealed class ParetoBarVm
{
    public string TypeCode { get; set; } = "";
    public int Count { get; set; }
    public double BarWidth { get; set; }
    public string CumText { get; set; } = "";
}

public sealed partial class StatsViewModel : ObservableObject, IRefreshable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;

    public StatsViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        DailyBars = new ObservableCollection<DailyBarVm>();
        ParetoBars = new ObservableCollection<ParetoBarVm>();
        var ids = station.Recipes.List().Select(r => r.Id).Distinct().ToList();
        RecipeItems = new List<string> { "全部" }.Concat(ids).ToList();
        _ = QueryAsync();
    }

    public ObservableCollection<DailyBarVm> DailyBars { get; }
    public ObservableCollection<ParetoBarVm> ParetoBars { get; }
    public IReadOnlyList<string> RecipeItems { get; }

    [ObservableProperty] private DateTime? _fromDate = DateTime.Now.Date.AddDays(-14);
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private int _recipeIndex;
    [ObservableProperty] private string _kpiTotal = "—";
    [ObservableProperty] private string _kpiOk = "—";
    [ObservableProperty] private string _kpiNg = "—";
    [ObservableProperty] private string _kpiFault = "—";
    [ObservableProperty] private string _kpiYield = "—";
    [ObservableProperty] private string _kpiAvgMs = "—";
    [ObservableProperty] private string _kpiFpy = "—";
    [ObservableProperty] private string _kpiFalseCall = "—";
    [ObservableProperty] private string _kpiDppm = "—";
    [ObservableProperty] private string _kpiVoid = "—";
    [ObservableProperty] private bool _hasData = true;
    [ObservableProperty] private string _emptyText = "此区间尚无检测";
    [ObservableProperty] private string _countAxisTop = "0";
    [ObservableProperty] private string _countAxisMid = "0";
    [ObservableProperty] private string _shiftTitle = "本班";
    [ObservableProperty] private string _shiftCountText = "—";
    [ObservableProperty] private string _shiftYieldText = "—";

    private string? RecipeFilter => RecipeIndex > 0 ? RecipeItems[RecipeIndex] : null;

    /// <summary>目标良率（StatsConfig.TargetYieldPct）。</summary>
    public double TargetYieldPct => _station.Config.Stats.TargetYieldPct;
    public string TargetText => $"目标 {TargetYieldPct:F0}%";

    [RelayCommand]
    private async Task QueryAsync()
    {
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RangeTodayAsync()
    {
        FromDate = DateTime.Today;
        ToDate = DateTime.Today;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RangeWeekAsync()
    {
        FromDate = DateTime.Today.AddDays(-6);
        ToDate = DateTime.Today;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RangeMonthAsync()
    {
        FromDate = DateTime.Today.AddDays(-29);
        ToDate = DateTime.Today;
        await LoadAsync().ConfigureAwait(true);
    }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var from = FromDate;
            var to = ToDate?.AddDays(1);
            var recipe = RecipeFilter;

            var kpi = await Task.Run(() => _station.Db.Summary(from, to, recipe)).ConfigureAwait(true);
            KpiTotal = kpi.Total.ToString("N0");
            KpiOk = kpi.Ok.ToString("N0");
            KpiNg = kpi.Ng.ToString("N0");
            KpiFault = kpi.Fault.ToString("N0");
            var valid = kpi.Ok + kpi.Ng;
            KpiYield = valid == 0 ? "—" : $"{100.0 * kpi.Ok / valid:F1}%";
            KpiAvgMs = kpi.Total == 0 ? "—" : $"{kpi.AvgMs:F0} ms";
            // FPY：首检即 OK / 参与良率统计的板数（分母不含 FAULT/VOID）
            KpiFpy = valid == 0 ? "—" : $"{100.0 * kpi.FirstPassOk / valid:F1}%";
            // 伪点率：机器判 NG 但人工终判 OK / 机器 NG 数
            KpiFalseCall = kpi.Ng == 0 ? "—" : $"{100.0 * kpi.FalseCall / kpi.Ng:F1}%";
            // DPPM：区间缺陷数 / 有效板数 × 1e6
            KpiDppm = valid == 0 ? "—" : $"{kpi.DefectTotal * 1000000.0 / valid:N0}";
            KpiVoid = kpi.Void.ToString("N0");
            HasData = kpi.Total > 0;
            EmptyText = "此区间尚无检测";

            var daily = await Task.Run(() => _station.Db.DailyYield(from, to, recipe)).ConfigureAwait(true);
            var days = daily.Skip(Math.Max(0, daily.Count - 31)).ToList();
            var maxTotal = days.Count == 0 ? 0 : days.Max(d => d.Total);
            CountAxisTop = maxTotal.ToString("N0");
            CountAxisMid = Math.Round(maxTotal / 2.0).ToString("N0");
            DailyBars.Clear();
            foreach (var d in days)
            {
                DailyBars.Add(new DailyBarVm
                {
                    Day = d.Day,
                    DayText = d.Day.Length >= 10 ? d.Day.Substring(5) : d.Day,
                    Total = d.Total,
                    Ok = d.Ok,
                    YieldPct = d.YieldPct,
                    TargetYieldPct = TargetYieldPct,
                    CountPct = maxTotal == 0 ? 0 : 100.0 * d.Total / maxTotal
                });
            }

            var pareto = await Task.Run(() => _station.Db.DefectPareto(from, to, recipe)).ConfigureAwait(true);
            ParetoBars.Clear();
            var max = pareto.Count == 0 ? 1 : pareto[0].Count;
            var grand = pareto.Sum(p => p.Count);
            var cum = 0;
            foreach (var p in pareto)
            {
                cum += p.Count;
                ParetoBars.Add(new ParetoBarVm
                {
                    TypeCode = p.TypeCode,
                    Count = p.Count,
                    BarWidth = Math.Max(4, 260.0 * p.Count / max),
                    CumText = grand == 0 ? "" : $"累计 {100.0 * cum / grand:F0}%"
                });
            }

            await LoadShiftAsync(recipe).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 失败时清空旧图，避免留下过期数字
            DailyBars.Clear();
            ParetoBars.Clear();
            HasData = false;
            EmptyText = "查询失败：" + ex.Message;
            KpiTotal = KpiOk = KpiNg = KpiFault = "—";
            KpiYield = KpiAvgMs = "—";
            KpiFpy = KpiFalseCall = KpiDppm = KpiVoid = "—";
            CountAxisTop = CountAxisMid = "0";
            ShiftCountText = ShiftYieldText = "—";
            _dialogs.Warn("统计查询失败: " + ex.Message, "统计报表");
        }
    }

    // 白班 08:00–20:00，夜班 20:00–次日 08:00
    private async Task LoadShiftAsync(string? recipe)
    {
        var (name, start) = CurrentShift(DateTime.Now);
        var kpi = await Task.Run(() => _station.Db.Summary(start, null, recipe)).ConfigureAwait(true);
        var valid = kpi.Ok + kpi.Ng;
        ShiftTitle = $"{name} · {start:MM-dd HH:mm} 起";
        ShiftCountText = valid == 0 ? "0 片" : $"{valid:N0} 片";
        ShiftYieldText = valid == 0 ? "—" : $"{100.0 * kpi.Ok / valid:F1}%";
    }

    // 班次边界统一引 InspectionTicket.ShiftOf（L4）：原实现在此自带一份 08:00/20:00 规则，
    // 与落库记录的 Shift 码（A/B）各写一遍；边界一旦调整，这里会静默偏离库里的班次归属。
    // 展示名保留中文（白班/夜班），判定一律走 ShiftOf。
    private static (string Name, DateTime Start) CurrentShift(DateTime now)
    {
        var dayShift = InspectionTicket.ShiftOf(now) == "A";
        return dayShift
            ? ("白班", now.Date.AddHours(8))
            : ("夜班", now.Hour >= 20 ? now.Date.AddHours(20) : now.Date.AddDays(-1).AddHours(20));
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        try
        {
            var from = FromDate;
            var to = ToDate?.AddDays(1);
            var recipe = RecipeFilter;
            var (shiftName, shiftStart) = CurrentShift(DateTime.Now);

            // 查库（含班次汇总）移到线程池，避免大数据量时卡 UI（P3）
            var (kpi, daily, pareto, shift) = await Task.Run(() =>
            {
                var k = _station.Db.Summary(from, to, recipe);
                var d = _station.Db.DailyYield(from, to, recipe);
                var p = _station.Db.DefectPareto(from, to, recipe);
                var s = _station.Db.Summary(shiftStart, null, recipe);
                return (k, d, p, s);
            }).ConfigureAwait(true);

            var dir = Path.Combine(_station.Config.Storage.OutputRoot, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"stats_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine($"KPI,总数={kpi.Total},OK={kpi.Ok},NG={kpi.Ng},FAULT={kpi.Fault},平均节拍ms={kpi.AvgMs:F1}");
            var valid = kpi.Ok + kpi.Ng;
            sb.AppendLine($"质量指标,FPY={(valid == 0 ? "—" : $"{100.0 * kpi.FirstPassOk / valid:F1}%")},"
                + $"伪点率={(kpi.Ng == 0 ? "—" : $"{100.0 * kpi.FalseCall / kpi.Ng:F1}%")},"
                + $"DPPM={(valid == 0 ? "—" : $"{kpi.DefectTotal * 1000000.0 / valid:F0}")},"
                + $"作废数={kpi.Void},已复判={kpi.Reviewed},伪点数={kpi.FalseCall},缺陷总数={kpi.DefectTotal}");
            sb.AppendLine($"班次,{shiftName},{shiftStart:yyyy-MM-dd HH:mm} 起,OK={shift.Ok},NG={shift.Ng}");
            sb.AppendLine();
            sb.AppendLine("日期,检测数,OK数,良率%");
            foreach (var d in daily)
                sb.AppendLine($"{d.Day},{d.Total},{d.Ok},{d.YieldPct:F2}");
            sb.AppendLine();
            sb.AppendLine("缺陷类型,数量");
            foreach (var p in pareto)
                sb.AppendLine($"{p.TypeCode},{p.Count}");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Warn("导出失败: " + ex.Message, "统计报表");
        }
    }
}
