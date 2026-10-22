using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 配准阶段：把模板对齐到实测图坐标系，输出后续差分所用的像素缓冲。
/// </summary>
/// <remarks>
/// 由 <c>HalconPcbDefectDetector.cs</c> 的 <c>DetectCore</c> 前段搬移而来，
/// 逻辑一行未改，只是把「输入→输出」显式化（原先是 25 行内联 + 5 个可变局部变量）。
/// </remarks>
public sealed partial class HalconPcbDefectDetector
{
    /// <summary>配准阶段产出。</summary>
    private sealed class RegisterResult
    {
        /// <summary>实测图紧凑像素（去掉行填充）。</summary>
        public byte[] TestPx = null!;

        /// <summary>对齐后的模板像素。</summary>
        public byte[] TempPx = null!;

        /// <summary>配准前的原始模板像素（边缘解释闸的回退基准）。</summary>
        public byte[] RawTempPx = null!;

        /// <summary>逐块分数位移 X（仅 SubpixelGradient 模式非 null）。</summary>
        public double[]? FracX;

        /// <summary>逐块分数位移 Y（仅 SubpixelGradient 模式非 null）。</summary>
        public double[]? FracY;
    }

    /// <summary>
    /// 配准：分块整数平移（可附带分数位移输出），把模板移到实测图坐标系。
    /// <see cref="RecipeConfig.RegisterShiftMax"/> ≤ 0 时不做配准（DeepPCB 官方数据本身已预对齐）。
    /// </summary>
    private RegisterResult RegisterStage(FrameBuffer frame)
    {
        var tCompact = StageTimer.Mark();
        var testPx = Compact(frame.Pixels, frame.Width, frame.Height, frame.Stride);
        var tempPx = Compact(frame.TemplatePixels!, frame.Width, frame.Height, frame.Width);
        var rawTempPx = tempPx;
        StageTimer.Add(">1a_compact", tCompact);
        double[]? fracX = null;
        double[]? fracY = null;
        if (_recipe.RegisterShiftMax > 0)
        {
            var tAlign = StageTimer.Mark();
            // DeepPCB 预对齐仍残留 1–2px 分区偏移（见 QUALITY_DEBT 过杀归因），铜 ROI 随之在对齐后模板上取
            if (_recipe.SubpixelGradient)
            {
                // E1：整数位移与 Align 逐位一致，只额外拿每块分数位移（不重采样，供梯度补偿差分）
                tempPx = TileShiftRegistration.AlignWithFraction(testPx, tempPx, frame.Width, frame.Height,
                    _recipe.RegisterShiftMax, _recipe.RegisterTiles, _recipe.RegisterMinGain, out fracX, out fracY);
            }
            else if (_recipe.RegisterSubPixel)
            {
                tempPx = TileShiftRegistration.AlignSmooth(testPx, tempPx, frame.Width, frame.Height,
                    _recipe.RegisterShiftMax, _recipe.RegisterTiles, _recipe.RegisterMinGain, out _);
            }
            else
            {
                tempPx = TileShiftRegistration.Align(testPx, tempPx, frame.Width, frame.Height,
                    _recipe.RegisterShiftMax, _recipe.RegisterTiles, _recipe.RegisterMinGain, out _);
            }
            StageTimer.Add(">1b_align", tAlign);
        }
        return new RegisterResult
        {
            TestPx = testPx,
            TempPx = tempPx,
            RawTempPx = rawTempPx,
            FracX = fracX,
            FracY = fracY
        };
    }
}
