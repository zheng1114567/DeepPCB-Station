namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 候选置信度（v1.2.0）：官方口径 mAP 的排序键。
///
/// 演进（拟合依据 reports/features_train2/features_20261021_013052.tsv，
/// data/full 非 official_test 的 1000 对 / 6762 候选，标签＝IoU≥0.33 命中 GT，5 折 CV）：
///   · v1.0.0（7 特征：面积/均值差/边缘/实心度/长宽比/包围度/咬深）：cvAUC 0.782。
///     官方 test 检出 mAP11 89.3%→89.7%（相对面积排序 +0.4pp，排序仍是短板）。
///   · v1.2.0（17 特征，新增线宽/峰值/拓扑族）：cvAUC **0.838**（+5.6pp，五折一致）。
///     新增强信号：远离模板铜 away(+0.36，真余铜缺陷远离铜、鬼影贴铜边)、
///     邻域铜网数 nets(−0.86，跨网越多越可能是配准噪声)、咬进深度 lnDepth(+0.71)、
///     局部线宽 lnLocalW(+0.68，真缺陷尺寸随线宽缩放、噪声不随)。
///
/// 权重＝同一脚本 tools/fit_type_tree.py 的逻辑回归（l2=1e-3，全量 1200 步）；
/// trainAUC 0.845，train−CV gap 0.7pp（与分型树同量级，未过拟合）。
///
/// 本分数只做排序，不参与任何闸门；检出集合与 v1.0.0 逐位相同。
/// </summary>
public static class PcbConfidence
{
    private const double B = -0.0165;      // 截距
    private const double WLnArea = 0.5795;     // ln(面积)
    private const double WLnMean = -0.0246;    // ln(平均差分)
    private const double WEdge = -0.2802;      // 边缘解释度（越高越像鬼影）
    private const double WFill = -0.1515;      // 实心度
    private const double WLnAspect = -0.5262;  // ln(长宽比)
    private const double WSurround = 0.2054;   // 包围环铜占比
    private const double WBite = 0.0845;       // 咬深/线宽
    private const double WLnLocalW = 0.6834;   // ln(局部线宽)
    private const double WLnPeak = -0.0864;    // ln(峰值差分)
    private const double WParts = -0.3357;     // 邻域铜连通分量数
    private const double WSplit = -0.0043;     // 实测铜切断度
    private const double WAway = 0.3628;       // 远离模板铜（0/1）
    private const double WSevered = -0.2894;   // 模板铜被切断（0/1）
    private const double WNets = -0.8624;      // 邻域碰到的独立铜网数
    private const double WLnDepth = 0.7073;    // ln(咬进模板铜深度)
    private const double WSplitN = -0.2063;    // 邻域显著铜连通块数
    private const double WLateral = 0.3245;    // 短边/线宽

    /// <summary>返回 (0,1) 的置信度；越大越可能命中 GT。</summary>
    public static double Score(DefectFeatureRow f)
    {
        var z = B
                + WLnArea * Math.Log(Math.Max(1.0, f.Area))
                + WLnMean * Math.Log(Math.Max(1.0, f.MeanDiff))
                + WEdge * Clamp01(f.EdgeFrac)
                + WFill * Clamp01(f.Fill)
                + WLnAspect * Math.Log(Math.Max(1.0, f.Aspect))
                + WSurround * Clamp01(f.Surround)
                + WBite * f.Bite
                + WLnLocalW * Math.Log(Math.Max(1.0, f.LocalWidth))
                + WLnPeak * Math.Log(Math.Max(1.0, f.PeakDiff))
                + WParts * f.Parts
                + WSplit * f.Split
                + WAway * f.AwayFromCopper
                + WSevered * f.Severed
                + WNets * f.Nets
                + WLnDepth * Math.Log(Math.Max(1.0, f.MaxDepth))
                + WSplitN * f.SplitN
                + WLateral * f.Lateral;
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
