using System.Linq;
using DeepPCBStation.Core.Imaging;
using Xunit;

namespace DeepPCBStation.Core.Tests;

/// <summary>
/// 配准成本字段的语义契约：
/// <see cref="TileShiftRegistration.Align"/> 走二值 XOR（像素计数，整数），
/// <see cref="TileShiftRegistration.AlignSmooth"/> 走灰度 SAD（每像素平均绝对差，小数）。
/// 历史上两者共用一对 <c>Xor*</c> 字段（SAD 被强转成 int），读的人会把「平均灰度差 3.7」
/// 当成「3 个像素不吻合」。这里锁住两路各写各的字段、不串台。
/// </summary>
public class TileShiftRegistrationTests
{
    private const int W = 64;
    private const int H = 64;

    /// <summary>
    /// 竖条纹，模板相对实测图整体右移 2px（temp[x] = test[x-2]）。
    /// 于是 aligned[x] = temp[x+dx] = test[x+dx-2]，配准必须给出 dx=+2。
    /// </summary>
    private static (byte[] Test, byte[] Temp) ShiftedPair()
    {
        var test = new byte[W * H];
        var temp = new byte[W * H];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            test[y * W + x] = (x / 8) % 2 == 0 ? (byte)200 : (byte)40;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
            temp[y * W + x] = x >= 2 ? test[y * W + x - 2] : test[y * W + x];
        return (test, temp);
    }

    [Fact]
    public void Align_二值XOR路线_只写Xor字段()
    {
        var (test, temp) = ShiftedPair();
        TileShiftRegistration.Align(test, temp, W, H, maxShift: 3, tiles: 4, minGain: 0.02, out var shifts);

        Assert.NotEmpty(shifts);
        Assert.All(shifts, s =>
        {
            Assert.Equal(TileShiftRegistration.CostKind.BinaryXor, s.Cost);
            Assert.NotNull(s.XorAt0);
            Assert.NotNull(s.XorBest);
            Assert.Null(s.SadAt0);
            Assert.Null(s.SadBest);
        });
        // 整图相对实测右移 2px：最优位移必须是 +2（把模板拉回来）
        Assert.Contains(shifts, s => s.Dx == 2);
    }

    [Fact]
    public void AlignSmooth_灰度SAD路线_只写Sad字段()
    {
        var (test, temp) = ShiftedPair();
        TileShiftRegistration.AlignSmooth(test, temp, W, H, maxShift: 3, tiles: 4, minGain: 0.02, out var shifts);

        Assert.NotEmpty(shifts);
        Assert.All(shifts, s =>
        {
            Assert.Equal(TileShiftRegistration.CostKind.GraySad, s.Cost);
            Assert.Null(s.XorAt0);
            Assert.Null(s.XorBest);
            Assert.NotNull(s.SadAt0);
            Assert.NotNull(s.SadBest);
        });
        // SAD 是每像素平均灰度差，量纲不是「像素计数」：条纹图错位时应在数十灰度量级
        Assert.Contains(shifts, s => s.SadAt0!.Value > 1.0);
    }

    [Fact]
    public void 完全一致的图_两条路线都不平移且增益为0()
    {
        var (test, _) = ShiftedPair();
        TileShiftRegistration.Align(test, test, W, H, maxShift: 3, tiles: 4, minGain: 0.02, out var a);
        TileShiftRegistration.AlignSmooth(test, test, W, H, maxShift: 3, tiles: 4, minGain: 0.02, out var b);

        Assert.All(a, s => { Assert.Equal(0, s.Dx); Assert.Equal(0, s.Dy); Assert.Equal(0, s.XorAt0); Assert.Equal(0, s.Gain); });
        Assert.All(b, s => { Assert.Equal(0, s.Dx); Assert.Equal(0, s.Dy); Assert.Equal(0d, s.SadBest); Assert.Equal(0, s.Gain); });
    }
}
