using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

#if USE_HALCON
using HalconDotNet;
#endif

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 母团救援与残渣生长：开运算切碎的小面积候选回收链路，以及候选构造/拒收录入。
/// </summary>
/// <remarks>
/// 由 <c>HalconPcbDefectDetector.cs</c> 按职责拆分出的 <c>partial</c> 片段：
/// 本文件只放<b>自包含的 static 基元</b>（不捕获任何检测上下文）。
/// 拆分是纯文件搬移——调用点一行未改，因此与拆分前<b>逐位等价</b>。
/// </remarks>
public sealed partial class HalconPcbDefectDetector
{
    /// <summary>
    /// 残渣生长闸判定：从残渣包围盒出发，沿 |test − alignedTemp| ≥ 1 的像素洪泛（与
    /// GrowBoxesToRegion 同口径：margin 窗、8 邻接、按 growMaxScale 设硬上限），
    /// 返回生长后包围盒；<b>最大边 &gt; growMaxDim 则返回 null</b>（洪泛失控成大片，丢弃）。
    ///
    /// 为什么不直接用 GrowBoxesToRegion：它是 DefectItem 的后处理（要跑完全部闸门之后），
    /// 而残渣在 SelectShape 阶段就要决定放行与否，必须在此处就地判定。
    /// 闸门是必需的：无闸时召回随 margin 上升但 FP 同步上升，离线无法排除过拟合。
    /// </summary>
    private static (int x1, int y1, int x2, int y2)? GrowResidueBox(
        HObject residue, int growMaxDim, byte[] alignedTemp, byte[] testPx, int width, int height,
        double margin, double growMaxScale, bool diag)
    {
        HTuple r1, c1, r2, c2;
        HOperatorSet.SmallestRectangle1(residue, out r1, out c1, out r2, out c2);
        var bx1 = c1.I; var by1 = r1.I; var bx2 = c2.I; var by2 = r2.I;

        var wx1 = Math.Max(0, bx1 - margin); var wy1 = Math.Max(0, by1 - margin);
        var wx2 = Math.Min(width - 1, bx2 + margin); var wy2 = Math.Min(height - 1, by2 + margin);
        // 硬上限：与 growMaxScale 语义一致，同时绝不越过 growMaxDim 闸（越界即判失控）。
        var hardMax = Math.Max(1, Math.Max(bx2 - bx1 + 1, by2 - by1 + 1) * Math.Max(1, growMaxScale));
        var limit = Math.Min(hardMax, growMaxDim);

        var seen = new bool[width * height];
        var queue = new Queue<int>();
        // 以残渣整框为种子（对齐 GrowBoxesToRegion 的边框种子口径）
        for (var y = by1; y <= by2; y++)
        for (var x = bx1; x <= bx2; x++)
        {
            var i0 = y * width + x;
            if (seen[i0]) continue;
            seen[i0] = true; queue.Enqueue(i0);
        }
        var gx1 = bx1; var gy1 = by1; var gx2 = bx2; var gy2 = by2;
        while (queue.Count > 0)
        {
            var idx = queue.Dequeue();
            var y = idx / width; var x = idx - y * width;
            var ad = testPx[idx] - alignedTemp[idx];
            if (ad < 0) ad = -ad;
            if (ad < 1) continue;   // 与 GrowBoxesToRegion 的 regionGrowDiff=1 同口径：只沿弱差及以上连通
            if (x < gx1) gx1 = x;
            if (x > gx2) gx2 = x;
            if (y < gy1) gy1 = y;
            if (y > gy2) gy2 = y;
            if (gx2 - gx1 + 1 > limit || gy2 - gy1 + 1 > limit) return null;   // 失控
            void Push(int ny, int nx)
            {
                if (ny < wy1 || ny > wy2 || nx < wx1 || nx > wx2) return;
                var ni = ny * width + nx;
                if (seen[ni]) return;
                seen[ni] = true; queue.Enqueue(ni);
            }
            Push(y - 1, x); Push(y + 1, x); Push(y, x - 1); Push(y, x + 1);
            if (diag) { Push(y - 1, x - 1); Push(y - 1, x + 1); Push(y + 1, x - 1); Push(y + 1, x + 1); }
        }
        // 终检：生长后仍须在闸内（无增长时= 残渣原框，自然在闸内）
        if (Math.Max(gx2 - gx1 + 1, gy2 - gy1 + 1) > growMaxDim) return null;
        return (gx1, gy1, gx2, gy2);
    }

    /// <summary>
    /// 母团救援：把「开运算后残渣面积 &lt; selectMin，但其所属母团（开运算前的连通域）面积 ≥ motherMin」
    /// 的候选补进 selected。开运算会切碎小面积浅咬缺陷（实测残渣面积 6~21，而框内差分像素 17~67），
    /// 只看残渣面积会误杀真缺陷；直接下调全局阈值又等于放行全场边缘残渣。
    /// 每板最多补 rescueCap 个，防止大母团捞出海量残渣（实测无上限时跑不完）。
    /// 放行的残渣键写入 rescuedKeys，供下游 typeMin 二次闸门豁免。
    ///
    /// ★ 历史实测（2026-10-21，train1000）：**单纯放行残渣对本项目漏检无增益**——
    /// 救援确实触发（100 对中 53 板、每板 1~12 个残渣），但漏检数恒定不变（300 对恒为 27）。
    /// 根因：area&lt;min 的 36 个漏检，其 GT 框 25~34px 而开运算残渣仅 6~21px，
    /// 放行后面积仍远小于 GT，MatchOneToOne 匹配不上 —— 缺的是**形态**不是**面积**。
    ///
    /// ★ v1.2.2 候选（residueGrowMaxDim&gt;0 生效）：按上述结论补上「长回足迹」这一步。
    /// 残渣沿 |test−alignedTemp| ≥ 1 的连通域生长到缺陷完整足迹，再用**包围盒最大边**做闸：
    /// 仿真（tools/residue_grow_sim.py，train1000 的 area&lt;min + 开运算抹掉 27 例）显示
    /// 残渣原 IoU 中位仅 0.017（必漏检），生长后 maxDim≤50 的 21 例全部 IoU≥0.33（精确率 100%），
    /// 被闸门丢弃的 6 例 maxDim 68~120（差分洪泛失控成大片）。闸门是必需的：
    /// 无闸时召回随 margin 上升但 FP 同步上升，离线无法排除过拟合，故必须上 C# 实跑验收。
    /// </summary>
    private static void RescueByMother(ref HObject selected, HObject opened, HObject motherConnected,
        double selectMin, double maxArea, double motherMin, HashSet<string> rescuedKeys, int rescueCap,
        int growMaxDim, byte[] alignedTemp, byte[] testPx, int width, int height,
        double growMargin, double growMaxScale, bool growDiag)
    {
        HTuple nMother;
        HOperatorSet.CountObj(motherConnected, out nMother);
        if (nMother.I == 0 || rescueCap <= 0) return;

        // 母团按面积从大到小处理（大母团最可能被切碎）；但**不因面积小就跳过**——
        // 漏检残渣的母团面积中位仅 23（train1000 实测，母团 <40 者占全图 92.2%），
        // 而 motherMin 若设成 40/60 会把它们全部排除；rescueCap 又对着每图 30~1400 个
        // 母团按面积降序截断，真缺陷母团排在最后 → 两道闸门正好对着真缺陷关门。
        // 故 motherMin 只作极小下限（防单像素噪声），真正的选择性交给残渣生长闸。
        var mothers = new List<(HObject obj, double area)>();
        for (int mi = 1; mi <= nMother.I; mi++)
        {
            HObject m;
            HOperatorSet.SelectObj(motherConnected, out m, mi);
            HTuple ma;
            HOperatorSet.AreaCenter(m, out ma, out _, out _);
            mothers.Add((m, ma.D));
    }
        mothers.Sort((a, b) => b.area.CompareTo(a.area));

        var debugRescue = Environment.GetEnvironmentVariable("DPCB_RESCUE_DEBUG") == "1";
        int dbgSeen = 0, dbgGrowNull = 0, dbgEmpty = 0, dbgTooBig = 0, dbgAdded = 0;
        var extras = new List<(HObject obj, string key)>();
        foreach (var (m, marea) in mothers)
        {
            // cap 只统计**真正要放行**的残渣：原先写成 "extras.Count >= rescueCap" 会在
            // 扫过大母团时提前 break，而这些母团贡献 0 个残渣（Intersection 为空），
            // 等价于把真缺陷的小母团全跳过去。改为遍历完再由内层循环自己限流。
            if (marea < motherMin || marea > maxArea) { m.Dispose(); continue; }

            HObject keep;
            HOperatorSet.Intersection(m, opened, out keep);
            HTuple ka;
            HOperatorSet.AreaCenter(keep, out ka, out _, out _);
            if (ka.D <= 0) { keep.Dispose(); m.Dispose(); continue; }

            HObject keepConn;
            HOperatorSet.Connection(keep, out keepConn);
            keep.Dispose();
            HTuple nk;
            HOperatorSet.CountObj(keepConn, out nk);
            for (int ki = 1; ki <= nk.I && extras.Count < rescueCap; ki++)
            {
                HObject k1;
                HOperatorSet.SelectObj(keepConn, out k1, ki);
                HTuple k1a;
                HOperatorSet.AreaCenter(k1, out k1a, out _, out _);
                if (k1a.D < selectMin)
                {
                    dbgSeen++;
                    if (growMaxDim > 0)
                    {
                        // 沿 |test-alignedTemp|>=1 连通域把残渣长回缺陷足迹，再按包围盒最大边闸门。
                        var grownBox = GrowResidueBox(k1, growMaxDim, alignedTemp, testPx, width, height,
                            growMargin, growMaxScale, growDiag);
                        if (grownBox == null) { dbgGrowNull++; k1.Dispose(); continue; }   // 洪泛失控，丢弃
                        // ★ v1.2.2 修正：原先此处用的是「残渣原框 ±growMargin ∩ 母团」，
                        // 把 GrowResidueBox 辛苦算出的生长框直接丢弃了 —— 于是生长闸 maxDim
                        // 完全不约束**实际发射的框**（母团可很大，实测 FP 框最大边到 71px），
                        // 闸门形同虚设。改为真正按生长框发射：取生长框 ∩ 母团，
                        // 既保留真实差分像素，又让 maxDim 闸真正生效。
                        // 注意 GenRectangle1 的参数顺序是 (row1, col1, row2, col2)，
                        // 而 GrowResidueBox 返回的是 (x1=列, y1=行) —— 必须交换，
                        // 否则矩形被转置、与母团交集恒空（表现为修复后候选全部消失）。
                        HOperatorSet.GenRectangle1(out var growRect,
                            grownBox.Value.y1, grownBox.Value.x1, grownBox.Value.y2, grownBox.Value.x2);
                        if (debugRescue)
                        {
                            HOperatorSet.SmallestRectangle1(m, out var mr1, out var mc1, out var mr2, out var mc2);
                            HOperatorSet.SmallestRectangle1(k1, out var kr1, out var kc1, out var kr2, out var kc2);
                            Console.Error.WriteLine($"[rescue-dbg] mother=({mc1.I},{mr1.I})-({mc2.I},{mr2.I}) res=({kc1.I},{kr1.I})-({kc2.I},{kr2.I}) grow=({grownBox.Value.x1},{grownBox.Value.y1})-({grownBox.Value.x2},{grownBox.Value.y2})");
    }
                        HOperatorSet.Intersection(growRect, m, out var candRect);
                        growRect.Dispose();
                        if (candRect == null) { dbgEmpty++; k1.Dispose(); continue; }
                        HTuple ga;
                        HOperatorSet.AreaCenter(candRect, out ga, out _, out _);
                        if (ga.D > maxArea) { dbgTooBig++; candRect.Dispose(); k1.Dispose(); continue; }
                        var key = BlobKey(candRect);
                        if (debugRescue)
                            Console.Error.WriteLine($"[rescue] mother={marea:F0} res={k1a.D:F0} grown={grownBox.Value.x2 - grownBox.Value.x1 + 1}x{grownBox.Value.y2 - grownBox.Value.y1 + 1} cand={ga.D:F0} key={key}");
                        dbgAdded++;
                        extras.Add((candRect.Clone(), key));
                        candRect.Dispose();
                        k1.Dispose();
                    }
                    else
                    {
                        var key = BlobKey(k1);
                        extras.Add((k1.Clone(), key));
                    }
                }
                else k1.Dispose();
            }
            keepConn.Dispose();
            m.Dispose();
        }
        if (extras.Count == 0) return;

        if (debugRescue)
            Console.Error.WriteLine($"[rescue-summary] seen={dbgSeen} growNull={dbgGrowNull} empty={dbgEmpty} tooBig={dbgTooBig} added={dbgAdded}");

        // 用 concat_obj 追加对象，**不能**用 union2：union2 会把多个候选并成一个对象，
        // 而下游按 SelectObj 逐对象遍历候选，一并就塌成单个巨 blob（实测漏检 144→237）。
        HOperatorSet.CountObj(selected, out var nSel0);
        HObject acc;
        if (nSel0.I == 0)
        {
            // selected 为空时不能 SelectObj(空)，直接拿第一个被救残渣起头。
            var seed = extras[0];
            acc = seed.obj.Clone();
            rescuedKeys.Add(seed.key);
        }
        else
        {
            HOperatorSet.SelectObj(selected, out var firstObj, 1);
            acc = firstObj.Clone();
            firstObj.Dispose();
        }
        for (int i = 2; i <= nSel0.I; i++)
        {
            HObject s;
            HOperatorSet.SelectObj(selected, out s, i);
            HOperatorSet.ConcatObj(acc, s, out var c2);
            acc.Dispose();
            acc = c2;
            s.Dispose();
        }
        foreach (var (obj, key) in extras)
        {
            HOperatorSet.ConcatObj(acc, obj, out var c3);
            acc.Dispose();
            acc = c3;
            rescuedKeys.Add(key);
            obj.Dispose();
    }
        selected.Dispose();
        selected = acc;
    }

    private static void Reject(List<RejectedBlob>? rejected, HObject one, string reason,
        double area, double aspect, double surround = -1, int ringComponents = -1,
        double localWidth = -1, double maxDepth = -1,
        double lopFrac = -1, int lopDx = 0, int lopDy = 0, double oppRatio = -1)
    {
        if (rejected == null) return;
        var (item, _) = ToItem(one, "?", area);
        rejected.Add(new RejectedBlob
        {
            Reason = reason, Blob = item, Area = area, Aspect = aspect,
            Surround = surround, RingComponents = ringComponents,
            LocalWidth = localWidth, MaxDepth = maxDepth,
            LopFrac = lopFrac, LopDx = lopDx, LopDy = lopDy, OppRatio = oppRatio
        });
    }

    private static (DefectItem item, double aspect) ToItem(HObject one, string type, double area)
    {
        HTuple row1, col1, row2, col2, runRows, runColBegin, runColEnd;
        HOperatorSet.SmallestRectangle1(one, out row1, out col1, out row2, out col2);
        HOperatorSet.GetRegionRuns(one, out runRows, out runColBegin, out runColEnd);
        var w = Math.Max(1, col2.I - col1.I + 1);
        var h = Math.Max(1, row2.I - row1.I + 1);
        var item = new DefectItem
        {
            TypeCode = type, X = col1.I, Y = row1.I, W = w, H = h, Score = area,
            RunRows = ToIntArray(runRows), RunColBegin = ToIntArray(runColBegin), RunColEnd = ToIntArray(runColEnd)
        };
        return (item, w >= h ? (double)w / h : (double)h / w);
    }

}
