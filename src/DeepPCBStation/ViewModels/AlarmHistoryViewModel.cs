using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Services;

namespace DeepPCBStation.ViewModels;

public sealed class AlarmHistoryItemVm
{
    public AlarmHistoryItemVm(AlarmRecord row)
    {
        Row = row;
    }

    public AlarmRecord Row { get; }
    public long Id => Row.Id;

    public string TimeText =>
        DateTime.TryParse(Row.LastTs, null, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToString("MM-dd HH:mm:ss")
            : Row.LastTs;

    public string SeverityText => Row.Severity switch
    {
        >= (int)AlarmSeverity.Critical => "严重",
        >= (int)AlarmSeverity.Warning => "警告",
        _ => "提示"
    };

    public Brush SeverityBrush => Res(Row.Severity switch
    {
        >= (int)AlarmSeverity.Critical => "NgBrush",
        >= (int)AlarmSeverity.Warning => "WarnBrush",
        _ => "MutedBrush"
    });

    public string SourceCode => Row.Source + " · " + Row.Code;
    public string Message => Row.Message;
    public string OccurrencesText => Row.Occurrences > 1 ? $"×{Row.Occurrences}" : "";
    public bool IsAcked => Row.Acked;

    public string AckText
    {
        get
        {
            if (!Row.Acked) return "未确认";
            var ts = DateTime.TryParse(Row.AckTs, null, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToString("MM-dd HH:mm")
                : "";
            return string.IsNullOrEmpty(Row.AckUser) ? ts : $"{Row.AckUser} · {ts}";
        }
    }

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

// 确认动作写审计
public sealed partial class AlarmHistoryViewModel : ObservableObject, IRefreshable
{
    private readonly StationServices _station;

    public AlarmHistoryViewModel(StationServices station)
    {
        _station = station;
        Items = new ObservableCollection<AlarmHistoryItemVm>();
        Load();
    }

    public ObservableCollection<AlarmHistoryItemVm> Items { get; }

    [ObservableProperty] private bool _onlyUnacked;
    [ObservableProperty] private AlarmHistoryItemVm? _selectedItem;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _ackError = "";
    [ObservableProperty] private bool _hasItems = true;
    /// <summary>列表里是否还有未确认项——决定「全部确认」可用态与空态反馈。</summary>
    [ObservableProperty] private bool _canAckAll;
    [ObservableProperty] private string _emptyText = "暂无告警记录";

    partial void OnOnlyUnackedChanged(bool value) => Load();

    public bool CanAckSelected => SelectedItem is { IsAcked: false } sel &&
        AlarmService.CanAcknowledge(sel.Row, App.CurrentUser?.Role, out _);

    partial void OnSelectedItemChanged(AlarmHistoryItemVm? value) =>
        OnPropertyChanged(nameof(CanAckSelected));

    public void Refresh() => Load();

    private int _loadSeq;

    private void Load() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var seq = System.Threading.Interlocked.Increment(ref _loadSeq);
        try
        {
            // 查库移到线程池，避免 UI 线程同步阻塞（P3）
            var rows = await Task.Run(() => _station.Db.ListAlarms(OnlyUnacked, 800)).ConfigureAwait(true);
            if (seq != _loadSeq) return; // 已触发更新的加载，丢弃陈旧结果
            Items.Clear();
            foreach (var r in rows) Items.Add(new AlarmHistoryItemVm(r));
            var unacked = _station.Alarms.UnackedCount;
            SummaryText = $"未确认 {unacked} · 本页 {Items.Count} 条";
            HasItems = Items.Count > 0;
            var anyUnacked = false;
            foreach (var it in Items) { if (!it.IsAcked) { anyUnacked = true; break; } }
            CanAckAll = anyUnacked;
            EmptyText = OnlyUnacked ? "没有未确认告警" : "暂无告警记录";
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq) return;
            Items.Clear();
            HasItems = false;
            CanAckAll = false;
            SummaryText = "";
            EmptyText = "读取失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void AckSelected()
    {
        var sel = SelectedItem;
        if (sel == null || sel.IsAcked) return;
        if (!_station.Alarms.Acknowledge(sel.Id, App.CurrentUser?.Name ?? "-",
                App.CurrentUser?.Role, out var error))
        {
            AckError = error;
            return;
        }
        AckError = "";
        Audit("ALARM_ACK", $"id={sel.Id} · {sel.SourceCode} · sev={sel.Row.Severity}");
        Load();
    }

    [RelayCommand]
    private void AckAll()
    {
        var n = _station.Alarms.AcknowledgeAll(App.CurrentUser?.Name ?? "-", App.CurrentUser?.Role,
            out var skipped, out var error);
        AckError = error;
        if (n > 0) Audit("ALARM_ACK_ALL", $"acked={n} skipped={skipped}");
        Load();
    }

    private void Audit(string action, string detail)
    {
        try { _station.Db.AddAudit(App.CurrentUser?.Name ?? "-", action, detail); }
        catch { /* 审计失败不阻塞 */ }
    }
}
