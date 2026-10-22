using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

// 过杀归因（只读）：对未匹配 Pred 做特征归类并落三联图。启发式定位，不是成绩。
public sealed class PcbFpAuditor
{
    public const int NearGtGapPx = 8;
    public const int ShallowDepthPx = 2;
    public const int ShiftTolPx = 2;
    public const double ShiftExplainedMin = 0.8;
    private const int CropMargin = 24;
    private const int CropScale = 3;

    public static class Category
    {
        public const string OtherClass = "落在非承诺类缺陷上";
        public const string Fragment = "承诺类GT碎片（GT已被命中）";
        public const string Offset = "承诺类GT偏框（GT未命中）";
        public const string Misregistration = "局部错位（±2px平移可解释）";
        public const string EdgeShallow = "铜边浅层差异（深度≤2px）";
        public const string Thin = "细条（厚度≤1px）";
        public const string Other = "其他（需人工看）";

        public static readonly string[] Order = { OtherClass, Fragment, Offset, Misregistration, EdgeShallow, Thin, Other };
    }

    public sealed class FpItem
    {
        public string Stem { get; set; } = "";
        public int Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public double Area { get; set; }
        public int MaxDiff { get; set; }
        public double MeanDiff { get; set; }
        /// <summary>blob 像素距模板非铜区的最大棋盘距离：缺口咬进铜线多深。</summary>
        public int MaxDepth { get; set; }
        /// <summary>blob 内切半径（棋盘距离）。</summary>
        public int Thickness { get; set; }
        /// <summary>±ShiftTolPx 内最佳平移下，blob 像素被平移模板解释（差分低于阈值）的比例。</summary>
        public double ShiftExplained { get; set; }
        public int BestDx { get; set; }
        public int BestDy { get; set; }
        /// <summary>模板 (2·ShiftTolPx+1)² 灰度膨胀后，blob 像素被解释的比例。</summary>
        public double DilationExplained { get; set; }
        /// <summary>检测器算的反极性证据比（-1=未计算）。错位鬼影高，真缺口低。</summary>
        public double OppRatio { get; set; } = -1;
        public int GapToCommittedGt { get; set; } = -1;
        public bool NearestCommittedGtMatched { get; set; }
        public string? OverlapOtherClass { get; set; }
        public string Category { get; set; } = "";
        public string CropPng { get; set; } = "";
    }

    public sealed class PairShift
    {
        public string Stem { get; set; } = "";
        public int Dx { get; set; }
        public int Dy { get; set; }
        public int XorAt0 { get; set; }
        public int XorBest { get; set; }
    }

    public sealed class AuditReport
    {
        public DateTimeOffset Timestamp { get; set; }
        public string RecipeId { get; set; } = "";
        public string RecipeVersion { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public string Engine { get; set; } = "";
        public int PairCount { get; set; }
        public int TotalPredBlobs { get; set; }
        public int FpCount { get; set; }
        public Dictionary<string, int> ByCategory { get; set; } = new();
        public Dictionary<string, int> OtherClassBreakdown { get; set; } = new();
        public List<FpItem> Items { get; set; } = new();
        public int TpCount { get; set; }
        /// <summary>已命中的 Pred 中，也能被 ±2px 平移解释的（位移容差差分会误删的真缺陷）。</summary>
        public List<string> TpShiftExplained { get; set; } = new();
        public List<string> TpDilationExplained { get; set; } = new();
        public List<PairShift> PairShifts { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? OutputDir { get; set; }
    }

    public AuditReport Run(
        string samplesDir,
        IDefectDetector detector,
        RecipeConfig recipe,
        string reportsDir,
        double iouHit = PcbGoldenEvaluator.DefaultIouHit,
        int maxPairs = 100)
    {
        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);
        var copperMax = recipe.CopperMaxGray > 0 ? recipe.CopperMaxGray : 100;
        var diffThresh = recipe.DiffThreshold > 0 ? recipe.DiffThreshold : 30;

        var report = new AuditReport
        {
            Timestamp = DateTimeOffset.Now,
            RecipeId = recipe.Id,
            RecipeVersion = recipe.Version,
            RecipeHash = recipe.ContentHash,
            Engine = detector.EngineName
        };
        var stamp = report.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine(reportsDir, $"fp_audit_{stamp}");
        var cropDir = Path.Combine(outDir, "crops");
        Directory.CreateDirectory(cropDir);
        report.OutputDir = outDir;

        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(maxPairs).ToList();
        report.PairCount = pairs.Count;

        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            // 与金标同口径：六类模式下对全部六类 GT 匹配，否则只对承诺类。
            // 旧口径（恒为承诺类）会把六类合法 pred 误报成 FP，归因失真。
            var allGts = PcbGoldenEvaluator.LoadAllBoxes(annPath);
            var gts = recipe.SixClass ? allGts : allGts.Where(b => b.TypeId == 1 || b.TypeId == 3).ToList();
            var others = recipe.SixClass
                ? new List<PcbGoldenEvaluator.GtBox>()
                : allGts.Where(b => b.TypeId != 1 && b.TypeId != 3).ToList();

            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var frame = new FrameBuffer(
                test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);
            var outcome = detector.Detect(frame);
            if (outcome.IsFault)
            {
                report.Warnings.Add($"Fault {outcome.FaultCode} on {stem}");
                continue;
            }

            var preds = outcome.Defects.ToList();
            report.TotalPredBlobs += preds.Count;
            var (predMatched, gtMatched) = PcbGoldenEvaluator.MatchOneToOne(preds, gts, iouHit);

            var (gdx, gdy, xor0, xorBest) = GlobalShift(test, temp);
            report.PairShifts.Add(new PairShift { Stem = stem, Dx = gdx, Dy = gdy, XorAt0 = xor0, XorBest = xorBest });

            for (var pi = 0; pi < preds.Count; pi++)
            {
                if (!predMatched[pi]) continue;
                report.TpCount++;
                var (frac, _, _) = ShiftExplain(preds[pi], test, temp, diffThresh);
                if (frac >= ShiftExplainedMin)
                    report.TpShiftExplained.Add($"{stem}#{pi} ({frac:P0})");
                var dil = DilationExplain(preds[pi], test, temp, diffThresh);
                if (dil >= ShiftExplainedMin)
                    report.TpDilationExplained.Add($"{stem}#{pi} ({dil:P0})");
            }

            for (var pi = 0; pi < preds.Count; pi++)
            {
                if (predMatched[pi]) continue;
                var item = Analyze(stem, pi, preds[pi], test, temp, copperMax, diffThresh, gts, gtMatched, others);
                item.Category = Classify(item);
                item.CropPng = $"crops/{stem}_fp{pi:D2}.png";
                SaveCrop(Path.Combine(outDir, item.CropPng), preds[pi], test, temp, gts, others);
                report.Items.Add(item);
            }
        }

        report.FpCount = report.Items.Count;
        foreach (var c in Category.Order)
            report.ByCategory[c] = report.Items.Count(i => i.Category == c);
        foreach (var g in report.Items.Where(i => i.OverlapOtherClass != null).GroupBy(i => i.OverlapOtherClass!))
            report.OtherClassBreakdown[g.Key] = g.Count();

        File.WriteAllText(Path.Combine(outDir, "fp_audit.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "fp_audit.md"), RenderMarkdown(report), new UTF8Encoding(false));
        return report;
    }

    private static string Classify(FpItem i)
    {
        if (i.OverlapOtherClass != null) return Category.OtherClass;
        if (i.GapToCommittedGt >= 0 && i.GapToCommittedGt <= NearGtGapPx)
            return i.NearestCommittedGtMatched ? Category.Fragment : Category.Offset;
        if (i.ShiftExplained >= ShiftExplainedMin) return Category.Misregistration;
        if (i.MaxDepth <= ShallowDepthPx) return Category.EdgeShallow;
        if (i.Thickness <= 1) return Category.Thin;
        return Category.Other;
    }

    private static FpItem Analyze(
        string stem, int index, DefectItem d, FrameBuffer test, FrameBuffer temp, int copperMax, int diffThresh,
        List<PcbGoldenEvaluator.GtBox> gts, bool[] gtMatched, List<PcbGoldenEvaluator.GtBox> others)
    {
        var item = new FpItem { Stem = stem, Index = index, X = d.X, Y = d.Y, W = d.W, H = d.H, Area = d.Score };

        var (wx0, wy0, ww, wh) = Window(d, test.Width, test.Height);
        var blob = BlobMask(d, wx0, wy0, ww, wh);

        // 缺口深度：从模板非铜像素出发的棋盘距离场，取 blob 内最大值
        var depthField = Bfs(ww, wh, (x, y) => temp.GetPixel(wx0 + x, wy0 + y) > copperMax);
        // 厚度：从 blob 外像素出发的距离场，取 blob 内最大值
        var thickField = Bfs(ww, wh, (x, y) => !blob[y * ww + x]);

        long sum = 0; var n = 0; var maxDiff = int.MinValue; var maxDepth = 0; var thick = 0;
        for (var y = 0; y < wh; y++)
        for (var x = 0; x < ww; x++)
        {
            var k = y * ww + x;
            if (!blob[k]) continue;
            var diff = test.GetPixel(wx0 + x, wy0 + y) - temp.GetPixel(wx0 + x, wy0 + y);
            sum += diff; n++;
            if (diff > maxDiff) maxDiff = diff;
            if (depthField[k] > maxDepth) maxDepth = depthField[k];
            if (thickField[k] > thick) thick = thickField[k];
        }
        item.MaxDiff = n > 0 ? maxDiff : 0;
        item.MeanDiff = n > 0 ? Math.Round((double)sum / n, 1) : 0;
        item.MaxDepth = maxDepth;
        item.Thickness = thick;
        (item.ShiftExplained, item.BestDx, item.BestDy) = ShiftExplain(d, test, temp, diffThresh);
        item.DilationExplained = DilationExplain(d, test, temp, diffThresh);
        item.OppRatio = d.OppRatio;

        var best = int.MaxValue;
        for (var gi = 0; gi < gts.Count; gi++)
        {
            var gap = BoxGap(d, gts[gi]);
            if (gap < best) { best = gap; item.NearestCommittedGtMatched = gtMatched[gi]; }
        }
        item.GapToCommittedGt = best == int.MaxValue ? -1 : best;

        var hitOther = others.Where(o => BoxGap(d, o) <= 4).OrderBy(o => BoxGap(d, o)).FirstOrDefault();
        item.OverlapOtherClass = hitOther?.ClassName;
        return item;
    }

    /// <summary>有符号缺铜差分 test(x,y) − temp(x+dx,y+dy) &lt; 阈值 视为被平移解释；取 ±ShiftTolPx 内最大比例。</summary>
    public static (double frac, int dx, int dy) ShiftExplain(DefectItem d, FrameBuffer test, FrameBuffer temp, int diffThresh)
    {
        var (wx0, wy0, ww, wh) = Window(d, test.Width, test.Height);
        var blob = BlobMask(d, wx0, wy0, ww, wh);
        var total = blob.Count(b => b);
        if (total == 0) return (0, 0, 0);
        double best = -1; int bdx = 0, bdy = 0;
        for (var dy = -ShiftTolPx; dy <= ShiftTolPx; dy++)
        for (var dx = -ShiftTolPx; dx <= ShiftTolPx; dx++)
        {
            var ok = 0;
            for (var y = 0; y < wh; y++)
            for (var x = 0; x < ww; x++)
            {
                if (!blob[y * ww + x]) continue;
                int gx = wx0 + x, gy = wy0 + y;
                int tx = Math.Min(test.Width - 1, Math.Max(0, gx + dx));
                int ty = Math.Min(test.Height - 1, Math.Max(0, gy + dy));
                if (test.GetPixel(gx, gy) - temp.GetPixel(tx, ty) < diffThresh) ok++;
            }
            var frac = (double)ok / total;
            if (frac > best || (frac == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bdx) + Math.Abs(bdy)))
            {
                best = frac; bdx = dx; bdy = dy;
            }
        }
        return (Math.Round(best, 3), bdx, bdy);
    }

    /// <summary>
    /// 等价 gray_dilation_rect(temp, 2·ShiftTolPx+1)：test(x,y) − 邻域内模板最大值 &lt; 阈值 视为被解释。
    /// 逐像素取最优平移，能吸收孔径/线宽的 ±ShiftTolPx 胀缩，比单一平移更宽松。
    /// </summary>
    public static double DilationExplain(DefectItem d, FrameBuffer test, FrameBuffer temp, int diffThresh)
    {
        var (wx0, wy0, ww, wh) = Window(d, test.Width, test.Height);
        var blob = BlobMask(d, wx0, wy0, ww, wh);
        int total = 0, ok = 0;
        for (var y = 0; y < wh; y++)
        for (var x = 0; x < ww; x++)
        {
            if (!blob[y * ww + x]) continue;
            total++;
            int gx = wx0 + x, gy = wy0 + y;
            var maxT = 0;
            for (var dy = -ShiftTolPx; dy <= ShiftTolPx; dy++)
            for (var dx = -ShiftTolPx; dx <= ShiftTolPx; dx++)
            {
                int tx = Math.Min(test.Width - 1, Math.Max(0, gx + dx));
                int ty = Math.Min(test.Height - 1, Math.Max(0, gy + dy));
                var v = temp.GetPixel(tx, ty);
                if (v > maxT) maxT = v;
            }
            if (test.GetPixel(gx, gy) - maxT < diffThresh) ok++;
        }
        return total > 0 ? Math.Round((double)ok / total, 3) : 0;
    }

    /// <summary>整图二值 XOR 最小的全局平移（±3px，去掉 4px 边）。</summary>
    private static (int dx, int dy, int xorAt0, int xorBest) GlobalShift(FrameBuffer test, FrameBuffer temp)
    {
        int Xor(int dx, int dy)
        {
            var n = 0;
            for (var y = 4; y < test.Height - 4; y++)
            for (var x = 4; x < test.Width - 4; x++)
                if ((test.GetPixel(x, y) >= 128) != (temp.GetPixel(x + dx, y + dy) >= 128)) n++;
            return n;
        }
        var at0 = Xor(0, 0);
        int best = at0, bdx = 0, bdy = 0;
        for (var dy = -3; dy <= 3; dy++)
        for (var dx = -3; dx <= 3; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var v = Xor(dx, dy);
            if (v < best) { best = v; bdx = dx; bdy = dy; }
        }
        return (bdx, bdy, at0, best);
    }

    private static (int x0, int y0, int w, int h) Window(DefectItem d, int imgW, int imgH)
    {
        var x0 = Math.Max(0, d.X - CropMargin);
        var y0 = Math.Max(0, d.Y - CropMargin);
        var x1 = Math.Min(imgW - 1, d.X + d.W - 1 + CropMargin);
        var y1 = Math.Min(imgH - 1, d.Y + d.H - 1 + CropMargin);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static bool[] BlobMask(DefectItem d, int wx0, int wy0, int ww, int wh)
    {
        var mask = new bool[ww * wh];
        if (d.HasRuns)
        {
            for (var r = 0; r < d.RunRows!.Length; r++)
            {
                var y = d.RunRows[r] - wy0;
                if ((uint)y >= (uint)wh) continue;
                for (var x = d.RunColBegin![r]; x <= d.RunColEnd![r]; x++)
                {
                    var lx = x - wx0;
                    if ((uint)lx < (uint)ww) mask[y * ww + lx] = true;
                }
            }
        }
        else
        {
            for (var y = d.Y; y < d.Y + d.H; y++)
            for (var x = d.X; x < d.X + d.W; x++)
                mask[(y - wy0) * ww + (x - wx0)] = true;
        }
        return mask;
    }

    /// <summary>多源 BFS 棋盘距离；source 像素距离为 0，无源可达时为 int.MaxValue/2 截断。</summary>
    private static int[] Bfs(int w, int h, Func<int, int, bool> isSource)
    {
        var dist = new int[w * h];
        var q = new Queue<int>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var k = y * w + x;
            if (isSource(x, y)) { dist[k] = 0; q.Enqueue(k); }
            else dist[k] = int.MaxValue / 2;
        }
        while (q.Count > 0)
        {
            var k = q.Dequeue();
            int cx = k % w, cy = k / w;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                var nk = ny * w + nx;
                if (dist[nk] > dist[k] + 1) { dist[nk] = dist[k] + 1; q.Enqueue(nk); }
            }
        }
        return dist;
    }

    private static int BoxGap(DefectItem d, PcbGoldenEvaluator.GtBox g)
    {
        var dx = Math.Max(0, Math.Max(g.X1 - (d.X + d.W - 1), d.X - g.X2));
        var dy = Math.Max(0, Math.Max(g.Y1 - (d.Y + d.H - 1), d.Y - g.Y2));
        return Math.Max(dx, dy);
    }

    private static void SaveCrop(
        string path, DefectItem d, FrameBuffer test, FrameBuffer temp,
        List<PcbGoldenEvaluator.GtBox> gts, List<PcbGoldenEvaluator.GtBox> others)
    {
        var (wx0, wy0, ww, wh) = Window(d, test.Width, test.Height);
        var blob = BlobMask(d, wx0, wy0, ww, wh);
        const int sep = 4;
        var pw = ww * CropScale;
        var ph = wh * CropScale;
        var outW = pw * 3 + sep * 2;
        var bgr = new byte[outW * ph * 3];
        for (var i = 0; i < bgr.Length; i++) bgr[i] = 255;

        for (var panel = 0; panel < 3; panel++)
        {
            var ox = panel * (pw + sep);
            for (var y = 0; y < wh; y++)
            for (var x = 0; x < ww; x++)
            {
                var tv = temp.GetPixel(wx0 + x, wy0 + y);
                var sv = test.GetPixel(wx0 + x, wy0 + y);
                byte b, g, r;
                if (panel == 0) { b = g = r = tv; }
                else if (panel == 1)
                {
                    b = g = r = sv;
                    if (blob[y * ww + x]) { r = 255; g = (byte)(sv / 3); b = (byte)(sv / 3); }
                }
                else { b = g = r = (byte)Math.Min(255, Math.Abs(sv - tv) * 2); }
                for (var sy = 0; sy < CropScale; sy++)
                for (var sx = 0; sx < CropScale; sx++)
                    Put(bgr, outW, ph, ox + x * CropScale + sx, y * CropScale + sy, b, g, r);
            }

            foreach (var gt in gts)
                Rect(bgr, outW, ph, ox, wx0, wy0, ww, wh, gt.X1, gt.Y1, gt.X2, gt.Y2, 0, 200, 0);
            foreach (var o in others)
                Rect(bgr, outW, ph, ox, wx0, wy0, ww, wh, o.X1, o.Y1, o.X2, o.Y2, 255, 128, 0);
            if (panel != 1)
                Rect(bgr, outW, ph, ox, wx0, wy0, ww, wh, d.X, d.Y, d.X + d.W - 1, d.Y + d.H - 1, 0, 0, 255);
        }
        ImageIo.SaveBgrPng(path, bgr, outW, ph);
    }

    private static void Rect(byte[] bgr, int outW, int outH, int ox, int wx0, int wy0, int ww, int wh,
        int x1, int y1, int x2, int y2, byte b, byte g, byte r)
    {
        var lx1 = (x1 - wx0) * CropScale;
        var ly1 = (y1 - wy0) * CropScale;
        var lx2 = (x2 - wx0 + 1) * CropScale - 1;
        var ly2 = (y2 - wy0 + 1) * CropScale - 1;
        var maxX = ww * CropScale - 1;
        var maxY = wh * CropScale - 1;
        if (lx2 < 0 || ly2 < 0 || lx1 > maxX || ly1 > maxY) return;
        int cx1 = Math.Max(0, lx1), cy1 = Math.Max(0, ly1), cx2 = Math.Min(maxX, lx2), cy2 = Math.Min(maxY, ly2);
        for (var t = 0; t < 2; t++)
        {
            for (var x = cx1; x <= cx2; x++)
            {
                if (ly1 + t >= 0 && ly1 + t <= maxY) Put(bgr, outW, outH, ox + x, ly1 + t, b, g, r);
                if (ly2 - t >= 0 && ly2 - t <= maxY) Put(bgr, outW, outH, ox + x, ly2 - t, b, g, r);
            }
            for (var y = cy1; y <= cy2; y++)
            {
                if (lx1 + t >= 0 && lx1 + t <= maxX) Put(bgr, outW, outH, ox + lx1 + t, y, b, g, r);
                if (lx2 - t >= 0 && lx2 - t <= maxX) Put(bgr, outW, outH, ox + lx2 - t, y, b, g, r);
            }
        }
    }

    private static void Put(byte[] bgr, int w, int h, int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        var i = (y * w + x) * 3;
        bgr[i] = b;
        bgr[i + 1] = g;
        bgr[i + 2] = r;
    }

    public static string RenderMarkdown(AuditReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DeepPCB 过杀归因（只读审计）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{r.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 引擎：{r.Engine} · 配方 {r.RecipeId} v{r.RecipeVersion} hash=`{r.RecipeHash}`");
        sb.AppendLine($"- 对数：{r.PairCount} · Pred blobs={r.TotalPredBlobs} · 未匹配 FP={r.FpCount}");
        sb.AppendLine("- 口径：匹配逻辑与金标完全一致（一对一贪心）；归类为启发式规则，按下表顺序取第一个命中的类别。");
        sb.AppendLine($"- 规则：非承诺类 GT 框外扩 4px 内 → 落在非承诺类；距承诺类 GT ≤{NearGtGapPx}px → 碎片/偏框；±{ShiftTolPx}px 平移可解释 ≥{ShiftExplainedMin:P0} → 局部错位；缺口深度 ≤{ShallowDepthPx}px → 铜边浅层；内切半径 ≤1 → 细条。");
        sb.AppendLine("- 截图：左=模板，中=测试（红=Pred blob），右=|差分|×2；绿框=承诺类 GT，蓝框=非承诺类 GT，红框=该 FP。");
        sb.AppendLine();
        sb.AppendLine("## 按类别");
        sb.AppendLine();
        sb.AppendLine("| 类别 | 个数 | 占 FP |");
        sb.AppendLine("|------|------|-------|");
        foreach (var c in Category.Order)
        {
            var n = r.ByCategory.TryGetValue(c, out var v) ? v : 0;
            sb.AppendLine($"| {c} | {n} | {(r.FpCount > 0 ? (double)n / r.FpCount : 0):P1} |");
        }
        if (r.OtherClassBreakdown.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("非承诺类细分：" + string.Join("，", r.OtherClassBreakdown.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        sb.AppendLine();
        sb.AppendLine($"## ±{ShiftTolPx}px 平移检验（位移容差差分的预演）");
        sb.AppendLine();
        var fpExplained = r.Items.Count(i => i.ShiftExplained >= ShiftExplainedMin);
        sb.AppendLine($"- FP 中可被平移解释（≥{ShiftExplainedMin:P0} blob 像素）：**{fpExplained}/{r.FpCount}**（不论归入哪一类）");
        sb.AppendLine($"- 已命中 TP 中也可被平移解释（会被误删）：**{r.TpShiftExplained.Count}/{r.TpCount}**" +
                      (r.TpShiftExplained.Count > 0 ? "：" + string.Join("，", r.TpShiftExplained) : ""));
        var fpDil = r.Items.Count(i => i.DilationExplained >= ShiftExplainedMin);
        sb.AppendLine($"- 模板 {2 * ShiftTolPx + 1}×{2 * ShiftTolPx + 1} 灰度膨胀后：FP 可解释 **{fpDil}/{r.FpCount}**，TP 会被误删 **{r.TpDilationExplained.Count}/{r.TpCount}**" +
                      (r.TpDilationExplained.Count > 0 ? "：" + string.Join("，", r.TpDilationExplained) : ""));
        sb.AppendLine("- 注意：TP 被平移解释≠GT 一定漏检（同一 GT 可能有别的 blob 命中），须以金标重跑为准。");
        sb.AppendLine();
        var shifted = r.PairShifts.Where(s => s.Dx != 0 || s.Dy != 0).ToList();
        sb.AppendLine($"整图全局最佳平移（±3px，二值 XOR 最小）非零的对：**{shifted.Count}/{r.PairShifts.Count}**");
        if (shifted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| 对 | dx | dy | XOR@0 | XOR@最佳 | 降幅 |");
            sb.AppendLine("|----|----|----|-------|----------|------|");
            foreach (var s in shifted)
                sb.AppendLine($"| {s.Stem} | {s.Dx} | {s.Dy} | {s.XorAt0} | {s.XorBest} | {(s.XorAt0 > 0 ? 1 - (double)s.XorBest / s.XorAt0 : 0):P1} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 逐个 FP");
        sb.AppendLine();
        sb.AppendLine("| 对 | # | 框 (x,y,w,h) | 面积 | 最大差分 | 深度 | 厚度 | 平移解释 | 膨胀解释 | 反极性 | 距承诺GT | 类别 | 截图 |");
        sb.AppendLine("|----|---|--------------|------|----------|------|------|----------|----------|--------|----------|------|------|");
        foreach (var i in r.Items)
        {
            var gap = i.GapToCommittedGt < 0 ? "-" : i.GapToCommittedGt.ToString(CultureInfo.InvariantCulture);
            var cat = i.OverlapOtherClass != null ? $"{i.Category}（{i.OverlapOtherClass}）" : i.Category;
            var opp = i.OppRatio < 0 ? "-" : i.OppRatio.ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {i.Stem} | {i.Index} | {i.X},{i.Y},{i.W},{i.H} | {i.Area:F0} | {i.MaxDiff} | {i.MaxDepth} | {i.Thickness} | {i.ShiftExplained:P0} ({i.BestDx},{i.BestDy}) | {i.DilationExplained:P0} | {opp} | {gap} | {cat} | [png]({i.CropPng}) |");
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in r.Warnings) sb.AppendLine($"- {w}");
        }
        return sb.ToString();
    }
}
