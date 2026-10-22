using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 局部最优平移解释（LOP）：配准残差若只是「整块被搬了 1–N px」，
/// 存在同一个平移 d 使 blob 内绝大多数像素与平移后的模板一致（|test − temp(d)| &lt; thresh）。
/// 真缺陷在任何单一 d 下都会留下不可解释的核心，因此该判据比
/// <see cref="EdgeExplain"/>（逐像素独立取邻域极值，宽松）更严格，
/// 专用于复核「深度豁免」跳过边缘闸的深鬼影，同时不误杀真缺陷。
/// </summary>
public static class LocalShiftMatch
{
    /// <summary>
    /// 在 ±radius 内找使 blob 像素被同一位移解释比例最大的 d。
    /// 解释定义为 |test(x,y) − temp(x+dx,y+dy)| &lt; thresh；越界像素视为未解释。
    /// </summary>
    public static (double frac, int dx, int dy) Best(
        DefectItem d, byte[] test, byte[] temp, int width, int height, int thresh, int radius)
    {
        if (!d.HasRuns || radius <= 0) return (0, 0, 0);
        
        var runRows = d.RunRows!;
        var runBegin = d.RunColBegin!;
        var runEnd = d.RunColEnd!;
        var total = 0;
        for (var r = 0; r < runRows.Length; r++)
        {
            var y = runRows[r];
            if ((uint)y >= (uint)height) continue;
            var x1 = Math.Max(0, runBegin[r]);
            var x2 = Math.Min(width - 1, runEnd[r]);
            if (x2 >= x1) total += x2 - x1 + 1;
        }
        if (total <= 0) return (0, 0, 0);

        double best = -1;
        var bdx = 0;
        var bdy = 0;
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            var ok = 0;
            for (var r = 0; r < runRows.Length; r++)
            {
                var y = runRows[r];
                if ((uint)y >= (uint)height) continue;
                var ty = y + dy;
                if ((uint)ty >= (uint)height) continue;
                var row = y * width;
                var trow = ty * width;
                var xb = runBegin[r];
                var xe = runEnd[r];
                for (var x = xb; x <= xe; x++)
                {
                    if ((uint)x >= (uint)width) continue;
                    var tx = x + dx;
                    if ((uint)tx >= (uint)width) continue;
                    var diff = test[row + x] - temp[trow + tx];
                    if (diff < thresh && diff > -thresh) ok++;
                }
            }
            var frac = (double)ok / total;
            // 同分时偏向零平移：解释力来自「本来就无差异」时不要虚构位移
            if (frac > best || (frac == best && dx == 0 && dy == 0))
            {
                best = frac;
                bdx = dx;
                bdy = dy;
            }
        }
        return (best, bdx, bdy);
    }
}
