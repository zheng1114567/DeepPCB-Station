using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Storage.Db;

namespace DeepPCBStation.Services;

/// <summary>
/// 告警状态机（ISA-18.2）：
///   产生 → 未确认 → 确认（人的动作） → 条件恢复（设备状态） → 关闭。
/// 确认 ≠ 恢复：确认只是"人已看到"，条件没消失前不能从看板消失。
/// 同一 source+code 再次发生视为新一次要求，需要重新确认（防"确认后永久静音"）。
/// </summary>
public sealed class AlarmService
{
    private readonly StationDb _db;
    private readonly object _gate = new();
    /// <summary>未恢复的告警（含已确认未恢复）。</summary>
    private readonly List<AlarmRecord> _open = new();

    public AlarmService(StationDb db)
    {
        _db = db;
        try
        {
            // L7：必须是**全量**未恢复告警。原实现从 ListAlarms(limit:500) 里筛 !Cleared，
            // 一旦未恢复告警超过 500 条（或更旧的被挤出窗口），重启后看板就会漏掉告警——
            // 库里有、界面上没有，而告警是安全信号，漏显示比多显示危险得多。
            foreach (var a in _db.ListOpenAlarms())
            {
                _open.Add(a);
            }
        }
        catch (Exception ex)
        {
            StationLog.Warn(nameof(AlarmService), "载入告警失败: " + ex.Message);
        }
    }

    public event Action? Changed;

    public IReadOnlyList<AlarmRecord> Snapshot()
    {
        lock (_gate) return _open.ToList();
    }

    /// <summary>条件仍在的告警数（已确认未恢复也算：故障没消失）。</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>未确认告警数。</summary>
    public int UnackedCount
    {
        get { lock (_gate) return _open.Count(a => !a.Acked); }
    }

    /// <summary>
    /// 横幅取用的最高优先级：未确认优先，其次严重度，再次最新。
    /// 已确认未恢复的会排在未确认之后，但仍可能被显示（常亮，不闪烁）。
    /// </summary>
    public AlarmRecord? HighestActive
    {
        get
        {
            lock (_gate)
            {
                return _open
                    .OrderBy(a => a.Acked ? 1 : 0)
                    .ThenByDescending(a => a.Severity)
                    .ThenByDescending(a => a.LastTs, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
        }
    }

    /// <summary>严重告警只能由工程师/管理员确认：操作员无权消除最高级报警。</summary>
    public static bool CanAcknowledge(AlarmRecord a, string? role, out string error)
    {
        error = "";
        if (a.Severity < (int)AlarmSeverity.Critical) return true;
        if (string.Equals(role, "Engineer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return true;
        error = "严重告警需工程师或管理员确认。";
        return false;
    }

    // DB 失败不阻塞产线
    public AlarmRecord Raise(AlarmSeverity severity, string source, string code, string message)
    {
        var now = DateTime.Now;
        var ts = now.ToString("O");
        AlarmRecord record;
        var reopened = false;
        lock (_gate)
        {
            var existing = _open.FirstOrDefault(a =>
                string.Equals(a.Source, source, StringComparison.Ordinal) &&
                string.Equals(a.Code, code, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.Occurrences++;
                existing.LastTs = ts;
                existing.Message = message;
                if ((int)severity > existing.Severity) existing.Severity = (int)severity;
                // 再次发生 = 新的一次事件，需要重新确认，否则"确认过就永久静音"
                if (existing.Acked)
                {
                    existing.Acked = false;
                    existing.AckTs = null;
                    existing.AckUser = null;
                    reopened = true;
                }
                record = existing;
                try { _db.TouchAlarm(existing.Id, ts, message, existing.Severity); }
                catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "刷新失败: " + ex.Message); }
            }
            else
            {
                record = new AlarmRecord
                {
                    Ts = ts,
                    LastTs = ts,
                    Severity = (int)severity,
                    Source = source,
                    Code = code,
                    Message = message,
                    Occurrences = 1
                };
                try { record.Id = _db.InsertAlarm(record); }
                catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "写库失败: " + ex.Message); }
                _open.Add(record);
            }
        }
        if (reopened && record.Id > 0)
        {
            try { _db.ReopenAlarm(record.Id); }
            catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "重新激活失败: " + ex.Message); }
        }
        Changed?.Invoke();
        return record;
    }

    /// <summary>确认单条；Critical 需 Engineer/Admin。</summary>
    public bool Acknowledge(long id, string user, string? role, out string error)
    {
        error = "";
        AlarmRecord? item;
        lock (_gate) item = _open.FirstOrDefault(a => a.Id == id);
        if (item == null) { error = "告警不存在或已恢复。"; return false; }
        if (item.Acked) { error = "该告警已确认。"; return false; }
        if (!CanAcknowledge(item, role, out error)) return false;

        var ts = DateTime.Now;
        lock (_gate)
        {
            item.Acked = true;
            item.AckTs = ts.ToString("O");
            item.AckUser = user;
            try { _db.AckAlarm(id, user, ts); }
            catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "确认失败: " + ex.Message); }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>确认全部（权限不足的严重告警跳过，返回 skipped）。</summary>
    public int AcknowledgeAll(string user, string? role, out int skipped, out string error)
    {
        skipped = 0;
        error = "";
        var acked = 0;
        var ts = DateTime.Now;
        lock (_gate)
        {
            foreach (var a in _open.Where(x => !x.Acked).ToList())
            {
                if (!CanAcknowledge(a, role, out _)) { skipped++; continue; }
                a.Acked = true;
                a.AckTs = ts.ToString("O");
                a.AckUser = user;
                try { _db.AckAlarm(a.Id, user, ts); }
                catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "批量确认失败: " + ex.Message); }
                acked++;
            }
        }
        if (skipped > 0) error = $"{skipped} 条严重告警需工程师或管理员确认。";
        if (acked > 0) Changed?.Invoke();
        return acked;
    }

    /// <summary>条件恢复：只清同 source+code，不改变确认状态（审计留痕在 DB）。</summary>
    public int Clear(string source, string code)
    {
        var ts = DateTime.Now;
        var removed = 0;
        lock (_gate)
        {
            foreach (var a in _open
                         .Where(x => string.Equals(x.Source, source, StringComparison.Ordinal) &&
                                     string.Equals(x.Code, code, StringComparison.Ordinal))
                         .ToList())
            {
                a.Cleared = true;
                a.ClearedTs = ts.ToString("O");
                _open.Remove(a);
                removed++;
            }
        }
        try { _db.ClearAlarm(source, code, ts); }
        catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "恢复失败: " + ex.Message); }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>整源恢复（如 TCP 回到 Ready 后一次清掉所有 TCP 告警）。</summary>
    public int ClearAll(string source)
    {
        var ts = DateTime.Now;
        var removed = 0;
        lock (_gate)
        {
            foreach (var a in _open.Where(x => string.Equals(x.Source, source, StringComparison.Ordinal)).ToList())
            {
                a.Cleared = true;
                a.ClearedTs = ts.ToString("O");
                _open.Remove(a);
                removed++;
            }
        }
        try { _db.ClearAlarmsBySource(source, ts); }
        catch (Exception ex) { StationLog.Warn(nameof(AlarmService), "批量恢复失败: " + ex.Message); }
        if (removed > 0) Changed?.Invoke();
        return removed;
    }
}
