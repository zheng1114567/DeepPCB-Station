using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

// 纹理可分性诊断（只读，不改任何判定闸门）。
//
// 目的：回答「鬼影（过杀）与小面积浅咬真缺陷（漏检）在灰度幅值上高度重叠，
// 能否用正交的判别信息（局部纹理/结构能量）分开」。
// 做法：取两类区域各一组，算结构类特征，报每维特征的 AUC（Mann-Whitney U）与最优阈值。
// 若AUC 显著 >0.5，说明存在可用的新判别维度；否则该方向应证伪、不必落闸门。
public sealed class PcbTextureSeparability
{
    /// <summary>特征名前缀，与落盘 CSV 列名一致。</summary>
    public static readonly string[] Features =
    {
        "GradMean", "GradP95", "LapMean", "EdgeDensity", "Entropy", "Range", "Sharpness"
    };

    public sealed class Row
    {
        public string Stem { get; set; } = "";
        /// <summary>POS=漏检真缺陷；NEG=过杀/鬼影。</summary>
        public string Label { get; set; } = "";
        public string Reason { get; set; } = "";
        public string ClassName { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        /// <summary>框内差分像素数（ROI 规模），供人工核量级。</summary>
        public int DiffPx { get; set; }
        public Dictionary<string, double> Feat { get; set; } = new();
    }

    public sealed class FeatureStat
    {
        public string Name { get; set; } = "";
        public int PosCount { get; set; }
        public int NegCount { get; set; }
        public double PosMean { get; set; }
        public double NegMean { get; set; }
        public double PosMedian { get; set; }
        public double NegMedian { get; set; }
        /// <summary>AUC=P(pos>neg)+0.5·P(pos==neg)。0.5=不可分。</summary>
        public double Auc { get; set; }
        /// <summary>按该特征单独分流的准确率下界（取较优方向）。</summary>
        public double BestAcc { get; set; }
        public double BestThreshold { get; set; }
    }

    public sealed class Report
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SamplesDir { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public string Engine { get; set; } = "";
        public int PairCount { get; set; }
        public int PosCount { get; set; }
        public int NegCount { get; set; }
        public List<Row> Rows { get; set; } = new();
        public List<FeatureStat> Stats { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string? OutputDir { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>跑诊断。maxPairs 与其它审计工具同口径。</summary>
    public Report Run(
        string samplesDir,
        HalconPcbDefectDetector detector,
        RecipeConfig recipe,
        string reportsDir,
        double iouHit = PcbGoldenEvaluator.DefaultIouHit,
        int maxPairs = 1000)
    {
        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);
        // POS 区域优先取被拒 blob（真正被闸门杀掉的那个），故必须开收集。
        detector.CollectRejected = true;
        var diffThresh = recipe.DiffThreshold > 0 ? recipe.DiffThreshold : 30;

        var report = new Report
        {
            Timestamp = DateTimeOffset.Now,
            SamplesDir = samplesDir,
            RecipeHash = recipe.ContentHash,
            Engine = detector.EngineName,
            Note = "特征在待检图(test)的框内计算；POS=漏检真缺陷，NEG=未匹配 GT 的过杀 pred。"
                 + "AUC=0.5 表示该维度不可分。AUC 越高表示越可用。"
        };

        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(maxPairs).ToList();
        report.PairCount = pairs.Count;

        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            var allGts = PcbGoldenEvaluator.LoadAllBoxes(annPath);
            var gts = recipe.SixClass
                ? allGts
                : allGts.Where(b => b.TypeId == 1 || b.TypeId == 3).ToList();

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
            var rejected = outcome.Rejected ?? Array.Empty<RejectedBlob>();
            var (predMatched, gtMatched) = PcbGoldenEvaluator.MatchOneToOne(preds, gts, iouHit);

            // POS：漏检的真缺陷。区域取与之匹配最好的被拒 blob，退化到 GT 框本身。
            for (var gi = 0; gi < gts.Count; gi++)
            {
                if (gtMatched[gi]) continue;
                var g = gts[gi];
                var rej = rejected
                    .Select(r => (r, s: PcbGoldenEvaluator.MatchScore(r.Blob, g, iouHit)))
                    .Where(t => t.s > 0)
                    .OrderByDescending(t => t.s)
                    .Select(t => t.r)
                    .FirstOrDefault();
                DefectItem? box = rej != null ? rej.Blob : null;
                report.Rows.Add(MakeRow(test, temp, stem, "POS",
                    rej?.Reason ?? "noBlob",
                    g.ClassName,
                    box != null ? box.X : g.X1,
                    box != null ? box.Y : g.Y1,
                    box != null ? box.W : g.W,
                    box != null ? box.H : g.H,
                    diffThresh));
            }

            // NEG：未匹配到任何 GT 的 pred（过杀/鬼影候选）。口径与 FpAuditor 一致。
            for (var i = 0; i < preds.Count; i++)
            {
                if (predMatched[i]) continue;
                var p = preds[i];
                report.Rows.Add(MakeRow(test, temp, stem, "NEG", "fp", p.TypeCode,
                    p.X, p.Y, p.W, p.H, diffThresh));
            }
        }

        report.PosCount = report.Rows.Count(r => r.Label == "POS");
        report.NegCount = report.Rows.Count(r => r.Label == "NEG");
        report.Stats = ComputeStats(report.Rows);
        if (report.PosCount < 10 || report.NegCount < 10)
            report.Warnings.Add(
                $"样本不足（POS={report.PosCount} / NEG={report.NegCount}）。"
                + "AUC 在小样本下不可靠，请换更大的 --samples 或调大 --max-pairs。");

        var stamp = report.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine(reportsDir, $"tex_sep_{stamp}");
        Directory.CreateDirectory(outDir);
        report.OutputDir = outDir;
        File.WriteAllText(Path.Combine(outDir, "tex_sep.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "tex_sep.md"), RenderMarkdown(report), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "tex_sep.csv"), RenderCsv(report), new UTF8Encoding(false));
        return report;
    }

    private static Row MakeRow(
        FrameBuffer test, FrameBuffer temp, string stem, string label, string reason, string className,
        int x, int y, int w, int h, int diffThresh)
    {
        var row = new Row
        {
            Stem = stem, Label = label, Reason = reason, ClassName = className,
            X = x, Y = y, W = Math.Max(1, w), H = Math.Max(1, h)
        };
        // 至少取 12×12，保证梯度/Laplacian 有邻域可用。
        int bw = Math.Max(12, row.W), bh = Math.Max(12, row.H);

        // ROI = 框内双极性差分达到阈值的像素（POS/NEG 同一口径）。
        // 只统计这些像素上的结构特征，避免框内大量正常像素稀释信号。
        var mask = BuildDiffMask(test.Pixels, temp.Pixels, test.Width, test.Height,
            row.X, row.Y, bw, bh, diffThresh);
        var cnt = 0;
        foreach (var m in mask) if (m) cnt++;
        row.DiffPx = cnt;
        Compute(test.Pixels, test.Width, test.Height, row.X, row.Y, bw, bh, mask, row.Feat);
        return row;
    }

    /// <summary>框内 |test−template| ≥ 阈值的像素掩膜。</summary>
    private static bool[] BuildDiffMask(byte[] px, byte[] tp, int width, int height,
        int x0, int y0, int bw, int bh, int thresh)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        var x1 = Math.Min(width - 1, x0 + bw - 1);
        var y1 = Math.Min(height - 1, y0 + bh - 1);
        var mask = new bool[Math.Max(0, (x1 - x0 + 1) * (y1 - y0 + 1))];
        var k = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var d = px[y * width + x] - tp[y * width + x];
            mask[k++] = Math.Abs(d) >= thresh;
        }
        return mask;
    }

    /// <summary>
    /// 在 test 图框内、且限定于差分掩膜像素上算结构类特征。
    /// 灰度幅值类特征刻意不算——已知不可分（MaxDiff 仅作对照列）。
    /// </summary>
    private static void Compute(byte[] px, int width, int height, int x0, int y0, int bw, int bh,
        bool[] mask, Dictionary<string, double> feat)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        var x1 = Math.Min(width - 1, x0 + bw - 1);
        var y1 = Math.Min(height - 1, y0 + bh - 1);

        var grads = new List<double>(1024);
        var laps = new List<double>(1024);
        var hist = new int[32];
        double min = 255, max = 0;
        var n = 0;

        var m = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++, m++)
        {
            if (!mask[m]) continue;
            var v = px[y * width + x];
            hist[v >> 3]++;
            if (v < min) min = v;
            if (v > max) max = v;
            n++;
            if (n == 1) { min = v; max = v; }

            if (x <= 0 || y <= 0 || x >= width - 1 || y >= height - 1) continue;
            var i = y * width + x;
            // Sobel
            var gx = -px[i - width - 1] - 2 * px[i - 1] - px[i + width - 1]
                     + px[i - width + 1] + 2 * px[i + 1] + px[i + width + 1];
            var gy = -px[i - width - 1] - 2 * px[i - width] - px[i - width + 1]
                     + px[i + width - 1] + 2 * px[i + width] + px[i + width + 1];
            grads.Add(Math.Sqrt(gx * gx + gy * gy) / 4.0);
            // Laplacian
            var lp = px[i - 1] + px[i + 1] + px[i - width] + px[i + width] - 4 * v;
            laps.Add(Math.Abs((double)lp));
        }

        feat["GradMean"] = grads.Count > 0 ? grads.Average() : 0;
        feat["GradP95"] = grads.Count > 0 ? Percentile(grads, 0.95) : 0;
        feat["LapMean"] = laps.Count > 0 ? laps.Average() : 0;
        feat["EdgeDensity"] = grads.Count > 0 ? grads.Count(g => g > 40) / (double)grads.Count : 0;
        feat["Range"] = n == 0 ? 0 : max - min;

        // Shannon 熵（32 bin）
        var ent = 0.0;
        if (n > 0)
            foreach (var c in hist)
            {
                if (c == 0) continue;
                var pr = c / (double)n;
                ent -= pr * Math.Log(pr, 2);
            }
        feat["Entropy"] = ent;

        // Sharpness：p95 与均值梯度之比 —— 近似「跃变宽度」的倒数。
        // 鬼影是渐变的（低）；锐利结构破坏（真缺陷）是高。
        var gmean = feat["GradMean"];
        feat["Sharpness"] = gmean > 1e-6 ? feat["GradP95"] / gmean : 0;
    }

    private static double Percentile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var a = sorted.ToArray();
        Array.Sort(a);
        var idx = (int)Math.Round(q * (a.Length - 1));
        return a[Math.Max(0, Math.Min(a.Length - 1, idx))];
    }

    private static List<FeatureStat> ComputeStats(List<Row> rows)
    {
        var pos = rows.Where(r => r.Label == "POS").ToList();
        var neg = rows.Where(r => r.Label == "NEG").ToList();
        var stats = new List<FeatureStat>();
        foreach (var f in Features)
        {
            var pv = pos.Select(r => r.Feat.TryGetValue(f, out var v) ? v : 0).OrderBy(v => v).ToList();
            var nv = neg.Select(r => r.Feat.TryGetValue(f, out var v) ? v : 0).OrderBy(v => v).ToList();
            if (pv.Count == 0 || nv.Count == 0) continue;

            // Mann-Whitney U：逐对比较 O(nm)，本工具样本量在千级，可接受。
            var gt = 0; var eq = 0;
            foreach (var p in pv)
                foreach (var n in nv)
                {
                    if (p > n) gt++;
                    else if (p == n) eq++;
                }
            var auc = (gt + 0.5 * eq) / (pv.Count * (double)nv.Count);

            // 最优单阈值准确率（扫所有候选切点）
            var all = pv.Concat(nv).Distinct().OrderBy(v => v).ToList();
            var bestAcc = 0.0; var bestT = 0.0;
            for (var i = 0; i < all.Count; i++)
            {
                var t = all[i];
                var tp = pv.Count(v => v >= t); var fp = nv.Count(v => v >= t);
                var tn = neg.Count - fp; var fn = pos.Count - tp;
                var acc = (tp + tn) / (double)(pos.Count + neg.Count);
                if (acc > bestAcc) { bestAcc = acc; bestT = t; }
            }

            stats.Add(new FeatureStat
            {
                Name = f, PosCount = pv.Count, NegCount = nv.Count,
                PosMean = pv.Average(), NegMean = nv.Average(),
                PosMedian = Percentile(pv, 0.5), NegMedian = Percentile(nv, 0.5),
                Auc = Math.Round(auc, 4),
                BestAcc = Math.Round(bestAcc, 4),
                BestThreshold = Math.Round(bestT, 3)
            });
        }
        return stats.OrderByDescending(s => s.Auc).ToList();
    }

    private static string RenderCsv(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("stem,label,reason,class,x,y,w,h,diffPx," + string.Join(",", Features));
        foreach (var i in r.Rows)
            sb.AppendLine(string.Join(",",
                i.Stem, i.Label, i.Reason, i.ClassName,
                i.X, i.Y, i.W, i.H, i.DiffPx,
                string.Join(",", Features.Select(f => i.Feat.TryGetValue(f, out var v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "0"))));
        return sb.ToString();
    }

    public static string RenderMarkdown(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 纹理/结构特征可分性诊断（只读，未改任何闸门）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{r.Timestamp:yyyy-MM-dd HH:mm:ss} · 样本：`{r.SamplesDir}` · 对数：{r.PairCount}");
        sb.AppendLine($"- 配方 hash=`{r.RecipeHash}` · 引擎：{r.Engine}");
        sb.AppendLine($"- 样本量：**POS（漏检真缺陷）={r.PosCount}** / **NEG（过杀鬼影）={r.NegCount}**");
        sb.AppendLine();
        sb.AppendLine("## 结论表（按 AUC 降序）");
        sb.AppendLine();
        sb.AppendLine("| 特征 | AUC | 最优单阈值准确率 | 切点 | POS 中位 | NEG 中位 | POS 均值 | NEG 均值 |");
        sb.AppendLine("|------|-----|------------------|------|----------|----------|----------|----------|");
        foreach (var s in r.Stats)
            sb.AppendLine($"| {s.Name} | {s.Auc:F3} | {s.BestAcc:P1} | {s.BestThreshold:F3} | {s.PosMedian:F2} | {s.NegMedian:F2} | {s.PosMean:F2} | {s.NegMean:F2} |");
        sb.AppendLine();
        sb.AppendLine("**读法**：AUC=0.500 不可分；0.65以下增益存疑；0.75 以上视为可用维度。"
                    + "AUC 为负相关时其补特征同效（表中按单向上调扫描，方向取绝对值判读）。");
        sb.AppendLine();
        if (r.Stats.Count > 0)
        {
            var best = r.Stats[0];
            var second = r.Stats.Skip(1).FirstOrDefault();
            sb.AppendLine($"- 最强维度：**{best.Name}**（AUC {best.Auc:F3}）；次强：{(second == null ? "—" : $"{second.Name} {second.Auc:F3}")}。");
            sb.AppendLine(best.Auc >= 0.75
                ? "- 判定：**存在可用的新判别维度**，可进入 S2 落闸门。"
                : "- 判定：**无强可分维度**（最强 AUC < 0.75）。该方向应按「已证伪」记录，不再提议纹理特征闸门。");
        }
        sb.AppendLine();
        sb.AppendLine("## 分布细分（POS 按漏检原因）");
        sb.AppendLine();
        var byReason = r.Rows.Where(i => i.Label == "POS").GroupBy(i => i.Reason).OrderByDescending(g => g.Count());
        if (byReason.Any())
        {
            sb.AppendLine("| 漏检原因 | n | " + string.Join(" | ", r.Stats.Select(s => s.Name)) + " |");
            sb.AppendLine("|----------|---|" + string.Concat(r.Stats.Select(_ => "---|")));
            foreach (var g in byReason)
                sb.AppendLine($"| {g.Key} | {g.Count()} | " + string.Join(" | ",
                    r.Stats.Select(s =>
                    {
                        var vals = g.Select(i => i.Feat.TryGetValue(s.Name, out var v) ? v : 0).OrderBy(v => v).ToList();
                        return vals.Count == 0 ? "-" : $"{Percentile(vals, 0.5):F2}";
                    })) + " |");
            sb.AppendLine();
            sb.AppendLine("（表中为该组特征中位数。）");
        }
        sb.AppendLine();
        sb.AppendLine("## POS vs NEG 的 ROI 规模（框内差分像素数）");
        sb.AppendLine();
        foreach (var g in r.Rows.GroupBy(i => i.Label))
        {
            var label = g.Key;
            var v = g.Select(i => (double)i.DiffPx).OrderBy(x => x).ToList();
            sb.AppendLine($"- {label}（n={v.Count}）：ROI 差分像素 中位 {Percentile(v, 0.5):F0}，p25 {Percentile(v, 0.25):F0}，p75 {Percentile(v, 0.75):F0}");
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in r.Warnings) sb.AppendLine($"- {w}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("逐条数据见 `tex_sep.csv`；本诊断不构成成绩，未改检测管线。");
        return sb.ToString();
    }
}