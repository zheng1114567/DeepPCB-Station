using System;
using DeepPCBStation.Core.Metrology;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 走线亚像素量测。
///
/// 验收方式：合成已知真值的走线图（宽度/节距精确已知，边缘带 1px 线性过渡
/// 模拟光学扩散），验证 Lanser2 亚像素边缘能恢复真值。
/// 精度断言即本模块的「合格线」——换环境/换 Halcon 版本时若退化，这里会响。
/// </summary>
public class LineMetrologyTests
{
    private const int W = 640;
    private const int H = 640;

    /// <summary>合成竖直走线图：黑基材(0) + 白铜(255)，边缘 1px 线性过渡。</summary>
    private static byte[] Traces(double[] centers, double traceWidth)
    {
        var px = new byte[W * H];
        var half = traceWidth / 2.0;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var v = 0;
            foreach (var c in centers)
            {
                var d = Math.Abs(x - c);
                if (d <= half - 0.5) v = 255;
                else if (d < half + 0.5) v = (int)Math.Round(255 * (half + 0.5 - d));
                if (v == 255) break;
            }
            px[y * W + x] = (byte)Math.Min(255, v);
        }
        return px;
    }

    private static LineMetrology.Config Cfg() => new();

    /// <summary>
    /// L8：量测配置必须**构造后只读**。
    /// 若哪天有人把 <c>init</c> 改回 <c>set</c>，共享的默认实例
    /// <see cref="LineMetrology.DefaultConfig"/> 就能被任一调用方改坏、影响后续所有量测，
    /// 而这个错误在单测里几乎查不出来——所以用反射把「只读」钉死。
    /// 判定方法：init 访问器在元数据上是带 <c>modreq(IsExternalInit)</c> 的 setter。
    /// </summary>
    [Fact]
    public void Config_所有可写属性都是init只读()
    {
        var props = typeof(LineMetrology.Config).GetProperties();
        Assert.NotEmpty(props);
        foreach (var p in props)
        {
            var setter = p.SetMethod;
            if (setter == null) continue;
            var mods = setter.ReturnParameter.GetRequiredCustomModifiers();
            Assert.Contains(mods, m => m.Name == "IsExternalInit");
        }
    }

    [Fact]
    public void DefaultConfig_是共享实例且取默认滤波器()
    {
        Assert.Same(LineMetrology.DefaultConfig, LineMetrology.DefaultConfig);
        Assert.Equal("lanser2", LineMetrology.DefaultConfig.Filter);
        Assert.True(LineMetrology.DefaultConfig.High > LineMetrology.DefaultConfig.Low);
    }

    /// <summary>
    /// M3：Halcon 相关用例由「编译期 #if USE_HALCON」改为运行时跳过。
    /// 原实现在无 SDK 的环境整个块不参与编译 → 用例从计数里<b>无声消失</b>，
    /// CI 与本地的报告数不一致，也没人知道少跑了什么。
    /// 判定条件与 csproj 同源（HALCONROOT），即 Core 是否真的编译进了 Halcon 路径；
    /// 无 SDK 时 LineMetrology 返回 NoHalcon，跑这些用例只会得到没有意义的失败。
    /// 注：xunit v2 无运行时 Skip（Assert.Skip / SkipException 需 v3），故用 SkippableFact。
    /// </summary>
    private static bool HalconAvailable()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HALCONROOT"));

    [SkippableFact]
    public void SingleTrace_RecoversTrueWidth()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, 40.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 300, 100, 340, 500, Cfg());

        Assert.True(r.Ok, r.ToString());
        Assert.InRange(r.WidthPx, 39.5, 40.5); // 亚像素误差 < 0.5px
        Assert.True(r.WidthMadPx < 0.2, $"行间重复性应极高，实测 MAD={r.WidthMadPx:F3}");
    }

    [SkippableTheory]
    [InlineData(6.0)]    // 细线
    [InlineData(12.0)]
    [InlineData(40.0)]
    [InlineData(80.0)]
    [InlineData(160.0)]  // 粗线/铜皮
    public void WidthAcrossRange_Recovered(double trueWidth)
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, trueWidth);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 200, 100, 440, 500, Cfg());

        Assert.True(r.Ok, r.ToString());
        Assert.True(Math.Abs(r.WidthPx - trueWidth) < 0.5,
            $"真值 {trueWidth}px，实测 {r.WidthPx:F3}px，误差 {Math.Abs(r.WidthPx - trueWidth):F3}px");
    }

    [SkippableFact]
    public void Width_InvariantToPosition()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var left = LineMetrology.MeasureTraceWidth(Traces(new[] { 100.0 }, 30.0), W, H, 80, 100, 120, 500, Cfg());
        var right = LineMetrology.MeasureTraceWidth(Traces(new[] { 540.0 }, 30.0), W, H, 520, 100, 560, 500, Cfg());

        Assert.True(left.Ok && right.Ok);
        Assert.True(Math.Abs(left.WidthPx - right.WidthPx) < 0.3,
            $"位置不应影响宽度：{left.WidthPx:F3} vs {right.WidthPx:F3}");
    }

    [SkippableFact]
    public void Pitch_Recovered()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        // 三条 30px 宽走线，中心间距 100px
        var img = Traces(new[] { 220.0, 320.0, 420.0 }, 30.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 190, 100, 450, 500, Cfg());

        Assert.True(r.Ok, r.ToString());
        Assert.True(r.PitchPx > 0, "应能测出节距");
        Assert.InRange(r.PitchPx, 99.0, 101.0);
    }

    [SkippableFact]
    public void Repeatable_Deterministic()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, 40.0);
        var first = LineMetrology.MeasureTraceWidth(img, W, H, 300, 100, 340, 500, Cfg());
        for (var i = 0; i < 10; i++)
        {
            var again = LineMetrology.MeasureTraceWidth(img, W, H, 300, 100, 340, 500, Cfg());
            Assert.Equal(first.WidthPx, again.WidthPx);
            Assert.Equal(first.PairCount, again.PairCount);
        }
    }

    [SkippableFact]
    public void UmConversion_Applied()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, 40.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 300, 100, 340, 500, Cfg(), pixelUm: 0.5);

        Assert.True(r.Ok);
        Assert.Equal(0.5, r.PixelUm);
        Assert.Equal(r.WidthPx * 0.5, r.WidthUm, 6);
    }

    [SkippableFact]
    public void BlankRoi_NoEdges()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, 40.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 20, 100, 80, 500, Cfg());

        Assert.False(r.Ok);
        Assert.Equal(LineMetrology.Codes.NoEdges, r.FaultCode);
    }

    [SkippableFact]
    public void RoiTooNarrow_TooFewRows()
    {
        Skip.If(!HalconAvailable(), "未编译 Halcon SDK（HALCONROOT 未设置）：量测用例跳过，检测主路径不受影响。");
        var img = Traces(new[] { 320.0 }, 40.0);
        // ROI 只覆盖走线左半边，每行只剩 1 个边缘点 → 无法成对
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 300, 100, 318, 500, Cfg());

        Assert.False(r.Ok);
        Assert.Equal(LineMetrology.Codes.TooFewRows, r.FaultCode);
    }

    // ---------- 与 Halcon 无关的行为（任何环境都跑） ----------

    [Fact]
    public void OutOfBoundsRoi_Rejected()
    {
        var img = Traces(new[] { 320.0 }, 40.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 600, 100, 700, 500, Cfg());

        Assert.False(r.Ok);
        Assert.Equal(LineMetrology.Codes.BadRoi, r.FaultCode);
    }

    [Fact]
    public void DegenerateRoi_Rejected()
    {
        var img = Traces(new[] { 320.0 }, 40.0);
        var r = LineMetrology.MeasureTraceWidth(img, W, H, 320, 100, 321, 500, Cfg());
        Assert.False(r.Ok);
        Assert.Equal(LineMetrology.Codes.BadRoi, r.FaultCode);
    }

    [Fact]
    public void MismatchedBuffer_Rejected()
    {
        var r = LineMetrology.MeasureTraceWidth(new byte[10], W, H, 100, 100, 200, 500, Cfg());
        Assert.False(r.Ok);
        Assert.Equal(LineMetrology.Codes.BadRoi, r.FaultCode);
    }
}
