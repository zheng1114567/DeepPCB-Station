using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Metrology;

/// <summary>
/// 走线量测的命令行/批处理驱动器：把 <see cref="LineMetrology"/> 变成一个
/// 真正可用的工位功能——「检测回答有没有问题，量测回答工艺参数是否在公差内」。
///
/// 两种用法：
/// 1. **直接量测**：给定 ROI 与像素当量，报线宽/节距的 px 与 μm，按公差判 PASS/FAIL；
/// 2. **反标定标**：给定该走线的真实宽度（设计值，μm），反推像素当量
///    pixelUm = 真实宽 / 实测宽，再用它把节距换算成 μm——这就是产线 AOI
///    「像素当量标定」的标准做法（用一根已知宽度的走线当标定尺）。
///
/// 与检测主线的关系：**默认不参与检测**，由操作工/配方显式调用；
/// 量测结果单独落 JSON，不污染检测判定。
/// </summary>
public sealed class TraceMeasurementRunner
{
    public sealed class Options
    {
        /// <summary>待量测图像（灰度路径；支持多张做批量统计）。</summary>
        public IReadOnlyList<string> ImagePaths { get; set; } = Array.Empty<string>();

        /// <summary>ROI：须横跨整条走线（由示教/配方给定，量测器不自动找线）。</summary>
        public int RoiX0 { get; set; }
        public int RoiY0 { get; set; }
        public int RoiX1 { get; set; }
        public int RoiY1 { get; set; }

        /// <summary>已知像素当量（μm/px）；≤0 时只给像素值。</summary>
        public double PixelUm { get; set; }

        /// <summary>标定模式：该走线的真实宽度（μm）。&gt;0 时反推像素当量。</summary>
        public double CalibTraceUm { get; set; }

        /// <summary>线宽公差（±，μm；无当量时按 px 解释）。0 = 不判。</summary>
        public double WidthTolerance { get; set; }

        /// <summary>节距公差（±，同上）。0 = 不判。</summary>
        public double PitchTolerance { get; set; }

        public string ReportsDir { get; set; } = "reports";
        public LineMetrology.Config? Config { get; set; }
    }

    public sealed class ItemReport
    {
        public string Image { get; set; } = "";
        public bool Ok { get; set; }
        public string? FaultCode { get; set; }
        public string? Reason { get; set; }
        public double WidthPx { get; set; }
        public double PitchPx { get; set; }
        public double PixelUm { get; set; }
        public double WidthUm { get; set; }
        public double PitchUm { get; set; }
        public int PairCount { get; set; }
        public int EdgeRowCount { get; set; }
        public double ElapsedMs { get; set; }
        public string WidthVerdict { get; set; } = "N/A";
        public string PitchVerdict { get; set; } = "N/A";
    }

    public sealed class BatchReport
    {
        public int Total { get; set; }
        public int Ok { get; set; }
        public int Failed { get; set; }
        public int WidthPass { get; set; }
        public int WidthFail { get; set; }
        public int PitchPass { get; set; }
        public int PitchFail { get; set; }
        public double MeanWidthPx { get; set; }
        public double StdWidthPx { get; set; }
        public double MeanPitchPx { get; set; }
        public double PixelUm { get; set; }
        public bool Calibrated { get; set; }
        public double CalibTraceUm { get; set; }
        /// <summary>参与标定反推的样本数（成功且线宽 &gt; 0 的张数）。</summary>
        public int CalibSamples { get; set; }
        /// <summary>标定中被 MAD 判为离群、未参与反推的样本数。</summary>
        public int CalibOutliers { get; set; }
        /// <summary>
        /// 名义值来源：CALIB（标定尺真实宽度）/ BATCH_MEAN_UM / BATCH_MEAN_PX。
        /// 未标定时名义值是**本批均值**，PASS 只表示「与同批一致」，不是设计公差。
        /// </summary>
        public string NominalSource { get; set; } = "NONE";
        public List<ItemReport> Items { get; set; } = new();
        public string? OutputJson { get; set; }
    }

    private string _reportsDir = "reports";

    public BatchReport Run(Options opt)
    {
        _reportsDir = opt.ReportsDir;
        var rep = new BatchReport
        {
            Total = opt.ImagePaths.Count,
            PixelUm = opt.PixelUm,
            CalibTraceUm = opt.CalibTraceUm,
        };
        if (rep.Total == 0) return rep;

        // ---------- 第一遍：逐张量测 ----------
        foreach (var path in opt.ImagePaths)
        {
            var item = new ItemReport { Image = Path.GetFileName(path) };
            rep.Items.Add(item);
            try
            {
                using var img = ImageIo.LoadGray(path);
                var m = LineMetrology.MeasureTraceWidth(
                    img.Pixels, img.Width, img.Height,
                    opt.RoiX0, opt.RoiY0, opt.RoiX1, opt.RoiY1,
                    opt.Config, opt.PixelUm);
                item.Ok = m.Ok;
                item.FaultCode = m.FaultCode;
                item.Reason = m.Reason;
                item.WidthPx = m.WidthPx;
                item.PitchPx = m.PitchPx;
                item.PairCount = m.PairCount;
                item.EdgeRowCount = m.EdgeRowCount;
                item.ElapsedMs = m.ElapsedMs;
                item.PixelUm = m.PixelUm;
                item.WidthUm = m.WidthUm;
                item.PitchUm = m.PitchUm;
                if (m.Ok) rep.Ok++; else rep.Failed++;
            }
            catch (Exception ex)
            {
                item.Ok = false;
                item.FaultCode = "IO_ERROR";
                item.Reason = ex.Message;
                rep.Failed++;
            }
        }

        // ---------- 标定：用成功样本线宽的**稳健中位数**反推像素当量 ----------
        if (opt.CalibTraceUm > 0)
        {
            // 原实现取「第一张成功样本」：那一张一旦测歪（离群），全批 μm 值跟着全错，
            // 且错得毫无征兆。改为中位数 + MAD 剔除离群后再取中位数（M8）。
            var calibWidths = rep.Items.Where(i => i.Ok && i.WidthPx > 0).Select(i => (double)i.WidthPx).ToList();
            var (medWidth, outliers) = RobustMedian(calibWidths);
            rep.CalibSamples = calibWidths.Count;
            rep.CalibOutliers = outliers;
            if (medWidth > 0)
            {
                rep.PixelUm = opt.CalibTraceUm / medWidth;
                rep.Calibrated = true;
                // 反推只依赖宽度；节距的 μm 值随之换算，全批共用同一当量
                foreach (var it in rep.Items)
                {
                    if (!it.Ok) continue;
                    it.PixelUm = rep.PixelUm;
                    it.WidthUm = it.WidthPx * rep.PixelUm;
                    it.PitchUm = it.PitchPx * rep.PixelUm;
                }
            }
        }

        // ---------- 名义值：标定模式下用标定尺真实宽度，否则用本批均值 ----------
        var widths = rep.Items.Where(i => i.Ok && i.WidthPx > 0).Select(i => i.WidthPx).ToList();
        if (widths.Count > 0)
        {
            rep.MeanWidthPx = widths.Average();
            rep.StdWidthPx = widths.Count > 1
                ? Math.Sqrt(widths.Sum(w => (w - rep.MeanWidthPx) * (w - rep.MeanWidthPx)) / (widths.Count - 1))
                : 0;
        }
        var pitches = rep.Items.Where(i => i.Ok && i.PitchPx > 0).Select(i => i.PitchPx).ToList();
        if (pitches.Count > 0) rep.MeanPitchPx = pitches.Average();

        // ---------- 名义值来源（M8）----------
        // 决定 PASS/FAIL 究竟在判定什么，必须在报告里写明：
        //   标定过 → 名义值是标定尺真实宽度，PASS = 符合设计值；
        //   未标定 → 名义值只能是本批均值，PASS = 与同批一致，**不等于**符合设计公差。
        // 不写明的话「线宽公差 PASS 100%」会被误读成「这批板符合图纸公差」。
        rep.NominalSource = rep.Calibrated ? "CALIB" : (rep.PixelUm > 0 ? "BATCH_MEAN_UM" : "BATCH_MEAN_PX");

        var useUm = rep.PixelUm > 0;
        var widthNominalUm = rep.Calibrated ? opt.CalibTraceUm : rep.MeanWidthPx * rep.PixelUm;
        var pitchNominalUm = rep.MeanPitchPx * rep.PixelUm;

        // ---------- 公差判定 ----------
        foreach (var it in rep.Items)
        {
            if (!it.Ok) continue;
            if (opt.WidthTolerance > 0)
            {
                var v = useUm ? it.WidthPx * rep.PixelUm : it.WidthPx;
                var nominal = useUm ? widthNominalUm : rep.MeanWidthPx;
                var pass = Math.Abs(v - nominal) <= opt.WidthTolerance;
                it.WidthVerdict = pass ? "PASS" : "FAIL";
                if (pass) rep.WidthPass++; else rep.WidthFail++;
            }
            if (opt.PitchTolerance > 0 && it.PitchPx > 0)
            {
                var v = useUm ? it.PitchPx * rep.PixelUm : it.PitchPx;
                var nominal = useUm ? pitchNominalUm : rep.MeanPitchPx;
                var pass = Math.Abs(v - nominal) <= opt.PitchTolerance;
                it.PitchVerdict = pass ? "PASS" : "FAIL";
                if (pass) rep.PitchPass++; else rep.PitchFail++;
            }
        }

        Render(rep, useUm ? "μm" : "px", opt);
        return rep;
    }

    /// <summary>
    /// 稳健中心估计：中位数 + MAD 剔除离群（|x−med| &gt; 3σ̂）后再取中位数。
    /// 返回 (中位数, 被剔除的离群点数)。
    ///
    /// 标定反推像素当量用它替代「取第一张成功样本」：后者一旦选中离群样本，
    /// 整批 μm 值会跟着全错且毫无征兆。样本 &lt; 4 或离散度为 0 时不做剔除（MAD 无意义）。
    /// </summary>
    public static (double Median, int Outliers) RobustMedian(IEnumerable<double> xs)
    {
        var sorted = xs.Where(x => x > 0).OrderBy(x => x).ToList();
        if (sorted.Count == 0) return (0, 0);
        var med = MedianOfSorted(sorted);
        if (sorted.Count < 4) return (med, 0);

        var devs = sorted.Select(x => Math.Abs(x - med)).OrderBy(x => x).ToList();
        var mad = MedianOfSorted(devs) * 1.4826;   // MAD → σ 估计
        if (mad <= 1e-9) return (med, 0);          // 完全一致，剔除无意义

        var kept = sorted.Where(x => Math.Abs(x - med) <= 3 * mad).ToList();
        // 剔除后剩得太少就认了——此时中位数本身就比"剔完的残骸"更可信
        return kept.Count >= 3 ? (MedianOfSorted(kept), sorted.Count - kept.Count) : (med, 0);
    }

    private static double MedianOfSorted(List<double> sorted)
    {
        var n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    public string RenderJson(BatchReport rep)
    {
        Directory.CreateDirectory(_reportsDir);
        var path = Path.Combine(_reportsDir, $"trace_measure_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rep,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        rep.OutputJson = path;
        return path;
    }

    private static void Render(BatchReport rep, string unit, Options opt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[TraceMeasure] 共 {rep.Total} 张，成功 {rep.Ok}，失败 {rep.Failed}");
        if (rep.Calibrated)
            sb.AppendLine($"[TraceMeasure] 标定：走线真实宽 {opt.CalibTraceUm:F2}μm → 像素当量 {rep.PixelUm:F6} μm/px" +
                          $"（样本 {rep.CalibSamples} 张，剔除离群 {rep.CalibOutliers} 张）");
        else if (rep.PixelUm > 0)
            sb.AppendLine($"[TraceMeasure] 像素当量 {rep.PixelUm:F6} μm/px（外部给定）");
        else
            sb.AppendLine("[TraceMeasure] 未给像素当量，仅输出像素值");

        // M8：名义值来源必须显式写出，否则 PASS 会被误读为「符合设计公差」
        if (rep.WidthPass + rep.WidthFail > 0 || rep.PitchPass + rep.PitchFail > 0)
        {
            sb.AppendLine(rep.NominalSource == "CALIB"
                ? $"[TraceMeasure] 名义值来源：标定尺（{opt.CalibTraceUm:F2}μm）—— PASS 表示符合设计值"
                : "[TraceMeasure] 名义值来源：本批均值（未标定）—— PASS **仅**表示与同批一致，不是设计公差");
        }

        sb.AppendLine($"  {"image",-30} {"w(px)",9} {"pitch(px)",10} {"w(" + unit + ")",9} {"pitch(" + unit + ")",11} {"pairs",6} {"ms",7}  verdict");
        foreach (var it in rep.Items)
        {
            if (!it.Ok)
            {
                sb.AppendLine($"  {it.Image,-30} {"-",9} {"-",10} {"-",9} {"-",11} {"-",6} {it.ElapsedMs,7:F1}  {it.FaultCode}");
                continue;
            }
            var wu = rep.PixelUm > 0 ? it.WidthUm.ToString("F3", CultureInfo.InvariantCulture) : "-";
            var pu = rep.PixelUm > 0 && it.PitchPx > 0
                ? it.PitchUm.ToString("F3", CultureInfo.InvariantCulture) : "-";
            sb.AppendLine($"  {it.Image,-30} {it.WidthPx,9:F4} {it.PitchPx,10:F4} {wu,9} {pu,11} {it.PairCount,6} {it.ElapsedMs,7:F1}  {it.WidthVerdict}/{it.PitchVerdict}");
        }
        if (rep.Ok > 0)
            sb.AppendLine($"[TraceMeasure] 线宽均值 {rep.MeanWidthPx:F4}px ± {rep.StdWidthPx:F4}px（批内重复性）");
        if (rep.WidthPass + rep.WidthFail > 0)
            sb.AppendLine($"[TraceMeasure] 线宽公差判定：PASS {rep.WidthPass} / FAIL {rep.WidthFail}");
        if (rep.PitchPass + rep.PitchFail > 0)
            sb.AppendLine($"[TraceMeasure] 节距公差判定：PASS {rep.PitchPass} / FAIL {rep.PitchFail}");
        Console.Write(sb.ToString());
    }
}
