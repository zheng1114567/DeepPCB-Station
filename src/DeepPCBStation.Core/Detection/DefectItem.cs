namespace DeepPCBStation.Core.Detection;

public sealed class DefectItem
{
    /// <summary>OPEN | MOUSEBITE（首期承诺类）。分型为长宽比启发式、非可靠分类器；配方已冻结勿再拧。
    /// 可由 <c>DlTypeClassifier</c> 旁路覆盖（只改标签，不改框与检出）。</summary>
    public string TypeCode { get; set; } = "OPEN";
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public double Score { get; init; }

    /// <summary>DL 分型置信度（softmax 最大值，0=未计算）。仅用于工业分档/复判参考，不参与任何闸门。</summary>
    public double TypeConfidence { get; set; }

    /// <summary>Halcon get_region_runs 或 Stub 等价 run-length。</summary>
    public int[]? RunRows { get; init; }
    public int[]? RunColBegin { get; init; }
    public int[]? RunColEnd { get; init; }

    /// <summary>边缘解释度（0=未计算）。双极性配对用：双方都高才像同一条错位边的正负鬼影。</summary>
    public double EdgeFrac { get; set; }

    /// <summary>反极性证据比（未计算为 -1）。平移鬼影在铜边另一侧必有反极性带，真缺口没有。</summary>
    public double OppRatio { get; set; } = -1;

    /// <summary>
    /// 置信度（0=未标定）。官方口径 mAP 的排序键；面积排序会让大鬼影压过真缺陷。
    /// 仅由 <c>FeatureConfidence</c> 打开时写入，不参与任何检出/抑制闸门。
    /// </summary>
    public double Confidence { get; set; }

    public bool HasRuns =>
        RunRows != null && RunColBegin != null && RunColEnd != null
        && RunRows.Length > 0
        && RunRows.Length == RunColBegin.Length
        && RunRows.Length == RunColEnd.Length;

    public override string ToString() =>
        $"{TypeCode}({X},{Y},{W}x{H},S={Score:F1},runs={(HasRuns ? RunRows!.Length : 0)})";
}
