using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Recipe;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 配方存储：ContentHash 防篡改、保存/加载往返、乐观锁、换型确认门闩。
/// 全部使用临时目录，不碰仓库内 recipes/。
/// </summary>
public class RecipeStoreTests : IDisposable
{
    private readonly string _dir;

    public RecipeStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dpcb_recipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    private static RecipeConfig Sample() => new()
    {
        Id = "test_recipe",
        Name = "测试配方",
        Version = "1.0.0",
        DiffThreshold = 30,
        MinBlobArea = 40,
    };

    // ---------- ContentHash ----------

    [Fact]
    public void Hash_IsStable_ForEquivalentConfigs()
    {
        Assert.Equal(
            RecipeStore.ComputeHashStatic(Sample()),
            RecipeStore.ComputeHashStatic(Sample()));
    }

    [Fact]
    public void Hash_Changes_WhenBehaviourFieldChanges()
    {
        var a = Sample();
        var b = Sample();
        b.DiffThreshold = 31; // 行为字段必须进哈希

        Assert.NotEqual(RecipeStore.ComputeHashStatic(a), RecipeStore.ComputeHashStatic(b));
    }

    [Fact]
    public void Hash_Ignores_DisplayOnlyFields()
    {
        var a = Sample();
        var b = Sample();
        b.Name = "另一个名字"; // 名称不进哈希，只影响展示

        Assert.Equal(RecipeStore.ComputeHashStatic(a), RecipeStore.ComputeHashStatic(b));
    }

    // ---------- 保存 / 加载往返 ----------

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new RecipeStore(new[] { Sample() });
        store.Save(_dir, Sample());

        var reloaded = RecipeStore.LoadFromDirectory(_dir);
        Assert.Equal("test_recipe", reloaded.Current.Id);
        Assert.Equal(30, reloaded.Current.DiffThreshold);
        Assert.Equal(40, reloaded.Current.MinBlobArea);
        Assert.False(string.IsNullOrEmpty(reloaded.Current.ContentHash), "加载后必须带上 ContentHash");
    }

    [Fact]
    public void Save_UpdatesContentHash_InPlace()
    {
        var cfg = Sample();
        var before = RecipeStore.ComputeHashStatic(cfg);
        var store = new RecipeStore(new[] { cfg });
        cfg.DiffThreshold = 44;
        store.Save(_dir, cfg);

        Assert.Equal(RecipeStore.ComputeHashStatic(cfg), cfg.ContentHash);
        Assert.NotEqual(before, cfg.ContentHash);
    }

    /// <summary>
    /// 全字段往返契约：反射构造「每个数值字段都非默认」的配方，Save→Load 后逐字段比对。
    /// 守住 RecipeConfig/RecipeFileDto/Load/Save 四件套同步——v1.2.x 曾漏
    /// borderMinAspect/edgeForcePruneArea/pairGhostFrac 三个 SetIf（UI 保存后静默丢失），
    /// 哈希契约测试查不出 Save 缺口，只有这条用例能炸出来。
    /// </summary>
    [Fact]
    public void SaveThenLoad_PreservesEveryField()
    {
        var cfg = new RecipeConfig { Id = "full", Name = "全字段", Version = "9.9.9" };
        foreach (var p in typeof(RecipeConfig).GetProperties().Where(p => p.CanWrite))
        {
            switch (p.Name)
            {
                case "ContentHash": break;
                case "Id": case "Name": case "Version": break; // 上面显式设置
                default:
                    if (p.PropertyType == typeof(int)) p.SetValue(cfg, 7);
                    else if (p.PropertyType == typeof(double)) p.SetValue(cfg, 0.125);
                    else if (p.PropertyType == typeof(bool)) p.SetValue(cfg, true);
                    break;
            }
        }

        var store = new RecipeStore(new[] { cfg });
        store.Save(_dir, cfg);
        var back = RecipeStore.LoadFromDirectory(_dir).Get("full");

        foreach (var p in typeof(RecipeConfig).GetProperties().Where(p => p.CanRead))
        {
            if (p.Name == "ContentHash") continue;
            Assert.Equal(p.GetValue(cfg), p.GetValue(back));
        }
    }

    [Fact]
    public void Save_RaisesStoreChanged()
    {
        var store = new RecipeStore(new[] { Sample() });
        var raised = false;
        store.StoreChanged += () => raised = true;
        store.Save(_dir, Sample());
        Assert.True(raised);
    }

    // ---------- 乐观锁 ----------

    [Fact]
    public void Save_WithStaleExpectedHash_Rejected()
    {
        var store = new RecipeStore(new[] { Sample() });
        var cfg = Sample();
        cfg.ContentHash = RecipeStore.ComputeHashStatic(cfg);

        Assert.Throws<RecipeConcurrencyException>(() =>
            store.Save(_dir, cfg, expectedHash: "deadbeef"));
    }

    [Fact]
    public void Save_WithMatchingExpectedHash_Accepted()
    {
        var store = new RecipeStore(new[] { Sample() });
        var cfg = Sample();
        cfg.ContentHash = RecipeStore.ComputeHashStatic(cfg);

        store.Save(_dir, cfg, expectedHash: cfg.ContentHash);
        Assert.Equal(cfg.ContentHash, RecipeStore.LoadFromDirectory(_dir).Current.ContentHash);
    }

    // ---------- 换型确认门闩 ----------

    [Fact]
    public void NewStore_RequiresChangeoverConfirmation()
    {
        var store = new RecipeStore(new[] { Sample() });
        Assert.False(store.IsChangeoverConfirmed);
        store.ConfirmChangeover();
        Assert.True(store.IsChangeoverConfirmed);
        Assert.NotNull(store.ConfirmedAt);
    }

    [Fact]
    public void Select_ThenConfirm_UpdatesCurrent()
    {
        var store = new RecipeStore(new[]
        {
            Sample(),
            new RecipeConfig { Id = "second", Name = "第二配方", Version = "1.0.0" },
        });
        store.Select("second");
        Assert.Equal("second", store.Current.Id);
        Assert.False(store.IsChangeoverConfirmed, "切换配方后必须重新确认换型");
        store.ConfirmChangeover();
        Assert.True(store.IsChangeoverConfirmed);
    }

    [Fact]
    public void Select_UnknownId_Throws()
    {
        var store = new RecipeStore(new[] { Sample() });
        Assert.Throws<KeyNotFoundException>(() => store.Select("no_such_recipe"));
    }
}
