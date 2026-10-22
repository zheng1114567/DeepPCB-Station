using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 反极性证据比：候选 blob 膨胀 radius 内、对侧极性差分像素占比。
/// 平移鬼影必然在铜边另一侧留下反极性带（铜被搬走的同时也在另一侧被搬来），
/// 真缺口（open/mousebite）只有单极性证据，故该比值能把两者分开。
/// 与 <see cref="LocalShiftMatch"/> 联用：LOP 高 + 反极性证据高 = 错位鬼影；
/// LOP 高但反极性证据低 = 真缺口（碰巧被平移"解释"）。
/// </summary>
public static class PolarityPair
{
    /// <summary>
    /// <paramref name="oppositePositive"/> = true 时统计 diff ≥ thresh（缺铜）像素，
    /// false 时统计 diff ≤ -thresh（多铜）像素。返回比值（0–N）；无法计算返回 -1。
    /// </summary>
    public static double Ratio(
        DefectItem d, byte[] test, byte[] temp, int width, int height,
        int thresh, int radius, bool oppositePositive)
    {
        if (!d.HasRuns || radius <= 0) return -1;
        var rows = d.RunRows!;
        var begins = d.RunColBegin!;
        var ends = d.RunColEnd!;

        var total = 0;
        for (var r = 0; r < rows.Length; r++)
        {
            var y = rows[r];
            if ((uint)y >= (uint)height) continue;
            var xb = Math.Max(0, begins[r]);
            var xe = Math.Min(width - 1, ends[r]);
            if (xe >= xb) total += xe - xb + 1;
        }
        if (total <= 0) return -1;

        // 按行合并膨胀后的区间（blob 很小，行区间法足够）
        var lo = new int[height];
        var hi = new int[height];
        for (var y = 0; y < height; y++) { lo[y] = int.MaxValue; hi[y] = -1; }
        for (var r = 0; r < rows.Length; r++)
        {
            var y = rows[r];
            if ((uint)y >= (uint)height) continue;
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(height - 1, y + radius);
            int xb = Math.Max(0, begins[r] - radius), xe = Math.Min(width - 1, ends[r] + radius);
            if (xe < xb) continue;
            for (var yy = y0; yy <= y1; yy++)
            {
                if (xb < lo[yy]) lo[yy] = xb;
                if (xe > hi[yy]) hi[yy] = xe;
            }
        }

        var hit = 0;
        for (var y = 0; y < height; y++)
        {
            if (hi[y] < lo[y]) continue;
            var row = y * width;
            for (var x = lo[y]; x <= hi[y]; x++)
            {
                var diff = test[row + x] - temp[row + x];
                if (oppositePositive ? diff >= thresh : diff <= -thresh) hit++;
            }
        }
        return (double)hit / total;
    }
}
