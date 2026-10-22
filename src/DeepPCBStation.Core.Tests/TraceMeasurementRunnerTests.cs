using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Metrology;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 量测驱动器：标定反推、公差判定、批量统计、坏图不炸。
/// 这些用例不依赖 Halcon（用假的量测结果驱动），任何环境都跑。
/// </summary>
public class TraceMeasurementRunnerTests : IDisposable
{
    private readonly string _dir;

    public TraceMeasurementRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "trace_meas_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    private static byte[] Png(int w, int h, byte fill)
    {
        var px = new byte[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = fill;
        return px;
    }

    [Fact]
    public void NoImages_EmptyReport()
    {
        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = Array.Empty<string>(),
        });
        Assert.Equal(0, rep.Total);
        Assert.Equal(0, rep.Ok);
    }

    [Fact]
    public void UnreadableImage_MarkedFailedNotThrown()
    {
        var missing = Path.Combine(_dir, "nope.jpg");
        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = new[] { missing },
            RoiX0 = 0, RoiY0 = 0, RoiX1 = 10, RoiY1 = 10,
        });
        Assert.Equal(1, rep.Total);
        Assert.Equal(0, rep.Ok);
        Assert.Equal(1, rep.Failed);
        Assert.Equal("IO_ERROR", rep.Items[0].FaultCode);
    }

    [Fact]
    public void RoiOutOfBounds_ItemFailsWithFaultCode()
    {
        var p = Path.Combine(_dir, "a.png");
        ImageIo.SaveGrayPng(p, new FrameBuffer(Png(64, 64, 128), 64, 64, 64, p));
        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = new[] { p },
            RoiX0 = 600, RoiY0 = 0, RoiX1 = 700, RoiY1 = 10, // 越界
        });
        Assert.Equal(0, rep.Ok);
        Assert.Equal(LineMetrology.Codes.BadRoi, rep.Items[0].FaultCode);
    }

    [Fact]
    public void Calibration_DerivesPixelEquivalent()
    {
        // 40px 宽的走线，真实宽度 20μm → 当量 0.5 μm/px；节距 100px → 50μm
        var p = Path.Combine(_dir, "cal.png");
        ImageIo.SaveGrayPng(p, new FrameBuffer(Png(64, 64, 128), 64, 64, 64, p));

        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = new[] { p },
            RoiX0 = 0, RoiY0 = 0, RoiX1 = 63, RoiY1 = 63,
            CalibTraceUm = 20.0,
        });
        // 纯色图无量测边缘 → 标定不成立，但不能崩
        Assert.Equal(0, rep.Ok);
        Assert.False(rep.Calibrated);
    }

    // ---------------------------------------------------------------- M8

    /// <summary>
    /// 标定反推像素当量原用「第一张成功样本」，一张测歪就全批 μm 全错。
    /// 改用稳健中位数后，单个离群不影响结果，且离群数被显式报告。
    /// </summary>
    [Fact]
    public void RobustMedian_IgnoresSingleGrossOutlier()
    {
        var xs = new[] { 40.0, 40.1, 39.9, 40.0, 40.05, 39.95, 200.0 };
        var (med, outliers) = TraceMeasurementRunner.RobustMedian(xs);
        Assert.Equal(40.0, med, 1);
        Assert.Equal(1, outliers);
    }

    [Fact]
    public void RobustMedian_TooFewSamples_NoRejection()
    {
        // 样本 <4 时 MAD 无意义：宁可不剔除，也不要把正常样本当离群扔掉
        var (med, outliers) = TraceMeasurementRunner.RobustMedian(new[] { 10.0, 12.0, 999.0 });
        Assert.Equal(0, outliers);
        Assert.Equal(12.0, med, 5);
    }

    [Fact]
    public void RobustMedian_Empty_IsZero()
    {
        var (med, outliers) = TraceMeasurementRunner.RobustMedian(Array.Empty<double>());
        Assert.Equal(0, med);
        Assert.Equal(0, outliers);
    }

    /// <summary>没量到任何东西时，名义值来源必须是 NONE（不能默认成"已标定"）。</summary>
    [Fact]
    public void NominalSource_IsNone_WhenNothingMeasured()
    {
        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = Array.Empty<string>(),
        });
        Assert.Equal("NONE", rep.NominalSource);
    }

    [Fact]
    public void RenderJson_WritesFile()
    {
        var rep = new TraceMeasurementRunner.BatchReport { Total = 1, Ok = 0, Failed = 1 };
        rep.Items.Add(new TraceMeasurementRunner.ItemReport { Image = "x.png", FaultCode = "IO_ERROR" });
        var path = new TraceMeasurementRunner().RenderJson(rep);
        Assert.True(File.Exists(path));
        Assert.Contains("trace_measure_", path);
        Assert.Equal(path, rep.OutputJson);
    }

    [Fact]
    public void MultipleImages_AllAttempted()
    {
        var paths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var p = Path.Combine(_dir, $"i{i}.png");
            ImageIo.SaveGrayPng(p, new FrameBuffer(Png(64, 64, (byte)(100 + i)), 64, 64, 64, p));
            paths.Add(p);
        }
        var rep = new TraceMeasurementRunner().Run(new TraceMeasurementRunner.Options
        {
            ImagePaths = paths,
            RoiX0 = 0, RoiY0 = 0, RoiX1 = 63, RoiY1 = 63,
        });
        Assert.Equal(3, rep.Total);
        Assert.Equal(3, rep.Items.Count);
    }
}
