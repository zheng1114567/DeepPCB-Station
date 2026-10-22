using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 配准后仍沿铜边残留的 1–2px 错位：blob 像素若能被模板邻域解释，就不是真缺陷。
/// 缺铜：test − max(temp 邻域)；多铜：min(temp 邻域) − test。
/// </summary>
public static class EdgeExplain
{
    public static double Fraction(
        DefectItem d, byte[] test, byte[] temp, int width, int height, int thresh, int rad, bool extraCopper)
    {
        if (!d.HasRuns || rad <= 0) return 0;
        int total = 0, ok = 0;
        for (var r = 0; r < d.RunRows!.Length; r++)
        {
            var y = d.RunRows[r];
            if ((uint)y >= (uint)height) continue;
            var row = y * width;
            for (var x = d.RunColBegin![r]; x <= d.RunColEnd![r]; x++)
            {
                if ((uint)x >= (uint)width) continue;
                total++;
                var tv = test[row + x];
                int extreme = extraCopper ? 255 : 0;
                var y0 = Math.Max(0, y - rad);
                var y1 = Math.Min(height - 1, y + rad);
                var x0 = Math.Max(0, x - rad);
                var x1 = Math.Min(width - 1, x + rad);
                for (var yy = y0; yy <= y1; yy++)
                {
                    var tr = yy * width;
                    for (var xx = x0; xx <= x1; xx++)
                    {
                        var v = temp[tr + xx];
                        if (extraCopper) { if (v < extreme) extreme = v; }
                        else if (v > extreme) extreme = v;
                    }
                }
                var explained = extraCopper ? extreme - tv < thresh : tv - extreme < thresh;
                if (explained) ok++;
            }
        }
        return total > 0 ? (double)ok / total : 0;
    }

    /// <summary>
    /// 剪枝：返回 blob 中「未被模板邻域解释」的残余像素（按 run 输出）。
    /// 主模板与回退模板任一解释即算解释（配准可能搬错方向，取并集）。
    /// 返回 null 表示残余为空（整 blob 可解释）。
    /// </summary>
    public static (int[] rows, int[] begins, int[] ends, int total, int explained)? Residual(
        DefectItem d, byte[] test, byte[] tempMain, byte[]? tempAlt, int width, int height, int thresh, int rad, bool extraCopper)
    {
        if (!d.HasRuns || d.RunRows == null || rad <= 0) return null;
        var rr = new List<int>();
        var rb = new List<int>();
        var re = new List<int>();
        int total = 0, explained = 0;
        for (var r = 0; r < d.RunRows.Length; r++)
        {
            var y = d.RunRows[r];
            if ((uint)y >= (uint)height) continue;
            var row = y * width;
            var segStart = -1;
            for (var x = d.RunColBegin![r]; x <= d.RunColEnd![r]; x++)
            {
                if ((uint)x >= (uint)width) continue;
                total++;
                var exp = ExplainedAt(test, tempMain, width, height, y, x, thresh, rad, extraCopper);
                if (!exp && tempAlt != null)
                    exp = ExplainedAt(test, tempAlt, width, height, y, x, thresh, rad, extraCopper);
                if (exp) explained++;
                if (!exp)
                {
                    if (segStart < 0) segStart = x;
                }
                else if (segStart >= 0)
                {
                    rr.Add(y); rb.Add(segStart); re.Add(x - 1);
                    segStart = -1;
                }
            }
            if (segStart >= 0)
            {
                rr.Add(y); rb.Add(segStart); re.Add(d.RunColEnd[r]);
            }
        }
        if (rr.Count == 0) return null;
        return (rr.ToArray(), rb.ToArray(), re.ToArray(), total, explained);
    }

    private static bool ExplainedAt(byte[] test, byte[] temp, int width, int height, int y, int x, int thresh, int rad, bool extraCopper)
    {
        var tv = test[y * width + x];
        int extreme = extraCopper ? 255 : 0;
        var y0 = Math.Max(0, y - rad);
        var y1 = Math.Min(height - 1, y + rad);
        var x0 = Math.Max(0, x - rad);
        var x1 = Math.Min(width - 1, x + rad);
        for (var yy = y0; yy <= y1; yy++)
        {
            var tr = yy * width;
            for (var xx = x0; xx <= x1; xx++)
            {
                var v = temp[tr + xx];
                if (extraCopper) { if (v < extreme) extreme = v; }
                else if (v > extreme) extreme = v;
            }
        }
        return extraCopper ? extreme - tv < thresh : tv - extreme < thresh;
    }
}
