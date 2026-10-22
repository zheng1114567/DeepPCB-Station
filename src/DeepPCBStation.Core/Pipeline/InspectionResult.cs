using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Pipeline;

/// <summary>
/// 工位判定分类（工业 AOI 的四态口径）。
/// </summary>
/// <remarks>
/// 二态（OK/NG）在工业现场不够用：
/// <list type="bullet">
/// <item><see cref="Review"/>：有缺陷但置信度不足，转人工复判，不直接判 NG（控制 FCR 假点率）。</item>
/// <item><see cref="Invalid"/>：采图环节失效（失焦/过曝/空图/上错料号），处置是重拍，
///      既不算 NG 也不计入良率——这类板占过杀的相当一部分。</item>
/// </list>
/// </remarks>
public enum VerdictKind
{
    /// <summary>未判定（如作废）。</summary>
    None,
    /// <summary>合格。</summary>
    Ok,
    /// <summary>确认为缺陷。</summary>
    Ng,
    /// <summary>疑似缺陷，需人工复判。</summary>
    Review,
    /// <summary>采图无效，需重拍。</summary>
    Invalid,
    /// <summary>设备故障（引擎/无图/未确认换型…）。</summary>
    Fault
}

public sealed class InspectionResult
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string InspectionId { get; init; } = "";
    public string? PartId { get; init; }
    public bool IsOk { get; init; }

    /// <summary>四态判定。默认随 IsOk/FaultCode 推导，见 <see cref="Verdict"/>。</summary>
    public VerdictKind Kind { get; set; }

    /// <summary>采集无效的原因码（<see cref="Imaging.CaptureQuality.Codes"/>）；有效时为 null。</summary>
    public string? InvalidCode { get; set; }

    /// <summary>采集无效/复判的人读说明（直接上屏）。</summary>
    public string? VerdictNote { get; set; }

    /// <summary>触发复判的最低置信度（诊断/审计留痕）；未触发为 null。</summary>
    public double? ReviewTriggerConfidence { get; set; }
    public double ElapsedMs { get; init; }
    public int DefectCount { get; init; }
    public IReadOnlyList<DefectItem> Defects { get; init; } = Array.Empty<DefectItem>();
    public string RecipeName { get; init; } = "";
    public string RecipeId { get; init; } = "";
    public string RecipeVersion { get; init; } = "";
    public string RecipeHash { get; init; } = "";
    public string? ImagePath { get; set; }
    public string? RawImagePath { get; set; }
    public string? OverlayImagePath { get; set; }
    public string? SidecarJsonPath { get; set; }
    public string? FaultCode { get; init; }
    public string EngineName { get; init; } = "";

    // ---------- 归属（IPC-1782：人 / 机 / 触发源）----------

    /// <summary>触发本次检测的登录用户；未登录或系统触发为空。</summary>
    public string? Operator { get; init; }

    /// <summary>班次文本（白班/夜班）。</summary>
    public string Shift { get; init; } = "";

    /// <summary>触发来源：UI | PLC | AUTO。</summary>
    public string TriggerSource { get; init; } = "AUTO";

    public string StationId { get; init; } = "";

    /// <summary>作废记录关联的原始记录（如握手 TIMEOUT 故障行的 inspectionId）。</summary>
    public string? ParentInspectionId { get; init; }

    // ---------- 作废（超时/断连后才跑完的结果）----------

    /// <summary>true=作废：保留证据与过程记录，但不回给上位机、不计良率、不计 OK/NG。</summary>
    public bool IsVoid { get; set; }

    public string? VoidReason { get; set; }

    // ---------- 人工终判（复判后回填）----------

    public string? FinalVerdict { get; set; }
    public string? ReviewUser { get; set; }
    public string? ReviewTs { get; set; }

    public bool IsFault => !string.IsNullOrEmpty(FaultCode);

    /// <summary>
    /// 判定文本。四态：<c>OK</c> / <c>NG</c> / <c>REVIEW</c> / <c>INVALID:CODE</c> / <c>FAULT:CODE</c> / <c>VOID</c>。
    /// </summary>
    public string Verdict
    {
        get
        {
            // 采集无效优先于 FAULT：两者都不进良率，但「请重拍」与「设备故障」处置不同，
            // 分开上屏才能让人知道下一步该做什么。
            if (IsVoid) return "VOID";
            if (Kind == VerdictKind.Invalid) return $"INVALID:{InvalidCode}";
            if (IsFault) return $"FAULT:{FaultCode}";
            return Kind switch
            {
                VerdictKind.Review => "REVIEW",
                VerdictKind.Ng => "NG",
                _ => "OK"
            };
        }
    }

    /// <summary>是否计入良率分母：仅成功完成的 OK/NG/REVIEW（采集无效/Fault/作废不计）。</summary>
    public bool CountsTowardYield => !IsFault && !IsVoid && Kind != VerdictKind.Invalid;

    /// <summary>标记作废（握手超时/断连后结果迟到）。</summary>
    public void MarkVoid(string reason)
    {
        IsVoid = true;
        VoidReason = reason;
    }
}
