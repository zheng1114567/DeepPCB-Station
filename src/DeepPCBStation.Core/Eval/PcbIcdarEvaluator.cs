using System.Globalization;
using System.Text;
using System.Text.Json;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

/// <summary>
/// DeepPCB 官方口径评测（ICDAR 2015 修改版）：IoU≥0.33、同类型、同图一对一贪心匹配。
/// 双口径输出：
///   · 严格分型 mAP/F-score —— 官方论文法（mAP 98.6% / F 98.2%）的同类可比口径（要求类型匹配）。
///   · 忽略分型 mAP/F-score —— 定位能力上界参考（任何类型 pred 可匹配任何类型 GT）。
/// 置信度 = 候选面积（仅用于排序；无阈值截断，故 F-score 取全量匹配点）。
/// 对标说明（2026-10-22 更正）：DeepPCB 论文（Tang et al., arXiv:1902.06197）的 98.6% mAP / 98.2% F
///   是 **VGG16-tiny + Group Pyramid Pooling 深度神经网络模型**的成绩（数据集 README 原文
///   "our model based on deep neural network"），**不是传统模板差分法**；传统 image processing
///   基线约 89.3%。故本项目 97.0% 检出 F 属「传统 vs DL 差 1.2pp」的跨范式对比，不得表述为"同范式对拼"。
///   数字以 reports/ 落盘证据为准。
/// </summary>
public sealed class PcbIcdarEvaluator
{
    public const double IouConstraint = 0.33;

    /// <summary>
    /// 离线验证专用：按 "stem|x1|y1|x2|y2" 覆盖 pred 的类别（用于验证 DL 分型替代规则树）。
    /// null = 不覆盖，生产路径与既有基线逐位不变。
    /// </summary>
    public IReadOnlyDictionary<string, int>? TypeOverride { get; set; }

    private double _iou = IouConstraint;
    private static string _lastConfMode = "score";

    private static readonly string[] ClassNames =
        { "open", "short", "mousebite", "spur", "copper", "pinhole" };

    private sealed class Box
    {
        public int Pair;          // 所属图对索引（跨图不可匹配）
        public int X1, Y1, X2, Y2;
        public int Type;          // 1..6
        public double Conf;       // 排序用置信度
        public int W => Math.Max(1, X2 - X1 + 1);
        public int H => Math.Max(1, Y2 - Y1 + 1);
    }

    public sealed class ClassAp
    {
        public int Type { get; init; }
        public string Name { get; init; } = "";
        public int GtCount { get; set; }
        public int PredCount { get; set; }
        public int TpStrict { get; set; }
        public double ApStrict { get; set; }
        public double ApStrict11 { get; set; }
    }

    /// <summary>外扩扫描单档结果：pred 框绕中心各向同性缩放后的官方口径指标。</summary>
    public sealed class GrowPoint
    {
        public double Factor { get; set; }
        public int IgnoreTp { get; set; }
        public double IgnoreF1 { get; set; }
        public double MapIgnore11 { get; set; }
        public int StrictTp { get; set; }
        public double StrictF1 { get; set; }
    }

    /// <summary>单类几何归因：哪一类的框系统与 GT 系统性不匹配（分型调优用）。</summary>
    public sealed class ClassGeo
    {
        public int Type { get; set; }
        public string Name { get; set; } = "";
        public int GtN { get; set; }
        public double MedianGtArea { get; set; }
        public double MedianPredArea { get; set; }
        public double MedianIoU { get; set; }
        public double IoUPassRate { get; set; }
    }

    /// <summary>框几何诊断：以 GT 为主体，取其最优重叠 pred，统计 IoU 分布与框尺寸比。</summary>
    public sealed class GeoStats
    {
        public int GtTotal { get; set; }
        public int GtWithOverlap { get; set; }      // 存在中心落框或 IoU>0 的 pred
        public int GtIouPass { get; set; }          // 其中 IoU≥约束
        public double MedianBestIou { get; set; }
        public double MedianGtArea { get; set; }
        public double MedianPredArea { get; set; }
        public double MedianSizeRatio { get; set; } // sqrt(pred/gt)，&gt;1 表示 pred 更大
        public int[] IouBuckets { get; set; } = new int[6]; // 0 / .0-.1 / .1-.2 / .2-.33 / .33-.5 / .5-1
        public int ContainedPred { get; set; }      // pred 完全落在 GT 内（框偏小的证据）
        public int CoveringPred { get; set; }       // pred 完全包住 GT（框偏大的证据）
        public List<ClassGeo> PerClass { get; set; } = new();
    }

    /// <summary>
    /// 工业 KPI（与学术指标并列）：FCR 假点率 与 Escape Rate 漏放率。
    /// </summary>
    /// <remarks>
    /// 工业 AOI 不看单一准确率，看这两个数：
    /// <list type="bullet">
    /// <item><b>FCR（False Call Rate）</b>：每板假点数。决定复判工位的人力成本——
    /// 每板 0.5 个假点与每板 3 个假点，在 F1 上可能只差 1pp，在产线上是 6 倍人力。</item>
    /// <item><b>Escape Rate（漏放率）</b>：有缺陷却没检出的板占比。这是客户真正投诉的指标，
    /// 单个缺陷漏检在 F1 上微不足道，但一个漏放板流到客户手里就是一次质量事故。</item>
    /// </list>
    /// </remarks>
    public sealed class IndustrialKpi
    {
        /// <summary>参与统计的板数。</summary>
        public int BoardCount { get; set; }

        // ---------- FCR ----------
        /// <summary>假点总数（未匹配上任何 GT 的 pred）。</summary>
        public int FalseCallCount { get; set; }

        /// <summary>平均每板假点数（核心 FCR 指标）。</summary>
        public double FalseCallPerBoard { get; set; }

        /// <summary>每板假点数的 P95（最坏 5% 板的工作量）。</summary>
        public double FalseCallPerBoardP95 { get; set; }

        /// <summary>单板最多假点数。</summary>
        public int FalseCallPerBoardMax { get; set; }

        /// <summary>含假点的板数。</summary>
        public int BoardsWithFalseCall { get; set; }

        /// <summary>板级假点率 = 含假点板 / 总板（工业常称 False Call Rate）。</summary>
        public double BoardFalseCallRate { get; set; }

        // ---------- Escape ----------
        /// <summary>含缺陷的板数（GT&gt;0）。</summary>
        public int DefectiveBoards { get; set; }

        /// <summary>漏放板数：有缺陷但一个都没命中的板。</summary>
        public int EscapedBoards { get; set; }

        /// <summary>板级漏放率 = 漏放板 / 含缺陷板。<b>工业主指标</b>。</summary>
        public double EscapeRateBoard { get; set; }

        /// <summary>
        /// 板级漏放率的 Wilson 95% <b>置信上界</b>。
        /// </summary>
        /// <remarks>
        /// <b>0/500 不等于 0%</b>。零事件时的 95% 上界约为 3/n（Rule of Three），
        /// 即 500 板零漏放只能说明真实漏放率 ≲ 0.76%，远达不到工业 ppm 级验收所需的样本量。
        /// 对外引用漏放率必须带上界，不得只报点估计。
        /// </remarks>
        public double EscapeRateBoardUpper95 { get; set; }

        /// <summary>缺陷级漏放率 = 漏检 GT / 总 GT（= 1 − 召回，仅供参考）。</summary>
        public double EscapeRateBox { get; set; }

        /// <summary>缺陷级漏放率的 Wilson 95% 置信上界。</summary>
        public double EscapeRateBoxUpper95 { get; set; }

        /// <summary>
        /// 要在此板数下证明「漏放率低于 target」所需的样本量（零事件时按 Rule of Three 反推）。
        /// 用于回答「当前验证规模够不够工业验收」这一必答题。
        /// </summary>
        public int BoardsNeededFor(double target)
            => target > 0 ? (int)Math.Ceiling(3.0 / target) : int.MaxValue;
    }

    public sealed class Report
    {
        public DateTimeOffset Timestamp { get; set; }
        public string RecipeId { get; set; } = "";
        public string RecipeVersion { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public string Engine { get; set; } = "";
        public int PairCount { get; set; }

        /// <summary>实际完成检测的对数（FAULT 板不计；KPI 分母用它而不是 PairCount）。</summary>
        public int InspectedPairs { get; set; }

        public List<ClassAp> Classes { get; set; } = new();

        public int TotalGt { get; set; }
        public int TotalPred { get; set; }

        // 严格分型
        public double MapStrict { get; set; }
        public double MapStrict11 { get; set; }
        public double StrictPrecision { get; set; }
        public double StrictRecall { get; set; }
        public double StrictF1 { get; set; }
        public int StrictTp { get; set; }

        // 忽略分型
        public double MapIgnore { get; set; }
        public double MapIgnore11 { get; set; }
        public double IgnorePrecision { get; set; }
        public double IgnoreRecall { get; set; }
        public double IgnoreF1 { get; set; }
        public int IgnoreTp { get; set; }

        // 诊断：忽略分型下的三种命中口径（同一贪心，仅判定谓词不同）。
        public int HitsCenterOrIou { get; set; }   // 中心落框 或 IoU≥阈值（=旧 F1 口径）
        public int HitsIouOnly { get; set; }        // 仅 IoU≥阈值（=官方口径位置）
        public int HitsTypeAndIou { get; set; }     // 类型一致 且 IoU≥阈值（=严格分型）

        public GeoStats Geometry { get; set; } = new();

        /// <summary>工业 KPI：FCR（每板假点数）与 Escape Rate（漏放率）。</summary>
        public IndustrialKpi Industrial { get; set; } = new();

        /// <summary>诊断混淆矩阵 [GT 类, Pred 类]：逐 GT 取同图 IoU 最优 pred（不强制一对一）。</summary>
        public List<List<int>> Confusion { get; set; } = new();
        public List<GrowPoint> GrowSweep { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
        public string ReportMdPath { get; set; } = "";
        public string ReportJsonPath { get; set; } = "";
    }

    public Report Run(
        string samplesDir, IDefectDetector detector, RecipeConfig recipe,
        string reportsDir, int maxPairs = int.MaxValue, double iou = IouConstraint, string? confMode = null)
    {
        _iou = iou;
        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);

        var report = new Report
        {
            Timestamp = DateTimeOffset.Now,
            RecipeId = recipe.Id,
            RecipeVersion = recipe.Version,
            RecipeHash = recipe.ContentHash,
            Engine = detector.EngineName
        };

        if (string.Equals(detector.EngineName, "Stub", StringComparison.OrdinalIgnoreCase))
            report.Warnings.Add("引擎=Stub：数字仅冒烟，不得当交付 Halcon 成绩。");

        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(maxPairs).ToList();
        report.PairCount = pairs.Count;
        // 默认用配方输出的标定置信度（v1.0.0）；未标定（=0）时逐框回落面积，口径不丢。
        confMode = (confMode ?? "score").ToLowerInvariant();
        if (confMode != "contrast" && confMode != "score") confMode = "area";
        _lastConfMode = confMode;
        if (pairs.Count == 0)
        {
            report.Warnings.Add("无可用成对样例");
            return report;
        }

        var allPred = new List<Box>();
        var allGt = new List<Box>();
        var detected = new bool[pairs.Count];
        var faultPairs = 0;

        for (var pairIdx = 0; pairIdx < pairs.Count; pairIdx++)
        {
            var (stem, testPath, tempPath, annPath) = pairs[pairIdx];

            // 两张图独立解码，并行可省约一半加载时间（ImageSharp 每次 Load 无共享状态）
            var testTask = System.Threading.Tasks.Task.Run(() => ImageIo.LoadGray(testPath));
            var tempTask = System.Threading.Tasks.Task.Run(() => ImageIo.LoadGray(tempPath));
            using var test = testTask.GetAwaiter().GetResult();
            using var temp = tempTask.GetAwaiter().GetResult();
            var frame = new FrameBuffer(
                test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);

            var outcome = detector.Detect(frame);
            if (outcome.IsFault)
            {
                // FAULT＝工位没看清（缺证/缺模板/内部异常），不是「检测器漏了缺陷」。
                // 产线语义是重拍复检、不计良率：该板 GT 不入指标、不进 KPI 分母，
                // 只记告警。否则一块 FAULT 板会同时污染召回、板级漏放率与 FCR 分母。
                faultPairs++;
                report.Warnings.Add($"Fault {outcome.FaultCode} on {stem}（未检测，已从全部指标与 KPI 分母剔除）");
                continue;
            }
            detected[pairIdx] = true;

            foreach (var g in PcbGoldenEvaluator.LoadAllBoxes(annPath))
                allGt.Add(new Box
                {
                    Pair = pairIdx, X1 = g.X1, Y1 = g.Y1, X2 = g.X2, Y2 = g.Y2,
                    Type = g.TypeId, Conf = 1.0
                });

            foreach (var d in outcome.Defects)
            {
                var t = TypeCodeToId(d.TypeCode);
                if (TypeOverride != null &&
                    TypeOverride.TryGetValue($"{stem}|{d.X}|{d.Y}|{d.X + d.W - 1}|{d.Y + d.H - 1}", out var ovType))
                    t = ovType;
                var x1 = Math.Max(0, d.X); var y1 = Math.Max(0, d.Y);
                var x2 = Math.Min(test.Width - 1, d.X + d.W - 1);
                var y2 = Math.Min(test.Height - 1, d.Y + d.H - 1);
                allPred.Add(new Box
                {
                    Pair = pairIdx,
                    X1 = d.X, Y1 = d.Y, X2 = d.X + d.W - 1, Y2 = d.Y + d.H - 1,
                    Type = t,
                    Conf = confMode == "contrast"
                        ? BoxContrast(test.Pixels, temp.Pixels, test.Stride, x1, y1, x2, y2)
                        : confMode == "score" && d.Confidence > 0
                            ? d.Confidence
                            : d.Score
                });
            }
        }

        report.TotalGt = allGt.Count;
        report.TotalPred = allPred.Count;
        report.InspectedPairs = detected.Count(d => d);
        if (faultPairs > 0)
            report.Warnings.Insert(0,
                $"共 {faultPairs}/{pairs.Count} 板 FAULT 未检测：其 GT 不进任何指标，也不进工业 KPI 分母（产线语义＝重拍复检）。");

        // ---------- 严格分型：per-class AP + 全局 TP ----------
        var gtByPairType = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < allGt.Count; i++)
        {
            var g = allGt[i];
            var key = (g.Pair, g.Type);
            if (!gtByPairType.TryGetValue(key, out var lst)) gtByPairType[key] = lst = new List<int>();
            lst.Add(i);
        }

        var classes = new List<ClassAp>();
        double apSum = 0, apSum11 = 0; var apN = 0;
        var strictTpTotal = 0;

        for (var c = 1; c <= 6; c++)
        {
            var gtsC = allGt.FindAll(g => g.Type == c);
            var predsC = allPred.FindAll(p => p.Type == c);
            var stat = new ClassAp
            {
                Type = c, Name = ClassNames[c - 1],
                GtCount = gtsC.Count, PredCount = predsC.Count
            };

            if (gtsC.Count > 0)
            {
                var used = new bool[allGt.Count];
                var ordered = OrderByConfDesc(predsC);
                var (rec, prec, tp) = Accumulate(ordered, allGt, used,
                    p => gtByPairType.TryGetValue((p.Pair, p.Type), out var l) ? l : null,
                    gtsC.Count, IoOK);
                stat.TpStrict = tp;
                stat.ApStrict = ApAllPoint(rec, prec);
                stat.ApStrict11 = ApEleven(rec, prec);
                apSum += stat.ApStrict; apSum11 += stat.ApStrict11; apN++;
                strictTpTotal += tp;
            }
            classes.Add(stat);
        }

        report.Classes = classes;
        report.StrictTp = strictTpTotal;
        report.MapStrict = apN > 0 ? apSum / apN : 0;
        report.MapStrict11 = apN > 0 ? apSum11 / apN : 0;
        var (sp, sr, sf) = Prf(strictTpTotal, allPred.Count, allGt.Count);
        report.StrictPrecision = sp; report.StrictRecall = sr; report.StrictF1 = sf;

        // ---------- 忽略分型：单通道全局 AP + P/R/F ----------
        var gtByPair = new Dictionary<int, List<int>>();
        for (var i = 0; i < allGt.Count; i++)
        {
            if (!gtByPair.TryGetValue(allGt[i].Pair, out var lst)) gtByPair[allGt[i].Pair] = lst = new List<int>();
            lst.Add(i);
        }
        var usedIg = new bool[allGt.Count];
        var orderedIg = OrderByConfDesc(allPred);
        var isTpIg = new bool[orderedIg.Count];
        var (recI, precI, tpI) = Accumulate(orderedIg, allGt, usedIg,
            p => gtByPair.TryGetValue(p.Pair, out var l) ? l : null,
            allGt.Count, IoOK, isTpIg);
        report.IgnoreTp = tpI;

        report.Industrial = ComputeIndustrialKpi(allGt, orderedIg, isTpIg, usedIg, pairs.Count, detected);
        report.HitsIouOnly = tpI;
        report.HitsTypeAndIou = strictTpTotal;
        // 诊断：忽略分型、中心落框或 IoU≥阈值 的贪心命中数（=旧 F1 口径）
        var (recC, precC, tpC) = Accumulate(OrderByConfDesc(allPred), allGt, new bool[allGt.Count],
            p => gtByPair.TryGetValue(p.Pair, out var l) ? l : null,
            allGt.Count, (p, g) => CenterIn(p, g) || IoOK(p, g));
        report.HitsCenterOrIou = tpC;
        report.MapIgnore = ApAllPoint(recI, precI);
        report.MapIgnore11 = ApEleven(recI, precI);
        var (ip, ir, iff) = Prf(tpI, allPred.Count, allGt.Count);
        report.IgnorePrecision = ip; report.IgnoreRecall = ir; report.IgnoreF1 = iff;

        report.Confusion = BuildConfusion(allGt, allPred);
        report.Geometry = Geometry(allGt, allPred);
        report.GrowSweep = GrowSweep(allGt, allPred,
            new[] { 0.6, 0.7, 0.8, 0.85, 0.9, 0.95, 1.0, 1.05, 1.1, 1.15, 1.25 });

        // ---------- 写报告 ----------
        var stamp = report.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine(reportsDir, $"sota_eval_{stamp}");
        Directory.CreateDirectory(outDir);
        DumpMissesAndFp(outDir, allGt, allPred, pairs.Select(p => p.stem).ToList());
        report.ReportMdPath = Path.Combine(outDir, "sota_eval.md");
        report.ReportJsonPath = Path.Combine(outDir, "sota_eval.json");
        File.WriteAllText(report.ReportMdPath, RenderMarkdown(report), new UTF8Encoding(false));
        File.WriteAllText(report.ReportJsonPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return report;
    }

    private static int Ty(int t) => t < 1 ? 1 : t > 6 ? 6 : t;

    /// <summary>
    /// 分型混淆矩阵（诊断用，不参与指标）：逐 GT 取同图内 IoU 最优且过阈的 pred，累计 [GT 类, Pred 类]。
    /// 行和 &lt; 该类 GT 数即「该类的漏检/无候选」；非对角元即「位置对、类型错」。
    /// </summary>
    private List<List<int>> BuildConfusion(List<Box> allGt, List<Box> allPred)
    {
        var m = new int[6, 6];
        var byPair = new Dictionary<int, List<Box>>();
        foreach (var p in allPred)
        {
            if (!byPair.TryGetValue(p.Pair, out var l)) byPair[p.Pair] = l = new List<Box>();
            l.Add(p);
        }
        foreach (var g in allGt)
        {
            if (!byPair.TryGetValue(g.Pair, out var cands)) continue;
            Box? best = null; var bestIou = -1.0;
            foreach (var p in cands)
            {
                var v = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                if (v > bestIou) { bestIou = v; best = p; }
            }
            if (best == null || bestIou < _iou) continue;
            m[Ty(g.Type) - 1, Ty(best.Type) - 1]++;
        }
        var rows = new List<List<int>>(6);
        for (var i = 0; i < 6; i++)
        {
            var r = new List<int>(6);
            for (var j = 0; j < 6; j++) r.Add(m[i, j]);
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>
    /// 逐 GT 找同图内重叠最优（中心落框或 IoU&gt;0）的 pred，统计 IoU 直方图与框尺寸关系。
    /// 用于回答「命中率高但 IoU 低」究竟是谁的框不对。
    /// </summary>
    private GeoStats Geometry(List<Box> allGt, List<Box> allPred)
    {
        var byPair = new Dictionary<int, List<Box>>();
        foreach (var p in allPred)
        {
            if (!byPair.TryGetValue(p.Pair, out var l)) byPair[p.Pair] = l = new List<Box>();
            l.Add(p);
        }

        var s = new GeoStats { GtTotal = allGt.Count };
        var ious = new List<double>();
        var gtAreas = new List<double>();
        var predAreas = new List<double>();
        var ratios = new List<double>();
        var per = new ClassGeo[6];
        var perGtA = new List<double>[6];
        var perPrA = new List<double>[6];
        var perIou = new List<double>[6];
        var perPass = new int[6];
        for (var t = 0; t < 6; t++)
        {
            per[t] = new ClassGeo { Type = t + 1, Name = ClassNames[t] };
            perGtA[t] = new List<double>(); perPrA[t] = new List<double>(); perIou[t] = new List<double>();
        }

        foreach (var g in allGt)
        {
            if (!byPair.TryGetValue(g.Pair, out var cands)) { s.IouBuckets[0]++; continue; }
            Box? best = null; var bestIou = -1.0;
            foreach (var p in cands)
            {
                var iou = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                if (iou <= 0 && !CenterIn(p, g)) continue;
                if (iou > bestIou) { bestIou = iou; best = p; }
            }
            if (best == null || bestIou < 0) { s.IouBuckets[0]++; continue; }

            s.GtWithOverlap++;
            if (bestIou >= _iou) s.GtIouPass++;
            var b = best!;
            ious.Add(bestIou);
            var ga = (double)g.W * g.H; var pa = (double)b.W * b.H;
            gtAreas.Add(ga); predAreas.Add(pa);
            ratios.Add(Math.Sqrt(pa / Math.Max(1.0, ga)));
            if (b.X1 >= g.X1 && b.Y1 >= g.Y1 && b.X2 <= g.X2 && b.Y2 <= g.Y2) s.ContainedPred++;
            else if (b.X1 <= g.X1 && b.Y1 <= g.Y1 && b.X2 >= g.X2 && b.Y2 >= g.Y2) s.CoveringPred++;

            var k = bestIou <= 0 ? 0 : bestIou < 0.1 ? 1 : bestIou < 0.2 ? 2 :
                bestIou < _iou ? 3 : bestIou < 0.5 ? 4 : 5;
            s.IouBuckets[k]++;

            var ci = g.Type - 1;
            if (ci >= 0 && ci < 6)
            {
                per[ci].GtN++;
                perGtA[ci].Add(ga); perPrA[ci].Add(pa); perIou[ci].Add(bestIou);
                if (bestIou >= _iou) perPass[ci]++;
            }
        }

        s.MedianBestIou = Median(ious);
        s.MedianGtArea = Median(gtAreas);
        s.MedianPredArea = Median(predAreas);
        s.MedianSizeRatio = Median(ratios);
        for (var t = 0; t < 6; t++)
        {
            per[t].MedianGtArea = Median(perGtA[t]);
            per[t].MedianPredArea = Median(perPrA[t]);
            per[t].MedianIoU = Median(perIou[t]);
            per[t].IoUPassRate = per[t].GtN > 0 ? 100.0 * perPass[t] / per[t].GtN : 0;
            s.PerClass.Add(per[t]);
        }
        return s;
    }

    /// <summary>
    /// 诊断转储（仅测量层，不改任何指标）：把「零重叠漏检 GT」与该图最近 pred、
    /// 「孤立 FP pred」与该图最近 GT 导出为 miss_dump.tsv / fp_dump.tsv，供漏检画像分析。
    /// </summary>
    private void DumpMissesAndFp(string outDir, List<Box> allGt, List<Box> allPred, List<string> stems)
    {
        static double Cx(Box b) => (b.X1 + b.X2) / 2.0;
        static double Cy(Box b) => (b.Y1 + b.Y2) / 2.0;
        string StemOf(int pair) => pair >= 0 && pair < stems.Count ? stems[pair] : $"#{pair}";
        string NameOf(int type) => type >= 1 && type <= 6 ? ClassNames[type - 1] : $"t{type}";

        var predByPair = new Dictionary<int, List<Box>>();
        foreach (var p in allPred)
        {
            if (!predByPair.TryGetValue(p.Pair, out var l)) predByPair[p.Pair] = l = new List<Box>();
            l.Add(p);
        }
        var gtByPair = new Dictionary<int, List<Box>>();
        foreach (var g in allGt)
        {
            if (!gtByPair.TryGetValue(g.Pair, out var l)) gtByPair[g.Pair] = l = new List<Box>();
            l.Add(g);
        }

        // ---- 漏检 GT：同图内无任何 IoU>0 或中心落框的 pred ----
        var miss = new List<string>();
        miss.Add("stem\ttype\tGX1\tGY1\tGX2\tGY2\tGArea\tGRatio\tnPredInImg\tnearestDist\tnearestType\tNX1\tNY1\tNX2\tNY2");
        foreach (var g in allGt)
        {
            predByPair.TryGetValue(g.Pair, out var cands); cands ??= new List<Box>();
            var hasOverlap = false;
            Box? near = null; var nearD = double.MaxValue;
            foreach (var p in cands)
            {
                var iou = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                if (iou > 0 || CenterIn(p, g)) hasOverlap = true;
                var d = Math.Sqrt((Cx(p) - Cx(g)) * (Cx(p) - Cx(g)) + (Cy(p) - Cy(g)) * (Cy(p) - Cy(g)));
                if (d < nearD) { nearD = d; near = p; }
            }
            if (hasOverlap) continue;
            var ar = (double)g.W / g.H;
            var nType = near == null ? "-" : NameOf(near.Type);
            var line = $"{StemOf(g.Pair)}\t{NameOf(g.Type)}\t{g.X1}\t{g.Y1}\t{g.X2}\t{g.Y2}\t{(double)g.W * g.H}\t"
                + $"{ar.ToString("F2", CultureInfo.InvariantCulture)}\t"
                + (near == null
                    ? $"{cands.Count}\t-1\t-\t-\t-\t-\t-"
                    : $"{cands.Count}\t{nearD.ToString("F1", CultureInfo.InvariantCulture)}\t{nType}\t{near.X1}\t{near.Y1}\t{near.X2}\t{near.Y2}");
            miss.Add(line);
        }
        File.WriteAllLines(Path.Combine(outDir, "miss_dump.tsv"), miss, new UTF8Encoding(false));

        // ---- 近失 GT：同图内最优重叠 pred 的 IoU 落在 [0.1, 约束) —— 几何差一点未达阀 ----
        var nearRows = new List<string>();
        nearRows.Add("stem\ttype\tbestIoU\tdcx\tdcy\tdist\tGArea\tGRatioHW\tGW\tGH\tPredW\tPredH\tPredRatioHW\tPredArea\tGcx\tGcy\tPcx\tPcy");
        foreach (var g in allGt)
        {
            predByPair.TryGetValue(g.Pair, out var cands); cands ??= new List<Box>();
            Box? bbest = null; var bIou = -1.0;
            foreach (var p in cands)
            {
                var iou = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                if (iou > bIou) { bIou = iou; bbest = p; }
            }
            if (bbest == null || bIou < 0.1 || bIou >= _iou) continue;
            var b = bbest!;
            var gR = (double)g.W / g.H; var pR = (double)b.W / b.H;
            var gcx = (g.X1 + g.X2) / 2.0; var gcy = (g.Y1 + g.Y2) / 2.0;
            var pcx = (b.X1 + b.X2) / 2.0; var pcy = (b.Y1 + b.Y2) / 2.0;
            var dcx = pcx - gcx; var dcy = pcy - gcy;
            var dist = Math.Sqrt(dcx * dcx + dcy * dcy);
            string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
            nearRows.Add($"{StemOf(g.Pair)}\t{NameOf(g.Type)}\t{bIou.ToString("F3", CultureInfo.InvariantCulture)}\t{F(dcx)}\t{F(dcy)}\t{F(dist)}\t{(double)g.W * g.H}\t"
                + $"{gR.ToString("F2", CultureInfo.InvariantCulture)}\t{g.W}\t{g.H}\t{b.W}\t{b.H}\t{pR.ToString("F2", CultureInfo.InvariantCulture)}\t{(double)b.W * b.H}\t{F(gcx)}\t{F(gcy)}\t{F(pcx)}\t{F(pcy)}");
        }
        File.WriteAllLines(Path.Combine(outDir, "iou_near_dump.tsv"), nearRows, new UTF8Encoding(false));

        // ---- 孤立 FP：同图内与任何 GT 的最大 IoU < 约束 ----
        var fp = new List<string>();
        fp.Add("stem\ttype\tPX1\tPY1\tPX2\tPY2\tPArea\tnGtInImg\tmaxIoU\tbestGtType");
        foreach (var p in allPred)
        {
            gtByPair.TryGetValue(p.Pair, out var gts); gts ??= new List<Box>();
            var maxIoU = -1.0; Box? best = null;
            foreach (var g in gts)
            {
                var iou = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                if (iou > maxIoU) { maxIoU = iou; best = g; }
            }
            if (maxIoU >= _iou) continue;
            var bType = best == null ? "-" : NameOf(best.Type);
            fp.Add($"{StemOf(p.Pair)}\t{NameOf(p.Type)}\t{p.X1}\t{p.Y1}\t{p.X2}\t{p.Y2}\t{(double)p.W * p.H}\t"
                + $"{gts.Count}\t{maxIoU.ToString("F3", CultureInfo.InvariantCulture)}\t{bType}");
        }
        File.WriteAllLines(Path.Combine(outDir, "fp_dump.tsv"), fp, new UTF8Encoding(false));
    }

    /// <summary>
    /// 几何外扩扫描：将每个 pred 绕中心各向同性放大 factor 倍，重算官方口径命中。
    /// 用于判定「pred 框是 GT 框的子集」这一结构性差距的量级，不改变实际检出。
    /// </summary>
    private List<GrowPoint> GrowSweep(List<Box> allGt, List<Box> allPred, double[] factors)
    {
        var gtByPair = new Dictionary<int, List<int>>();
        for (var i = 0; i < allGt.Count; i++)
        {
            if (!gtByPair.TryGetValue(allGt[i].Pair, out var l)) gtByPair[allGt[i].Pair] = l = new List<int>();
            l.Add(i);
        }
        var gtByPairType = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < allGt.Count; i++)
        {
            var g = allGt[i];
            if (!gtByPairType.TryGetValue((g.Pair, g.Type), out var l))
                gtByPairType[(g.Pair, g.Type)] = l = new List<int>();
            l.Add(i);
        }

        var outList = new List<GrowPoint>();
        foreach (var f in factors)
        {
            var grown = new List<Box>(allPred.Count);
            foreach (var p in allPred) grown.Add(Scale(p, f));

            var (recI, precI, tpI) = Accumulate(OrderByConfDesc(grown), allGt, new bool[allGt.Count],
                p => gtByPair.TryGetValue(p.Pair, out var l) ? l : null, allGt.Count, IoOK);
            var (_, _, fIg) = Prf(tpI, grown.Count, allGt.Count);

            var strictTp = 0;
            for (var c = 1; c <= 6; c++)
            {
                var cCount = allGt.FindAll(g => g.Type == c).Count;
                if (cCount == 0) continue;
                var predsC = grown.FindAll(p => p.Type == c);
                var (_, _, tp) = Accumulate(OrderByConfDesc(predsC), allGt, new bool[allGt.Count],
                    p => gtByPairType.TryGetValue((p.Pair, p.Type), out var l) ? l : null,
                    cCount, IoOK);
                strictTp += tp;
            }
            var (_, _, fSt) = Prf(strictTp, grown.Count, allGt.Count);

            outList.Add(new GrowPoint
            {
                Factor = f, IgnoreTp = tpI, IgnoreF1 = fIg,
                MapIgnore11 = ApEleven(recI, precI),
                StrictTp = strictTp, StrictF1 = fSt
            });
        }
        return outList;
    }

    /// <summary>绕中心各向同性缩放（min 1px）；返回新对象，不改动原 pred。</summary>
    private static Box Scale(Box p, double f)
    {
        if (Math.Abs(f - 1.0) < 1e-9) return p;
        var dcx = (p.X1 + p.X2) / 2.0;
        var dcy = (p.Y1 + p.Y2) / 2.0;
        var nw = Math.Max(1, (int)Math.Round(p.W * f));
        var nh = Math.Max(1, (int)Math.Round(p.H * f));
        var x1 = (int)Math.Round(dcx - nw / 2.0);
        var y1 = (int)Math.Round(dcy - nh / 2.0);
        return new Box
        {
            Pair = p.Pair, Type = p.Type, Conf = p.Conf,
            X1 = x1, Y1 = y1, X2 = x1 + nw - 1, Y2 = y1 + nh - 1
        };
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var a = xs.ToArray(); Array.Sort(a);
        var n = a.Length;
        return n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
    }

    // 置信度降序（同置信度按 pair 稳定）。
    private static List<Box> OrderByConfDesc(List<Box> preds)
    {
        var arr = preds.ToArray();
        Array.Sort(arr, (a, b) =>
        {
            var c = b.Conf.CompareTo(a.Conf);
            return c != 0 ? c : a.Pair.CompareTo(b.Pair);
        });
        return arr.ToList();
    }

    private bool IoOK(Box p, Box g)
        => IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2) >= _iou;

    private static bool CenterIn(Box p, Box g)
    {
        var cx = p.X1 + p.W / 2.0;
        var cy = p.Y1 + p.H / 2.0;
        return cx >= g.X1 && cx <= g.X2 && cy >= g.Y1 && cy <= g.Y2;
    }

    /// <summary>
    /// 按 conf 降序逐个贪心匹配（同图内、候选 GT 集合由 gtIdxOf 提供），返回累积 recall/precision 曲线与总 TP。
    /// 每个 GT 只能被占用一次（used 标记）。
    /// </summary>
    /// <summary>
    /// 二项比例的 Wilson 95% 置信上界。用于给漏放率/假点率配一个可信上界——
    /// 零事件时的点估计是 0，但那只是「样本量不足」，不是「真的没有」。
    /// </summary>
    private static double WilsonUpper(int events, int n, double z = 1.96)
    {
        if (n <= 0) return 1.0;
        var p = (double)events / n;
        var den = 1.0 + z * z / n;
        var centre = p + z * z / (2.0 * n);
        var marg = z * Math.Sqrt(p * (1.0 - p) / n + z * z / (4.0 * n * n));
        var u = (centre + marg) / den;
        return u > 1.0 ? 1.0 : u;
    }

    /// <summary>
    /// 工业 KPI：按板聚合假点数（FCR）与漏放率（Escape Rate）。
    /// </summary>
    /// <remarks>
    /// 学术指标（F1/mAP）是<b>框级</b>的，会掩盖「假点集中在少数板上」这类产线问题；
    /// 工业指标必须回到<b>板级</b>：复判人力按板算，漏放事故按板算。
    /// <para>
    /// FAULT 板（引擎没跑完：缺证/缺模板/内部异常）不计入任何分母——它不是漏检，
    /// 产线语义是重拍复检。受检板集合由 <paramref name="inspected"/> 给出。
    /// </para>
    /// </remarks>
    private static IndustrialKpi ComputeIndustrialKpi(
        List<Box> allGt, List<Box> orderedPreds, bool[] isTp, bool[] gtUsed, int boardCount,
        bool[]? inspected = null)
    {
        // pairIdx → 受检板序号；未受检板 = -1，不进任何统计
        var slot = new int[boardCount];
        var inspectedCount = 0;
        for (var b = 0; b < boardCount; b++)
            slot[b] = inspected == null || inspected[b] ? inspectedCount++ : -1;

        var fpPerBoard = new int[inspectedCount];
        var gtPerBoard = new int[inspectedCount];
        var gtHitPerBoard = new int[inspectedCount];

        for (var k = 0; k < orderedPreds.Count; k++)
        {
            if (isTp[k]) continue;
            var s = slot[orderedPreds[k].Pair];
            if (s >= 0) fpPerBoard[s]++;
        }

        for (var i = 0; i < allGt.Count; i++)
        {
            var s = slot[allGt[i].Pair];
            if (s < 0) continue; // FAULT 板的 GT 已不入库，双保险
            gtPerBoard[s]++;
            if (gtUsed[i]) gtHitPerBoard[s]++;
        }

        var totalFp = 0;
        var boardsWithFp = 0;
        var maxFp = 0;
        foreach (var f in fpPerBoard)
        {
            totalFp += f;
            if (f > 0) boardsWithFp++;
            if (f > maxFp) maxFp = f;
        }

        var defective = 0;
        var escaped = 0;
        for (var b = 0; b < inspectedCount; b++)
        {
            if (gtPerBoard[b] <= 0) continue;
            defective++;
            if (gtHitPerBoard[b] == 0) escaped++;
        }

        var sortedFp = (int[])fpPerBoard.Clone();
        Array.Sort(sortedFp);
        var p95Idx = Math.Min(sortedFp.Length - 1,
            (int)Math.Ceiling(0.95 * sortedFp.Length) - 1);
        if (p95Idx < 0) p95Idx = 0;

        var gtTotal = allGt.Count;
        var gtMissed = 0;
        for (var i = 0; i < allGt.Count; i++) if (!gtUsed[i]) gtMissed++;

        return new IndustrialKpi
        {
            BoardCount = inspectedCount,
            FalseCallCount = totalFp,
            FalseCallPerBoard = inspectedCount > 0 ? (double)totalFp / inspectedCount : 0,
            FalseCallPerBoardP95 = inspectedCount > 0 ? sortedFp[p95Idx] : 0,
            FalseCallPerBoardMax = maxFp,
            BoardsWithFalseCall = boardsWithFp,
            BoardFalseCallRate = inspectedCount > 0 ? (double)boardsWithFp / inspectedCount : 0,
            DefectiveBoards = defective,
            EscapedBoards = escaped,
            EscapeRateBoard = defective > 0 ? (double)escaped / defective : 0,
            EscapeRateBoardUpper95 = WilsonUpper(escaped, defective),
            EscapeRateBox = gtTotal > 0 ? (double)gtMissed / gtTotal : 0,
            EscapeRateBoxUpper95 = WilsonUpper(gtMissed, gtTotal)
        };
    }

    /// <param name="isTp">可选：输出每个 pred 是否为 TP（按板统计 FCR 用）。</param>
    private static (double[] rec, double[] prec, int tp) Accumulate(
        List<Box> orderedPreds, List<Box> allGt, bool[] used,
        Func<Box, List<int>?> gtIdxOf, int nGt, Func<Box, Box, bool> ok,
        bool[]? isTp = null)
    {
        var rec = new double[orderedPreds.Count];
        var prec = new double[orderedPreds.Count];
        var tp = 0; var fp = 0;
        for (var k = 0; k < orderedPreds.Count; k++)
        {
            var p = orderedPreds[k];
            var lst = gtIdxOf(p);
            var bestIou = -1.0; var bestIdx = -1;
            if (lst != null)
            {
                foreach (var gi in lst)
                {
                    if (used[gi]) continue;
                    var g = allGt[gi];
                    if (!ok(p, g)) continue;
                    var iou = IoU(p.X1, p.Y1, p.X2, p.Y2, g.X1, g.Y1, g.X2, g.Y2);
                    if (iou > bestIou) { bestIou = iou; bestIdx = gi; }
                }
            }
            var hit = bestIdx >= 0;
            if (hit) { used[bestIdx] = true; tp++; }
            else fp++;
            // 逐框 TP 标记（与 orderedPreds 同序），供按板统计 FCR 用
            if (isTp != null) isTp[k] = hit;
            var total = tp + fp;
            rec[k] = nGt > 0 ? (double)tp / nGt : 0;
            prec[k] = total > 0 ? (double)tp / total : 0;
        }
        return (rec, prec, tp);
    }

    /// <summary>全点单调包络积分（VOC2010+ / COCO 风格 AP）。</summary>
    private static double ApAllPoint(double[] rec, double[] prec)
    {
        var n = rec.Length;
        if (n == 0) return 0;
        var env = new double[n];
        var best = 0.0;
        for (var i = n - 1; i >= 0; i--)
        {
            if (prec[i] > best) best = prec[i];
            env[i] = best;
        }
        double ap = 0; var prevRec = 0.0;
        for (var i = 0; i < n; i++)
        {
            ap += (rec[i] - prevRec) * env[i];
            prevRec = rec[i];
        }
        return ap;
    }

    /// <summary>11 点插值 AP（ICDAR/VOC2007 风格）。</summary>
    private static double ApEleven(double[] rec, double[] prec)
    {
        var n = rec.Length;
        if (n == 0) return 0;
        double ap = 0;
        for (var j = 0; j <= 10; j++)
        {
            var t = j / 10.0;
            var p = 0.0;
            for (var i = 0; i < n; i++)
                if (rec[i] >= t && prec[i] > p) p = prec[i];
            ap += p;
        }
        return ap / 11.0;
    }

    private static (double p, double r, double f) Prf(int tp, int nPred, int nGt)
    {
        var fp = nPred - tp;
        var fn = nGt - tp;
        var p = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0;
        var r = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0;
        var f = (p + r) > 0 ? 2 * p * r / (p + r) : 0;
        return (p, r, f);
    }

    /// <summary>
    /// 排序置信度：框内平均灰度差 × √框面积。只影响 AP 曲线的排序，不改检出与框几何；
    /// 面积单作置信度会把「弱但大」的框排在「强且小」的真缺陷之前。
    /// </summary>
    /// <summary>工业 KPI 小节：FCR 与 Escape Rate。口径是<b>板级</b>，与框级 F1/mAP 并列展示。</summary>
    private static void AppendIndustrialKpi(StringBuilder sb, IndustrialKpi k)
    {
        sb.AppendLine("### 工业 KPI（板级口径：产线看这两个数，不是看 F1）");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 | 含义 |");
        sb.AppendLine("|------|-----|------|");
        sb.AppendLine($"| 板数（受检） | {k.BoardCount} | 参与统计的板；FAULT 板未检测，不进分母 |");
        sb.AppendLine($"| **FCR 每板假点数** | **{k.FalseCallPerBoard:F2}** | 复判工位人均工作量，越低越好 |");
        sb.AppendLine($"| 每板假点 P95 | {k.FalseCallPerBoardP95:F0} | 最坏 5% 板的假点数 |");
        sb.AppendLine($"| 单板最多假点 | {k.FalseCallPerBoardMax} | — |");
        sb.AppendLine($"| 板级假点率 | {k.BoardFalseCallRate:P1} | 含假点的板占比 |");
        sb.AppendLine($"| **板级漏放率 Escape** | **{k.EscapeRateBoard:P2}** | 有缺陷却零命中的板 / 含缺陷板（客户投诉口径） |");
        sb.AppendLine($"| 漏放板数 | {k.EscapedBoards} / {k.DefectiveBoards} | 漏放板 / 含缺陷板 |");
        sb.AppendLine($"| 板级漏放 **95% 上界** | **{k.EscapeRateBoardUpper95:P2}** | ⚠ 零事件时点估计为 0，但上界才是对外可承诺的数 |");
        sb.AppendLine($"| 缺陷级漏放率 | {k.EscapeRateBox:P2}（{k.EscapeRateBox * 1e6:F0} ppm） | 漏检 GT / 总 GT（=1−召回） |");
        sb.AppendLine($"| 缺陷级漏放 95% 上界 | {k.EscapeRateBoxUpper95:P2} | 同上 |");
        sb.AppendLine();
        sb.AppendLine("> FCR 与 Escape 按板统计：复判人力按板计、漏放事故按板计，");
        sb.AppendLine("> 框级 F1 会掩盖「假点集中在少数板」这类产线问题，故工业口径必须回到板级。");
        sb.AppendLine();
        sb.AppendLine("> ⚠ **零事件不等于零风险**：若要在 95% 置信下声称板级漏放率低于某目标值，");
        sb.AppendLine("> 按 Rule of Three 需要约 3/目标 块含缺陷板且零漏放——");
        foreach (var tgt in new[] { 0.001, 0.0001, 0.00001 })
            sb.AppendLine($"> 目标 {tgt * 1e6:F0} ppm 需 **{k.BoardsNeededFor(tgt):N0}** 板（当前仅 {k.BoardCount} 板）。");
    }

    private static double BoxContrast(byte[] test, byte[] temp, int stride, int x1, int y1, int x2, int y2)
    {
        if (test == null || temp == null || x2 < x1 || y2 < y1) return 0;
        double sum = 0;
        var n = 0;
        for (var y = y1; y <= y2; y++)
        {
            var row = y * stride;
            for (var x = x1; x <= x2; x++)
            {
                var d = test[row + x] - temp[row + x];
                sum += d < 0 ? -d : d;
                n++;
            }
        }
        return n > 0 ? sum / n * Math.Sqrt(n) : 0;
    }

    private static double IoU(int ax1, int ay1, int ax2, int ay2,
        int bx1, int by1, int bx2, int by2)
    {
        var ix1 = Math.Max(ax1, bx1);
        var iy1 = Math.Max(ay1, by1);
        var ix2 = Math.Min(ax2, bx2);
        var iy2 = Math.Min(ay2, by2);
        var iw = Math.Max(0, ix2 - ix1 + 1);
        var ih = Math.Max(0, iy2 - iy1 + 1);
        var inter = (double)iw * ih;
        if (inter <= 0) return 0;
        var a = Math.Max(1, ax2 - ax1 + 1) * Math.Max(1, ay2 - ay1 + 1);
        var b = Math.Max(1, bx2 - bx1 + 1) * Math.Max(1, by2 - by1 + 1);
        return inter / (a + b - inter);
    }

    private static int TypeCodeToId(string? typeCode)
    {
        if (string.IsNullOrEmpty(typeCode)) return 0;
        return typeCode!.Trim().ToUpperInvariant() switch   // net48 的 IsNullOrEmpty 无流分析注解
        {
            "OPEN" => 1,
            "SHORT" => 2,
            "MOUSEBITE" => 3,
            "SPUR" => 4,
            "COPPER" => 5,
            "PINHOLE" => 6,
            _ => 0
        };
    }

    private static void AppendGeometry(StringBuilder sb, GeoStats g)
    {
        sb.AppendLine("### 框几何诊断（以 GT 为主体，取同图重叠最优 pred）");
        sb.AppendLine();
        sb.AppendLine($"- GT 总数 {g.GtTotal} · 有重叠候选 {g.GtWithOverlap} · 其中 IoU≥{IouConstraint:F2} 仅 {g.GtIouPass}");
        sb.AppendLine($"- 最优 IoU 中位数 {g.MedianBestIou:F3} · GT 中位面积 {g.MedianGtArea:F0}px² · Pred 中位面积 {g.MedianPredArea:F0}px²");
        sb.AppendLine($"- Pred/GT 边长比中位数 {g.MedianSizeRatio:F2}（>1=pred 偏大，<1=pred 偏小）");
        sb.AppendLine($"- pred 完全落在 GT 内 {g.ContainedPred} · pred 完全包住 GT {g.CoveringPred}");
        sb.AppendLine($"- IoU 直方图：≤0 {g.IouBuckets[0]} · 0-0.1 {g.IouBuckets[1]} · 0.1-0.2 {g.IouBuckets[2]} · 0.2-{IouConstraint:F2} {g.IouBuckets[3]} · 0.33-0.5 {g.IouBuckets[4]} · ≥0.5 {g.IouBuckets[5]}");
        if (g.PerClass.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| 类型 | GT 数 | GT 中位面积 | 最优重叠 pred 中位面积 | 中位 IoU | IoU≥约束占比 |");
            sb.AppendLine("|------|-------|-------------|--------------------------|----------|---------------|");
            foreach (var c in g.PerClass)
                sb.AppendLine($"| {c.Name} | {c.GtN} | {c.MedianGtArea:F0}px² | {c.MedianPredArea:F0}px² | {c.MedianIoU:F3} | {c.IoUPassRate:F1}% |");
        }
        sb.AppendLine();
    }

    public static string RenderMarkdown(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DeepPCB 官方口径（ICDAR 2015 修改版）评测");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{r.Timestamp:yyyy-MM-dd HH:mm:ss} · 对数：{r.PairCount}（受检 {r.InspectedPairs}） · 引擎：{r.Engine} · 配方 {r.RecipeId} v{r.RecipeVersion} hash=`{r.RecipeHash}`");
        sb.AppendLine($"- 口径：IoU≥{IouConstraint:F2}、同图、一对一贪心；置信度={(string.Equals(_lastConfMode, "contrast", StringComparison.OrdinalIgnoreCase) ? "框内对比度" : string.Equals(_lastConfMode, "score", StringComparison.OrdinalIgnoreCase) ? "标定分数（v1.2.0 logistic 17 特征，未标定则回落面积）" : "候选面积")}（仅排序）。GT {r.TotalGt}，Pred {r.TotalPred}。");
        sb.AppendLine();
        sb.AppendLine("## 汇总（本项目实测；论文对照口径见文末声明）");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 严格分型（可比） | 忽略分型（定位上界） |");
        sb.AppendLine("|------|------------------|----------------------|");
        sb.AppendLine($"| mAP (全点 AP) | {r.MapStrict:P1} | {r.MapIgnore:P1} |");
        sb.AppendLine($"| mAP (11 点) | {r.MapStrict11:P1} | {r.MapIgnore11:P1} |");
        sb.AppendLine($"| Precision | {r.StrictPrecision:P1} | {r.IgnorePrecision:P1} |");
        sb.AppendLine($"| Recall | {r.StrictRecall:P1} | {r.IgnoreRecall:P1} |");
        sb.AppendLine($"| F-score | **{r.StrictF1:P1}** | **{r.IgnoreF1:P1}** |");
        sb.AppendLine($"| TP | {r.StrictTp} | {r.IgnoreTp} |");
        sb.AppendLine();
        sb.AppendLine("### 命中口径诊断（忽略分型，同一贪心、仅谓词不同）");
        sb.AppendLine();
        sb.AppendLine($"- 中心落框 或 IoU≥{IouConstraint:F2}（旧 F1 口径）：{r.HitsCenterOrIou}");
        sb.AppendLine($"- 仅 IoU≥{IouConstraint:F2}（官方位置口径）：{r.HitsIouOnly}");
        sb.AppendLine($"- 类型一致 且 仅 IoU≥{IouConstraint:F2}（严格分型）：{r.HitsTypeAndIou}");
        sb.AppendLine();
        AppendIndustrialKpi(sb, r.Industrial);
        sb.AppendLine();
        AppendGeometry(sb, r.Geometry);
        sb.AppendLine();
        if (r.GrowSweep.Count > 0)
        {
            sb.AppendLine("### 几何外扩扫描（模拟：pred 绕中心放大 f 倍后重算，不改实际检出）");
            sb.AppendLine();
            sb.AppendLine("| f | 忽略分型 TP | 忽略分型 F | mAP(11) | 严格分型 TP | 严格分型 F |");
            sb.AppendLine("|---|-------------|-----------|---------|-------------|-----------|");
            foreach (var g in r.GrowSweep)
                sb.AppendLine($"| {g.Factor:F2} | {g.IgnoreTp} | {g.IgnoreF1:P1} | {g.MapIgnore11:P1} | {g.StrictTp} | {g.StrictF1:P1} |");
            sb.AppendLine();
        }
        if (r.Confusion.Count == 6)
        {
            sb.AppendLine("### 混淆矩阵（GT 行 × Pred 列，诊断：逐 GT 取最优 IoU，不强制一对一）");
            sb.AppendLine();
            sb.AppendLine("| GT\\Pred | open | short | mousebite | spur | copper | pinhole | 行和 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            for (var i = 0; i < 6; i++)
            {
                var row = r.Confusion[i];
                var sum = row.Sum();
                sb.AppendLine($"| **{ClassNames[i]}** | {row[0]} | {row[1]} | {row[2]} | {row[3]} | {row[4]} | {row[5]} | {sum} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 分型 AP（严格：pred 类型须等于 GT 类型）");
        sb.AppendLine();
        sb.AppendLine("| 类 | GT | Pred | TP | AP(全点) | AP(11点) |");
        sb.AppendLine("|----|----|------|----|----------|----------|");
        foreach (var c in r.Classes)
            sb.AppendLine($"| {c.Name} | {c.GtCount} | {c.PredCount} | {c.TpStrict} | {c.ApStrict:P1} | {c.ApStrict11:P1} |");
        sb.AppendLine();
        sb.AppendLine("## 口径与对照声明");
        sb.AppendLine();
        sb.AppendLine("- 分型由 PcbTypeTree（v1.2.1，六类全输出，判据按线宽归一化 + 实测铜连通/切断拓扑，train1000 拟合、5 折 CV）打标后参与严格口径匹配；分型错误会把 TP 同时变成漏检与误检，因此严格分型 mAP 低于定位上界；忽略分型列给出定位上界。");
        sb.AppendLine("- **对标事实（2026-10-22 核实）**：DeepPCB 官方论文的最好成绩 mAP 98.6% / F 98.2% 来自其**深度神经网络**模型（VGG16-tiny + Group Pyramid Pooling；数据集 README 原文 \"our model based on deep neural network\"），**不是**传统模板差分法；传统 image processing 基线约 89.3%（第三方汇总）。本项目不上 DL，正确对标是「传统 vs DL 差 1.2pp」，不得表述为同范式对拼。");
        sb.AppendLine("- **诚信红线**：禁止把官方/论文的 98.6% 或 ≈89% 直接复制写成本项目成绩；对外声明必须附本项目 reports/ 实测落盘证据。");
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 告警");
            foreach (var w in r.Warnings) sb.AppendLine($"- {w}");
        }
        return sb.ToString();
    }
}
