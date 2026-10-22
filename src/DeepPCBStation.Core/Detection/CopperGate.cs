using HalconDotNet;

namespace DeepPCBStation.Core.Detection
{
    /// <summary>
    /// 模板铜几何闸：把「缺陷 blob 与模板铜的相对位置」做成可复用判据。
    ///
    /// 物理前提（DeepPCB 采样：铜=暗，衬底=亮，copperRegion = Threshold(0, copperMax)）：
    ///   · 缺铜类（open/mousebite/pinhole，posCands）= 铜被吃掉 → blob 必须踩在模板铜上；
    ///     整块落在衬底上的是反光/污渍，不是六类缺陷。
    ///   · 多铜类（short/spur/copper，negCands）= 模板衬底上多出铜 → 若离模板铜超过
    ///     平移搜索半径，则模板那里没有铜可错位，必然是真多铜，可免边缘解释/LOP 闸。
    /// </summary>
    public static class CopperGate
    {
        /// <summary>blob 落在模板铜内的面积占比（0..1）。</summary>
        public static double CopperCover(HObject blob, HObject copperRegion, double area)
        {
            if (area <= 0) return 1.0;
            HObject ov;
            HOperatorSet.Intersection(blob, copperRegion, out ov);
            try
            {
                HTuple a;
                HOperatorSet.AreaCenter(ov, out a, out _, out _);
                return a.D / area;
            }
            finally { ov.Dispose(); }
        }

        /// <summary>
        /// blob 是否整块落在「模板铜外扩 rad 之后」的区域之外（即离铜 &gt; rad）。
        /// cache 为一次检测内的铜外扩缓存（首调用时建立）。
        /// </summary>
        public static bool AwayFromCopper(HObject blob, HObject copperRegion, double rad, ref HObject? cache)
        {
            if (cache == null)
            {
                HObject dil;
                HOperatorSet.DilationCircle(copperRegion, out dil, rad);
                cache = dil;
            }
            HObject ov;
            HOperatorSet.Intersection(blob, cache, out ov);
            try
            {
                HTuple a;
                HOperatorSet.AreaCenter(ov, out a, out _, out _);
                return a.D < 1;
            }
            finally { ov.Dispose(); }
        }
    }
}
