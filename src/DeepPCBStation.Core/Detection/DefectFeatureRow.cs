namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 候选级诊断特征：分型树与置信度标定共用。
/// 全部量都已按线宽归一化（Bite/Lateral），避免「绝对像素阈值」这类量纲错误。
/// 产线默认不采集（Detector.FeatureSink 为 null 即零开销）。
/// </summary>
public sealed class DefectFeatureRow
{
    /// <summary>图对标识（dump 用，产线不填）。</summary>
    public string Pair { get; set; } = "";

    /// <summary>当前（旧）分型，仅作对照。</summary>
    public string PredType { get; set; } = "";

    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>blob 面积（像素）。</summary>
    public double Area { get; set; }

    /// <summary>外接矩长宽比（≥1）。</summary>
    public double Aspect { get; set; }

    /// <summary>实心度 area/(W·H)。</summary>
    public double Fill { get; set; }

    /// <summary>blob 内最大 |test−temp|。</summary>
    public double PeakDiff { get; set; }

    /// <summary>blob 内平均 |test−temp|。</summary>
    public double MeanDiff { get; set; }

    /// <summary>局部铜线宽（距场截尾最大 ×2）。</summary>
    public double LocalWidth { get; set; } = 1;

    /// <summary>咬进模板铜的最大深度（距场最大值）。</summary>
    public double MaxDepth { get; set; }

    /// <summary>咬深 / 线宽（0~0.5）。</summary>
    public double Bite { get; set; }

    /// <summary>短边 / 线宽：咬口横向占线宽比。</summary>
    public double Lateral { get; set; }

    /// <summary>是否切断铜网（0/1）。</summary>
    public int Severed { get; set; }

    /// <summary>邻域碰到的独立铜网数（0/1/2，2=至少两块）。</summary>
    public int Nets { get; set; }

    /// <summary>邻域铜连通分量数（去掉 blob 后）。</summary>
    public int Parts { get; set; }

    /// <summary>包围环铜占比（0~1）。</summary>
    public double Surround { get; set; }

    /// <summary>是否远离模板铜（负极性 copper 判据，0/1）。</summary>
    public int AwayFromCopper { get; set; }

    /// <summary>+1 缺铜（open/mousebite/pinhole），−1 多铜（short/spur/copper）。</summary>
    public int Polarity { get; set; } = 1;

    /// <summary>边缘解释度（0=未计算）。</summary>
    public double EdgeFrac { get; set; }

    /// <summary>反极性证据比（−1=未计算）。</summary>
    public double OppRatio { get; set; } = -1;

    /// <summary>
    /// 测试图铜在缺陷邻域的「被切断度」＝第二大连通块/最大连通块（0=仍连通，≈1=彻底断开）。
    /// open 与 mousebite 的物理判据：开路把走线切断，鼠咬只是啃掉一角、铜仍连通。
    /// 本列（2×线宽窗）是置信度 S4 的 split/splitN 特征来源；分型树自 v1.2.1 起改用 SplitB。
    /// </summary>
    public double Split { get; set; }

    /// <summary>邻域测试铜的显著连通块数量（面积 ≥15px²，2×线宽窗）。</summary>
    public int SplitN { get; set; }

    /// <summary>
    /// 同 Split 但窗半径 max(8, 1.5×线宽)：分型树的 split 判据（v1.2.1 起）。
    /// 小窗减少邻域无关铜干扰——train1000 实测 open↔mousebite 错分 99→65、总分型错误 209→175，
    /// 5 折逐折改善；三个树阈值无需重调（见 PcbTypeTree）。
    /// </summary>
    public double SplitB { get; set; }

    /// <summary>dump 留档列：测试铜侧、显著块面积下限 6px²。已证伪与 Split 逐位相同（fit_v13）。</summary>
    public double SplitC { get; set; }

    /// <summary>dump 留档列：模板铜侧切断度（剪掉 blob）。已证伪——阈值换源后精度崩塌（fit_v13）。</summary>
    public double SplitT { get; set; }

    /// <summary>置信度（标定后写入 DefectItem.Confidence）。</summary>
    public double Confidence { get; set; }

    // ---------- 评估期回填 ----------
    /// <summary>与同图 GT 的最大 IoU（未命中为 −1）。</summary>
    public double MatchIou { get; set; } = -1;

    /// <summary>匹配到的 GT 类型（1..6，未命中为 0）。</summary>
    public int GtType { get; set; }

    public string ToTsv()
    {
        static string F(double v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join("\t",
            Pair, PredType, X, Y, W, H, F(Area), F(Aspect), F(Fill), F(PeakDiff), F(MeanDiff),
            F(LocalWidth), F(MaxDepth), F(Bite), F(Lateral), Severed, Nets, Parts, F(Surround),
            AwayFromCopper, Polarity, F(EdgeFrac), F(OppRatio), F(MatchIou), GtType, F(Confidence),
            F(Split), SplitN, F(SplitT), F(SplitC), F(SplitB));
    }

    public const string TsvHeader =
        "pair\tpred\tx\ty\tw\th\tarea\taspect\tfill\tpeak\tmean\tlocalW\tdepth\tbite\tlateral\t" +
        "severed\tnets\tparts\tsurround\taway\tpolarity\tedgeFrac\toppRatio\tmatchIou\tgtType\tconf\tsplit\tsplitN\tsplitT\tsplitC\tsplitB";
}
