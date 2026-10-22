using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 图像与区域的底层基元：梯度、压缩、配准、鲁棒极值、几何量测、铜网标号。
/// </summary>
/// <remarks>
/// 由 <c>HalconPcbDefectDetector.cs</c> 按职责拆分出的 <c>partial</c> 片段：
/// 本文件只放<b>自包含的 static 基元</b>（不捕获任何检测上下文）。
/// 拆分是纯文件搬移——调用点一行未改，因此与拆分前<b>逐位等价</b>。
/// </remarks>
public sealed partial class HalconPcbDefectDetector
{
    /// <summary>3×3 Sobel 梯度（边界钳位）：供梯度补偿差分取模板边沿响应。单帧一次。</summary>
    private static void Sobel3x3(byte[] src, short[] gradX, short[] gradY, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            var ym = y > 0 ? y - 1 : y;
            var yp = y < h - 1 ? y + 1 : y;
            for (var x = 0; x < w; x++)
            {
                var xm = x > 0 ? x - 1 : x;
                var xp = x < w - 1 ? x + 1 : x;
                var gx = (src[ym * w + xp] + 2 * src[y * w + xp] + src[yp * w + xp])
                       - (src[ym * w + xm] + 2 * src[y * w + xm] + src[yp * w + xm]);
                var gy = (src[yp * w + xm] + 2 * src[yp * w + x] + src[yp * w + xp])
                       - (src[ym * w + xm] + 2 * src[ym * w + x] + src[ym * w + xp]);
                var i = y * w + x;
                gradX[i] = (short)gx;
                gradY[i] = (short)gy;
            }
        }
    }

    private static bool OverlapsAny(List<DefectItem> cs, DefectItem d)
    {
        foreach (var c in cs)
            if (BboxGap(c, d) <= 0) return true;
        return false;
    }

    /// <summary>模板铜网编号（0=非铜）。一次标号，桥核用邻域编号种类判定是否跨网。</summary>
    private static int[] CopperLabels(HObject copper, int w, int h)
    {
        var labels = new int[w * h];
        HOperatorSet.Connection(copper, out var conn);
        HOperatorSet.CountObj(conn, out var n);
        var limit = n.I;
        for (var i = 1; i <= limit; i++)
        {
            HOperatorSet.SelectObj(conn, out var cc, i);
            HOperatorSet.GetRegionPoints(cc, out var rows, out var cols);
            var len = rows.Length;
            for (var k = 0; k < len; k++)
            {
                var y = rows[k].I;
                var x = cols[k].I;
                if ((uint)y < (uint)h && (uint)x < (uint)w)
                    labels[y * w + x] = i;
            }
            cc.Dispose();
        }
        conn.Dispose();
        return labels;
    }

    /// <summary>blob 邻域碰到的模板铜网数，2 表示「至少两块」后即停。</summary>
    private static int UnionNets(int[] labels, int w, int h, HObject blob, int rad)
    {
        HOperatorSet.GetRegionRuns(blob, out var rows, out var beg, out var end);
        Span<int> seen = stackalloc int[2];
        var n = 0;
        for (var r = 0; r < rows.Length; r++)
        {
            var y = rows[r].I;
            if ((uint)y >= (uint)h) continue;
            for (var x = beg[r].I; x <= end[r].I; x++)
            {
                if ((uint)x >= (uint)w) continue;
                var y0 = Math.Max(0, y - rad);
                var y1 = Math.Min(h - 1, y + rad);
                var x0 = Math.Max(0, x - rad);
                var x1 = Math.Min(w - 1, x + rad);
                for (var yy = y0; yy <= y1 && n < 2; yy++)
                {
                    var row = yy * w;
                    for (var xx = x0; xx <= x1; xx++)
                    {
                        var lab = labels[row + xx];
                        if (lab == 0) continue;
                        if (n > 0 && lab == seen[0]) continue;
                        if (n > 1 && lab == seen[1]) continue;
                        seen[n++] = lab;
                        if (n >= 2) return 2;
                    }
                }
            }
        }
        return n;
    }

    /// <summary>像素棋盘邻域内不同铜网编号的个数（不含背景）。</summary>
    private static int DistinctCopper(int[] labels, int w, int h, int y, int x, int rad)
    {
        Span<int> seen = stackalloc int[8];
        var n = 0;
        var y0 = Math.Max(0, y - rad);
        var y1 = Math.Min(h - 1, y + rad);
        var x0 = Math.Max(0, x - rad);
        var x1 = Math.Min(w - 1, x + rad);
        for (var yy = y0; yy <= y1; yy++)
        {
            var row = yy * w;
            for (var xx = x0; xx <= x1; xx++)
            {
                var lab = labels[row + xx];
                if (lab == 0) continue;
                var known = false;
                for (var i = 0; i < n; i++)
                    if (seen[i] == lab) { known = true; break; }
                if (known) continue;
                if (n >= seen.Length) return n + 1;
                seen[n++] = lab;
            }
        }
        return n;
    }

    /// <summary>两个候选外接框的间隙（棋盘距离，重叠时为 0）。</summary>
    private static double BboxGap(DefectItem a, DefectItem b)
    {
        var dx = Math.Max(0, Math.Max(b.X - (a.X + a.W - 1), a.X - (b.X + b.W - 1)));
        var dy = Math.Max(0, Math.Max(b.Y - (a.Y + a.H - 1), a.Y - (b.Y + b.H - 1)));
        return Math.Max(dx, dy);
    }

    private static (double area, double aspect) BlobAreaAspect(HObject blob)
    {
        HOperatorSet.SmallestRectangle1(blob, out var r1, out var c1, out var r2, out var c2);
        HOperatorSet.AreaCenter(blob, out var a, out var r, out var c);
        var w = Math.Max(1, c2.I - c1.I + 1);
        var h = Math.Max(1, r2.I - r1.I + 1);
        return (a.D, w >= h ? (double)w / h : (double)h / w);
    }

    /// <summary>
    /// maxDepth = blob 上铜距场最大值（缺口咬进多深）；
    /// localWidth = 邻域∩铜 距场截尾最大 × 2。不用全域最大，避免旁边焊盘把线宽抬飞。
    /// </summary>
    private static (double maxDepth, double localWidth) ScaleFeatures(
        HObject blob, HObject copperRegion, HObject distToSubstrate, double widthRadius)
    {
        var maxDepth = RobustMaxGray(blob, distToSubstrate, 0);
        HObject dilated, clipped;
        HOperatorSet.DilationCircle(blob, out dilated, widthRadius);
        HOperatorSet.Intersection(dilated, copperRegion, out clipped);
        dilated.Dispose();
        var halfW = RobustMaxGray(clipped, distToSubstrate, 25);
        clipped.Dispose();
        if (halfW < 1.0) halfW = Math.Max(1.0, maxDepth);
        return (maxDepth, Math.Max(1.0, 2.0 * halfW));
    }

    /// <summary>Percent&gt;0 时丢掉最亮的 Percent%，避免焊盘中心污染。</summary>
    private static double RobustMaxGray(HObject region, HObject image, double percent)
    {
        HTuple area, r, c;
        HOperatorSet.AreaCenter(region, out area, out r, out c);
        if (area.Length == 0 || area.D <= 0) return 0;
        HTuple minV, maxV, range;
        HOperatorSet.MinMaxGray(region, image, percent, out minV, out maxV, out range);
        return maxV.D;
    }

    /// <summary>blob 的位置键，用于跨阶段标记同一候选。</summary>
    private static string BlobKey(HObject one)
    {
        HTuple r1, c1, r2, c2;
        HOperatorSet.SmallestRectangle1(one, out r1, out c1, out r2, out c2);
        return $"{c1.I},{r1.I},{c2.I - c1.I + 1},{r2.I - r1.I + 1}";
    }

    private static byte[] Compact(byte[] pixels, int width, int height, int stride)
    {
        if (stride == width) return pixels;
        var compact = new byte[width * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, compact, y * width, width);
        return compact;
    }

    private static int[] ToIntArray(HTuple t)
    {
        if (t == null || t.Length == 0) return Array.Empty<int>();
        var a = new int[t.Length];
        for (var i = 0; i < t.Length; i++)
            a[i] = t[i].I;
        return a;
    }

    private static bool IsLicenseError(HOperatorException ex)
    {
        var msg = ex.Message ?? "";
        return msg.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0
               || msg.IndexOf("Licence", StringComparison.OrdinalIgnoreCase) >= 0;
    }

}
