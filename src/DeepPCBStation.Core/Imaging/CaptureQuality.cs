using System;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 采图有效性自检（工业 AOI 必备）：把「采图异常」与「真缺陷」分开判定。
/// </summary>
/// <remarks>
/// 工业 AOI 的过杀（FCR）里有相当一部分不是算法误判，而是采图环节失效——
/// 失焦、过曝/欠曝、黑图白图、上错料号（配准残差超限）。这类板不该判 NG，
/// 也不该计入良率，正确处置是判「采集无效 · 请重拍」。
///
/// 判据一律采用<b>test 相对 template 的相对量</b>，不用绝对阈值：
/// 同一套阈值即可适应不同料号、不同批次的光照与曝光，避免换型就得重新标定。
///
/// 本模块是<b>纯函数</b>，不依赖 Halcon，可在检测前独立调用。
/// </remarks>
public static class CaptureQuality
{
    /// <summary>判定码。与 <see cref="Codes"/> 对应。</summary>
    public static class Codes
    {
        /// <summary>失焦：实测图锐度显著低于模板。</summary>
        public const string Blur = "BLUR";

        /// <summary>过曝：高饱和像素占比异常升高。</summary>
        public const string OverExposure = "OVER_EXPOSURE";

        /// <summary>欠曝：近黑像素占比异常升高。</summary>
        public const string UnderExposure = "UNDER_EXPOSURE";

        /// <summary>空图/恒值：灰度几乎无变化（相机未出图、遮光、白图）。</summary>
        public const string Flat = "FLAT";

        /// <summary>配准残差超限：图与模板对不上（上错料号、严重偏移、畸变）。</summary>
        public const string RegisterFail = "REGISTER_FAIL";
    }

    public sealed class Report
    {
        /// <summary>true=采图有效，可正常判定。</summary>
        public bool IsValid => Code == null;

        /// <summary>无效判定码（<see cref="Codes"/>）；有效时为 null。</summary>
        public string? Code { get; set; }

        /// <summary>人读原因（中文，直接上屏）。</summary>
        public string? Reason { get; set; }

        // ---------- 量测值（诊断/标定时落盘用）----------

        /// <summary>实测图平均梯度能量（Laplacian 绝对值均值）。</summary>
        public double Sharpness { get; set; }

        /// <summary>锐度比 = 实测图锐度 / 模板锐度。显著 &lt; 1 表示失焦。</summary>
        public double SharpnessRatio { get; set; }

        /// <summary>实测图高饱和（≥255−hiMargin）像素占比。</summary>
        public double SatHi { get; set; }

        /// <summary>实测图近黑（≤loMargin）像素占比。</summary>
        public double SatLo { get; set; }

        /// <summary>实测图灰度标准差。极小表示空图/恒值。</summary>
        public double Std { get; set; }

        /// <summary>实测图灰度均值。</summary>
        public double Mean { get; set; }

        /// <summary>对比度比 = 实测图标准差 / 模板标准差。显著 &lt; 1 表示成像对比度塌陷（过/欠曝）。</summary>
        public double ContrastRatio { get; set; }

        /// <summary>均值偏移 = 实测图均值 − 模板均值：正=偏亮（过曝方向），负=偏暗（欠曝方向）。</summary>
        public double MeanShift { get; set; }

        /// <summary>最佳整数配准后的残余异号像素比例（0~1）。1=完全对不上。</summary>
        public double RegisterResidual { get; set; }

        /// <summary>自检耗时（ms）。</summary>
        public double ElapsedMs { get; set; }

        public override string ToString() =>
            IsValid
                ? $"VALID sharp={Sharpness:F2} ratio={SharpnessRatio:F3} satHi={SatHi:F4} satLo={SatLo:F4} std={Std:F1} reg={RegisterResidual:F4}"
                : $"{Code} · {Reason}";
    }

    /// <summary>
    /// 自检配置。全部阈值默认<b>关闭</b>（<see cref="Enabled"/>=false），
    /// 未开启时 <see cref="Assess"/> 恒返回有效，生产行为零变化。
    /// </summary>
    public sealed class Config
    {
        /// <summary>总开关。false=不做自检（现状行为）。</summary>
        public bool Enabled { get; set; }

        // 失焦
        /// <summary>锐度比下限：test/temp 低于此值判失焦（默认 0.6）。</summary>
        public double BlurRatioMin { get; set; } = 0.6;

        /// <summary>锐度绝对下限：实测图梯度能量低于此值判失焦（默认 1.0，0=不启用绝对闸）。</summary>
        public double BlurSharpnessMin { get; set; } = 1.0;

        // 曝光
        /// <summary>高饱和判据的灰度余量：≥255−margin 视为饱和。</summary>
        public int HiMargin { get; set; } = 5;

        /// <summary>近黑判据的灰度上限。</summary>
        public int LoMargin { get; set; } = 5;

        /// <summary>
        /// 高饱和占比上限；超出且显著高于模板时判过曝。
        /// </summary>
        /// <remarks>
        /// <b>标定教训（2026-10-22，official_test 500 对实测）</b>：PCB 图像是双峰的
        /// （铜≈255 / 基材≈0），正常板 satHi 中位 <b>67%</b>、最大 <b>98.65%</b>——
        /// 任何低于 ~0.99 的绝对上限都会误杀正常板。因此本闸默认关闭（1.01 不可达），
        /// 曝光判据实际由 <see cref="SatExcessOverTemplate"/>（相对模板的增量）承担。
        /// </remarks>
        public double SatHiMax { get; set; } = 1.01;

        /// <summary>
        /// 近黑占比上限；超出且显著高于模板时判欠曝。同 <see cref="SatHiMax"/>，
        /// 正常板 satLo 最大 81.75%，绝对上限默认关闭。
        /// </summary>
        public double SatLoMax { get; set; } = 1.01;

        /// <summary>饱和类判据要求「比模板高出」的绝对量，避免批次本身亮/暗被误判。</summary>
        public double SatExcessOverTemplate { get; set; } = 0.05;

        /// <summary>
        /// 对比度比下限：test/temp 标准差比低于此值判成像对比度塌陷。
        /// </summary>
        /// <remarks>
        /// <b>为什么用对比度而不是饱和占比</b>（2026-10-22 标定的结论）：
        /// PCB 图是「暗铜 + 亮基材」的双峰分布，基材本来就已是 255 饱和
        /// （实测正常板 satHi 中位 67%、最大 98.65%），继续过曝<b>不会</b>进一步提高
        /// 饱和占比——正向对照里「+120 灰度」的合成过曝就没被饱和闸抓到。
        /// 过曝在该模态下的真实可观测效应是<b>对比度塌陷</b>（暗区被抬亮、层次丢失），
        /// 因此判据改用相对模板的标准差比，并用均值偏移的符号区分过曝/欠曝。
        /// </remarks>
        public double ContrastRatioMin { get; set; } = 0.7;

        /// <summary>判定过曝/欠曝所需的均值偏移量（灰度级）。</summary>
        public double MeanShiftForExposure { get; set; } = 10.0;

        // 空图
        /// <summary>灰度标准差下限，低于此值判空图。</summary>
        public double StdMin { get; set; } = 3.0;

        // 配准
        /// <summary>配准残差上限（残余异号像素比例）；超出判配准失败。</summary>
        public double RegisterResidualMax { get; set; } = 0.20;

        /// <summary>配准残差搜索的降采样倍率（1=原尺度）。降采样可大幅提速。</summary>
        public int RegisterDownsample { get; set; } = 4;

        /// <summary>配准残差搜索的最大整数位移（像素）。</summary>
        public int RegisterMaxShift { get; set; } = 8;
    }

    private static readonly Config Off = new Config { Enabled = false };

    /// <summary>
    /// 采图有效性自检。cfg 为 null 或 Enabled=false 时恒返回有效（零开销、零行为变化）。
    /// </summary>
    /// <param name="test">实测图灰度（stride 可为 null 表示无行填充）。</param>
    /// <param name="temp">模板图灰度；为 null 时只能做单图判据（锐度绝对闸/空图/饱和绝对闸）。</param>
    public static Report Assess(byte[] test, byte[]? temp, int width, int height, Config? cfg,
        int? stride = null, byte[]? tempStrideIgnored = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cfg ??= Off;
        var r = new Report();

        // M7：关闭时立即返回，让「零开销、零行为变化」的承诺成立。
        // 原实现在全部量测（ComputeStats×2 + RegisterResidual 位移搜索）之后才判断
        // !cfg.Enabled——关闭时这些计算全额发生。诊断字段在关闭时不填充：
        // 生产调用方（InspectionPipeline.CheckCapture）先判 Enabled 再调用，
        // --capture-diag 强制 Enabled=true 才会走到量测路径。
        if (!cfg.Enabled)
        {
            r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        var st = stride ?? width;
        var hasTemplate = temp != null && temp.Length >= width * height;

        // ---------- 单图量测（无需模板）----------
        ComputeStats(test, st, width, height, cfg, out var sharp, out var satHi, out var satLo, out var std, out var mean);
        r.Sharpness = sharp;
        r.SatHi = satHi;
        r.SatLo = satLo;
        r.Std = std;
        r.Mean = mean;

        double tSharp = 1.0, tSatHi = 0.0, tSatLo = 0.0, tStd = 0.0, tMean = 0.0;
        if (hasTemplate)
        {
            ComputeStats(temp!, width, width, height, cfg, out tSharp, out tSatHi, out tSatLo, out tStd, out tMean);
            r.SharpnessRatio = tSharp > 1e-6 ? sharp / tSharp : 1.0;
            r.ContrastRatio = tStd > 1e-6 ? std / tStd : 1.0;
            r.MeanShift = mean - tMean;
        }
        else
        {
            r.SharpnessRatio = 1.0;
            r.ContrastRatio = 1.0;
            r.MeanShift = 0.0;
        }

        if (hasTemplate)
        {
            r.RegisterResidual = RegisterResidual(test, st, temp!, width, width, height,
                cfg.RegisterDownsample, cfg.RegisterMaxShift);
        }

        r.ElapsedMs = sw.Elapsed.TotalMilliseconds;

        // ---------- 判定（顺序即优先级：先判采集硬故障，再判光照/失焦）----------
        // （关闭时的短路已前移到方法开头，见上。）
        if (std < cfg.StdMin)
        {
            r.Code = Codes.Flat;
            r.Reason = $"图像灰度几乎无变化（std={std:F1}），疑似未出图/遮光/白图，请检查相机与光源后重拍。";
            return r;
        }

        // 曝光：饱和占比为绝对闸（默认关闭，见 SatHiMax 注释），此处为相对增量闸
        if (cfg.SatHiMax <= 1.0 && satHi > cfg.SatHiMax && satHi - tSatHi > cfg.SatExcessOverTemplate)
        {
            r.Code = Codes.OverExposure;
            r.Reason = $"高饱和像素占比 {satHi:P1}（模板 {tSatHi:P1}），疑似过曝，请降低曝光/光源亮度后重拍。";
            return r;
        }

        if (cfg.SatLoMax <= 1.0 && satLo > cfg.SatLoMax && satLo - tSatLo > cfg.SatExcessOverTemplate)
        {
            r.Code = Codes.UnderExposure;
            r.Reason = $"近黑像素占比 {satLo:P1}（模板 {tSatLo:P1}），疑似欠曝，请提高曝光/光源亮度后重拍。";
            return r;
        }

        // 曝光（主判据）：对比度塌陷 + 均值偏移方向。
        // 见 ContrastRatioMin 注释——本模态下过曝的可观测效应是对比度塌陷而非饱和占比上升。
        if (hasTemplate && cfg.ContrastRatioMin > 0 && r.ContrastRatio < cfg.ContrastRatioMin
            && Math.Abs(r.MeanShift) > cfg.MeanShiftForExposure)
        {
            if (r.MeanShift > 0)
            {
                r.Code = Codes.OverExposure;
                r.Reason = $"对比度仅为模板的 {r.ContrastRatio:P0} 且整体偏亮 {r.MeanShift:F0} 灰阶，疑似过曝，请降低曝光/光源亮度后重拍。";
            }
            else
            {
                r.Code = Codes.UnderExposure;
                r.Reason = $"对比度仅为模板的 {r.ContrastRatio:P0} 且整体偏暗 {-r.MeanShift:F0} 灰阶，疑似欠曝，请提高曝光/光源亮度后重拍。";
            }
            return r;
        }

        // 配准残差：放在曝光/失焦之后——严重过曝会让二值图整体翻转而「对不上」，
        // 但根因是光照（可调曝光/光源），比「上错料号（需换料）」更具体、更可行动。
        if (cfg.RegisterResidualMax > 0 && hasTemplate && r.RegisterResidual > cfg.RegisterResidualMax)
        {
            r.Code = Codes.RegisterFail;
            r.Reason = $"图像与模板对不上（残余差异 {r.RegisterResidual:P1}），疑似上错料号/严重偏移，请核对料号后重拍。";
            return r;
        }

        if (hasTemplate && cfg.BlurRatioMin > 0 && r.SharpnessRatio < cfg.BlurRatioMin)
        {
            r.Code = Codes.Blur;
            r.Reason = $"实测图锐度仅为模板的 {r.SharpnessRatio:P0}（{sharp:F2} vs {tSharp:F2}），疑似失焦，请重新对焦后重拍。";
            return r;
        }

        if (!hasTemplate && cfg.BlurSharpnessMin > 0 && sharp < cfg.BlurSharpnessMin)
        {
            r.Code = Codes.Blur;
            r.Reason = $"实测图锐度 {sharp:F2} 低于下限 {cfg.BlurSharpnessMin:F2}，疑似失焦，请重新对焦后重拍。";
            return r;
        }

        return r;
    }

    // ---------- 量测 ----------

    private static void ComputeStats(byte[] img, int stride, int w, int h, Config cfg,
        out double sharp, out double satHi, out double satLo, out double std, out double mean)
    {
        long sum = 0, sumSq = 0;
        long grad = 0;
        long hi = 0, lo = 0;
        var hiThr = Math.Max(0, 255 - cfg.HiMargin);
        var loThr = Math.Min(255, cfg.LoMargin);
        var n = 0;

        // 内部区域跳过 1px 边界，Laplacian 不越界
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * stride;
            for (var x = 1; x < w - 1; x++)
            {
                var c = img[row + x];
                sum += c;
                sumSq += (long)c * c;
                n++;
                if (c >= hiThr) hi++;
                if (c <= loThr) lo++;

                // 3x3 Laplacian 绝对值（近似），对失焦最敏感
                var up = img[(y - 1) * stride + x];
                var dn = img[(y + 1) * stride + x];
                var lf = img[row + x - 1];
                var rt = img[row + x + 1];
                var lap = 4 * c - up - dn - lf - rt;
                grad += lap < 0 ? -lap : lap;
            }
        }

        if (n == 0) { sharp = satHi = satLo = std = mean = 0; return; }

        mean = (double)sum / n;
        var var = (double)sumSq / n - mean * mean;
        std = var > 0 ? Math.Sqrt(var) : 0.0;
        sharp = (double)grad / n;
        satHi = (double)hi / n;
        satLo = (double)lo / n;
    }

    /// <summary>
    /// 配准残差：在降采样图上搜索最佳整数平移，返回该平移下的异号像素（|差|&gt;容差）比例。
    /// 只求「能否对上」的粗判据，不追求亚像素精度，故降采样即可。
    /// </summary>
    private static double RegisterResidual(byte[] test, int testStride, byte[] temp, int tempStride,
        int w, int h, int downsample, int maxShift)
    {
        var f = Math.Max(1, downsample);
        var dw = w / f;
        var dh = h / f;
        if (dw <= 2 * maxShift + 1 || dh <= 2 * maxShift + 1) return 0.0;

        var sx0 = maxShift;
        var sx1 = dw - maxShift;
        var sy0 = maxShift;
        var sy1 = dh - maxShift;

        // 二值阈值取中位灰度附近：PCB 图是双峰（铜/基材），用 128 足够稳健
        const int binThr = 128;
        // 二值化后降采样图（取中心像素），异号计数即配准残差
        var a = new byte[dw * dh];
        var b = new byte[dw * dh];
        for (var y = 0; y < dh; y++)
        {
            var ry = y * f;
            for (var x = 0; x < dw; x++)
            {
                var rx = x * f;
                a[y * dw + x] = test[ry * testStride + rx] >= binThr ? (byte)1 : (byte)0;
                b[y * dw + x] = temp[ry * tempStride + rx] >= binThr ? (byte)1 : (byte)0;
            }
        }

        var best = long.MaxValue;
        for (var dy = -maxShift; dy <= maxShift; dy++)
        for (var dx = -maxShift; dx <= maxShift; dx++)
        {
            long diff = 0;
            for (var y = sy0; y < sy1; y++)
            {
                var ra = y * dw;
                var rb = (y + dy) * dw + dx;
                for (var x = sx0; x < sx1; x++)
                    if (a[ra + x] != b[rb + x]) diff++;
            }
            if (diff < best) best = diff;
        }

        var area = (long)(sx1 - sx0) * (sy1 - sy0);
        return area > 0 ? (double)best / area : 0.0;
    }
}
