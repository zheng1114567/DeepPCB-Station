using System;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 配方参数解析（Core/Detection/PcbRecipeParams.cs）。
/// 该类把原先散落检测器内的 79 个「>0 ? 用值 : 默认」兜底收敛为单一真源，
/// 这些用例锁住兜底语义，防止后续重构静默改变行为。
/// </summary>
public class PcbRecipeParamsTests
{
    [Fact]
    public void Defaults_WhenConfigIsDefault()
    {
        var p = new PcbRecipeParams(new RecipeConfig());

        Assert.Equal(30, p.thresh);
        Assert.Equal(40.0, p.minArea);
        Assert.Equal(40.0, p.minOpen);
        Assert.Equal(40.0, p.minMb);
        Assert.Equal(40.0, p.selectMin);
        Assert.Equal(8000.0, p.maxArea);
        Assert.Equal(1, p.openR);
        Assert.Equal(100, p.copperMax);
        Assert.Equal(4.0, p.surroundR);
        Assert.Equal(0.95, p.surroundMin);
        Assert.Equal(6.0, p.maxAspect);
        Assert.Equal(24, p.rescueCap);
    }

    [Fact]
    public void ExplicitValues_PassThrough()
    {
        var p = new PcbRecipeParams(new RecipeConfig
        {
            DiffThreshold = 55,
            MinBlobArea = 120,
            MinBlobAreaOpen = 200,
            MinBlobAreaMousebite = 90,
            MaxBlobArea = 5000,
            OpenRadius = 2,
            CopperMaxGray = 80,
            SurroundRadius = 6,
            SurroundCopperMin = 0.9,
            MaxAspect = 8,
            MotherRescueCap = 7,
            GrowMargin = 16,
        });

        Assert.Equal(55, p.thresh);
        Assert.Equal(120.0, p.minArea);
        Assert.Equal(200.0, p.minOpen);
        Assert.Equal(90.0, p.minMb);
        Assert.Equal(90.0, p.selectMin); // selectMin = min(open, mousebite)
        Assert.Equal(5000.0, p.maxArea);
        Assert.Equal(2, p.openR);
        Assert.Equal(80, p.copperMax);
        Assert.Equal(6.0, p.surroundR);
        Assert.Equal(0.9, p.surroundMin);
        Assert.Equal(8.0, p.maxAspect);
        Assert.Equal(7, p.rescueCap);
        Assert.Equal(16.0, p.growMargin);
    }

    [Fact]
    public void InvalidValues_FallBackToDefaults()
    {
        var p = new PcbRecipeParams(new RecipeConfig
        {
            DiffThreshold = -5,
            MaxBlobArea = 0,
            OpenRadius = 0,
            CopperMaxGray = -5,
            MotherRescueCap = 0,
            GrowMargin = 0,
            GrowMaxScale = 0,
            GrowIters = 0,
        });

        Assert.Equal(30, p.thresh);          // 0/负 → 30
        Assert.Equal(8000.0, p.maxArea);     // 0 → 8000（与 DTO/属性初始值同一值）
        Assert.Equal(1, p.openR);            // 0 → 1（Math.Max(1, …)）
        Assert.Equal(100, p.copperMax);      // 负 → 100
        Assert.Equal(24, p.rescueCap);       // 0 → 24
        Assert.Equal(20.0, p.growMargin);    // 0 → 20
        Assert.Equal(3.0, p.growMaxScale);   // 0 → 3
        Assert.Equal(3, p.growIters);        // 0 → 3
    }

    [Fact]
    public void DerivedThresholds_AreConsistent()
    {
        var p = new PcbRecipeParams(new RecipeConfig { DiffThreshold = 40, LopThreshFrac = 0.5 });
        // lopThresh = max(4, round(thresh * frac)) = max(4, 20) = 20
        Assert.Equal(20, p.lopThresh);

        var tiny = new PcbRecipeParams(new RecipeConfig { DiffThreshold = 4, LopThreshFrac = 0.5 });
        Assert.Equal(4, tiny.lopThresh); // 下限 4
    }

    [Fact]
    public void MinArea_NeverBelowOne()
    {
        var p = new PcbRecipeParams(new RecipeConfig { MinBlobArea = -100 });
        Assert.True(p.minArea >= 1.0);
    }
}
