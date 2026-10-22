using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

// 金标：bbox→区域近似。Pred 分型不可靠，命中只看位置（中心落框或 IoU≥阈值）。
public sealed class PcbGoldenEvaluator
{
    public const double DefaultIouHit = 0.33;
    public const double MissMax = 0.20;
    public const double OverkillMax = 0.50;

    public sealed class GtBox
    {
        public string ClassName { get; init; } = ""; // 见 ClassNameOf
        public int TypeId { get; init; }
        public int X1 { get; init; }
        public int Y1 { get; init; }
        public int X2 { get; init; }
        public int Y2 { get; init; }
        public int W => Math.Max(1, X2 - X1 + 1);
        public int H => Math.Max(1, Y2 - Y1 + 1);
    }

    public sealed class ClassStats
    {
        public string ClassName { get; set; } = "";
        public int GtCount { get; set; }
        public int GtHit { get; set; }
        public int GtMiss { get; set; }
        public double MissRate { get; set; }
        public double MissCiLow { get; set; }
        public double MissCiHigh { get; set; }
        public int PredMatchedToClass { get; set; }
        public int PredFpForClass { get; set; }
        public double OverkillRate { get; set; }
        public bool PassMiss { get; set; }
        public bool PassOverkill { get; set; }
        public bool Pass => PassMiss && PassOverkill;
    }

    public sealed class Report
    {
        public DateTimeOffset Timestamp { get; set; }
        public string RecipeId { get; set; } = "";
        public string RecipeVersion { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public string Engine { get; set; } = "";
        public string BboxApproxNote { get; set; } =
            "金标为 bbox→区域近似（GT 以轴对齐框填充满矩形；命中=Pred 中心落在框内或 IoU≥阈值），不是像素级工业封样。";
        public string ClassifierNote { get; set; } =
            "Pred TypeCode 为长宽比启发式，非可靠分类器；金标匹配忽略 Pred 分型，主看命中/过杀。";
        public string OverlayNoiseNote { get; set; } =
            "首期 overlay 可有噪声候选框；不得把噪声框当过杀已控。本报表过杀来自 Pred↔GT 对照。";
        public double IouHitThreshold { get; set; } = DefaultIouHit;
        public int PairCount { get; set; }
        /// <summary>计入金标门槛的对数（排除 roi=fallback_full）。</summary>
        public int ScoredPairCount { get; set; }
        public int FallbackPairCount { get; set; }
        public int PredFault { get; set; }
        public int PredOk { get; set; }
        public int PredNg { get; set; }
        public int TotalPredBlobs { get; set; }
        public int UnmatchedPredBlobs { get; set; }
        public double GlobalFpRate { get; set; }
        public double FpCiLow { get; set; }
        public double FpCiHigh { get; set; }
        /// <summary>至少含一个未匹配 Pred 的板数。</summary>
        public int BoardsWithFp { get; set; }
        public double BoardFpRate { get; set; }
        public double BoardFpCiLow { get; set; }
        public double BoardFpCiHigh { get; set; }
        public ClassStats Open { get; set; } = new() { ClassName = "open" };
        public ClassStats Mousebite { get; set; } = new() { ClassName = "mousebite" };
        public ClassStats Short { get; set; } = new() { ClassName = "short" };
        public ClassStats Spur { get; set; } = new() { ClassName = "spur" };
        public ClassStats Copper { get; set; } = new() { ClassName = "copper" };
        public ClassStats Pinhole { get; set; } = new() { ClassName = "pinhole" };
        public bool GatePass { get; set; }
        public bool CopperRoiEffective { get; set; }
        public string Verdict { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
        public List<string> PairSummaries { get; set; } = new();
        public List<string> FallbackPairs { get; set; } = new();
        /// <summary>相对 v0.2.0 基线的全局 FP 对比（仅非 fallback）。</summary>
        public BaselineFpCompare? VsV020 { get; set; }
        public string? ReportJsonPath { get; set; }
        public string? ReportMdPath { get; set; }
    }

    public sealed class BaselineFpCompare
    {
        public string BaselineLabel { get; set; } = "v0.2.0";
        public int BaselinePredBlobs { get; set; }
        public int BaselineFp { get; set; }
        public double BaselineFpRate { get; set; }
        public int CandidatePredBlobs { get; set; }
        public int CandidateFp { get; set; }
        public double CandidateFpRate { get; set; }
        public double FpRateDelta { get; set; }
        public bool FpReduced { get; set; }
        public string Note { get; set; } = "全对统计。";
    }

    public Report Run(
        string samplesDir,
        IDefectDetector detector,
        RecipeConfig recipe,
        string reportsDir,
        double iouHit = DefaultIouHit,
        int maxPairs = 100)
    {
        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);

        var pairs = DiscoverPairs(samplesDir).Take(maxPairs).ToList();
        var report = new Report
        {
            Timestamp = DateTimeOffset.Now,
            RecipeId = recipe.Id,
            RecipeVersion = recipe.Version,
            RecipeHash = recipe.ContentHash,
            Engine = detector.EngineName,
            IouHitThreshold = iouHit,
            PairCount = pairs.Count
        };

        if (string.Equals(detector.EngineName, "Stub", StringComparison.OrdinalIgnoreCase))
            report.Warnings.Add("引擎=Stub：数字仅冒烟，不得当交付 Halcon 成绩。");

        if (pairs.Count == 0)
        {
            report.Warnings.Add("无可用成对样例");
            report.Verdict = "质量债停拧：无样例";
            report.GatePass = false;
            WriteReports(report, reportsDir);
            return report;
        }

        // 全局：每个 Pred 最多匹配一个 committed GT；每个 GT 最多被一个 Pred 命中
        // ★ roi=fallback_full 的对：不计门槛、不并入相对 v0.2 FP 对比
        var sixClass = recipe.SixClass;
        var openGt = 0; var openHit = 0;
        var mbGt = 0; var mbHit = 0;
        var shortGt = 0; var shortHit = 0;
        var spurGt = 0; var spurHit = 0;
        var copperGt = 0; var copperHit = 0;
        var pinholeGt = 0; var pinholeHit = 0;
        var totalPred = 0; var matchedPred = 0;
        var boardsWithFp = 0;
        var scoredPairs = 0;

        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            var gts = sixClass ? LoadAllBoxes(annPath) : LoadCommittedBoxes(annPath);

            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var frame = new FrameBuffer(
                test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);

            var outcome = detector.Detect(frame);

            if (outcome.IsFault)
            {
                report.PredFault++;
                report.Warnings.Add($"Fault {outcome.FaultCode} on {stem}");
                report.PairSummaries.Add($"{stem}: FAULT:{outcome.FaultCode}");
                continue;
            }

            scoredPairs++;
            openGt += gts.Count(g => g.TypeId == 1);
            mbGt += gts.Count(g => g.TypeId == 3);
            if (sixClass)
            {
                shortGt += gts.Count(g => g.TypeId == 2);
                spurGt += gts.Count(g => g.TypeId == 4);
                copperGt += gts.Count(g => g.TypeId == 5);
                pinholeGt += gts.Count(g => g.TypeId == 6);
            }

            if (outcome.IsOk) report.PredOk++;
            else report.PredNg++;

            var preds = outcome.Defects.ToList();
            totalPred += preds.Count;

            var (predMatched, gtMatched) = MatchOneToOne(preds, gts, iouHit);
            matchedPred += predMatched.Count(m => m);
            openHit += gts.Where((g, i) => g.TypeId == 1 && gtMatched[i]).Count();
            mbHit += gts.Where((g, i) => g.TypeId == 3 && gtMatched[i]).Count();
            if (sixClass)
            {
                shortHit += gts.Where((g, i) => g.TypeId == 2 && gtMatched[i]).Count();
                spurHit += gts.Where((g, i) => g.TypeId == 4 && gtMatched[i]).Count();
                copperHit += gts.Where((g, i) => g.TypeId == 5 && gtMatched[i]).Count();
                pinholeHit += gts.Where((g, i) => g.TypeId == 6 && gtMatched[i]).Count();
            }

            var hitOpen = gts.Where((g, i) => g.TypeId == 1 && gtMatched[i]).Count();
            var missOpen = gts.Where((g, i) => g.TypeId == 1 && !gtMatched[i]).Count();
            var hitMb = gts.Where((g, i) => g.TypeId == 3 && gtMatched[i]).Count();
            var missMb = gts.Where((g, i) => g.TypeId == 3 && !gtMatched[i]).Count();
            var fp = predMatched.Count(m => !m);
            if (fp > 0) boardsWithFp++;
            report.PairSummaries.Add(
                $"{stem}: preds={preds.Count} fp={fp} open hit/miss={hitOpen}/{missOpen} mousebite hit/miss={hitMb}/{missMb}");
        }

        report.ScoredPairCount = scoredPairs;
        report.TotalPredBlobs = totalPred;
        report.UnmatchedPredBlobs = Math.Max(0, totalPred - matchedPred);
        report.GlobalFpRate = totalPred > 0 ? (double)report.UnmatchedPredBlobs / totalPred : 0;
        (report.FpCiLow, report.FpCiHigh) = Wilson(report.UnmatchedPredBlobs, totalPred);
        report.BoardsWithFp = boardsWithFp;
        report.BoardFpRate = scoredPairs > 0 ? (double)boardsWithFp / scoredPairs : 0;
        (report.BoardFpCiLow, report.BoardFpCiHigh) = Wilson(boardsWithFp, scoredPairs);

        report.Open = BuildClassStats("open", openGt, openHit, report.GlobalFpRate);
        report.Mousebite = BuildClassStats("mousebite", mbGt, mbHit, report.GlobalFpRate);
        report.Short = BuildClassStats("short", shortGt, shortHit, report.GlobalFpRate);
        report.Spur = BuildClassStats("spur", spurGt, spurHit, report.GlobalFpRate);
        report.Copper = BuildClassStats("copper", copperGt, copperHit, report.GlobalFpRate);
        report.Pinhole = BuildClassStats("pinhole", pinholeGt, pinholeHit, report.GlobalFpRate);

        // 相对 v0.2.0 基线 FP（冻结报告数字）
        report.VsV020 = new BaselineFpCompare
        {
            BaselineLabel = "v0.2.0",
            BaselinePredBlobs = 182,
            BaselineFp = 70,
            BaselineFpRate = 0.38461538461538464,
            CandidatePredBlobs = report.TotalPredBlobs,
            CandidateFp = report.UnmatchedPredBlobs,
            CandidateFpRate = report.GlobalFpRate,
            FpRateDelta = report.GlobalFpRate - 0.38461538461538464,
            FpReduced = report.GlobalFpRate < 0.38461538461538464 - 1e-9
        };

        var allClassesPass = report.Open.Pass && report.Mousebite.Pass
                             && (!sixClass || (report.Short.Pass && report.Spur.Pass && report.Copper.Pass && report.Pinhole.Pass));
        report.GatePass = allClassesPass && report.PredFault == 0
                          && report.ScoredPairCount > 0
                          && (sixClass || report.VsV020.FpReduced);
        if (report.PredFault > 0)
        {
            report.GatePass = false;
            report.Verdict = "质量债停拧：存在 FAULT，检测未完整跑通";
        }
        else if (report.ScoredPairCount == 0)
        {
            report.GatePass = false;
            report.Verdict = "质量债停拧：无可用样例";
        }
        else if (!sixClass && !report.VsV020.FpReduced)
        {
            report.GatePass = false;
            report.Verdict = "质量债停拧：相对 v0.2.0 全局 FP 未下降";
            report.Warnings.Add("反驳口径：一轮不过永久停，禁连拧；禁止同轮滑开 C。");
        }
        else if (allClassesPass)
        {
            report.GatePass = true;
            report.Verdict = sixClass
                ? "通过：六类漏检≤20% 且过杀≤50%"
                : "通过：open/mousebite 漏检≤20% 且过杀≤50%，且相对 v0.2 FP 下降";
        }
        else
        {
            report.GatePass = false;
            report.Verdict = "质量债停拧：未达首期门槛（过杀≤50%/漏检≤20%），停止拧检测参";
            report.Warnings.Add("反驳口径：一轮不过永久停，禁连拧；禁止同轮滑开 C。");
        }

        report.Warnings.Add("未计算、未报告 Pred 分型准确率（非可靠分类器）。");

        WriteReports(report, reportsDir);
        return report;
    }

    private static ClassStats BuildClassStats(string name, int gt, int hit, double globalFp)
    {
        var miss = Math.Max(0, gt - hit);
        var missRate = gt > 0 ? (double)miss / gt : 0;
        // 过杀：因分型不可靠，采用全局 FP 率（未匹配任何承诺类 GT 的 Pred / 全部 Pred）
        // 按类各报同一 GlobalFpRate，并在报告中写明口径。
        var s = new ClassStats
        {
            ClassName = name,
            GtCount = gt,
            GtHit = hit,
            GtMiss = miss,
            MissRate = missRate,
            PredFpForClass = -1, // 哨兵：使用全局 FP
            OverkillRate = globalFp,
            PassMiss = gt == 0 || missRate <= MissMax,
            PassOverkill = globalFp <= OverkillMax
        };
        (s.MissCiLow, s.MissCiHigh) = Wilson(miss, gt);
        return s;
    }

    /// <summary>Wilson 95% 区间；n=0 时返回 (0,1)。</summary>
    private static (double Low, double High) Wilson(int k, int n)
    {
        if (n <= 0) return (0, 1);
        const double z = 1.96;
        var p = (double)k / n;
        var z2 = z * z;
        var denom = 1 + z2 / n;
        var center = (p + z2 / (2.0 * n)) / denom;
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>贪心一对一：每个 Pred 最多匹配一个 GT，每个 GT 最多被一个 Pred 命中。</summary>
    public static (bool[] predMatched, bool[] gtMatched) MatchOneToOne(
        IReadOnlyList<DefectItem> preds, IReadOnlyList<GtBox> gts, double iouHit)
    {
        var gtMatched = new bool[gts.Count];
        var predMatched = new bool[preds.Count];
        var cand = new List<(int pi, int gi, double score)>();
        for (var pi = 0; pi < preds.Count; pi++)
        {
            for (var gi = 0; gi < gts.Count; gi++)
            {
                var score = MatchScore(preds[pi], gts[gi], iouHit);
                if (score > 0)
                    cand.Add((pi, gi, score));
            }
        }
        foreach (var (pi, gi, _) in cand.OrderByDescending(c => c.score))
        {
            if (predMatched[pi] || gtMatched[gi]) continue;
            predMatched[pi] = true;
            gtMatched[gi] = true;
        }
        return (predMatched, gtMatched);
    }

    internal static double MatchScore(DefectItem pred, GtBox gt, double iouThr)
    {
        var pcx = pred.X + pred.W / 2.0;
        var pcy = pred.Y + pred.H / 2.0;
        var centerIn = pcx >= gt.X1 && pcx <= gt.X2 && pcy >= gt.Y1 && pcy <= gt.Y2;
        var iou = IoU(pred.X, pred.Y, pred.X + pred.W - 1, pred.Y + pred.H - 1, gt.X1, gt.Y1, gt.X2, gt.Y2);
        if (centerIn) return 1.0 + iou; // 中心落框优先
        if (iou >= iouThr) return iou;
        return 0;
    }

    /// <summary>
    /// 轴对齐框 IoU（闭区间像素坐标，x2/y2 含端点）。**评测口径的唯一实现**：
    /// 金标评测、导出器、审计器都必须调这里，避免各自复制一份后悄悄漂移。
    /// </summary>
    public static double IoU(int ax1, int ay1, int ax2, int ay2, int bx1, int by1, int bx2, int by2)
    {
        var ix1 = Math.Max(ax1, bx1);
        var iy1 = Math.Max(ay1, by1);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);
        var iw = Math.Max(0, ix2 - ix1 + 1);
        var ih = Math.Max(0, iy2 - iy1 + 1);
        var inter = iw * ih;
        if (inter <= 0) return 0;
        var a = Math.Max(1, ax2 - ax1 + 1) * Math.Max(1, ay2 - ay1 + 1);
        var b = Math.Max(1, bx2 - bx1 + 1) * Math.Max(1, by2 - by1 + 1);
        return (double)inter / (a + b - inter);
    }

    public static List<(string stem, string test, string temp, string ann)> DiscoverPairs(string samplesDir)
    {
        var list = new List<(string, string, string, string)>();
        if (!Directory.Exists(samplesDir)) return list;
        foreach (var test in Directory.EnumerateFiles(samplesDir, "*_test.jpg", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fname = Path.GetFileName(test);
            var stem = fname.EndsWith("_test.jpg", StringComparison.OrdinalIgnoreCase)
                ? fname.Substring(0, fname.Length - "_test.jpg".Length)
                : Path.GetFileNameWithoutExtension(fname);
            var temp = Path.Combine(samplesDir, stem + "_temp.jpg");
            var ann = Path.Combine(samplesDir, stem + ".txt");
            if (!File.Exists(temp) || !File.Exists(ann)) continue;
            list.Add((stem, test, temp, ann));
        }
        return list;
    }

    public static List<GtBox> LoadCommittedBoxes(string annPath) =>
        LoadAllBoxes(annPath).Where(b => b.TypeId == 1 || b.TypeId == 3).ToList();

    /// <summary>DeepPCB 全部 6 类：1 open · 2 short · 3 mousebite · 4 spur · 5 copper · 6 pin-hole。</summary>
    public static List<GtBox> LoadAllBoxes(string annPath)
    {
        var boxes = new List<GtBox>();
        foreach (var line in File.ReadAllLines(annPath))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            var p = t.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 5) continue;
            if (!int.TryParse(p[0], out var x1)) continue;
            if (!int.TryParse(p[1], out var y1)) continue;
            if (!int.TryParse(p[2], out var x2)) continue;
            if (!int.TryParse(p[3], out var y2)) continue;
            if (!int.TryParse(p[4], out var type)) continue;
            boxes.Add(new GtBox
            {
                TypeId = type,
                ClassName = ClassNameOf(type),
                X1 = Math.Min(x1, x2),
                Y1 = Math.Min(y1, y2),
                X2 = Math.Max(x1, x2),
                Y2 = Math.Max(y1, y2)
            });
        }
        return boxes;
    }

    public static string ClassNameOf(int type) => type switch
    {
        1 => "open",
        2 => "short",
        3 => "mousebite",
        4 => "spur",
        5 => "copper",
        6 => "pin-hole",
        _ => $"type{type}"
    };

    private static void WriteReports(Report report, string reportsDir)
    {
        Directory.CreateDirectory(reportsDir);
        var stamp = report.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(reportsDir, $"golden_{stamp}.json");
        var mdPath = Path.Combine(reportsDir, $"golden_{stamp}.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
        File.WriteAllText(mdPath, RenderMarkdown(report), new UTF8Encoding(false));
        // 固定最新指针
        File.WriteAllText(Path.Combine(reportsDir, "golden_latest.md"), RenderMarkdown(report), new UTF8Encoding(false));
        File.Copy(jsonPath, Path.Combine(reportsDir, "golden_latest.json"), overwrite: true);
        report.ReportJsonPath = jsonPath;
        report.ReportMdPath = mdPath;
        report.Warnings.Add($"报告已写: {mdPath}");
    }

    public static string RenderMarkdown(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DeepPCB 金标报告（bbox→区域近似）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{r.Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 引擎：{r.Engine} · 配方 {r.RecipeId} v{r.RecipeVersion} hash=`{r.RecipeHash}`");
        sb.AppendLine($"- 对数：{r.PairCount}（≤100）· IoU={r.IouHitThreshold}");
        sb.AppendLine($"- **结论：{r.Verdict}**");
        sb.AppendLine();
        sb.AppendLine("## 口径声明");
        sb.AppendLine();
        sb.AppendLine($"- {r.BboxApproxNote}");
        sb.AppendLine($"- {r.ClassifierNote}");
        sb.AppendLine($"- {r.OverlayNoiseNote}");
        sb.AppendLine("- 过杀按类列示时采用 **全局 FP 率**（未匹配任何 open/mousebite GT 的 Pred / 全部 Pred），因分型不可靠、不按 Pred TypeCode 归类刷数。");
        sb.AppendLine("- **禁止**把论文 ≈89% mAP 写成己方成绩。");
        sb.AppendLine();
        sb.AppendLine("## 按类指标（门槛：漏检≤20%，过杀≤50%）");
        sb.AppendLine();
        sb.AppendLine("| 类 | GT数 | 命中 | 漏检 | 漏检率 | 漏检95%CI | 过杀率(全局FP) | 漏检过门 | 过杀过门 |");
        sb.AppendLine("|----|------|------|------|--------|-----------|----------------|----------|----------|");
        var classes = r.Short.GtCount > 0 || r.Spur.GtCount > 0 || r.Copper.GtCount > 0 || r.Pinhole.GtCount > 0
            ? new[] { r.Open, r.Mousebite, r.Short, r.Spur, r.Copper, r.Pinhole }
            : new[] { r.Open, r.Mousebite };
        foreach (var c in classes)
        {
            sb.AppendLine($"| {c.ClassName} | {c.GtCount} | {c.GtHit} | {c.GtMiss} | {c.MissRate:P1} | {c.MissCiLow:P1}–{c.MissCiHigh:P1} | {c.OverkillRate:P1} | {(c.PassMiss ? "是" : "否")} | {(c.PassOverkill ? "是" : "否")} |");
        }
        sb.AppendLine();
        sb.AppendLine($"- Pred OK/NG/FAULT = {r.PredOk}/{r.PredNg}/{r.PredFault}（计分对）");
        sb.AppendLine($"- Pred blobs = {r.TotalPredBlobs}，未匹配 FP = {r.UnmatchedPredBlobs}，全局 FP 率 = {r.GlobalFpRate:P1}（95%CI {r.FpCiLow:P1}–{r.FpCiHigh:P1}）");
        sb.AppendLine($"- 板级：{r.BoardsWithFp}/{r.ScoredPairCount} 板含噪声框（{r.BoardFpRate:P1}，95%CI {r.BoardFpCiLow:P1}–{r.BoardFpCiHigh:P1}）");
        sb.AppendLine();
        if (r.VsV020 != null)
        {
            var v = r.VsV020;
            sb.AppendLine("## 相对 v0.2.0 全局 FP 对比");
            sb.AppendLine();
            sb.AppendLine("| 配方 | Pred blobs | FP | 全局 FP 率 |");
            sb.AppendLine("|------|------------|----|------------|");
            sb.AppendLine($"| {v.BaselineLabel} | {v.BaselinePredBlobs} | {v.BaselineFp} | {v.BaselineFpRate:P1} |");
            sb.AppendLine($"| candidate | {v.CandidatePredBlobs} | {v.CandidateFp} | {v.CandidateFpRate:P1} |");
            sb.AppendLine();
            sb.AppendLine($"- ΔFP率 = {v.FpRateDelta:+0.0%;-0.0%;0} · 下降？ **{(v.FpReduced ? "是" : "否")}**");
            sb.AppendLine($"- {v.Note}");
            sb.AppendLine();
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in r.Warnings)
                sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        if (r.PairSummaries.Count > 0)
        {
            sb.AppendLine("## 逐对摘要");
            sb.AppendLine();
            foreach (var p in r.PairSummaries)
                sb.AppendLine($"- `{p}`");
        }
        return sb.ToString();
    }
}
