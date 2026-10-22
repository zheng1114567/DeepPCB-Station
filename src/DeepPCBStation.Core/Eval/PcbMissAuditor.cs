using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

// 漏检归因（只读）：定位未命中 GT 在管线哪一步被丢。匹配口径与金标一致。
public sealed class PcbMissAuditor
{
    public static class Stage
    {
        public const string NoDiff = "差分无像素";
        public const string Opening = "开运算抹掉";
        public const string Unmatched = "有blob但匹配不上";
        public const string DetectedTaken = "已检出但被别的GT占用";

        public static readonly string[] Order =
        {
            NoDiff, Opening,
            RejectedBlob.Reasons.AreaTooSmall, RejectedBlob.Reasons.AreaTooLarge,
            RejectedBlob.Reasons.Aspect, RejectedBlob.Reasons.PinHole, RejectedBlob.Reasons.Tip,
            RejectedBlob.Reasons.TypeMinArea, RejectedBlob.Reasons.ScaleArea, RejectedBlob.Reasons.ScaleDepth,
            RejectedBlob.Reasons.Thin, RejectedBlob.Reasons.Edge, RejectedBlob.Reasons.EdgePair,
            RejectedBlob.Reasons.Lop,
            RejectedBlob.Reasons.Border,
            Unmatched, DetectedTaken
        };
    }

    public sealed class MissItem
    {
        public string Stem { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int RawDiffPx { get; set; }
        public string Stage { get; set; } = "";
        public double BlobArea { get; set; } = -1;
        public double BlobAspect { get; set; } = -1;
        public double Surround { get; set; } = -1;
        public int RingComponents { get; set; } = -1;
        public double LopFrac { get; set; } = -1;
        public int LopDx { get; set; }
        public int LopDy { get; set; }
        public double OppRatio { get; set; } = -1;
        public double MaxDepth { get; set; } = -1;
    }

    public sealed class AuditReport
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SamplesDir { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public int PairCount { get; set; }
        public bool SixClass { get; set; }
        public int OpenGt { get; set; }
        public int MousebiteGt { get; set; }
        public int ShortGt { get; set; }
        public int SpurGt { get; set; }
        public int CopperGt { get; set; }
        public int PinholeGt { get; set; }
        public List<MissItem> Items { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? OutputDir { get; set; }

        public int GtCount(string className) => className switch
        {
            "open" => OpenGt,
            "mousebite" => MousebiteGt,
            "short" => ShortGt,
            "spur" => SpurGt,
            "copper" => CopperGt,
            "pinhole" => PinholeGt,
            _ => 0
        };
    }

    public AuditReport Run(
        string samplesDir,
        HalconPcbDefectDetector detector,
        RecipeConfig recipe,
        string reportsDir,
        double iouHit = PcbGoldenEvaluator.DefaultIouHit,
        int maxPairs = 1000)
    {
        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);
        detector.CollectRejected = true;
        var thresh = recipe.DiffThreshold > 0 ? recipe.DiffThreshold : 30;

        var report = new AuditReport
        {
            Timestamp = DateTimeOffset.Now, SamplesDir = samplesDir, RecipeHash = recipe.ContentHash,
            SixClass = recipe.SixClass
        };
        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(maxPairs).ToList();
        report.PairCount = pairs.Count;

        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            var gts = recipe.SixClass
                ? PcbGoldenEvaluator.LoadAllBoxes(annPath)
                : PcbGoldenEvaluator.LoadCommittedBoxes(annPath);
            foreach (var g in gts)
                switch (g.TypeId)
                {
                    case 1: report.OpenGt++; break;
                    case 2: report.ShortGt++; break;
                    case 3: report.MousebiteGt++; break;
                    case 4: report.SpurGt++; break;
                    case 5: report.CopperGt++; break;
                    case 6: report.PinholeGt++; break;
                }

            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var frame = new FrameBuffer(test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);
            var outcome = detector.Detect(frame);
            if (outcome.IsFault)
            {
                report.Warnings.Add($"Fault {outcome.FaultCode} on {stem}");
                continue;
            }

            var preds = outcome.Defects.ToList();
            var (_, gtMatched) = PcbGoldenEvaluator.MatchOneToOne(preds, gts, iouHit);
            if (gtMatched.All(m => m)) continue;

            var tempPx = temp.Pixels;
            if (recipe.RegisterShiftMax > 0)
                tempPx = recipe.RegisterSubPixel
                    ? TileShiftRegistration.AlignSmooth(test.Pixels, temp.Pixels, test.Width, test.Height,
                        recipe.RegisterShiftMax, recipe.RegisterTiles, recipe.RegisterMinGain, out _)
                    : TileShiftRegistration.Align(test.Pixels, temp.Pixels, test.Width, test.Height,
                        recipe.RegisterShiftMax, recipe.RegisterTiles, recipe.RegisterMinGain, out _);
            var rejected = outcome.Rejected ?? Array.Empty<RejectedBlob>();

            for (var gi = 0; gi < gts.Count; gi++)
            {
                if (gtMatched[gi]) continue;
                var g = gts[gi];
                var item = new MissItem
                {
                    Stem = stem, ClassName = g.ClassName, X1 = g.X1, Y1 = g.Y1, W = g.W, H = g.H,
                    RawDiffPx = RawDiff(test, tempPx, g, thresh)
                };

                var rej = rejected
                    .Select(r => (r, s: PcbGoldenEvaluator.MatchScore(r.Blob, g, iouHit)))
                    .Where(t => t.s > 0)
                    .OrderByDescending(t => t.s)
                    .Select(t => t.r)
                    .FirstOrDefault();
                if (preds.Any(p => PcbGoldenEvaluator.MatchScore(p, g, iouHit) > 0))
                    item.Stage = Stage.DetectedTaken;
                else if (rej != null)
                {
                    item.Stage = rej.Reason;
                    item.BlobArea = rej.Area;
                    item.BlobAspect = Math.Round(rej.Aspect, 2);
                    item.Surround = Math.Round(rej.Surround, 3);
                    item.RingComponents = rej.RingComponents;
                    item.LopFrac = rej.LopFrac;
                    item.LopDx = rej.LopDx;
                    item.LopDy = rej.LopDy;
                    item.OppRatio = rej.OppRatio;
                    item.MaxDepth = rej.MaxDepth;
                }
                else if (item.RawDiffPx == 0)
                    item.Stage = Stage.NoDiff;
                else if (preds.Any(p => Overlaps(p, g)) || rejected.Any(r => Overlaps(r.Blob, g)))
                    item.Stage = Stage.Unmatched;
                else
                    item.Stage = Stage.Opening;
                report.Items.Add(item);
            }
        }

        var stamp = report.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine(reportsDir, $"miss_audit_{stamp}");
        Directory.CreateDirectory(outDir);
        report.OutputDir = outDir;
        File.WriteAllText(Path.Combine(outDir, "miss_audit.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "miss_audit.md"), RenderMarkdown(report), new UTF8Encoding(false));
        return report;
    }

    private static int RawDiff(FrameBuffer test, byte[] tempPx, PcbGoldenEvaluator.GtBox g, int thresh)
    {
        // 双极性：缺铜（test 亮）与多铜（test 暗）都算有差分信号
        var n = 0;
        for (var y = Math.Max(0, g.Y1); y <= Math.Min(test.Height - 1, g.Y2); y++)
        for (var x = Math.Max(0, g.X1); x <= Math.Min(test.Width - 1, g.X2); x++)
        {
            var d = test.GetPixel(x, y) - tempPx[y * test.Width + x];
            if (d >= thresh || d <= -thresh) n++;
        }
        return n;
    }

    private static bool Overlaps(DefectItem d, PcbGoldenEvaluator.GtBox g) =>
        d.X <= g.X2 && d.X + d.W - 1 >= g.X1 && d.Y <= g.Y2 && d.Y + d.H - 1 >= g.Y1;

    public static string RenderMarkdown(AuditReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DeepPCB 漏检归因（只读审计）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{r.Timestamp:yyyy-MM-dd HH:mm:ss} · 样本：`{r.SamplesDir}` · 对数：{r.PairCount}");
        sb.AppendLine($"- 配方 hash=`{r.RecipeHash}` · GT：open={r.OpenGt} mousebite={r.MousebiteGt} short={r.ShortGt} spur={r.SpurGt} copper={r.CopperGt} pinhole={r.PinholeGt}");
        sb.AppendLine("- 归因：优先取与 GT 匹配得上（中心落框或 IoU≥阈值）的被拒 blob 的拒因；没有则看框内是否有差分像素、是否有 blob 重叠。");
        sb.AppendLine();
        sb.AppendLine("## 按阶段");
        sb.AppendLine();
        var classNames = r.SixClass
            ? new[] { "open", "mousebite", "short", "spur", "copper", "pinhole" }
            : new[] { "open", "mousebite" };
        sb.Append("| 阶段 |");
        foreach (var c in classNames) sb.Append($" {c} |");
        sb.AppendLine(" 合计 |");
        sb.Append("|------|");
        for (var i = 0; i < classNames.Length; i++) sb.Append("------|");
        sb.AppendLine("------|");
        foreach (var s in Stage.Order)
        {
            var n = r.Items.Count(i => i.Stage == s);
            if (n == 0) continue;
            sb.Append($"| {s} |");
            foreach (var c in classNames) sb.Append($" {r.Items.Count(i => i.Stage == s && i.ClassName == c)} |");
            sb.AppendLine($" {n} |");
        }
        sb.Append("| **合计漏检** |");
        foreach (var c in classNames)
            sb.Append($" {r.Items.Count(i => i.ClassName == c)}/{r.GtCount(c)} |");
        sb.AppendLine($" {r.Items.Count} |");
        sb.AppendLine();
        sb.AppendLine("## 被拒 blob 特征分布（漏检 GT 对应的那个 blob）");
        sb.AppendLine();
        foreach (var g in r.Items.Where(i => i.BlobArea >= 0).GroupBy(i => i.Stage))
        {
            var areas = g.Select(i => i.BlobArea).OrderBy(v => v).ToList();
            var asp = g.Select(i => i.BlobAspect).OrderBy(v => v).ToList();
            var sur = g.Where(i => i.Surround >= 0).Select(i => i.Surround).OrderBy(v => v).ToList();
            sb.Append($"- **{g.Key}**（{g.Count()}）：面积 [{string.Join(", ", areas.Select(v => v.ToString("F0", CultureInfo.InvariantCulture)))}]");
            sb.Append($"；长宽比 [{string.Join(", ", asp.Select(v => v.ToString("F1", CultureInfo.InvariantCulture)))}]");
            if (sur.Count > 0)
                sb.Append($"；surround [{string.Join(", ", sur.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)))}]");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("## 逐个漏检");
        sb.AppendLine();
        sb.AppendLine("| 对 | 类 | GT框 (x,y,w,h) | 框内差分px | 阶段 | blob面积 | 长宽比 | surround | 环分量 | 深度 | LOP | 反极性 |");
        sb.AppendLine("|----|----|----------------|------------|------|----------|--------|----------|--------|------|-----|--------|");
        foreach (var i in r.Items)
            sb.AppendLine($"| {i.Stem} | {i.ClassName} | {i.X1},{i.Y1},{i.W},{i.H} | {i.RawDiffPx} | {i.Stage} | {(i.BlobArea >= 0 ? i.BlobArea.ToString("F0", CultureInfo.InvariantCulture) : "-")} | {(i.BlobAspect >= 0 ? i.BlobAspect.ToString("F1", CultureInfo.InvariantCulture) : "-")} | {(i.Surround >= 0 ? i.Surround.ToString("F2", CultureInfo.InvariantCulture) : "-")} | {(i.RingComponents >= 0 ? i.RingComponents.ToString(CultureInfo.InvariantCulture) : "-")} | {(i.MaxDepth >= 0 ? i.MaxDepth.ToString("F1", CultureInfo.InvariantCulture) : "-")} | {(i.LopFrac >= 0 ? i.LopFrac.ToString("F3", CultureInfo.InvariantCulture) + $"({i.LopDx},{i.LopDy})" : "-")} | {(i.OppRatio >= 0 ? i.OppRatio.ToString("F2", CultureInfo.InvariantCulture) : "-")} |");
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in r.Warnings) sb.AppendLine($"- {w}");
        }
        return sb.ToString();
    }
}
