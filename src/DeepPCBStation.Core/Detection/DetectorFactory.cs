namespace DeepPCBStation.Core.Detection;

public static class DetectorFactory
{
    /// <summary>
    /// 显式开关：CI/冒烟要用 Stub（假检测器）时，设该环境变量为 1，或在代码里传 allowStub:true。
    /// </summary>
    public const string AllowStubEnv = "DPCB_ALLOW_STUB_ENGINE";

    /// <summary>
    /// engine: "Halcon"（交付，匹配→仿射→差分→Blob）| "Stub"（仅编译/CI，产出与真实缺陷无关的框）。
    /// </summary>
    /// <param name="allowStub">
    /// 是否允许 Stub。生产路径一律 false：配置里误写 Stub 时**抛异常快速失败**，
    /// 而不是静默跑假引擎——假引擎的界面表现是"外观正常、判定全是编出来的"，
    /// 比启动失败危险得多，且事后从 UI 上完全看不出来。
    /// </param>
    public static IDefectDetector Create(string? engine, out string warning, bool allowStub = false)
    {
        warning = "";
        var e = (engine ?? "Halcon").Trim();
        if (string.Equals(e, "Stub", StringComparison.OrdinalIgnoreCase))
        {
            var envOn = string.Equals(
                Environment.GetEnvironmentVariable(AllowStubEnv), "1", StringComparison.Ordinal);
            if (!allowStub && !envOn)
                throw new InvalidOperationException(
                    "检测引擎被指定为 Stub（假检测器，只用于编译/冒烟，产出与实际图像无关的缺陷框）。" +
                    "生产路径不接受它：请把 Detection.Engine 改回 Halcon；" +
                    "确为 CI/冒烟时设置环境变量 " + AllowStubEnv + "=1。");
            warning = "WARNING: 使用 StubDefectDetector（非交付引擎，仅保编译/冒烟）。";
            return new StubDefectDetector();
        }
        return new HalconPcbDefectDetector();
    }
}
