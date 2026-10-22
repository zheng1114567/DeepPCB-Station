namespace DeepPCBStation.Core.Alarms;

// 数值越大越优先
public enum AlarmSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public sealed class AlarmRecord
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string LastTs { get; set; } = "";
    public int Severity { get; set; }
    public string Source { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public int Occurrences { get; set; } = 1;
    public bool Acked { get; set; }
    public string? AckTs { get; set; }
    public string? AckUser { get; set; }

    /// <summary>条件已恢复（源状态已回转）。恢复≠确认：确认是人的动作，恢复是设备状态。</summary>
    public bool Cleared { get; set; }
    public string? ClearedTs { get; set; }

    public AlarmSeverity SeverityEnum => (AlarmSeverity)Severity;
}
