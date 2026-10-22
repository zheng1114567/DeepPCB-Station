using DeepPCBStation.Core.Alarms;

namespace DeepPCBStation.Services;

/// <summary>
/// 存储健康度：DB 写失败一次就亮 Critical 告警（宁可吵，不可静默丢账），
/// 之后任意一次写成功即恢复。产线不停，但操作员必须看得见"这一板没进账"。
/// </summary>
public sealed class StorageHealth
{
    private readonly AlarmService _alarms;
    private readonly object _gate = new();
    private int _fails;
    private string _lastError = "";

    public StorageHealth(AlarmService alarms) => _alarms = alarms;

    public int FailCount => Volatile.Read(ref _fails);
    public bool HasIssue => FailCount > 0;

    public string LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public event Action? Changed;

    public void Fail(Exception ex)
    {
        var n = Interlocked.Increment(ref _fails);
        var msg = ex.GetBaseException().Message;
        lock (_gate) _lastError = msg;
        _alarms.Raise(AlarmSeverity.Critical, "存储", "DB_WRITE_FAILED",
            $"检测记录写库失败 ×{n}：{msg}");
        Changed?.Invoke();
    }

    public void Success()
    {
        if (Interlocked.Exchange(ref _fails, 0) == 0) return;
        lock (_gate) _lastError = "";
        _alarms.Clear("存储", "DB_WRITE_FAILED");
        Changed?.Invoke();
    }
}
