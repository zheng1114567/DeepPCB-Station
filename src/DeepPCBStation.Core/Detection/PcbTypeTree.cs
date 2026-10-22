using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 六类分型（v1.2.1）：判据全部按线宽归一化，v1.0.0 三判据 + v1.2 拟合扩展。
///
/// 演进（实测依据 reports/features_train3/features_20261021_060322.tsv，
/// data/full 中非 official_test 的 1000 对 / 6651 命中候选，5 折 CV；official_test 500 对仅验收）：
///   · v1.0.0：surround≥0.9725→针孔；split≥0.55→开路；bite≥0.64→开路；否则鼠咬。
///     多铜侧 localW≤2.83→余铜；parts≥2→短路；否则毛刺。总体 95.4%。
///   · v1.2.0：同数据重新拟合 depth-5 树后坍缩冗余分支，总体 95.4%→96.9%
///     （缺铜 94.7%→96.1%，多铜 96.2%→97.8%；CV 95.3%/97.8%，train−CV gap 0.8pp/0pp，未过拟合）。
///   · v1.2.1：split 判据来源由 Split（2×线宽窗）换为 SplitB（1.5×线宽窗，见 DefectFeatureRow），
///     三个阈值全部不变（网格搜索确认对 SplitB 分布仍最优，零新拟合）：
///     总分型错误 209→175，open↔mousebite 99→65（提升全部来自该对），
///     5 折逐折改善 [43,44,40,50,32]→[33,39,35,44,24]，mb↔ph +2、short↔spur +2 可忽略。
///     物理：小窗减少邻域无关铜对「实测铜是否被切断」的污染，正对 open/mousebite 边界。
///
/// v1.2 相对 v1.0.0 的四处判据变化（全部来自拟合，非手工拍脑袋）：
///   ① <b>半断带</b>：split∈(0.1655, 0.4747] 时看 <c>Lateral</c>（短边/线宽）≤3.15→鼠咬、否则开路。
///      物理含义：缺口横向只占线宽约 3 倍以内是啃角，铺开更大是贯穿。该带内鼠咬 154/166、开路 33/46；
///   ② <b>深咬细线</b>：bite&gt;0.6377 且局部线宽≤15px（ln(localW)≤2.7058）时改看 surround≤0.8174→鼠咬、
///      否则针孔（深咬缺口在细线上既可能是鼠咬也可能是小针孔，n=97 两方接近，按数据落位）；
///   ③ <b>宽线深咬</b>：bite&gt;0.6377 且线宽&gt;15px → 开路（96/104）；
///   ④ <b>毛刺救回</b>：多铜侧 parts≥2 但实测铜仍连通（split≤0.1794）才是短路；
///      parts≥2 且 split&gt;0.1794（实测铜已断成两块）= 挂在网上的毛刺被邻域切成两块 → 毛刺。
///      单此条把 spur→short 误判 62→12（train1000）。
///
/// <b>本树只打标签，不参与任何检出/抑制闸门</b>，因此开关它不会改变「找到没找到」，
/// 只影响官方严格分型口径与叠图着色。
/// </summary>
public static class PcbTypeTree
{
    public const string Open = "OPEN";
    public const string Mousebite = "MOUSEBITE";
    public const string Pinhole = "PINHOLE";
    public const string Short = "SHORT";
    public const string Spur = "SPUR";
    public const string Copper = "COPPER";

    /// <summary>包围环铜占比 ≥ 此值判针孔。</summary>
    private const double PinholeSurround = 0.9725;

    /// <summary>切断度（SplitB，1.5×线宽窗）&gt; 此值判开路（近断，v1.0.0 为 0.55）。</summary>
    private const double OpenSplit = 0.4747;

    /// <summary>切断度（SplitB）≤ 此值判鼠咬（仍连通）。</summary>
    private const double MousebiteSplit = 0.1655;

    /// <summary>咬深 / 线宽 &gt; 此值进入深咬分支（v1.0.0 为 0.64）。</summary>
    private const double OpenBite = 0.6377;

    /// <summary>深咬且细线（ln(局部线宽) ≤ 此值，≈15px）时的包围度判据。</summary>
    private const double ThinTraceLnWidth = 2.7058;

    /// <summary>深咬细线分支内，包围环铜占比 ≤ 此值判鼠咬，否则针孔。</summary>
    private const double DeepBiteSurround = 0.8174;

    /// <summary>半断带内，短边/线宽 ≤ 此值判鼠咬，否则开路。</summary>
    private const double LateralMousebite = 3.1548;

    /// <summary>局部线宽 ≤ 此值（≈2.8px，等于邻域几乎没有模板铜）判悬空余铜。</summary>
    private const double CopperLocalWidth = 2.83;

    /// <summary>多铜侧 parts ≥ 2 时，实测铜切断度（SplitB）≤ 此值（仍连通）才判短路，否则毛刺。</summary>
    private const double ShortSplit = 0.1794;

    /// <summary>按特征判六类。缺铜/多铜分别走两棵子树（极性是物理先验，不做学习特征）。</summary>
    public static string Classify(DefectFeatureRow f)
    {
        return f.Polarity > 0 ? ClassifyPositive(f) : ClassifyNegative(f);
    }

    /// <summary>缺铜侧：open（切断/贯穿线宽）vs mousebite（咬一角）vs pinhole（被铜全包围）。</summary>
    private static string ClassifyPositive(DefectFeatureRow f)
    {
        // 被铜全包围 → 针孔（该分支内 982/992 为针孔，含低实心度小孔）。
        if (f.Surround >= PinholeSurround) return Pinhole;

        // 实测铜近断（第二/第一连通块面积比 > 0.47）→ 开路：走线已被切断。
        if (f.SplitB > OpenSplit) return Open;

        // 深咬（咬深超线宽 64%）：细线上可能是鼠咬也可能是针孔，看包围度；宽线上是贯穿开路。
        if (f.Bite > OpenBite)
        {
            if (Math.Log(Math.Max(1.0, f.LocalWidth)) <= ThinTraceLnWidth)
                return f.Surround <= DeepBiteSurround ? Mousebite : Pinhole;
            return Open;
        }

        // 浅咬：实测铜仍连通 → 鼠咬；半断带看缺口横向占比。
        if (f.SplitB <= MousebiteSplit) return Mousebite;
        return f.Lateral <= LateralMousebite ? Mousebite : Open;
    }

    /// <summary>多铜侧：short（跨两块独立铜网）vs spur（挂单条铜的细凸起）vs copper（悬空铜渣）。</summary>
    private static string ClassifyNegative(DefectFeatureRow f)
    {
        if (f.LocalWidth <= CopperLocalWidth) return Copper;
        // parts≥2 但实测铜仍连通 → 真短路（桥接着两块网）；
        // parts≥2 且实测铜已断 → 毛刺（凸起把邻域铜切成两块，并非跨网）。
        if (f.Parts >= 2) return f.SplitB <= ShortSplit ? Short : Spur;
        return Spur;
    }
}
