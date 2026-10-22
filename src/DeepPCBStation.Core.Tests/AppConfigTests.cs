using System;
using System.IO;
using System.Text;
using DeepPCBStation.Core.Config;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// M2：配置层集成测试（此前该层测试数为 0）。
///
/// 为什么值得测：配置静默替换是「以为在跑 A 实际在跑 B」类事故的直接来源，
/// 而且它不会报错——现场只会表现为「参数改了没效果」。
/// 本文件钉住 H4 的两条结果：配置往返不丢值、缺配方不再被静默塞回退版本。
/// </summary>
public sealed class AppConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dpdb-cfg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "appsettings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SaveThenLoad_PreservesValues()
    {
        var cfg = AppConfig.CreateDefault();
        cfg.Detection.DiffThreshold = 55;
        cfg.Detection.MinBlobArea = 17;
        cfg.Camera.Width = 800;
        cfg.Camera.PlaybackRootDir = "data/official_test";
        cfg.Recipes.Add(new RecipeConfig
        {
            Id = "test_recipe",
            Name = "测试配方",
            Version = "9.9.9",
            DiffThreshold = 41
        });

        cfg.Save(_path);
        var back = AppConfig.Load(_path);

        Assert.Equal(55, back.Detection.DiffThreshold);
        Assert.Equal(17, back.Detection.MinBlobArea);
        Assert.Equal(800, back.Camera.Width);
        Assert.Equal("data/official_test", back.Camera.PlaybackRootDir);
        Assert.Single(back.Recipes);
        Assert.Equal("test_recipe", back.Recipes[0].Id);
        Assert.Equal(41, back.Recipes[0].DiffThreshold);
    }

    /// <summary>
    /// H4 回归：appsettings 里没有 recipes 时，**不得**静默塞回退副本。
    /// 原实现（AppConfig.Load 的 Count==0 → CreateDefault().Recipes）会让
    /// 「recipes/ 目录加载失败」表现为「用 v0.2.0 老参数悄悄跑起来了」。
    /// 现在为空就是空，由 RecipeStore 抛「至少需要一条配方」快速失败。
    /// </summary>
    [Fact]
    public void MissingRecipes_IsNotSilentlyBackfilled()
    {
        File.WriteAllText(_path, """
        {
          "detection": { "engine": "Halcon", "diffThreshold": 44 },
          "camera": { "mode": "PairedFilePlayback" }
        }
        """, new UTF8Encoding(false));

        var cfg = AppConfig.Load(_path);

        Assert.Empty(cfg.Recipes);        // 空就是空，不偷偷补默认配方
        Assert.Equal(44, cfg.Detection.DiffThreshold);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var cfg = AppConfig.Load(Path.Combine(_dir, "nope.json"));
        Assert.Equal("Halcon", cfg.Detection.Engine);
        Assert.Empty(cfg.Recipes);
    }

    /// <summary>
    /// L7 回归：UI 保存的目标必须是用户级目录，不能是程序目录（= 构建输出目录）。
    /// 程序目录那份由构建从源码树 PreserveNewest 复制过去，写它会让现场改过的参数
    /// 在下一次构建时凭空消失；且 Program Files 这类只读目录压根写不进去。
    /// </summary>
    [Fact]
    public void UserConfigPath_IsOutsideBuildOutputDir()
    {
        var userPath = Path.GetFullPath(AppConfig.UserConfigPath());
        var appDir = Path.GetFullPath(AppContext.BaseDirectory);

        Assert.EndsWith("appsettings.json", userPath, StringComparison.Ordinal);
        Assert.False(userPath.StartsWith(appDir, StringComparison.OrdinalIgnoreCase),
            $"用户配置不能落在构建输出目录内（appDir={appDir}）");
    }

    /// <summary>向前兼容：读到将来新增的键不应崩溃（现场升级路径是老程序读新配置）。</summary>
    [Fact]
    public void UnknownKeys_Tolerated()
    {
        File.WriteAllText(_path, """
        {
          "detection": { "diffThreshold": 33 },
          "futureFeature": { "somethingNew": true }
        }
        """, new UTF8Encoding(false));

        var cfg = AppConfig.Load(_path);
        Assert.Equal(33, cfg.Detection.DiffThreshold);
    }
}
