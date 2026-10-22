using System;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using Xunit;

/// <summary>
/// Stub 是「假检测器」：它只为跑通链路存在，产出的框与真实图像内容无关。
/// 因此它必须**只能被显式打开**——配置里误写 Stub 时宁可启动失败，
/// 也不能让工位"外观正常地"输出编造的判定。
/// </summary>
public class DetectorFactoryTests
{
    [Fact]
    public void Stub_Rejected_WhenNotExplicitlyAllowed()
    {
        var saved = Environment.GetEnvironmentVariable(DetectorFactory.AllowStubEnv);
        Environment.SetEnvironmentVariable(DetectorFactory.AllowStubEnv, null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DetectorFactory.Create("Stub", out _));
            // 报错必须告诉运维怎么修，而不只是说"不行"
            Assert.Contains(DetectorFactory.AllowStubEnv, ex.Message, StringComparison.Ordinal);
            Assert.Contains("Halcon", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DetectorFactory.AllowStubEnv, saved);
        }
    }

    [Fact]
    public void Stub_Allowed_WhenCallerOptsIn_AndWarns()
    {
        var d = DetectorFactory.Create("Stub", out var warn, allowStub: true);
        Assert.Equal("Stub", d.EngineName);
        Assert.Contains("WARNING", warn, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归：旧实现在无模板时按文件名（含 "_test"）在画面中央造一个假缺陷框。
    /// 真实相机路径一旦碰到它，现场会看到一片"永远报 NG"的板，且框的位置与图像无关。
    /// </summary>
    [Fact]
    public void Stub_WithoutTemplate_ProducesNoFakeBox()
    {
        const int w = 64, h = 64;
        var frame = new FrameBuffer(new byte[w * h], w, h, sourcePath: "00041000_test.jpg");
        Assert.False(frame.HasTemplate);

        var outcome = new StubDefectDetector().Detect(frame);
        Assert.NotNull(outcome.Defects);
        Assert.Empty(outcome.Defects);
        Assert.True(outcome.IsOk);
    }
}
