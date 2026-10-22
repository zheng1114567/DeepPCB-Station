using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Camera;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Core.Storage;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 工位管线的四态判定行为（OK / NG / REVIEW / INVALID / FAULT）。
/// 用脚本相机 + Stub 检测器，不依赖 Halcon，可在任何环境跑。
/// </summary>
public class PipelineVerdictTests : IDisposable
{
    private const int W = 320;
    private const int H = 320;
    private readonly string _root;

    public PipelineVerdictTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dpcb_pipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class ScriptedCamera : ICamera
    {
        private readonly FrameBuffer _frame;
        public ScriptedCamera(FrameBuffer frame) => _frame = frame;
        public string Name => "Scripted";
        public bool IsOpened => true;
        public int FrameWidth => _frame.Width;
        public int FrameHeight => _frame.Height;
        public void Open() { }
        public void Close() { }
        public FrameBuffer Grab() => _frame.Clone();
        public void Dispose() { }
    }

    private static byte[] BimodalTemplate()
    {
        var rng = new Random(7);
        var px = new byte[W * H];
        for (var i = 0; i < px.Length; i++)
            px[i] = rng.NextDouble() < 0.5 ? (byte)0 : (byte)255;
        return px;
    }

    private InspectionPipeline Build(
        FrameBuffer frame,
        CaptureQualityConfig? capture = null,
        ReviewPolicyConfig? review = null,
        bool confirmChangeover = true)
    {
        var recipes = new RecipeStore(new[] { new RecipeConfig { Id = "t", Name = "t", Version = "1.0.0" } });
        if (confirmChangeover) recipes.ConfirmChangeover();
        var storage = new StorageConfig { OutputRoot = _root, SaveImages = false, DbPath = Path.Combine(_root, "t.db") };
        var store = new ResultStore(storage, _root);
        return new InspectionPipeline(new ScriptedCamera(frame), new StubDefectDetector(), store, recipes, storage)
        {
            CaptureQuality = capture,
            ReviewPolicy = review,
            StationId = "TEST",
        };
    }

    private static InspectionTicket Ticket() => new()
    {
        PartId = "P-001",
        Trigger = TriggerSource.AutoLoop,
        Persist = true,
        RequireChangeover = true,
    };

    // ---------- 换型未确认 ----------

    [Fact]
    public async Task UnconfirmedChangeover_ReturnsFault()
    {
        var tpl = BimodalTemplate();
        var pipeline = Build(new FrameBuffer(tpl, W, H, templatePixels: tpl), confirmChangeover: false);

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal("RECIPE_NOT_CONFIRMED", r.FaultCode);
        Assert.Equal(VerdictKind.Fault, r.Kind);
        Assert.False(r.IsOk);
    }

    private sealed class ThrowingCamera : ICamera
    {
        private readonly Exception _ex;
        public ThrowingCamera(Exception ex) => _ex = ex;
        public string Name => "Throwing";
        public bool IsOpened => true;
        public int FrameWidth => W;
        public int FrameHeight => H;
        public void Open() { }
        public void Close() { }
        public FrameBuffer Grab() => throw _ex;
        public void Dispose() { }
    }

    private InspectionPipeline BuildWithCamera(ICamera camera)
    {
        var storage = new StorageConfig { OutputRoot = _root, SaveImages = false, DbPath = Path.Combine(_root, "t.db") };
        var recipes = new RecipeStore(new[] { new RecipeConfig { Id = "t", Name = "t", Version = "1.0.0" } });
        recipes.ConfirmChangeover();
        return new InspectionPipeline(camera, new StubDefectDetector(),
            new ResultStore(storage, _root), recipes, storage)
        {
            StationId = "TEST",
        };
    }

    // ---------- 无图：按**类型**判定，不靠消息子串 ----------

    [Fact]
    public async Task CameraNoImage_MapsToNoImageFault()
    {
        var pipeline = BuildWithCamera(new ThrowingCamera(new CameraNoImageException("NO_IMAGE: 回放已耗尽")));
        var r = await pipeline.RunOnceAsync(Ticket());
        Assert.Equal("NO_IMAGE", r.FaultCode);
        Assert.Equal(VerdictKind.Fault, r.Kind);
    }

    /// <summary>
    /// 兼容：相机抛的其它 InvalidOperationException（未打开、目录未配置等）同样归 NO_IMAGE，
    /// 与改成类型判定之前的行为一致——这个测试锁住「改判定方式不改结果」。
    /// </summary>
    [Fact]
    public async Task CameraInvalidOperation_StillMapsToNoImageFault()
    {
        var pipeline = BuildWithCamera(new ThrowingCamera(new InvalidOperationException("相机未打开，请先调用 Open()。")));
        var r = await pipeline.RunOnceAsync(Ticket());
        Assert.Equal("NO_IMAGE", r.FaultCode);
    }

    /// <summary>非相机族的异常不得被误判成无图——它要继续向外抛。</summary>
    [Fact]
    public async Task CameraIoException_NotSwallowedAsNoImage()
    {
        var pipeline = BuildWithCamera(new ThrowingCamera(new IOException("读图失败")));
        await Assert.ThrowsAsync<IOException>(() => pipeline.RunOnceAsync(Ticket()));
    }

    // ---------- 采图无效 → INVALID，绝不回 NG ----------

    [Fact]
    public async Task BlackFrame_ReturnsInvalid_NotNg()
    {
        var tpl = BimodalTemplate();
        var pipeline = Build(
            new FrameBuffer(new byte[W * H], W, H, templatePixels: tpl),
            capture: new CaptureQualityConfig { Enabled = true });

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal(VerdictKind.Invalid, r.Kind);
        Assert.NotNull(r.InvalidCode);
        Assert.Equal("FLAT", r.InvalidCode);
        Assert.StartsWith("CAPTURE_INVALID:", r.FaultCode);
        Assert.False(r.IsOk);
    }

    [Fact]
    public async Task WrongMaterial_ReturnsInvalid_NotNg()
    {
        var tpl = BimodalTemplate();
        var other = BimodalTemplate();
        for (var i = 0; i < other.Length; i += 2) other[i] = (byte)(255 - other[i]);

        var pipeline = Build(
            new FrameBuffer(other, W, H, templatePixels: tpl),
            capture: new CaptureQualityConfig { Enabled = true });

        var r = await pipeline.RunOnceAsync(Ticket());
        Assert.Equal(VerdictKind.Invalid, r.Kind);
    }

    // ---------- 正常路径 ----------

    [Fact]
    public async Task CleanFrame_ReturnsOk()
    {
        var tpl = BimodalTemplate();
        var pipeline = Build(new FrameBuffer(tpl, W, H, templatePixels: tpl));

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal(VerdictKind.Ok, r.Kind);
        Assert.True(r.IsOk);
        Assert.Null(r.FaultCode);
        Assert.False(string.IsNullOrEmpty(r.RecipeHash), "每条记录必须带配方 hash");
    }

    [Fact]
    public async Task DefectFrame_ReturnsNg()
    {
        var tpl = BimodalTemplate();
        var test = (byte[])tpl.Clone();
        for (var y = 120; y < 180; y++)
        for (var x = 120; x < 180; x++)
            test[y * W + x] = (byte)(test[y * W + x] > 128 ? 0 : 255);

        var pipeline = Build(new FrameBuffer(test, W, H, templatePixels: tpl));

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal(VerdictKind.Ng, r.Kind);
        Assert.False(r.IsOk);
        Assert.True(r.DefectCount >= 1);
    }

    // ---------- 复判策略 ----------

    [Fact]
    public async Task LowConfidenceDefect_GoesReview()
    {
        var tpl = BimodalTemplate();
        var test = (byte[])tpl.Clone();
        for (var y = 120; y < 180; y++)
        for (var x = 120; x < 180; x++)
            test[y * W + x] = (byte)(test[y * W + x] > 128 ? 0 : 255);

        var pipeline = Build(
            new FrameBuffer(test, W, H, templatePixels: tpl),
            review: new ReviewPolicyConfig { Enabled = true, ConfidenceBelow = 1e9 });

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal(VerdictKind.Review, r.Kind);
        Assert.True(r.ReviewTriggerConfidence > 0);
        Assert.Contains("复判", r.VerdictNote ?? "");
    }

    [Fact]
    public async Task ReviewDisabled_DefectStaysNg()
    {
        var tpl = BimodalTemplate();
        var test = (byte[])tpl.Clone();
        for (var y = 120; y < 180; y++)
        for (var x = 120; x < 180; x++)
            test[y * W + x] = (byte)(test[y * W + x] > 128 ? 0 : 255);

        var pipeline = Build(new FrameBuffer(test, W, H, templatePixels: tpl)); // review = null

        var r = await pipeline.RunOnceAsync(Ticket());
        Assert.Equal(VerdictKind.Ng, r.Kind);
    }

    [Fact]
    public async Task TooManyDefects_ForceNg_NotReview()
    {
        var tpl = BimodalTemplate();
        var test = (byte[])tpl.Clone();
        for (var b = 0; b < 12; b++)
        {
            var y0 = 20 + b * 22;
            for (var y = y0; y < y0 + 18; y++)
            for (var x = 100; x < 118; x++)
                test[y * W + x] = (byte)(test[y * W + x] > 128 ? 0 : 255);
        }

        var pipeline = Build(
            new FrameBuffer(test, W, H, templatePixels: tpl),
            review: new ReviewPolicyConfig { Enabled = true, ConfidenceBelow = 1e9, ForceNgAboveCount = 8 });

        var r = await pipeline.RunOnceAsync(Ticket());

        Assert.Equal(VerdictKind.Ng, r.Kind);
        Assert.True(r.DefectCount > 8);
    }

    // ---------- 自检关闭时行为不变 ----------

    [Fact]
    public async Task CaptureQualityDisabled_BlackFrameGoesThrough()
    {
        var tpl = BimodalTemplate();
        var pipeline = Build(
            new FrameBuffer(new byte[W * H], W, H, templatePixels: tpl),
            capture: new CaptureQualityConfig { Enabled = false });

        var r = await pipeline.RunOnceAsync(Ticket());

        // 不开启自检时黑图照样进检测（现状行为），不得出现 INVALID
        Assert.NotEqual(VerdictKind.Invalid, r.Kind);
    }

    // ---------- 并发闸门 ----------

    [Fact]
    public async Task Pipeline_GateSerializesConcurrentRuns()
    {
        var tpl = BimodalTemplate();
        var pipeline = Build(new FrameBuffer(tpl, W, H, templatePixels: tpl));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => pipeline.RunOnceAsync(Ticket())));

        Assert.Equal(6, results.Length);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.InspectionId)));
        Assert.Equal(6, results.Select(r => r.InspectionId).Distinct().Count());
    }
}
