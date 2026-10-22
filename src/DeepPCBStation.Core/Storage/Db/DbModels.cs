namespace DeepPCBStation.Core.Storage.Db;

public sealed class InspectionRow
{
    public string Id { get; set; } = "";
    public string? PartId { get; set; }
    /// <summary>ISO8601 本地时间（"O" 往返格式），按字典序比较。</summary>
    public string Ts { get; set; } = "";
    public string Verdict { get; set; } = "";
    public bool IsOk { get; set; }
    public string? FaultCode { get; set; }
    public double EngineMs { get; set; }
    public int DefectCount { get; set; }
    public string RecipeId { get; set; } = "";
    public string RecipeVersion { get; set; } = "";
    public string RecipeHash { get; set; } = "";
    public string Engine { get; set; } = "";
    public bool CountsTowardYield { get; set; }

    // ---------- 归属 ----------
    public string? Operator { get; set; }
    public string? Shift { get; set; }
    public string TriggerSource { get; set; } = "AUTO";
    public string? StationId { get; set; }

    // ---------- 作废 / 复判 ----------
    public bool IsVoid { get; set; }
    public string? VoidReason { get; set; }
    public string? ParentId { get; set; }
    public string? FinalVerdict { get; set; }
    public string? ReviewTs { get; set; }
    public string? ReviewUser { get; set; }

    public string? RawPath { get; set; }
    public string? OvlPath { get; set; }
    public string? SidecarPath { get; set; }

    /// <summary>展示用：终端判定优先于机器判定。</summary>
    public string EffectiveVerdict => string.IsNullOrEmpty(FinalVerdict) ? Verdict : FinalVerdict!;
}

public sealed class DefectRow
{
    public long Id { get; set; }
    public string InspectionId { get; set; } = "";
    public string TypeCode { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public double Score { get; set; }
}

public sealed class RecipeSnapshotRow
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Hash { get; set; } = "";
    public string? Note { get; set; }
    public string Json { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class UserRow
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Role { get; set; } = "Operator";
    public string CreatedAt { get; set; } = "";
    public string? LastLogin { get; set; }

    // ---------- 登录策略 ----------
    public int FailedCount { get; set; }
    public string? LockedUntil { get; set; }
    public bool MustChangePassword { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AuditRow
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>审计对象（如 inspection/inspectionId、alarm/alarmId）。</summary>
    public string? Entity { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
}

/// <summary>复判记录（AOI Review Station：操作员最终裁决）。</summary>
public sealed class ReviewRow
{
    public long Id { get; set; }
    public string InspectionId { get; set; } = "";
    public string Ts { get; set; } = "";
    public string Reviewer { get; set; } = "";
    /// <summary>CONFIRM_NG | FALSE_CALL | REPAIR | SCRAP</summary>
    public string Decision { get; set; } = "";
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }
    public string? PrevVerdict { get; set; }
}

public sealed class InspectionQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>"OK" | "NG" | "FAULT"（前缀匹配），null=全部。</summary>
    public string? Verdict { get; set; }
    /// <summary>存在该类型缺陷的检测（EXISTS defects）。</summary>
    public string? TypeCode { get; set; }
    public string? RecipeId { get; set; }
    /// <summary>partId 子串匹配。</summary>
    public string? PartIdContains { get; set; }
    /// <summary>操作员精确匹配。</summary>
    public string? Operator { get; set; }
    /// <summary>true=只看未复判的检测（final_verdict 为空）。</summary>
    public bool OnlyPendingReview { get; set; }
    /// <summary>true=只看终判为 OK（伪点）的检测。</summary>
    public bool OnlyFalseCall { get; set; }
    /// <summary>true=包含作废记录（VOID）；默认 false。</summary>
    public bool IncludeVoid { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class DailyYieldRow
{
    public string Day { get; set; } = "";
    public int Total { get; set; }
    public int Ok { get; set; }
    public double YieldPct { get; set; }
}

public sealed class ParetoRow
{
    public string TypeCode { get; set; } = "";
    public int Count { get; set; }
}

public sealed class KpiSummaryRow
{
    /// <summary>有效板数 = OK + NG + FAULT（不含 VOID）。</summary>
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Ng { get; set; }
    public int Fault { get; set; }
    public double AvgMs { get; set; }

    /// <summary>作废记录数（超时/断连后完成）。</summary>
    public int Void { get; set; }

    /// <summary>首检即 OK 且未被复判改判的板数（FPY 分子）。</summary>
    public int FirstPassOk { get; set; }

    /// <summary>已完成复判的检测数。</summary>
    public int Reviewed { get; set; }

    /// <summary>机器判 NG、人工终判为 OK 的伪点数。</summary>
    public int FalseCall { get; set; }

    /// <summary>区间内缺陷总数（DPPM 分子）。</summary>
    public int DefectTotal { get; set; }
}

public sealed class ReviewStatRow
{
    public int Reviewed { get; set; }
    public int FalseCall { get; set; }
    public int ConfirmedNg { get; set; }
    public int Repair { get; set; }
    public int Scrap { get; set; }
}
