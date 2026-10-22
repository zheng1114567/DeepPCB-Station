using System;
using System.IO;
using System.Linq;
using DeepPCBStation.Core.Imaging;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 采图有效性自检（Core/Imaging/CaptureQuality.cs）。
///
/// 纪律：只证明「不误杀」等于没证明——恒不触发的闸门同样不误杀。
/// 因此每个闸门都配两侧用例：正常图不触发 + 合成异常必触发。
/// </summary>
public class CaptureQualityTests
{
    private const int W = 640;
    private const int H = 640;
    private static readonly CaptureQuality.Config On = new() { Enabled = true };

    /// <summary>PCB 风格双峰图（暗铜≈0 / 亮基材≈255），空间随机。</summary>
    private static byte[] Bimodal(int seed)
    {
        var rng = new Random(seed);
        var px = new byte[W * H];
        for (var i = 0; i < px.Length; i++)
            px[i] = rng.NextDouble() < 0.5 ? (byte)0 : (byte)255;
        return px;
    }

    /// <summary>竖直条纹图：边缘锐利；模糊后结构不变（配准仍能对上）但锐度塌陷。</summary>
    private static byte[] Stripes(int period = 8)
    {
        var px = new byte[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            px[y * W + x] = (x / period) % 2 == 0 ? (byte)0 : (byte)255;
        return px;
    }

    private static byte[] Blur(byte[] src, int radius)
    {
        var dst = (byte[])src.Clone();
        var k = 2 * radius + 1;
        for (var y = radius; y < H - radius; y++)
        for (var x = radius; x < W - radius; x++)
        {
            var sum = 0;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                sum += src[(y + dy) * W + (x + dx)];
            dst[y * W + x] = (byte)(sum / (k * k));
        }
        return dst;
    }

    /// <summary>压对比度 + 整体偏移：模拟曝光导致的层次丢失。</summary>
    private static byte[] Collapse(byte[] src, double contrast, double shift)
    {
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            var v = 128 + (src[i] - 128) * contrast + shift;
            dst[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
        return dst;
    }

    // ---------- 总开关：关闭时零行为变化 ----------

    [Fact]
    public void Disabled_AlwaysValid_EvenBlackFrame()
    {
        var off = new CaptureQuality.Config { Enabled = false };
        var r = CaptureQuality.Assess(new byte[W * H], null, W, H, off);
        Assert.True(r.IsValid, "关闭自检时必须恒判有效（保持现状行为）");
    }

    [Fact]
    public void Disabled_AlwaysValid_EvenWrongMaterial()
    {
        var off = new CaptureQuality.Config { Enabled = false };
        var r = CaptureQuality.Assess(Bimodal(2), Bimodal(3), W, H, off);
        Assert.True(r.IsValid);
        // M7：关闭时必须短路——双峰图若被量测，Sharpness 必 >0
        Assert.Equal(0.0, r.Sharpness);
        Assert.Equal(0.0, r.RegisterResidual);
    }

    // ---------- 正常图零误触发 ----------

    [Fact]
    public void NormalPair_NotFlagged()
    {
        var tpl = Bimodal(11);
        var r = CaptureQuality.Assess(tpl, tpl, W, H, On);
        Assert.True(r.IsValid, r.ToString());
        Assert.InRange(r.Std, 100, 140);
    }

    [Fact]
    public void NormalPairWithSmallDefect_NotFlagged()
    {
        var tpl = Bimodal(12);
        var test = (byte[])tpl.Clone();
        for (var y = 300; y < 340; y++)
        for (var x = 300; x < 340; x++)
            test[y * W + x] = (byte)(test[y * W + x] > 128 ? 0 : 255);
        var r = CaptureQuality.Assess(test, tpl, W, H, On);
        Assert.True(r.IsValid, r.ToString());
    }

    // ---------- FLAT：未出图 / 遮光 / 白图 ----------

    [Fact]
    public void BlackFrame_FlagFlat()
    {
        var r = CaptureQuality.Assess(new byte[W * H], Bimodal(11), W, H, On);
        Assert.Equal(CaptureQuality.Codes.Flat, r.Code);
    }

    [Fact]
    public void FlatWhiteFrame_FlagFlat()
    {
        var test = Enumerable.Repeat((byte)255, W * H).ToArray();
        var r = CaptureQuality.Assess(test, Bimodal(11), W, H, On);
        Assert.Equal(CaptureQuality.Codes.Flat, r.Code);
    }

    [Fact]
    public void FlatGrayFrame_FlagFlat()
    {
        var test = Enumerable.Repeat((byte)200, W * H).ToArray();
        var r = CaptureQuality.Assess(test, Bimodal(11), W, H, On);
        Assert.Equal(CaptureQuality.Codes.Flat, r.Code);
    }

    // ---------- 曝光：对比度塌陷 + 均值偏移方向 ----------

    [Fact]
    public void OverExposed_FlagOverExposure()
    {
        var tpl = Bimodal(13);
        var r = CaptureQuality.Assess(Collapse(tpl, 0.25, +20), tpl, W, H, On);
        Assert.Equal(CaptureQuality.Codes.OverExposure, r.Code);
    }

    [Fact]
    public void UnderExposed_FlagUnderExposure()
    {
        var tpl = Bimodal(13);
        var r = CaptureQuality.Assess(Collapse(tpl, 0.25, -20), tpl, W, H, On);
        Assert.Equal(CaptureQuality.Codes.UnderExposure, r.Code);
    }

    [Fact]
    public void ContrastCollapseWithoutMeanShift_NotExposureFlag()
    {
        // 只压对比度但均值不动：可能是批次本身，不该判曝光（这是「相对判据」的意义）
        var tpl = Bimodal(14);
        var r = CaptureQuality.Assess(Collapse(tpl, 0.3, 0), tpl, W, H, On);
        Assert.NotEqual(CaptureQuality.Codes.OverExposure, r.Code);
        Assert.NotEqual(CaptureQuality.Codes.UnderExposure, r.Code);
    }

    // ---------- 失焦：锐度比塌陷 ----------

    [Fact]
    public void BlurredFrame_FlagBlur()
    {
        var tpl = Stripes();
        var r = CaptureQuality.Assess(Blur(tpl, 3), tpl, W, H, On);
        Assert.Equal(CaptureQuality.Codes.Blur, r.Code);
    }

    [Fact]
    public void SharpImage_NotFlagBlur()
    {
        var tpl = Stripes();
        var r = CaptureQuality.Assess(tpl, tpl, W, H, On);
        Assert.NotEqual(CaptureQuality.Codes.Blur, r.Code);
    }

    // ---------- 配准失败：上错料号 ----------

    [Fact]
    public void WrongMaterial_FlagRegisterFail()
    {
        var r = CaptureQuality.Assess(Bimodal(21), Bimodal(22), W, H, On);
        Assert.Equal(CaptureQuality.Codes.RegisterFail, r.Code);
    }

    [Fact]
    public void SameMaterialShiftedWithinTolerance_NotRegisterFail()
    {
        var tpl = Stripes();
        var test = (byte[])tpl.Clone();
        // 平移 2px（在 RegisterMaxShift=8 容差内）：残差搜索应能吸收
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W - 2; x++)
            test[y * W + x] = tpl[y * W + x + 2];
        var r = CaptureQuality.Assess(test, tpl, W, H, On);
        Assert.True(r.IsValid, r.ToString());
    }

    // ---------- 量测 sanity ----------

    [Fact]
    public void Metrics_AreSane()
    {
        var tpl = Bimodal(31);
        var r = CaptureQuality.Assess(tpl, tpl, W, H, On);
        Assert.True(r.RegisterResidual < 0.01, $"同图配准残差应接近 0，实测 {r.RegisterResidual:F4}");
        Assert.True(r.Sharpness > 0);
        Assert.True(r.ElapsedMs >= 0);
    }
}
