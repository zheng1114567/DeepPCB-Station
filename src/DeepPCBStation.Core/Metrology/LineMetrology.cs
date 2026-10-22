using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Metrology;

/// <summary>
/// PCB 走线亚像素量测（线宽 / 节距）。
///
/// 定位：AOI 工位的「检测 + 量测」一体能力——缺陷检测回答「有没有问题」，
/// 线宽/节距量测回答「工艺参数是否在公差内」。这是 Halcon 量测工具箱
/// （亚像素边缘 + 几何拟合）在 PCB 场景的落地。
///
/// 设计纪律（与 CaptureQuality 同模式）：
/// 1. **纯函数、不接入检测主线**：调用方显式调用，默认关闭时零行为变化；
/// 2. **不依赖模板差分**：只用单图灰度，判据全部可解释；
/// 3. **可单测**：合成已知真值的走线图即可验证亚像素恢复精度。
///
/// 方法：Halcon <c>edges_sub_pix</c>（Lanser2 高斯核亚像素边缘）→ ROI 内取点 →
/// **全局 1D 聚类**（同一条边缘的多次采样合成一个亚像素位置）→
/// **贪心边缘配对**（间距≈走线宽的两条相邻边缘即同一条走线的两侧）→
/// 多配对取中位数 + MAD（抗离群）。
/// 节距 = 相邻走线中心距的中位数。
///
/// 为什么按「全局聚类」而不是「按行取首末边缘」：ROI 内含多条走线时，
/// 每行首末边缘的距离是 ROI 总宽而非单条线宽，按行统计会直接失效。
/// </summary>
public static class LineMetrology
{
    public static class Codes
    {
        public const string NoHalcon = "NO_HALCON";
        public const string NoEdges = "NO_EDGES";
        public const string TooFewRows = "TOO_FEW_ROWS";
        public const string BadRoi = "BAD_ROI";
    }

    /// <summary>
    /// 量测配置。<b>构造后只读（init-only）</b>：这些值在一次量测过程中必须保持一致，
    /// 若为可写属性，调用方改一半再传入会让「边缘提取用旧阈值、聚类用新阈值」，
    /// 且共享的默认实例（<see cref="DefaultConfig"/>）会被任一调用方改坏、影响后续所有量测。
    /// </summary>
    public sealed class Config
    {
        /// <summary>亚像素边缘滤波器：lanser2（默认，高斯核，阶跃边缘精度最高）。</summary>
        public string Filter { get; init; } = "lanser2";

        /// <summary>平滑核半径（像素）。1 为 Halcon 默认；噪声大可加大。</summary>
        public double SmoothSize { get; init; } = 1.0;

        /// <summary>边缘滞回下界（梯度幅度）。0~255 阶跃下图取 20 足够稳。</summary>
        public double Low { get; init; } = 20.0;

        /// <summary>边缘滞回上界（梯度幅度）。</summary>
        public double High { get; init; } = 40.0;

        /// <summary>最少有效行数：低于此值认为 ROI 内没有可量测走线。</summary>
        public int MinRows { get; init; } = 8;

        /// <summary>单行线宽下界（像素）：低于此值的行视为噪声行，剔除。</summary>
        public double MinWidthPx { get; init; } = 2.0;

        /// <summary>
        /// 边缘聚类分裂阈值（像素）：同一条边缘的多次亚像素采样散布在 0.1px 以内，
        /// 而两条不同边缘的间距不小于可测的最细走线宽度，因此取 <see cref="MinWidthPx"/>
        /// 即可把「同一条边缘的重复采样」合并为一个边缘位置。
        /// </summary>
        public double MinEdgeGapPx { get; init; } = 2.0;

        /// <summary>单行线宽上界（像素）：高于此值的行视为跨界行，剔除。</summary>
        public double MaxWidthPx { get; init; } = 500.0;
    }

    public sealed class Report
    {
        public bool Ok { get; set; }
        public string? FaultCode { get; set; }
        public string? Reason { get; set; }

        /// <summary>参与统计的有效行数。</summary>
        public int EdgeRowCount { get; set; }

        /// <summary>ROI 内检出的边缘点数（诊断用）。</summary>
        public int EdgePointCount { get; set; }

        /// <summary>全图检出的亚像素轮廓数（诊断用）。</summary>
        public int ContourCount { get; set; }

        /// <summary>成功配成「走线对」的条数（= 参与统计的线宽样本数）。</summary>
        public int PairCount { get; set; }

        /// <summary>线宽中位数（像素）。</summary>
        public double WidthPx { get; set; }

        /// <summary>线宽中位数绝对偏差（像素）——行间重复性，越小越稳。</summary>
        public double WidthMadPx { get; set; }

        /// <summary>线宽最小/最大（像素），供公差判定。</summary>
        public double WidthMinPx { get; set; }
        public double WidthMaxPx { get; set; }

        /// <summary>节距中位数（像素）；ROI 内边缘不足两根时为 0。</summary>
        public double PitchPx { get; set; }

        /// <summary>像素当量（μm/px）；>0 时上面三个字段同步给出 μm 值。</summary>
        public double PixelUm { get; set; }
        public double WidthUm { get; set; }
        public double PitchUm { get; set; }

        public double ElapsedMs { get; set; }

        public override string ToString() =>
            Ok
                ? $"width={WidthPx:F3}px(±{WidthMadPx:F3}) pairs={PairCount}" +
                  (PixelUm > 0 ? $" = {WidthUm:F2}μm" : "") +
                  (PitchPx > 0 ? $" pitch={PitchPx:F3}px" : "")
                : $"{FaultCode} · {Reason}";
    }

    /// <summary>默认配置。只读且不可变，可安全共享给所有调用方。</summary>
    public static Config DefaultConfig { get; } = new();

    /// <summary>
    /// 量测一条走线的宽度。ROI 须横跨整条走线且只含这一条（由调用方保证）。
    /// </summary>
    /// <param name="gray">灰度图（stride = width）。</param>
    /// <param name="roiX0">ROI 左边界（含）。</param>
    /// <param name="roiY0">ROI 上边界（含）。</param>
    /// <param name="roiX1">ROI 右边界（含）。</param>
    /// <param name="roiY1">ROI 下边界（含）。</param>
    /// <param name="pixelUm">像素当量；≤0 时只给像素值。</param>
    public static Report MeasureTraceWidth(
        byte[] gray, int width, int height,
        int roiX0, int roiY0, int roiX1, int roiY1,
        Config? cfg = null, double pixelUm = 0)
    {
        var sw = Stopwatch.StartNew();
        cfg ??= DefaultConfig;

        if (gray.Length < (long)width * height || width <= 0 || height <= 0)
            return Fail(Codes.BadRoi, "灰度缓冲与宽高不匹配。", sw);
        if (roiX1 - roiX0 < 2 || roiY1 - roiY0 < 1
            || roiX0 < 0 || roiY0 < 0 || roiX1 >= width || roiY1 >= height)
            return Fail(Codes.BadRoi, $"ROI 越界或过小：[{roiX0},{roiY0}]-{roiX1},{roiY1} in {width}x{height}", sw);

#if !USE_HALCON
        sw.Stop();
        return Fail(Codes.NoHalcon, "未编译 Halcon SDK，量测不可用（检测主路径不受影响）。", sw);
#else
        var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
        HObject? img = null, edges = null;
        try
        {
            HOperatorSet.GenImage1(out img, "byte", width, height, handle.AddrOfPinnedObject());
            // Lanser2：基于高斯的亚像素边缘，阶跃边缘理论精度约 1/50 px
            HOperatorSet.EdgesSubPix(img, out edges, cfg.Filter, cfg.SmoothSize, cfg.Low, cfg.High);

            // 逐轮廓取点，按行分组（只保留 ROI 内的点）
            HOperatorSet.CountObj(edges, out HTuple nContour);
            var contourCount = nContour.I;
            var rows = new SortedDictionary<int, List<double>>();
            for (var i = 1; i <= nContour.I; i++)
            {
                HObject one;
                HOperatorSet.SelectObj(edges, out one, i);
                try
                {
                    HTuple r, c;
                    HOperatorSet.GetContourXld(one, out r, out c);
                    var nr = r.TupleLength();
                    for (var k = 0; k < nr; k++)
                    {
                        var px = c[k].D;
                        var py = r[k].D;
                        // ★ 边界必须四舍五入：Halcon 亚像素坐标的「整数值」常是 299.9999…，
                        //   (int) 截断会得 299 而被 ROI 左/上边界误滤（曾因此每行只剩一个边缘点）。
                        var ix = (int)Math.Round(px);
                        var iy = (int)Math.Round(py);
                        if (ix < roiX0 || ix > roiX1 || iy < roiY0 || iy > roiY1) continue;
                        if (!rows.TryGetValue(iy, out var list))
                        {
                            list = new List<double>();
                            rows[iy] = list;
                        }
                        list.Add(px);
                    }
                }
                finally { one.Dispose(); }
            }

            if (rows.Count == 0)
                return Fail(Codes.NoEdges, "ROI 内未检出任何亚像素边缘。", sw);

            var edgePoints = 0;
            foreach (var kv in rows) edgePoints += kv.Value.Count;

            // ---------- 全局边缘位置：1D 聚类 ----------
            // 簇内散布是亚像素级（<0.1px），簇间最小间隔≈走线宽度（≥数 px），
            // 因此用 MinEdgeGapPx 分裂即可把「同一条边缘的多次采样」合成一个边缘位置。
            var allCols = new List<double>();
            foreach (var kv in rows) allCols.AddRange(kv.Value);
            allCols.Sort();
            var clusters = new List<List<double>>();
            foreach (var x in allCols)
            {
                if (clusters.Count > 0 && x - clusters[clusters.Count - 1][clusters[clusters.Count - 1].Count - 1] > cfg.MinEdgeGapPx)
                    clusters.Add(new List<double>());
                if (clusters.Count == 0) clusters.Add(new List<double>());
                clusters[clusters.Count - 1].Add(x);
            }
            var edgesPos = clusters.Select(cl => Median(cl)).ToList(); // 每条边缘的亚像素位置

            if (edgesPos.Count < 2)
                return Fail(Codes.TooFewRows,
                    $"ROI 内只检出 {edgesPos.Count} 条边缘（轮廓 {contourCount} 条、行 {rows.Count}、点 {edgePoints}）；" +
                    "至少需要同一走线的两侧边缘。", sw);

            // ---------- 走线宽度种子：有效相邻边缘间距的中位数 ----------
            // 用中位数而非最小值：噪声/伪边缘会造出极小的假间距，若用它当种子会把
            // 配对窗口拉偏（真实边缘反而配不上）。等宽走线的真实间距在直方图里是主峰，
            // 中位数对少数离群值免疫（<50% 污染才失效）。
            var gapsAll = new List<double>();
            for (var k = 1; k < edgesPos.Count; k++)
            {
                var g = edgesPos[k] - edgesPos[k - 1];
                if (g >= cfg.MinWidthPx) gapsAll.Add(g); // 亚像素噪声造成的伪边缘直接丢弃
            }
            if (gapsAll.Count == 0)
                return Fail(Codes.TooFewRows,
                    $"边缘 {edgesPos.Count} 条但相邻间距全部 < {cfg.MinWidthPx}px（疑似噪声边缘）。", sw);
            gapsAll.Sort();
            var widthSeed = Median(gapsAll);

            // ---------- 贪心配对：间距≈走线宽的两条相邻边缘 = 同一条走线的两侧 ----------
            var widths = new List<double>();
            var centers = new List<double>();
            for (var k = 0; k + 1 < edgesPos.Count;)
            {
                var gap = edgesPos[k + 1] - edgesPos[k];
                if (gap >= widthSeed * 0.6 && gap <= widthSeed * 1.6)
                {
                    widths.Add(gap);
                    centers.Add((edgesPos[k] + edgesPos[k + 1]) / 2.0);
                    k += 2;
                }
                else k += 1;
            }

            if (widths.Count == 0)
                return Fail(Codes.TooFewRows,
                    $"边缘 {edgesPos.Count} 条但无法配成走线对（宽度种子 {widthSeed:F2}px）；" +
                    "ROI 可能只覆盖半条走线或含大面积铜皮。", sw);

            widths.Sort();
            var median = Median(widths);
            var devs = widths.Select(w => Math.Abs(w - median)).ToList();
            devs.Sort(); // MAD 的入参必须有序，否则「中位数」取的不是中位数
            var mad = Median(devs);

            // ---------- 节距：同一走线对序列的相邻中心距 ----------
            var gaps = new List<double>();
            for (var k = 1; k < centers.Count; k++)
            {
                var g = centers[k] - centers[k - 1];
                if (g >= cfg.MinWidthPx && g <= cfg.MaxWidthPx) gaps.Add(g);
            }
            var pitch = gaps.Count > 0 ? Median(gaps) : 0.0;

            sw.Stop();
            var rep = new Report
            {
                Ok = true,
                EdgeRowCount = rows.Count,
                EdgePointCount = edgePoints,
                ContourCount = contourCount,
                PairCount = widths.Count,
                WidthPx = median,
                WidthMadPx = mad,
                WidthMinPx = widths[0],
                WidthMaxPx = widths[widths.Count - 1],
                PitchPx = pitch,
                PixelUm = pixelUm,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
            if (pixelUm > 0)
            {
                rep.WidthUm = median * pixelUm;
                rep.PitchUm = pitch * pixelUm;
            }
            return rep;
        }
        catch (HOperatorException ex)
        {
            sw.Stop();
            return Fail(Codes.NoEdges, $"Halcon 算子异常：{ex.Message}", sw);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail("INTERNAL", ex.Message, sw);
        }
        finally
        {
            edges?.Dispose();
            img?.Dispose();
            handle.Free();
        }
#endif
    }

    /// <summary>
    /// 量测多条走线的节距。ROI 须覆盖至少两条完整走线。
    /// </summary>
    public static Report MeasurePitch(
        byte[] gray, int width, int height,
        int roiX0, int roiY0, int roiX1, int roiY1,
        Config? cfg = null, double pixelUm = 0)
        => MeasureTraceWidth(gray, width, height, roiX0, roiY0, roiX1, roiY1, cfg, pixelUm);

    private static double Median(List<double> sorted)
    {
        var n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static Report Fail(string code, string reason, Stopwatch sw)
    {
        sw.Stop();
        return new Report { Ok = false, FaultCode = code, Reason = reason, ElapsedMs = sw.Elapsed.TotalMilliseconds };
    }
}
