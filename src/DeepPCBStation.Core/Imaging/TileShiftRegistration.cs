using System.Threading.Tasks;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 分块整数平移配准：每块在 ±maxShift 内取二值 XOR 最小的 (dx,dy)，输出 aligned(x,y)=temp(x+dx,y+dy)。
/// 降幅不足 minGain 的块保持零平移，避免小缺陷把配准带偏。
/// </summary>
public static class TileShiftRegistration
{
    /// <summary>
    /// 配准成本的度量口径。<see cref="Align"/>/<see cref="AlignWithFraction"/> 用二值 XOR 计数，
    /// <see cref="AlignSmooth"/> 用灰度平均绝对差（SAD）——两者量纲不同、
    /// 数值不可比，历史上曾被塞进同一对 <c>Xor*</c> 字段（SAD 强转 int），读的人会当成「差异像素数」。
    /// </summary>
    public enum CostKind
    {
        /// <summary>二值 XOR 不吻合像素计数（整数，≥0）。</summary>
        BinaryXor,

        /// <summary>灰度平均绝对差（每像素灰度，≥0，可为小数）。</summary>
        GraySad,
    }

    public sealed class TileShift
    {
        public int Row { get; init; }
        public int Col { get; init; }

        /// <summary>整数位移。<see cref="AlignSmooth"/> 下只有整数部分，分数部分在位移场里不回传。</summary>
        public int Dx { get; init; }

        public int Dy { get; init; }

        /// <summary>本块成本所用的度量口径；决定读 <c>Xor*</c> 还是 <c>Sad*</c>。</summary>
        public CostKind Cost { get; init; }

        /// <summary>零平移处的二值 XOR 成本。仅 <see cref="CostKind.BinaryXor"/> 时非空。</summary>
        public int? XorAt0 { get; init; }

        /// <summary>最优平移处的二值 XOR 成本（已含增益闸回退）。仅 <see cref="CostKind.BinaryXor"/> 时非空。</summary>
        public int? XorBest { get; init; }

        /// <summary>零平移处的灰度 SAD（每像素平均绝对差）。仅 <see cref="CostKind.GraySad"/> 时非空。</summary>
        public double? SadAt0 { get; init; }

        /// <summary>最优平移处的灰度 SAD（已含增益闸回退）。仅 <see cref="CostKind.GraySad"/> 时非空。</summary>
        public double? SadBest { get; init; }

        /// <summary>相对降幅（0~1）；成本为 0（完全吻合）时记 0。</summary>
        public double Gain => Cost == CostKind.GraySad
            ? SadAt0 is > 0 ? (SadAt0.Value - (SadBest ?? SadAt0.Value)) / SadAt0.Value : 0
            : XorAt0 is > 0 ? (double)(XorAt0.Value - (XorBest ?? XorAt0.Value)) / XorAt0.Value : 0;
    }

    public static byte[] Align(
        byte[] test, byte[] temp, int width, int height,
        int maxShift, int tiles, double minGain, out List<TileShift> shifts)
    {
        // AlignCore 的 shifts 声明为可空（wantFrac=true 时不给），本重载恒走 wantFrac:false，
        // 契约上必非空——用 s! 表达这个契约，避免把可空类型泄漏给所有调用方。
        var aligned = AlignCore(test, temp, width, height, maxShift, tiles, minGain,
            wantFrac: false, out _, out _, out var s);
        shifts = s!;
        return aligned;
    }

    /// <summary>
    /// 分块整数配准 + 分数位移输出（<b>不重采样图像</b>）：整数位移与 <see cref="Align"/> 逐位一致
    /// （同一搜索内核），额外输出每块分数部分（fx,fy∈[-0.5,0.5]，灰度 SAD 抛物线细化；
    /// 未过增益闸或 SAD 邻域过平的块记 0，逐块常数填充、与整数位移的硬块边界一致）。
    /// 供梯度补偿差分用：d = test − (tempA + Gx·fx + Gy·fy)，图像保持原始锐度
    /// （区别于 RegisterSubPixel 的重采样路线）。
    /// </summary>
    public static byte[] AlignWithFraction(
        byte[] test, byte[] temp, int width, int height,
        int maxShift, int tiles, double minGain,
        out double[] fracX, out double[] fracY)
    {
        var aligned = AlignCore(test, temp, width, height, maxShift, tiles, minGain,
            wantFrac: true, out var fx, out var fy, out _);
        fracX = fx!;
        fracY = fy!;
        return aligned;
    }

    private static byte[] AlignCore(
        byte[] test, byte[] temp, int width, int height,
        int maxShift, int tiles, double minGain, bool wantFrac,
        out double[]? fracX, out double[]? fracY, out List<TileShift>? shifts)
    {
        shifts = wantFrac ? null : new List<TileShift>();
        var fxLocal = wantFrac ? new double[width * height] : null;
        var fyLocal = wantFrac ? new double[width * height] : null;
        fracX = fxLocal;
        fracY = fyLocal;
        tiles = Math.Max(1, tiles);
        var aligned = new byte[width * height];
        var count = tiles * tiles;
        var shiftArr = new TileShift[count];

        // 预二值化：把「两次比较 + 分支」压成一次 XOR 累加。与 (test>=128)!=(temp>=128) 逐位等价。
        var testBin = new byte[width * height];
        var tempBin = new byte[width * height];
        var npx = width * height;
        for (var i = 0; i < npx; i++)
        {
            testBin[i] = (byte)(test[i] >= 128 ? 1 : 0);
            tempBin[i] = (byte)(temp[i] >= 128 ? 1 : 0);
        }

        void Work(int ti)
        {
            var tr = ti / tiles;
            var tc = ti % tiles;
            var x0 = tc * width / tiles;
            var x1 = (tc + 1) * width / tiles;
            var y0 = tr * height / tiles;
            var y1 = (tr + 1) * height / tiles;

            // 统计区内缩 maxShift，平移后取样不越界
            int sx0 = Math.Max(x0, maxShift), sx1 = Math.Min(x1, width - maxShift);
            int sy0 = Math.Max(y0, maxShift), sy1 = Math.Min(y1, height - maxShift);

            int Xor(int dx, int dy, int limit)
            {
                var n = 0;
                for (var y = sy0; y < sy1; y++)
                {
                    var rt = y * width;
                    var rm = (y + dy) * width + dx;
                    for (var x = sx0; x < sx1; x++)
                        n += testBin[rt + x] ^ tempBin[rm + x];
                    // 行级提前退出：内层无分支，判据仍是「超过当前最优即不可能更新」
                    if (n > limit) return n;
                }
                return n;
            }

            var at0 = Xor(0, 0, int.MaxValue);
            int best = at0, bdx = 0, bdy = 0;
            for (var dy = -maxShift; dy <= maxShift; dy++)
            for (var dx = -maxShift; dx <= maxShift; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var v = Xor(dx, dy, best);
                if (v < best || (v == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bdx) + Math.Abs(bdy)))
                {
                    best = v; bdx = dx; bdy = dy;
                }
            }
            var shifted = true;
            if (at0 == 0 || best > at0 * (1 - minGain))
            {
                best = at0; bdx = 0; bdy = 0;
                shifted = false;
            }
            shiftArr[ti] = new TileShift
            {
                Row = tr, Col = tc, Dx = bdx, Dy = bdy,
                Cost = CostKind.BinaryXor, XorAt0 = at0, XorBest = best,
            };

            for (var y = y0; y < y1; y++)
            {
                var ty = Math.Min(height - 1, Math.Max(0, y + bdy));
                for (var x = x0; x < x1; x++)
                {
                    var tx = Math.Min(width - 1, Math.Max(0, x + bdx));
                    aligned[y * width + x] = temp[ty * width + tx];
                }
            }

            if (!wantFrac || !shifted) return;
            // 分数部分：灰度 SAD 在整数最优的 ±1 邻域上做抛物线细化（SAD 对位移平滑可抛物线；
            // XOR 是二值量不可）。未过增益闸的块不估分数——同一理由：别被小缺陷把配准带偏。
            double Sad(int dx, int dy)
            {
                double sum = 0; var cnt = 0;
                for (var y = sy0; y < sy1; y++)
                {
                    var rt = y * width;
                    var rm = (y + dy) * width + dx;
                    for (var x = sx0; x < sx1; x++) { sum += Math.Abs(test[rt + x] - temp[rm + x]); cnt++; }
                }
                return cnt > 0 ? sum / cnt : double.MaxValue;
            }
            double Sub(double sm, double s0, double sp)
            {
                var den = sm - 2 * s0 + sp;
                if (Math.Abs(den) < 1e-9) return 0;
                var d = 0.5 * (sm - sp) / den;
                return d > 0.5 ? 0.5 : d < -0.5 ? -0.5 : d;
            }
            var sBest = Sad(bdx, bdy);
            var subX = 0.0;
            var subY = 0.0;
            if (Math.Abs(bdx) < maxShift)
            {
                var sm = Sad(bdx - 1, bdy); var sp = Sad(bdx + 1, bdy);
                // 邻域过平（极差 <1 灰度）说明该方向无可信位移信息，记 0
                if (Math.Max(sm, sp) - Math.Min(sm, sp) > 1.0) subX = Sub(sm, sBest, sp);
            }
            if (Math.Abs(bdy) < maxShift)
            {
                var sm = Sad(bdx, bdy - 1); var sp = Sad(bdx, bdy + 1);
                if (Math.Max(sm, sp) - Math.Min(sm, sp) > 1.0) subY = Sub(sm, sBest, sp);
            }
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                fxLocal![y * width + x] = subX;
                fyLocal![y * width + x] = subY;
            }
        }

        if (count >= 4) Parallel.For(0, count, Work);
        else for (var ti = 0; ti < count; ti++) Work(ti);

        if (shifts != null) shifts.AddRange(shiftArr);
        return aligned;
    }

    /// <summary>
    /// 平滑场配准：灰度 SAD 整数搜索 → 抛物线亚像素细化 → 3×3 中值 → 双线性插值位移场 + 整数取整采样。
    /// 相比 <see cref="Align"/> 的分块整数硬切，位移场随位置连续变化（可跟随旋转/局部畸变），
    /// 且中值滤波剔除被真缺陷带偏的块，减少瓦片接缝处的 1–2px 台阶鬼影。
    /// </summary>
    public static byte[] AlignSmooth(
        byte[] test, byte[] temp, int width, int height,
        int maxShift, int tiles, double minGain, out List<TileShift> shifts)
    {
        shifts = new List<TileShift>();
        tiles = Math.Max(1, tiles);
        maxShift = Math.Max(0, maxShift);
        if (maxShift == 0) return (byte[])temp.Clone();

        var fdx = new double[tiles, tiles];
        var fdy = new double[tiles, tiles];
        var cx = new double[tiles];
        var cy = new double[tiles];
        for (var tc = 0; tc < tiles; tc++)
        {
            cx[tc] = (tc * width / tiles + (tc + 1) * width / tiles - 1) / 2.0;
            cy[tc] = (tc * height / tiles + (tc + 1) * height / tiles - 1) / 2.0;
        }

        for (var tr = 0; tr < tiles; tr++)
        for (var tc = 0; tc < tiles; tc++)
        {
            var x0 = tc * width / tiles;
            var x1 = (tc + 1) * width / tiles;
            var y0 = tr * height / tiles;
            var y1 = (tr + 1) * height / tiles;
            int sx0 = Math.Max(x0, maxShift), sx1 = Math.Min(x1, width - maxShift);
            int sy0 = Math.Max(y0, maxShift), sy1 = Math.Min(y1, height - maxShift);
            if (sx1 <= sx0 || sy1 <= sy0) continue;

            double Sad(int dx, int dy, int step)
            {
                long sum = 0;
                long cnt = 0;
                for (var y = sy0; y < sy1; y += step)
                {
                    var rt = y * width;
                    var rm = (y + dy) * width + dx;
                    for (var x = sx0; x < sx1; x += step)
                    {
                        sum += Math.Abs(test[rt + x] - temp[rm + x]);
                        cnt++;
                    }
                }
                return cnt > 0 ? (double)sum / cnt : double.MaxValue;
            }

            // 粗搜 step2 → 候选邻域 step1 精搜（评分口径统一为 step1）
            var at0 = Sad(0, 0, 2);
            var coarseBest = at0;
            var cdx = 0;
            var cdy = 0;
            for (var dy = -maxShift; dy <= maxShift; dy += 2)
            for (var dx = -maxShift; dx <= maxShift; dx += 2)
            {
                if (dx == 0 && dy == 0) continue;
                var v = Sad(dx, dy, 2);
                if (v < coarseBest) { coarseBest = v; cdx = dx; cdy = dy; }
            }
            var s00 = Sad(0, 0, 1);
            var sBest = Sad(cdx, cdy, 1);
            var bdx = cdx;
            var bdy = cdy;
            for (var dy = cdy - 1; dy <= cdy + 1; dy++)
            for (var dx = cdx - 1; dx <= cdx + 1; dx++)
            {
                if (dx == cdx && dy == cdy) continue;
                if (Math.Abs(dx) > maxShift || Math.Abs(dy) > maxShift) continue;
                var v = Sad(dx, dy, 1);
                if (v < sBest) { sBest = v; bdx = dx; bdy = dy; }
            }
            if (sBest > s00) { sBest = s00; bdx = 0; bdy = 0; }

            // 抛物线亚像素细化（顶点距 [-0.5,0.5]）
            double Sub(double sm, double s0, double sp)
            {
                var den = sm - 2 * s0 + sp;
                if (Math.Abs(den) < 1e-9) return 0;
                var d = 0.5 * (sm - sp) / den;
                return d > 0.5 ? 0.5 : d < -0.5 ? -0.5 : d;
            }
            var subX = 0.0;
            var subY = 0.0;
            if (Math.Abs(bdx) < maxShift)
                subX = Sub(Sad(bdx - 1, bdy, 1), sBest, Sad(bdx + 1, bdy, 1));
            if (Math.Abs(bdy) < maxShift)
                subY = Sub(Sad(bdx, bdy - 1, 1), sBest, Sad(bdx, bdy + 1, 1));

            var gain = s00 > 0 ? (s00 - sBest) / s00 : 0;
            // 平坦区（纯铜/纯基材）的 SAD 几乎不变，微小的相对增益也会被当成位移；
            // 必须同时满足绝对降幅，否则该块保持零平移。
            if (gain < minGain || s00 < 2.0 || s00 - sBest < 2.0)
            {
                bdx = 0;
                bdy = 0;
                subX = 0;
                subY = 0;
                sBest = s00;
            }
            fdx[tr, tc] = bdx + subX;
            fdy[tr, tc] = bdy + subY;
            // 成本口径是灰度 SAD（不是 XOR 像素计数）：写 Sad*，Xor* 留空，
            // 避免下游把「平均灰度差 3.7」当成「3 个像素不吻合」。
            shifts.Add(new TileShift
            {
                Row = tr, Col = tc, Dx = bdx, Dy = bdy,
                Cost = CostKind.GraySad, SadAt0 = s00, SadBest = sBest,
            });
        }

        // 3×3 中值：剔除被真缺陷带偏的块，避免位移场长刺
        if (tiles >= 3)
        {
            var mdx = (double[,])fdx.Clone();
            var mdy = (double[,])fdy.Clone();
            var buf = new double[9];
            for (var tr = 0; tr < tiles; tr++)
            for (var tc = 0; tc < tiles; tc++)
            {
                var n = 0;
                for (var dr = -1; dr <= 1; dr++)
                for (var dc = -1; dc <= 1; dc++)
                {
                    var r = tr + dr;
                    var c = tc + dc;
                    if ((uint)r >= (uint)tiles || (uint)c >= (uint)tiles) continue;
                    buf[n++] = fdx[r, c];
                }
                Array.Sort(buf, 0, n);
                mdx[tr, tc] = buf[n / 2];
                n = 0;
                for (var dr = -1; dr <= 1; dr++)
                for (var dc = -1; dc <= 1; dc++)
                {
                    var r = tr + dr;
                    var c = tc + dc;
                    if ((uint)r >= (uint)tiles || (uint)c >= (uint)tiles) continue;
                    buf[n++] = fdy[r, c];
                }
                Array.Sort(buf, 0, n);
                mdy[tr, tc] = buf[n / 2];
            }
            fdx = mdx;
            fdy = mdy;
        }

        // 预计算 x/y 方向的插值索引与权重
        var ix = new int[width];
        var fx = new double[width];
        for (var x = 0; x < width; x++)
        {
            var k = 0;
            while (k < tiles - 2 && cx[k + 1] <= x) k++;
            ix[x] = k;
            var span = tiles > 1 ? cx[k + 1] - cx[k] : 1;
            fx[x] = tiles > 1 && span > 1e-9 ? Math.Min(1.0, Math.Max(0.0, (x - cx[k]) / span)) : 0;
        }
        var iy = new int[height];
        var fy = new double[height];
        for (var y = 0; y < height; y++)
        {
            var k = 0;
            while (k < tiles - 2 && cy[k + 1] <= y) k++;
            iy[y] = k;
            var span = tiles > 1 ? cy[k + 1] - cy[k] : 1;
            fy[y] = tiles > 1 && span > 1e-9 ? Math.Min(1.0, Math.Max(0.0, (y - cy[k]) / span)) : 0;
        }

        var aligned = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var k = iy[y];
            var k2 = tiles > 1 ? Math.Min(tiles - 1, k + 1) : 0;
            var ty = fy[y];
            for (var x = 0; x < width; x++)
            {
                var j = ix[x];
                var j2 = tiles > 1 ? Math.Min(tiles - 1, j + 1) : 0;
                var txw = fx[x];
                var dxf =
                    fdx[k, j] * (1 - txw) * (1 - ty) + fdx[k, j2] * txw * (1 - ty) +
                    fdx[k2, j] * (1 - txw) * ty + fdx[k2, j2] * txw * ty;
                var dyf =
                    fdy[k, j] * (1 - txw) * (1 - ty) + fdy[k, j2] * txw * (1 - ty) +
                    fdy[k2, j] * (1 - txw) * ty + fdy[k2, j2] * txw * ty;

                var sxf = x + dxf;
                var syf = y + dyf;
                // 整数取整采样：双线性重采样会把所有铜边缘糊成斜坡，在阈值 30 下反而制造整幅鬼影；
                // 平滑的是「位移场」，图像本身保持锐利。
                var sx0 = (int)Math.Round(sxf);
                var sy0 = (int)Math.Round(syf);
                if (sx0 < 0) sx0 = 0; else if (sx0 > width - 1) sx0 = width - 1;
                if (sy0 < 0) sy0 = 0; else if (sy0 > height - 1) sy0 = height - 1;
                aligned[y * width + x] = temp[sy0 * width + sx0];
            }
        }
        return aligned;
    }
}
