using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Recipe;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// ContentHash 契约测试。
///
/// 为什么需要它：换型确认门闩靠 ContentHash 判定「配方变没变」。若某个配方参数**没有**
/// 并入 hash payload，改了这个参数 hash 不变 → 产线不会弹「换型未确认」→ 用旧确认跑新参数。
/// 这类漏洞不会报错、不会崩溃，只会在产线上安静地发生（历史上已出过一次占位符错位）。
///
/// 这里用反射把「所有可写属性」逐个改一遍，要求 hash 必须变化；漏掉的字段会被点名。
/// 新增配方参数时若测试失败，请同步三处：RecipeConfig 属性 / RecipeFileDto + LoadFromDirectory
/// 搬运 / ComputeHashStatic 并入（以及 Save 里的 SetIf）。
/// </summary>
public class RecipeHashContractTests : IDisposable
{
    /// <summary>不参与 hash 的字段：只影响展示，或由 hash 自身派生。</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        "Name",       // 展示名
        "ContentHash" // 派生值
    };

    /// <summary>现行 v1.2.1 配方的冻结 hash（README / reports / 配方快照均引用此值）。</summary>
    private const string FrozenV121Hash = "26d991859176a03ee31cc7d5b728d646";

    private readonly string _dir;

    public RecipeHashContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dpcb_hash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    /// <summary>
    /// 「全开」配方：把所有条件并入的分支（registry / LOP / 母团 / 足迹 / z 通道 / 生长…）
    /// 都打开，使每个参数都能在 payload 里出现，才能被逐字段验证。
    /// </summary>
    private static RecipeConfig KitchenSink() => new()
    {
        Id = "hash_contract", Name = "契约", Version = "1.2.1",
        DiffThreshold = 30, MinBlobArea = 22, MinBlobAreaOpen = 18, MinBlobAreaMousebite = 11,
        TypeAwareAreaGate = true,
        MotherRescueArea = 40, MotherRescueCap = 24, ResidueGrowMaxDim = 50,
        MaxBlobArea = 8000, OpenRadius = 1, OpenRadiusNeg = -1, CopperMaxGray = 100,
        SurroundRadius = 6, SurroundCopperMin = 0.95, MaxAspect = 6,
        TipSurroundMax = 0.55, TipMaxWidth = 10, MinThickness = 1.2,
        BorderMargin = 8, BorderMinAspect = 3, SixClass = true,
        EdgeExplainMin = 0.85, EdgeExplainRadius = 2, EdgeSkipDepth = 3, EdgePrune = true,
        EdgeForcePruneArea = 300, EdgeExplainRawFallback = true,
        EdgeShallowDiff = 60, EdgeShallowMin = 0.7,
        EdgeExplainMinNeg = 0.85, EdgeExplainRadiusNeg = 1,
        ClosingRadius = 2, ClosingRadiusNeg = 2,
        PairPolarityRadius = 5, PairGhostFrac = 0.8, TopoMinArea = 30, TopoTouch = 6,
        TemplateGrayDilation = 3, RegisterShiftMax = 8, RegisterTiles = 8, RegisterMinGain = 0.1,
        RegisterSubPixel = true,
        ScaleMinAreaCoeff = 1.2, ScaleMinDepthFrac = 0.3, ScaleWidthRadius = 16,
        LopRadius = 2, LopExplainMin = 0.98, LopDepthSkip = 3, LopOppMin = 0.05, LopThreshFrac = 0.5,
        LopRescue = true, MergeRadius = 6, EdgeNetRescue = true, MergeScoreRatio = 0.3,
        LopReviewAll = true, LopReviewMin = 0.65, BridgeSkipEdge = true, BridgeThinRescue = true,
        PosCopperCoverMin = 0.4, NegAwayCopperRescue = true, PairNetKeep = true, PairKeepArea = 500,
        SatelliteRadius = 20, SatelliteRatio = 0.3, CoreBoxFrac = 0.5, RegionGrowDiff = 1,
        TypeRefineTopology = true, CopperBlobArea = 400, SpurAspectMax = 2.5,
        OpenAspectMin = 2.2, OpenSeverRadius = 12,
        GrowMargin = 20, GrowMaxScale = 3, GrowBoxScale = 1.5, GrowBoxElong = 1.15, GrowBoxPad = 1.5,
        GrowDiagonal = true, GrowIters = 3, OpenSeverDilate = 1, OpenBiteFrac = 0.8,
        TypeTree = true, FeatureConfidence = true,
        FootprintTau = 0.5, FootprintMargin = 8,
        SeedZ = 3, SeedZAbs = 12, ZEps = 2, ZWindow = 16, ZCopperRadius = 3,
        SubpixelGradient = true,
    };

    private static bool TryMutate(PropertyInfo p, RecipeConfig cfg)
    {
        var cur = p.GetValue(cfg);
        switch (cur)
        {
            case bool b: p.SetValue(cfg, !b); return true;
            case int i: p.SetValue(cfg, i + 7); return true;
            case double d: p.SetValue(cfg, d + 0.37); return true;
            case string s: p.SetValue(cfg, s + "_x"); return true;
            default: return false;
        }
    }

    // ---------- 契约：每个参数都进 hash ----------

    [Fact]
    public void EveryWritableProperty_AffectsHash()
    {
        var baseHash = RecipeStore.ComputeHashStatic(KitchenSink());
        var missing = new List<string>();

        foreach (var p in typeof(RecipeConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (Exempt.Contains(p.Name)) continue;

            var mutated = KitchenSink();
            if (!TryMutate(p, mutated)) continue;
            if (RecipeStore.ComputeHashStatic(mutated) == baseHash)
                missing.Add(p.Name);
        }

        Assert.True(missing.Count == 0,
            "以下配方参数没有进入 ContentHash payload（改它们不会触发换型确认）："
            + string.Join(", ", missing)
            + "。请在 RecipeStore.ComputeHashStatic 中并入，并同步 RecipeFileDto / LoadFromDirectory / Save 的 SetIf。");
    }

    [Fact]
    public void DisplayOnlyFields_DoNotAffectHash()
    {
        var a = KitchenSink();
        var b = KitchenSink();
        b.Name = "另一个名字";
        b.ContentHash = "deadbeef";
        Assert.Equal(RecipeStore.ComputeHashStatic(a), RecipeStore.ComputeHashStatic(b));
    }

    /// <summary>
    /// 冻结 hash：载荷格式（字段顺序/条件并入）一旦变动就会失败，必须显式确认影响面。
    /// 同时证明「把 grow* / footprint / z 通道等从 TypeRefineTopology 块中移出」是逐位等价的
    /// （v1.2.1 的 typeRefineTopology=1，移出后顺序与内容不变）。
    /// </summary>
    [Fact]
    public void ShippedRecipe_KeepsFrozenHash()
    {
        var recipesDir = FindRepoRecipesDir();
        var store = RecipeStore.LoadFromDirectory(recipesDir);
        Assert.Equal(FrozenV121Hash, store.Current.ContentHash);
        Assert.True(store.Diagnostics.Count == 0,
            "现行配方不应有加载/校验诊断：" + string.Join(" | ", store.Diagnostics));
    }

    /// <summary>未开 typeRefineTopology 的配方，改 growBoxScale 也必须改 hash（原实现会漏）。</summary>
    [Fact]
    public void GrowParams_AffectHash_EvenWithoutTypeRefine()
    {
        var a = new RecipeConfig { Id = "x", Version = "1.0.0", TypeRefineTopology = false, GrowBoxScale = 1.5 };
        var b = new RecipeConfig { Id = "x", Version = "1.0.0", TypeRefineTopology = false, GrowBoxScale = 1.6 };
        Assert.NotEqual(RecipeStore.ComputeHashStatic(a), RecipeStore.ComputeHashStatic(b));
    }

    // ---------- 校验 ----------

    [Fact]
    public void Validate_CleanRecipe_HasNoIssues()
    {
        Assert.Empty(RecipeStore.Validate(new RecipeConfig()));
    }

    [Theory]
    [InlineData(0, 0)]      // diffThreshold=0（等于关掉差分）
    [InlineData(300, 0)]    // diffThreshold 越界
    public void Validate_OutOfRangeThreshold_Reported(int threshold, int _)
    {
        var r = new RecipeConfig { DiffThreshold = threshold };
        Assert.Contains(RecipeStore.Validate(r), i => i.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InvertedAreaRange_Reported()
    {
        var r = new RecipeConfig { MinBlobArea = 500, MaxBlobArea = 100 };
        Assert.Contains(RecipeStore.Validate(r), i => i.Contains("maxBlobArea", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FractionOutOfRange_Reported()
    {
        var r = new RecipeConfig { LopRadius = 2, LopExplainMin = 1.5 };
        Assert.Contains(RecipeStore.Validate(r), i => i.Contains("lopExplainMin", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FootprintAndGrowBoxScale_Coexistence_Warns()
    {
        var r = new RecipeConfig { FootprintTau = 0.4, GrowBoxScale = 1.5 };
        Assert.Contains(RecipeStore.Validate(r), i => i.StartsWith("WARN", StringComparison.Ordinal));
    }

    // ---------- 加载：坏配方不再静默跳过 ----------

    [Fact]
    public void LoadFromDirectory_BrokenFile_ReportedInDiagnostics()
    {
        File.WriteAllText(Path.Combine(_dir, "good.json"),
            JsonSerializer.Serialize(new RecipeConfig { Id = "good", MinBlobArea = 20 }));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ this is not json");

        var store = RecipeStore.LoadFromDirectory(_dir);

        Assert.Equal("good", store.Current.Id);
        Assert.Contains(store.Diagnostics, d => d.Contains("bad.json", StringComparison.Ordinal)
                                               && d.Contains("解析失败", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromDirectory_AllFilesBroken_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "bad1.json"), "{");
        File.WriteAllText(Path.Combine(_dir, "bad2.json"), "null");

        var ex = Assert.Throws<InvalidDataException>(() => RecipeStore.LoadFromDirectory(_dir));
        Assert.Contains("bad1.json", ex.Message, StringComparison.Ordinal);
    }

    // ---------- 按类型面积闸：默认关 = 逐位等价 ----------

    [Fact]
    public void TypeAwareAreaGate_DefaultsOff_AndSelectMinUnchanged()
    {
        var off = new PcbRecipeParams(new RecipeConfig { MinBlobArea = 22 });
        Assert.False(off.typeAwareArea);
        Assert.Equal(22.0, off.selectMin);

        var on = new PcbRecipeParams(new RecipeConfig
        {
            MinBlobArea = 22, MinBlobAreaOpen = 18, MinBlobAreaMousebite = 11, TypeAwareAreaGate = true
        });
        Assert.True(on.typeAwareArea);
        // 预筛仍用较小者，保证候选不被提前杀掉；分类复核在分型之后
        Assert.Equal(11.0, on.selectMin);
        Assert.Equal(18.0, on.minOpen);
        Assert.Equal(11.0, on.minMb);
    }

    // ---------- 工具 ----------

    private static string FindRepoRecipesDir()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "recipes");
            if (File.Exists(Path.Combine(candidate, "pcb_open_mousebite_v0.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "找不到仓库内 recipes/ 目录（从 " + AppDomain.CurrentDomain.BaseDirectory + " 向上查找 10 层）。");
    }
}
