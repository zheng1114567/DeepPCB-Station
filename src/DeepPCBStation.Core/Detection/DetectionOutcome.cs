namespace DeepPCBStation.Core.Detection;

public sealed class DetectionOutcome
{
    public bool IsOk { get; init; }
    public IReadOnlyList<DefectItem> Defects { get; init; } = Array.Empty<DefectItem>();
    public double EngineMs { get; init; }
    /// <summary>
    /// NO_HALCON（未编译 SDK）/ LICENSE（真 license 异常）/ ENGINE 等。
    /// 非空表示检测路径故障，不是 NG。
    /// </summary>
    public string? FaultCode { get; init; }

    /// <summary>诊断用：被过滤掉的候选 blob（仅检测器开启收集时非空）。</summary>
    public IReadOnlyList<RejectedBlob>? Rejected { get; init; }

    public bool IsFault => !string.IsNullOrEmpty(FaultCode);
}

/// <summary>被管线过滤的候选 blob 及其被拒原因与特征值。</summary>
public sealed class RejectedBlob
{
    public static class Reasons
    {
        public const string AreaTooSmall = "area<min";
        public const string AreaTooLarge = "area>max";
        public const string Aspect = "aspect";
        public const string PinHole = "pinhole";
        public const string Tip = "tip";
        public const string TypeMinArea = "typeMinArea";
        public const string ScaleArea = "scaleArea";
        public const string ScaleDepth = "scaleDepth";
        public const string Thin = "thin";
        public const string Edge = "edge";
        public const string Lop = "lop";
        public const string EdgePair = "edgePair";
        public const string Border = "border";
        public const string Substrate = "substrate";
    }

    public string Reason { get; init; } = "";
    public DefectItem Blob { get; init; } = new();
    public double Area { get; init; }
    public double Aspect { get; init; }
    /// <summary>未计算时为 -1。</summary>
    public double Surround { get; init; } = -1;
    public int RingComponents { get; init; } = -1;
    public double LocalWidth { get; init; } = -1;
    public double MaxDepth { get; init; } = -1;
    /// <summary>LOP 最佳解释比例（未计算为 -1）；仅 Reason=lop 时有意义。</summary>
    public double LopFrac { get; init; } = -1;
    public int LopDx { get; init; }
    public int LopDy { get; init; }
    /// <summary>反极性证据比（未计算为 -1）。</summary>
    public double OppRatio { get; init; } = -1;
}
