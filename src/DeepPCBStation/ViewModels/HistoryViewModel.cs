using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Review;
using DeepPCBStation.Core.Storage;
using DeepPCBStation.Core.Storage.Db;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed class HistoryItemVm
{
    public InspectionRow Row { get; set; } = new();
    public string TimeText { get; set; } = "";
    public string VerdictText { get; set; } = "";
    public Brush VerdictBrush { get; set; } = Brushes.Gray;
    public string RecipeText { get; set; } = "";
    public string ElapsedText { get; set; } = "";

    /// <summary>机器判定（原始）。</summary>
    public string MachineVerdictText => Row.IsVoid ? "VOID" : Row.Verdict;
    /// <summary>人工终判；未复判时显示待复判。</summary>
    public string FinalVerdictText =>
        string.IsNullOrEmpty(Row.FinalVerdict) ? "待复判" : Row.FinalVerdict!;
    public Brush FinalVerdictBrush { get; set; } = Brushes.Gray;
    /// <summary>“复判人 · 时间”。</summary>
    public string ReviewText { get; set; } = "";
    public string OperatorText => string.IsNullOrEmpty(Row.Operator) ? "—" : Row.Operator!;

    /// <summary>可复判：OK/NG 且尚未终判且非作废。</summary>
    public bool IsPendingReview => !Row.IsVoid && string.IsNullOrEmpty(Row.FinalVerdict) &&
        (string.Equals(Row.Verdict, "OK", StringComparison.Ordinal) ||
         string.Equals(Row.Verdict, "NG", StringComparison.Ordinal));
}

// Number=0 表示省略号
public sealed class PageSlot
{
    public int Number { get; set; }
    public bool IsCurrent { get; set; }
    public bool IsEllipsis => Number == 0;
    public string Text => IsEllipsis ? "…" : Number.ToString();
}

public sealed partial class HistoryViewModel : ObservableObject, IRefreshable
{
    private readonly StationServices _station;
    private readonly IDialogService _dialogs;

    private static readonly string[] VerdictOptions = { "全部", "OK", "NG", "FAULT" };
    private static readonly string[] TypeCodeOptions =
        { "全部", "OPEN", "MOUSEBITE", "SHORT", "SPUR", "COPPER", "PINHOLE" };

    public HistoryViewModel(StationServices station, IDialogService? dialogs = null)
    {
        _station = station;
        _dialogs = dialogs ?? WpfDialogService.Instance;
        Rows = new ObservableCollection<HistoryItemVm>();
        Defects = new ObservableCollection<DefectRow>();
        PageSlots = new ObservableCollection<PageSlot>();
        PageSizeOptions = new[] { 50, 100, 200 };
        VerdictItems = VerdictOptions;
        TypeCodeItems = TypeCodeOptions;
        var ids = station.Recipes.List().Select(r => r.Id).Distinct().ToList();
        RecipeItems = new List<string> { "全部" }.Concat(ids).ToList();
        _ = QueryAsync();
    }

    public ObservableCollection<HistoryItemVm> Rows { get; }
    public ObservableCollection<DefectRow> Defects { get; }
    public ObservableCollection<PageSlot> PageSlots { get; }
    public IReadOnlyList<int> PageSizeOptions { get; }
    public IReadOnlyList<string> VerdictItems { get; }
    public IReadOnlyList<string> TypeCodeItems { get; }
    public IReadOnlyList<string> RecipeItems { get; }

    /// <summary>复判结论（下标与 ReviewDecision 枚举一致）。</summary>
    public IReadOnlyList<string> DecisionItems { get; } =
        new[] { ReviewDecision.ConfirmNg, ReviewDecision.FalseCall, ReviewDecision.Repair, ReviewDecision.Scrap }
            .Select(ReviewDecisions.Label).ToList();
    public IReadOnlyList<string> ReasonItems { get; } =
        ReviewDecisions.Reasons.Select(r => r.Label).ToList();

    [ObservableProperty] private DateTime? _fromDate = DateTime.Now.Date.AddDays(-7);
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private int _verdictIndex;
    [ObservableProperty] private int _typeCodeIndex;
    [ObservableProperty] private int _recipeIndex;
    [ObservableProperty] private string _partIdText = "";
    [ObservableProperty] private string _pageText = "0 / 0";
    [ObservableProperty] private string _pageSummaryText = "第 0 / 0 页";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private int _pageSize = 100;
    [ObservableProperty] private bool _canPrev;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private HistoryItemVm? _selectedRow;
    [ObservableProperty] private ImageSource? _rawImage;
    [ObservableProperty] private ImageSource? _overlayImage;
    [ObservableProperty] private string _detailHeader = "选择一条记录查看详情";
    [ObservableProperty] private string _detailVerdictText = "";
    [ObservableProperty] private Brush _detailVerdictBrush = Brushes.Gray;
    [ObservableProperty] private bool _hasDetail;
    [ObservableProperty] private bool _hasRows = true;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _emptyText = "没有符合条件的记录";

    // ---------- 复判 ----------
    [ObservableProperty] private bool _onlyPendingReview;
    [ObservableProperty] private int _decisionIndex;
    [ObservableProperty] private int _reasonIndex;
    [ObservableProperty] private string _reviewNote = "";
    [ObservableProperty] private string _reviewStatusText = "";

    partial void OnOnlyPendingReviewChanged(bool value)
    {
        _pageIndex = 1;
        _ = LoadPageAsync();
    }

    /// <summary>复判权限：已登录且非只读角色（本页本身已由角色门控）。</summary>
    public bool CanReview => App.CurrentUser != null && SelectedRow is { IsPendingReview: true };

    public bool CanExport => !IsExporting;

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    [ObservableProperty] private DefectRow? _selectedDefect;

    public int OverlayPixelWidth { get; private set; }
    public int OverlayPixelHeight { get; private set; }

    private int _pageIndex = 1;
    private int _total;

    partial void OnSelectedRowChanged(HistoryItemVm? value)
    {
        OnPropertyChanged(nameof(CanReview));
        ReviewStatusText = "";
        _ = LoadDetailAsync(value);
    }

    private InspectionQuery BuildQuery(int pageIndex)
    {
        // 起止颠倒时自动对调，避免"查询无结果"假象
        var from = FromDate;
        var to = ToDate?.AddDays(1);
        if (from != null && to != null && from > ToDate)
            (from, to) = (ToDate, from?.AddDays(1));

        return new InspectionQuery
        {
            From = from,
            To = to,
            Verdict = VerdictIndex > 0 ? VerdictOptions[VerdictIndex] : null,
            TypeCode = TypeCodeIndex > 0 ? TypeCodeOptions[TypeCodeIndex] : null,
            RecipeId = RecipeIndex > 0 ? RecipeItems[RecipeIndex] : null,
            PartIdContains = string.IsNullOrWhiteSpace(PartIdText) ? null : PartIdText.Trim(),
            OnlyPendingReview = OnlyPendingReview,
            Offset = (pageIndex - 1) * PageSize,
            Limit = PageSize
        };
    }

    [RelayCommand]
    private async Task QueryAsync()
    {
        _pageIndex = 1;
        await LoadPageAsync().ConfigureAwait(true);
    }

    public void Refresh() => _ = LoadPageAsync();

    [RelayCommand]
    private async Task PrevAsync()
    {
        if (_pageIndex > 1)
        {
            _pageIndex--;
            await LoadPageAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if ((_pageIndex * PageSize) < _total)
        {
            _pageIndex++;
            await LoadPageAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoToPageAsync(int number)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        if (number < 1 || number > totalPages || number == _pageIndex) return;
        _pageIndex = number;
        await LoadPageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task FirstPageAsync()
    {
        if (_pageIndex <= 1) return;
        _pageIndex = 1;
        await LoadPageAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        if (_pageIndex >= totalPages) return;
        _pageIndex = totalPages;
        await LoadPageAsync().ConfigureAwait(true);
    }

    partial void OnPageSizeChanged(int value)
    {
        _pageIndex = 1;
        _ = LoadPageAsync();
    }

    private int _pageSeq;
    private int _detailSeq;

    private async Task LoadPageAsync()
    {
        var seq = System.Threading.Interlocked.Increment(ref _pageSeq);
        try
        {
            var q = BuildQuery(_pageIndex);
            var (rows, total) = await Task.Run(() => _station.Db.QueryInspections(q)).ConfigureAwait(true);
            if (seq != _pageSeq) return; // 已有更新的翻页请求，丢弃本次陈旧结果
            _total = total;

            Rows.Clear();
            foreach (var r in rows)
                Rows.Add(ToItem(r));

            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
            PageText = $"{_pageIndex} / {totalPages}";
            PageSummaryText = $"第 {_pageIndex} / {totalPages} 页";
            TotalText = $"共 {total:N0} 条";
            RebuildPageSlots(totalPages);
            HasRows = total > 0;
            EmptyText = "没有符合条件的记录";
            CanPrev = _pageIndex > 1;
            CanNext = _pageIndex * PageSize < total;
        }
        catch (Exception ex)
        {
            _dialogs.Warn("查询失败: " + ex.Message, "检测历史");
        }
    }

    // 首页/末页固定出现，当前页左右各 2 页
    private void RebuildPageSlots(int totalPages)
    {
        PageSlots.Clear();
        if (totalPages <= 1)
        {
            PageSlots.Add(new PageSlot { Number = 1, IsCurrent = true });
            return;
        }

        var window = new SortedSet<int> { 1, totalPages };
        for (var i = _pageIndex - 2; i <= _pageIndex + 2; i++)
            if (i >= 1 && i <= totalPages)
                window.Add(i);

        int? prev = null;
        foreach (var n in window)
        {
            if (prev != null && n - prev > 1)
                PageSlots.Add(new PageSlot { Number = 0 });
            PageSlots.Add(new PageSlot { Number = n, IsCurrent = n == _pageIndex });
            prev = n;
        }
    }

    private static HistoryItemVm ToItem(InspectionRow r)
    {
        var isFault = r.Verdict.StartsWith("FAULT", StringComparison.OrdinalIgnoreCase);
        return new HistoryItemVm
        {
            Row = r,
            TimeText = DateTime.TryParse(r.Ts, null, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToString("MM-dd HH:mm:ss")
                : r.Ts,
            VerdictText = isFault ? "FAULT" : r.Verdict,
            VerdictBrush = Res(isFault ? "WarnBrush" : (r.IsOk ? "OkBrush" : "NgBrush"), Brushes.Gray),
            RecipeText = $"{r.RecipeId} v{r.RecipeVersion}",
            ElapsedText = $"{r.EngineMs:F0}",
            FinalVerdictBrush = string.IsNullOrEmpty(r.FinalVerdict)
                ? Res("MutedBrush", Brushes.Gray)
                : Res(r.FinalVerdict == "OK" ? "OkBrush" : "NgBrush", Brushes.Gray),
            ReviewText = string.IsNullOrEmpty(r.ReviewUser)
                ? ""
                : r.ReviewUser + " · " + FmtTime(r.ReviewTs)
        };
    }

    private static string FmtTime(string? ts) =>
        DateTime.TryParse(ts, null, DateTimeStyles.RoundtripKind, out var t) ? t.ToString("MM-dd HH:mm") : "";

    private async Task LoadDetailAsync(HistoryItemVm? item)
    {
        if (item == null) return;
        var seq = System.Threading.Interlocked.Increment(ref _detailSeq);
        var r = item.Row;
        DetailHeader = $"{r.Id} · {r.RecipeId}@{r.RecipeVersion} · {r.Engine}";
        DetailVerdictText = r.Verdict;
        DetailVerdictBrush = Res(r.Verdict.StartsWith("FAULT", StringComparison.OrdinalIgnoreCase)
            ? "WarnBrush" : (r.IsOk ? "OkBrush" : "NgBrush"), Brushes.Gray);
        HasDetail = true;
        RawImage = LoadImage(r.RawPath);
        OverlayImage = LoadImage(r.OvlPath);
        OverlayPixelWidth = (OverlayImage as BitmapImage)?.PixelWidth ?? 0;
        OverlayPixelHeight = (OverlayImage as BitmapImage)?.PixelHeight ?? 0;
        SelectedDefect = null;

        Defects.Clear();
        var defs = await Task.Run(() => _station.Db.GetDefects(r.Id)).ConfigureAwait(true);
        if (seq != _detailSeq) return; // 已切到别的记录，丢弃本次明细
        foreach (var d in defs)
            Defects.Add(d);
    }

    private static ImageSource? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (IsExporting) return;
        IsExporting = true;
        try
        {
            var q = BuildQuery(1);
            q.Offset = 0;
            q.Limit = 1_000_000;
            var (rows, _) = await Task.Run(() => _station.Db.QueryInspections(q)).ConfigureAwait(true);
            if (rows.Count == 0)
            {
                _dialogs.Info("当前筛选无数据。", "导出");
                return;
            }

            var (path, dir) = await Task.Run(() =>
            {
                var exportDir = Path.Combine(_station.Config.Storage.OutputRoot, "exports");
                Directory.CreateDirectory(exportDir);
                var p = Path.Combine(exportDir, $"history_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("timestamp,inspection_id,part_id,verdict,elapsed_ms,defect_count,recipe_id,recipe_version,recipe_hash,engine,fault,raw_path,ovl_path,yield_count");
                foreach (var r in rows)
                {
                    sb.AppendLine(string.Join(",",
                        "\"" + r.Ts + "\"", Esc(r.Id), Esc(r.PartId ?? ""), "\"" + r.Verdict + "\"",
                        r.EngineMs.ToString("F2", CultureInfo.InvariantCulture),
                        r.DefectCount.ToString(CultureInfo.InvariantCulture),
                        Esc(r.RecipeId), Esc(r.RecipeVersion), Esc(r.RecipeHash), Esc(r.Engine),
                        Esc(r.FaultCode ?? ""), Esc(r.RawPath ?? ""), Esc(r.OvlPath ?? ""),
                        r.CountsTowardYield ? "1" : "0"));
                }
                File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
                return (p, exportDir);
            }).ConfigureAwait(true);

            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Warn("导出失败: " + ex.Message, "导出");
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private async Task SubmitReviewAsync()
    {
        var sel = SelectedRow;
        if (sel == null) return;
        // net48 无 Math.Clamp
        var di = DecisionIndex < 0 ? 0 : (DecisionIndex > 3 ? 3 : DecisionIndex);
        var decision = (ReviewDecision)di;
        var reasons = ReviewDecisions.Reasons;
        var ri = ReasonIndex < 0 ? 0 : (ReasonIndex >= reasons.Length ? reasons.Length - 1 : ReasonIndex);
        var reason = reasons[ri].Code;
        var reviewer = App.CurrentUser?.Name ?? "";
        var note = ReviewNote;
        var id = sel.Row.Id;

        // 只提交一次（回写终判是事务性的，不能重试重入）
        var (ok, error) = await Task.Run(() =>
        {
            var svc = new ReviewService(_station.Db);
            var success = svc.Submit(sel.Row, decision, reason, note, reviewer, out var err);
            return (success, err);
        }).ConfigureAwait(true);

        if (!ok)
        {
            ReviewStatusText = error;
            return;
        }

        ReviewStatusText = $"已提交：{ReviewDecisions.Label(decision)} · {ReviewDecisions.ReasonLabel(reason)}";
        ReviewNote = "";
        await LoadPageAsync().ConfigureAwait(true);
        var again = Rows.FirstOrDefault(x => x.Row.Id == id);
        if (again != null) SelectedRow = again;
    }

    [RelayCommand]
    private void OpenImagesFolder()
    {
        var sel = SelectedRow?.Row;
        if (sel == null) return;
        var dir = Path.GetDirectoryName(sel.OvlPath ?? sel.RawPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private static string Esc(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

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
}
