using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 区域生长阶段：把阈值化得到的「高置信核心框」沿弱差分连通域扩到缺陷完整足迹。
/// </summary>
/// <remarks>
/// 由 <c>DetectCore</c> 内的局部函数搬移而来。原先它闭包捕获 11 个 DetectCore 局部变量，
/// 现已显式化为方法参数（配方参数统一走 <see cref="PcbRecipeParams"/>），
/// 逻辑一行未改——调用点改为显式传参，故与搬移前<b>逐位等价</b>。
/// </remarks>
public sealed partial class HalconPcbDefectDetector
{
    private List<DefectItem> GrowStage(List<DefectItem> items, int g,
        byte[] testPx, byte[] tempPx, int width, int height, PcbRecipeParams P)
    {
            if (g <= 0 || items.Count == 0) return items;
            var w = width; var h = height;
        
            DefectItem Grow(DefectItem d)
            {
                var margin = P.growMargin;
                var wx1 = Math.Max(0, d.X - margin); var wy1 = Math.Max(0, d.Y - margin);
                var wx2 = Math.Min(w - 1, d.X + d.W - 1 + margin);
                var wy2 = Math.Min(h - 1, d.Y + d.H - 1 + margin);
                var maxSide = (int)(Math.Max(d.W, d.H) * P.growMaxScale);
                var seen = new bool[w * h];
        
                for (var it = 0; it < P.growIters; it++)
                {
                    var bx1 = d.X; var by1 = d.Y; var bx2 = d.X + d.W - 1; var by2 = d.Y + d.H - 1;
                    var qx = new Queue<int>();
                    var seedEdge = new List<(int x, int y)>();
                    for (var y = by1; y <= by2; y++)
                    {
                        seedEdge.Add((bx1, y)); seedEdge.Add((bx2, y));
                    }
                    for (var x = bx1; x <= bx2; x++)
                    {
                        seedEdge.Add((x, by1)); seedEdge.Add((x, by2));
                    }
                    foreach (var (sx, sy) in seedEdge)
                    {
                        var si = sy * w + sx;
                        if (seen[si]) continue;
                        seen[si] = true;
                        qx.Enqueue(si);
                    }
                    var grew = false;
                    while (qx.Count > 0)
                    {
                        var idx = qx.Dequeue();
                        var y = idx / w; var x = idx - y * w;
                        var ad = testPx[idx] - tempPx[idx];
                        if (ad < 0) ad = -ad;
                        if (ad < g) continue;   // 只沿弱差及以上像素连通
                        if (x < bx1) { bx1 = x; grew = true; }
                        if (x > bx2) { bx2 = x; grew = true; }
                        if (y < by1) { by1 = y; grew = true; }
                        if (y > by2) { by2 = y; grew = true; }
                        if (bx2 - bx1 + 1 > maxSide || by2 - by1 + 1 > maxSide) break;
                        if (x > wx1) { var n = idx - 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                        if (x < wx2) { var n = idx + 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                        if (y > wy1) { var n = idx - w; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                        if (y < wy2) { var n = idx + w; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                        // 8-邻接：斜向的窄桥在 4-邻接下会断，导致缺口长不满（IoU 卡在 0.2-0.33）。
                        if (P.growDiag)
                        {
                            if (x > wx1 && y > wy1) { var n = idx - w - 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                            if (x < wx2 && y > wy1) { var n = idx - w + 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                            if (x > wx1 && y < wy2) { var n = idx + w - 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                            if (x < wx2 && y < wy2) { var n = idx + w + 1; if (!seen[n]) { seen[n] = true; qx.Enqueue(n); } }
                        }
                    }
                    if (!grew) break;
                    var nw = Math.Max(1, bx2 - bx1 + 1); var nh = Math.Max(1, by2 - by1 + 1);
                    if (nw > maxSide) nw = maxSide;
                    if (nh > maxSide) nh = maxSide;
                    if (nw == d.W && nh == d.H && bx1 == d.X && by1 == d.Y) break;
                    d = new DefectItem
                    {
                        TypeCode = d.TypeCode, X = bx1, Y = by1, W = nw, H = nh,
                        Score = d.Score, RunRows = d.RunRows, RunColBegin = d.RunColBegin,
                        RunColEnd = d.RunColEnd, EdgeFrac = d.EdgeFrac, OppRatio = d.OppRatio
                    };
                }
                return d;
            }
        
        // 末端逐框足迹（FWHM，footprintTau>0 时启用，与 P.growBoxScale/P.growBoxElong/P.growBoxPad 互斥）：
        // 在现框 ±P.fpMargin 窗内取 |diff| 的列/行投影剖面（每列取窗内最大 |diff|），
        // 以 τ×峰值 为界、取包含候选自身峰值的连续区间作为上报框边长。
        // 物理依据：模板差分对缺陷掩膜近似线性，|diff| 的空间分布 = 缺陷足迹与边缘响应的叠加，
        // 半高处对应标注的「完整足迹」；阈值化 blob 只是 30 灰度截断的高置信核。
        // 与全局缩放的差别：可胀可缩、按对比度自适应，逐框拟合尺寸分布尾部（GrowBoxScale 已证伪的天花板）。
        DefectItem Footprint(DefectItem d, double tau, int margin, int maxSide)
        {
            var w = width; var h = height;
            var wx1 = Math.Max(0, d.X - margin); var wy1 = Math.Max(0, d.Y - margin);
            var wx2 = Math.Min(w - 1, d.X + d.W - 1 + margin); var wy2 = Math.Min(h - 1, d.Y + d.H - 1 + margin);
            var ww = wx2 - wx1 + 1; var wh = wy2 - wy1 + 1;
            if (ww < 2 || wh < 2) return d;

            // 候选自身峰值（在现框内取，防邻缺陷抢锚）：cut = τ×peak，量纲无关。
            var peak = 0; var pkx = d.X + d.W / 2; var pky = d.Y + d.H / 2;
            for (var y = d.Y; y <= Math.Min(h - 1, d.Y + d.H - 1); y++)
            {
                var rowBase = y * w;
                for (var x = d.X; x <= Math.Min(w - 1, d.X + d.W - 1); x++)
                {
                    var ad = testPx[rowBase + x] - tempPx[rowBase + x];
                    if (ad < 0) ad = -ad;
                    if (ad > peak) { peak = ad; pkx = x; pky = y; }
                }
            }
            if (peak <= 0) return d;
            var cut = tau * peak;

            var px = new double[ww];
            var py = new double[wh];
            for (var y = wy1; y <= wy2; y++)
            {
                var rowBase = y * w;
                for (var x = wx1; x <= wx2; x++)
                {
                    var ad = testPx[rowBase + x] - tempPx[rowBase + x];
                    if (ad < 0) ad = -ad;
                    var ci = x - wx1;
                    if (ad > px[ci]) px[ci] = ad;
                    var ri = y - wy1;
                    if (ad > py[ri]) py[ri] = ad;
                }
            }

            // 含候选峰值的连续区间（列向 / 行向各自 expansion）。
            var ax = pkx - wx1; var ay = pky - wy1;
            var xa = ax; var xb = ax; var ya = ay; var yb = ay;
            while (xa > 0 && px[xa - 1] >= cut) xa--;
            while (xb < ww - 1 && px[xb + 1] >= cut) xb++;
            while (ya > 0 && py[ya - 1] >= cut) ya--;
            while (yb < wh - 1 && py[yb + 1] >= cut) yb++;

            var nw = Math.Min(maxSide, xb - xa + 1);
            var nh = Math.Min(maxSide, yb - ya + 1);
            if (nw < 1) nw = 1;
            if (nh < 1) nh = 1;

            // 中心取足迹区间中点；相对现框中心位移 >4px 说明剖面被邻缺陷/长链带偏，回退现框中心
            //（现框中心已证良好：scale=0 实测偏移中位 2.1px；本 guard 保旧口径「中心落框」不退化）。
            var fcx = wx1 + (xa + xb) / 2.0; var fcy = wy1 + (ya + yb) / 2.0;
            var dcx = d.X + (d.W - 1) / 2.0; var dcy = d.Y + (d.H - 1) / 2.0;
            var cx = Math.Abs(fcx - dcx) <= 4 ? fcx : dcx;
            var cy = Math.Abs(fcy - dcy) <= 4 ? fcy : dcy;
            var sx = (int)Math.Round(cx - (nw - 1) / 2.0);
            var sy = (int)Math.Round(cy - (nh - 1) / 2.0);
            sx = Math.Max(0, Math.Min(w - nw, sx));
            sy = Math.Max(0, Math.Min(h - nh, sy));
            return new DefectItem
            {
                TypeCode = d.TypeCode, X = sx, Y = sy, W = nw, H = nh,
                Score = d.Score, RunRows = d.RunRows, RunColBegin = d.RunColBegin,
                RunColEnd = d.RunColEnd, EdgeFrac = d.EdgeFrac, OppRatio = d.OppRatio
            };
        }

        for (var i = 0; i < items.Count; i++)
        {
            var maxSide = (int)(Math.Max(items[i].W, items[i].H) * P.growMaxScale);
            var d = Grow(items[i]);
            if (P.fpTau > 0.001)
            {
                // 逐框足迹替代全局缩放：可胀可缩，逐框拟合尺寸分布尾部。
                d = Footprint(d, P.fpTau, P.fpMargin, maxSide);
            }
            else if (P.growBoxScale > 0 || P.growBoxPad > 0)
                {
                    var dcx = d.X + (d.W - 1) / 2.0; var dcy = d.Y + (d.H - 1) / 2.0;
                    // 末端几何外扩：先乘法 P.growBoxScale（关时因子=1）补 BFS 未长到的按比例边距，
                    // 长轴（W、H 中较大者＝沿走线方向）再额外乘 P.growBoxElong 修正细长缺陷宽高比；
                    // 最后加法 P.growBoxPad 四边各补固定像素（小框相对增益大、大框相对增益小），拟合尺寸分布尾部。三者均绕中心、中心不变。
                    var m = P.growBoxScale > 0 ? P.growBoxScale : 1.0;
                    var el = P.growBoxElong > 0 ? P.growBoxElong : 1.0;
                    var padPx = P.growBoxPad > 0 ? 2.0 * P.growBoxPad : 0.0;
                    var sw = Math.Max(1, (int)Math.Round(d.W * m * (d.W >= d.H ? el : 1.0) + padPx));
                    var sh = Math.Max(1, (int)Math.Round(d.H * m * (d.H > d.W ? el : 1.0) + padPx));
                    var sx = Math.Max(0, Math.Min(w - 1, (int)Math.Round(dcx - sw / 2.0)));
                    var sy = Math.Max(0, Math.Min(h - 1, (int)Math.Round(dcy - sh / 2.0)));
                    if (sx + sw > w) sw = w - sx;
                    if (sy + sh > h) sh = h - sy;
                    d = new DefectItem
                    {
                        TypeCode = d.TypeCode, X = sx, Y = sy, W = sw, H = sh,
                        Score = d.Score, RunRows = d.RunRows, RunColBegin = d.RunColBegin,
                        RunColEnd = d.RunColEnd, EdgeFrac = d.EdgeFrac, OppRatio = d.OppRatio
                    };
                }
                items[i] = d;
            }
            return items;
        }
}
