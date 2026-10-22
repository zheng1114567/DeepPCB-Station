namespace DeepPCBStation.Core.Pipeline;

/// <summary>触发源：谁发起了这次检测。用于 DB 归属与占用提示。</summary>
public enum TriggerSource
{
    /// <summary>操作员在运行页点击（单次 / 连续）。</summary>
    Operator,
    /// <summary>SoftPLC 通过 TCP TRIG 触发。</summary>
    Plc,
    /// <summary>程序内部（自测 / 迁移批跑）。</summary>
    AutoLoop
}

public enum LeaseResult
{
    /// <summary>拿到触发权，可以开检。</summary>
    Acquired,
    /// <summary>已有人持有触发权——立即拒绝，不排队。</summary>
    Busy,
    /// <summary>无登录会话或会话已锁定，不允许产出可追溯的检测记录。</summary>
    NoOperator
}

/// <summary>
/// 触发权仲裁（单工位互斥）。UI 与 PLC 是两条互不知情的触发通道，
/// 唯一正确的语义是"谁先拿到谁跑，另一条立即被拒"，而不是排队等闸门——
/// 排队会让 PLC 端一直等满超时才收到 TIMEOUT，这正是一板双记录的根源。
/// 持有期间 <see cref="Changed"/> 触发，UI 据此禁用按钮并显示占用来源。
/// </summary>
public sealed class StationArbiter
{
    private readonly object _gate = new();
    private TriggerSource? _holder;
    private string? _holderPartId;

    /// <summary>当前持有者；null=空闲。</summary>
    public TriggerSource? Holder { get { lock (_gate) return _holder; } }

    /// <summary>持有者正在检测的 partId（PLC 触发时有值）。</summary>
    public string? HolderPartId { get { lock (_gate) return _holderPartId; } }

    public bool IsBusy => Holder != null;

    /// <summary>用于状态条文案：空闲 / 操作员占用中 / PLC 占用中 / 自动循环中。</summary>
    public string HolderText
    {
        get
        {
            lock (_gate)
            {
                return _holder switch
                {
                    TriggerSource.Operator => string.IsNullOrEmpty(_holderPartId)
                        ? "操作员占用中" : $"操作员占用中（{_holderPartId}）",
                    TriggerSource.Plc => string.IsNullOrEmpty(_holderPartId)
                        ? "PLC 占用中" : $"PLC 占用中（{_holderPartId}）",
                    TriggerSource.AutoLoop => "自动循环中",
                    _ => "空闲"
                };
            }
        }
    }

    /// <summary>占用状态变化（获取成功 / 释放）。可能在任意线程触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 尝试获取触发权。已有人持有 → <see cref="LeaseResult.Busy"/>，立即返回不等待。
    /// </summary>
    public LeaseResult TryAcquire(TriggerSource source, string? partId, out string reason)
    {
        lock (_gate)
        {
            if (_holder is { } h)
            {
                reason = h == source
                    ? $"{SourceText(h)} 已在检测中"
                    : $"被 {SourceText(h)} 占用";
                return LeaseResult.Busy;
            }
            _holder = source;
            _holderPartId = partId;
        }
        reason = "";
        RaiseChanged();
        return LeaseResult.Acquired;
    }

    /// <summary>释放触发权。只有持有者能释放，非持有者调用是空操作。</summary>
    public void Release(TriggerSource source)
    {
        lock (_gate)
        {
            if (_holder != source) return;
            _holder = null;
            _holderPartId = null;
        }
        RaiseChanged();
    }

    /// <summary>强制复位（异常兜底：进程内残留占用时由运维入口调用）。</summary>
    public void ForceRelease()
    {
        lock (_gate)
        {
            if (_holder == null) return;
            _holder = null;
            _holderPartId = null;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* UI 回调异常不得影响检测线程 */ }
    }

    private static string SourceText(TriggerSource s) => s switch
    {
        TriggerSource.Operator => "操作员",
        TriggerSource.Plc => "PLC",
        _ => "自动循环"
    };
}

/// <summary>
/// 一次检测的"工单"：把归属（谁 / 哪个班次 / 谁触发 / 哪台机）与处理语义
/// （是否需要换型确认 / 是否落盘）一次性传进 pipeline，避免 pipeline 到处收参数。
/// <see cref="MarkVoid"/> 在超时/断连后由握手侧调用——结果仍会落盘留证，但不计良率。
/// </summary>
public sealed class InspectionTicket
{
    public string? PartId { get; init; }
    /// <summary>登录用户名；未登录为 null（此类记录不允许产出）。</summary>
    public string? Operator { get; init; }
    /// <summary>A/B/C 班次。</summary>
    public string Shift { get; init; } = "";
    public TriggerSource Trigger { get; init; } = TriggerSource.AutoLoop;
    public string StationId { get; init; } = "";
    public bool RequireChangeover { get; init; } = true;
    public bool Persist { get; init; } = true;
    /// <summary>关联的父记录（如超时故障行 → 迟到结果行）。</summary>
    public string? ParentInspectionId { get; private set; }

    /// <summary>DB / 侧车使用的触发源文本：UI | PLC | AUTO。</summary>
    public string Source => Trigger switch
    {
        TriggerSource.Operator => "UI",
        TriggerSource.Plc => "PLC",
        _ => "AUTO"
    };

    private int _voidFlag;
    private string? _voidReason;

    public bool IsVoid => Volatile.Read(ref _voidFlag) != 0;
    public string? VoidReason => Volatile.Read(ref _voidReason);

    /// <summary>标记作废。返回 true 表示本次调用是第一个标记者。</summary>
    public bool MarkVoid(string reason)
    {
        Volatile.Write(ref _voidReason, reason);
        return Interlocked.Exchange(ref _voidFlag, 1) == 0;
    }

    /// <summary>回填父记录 id（超时故障行）。</summary>
    public void SetParentInspectionId(string? id)
    {
        if (!string.IsNullOrEmpty(id)) ParentInspectionId = id;
    }

    /// <summary>两班制：08:00–19:59 = A，其余 = B。</summary>
    public static string ShiftOf(DateTime when) => when.Hour is >= 8 and < 20 ? "A" : "B";
}
