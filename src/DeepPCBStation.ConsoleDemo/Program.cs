using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Alarms;
using DeepPCBStation.Core.Camera;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Diagnostics;
using DeepPCBStation.Core.Eval;
using DeepPCBStation.Core.Handshake;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Metrology;
using DeepPCBStation.Core.Pipeline;
using DeepPCBStation.Core.Recipe;
using DeepPCBStation.Core.Review;
using DeepPCBStation.Core.Storage;
using DeepPCBStation.Core.Storage.Db;

// ConsoleDemo：单检 / --handshake-selftest / --recipe-gate-selftest / --concurrency-selftest / --golden-eval / --fp-audit / --miss-audit / --measure（均可 --samples=目录）

var engineOverride = Arg(args, "--engine=");
var modeHandshake = Has(args, "--handshake-selftest", "--handshake");
var modeRecipeGate = Has(args, "--recipe-gate-selftest", "--recipe-gate");
var modeGolden = Has(args, "--golden-eval", "--golden");
var modeSota = Has(args, "--sota-eval", "--sota");
var modeFpAudit = Has(args, "--fp-audit");
var modeMeasure = Has(args, "--measure", "--trace-measure");
var modeMissAudit = Has(args, "--miss-audit");
var modeTexSep = Has(args, "--tex-sep", "--texture-sep");
var modeEdge = Has(args, "--edge-profile");
var modeCaptureDiag = Has(args, "--capture-diag", "--cap-diag");
var modeExportCand = Has(args, "--export-candidates", "--export-cand");
var modeFeatureDump = Has(args, "--feature-dump");
var modeDb = Has(args, "--db-selftest", "--db");
var modeConcurrency = Has(args, "--concurrency-selftest", "--concurrency");
var port = int.TryParse(Arg(args, "--port="), out var p) ? p : 19100;
var timeoutMs = int.TryParse(Arg(args, "--timeout-ms="), out var t) ? t : 2000;
var injectDelay = int.TryParse(Arg(args, "--inject-delay-ms="), out var delayMs) ? delayMs : 0;
var reportsDir = Arg(args, "--reports=") ?? "reports";
var samplesOverride = Arg(args, "--samples=");
// 默认全量。旧行为「无 --samples 时静默截断前 100 对」是口径陷阱：
// --sota-eval 默认 external（480 对）、export-cand/feature-dump 默认 full（1500 对），
// 都会只跑一个文件系统序的任意子集，而报告不注明子集规模 → 对外数字不可复现。
// 显式 --max-pairs=N 仍可限速，但打印醒目警告，保证子集规模进入运行记录。
var maxPairs = int.TryParse(Arg(args, "--max-pairs="), out var mp) ? Math.Max(1, mp) : int.MaxValue;
if (maxPairs != int.MaxValue)
    Console.WriteLine($"[警告] --max-pairs={maxPairs}：仅处理前 {maxPairs} 对；本次运行的所有统计口径为该子集，不是全集。");

// Stub（假检测器）只在**显式**场合允许：命令行 --engine=Stub、三个内置自检模式、
// 或环境变量 DPCB_ALLOW_STUB_ENGINE=1。其余路径一律拒绝——假引擎的产出与图像无关，
// 用它跑出来的评测数字是编的，而 UI 上看不出任何异常。
var allowStub = string.Equals(engineOverride, "Stub", StringComparison.OrdinalIgnoreCase)
    || modeHandshake || modeRecipeGate || modeConcurrency
    || Environment.GetEnvironmentVariable(DetectorFactory.AllowStubEnv) == "1";
IDefectDetector CreateEngine(string? eng, out string warn) => DetectorFactory.Create(eng, out warn, allowStub);

// 统一日志：库里的告警/错误走 stderr。
// 为什么必须走 stderr：stdout 被评测报告（JSON/CSV）占用，日志混进去会让报告不可解析。
StationLog.SetSink(new StationLog.DelegateSink(
    (lv, src, msg) => { if (lv >= LogLevel.Warn) Console.Error.WriteLine($"[{lv}] {src}: {msg}"); }));

var cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var cfg = AppConfig.Load(cfgPath);
cfg.Storage.OutputRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "results"));
ResolvePlayback(cfg);

var recipesDir = FindPath("recipes");
var recipeStore = RecipeStore.LoadFromDirectory(recipesDir, cfg.Recipes);
foreach (var diag in recipeStore.Diagnostics)
    Console.Error.WriteLine("[recipe] " + diag);
if (recipeStore.List().Count == 0)
    recipeStore = new RecipeStore(cfg.Recipes);
ApplySets(args, recipeStore.Current);

Console.WriteLine("=== DeepPCB-Station ConsoleDemo ===");
Console.WriteLine($"回放目录: {Path.GetFullPath(cfg.Camera.PlaybackRootDir)}");
Console.WriteLine($"输出目录: {cfg.Storage.OutputRoot}");
Console.WriteLine($"配方: {recipeStore.Current.Id} v{recipeStore.Current.Version} hash={recipeStore.Current.ContentHash}");
Console.WriteLine($"换型确认: {recipeStore.IsChangeoverConfirmed}");

if (modeEdge)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    if (CreateEngine(engineOverride ?? "Halcon", out var warnE) is not HalconPcbDefectDetector)
    {
        Console.WriteLine($"[EdgeProfile] 需要 Halcon 引擎。{warnE}");
        return 1;
    }
    var detE = CreateEngine(engineOverride ?? "Halcon", out _);
    var rad = int.TryParse(Arg(args, "--rad="), out var rr) ? rr : 2;
    Console.WriteLine($"[EdgeProfile] samples={samples} maxPairs={maxPairs} rad={rad}");
    try
    {
        new PcbEdgeProfiler().Run(samples, detE, recipeStore.Current, maxPairs, rad);
    }
    catch (Exception ex)
    {
        Console.WriteLine("EXTYPE " + ex.GetType().FullName);
        Console.WriteLine("EXMSG " + ex.Message);
        Console.WriteLine("EXSTACK " + ex.StackTrace);
        return 1;
    }
    return 0;
}

if (modeDb)
{
    return DbSelftest();
}

if (modeMissAudit)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    if (CreateEngine(engineOverride ?? "Halcon", out var warnM) is not HalconPcbDefectDetector halcon)
    {
        Console.WriteLine($"[MissAudit] 需要 Halcon 引擎。{warnM}");
        return 1;
    }
    Console.WriteLine($"[MissAudit] samples={samples} maxPairs={maxPairs}");
    var miss = new PcbMissAuditor().Run(samples, halcon, recipeStore.Current, Path.GetFullPath(reportsDir), maxPairs: maxPairs);
    Console.WriteLine(PcbMissAuditor.RenderMarkdown(miss));
    Console.WriteLine($"输出: {miss.OutputDir}");
    return 0;
}

if (modeCaptureDiag)
{
    // 采图有效性自检的标定诊断：量测正常板上各指标的分布，判断闸门会不会误杀。
    // 纪律：未量测不得上闸（lds4 教训——只看似合理就放行会在留出集翻车）。
    // 默认用 data/samples：official_test 是最终验收集，禁止默认在它上面做标定量测；
    // 历次正式量测均显式 --samples=data/official_test（500 对，见 docs/INDUSTRIAL-*.md）。
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    var gate = cfg.CaptureQuality.ToGate();
    gate.Enabled = true;    // 强制评估，便于统计「当前阈值会触发多少」
    Console.WriteLine($"[CaptureDiag] samples={samples} maxPairs={maxPairs}");
    Console.WriteLine($"[CaptureDiag] 阈值: blurRatio>={gate.BlurRatioMin} satHi>{gate.SatHiMax} satLo>{gate.SatLoMax} std<{gate.StdMin} regResidual>{gate.RegisterResidualMax}");

    var sharpR = new List<double>(); var sharp = new List<double>();
    var satHi = new List<double>(); var satLo = new List<double>();
    var stds = new List<double>(); var regs = new List<double>(); var ms = new List<double>();
    var contrast = new List<double>(); var meanShift = new List<double>();
    var triggers = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var n = 0;

    foreach (var (stem, testPath, tempPath, _) in PcbGoldenEvaluator.DiscoverPairs(samples).Take(maxPairs))
    {
        using var test = ImageIo.LoadGray(testPath);
        using var temp = ImageIo.LoadGray(tempPath);
        var rep = CaptureQuality.Assess(test.Pixels, temp.Pixels, test.Width, test.Height, gate, stride: test.Stride);
        sharpR.Add(rep.SharpnessRatio); sharp.Add(rep.Sharpness);
        satHi.Add(rep.SatHi); satLo.Add(rep.SatLo); stds.Add(rep.Std);
        regs.Add(rep.RegisterResidual); ms.Add(rep.ElapsedMs);
        contrast.Add(rep.ContrastRatio); meanShift.Add(rep.MeanShift);
        n++;
        if (rep.Code != null)
            triggers[rep.Code] = triggers.TryGetValue(rep.Code, out var c) ? c + 1 : 1;
    }

    static string Dist(List<double> v, bool pct = false)
    {
        if (v.Count == 0) return "-";
        var s = (List<double>)[.. v];            // 复制后排序
        s.Sort();
        double Q(double q) => s[Math.Min(s.Count - 1, Math.Max(0, (int)Math.Round(q * (s.Count - 1))))];
        var f = pct ? "P2" : "F3";
        return $"min={Q(0).ToString(f)} p05={Q(0.05).ToString(f)} p50={Q(0.5).ToString(f)} p95={Q(0.95).ToString(f)} max={Q(1).ToString(f)}";
    }

    Console.WriteLine();
    Console.WriteLine("| 指标 | 分布 |");
    Console.WriteLine("|------|------|");
    Console.WriteLine($"| 锐度比 sharpRatio | {Dist(sharpR)} |");
    Console.WriteLine($"| 锐度 sharpness | {Dist(sharp)} |");
    Console.WriteLine($"| 高饱和 satHi | {Dist(satHi, true)} |");
    Console.WriteLine($"| 近黑 satLo | {Dist(satLo, true)} |");
    Console.WriteLine($"| 灰度标准差 std | {Dist(stds)} |");
    Console.WriteLine($"| 对比度比 contrastRatio | {Dist(contrast)} |");
    Console.WriteLine($"| 均值偏移 meanShift | {Dist(meanShift)} |");
    Console.WriteLine($"| 配准残差 regResidual | {Dist(regs)} |");
    Console.WriteLine($"| 自检耗时 ms | {Dist(ms)} |");
    Console.WriteLine();
    Console.WriteLine($"样本对 {n}；当前阈值下会触发：{(triggers.Count == 0 ? "无（闸门不会误杀正常板）" : string.Join(" / ", triggers.Select(kv => $"{kv.Key}={kv.Value}")))}");

    // ---------- 正向对照：闸门必须抓得到真异常，否则「零误杀」毫无意义 ----------
    // 只证明「不误杀」是不够的——一个恒不触发的闸门同样不误杀。
    // 这里合成四类典型采图/上料异常，验证每类都能被对应原因码捕获。
    Console.WriteLine();
    Console.WriteLine("### 正向对照（合成异常，验证闸门确实会触发）");
    Console.WriteLine();
    Console.WriteLine("| 合成异常 | 期望捕获 | 实测 | 结论 |");
    Console.WriteLine("|----------|----------|------|------|");

    var probe = PcbGoldenEvaluator.DiscoverPairs(samples).FirstOrDefault();
    if (probe == default)
    {
        Console.WriteLine("| （无样本可用于正向对照） | | | |");
        return 0;
    }
    using (var pt = ImageIo.LoadGray(probe.Item2))
    using (var pm = ImageIo.LoadGray(probe.Item3))
    {
        var w = pt.Width; var h = pt.Height; var st = pt.Stride;
        var src = Compact(pt.Pixels, w, h, st);
        var tpl = Compact(pm.Pixels, w, h, pm.Stride);

        void Probe(string label, string expect, byte[] test, byte[]? temp, bool useTemplate)
        {
            var rep = CaptureQuality.Assess(test, useTemplate ? temp : null, w, h, gate, stride: w);
            var got = rep.Code ?? "VALID(未触发)";
            var pass = rep.Code == expect;
            Console.WriteLine($"| {label} | {expect} | {got} | {(pass ? "✅" : "❌")} |");
        }

        // 1) 失焦：5×5 均值模糊（锐度崩塌）
        Probe("失焦（5×5 均值模糊）", CaptureQuality.Codes.Blur, BoxBlur(src, w, h, 2), tpl, true);
        // 2) 过曝：向 255 压缩（暗区被抬亮、层次丢失）——这才是本模态下过曝的真实表现。
        //    单纯「+120 灰度」不会提高饱和占比（基材本就 255），已被标定证伪，不用作对照。
        var over = (byte[])src.Clone();
        for (var i = 0; i < over.Length; i++)
            over[i] = (byte)Math.Min(255, 255 - (255 - over[i]) * 0.25);
        Probe("过曝（向 255 压缩至 25% 层次）", CaptureQuality.Codes.OverExposure, over, tpl, true);
        // 2b) 欠曝：向 0 压缩
        var under = (byte[])src.Clone();
        for (var i = 0; i < under.Length; i++) under[i] = (byte)(under[i] * 0.35);
        Probe("欠曝（压缩至 35%）", CaptureQuality.Codes.UnderExposure, under, tpl, true);
        // 3) 空图/黑图
        Probe("黑图（全 0）", CaptureQuality.Codes.Flat, new byte[w * h], tpl, true);
        // 4) 上错料号：用另一块板的图当实测图（与模板完全对不上）
        var other = PcbGoldenEvaluator.DiscoverPairs(samples).Skip(1).FirstOrDefault();
        if (other != default)
        {
            using var ot = ImageIo.LoadGray(other.Item2);
            var otherPx = Compact(ot.Pixels, ot.Width, ot.Height, ot.Stride);
            if (otherPx.Length == w * h)
                Probe("上错料号（另一块板的图）", CaptureQuality.Codes.RegisterFail, otherPx, tpl, true);
        }
    }
    return 0;
}

// 均值模糊（失焦合成用）
static byte[] BoxBlur(byte[] src, int w, int h, int r)
{
    var dst = new byte[src.Length];
    for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int sum = 0, cnt = 0;
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    var yy = y + dy; var xx = x + dx;
                    if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                    sum += src[yy * w + xx]; cnt++;
                }
            dst[y * w + x] = (byte)(sum / cnt);
        }
    return dst;
}

static byte[] Compact(byte[] pixels, int width, int height, int stride)
{
    if (stride == width) return pixels;
    var outp = new byte[width * height];
    for (var y = 0; y < height; y++)
        Buffer.BlockCopy(pixels, y * stride, outp, y * width, width);
    return outp;
}

if (modeExportCand)
{
    // 阶段 0（旁路）：导出候选块训练数据，供后续「DL 候选复判」离线训练。
    // 不动任何生产判定、不改配方——只是把现有检测器跑出来的候选连同标签与证据图落盘。
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/full");
    var outDir = Path.GetFullPath(Arg(args, "--out=") ?? "data/candidates");
    var det = CreateEngine(engineOverride ?? "Halcon", out var warnE);
    if (!string.IsNullOrEmpty(warnE)) Console.WriteLine(warnE);
    Console.WriteLine($"[ExportCand] samples={samples} engine={det.EngineName} maxPairs={maxPairs}");
    Console.WriteLine($"[ExportCand] out={outDir}");

    var opt = new PcbCandidateExporter.Options
    {
        Margin = int.TryParse(Arg(args, "--margin="), out var mg) ? mg : 12,
        IouOk = double.TryParse(Arg(args, "--iou="), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var io) ? io : 0.33,
        IncludeRejected = !Has(args, "--no-rejected"),
        MaxRejectedPerPair = int.TryParse(Arg(args, "--max-rejected="), out var mr) ? mr : 60,
        DiffGain = int.TryParse(Arg(args, "--diff-gain="), out var dg) ? dg : 2,
        MaxPairs = maxPairs
    };
    var exp = new PcbCandidateExporter().Run(samples, det, recipeStore.Current, outDir, opt);
    Console.WriteLine(exp.RenderMarkdown());
    Console.WriteLine($"清单: {exp.ManifestPath}");
    return 0;
}

if (modeTexSep)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    if (CreateEngine(engineOverride ?? "Halcon", out var warnT) is not HalconPcbDefectDetector halconT)
    {
        Console.WriteLine($"[TexSep] 需要 Halcon 引擎。{warnT}");
        return 1;
    }
    Console.WriteLine($"[TexSep] samples={samples} maxPairs={maxPairs}");
    var sep = new PcbTextureSeparability().Run(samples, halconT, recipeStore.Current, Path.GetFullPath(reportsDir), maxPairs: maxPairs);
    Console.WriteLine(PcbTextureSeparability.RenderMarkdown(sep));
    Console.WriteLine($"输出: {sep.OutputDir}");
    return 0;
}

if (modeGolden)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    var engineG = engineOverride ?? "Halcon";
    var det = CreateEngine(engineG, out var warnG);
    if (!string.IsNullOrEmpty(warnG)) Console.WriteLine(warnG);
    Console.WriteLine($"[GoldenEval] samples={samples} engine={det.EngineName} maxPairs={maxPairs}");
    var report = new PcbGoldenEvaluator().Run(
        samples, det, recipeStore.Current,
        Path.GetFullPath(reportsDir),
        maxPairs: maxPairs);
    Console.WriteLine(PcbGoldenEvaluator.RenderMarkdown(report));
    Console.WriteLine($"报告: {report.ReportMdPath}");
    return report.GatePass ? 0 : 4;
}

if (modeSota)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/external");
    var det = CreateEngine(engineOverride ?? "Halcon", out var warnS);
    if (!string.IsNullOrEmpty(warnS)) Console.WriteLine(warnS);
    Console.WriteLine($"[SotaEval] samples={samples} engine={det.EngineName} maxPairs={maxPairs}");
    var iouS = double.TryParse(Arg(args, "--iou="), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var iv) ? iv : PcbIcdarEvaluator.IouConstraint;
    var sota = new PcbIcdarEvaluator { TypeOverride = LoadTypeOverride(Arg(args, "--type-override=")) }
        .Run(samples, det, recipeStore.Current, Path.GetFullPath(reportsDir), maxPairs: maxPairs, iou: iouS,
        confMode: Arg(args, "--conf="));   // 缺省交给评测器：标定置信度（v1.0.0），未标定回落面积
    Console.WriteLine(PcbIcdarEvaluator.RenderMarkdown(sota));
    Console.WriteLine($"输出: {sota.ReportMdPath}");
    if (DeepPCBStation.Core.Detection.StageTimer.Enabled)
        Console.WriteLine(DeepPCBStation.Core.Detection.StageTimer.Report(sota.PairCount));
    return 0;
}

if (modeFeatureDump)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/full");
    var det = CreateEngine(engineOverride ?? "Halcon", out var warnD);
    if (!string.IsNullOrEmpty(warnD)) Console.WriteLine(warnD);
    ISet<string>? exclude = null;
    var excludePath = Arg(args, "--exclude=");
    if (!string.IsNullOrEmpty(excludePath))
    {
        exclude = new HashSet<string>(
            File.ReadAllLines(Path.GetFullPath(excludePath))
                .Select(x => x.Trim()).Where(x => x.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }
    var dumpOut = Path.GetFullPath(Arg(args, "--out=") ?? Path.Combine(reportsDir, "features"));
    Console.WriteLine($"[FeatureDump] samples={samples} engine={det.EngineName} maxPairs={maxPairs} exclude={exclude?.Count ?? 0}");
    var fd = new PcbFeatureDumper().Run(samples, det, recipeStore.Current, dumpOut, maxPairs, exclude);
    Console.WriteLine($"pairs={fd.Pairs} candidates={fd.Candidates} matched={fd.Matched}");
    Console.WriteLine($"tsv: {fd.TsvPath}");
    return 0;
}

if (modeFpAudit)
{
    var samples = samplesOverride != null ? Path.GetFullPath(samplesOverride) : FindPath("data/samples");
    var det = CreateEngine(engineOverride ?? "Halcon", out var warnA);
    if (!string.IsNullOrEmpty(warnA)) Console.WriteLine(warnA);
    Console.WriteLine($"[FpAudit] samples={samples} engine={det.EngineName} maxPairs={maxPairs}");
    var audit = new PcbFpAuditor().Run(samples, det, recipeStore.Current, Path.GetFullPath(reportsDir), maxPairs: maxPairs);
    Console.WriteLine(PcbFpAuditor.RenderMarkdown(audit));
    Console.WriteLine($"输出: {audit.OutputDir}");
    return 0;
}

if (modeMeasure)
{
    // 走线亚像素量测（Halcon 量测工具箱落地）。与检测主线解耦：显式调用、默认关闭。
    // ROI 由调用方给定（产线上来自示教/配方），量测器不自动找线——宁可不测，不可瞎测。
    var imageArg = Arg(args, "--image=");
    if (string.IsNullOrEmpty(imageArg))
    {
        Console.WriteLine("[TraceMeasure] 缺少 --image=<灰度图路径或目录>。");
        return 1;
    }
    var imageFull = Path.GetFullPath(imageArg);
    var images = new List<string>();
    if (Directory.Exists(imageFull))
    {
        images.AddRange(Directory.GetFiles(imageFull, "*.jpg", SearchOption.TopDirectoryOnly));
        images.AddRange(Directory.GetFiles(imageFull, "*.png", SearchOption.TopDirectoryOnly));
        images.AddRange(Directory.GetFiles(imageFull, "*.bmp", SearchOption.TopDirectoryOnly));
        images.Sort(StringComparer.Ordinal);
    }
    else if (File.Exists(imageFull)) images.Add(imageFull);
    else
    {
        Console.WriteLine($"[TraceMeasure] 图像不存在: {imageFull}");
        return 1;
    }

    var roiArg = Arg(args, "--roi=");
    int rx0 = 0, ry0 = 0, rx1 = -1, ry1 = -1;
    if (!string.IsNullOrEmpty(roiArg))
    {
        var parts = roiArg!.Split(',');   // 已过 IsNullOrEmpty（net48 无流分析注解）
        if (parts.Length != 4 || !int.TryParse(parts[0], out rx0) || !int.TryParse(parts[1], out ry0)
            || !int.TryParse(parts[2], out rx1) || !int.TryParse(parts[3], out ry1))
        {
            Console.WriteLine($"[TraceMeasure] --roi 格式应为 x0,y0,x1,y1，收到: {roiArg}");
            return 1;
        }
    }
    else
    {
        Console.WriteLine("[TraceMeasure] 缺少 --roi=x0,y0,x1,y1（须横跨整条走线）。");
        return 1;
    }

    var mopt = new TraceMeasurementRunner.Options
    {
        ImagePaths = images,
        RoiX0 = rx0, RoiY0 = ry0, RoiX1 = rx1, RoiY1 = ry1,
        PixelUm = double.TryParse(Arg(args, "--um="), out var umV) ? umV : 0,
        CalibTraceUm = double.TryParse(Arg(args, "--calib-um="), out var cuV) ? cuV : 0,
        WidthTolerance = double.TryParse(Arg(args, "--w-tol="), out var wtV) ? wtV : 0,
        PitchTolerance = double.TryParse(Arg(args, "--p-tol="), out var ptV) ? ptV : 0,
        ReportsDir = Path.GetFullPath(reportsDir),
        Config = new LineMetrology.Config
        {
            Filter = Arg(args, "--m-filter=") ?? "lanser2",
            Low = double.TryParse(Arg(args, "--m-low="), out var mlV) ? mlV : 20.0,
            High = double.TryParse(Arg(args, "--m-high="), out var mhV) ? mhV : 40.0,
        },
    };
    Console.WriteLine($"[TraceMeasure] images={images.Count} roi=[{rx0},{ry0}]-{rx1},{ry1} " +
                      $"um={(mopt.PixelUm > 0 ? mopt.PixelUm.ToString("F4") : "-")} " +
                      $"calib={(mopt.CalibTraceUm > 0 ? mopt.CalibTraceUm.ToString("F2") + "um" : "-")}");
    var mrep = new TraceMeasurementRunner().Run(mopt);
    var mjson = new TraceMeasurementRunner().RenderJson(mrep);
    Console.WriteLine($"json: {mjson}");
    return mrep.Failed > 0 ? 2 : 0;
}

ICamera camera = string.Equals(cfg.Camera.Mode, "Mock", StringComparison.OrdinalIgnoreCase)
    ? new MockCamera(cfg.Camera)
    : new PairedFilePlaybackCamera(cfg.Camera);
camera.Open();
if (camera is PairedFilePlaybackCamera ppc)
    Console.WriteLine($"相机: {camera.Name}  pairs={ppc.FrameCount}");
else
    Console.WriteLine($"相机: {camera.Name}");

var engine = engineOverride ?? cfg.Detection.Engine;
if ((modeHandshake || modeRecipeGate || modeConcurrency) && engineOverride is null)
    engine = "Stub";
var detector = CreateEngine(engine, out var warn);
if (!string.IsNullOrEmpty(warn)) Console.WriteLine(warn);
Console.WriteLine($"引擎: {detector.EngineName}");

var store = new ResultStore(cfg.Storage);
using var pipeline = new InspectionPipeline(camera, detector, store, recipeStore, cfg.Storage);
pipeline.CaptureQuality = cfg.CaptureQuality;
pipeline.ReviewPolicy = cfg.ReviewPolicy;

if (modeConcurrency)
{
    return await RunConcurrencySelftest(pipeline, recipeStore, port, timeoutMs);
}

if (modeRecipeGate)
{
    return await RunRecipeGateSelftest(pipeline, recipeStore, port, timeoutMs);
}

if (!modeHandshake)
{
    if (!recipeStore.IsChangeoverConfirmed)
    {
        recipeStore.ConfirmChangeover();
        Console.WriteLine("（单检）已自动 ConfirmChangeover");
    }
    Console.WriteLine("\n[单检] 成对图回放…");
    var r = await pipeline.RunOnceAsync("PAIR0");
    Console.WriteLine($"  {r.Verdict}  {r.ElapsedMs:F1}ms  defects={r.DefectCount}  id={r.InspectionId}  hash={r.RecipeHash}");
    if (!string.IsNullOrEmpty(r.SidecarJsonPath))
        Console.WriteLine($"       sidecar={r.SidecarJsonPath}");
    if (!string.IsNullOrEmpty(r.RawImagePath))
        Console.WriteLine($"       raw={r.RawImagePath}");
    if (!string.IsNullOrEmpty(r.OverlayImagePath))
        Console.WriteLine($"       ovl={r.OverlayImagePath}");
    foreach (var def in r.Defects.Take(8))
        Console.WriteLine($"       · {def}");
    if (!r.IsFault && (string.IsNullOrEmpty(r.RawImagePath) || string.IsNullOrEmpty(r.OverlayImagePath)))
        Console.WriteLine("       WARNING: OK/NG 缺少 raw/ovl 落盘路径");
    Console.WriteLine("\n说明：无 USE_HALCON → FAULT:NO_HALCON；真 license 异常 → FAULT:LICENSE。");
    Console.WriteLine("换型门闩: --recipe-gate-selftest");
    Console.WriteLine("握手:     --handshake-selftest");
    return r.IsFault && r.FaultCode is "NO_IMAGE" or "NO_HALCON" or "LICENSE" or "ENGINE" or "NO_TEMPLATE" ? 1 : 0;
}

// —— Handshake self-test ——
recipeStore.ConfirmChangeover();
pipeline.TestInjectDelayMs = injectDelay;
Console.WriteLine($"\n[Handshake self-test] port={port} timeoutMs={timeoutMs} injectDelayMs={injectDelay}");

await using var server = MakeServer(pipeline, recipeStore, port, timeoutMs);
server.Log += m => Console.WriteLine("  [HS] " + m);
server.Start();
await Task.Delay(200);

var failures = new List<string>();

async Task<string?> ReadLine(StreamReader r, int waitMs = 8000)
{
    var readTask = r.ReadLineAsync();
    var delayTask = Task.Delay(waitMs);
    var done = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
    if (done != readTask) return null;
    try { return await readTask.ConfigureAwait(false); }
    catch (IOException) { return null; }
}

Console.WriteLine("\n-- Case A: TRIG full path + LAST --");
{
    using var client = new TcpClient();
    await client.ConnectAsync("127.0.0.1", port);
    using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
    using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

    await writer.WriteLineAsync("PING");
    var pong = await ReadLine(reader);
    Expect(failures, "A.PONG", pong == "PONG", pong);

    await writer.WriteLineAsync("TRIG,P1001");
    var busy = await ReadLine(reader);
    Expect(failures, "A.BUSY", busy == "BUSY,P1001", busy);
    var result = await ReadLine(reader, 15000);
    Expect(failures, "A.RESULT",
        result != null && (result.StartsWith("OK,P1001,") || result.StartsWith("NG,P1001,") || result.StartsWith("FAULT,P1001,")),
        result);

    await writer.WriteLineAsync("LAST");
    var last = await ReadLine(reader);
    Expect(failures, "A.LAST", last == result, last);
    Console.WriteLine($"  busy={busy} result={result} last={last}");
}

Console.WriteLine("\n======== SUMMARY ========");
if (failures.Count == 0)
{
    Console.WriteLine("ALL PASS");
    return 0;
}
foreach (var f in failures)
    Console.WriteLine("FAIL: " + f);
return 1;

static HandshakeServer MakeServer(InspectionPipeline pipeline, IRecipeStore recipes, int port, int timeoutMs,
    StationArbiter? arbiter = null, Func<string?>? currentOperator = null) =>
    new(
        port,
        timeoutMs,
        runInspection: (ticket, ct) => pipeline.RunOnceAsync(ticket, ct),
        recipeInfo: () => (pipeline.CurrentRecipeId, pipeline.CurrentRecipeVersion, pipeline.CurrentRecipeHash),
        persistResult: null,
        persistFault: (partId, code, ms, inspId, ticket) =>
            pipeline.PersistHandshakeFault(partId, code, ms, inspId, ticket),
        isChangeoverConfirmed: () => recipes.IsChangeoverConfirmed,
        arbiter: arbiter,
        currentOperator: currentOperator,
        stationId: "SELFTEST");

static async Task<int> RunRecipeGateSelftest(InspectionPipeline pipeline, RecipeStore recipes, int port, int timeoutMs)
{
    Console.WriteLine("\n[Recipe gate self-test]");
    recipes.InvalidateConfirmation();
    var failures = new List<string>();

    var r0 = await pipeline.RunOnceAsync("MANUAL0", requireChangeover: true);
    Expect(failures, "P.RECIPE_GATE", r0.FaultCode == "RECIPE_NOT_CONFIRMED", r0.Verdict);

    await using var server = MakeServer(pipeline, recipes, port, timeoutMs);
    server.Log += m => Console.WriteLine("  [HS] " + m);
    server.Start();
    await Task.Delay(150);

    async Task<string?> RL(StreamReader rd, int ms = 5000)
    {
        var readTask = rd.ReadLineAsync();
        var delayTask = Task.Delay(ms);
        var done = await Task.WhenAny(readTask, delayTask);
        if (done != readTask) return null;
        try { return await readTask; }
        catch { return null; }
    }

    using (var client = new TcpClient())
    {
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync("TRIG,PX1");
        var fault = await RL(reader);
        Expect(failures, "H.UNCONFIRMED", fault == "FAULT,-,RECIPE_NOT_CONFIRMED", fault);
    }

    recipes.ConfirmChangeover();
    Console.WriteLine($"  confirmed hash={recipes.Current.ContentHash}");
    using (var client = new TcpClient())
    {
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        await writer.WriteLineAsync("TRIG,PX2");
        var busy = await RL(reader);
        var res = await RL(reader, 15000);
        Expect(failures, "H.AFTER_CONFIRM", busy == "BUSY,PX2" && res != null && res.Contains("PX2"), $"{busy}|{res}");
    }

    var h1 = RecipeStore.ComputeHashStatic(recipes.Current);
    var h2 = RecipeStore.ComputeHashStatic(recipes.Current);
    Expect(failures, "HASH.STABLE", h1 == h2 && h1.Length == 32, h1);

    // 乐观锁：拿旧 hash 保存应被拒；拿当前 hash 才能写
    var tmpDir = Path.Combine(Path.GetTempPath(), "dpst_selftest_recipes");
    var conflict = false;
    try
    {
        recipes.Save(tmpDir, RecipeStore.Clone(recipes.Current), "selftest", "stale_hash_0000");
    }
    catch (RecipeConcurrencyException) { conflict = true; }
    catch (Exception ex) { Console.WriteLine("  [lock] unexpected " + ex.GetType().Name + ": " + ex.Message); }

    var savedWithToken = true;
    try
    {
        recipes.Save(tmpDir, RecipeStore.Clone(recipes.Current), "selftest", recipes.Current.ContentHash);
    }
    catch (Exception ex) { savedWithToken = false; Console.WriteLine("  [lock] save failed: " + ex.Message); }
    Expect(failures, "P.OPTIMISTIC_LOCK", conflict && savedWithToken, $"conflict={conflict} save={savedWithToken}");

    Console.WriteLine("\n======== RECIPE GATE SUMMARY ========");
    if (failures.Count == 0) { Console.WriteLine("ALL PASS"); return 0; }
    foreach (var f in failures) Console.WriteLine("FAIL: " + f);
    return 1;
}

/// <summary>
/// 并发仲裁自测：UI 与 PLC 两条触发通道抢同一工位时，必须一方秒拒而不是排队；
/// 超时后的迟到结果必须落 VOID（留证、不计良率）；归属与复判口径一并校验。
/// 全部使用临时 DB，不污染 results/station.db。
/// </summary>
static async Task<int> RunConcurrencySelftest(InspectionPipeline pipeline, RecipeStore recipes,
    int port, int timeoutMs)
{
    Console.WriteLine("\n[Concurrency self-test]");
    recipes.ConfirmChangeover();
    pipeline.TestInjectDelayMs = 0;

    var failures = new List<string>();
    var dbPath = Path.GetFullPath(Path.Combine("results", "evidence",
        $"conc_selftest_{DateTime.Now:yyyyMMdd_HHmmss}.db"));
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var db = new StationDb(dbPath);
    pipeline.Persisted += r => InsertRow(db, r);

    var arbiter = new StationArbiter();
    string? operatorName = "selftest-op";

    async Task<string?> ReadLine(StreamReader rd, int ms)
    {
        var readTask = rd.ReadLineAsync();
        var delayTask = Task.Delay(ms);
        var done = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
        if (done != readTask) return null;
        try { return await readTask.ConfigureAwait(false); }
        catch (IOException) { return null; }
    }

    async Task<(string? first, string? second, double firstMs)> Trig(int targetPort, string partId, int secondMs)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", targetPort).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
        { NewLine = "\n", AutoFlush = true };
        var sw = Stopwatch.StartNew();
        await writer.WriteLineAsync("TRIG," + partId).ConfigureAwait(false);
        var first = await ReadLine(reader, 8000).ConfigureAwait(false);
        var firstMs = sw.Elapsed.TotalMilliseconds;
        string? second = null;
        if (secondMs > 0 && first != null && first.StartsWith("BUSY", StringComparison.Ordinal))
            second = await ReadLine(reader, secondMs).ConfigureAwait(false);
        return (first, second, firstMs);
    }

    try
    {
        await using var server = MakeServer(pipeline, recipes, port, timeoutMs, arbiter, () => operatorName);
        server.Log += m => Console.WriteLine("  [HS] " + m);
        server.Start();
        await Task.Delay(200);

        // —— C.PLC_BUSY_WHEN_UI：操作员持租约时，PLC 的 TRIG 必须秒回 BUSY（不排队）——
        var opLease = arbiter.TryAcquire(TriggerSource.Operator, "P-UI", out var opDeny);
        var (busyLine, _, busyMs) = await Trig(port, "P-PLC1", 0);
        arbiter.Release(TriggerSource.Operator);
        Expect(failures, "C.PLC_BUSY_WHEN_UI",
            opLease == LeaseResult.Acquired && busyLine == "FAULT,-,BUSY" && busyMs < 1000,
            $"{opLease}/{opDeny}/{busyLine}/{busyMs:F0}ms");

        // —— C.UI_REJECT_WHEN_PLC：PLC 持租约时，UI 取租约必须 Busy 且给出原因 ——
        var plcLease = arbiter.TryAcquire(TriggerSource.Plc, "P-PLC2", out _);
        var uiLease = arbiter.TryAcquire(TriggerSource.Operator, "P-UI2", out var uiDeny);
        arbiter.Release(TriggerSource.Plc);
        Expect(failures, "C.UI_REJECT_WHEN_PLC",
            plcLease == LeaseResult.Acquired && uiLease == LeaseResult.Busy && !string.IsNullOrEmpty(uiDeny),
            $"{plcLease}/{uiLease}/{uiDeny}");

        // —— C.NO_OPERATOR：无登录会话 → 拒收，不产出无归属记录 ——
        operatorName = null;
        var (noOpLine, _, _) = await Trig(port, "P-NOOP", 0);
        operatorName = "selftest-op";
        Expect(failures, "C.NO_OPERATOR", noOpLine == "FAULT,-,NO_OPERATOR", noOpLine);

        // —— C.OPERATOR_FIELD：UI 触发记录带 operator/shift/trigger/station ——
        var opTicket = new InspectionTicket
        {
            PartId = "P-OP",
            Operator = operatorName,
            Shift = InspectionTicket.ShiftOf(DateTime.Now),
            Trigger = TriggerSource.Operator,
            StationId = "CONC01",
            RequireChangeover = true,
            Persist = true
        };
        var opTake = arbiter.TryAcquire(TriggerSource.Operator, "P-OP", out _);
        var opResult = await pipeline.RunOnceAsync(opTicket).ConfigureAwait(false);
        arbiter.Release(TriggerSource.Operator);
        var (opRows, _) = db.QueryInspections(new InspectionQuery { IncludeVoid = true, Limit = 200 });
        var opRow = opRows.FirstOrDefault(r => r.Id == opResult.InspectionId);
        Expect(failures, "C.OPERATOR_FIELD",
            opTake == LeaseResult.Acquired && opRow != null
            && opRow.Operator == "selftest-op" && opRow.TriggerSource == "UI"
            && opRow.StationId == "CONC01" && opRow.Shift == opTicket.Shift,
            $"{opRow?.Operator}/{opRow?.TriggerSource}/{opRow?.StationId}/{opRow?.Shift}");

        // —— C.VOID_TIMEOUT：超时后迟到结果必须落 VOID（留证不计良率）+ 故障行 id 回填 parent ——
        var slowPort = port + 17;
        pipeline.TestInjectDelayMs = 1500;
        await using (var slow = MakeServer(pipeline, recipes, slowPort, 400, arbiter, () => operatorName))
        {
            slow.Log += m => Console.WriteLine("  [HS-slow] " + m);
            slow.Start();
            await Task.Delay(200);
            var (slowBusy, timeoutLine, _) = await Trig(slowPort, "P-TMO", 3000);
            Console.WriteLine($"  busy={slowBusy} timeout={timeoutLine}");
            // 等迟到结果落盘（MarkVoid 之后才写，不计良率）
            await Task.Delay(2500);
        }
        pipeline.TestInjectDelayMs = 0;

        var (rows, _) = db.QueryInspections(new InspectionQuery { IncludeVoid = true, Limit = 200 });
        var tmoRows = rows.Where(r => r.PartId == "P-TMO").ToList();
        var tmoFault = tmoRows.FirstOrDefault(r => r.Verdict == "FAULT:TIMEOUT");
        var tmoVoid = tmoRows.FirstOrDefault(r => r.IsVoid);
        var kpi = db.Summary(null, null, null);
        Expect(failures, "C.VOID_TIMEOUT",
            tmoFault != null && tmoVoid != null
            && tmoVoid.VoidReason == "TIMEOUT"
            && tmoVoid.ParentId == tmoFault.Id
            && !tmoVoid.CountsTowardYield
            && kpi.Void >= 1
            && kpi.Total == kpi.Ok + kpi.Ng + kpi.Fault,
            $"fault={tmoFault?.Id} void={tmoVoid?.Id} parent={tmoVoid?.ParentId} voidN={kpi.Void} total={kpi.Total}/{kpi.Ok + kpi.Ng + kpi.Fault}");

        // —— C.REVIEW：伪点改判后终判 OK，FalseCall +1 ——
        var ngId = "CONC-REV";
        db.InsertInspection(new InspectionRow
        {
            Id = ngId, PartId = "P-REV", Ts = DateTime.Now.ToString("O"), Verdict = "NG", IsOk = false,
            EngineMs = 1, DefectCount = 0, RecipeId = recipes.Current.Id, RecipeVersion = recipes.Current.Version,
            RecipeHash = recipes.Current.ContentHash ?? "", Engine = "Stub", CountsTowardYield = true,
            Operator = "selftest-op", Shift = InspectionTicket.ShiftOf(DateTime.Now), TriggerSource = "PLC",
            StationId = "CONC01"
        }, Array.Empty<DefectRow>());
        var kpiBeforeRev = db.Summary(null, null, null);
        var (ngRows, _) = db.QueryInspections(new InspectionQuery { IncludeVoid = true, Limit = 200 });
        var ngRow = ngRows.FirstOrDefault(r => r.Id == ngId);
        var reviewSvc = new ReviewService(db);
        string? revErr = null;
        var revOk = ngRow != null && reviewSvc.Submit(ngRow, ReviewDecision.FalseCall,
            "FP_PSEUDO", "concurrency-selftest", "lead", out revErr);
        var kpiAfterRev = db.Summary(null, null, null);
        var (allAfter, _) = db.QueryInspections(new InspectionQuery { IncludeVoid = true, Limit = 200 });
        var revRow = allAfter.FirstOrDefault(r => r.Id == ngId);
        Expect(failures, "C.REVIEW",
            revOk && string.IsNullOrEmpty(revErr)
            && revRow != null && revRow.FinalVerdict == "OK" && revRow.ReviewUser == "lead"
            && kpiAfterRev.FalseCall == kpiBeforeRev.FalseCall + 1,
            $"{revOk}/{ngRow?.Verdict}/fc {kpiBeforeRev.FalseCall}->{kpiAfterRev.FalseCall}");
    }
    catch (Exception ex)
    {
        failures.Add("EXCEPTION: " + ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
    }
    finally
    {
        try { File.Delete(dbPath); } catch { /* cleanup best-effort */ }
    }

    Console.WriteLine("\n======== CONCURRENCY SUMMARY ========");
    if (failures.Count == 0)
    {
        Console.WriteLine("ALL PASS");
        return 0;
    }
    foreach (var f in failures)
        Console.WriteLine("FAIL: " + f);
    return 1;

    // 与 StationServices.Persisted 同口径（自测只写临时库）
    static void InsertRow(StationDb db, InspectionResult r) =>
        db.InsertInspection(new InspectionRow
        {
            Id = r.InspectionId,
            PartId = r.PartId,
            Ts = r.Timestamp.ToString("O"),
            Verdict = r.Verdict,
            IsOk = r.IsOk,
            FaultCode = r.FaultCode,
            EngineMs = r.ElapsedMs,
            DefectCount = r.DefectCount,
            RecipeId = r.RecipeId,
            RecipeVersion = r.RecipeVersion,
            RecipeHash = r.RecipeHash,
            Engine = r.EngineName,
            CountsTowardYield = r.CountsTowardYield,
            Operator = r.Operator,
            Shift = r.Shift,
            TriggerSource = r.TriggerSource,
            StationId = r.StationId,
            IsVoid = r.IsVoid,
            VoidReason = r.VoidReason,
            ParentId = r.ParentInspectionId,
            FinalVerdict = r.FinalVerdict,
            ReviewTs = r.ReviewTs,
            ReviewUser = r.ReviewUser,
            RawPath = r.RawImagePath,
            OvlPath = r.OverlayImagePath,
            SidecarPath = r.SidecarJsonPath
        }, r.Defects.Select(d => new DefectRow
        {
            InspectionId = r.InspectionId,
            TypeCode = d.TypeCode,
            X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score
        }).ToList());
}

static int DbSelftest()
{
    var dbPath = Path.GetFullPath(Path.Combine("results", "evidence",
        $"db_selftest_{DateTime.Now:yyyyMMdd_HHmmss}.db"));
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var failures = new List<string>();
    Console.WriteLine($"\n[Db self-test] db={dbPath}");

    try
    {
        var db = new StationDb(dbPath);
        Expect(failures, "DB.SCHEMA", File.Exists(dbPath), dbPath);

        // 锚定到「昨天 01:00 / 昨天 02:00 / 现在」：无论何时跑，前两条必同一天、第三条必是今天
        var t1 = DateTime.Today.AddDays(-1).AddHours(1);
        var t2 = DateTime.Today.AddDays(-1).AddHours(2);
        var t3 = DateTime.Now;

        var inserted1 = db.InsertInspection(
            new InspectionRow
            {
                Id = "T1", PartId = "P-T1", Ts = t1.ToString("O"), Verdict = "OK", IsOk = true,
                EngineMs = 100.5, DefectCount = 0, RecipeId = "pcb_open_mousebite", RecipeVersion = "0.3.0",
                RecipeHash = "hashT1", Engine = "Stub", CountsTowardYield = true
            },
            Array.Empty<DefectRow>());
        Expect(failures, "DB.INSERT.OK", inserted1, inserted1.ToString());

        var ngDefects = new List<DefectRow>
        {
            new() { InspectionId = "T2", TypeCode = "MOUSEBITE", X = 1, Y = 2, W = 3, H = 4, Score = 10 },
            new() { InspectionId = "T2", TypeCode = "MOUSEBITE", X = 5, Y = 6, W = 7, H = 8, Score = 20 },
            new() { InspectionId = "T2", TypeCode = "OPEN", X = 9, Y = 9, W = 2, H = 2, Score = 30 }
        };
        var inserted2 = db.InsertInspection(
            new InspectionRow
            {
                Id = "T2", PartId = "P-T2", Ts = t2.ToString("O"), Verdict = "NG", IsOk = false,
                EngineMs = 200.5, DefectCount = 3, RecipeId = "pcb_open_mousebite", RecipeVersion = "0.3.0",
                RecipeHash = "hashT2", Engine = "Stub", CountsTowardYield = true,
                RawPath = @"X:\raw.png", OvlPath = @"X:\ovl.png"
            },
            ngDefects);
        Expect(failures, "DB.INSERT.NG", inserted2, inserted2.ToString());

        var reInserted = db.InsertInspection(
            new InspectionRow
            {
                Id = "T2", PartId = "P-T2", Ts = t2.ToString("O"), Verdict = "NG", IsOk = false,
                EngineMs = 200.5, DefectCount = 3, RecipeId = "x", RecipeVersion = "x", RecipeHash = "x",
                Engine = "Stub", CountsTowardYield = true
            },
            Array.Empty<DefectRow>());
        Expect(failures, "DB.IDEMPOTENT", !reInserted, reInserted.ToString());

        db.InsertInspection(
            new InspectionRow
            {
                Id = "T3", PartId = "P-T3", Ts = t3.ToString("O"), Verdict = "NG", IsOk = false,
                EngineMs = 300.5, DefectCount = 1, RecipeId = "pcb_other", RecipeVersion = "0.1.0",
                RecipeHash = "hashT3", Engine = "Stub", CountsTowardYield = true
            },
            new List<DefectRow>
            {
                new() { InspectionId = "T3", TypeCode = "OPEN", X = 0, Y = 0, W = 1, H = 1, Score = 1 }
            });

        var (all, totalAll) = db.QueryInspections(new InspectionQuery());
        Expect(failures, "DB.QUERY.ALL", all.Count == 3 && totalAll == 3, $"{all.Count}/{totalAll}");

        var (ngRows, totalNg) = db.QueryInspections(new InspectionQuery { Verdict = "NG" });
        Expect(failures, "DB.QUERY.NG", ngRows.Count == 2 && totalNg == 2, $"{ngRows.Count}/{totalNg}");

        var (page1, totalPaged) = db.QueryInspections(new InspectionQuery { Limit = 1, Offset = 0 });
        var (page2, _) = db.QueryInspections(new InspectionQuery { Limit = 1, Offset = 1 });
        Expect(failures, "DB.QUERY.PAGE", page1.Count == 1 && page2.Count == 1 && totalPaged == 3
            && page1[0].Id == "T3" && page2[0].Id == "T2",
            $"{page1.Count}/{page2.Count}/{totalPaged} {page1.FirstOrDefault()?.Id}");

        var (openRows, totalOpen) = db.QueryInspections(new InspectionQuery { TypeCode = "OPEN" });
        Expect(failures, "DB.QUERY.TYPE", totalOpen == 2 && openRows.All(r => r.Id is "T2" or "T3"),
            $"{totalOpen}");

        var defects = db.GetDefects("T2");
        Expect(failures, "DB.DEFECTS", defects.Count == 3, defects.Count.ToString());

        var kpi = db.Summary(null, null, null);
        Expect(failures, "DB.KPI", kpi.Total == 3 && kpi.Ok == 1 && kpi.Ng == 2 && kpi.Fault == 0
            && kpi.AvgMs > 0 && kpi.DefectTotal == 4 && kpi.FirstPassOk == 1 && kpi.Void == 0
            && kpi.Reviewed == 0 && kpi.FalseCall == 0,
            $"{kpi.Total}/{kpi.Ok}/{kpi.Ng}/{kpi.Fault}/{kpi.AvgMs:F1} d={kpi.DefectTotal} fpy={kpi.FirstPassOk}");

        var daily = db.DailyYield(null, null, null);
        Expect(failures, "DB.DAILY", daily.Count == 2 && daily[0].Total == 2 && daily[1].Total == 1,
            string.Join(",", daily.Select(d => $"{d.Day}:{d.Total}")));

        var pareto = db.DefectPareto(null, null, null);
        Expect(failures, "DB.PARETO", pareto.Count >= 1 && pareto[0].TypeCode == "MOUSEBITE" && pareto[0].Count == 2,
            string.Join(",", pareto.Select(x => $"{x.TypeCode}:{x.Count}")));

        var paretoRecipe = db.DefectPareto(null, null, "pcb_other");
        Expect(failures, "DB.PARETO.RECIPE", paretoRecipe.Count == 1 && paretoRecipe[0].TypeCode == "OPEN",
            string.Join(",", paretoRecipe.Select(x => $"{x.TypeCode}:{x.Count}")));

        db.SaveRecipeSnapshot(new RecipeSnapshotRow
        {
            Id = "pcb_open_mousebite", Version = "0.3.0", Hash = "hashT1",
            Note = "selftest", Json = "{\"id\":\"pcb_open_mousebite\"}", UpdatedAt = DateTime.Now.ToString("O")
        });
        var snaps = db.ListRecipeSnapshots("pcb_open_mousebite");
        Expect(failures, "DB.SNAPSHOT", snaps.Count == 1 && snaps[0].Hash == "hashT1",
            snaps.Count.ToString());

        db.UpsertUser(new UserRow
        {
            Username = "admin", PasswordHash = "ph", Salt = "salt", Role = "Admin",
            CreatedAt = DateTime.Now.ToString("O")
        });
        var user = db.GetUser("admin");
        Expect(failures, "DB.USER.GET", user != null && user.Role == "Admin", user?.Role);
        db.TouchLastLogin("admin", DateTime.Now);
        var touched = db.GetUser("admin");
        Expect(failures, "DB.USER.TOUCH", touched?.LastLogin != null, touched?.LastLogin);

        db.AddAudit("admin", "SELFTEST", "db self-test audit row");
        var audit = db.ListAudit();
        Expect(failures, "DB.AUDIT", audit.Count == 1 && audit[0].Action == "SELFTEST", audit.Count.ToString());

        var counts = db.RowCounts();
        Expect(failures, "DB.COUNTS",
            counts.Inspections == 3 && counts.Defects == 4 && counts.Users == 1 && counts.Audit == 1,
            $"{counts.Inspections}/{counts.Defects}/{counts.Users}/{counts.Audit}");

        // ---------- schema 升级：旧库（user_version=2）→ 备份 + 重建为 v3 ----------
        var legacyPath = dbPath + ".legacy.db";
        try { File.Delete(legacyPath); } catch { /* ignore */ }
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyPath}"))
        {
            legacy.Open();
            using var cmd = legacy.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE inspections (id TEXT PRIMARY KEY, ts TEXT NOT NULL, verdict TEXT NOT NULL);
                CREATE TABLE alarms (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, source TEXT NOT NULL, code TEXT NOT NULL);
                INSERT INTO inspections VALUES ('OLD1', '2026-01-01T00:00:00', 'OK');
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }

        var v3 = new StationDb(legacyPath);
        var backups = Directory.GetFiles(Path.GetDirectoryName(legacyPath)!, Path.GetFileName(legacyPath) + ".bak.*");
        Expect(failures, "DB.MIGRATE.BACKUP", backups.Length >= 1, backups.Length.ToString());

        var (legacyRows, legacyTotal) = v3.QueryInspections(new InspectionQuery { IncludeVoid = true });
        Expect(failures, "DB.MIGRATE.REBUILD", legacyTotal == 0, legacyTotal.ToString());

        // 新列可用：归属 + 作废
        v3.InsertInspection(new InspectionRow
        {
            Id = "V1", PartId = "P-V1", Ts = DateTime.Now.ToString("O"), Verdict = "OK", IsOk = true,
            EngineMs = 1, RecipeId = "r", RecipeVersion = "v", RecipeHash = "h", Engine = "Stub",
            CountsTowardYield = true, Operator = "alice", Shift = "A", TriggerSource = "PLC", StationId = "ST01"
        }, Array.Empty<DefectRow>());
        v3.InsertInspection(new InspectionRow
        {
            Id = "V2", PartId = "P-V2", Ts = DateTime.Now.ToString("O"), Verdict = "VOID", IsOk = false,
            EngineMs = 1, RecipeId = "r", RecipeVersion = "v", RecipeHash = "h", Engine = "Stub",
            CountsTowardYield = false, Operator = "alice", IsVoid = true, VoidReason = "TIMEOUT"
        }, Array.Empty<DefectRow>());

        var (visible, visibleTotal) = v3.QueryInspections(new InspectionQuery());
        var (withVoid, withVoidTotal) = v3.QueryInspections(new InspectionQuery { IncludeVoid = true });
        Expect(failures, "DB.MIGRATE.NEWCOLS",
            visibleTotal == 1 && withVoidTotal == 2
            && visible[0].Operator == "alice" && visible[0].TriggerSource == "PLC" && visible[0].StationId == "ST01"
            && withVoid.Any(r => r.Id == "V2" && r.IsVoid && r.VoidReason == "TIMEOUT"),
            $"{visibleTotal}/{withVoidTotal}/{visible.FirstOrDefault()?.Operator}");

        // 复判：终判回写 + 伪点筛选
        var ngForReview = new InspectionRow
        {
            Id = "V3", PartId = "P-V3", Ts = DateTime.Now.ToString("O"), Verdict = "NG", IsOk = false,
            EngineMs = 1, RecipeId = "r", RecipeVersion = "v", RecipeHash = "h", Engine = "Stub",
            CountsTowardYield = true, Operator = "bob"
        };
        v3.InsertInspection(ngForReview, Array.Empty<DefectRow>());
        var reviewed = v3.InsertReview(new ReviewRow
        {
            InspectionId = "V3", Ts = DateTime.Now.ToString("O"), Reviewer = "lead",
            Decision = "FALSE_CALL", ReasonCode = "FP_PSEUDO", PrevVerdict = "NG"
        }, "OK");
        var (falseCalls, falseCallTotal) = v3.QueryInspections(new InspectionQuery { OnlyFalseCall = true });
        var reviewsOfV3 = v3.ListReviews("V3");
        Expect(failures, "DB.REVIEW",
            reviewed && falseCallTotal == 1 && falseCalls[0].EffectiveVerdict == "OK"
            && reviewsOfV3.Count == 1 && reviewsOfV3[0].Reviewer == "lead" && reviewsOfV3[0].Id > 0,
            $"{reviewed}/{falseCallTotal}/{reviewsOfV3.Count}");

        // 复判闭环：走服务（终判回写 + 审计），伪点改判后终判应为 OK
        var ngForService = new InspectionRow
        {
            Id = "V4", PartId = "P-V4", Ts = DateTime.Now.ToString("O"), Verdict = "NG", IsOk = false,
            EngineMs = 1, RecipeId = "r", RecipeVersion = "v", RecipeHash = "h", Engine = "Stub",
            CountsTowardYield = true, Operator = "bob"
        };
        v3.InsertInspection(ngForService, Array.Empty<DefectRow>());
        var reviewSvc = new ReviewService(v3);
        var submitted = reviewSvc.Submit(ngForService, ReviewDecision.FalseCall, "FP_PSEUDO", "selftest", "lead", out var reviewErr);
        var (allRows, _) = v3.QueryInspections(new InspectionQuery { OnlyFalseCall = true });
        var (v4Rows, _) = v3.QueryInspections(new InspectionQuery { IncludeVoid = true });
        var v4 = v4Rows.FirstOrDefault(r => r.Id == "V4");
        var auditRows = v3.ListAudit();
        var reviewAudit = auditRows.FirstOrDefault(a => a.Action == "REVIEW_SUBMIT");
        Expect(failures, "DB.REVIEW.SERVICE",
            submitted && string.IsNullOrEmpty(reviewErr)
            && v4 != null && v4.FinalVerdict == "OK" && v4.ReviewUser == "lead" && !string.IsNullOrEmpty(v4.ReviewTs)
            && allRows.Any(r => r.Id == "V4")
            && reviewAudit != null && reviewAudit.Entity == "inspection/V4"
            && reviewAudit.OldValue == "NG" && reviewAudit.NewValue == "OK" && reviewAudit.Reason == "FP_PSEUDO",
            $"{submitted}/{reviewErr}/{v4?.FinalVerdict}/{v4?.ReviewUser}/{reviewAudit?.Entity}");

        // 统计口径：VOID 不计 Total，FPY 分子/伪点率/DPPM 均从同一张表聚合
        var kpiQ = v3.Summary(null, null, null);
        Expect(failures, "DB.KPI.QUALITY",
            kpiQ.Total == 3 && kpiQ.Ok == 1 && kpiQ.Ng == 2 && kpiQ.Void == 1
            && kpiQ.FirstPassOk == 1 && kpiQ.Reviewed == 2 && kpiQ.FalseCall == 2
            && kpiQ.DefectTotal == 0,
            $"total={kpiQ.Total} ok={kpiQ.Ok} ng={kpiQ.Ng} void={kpiQ.Void} fpy={kpiQ.FirstPassOk} rev={kpiQ.Reviewed} fc={kpiQ.FalseCall}");

        // 告警：确认/恢复语义分离
        var alarmId = v3.InsertAlarm(new AlarmRecord
        {
            Ts = DateTime.Now.ToString("O"), LastTs = DateTime.Now.ToString("O"),
            Severity = (int)AlarmSeverity.Critical, Source = "TCP", Code = "STATE_FAULT", Message = "x"
        });
        v3.ClearAlarm("TCP", "STATE_FAULT", DateTime.Now);
        var remaining = v3.UnackedAlarmCount();
        var stillFindable = v3.FindUnackedAlarm("TCP", "STATE_FAULT");
        Expect(failures, "DB.ALARM.CLEAR",
            alarmId > 0 && remaining == 0 && stillFindable == null,
            $"{alarmId}/{remaining}/{stillFindable?.Id}");

        foreach (var b in backups) { try { File.Delete(b); } catch { /* ignore */ } }
        try { File.Delete(legacyPath); } catch { /* ignore */ }
    }
    catch (Exception ex)
    {
        failures.Add("EXCEPTION: " + ex.GetType().FullName + " " + ex.Message + "\n" + ex.StackTrace);
    }
    finally
    {
        try { File.Delete(dbPath); } catch { /* cleanup best-effort */ }
    }

    Console.WriteLine("\n======== DB SUMMARY ========");
    if (failures.Count == 0)
    {
        Console.WriteLine("ALL PASS");
        return 0;
    }
    foreach (var f in failures)
        Console.WriteLine("FAIL: " + f);
    return 1;
}

static void ApplySets(string[] args, RecipeConfig recipe)
{
    foreach (var a in args)
    {
        if (!a.StartsWith("--set=", StringComparison.OrdinalIgnoreCase)) continue;
        var body = a.Substring(6);
        var eq = body.IndexOf('=');
        if (eq <= 0) continue;
        var key = body.Substring(0, eq);
        var val = body.Substring(eq + 1);
        try
        {
            var prop = typeof(RecipeConfig).GetProperty(key);
            if (prop == null) { Console.WriteLine("unknown " + key); continue; }
            object boxed;
            if (prop.PropertyType == typeof(bool))
                boxed = bool.Parse(val);
            else if (prop.PropertyType == typeof(double))
                boxed = double.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
            else if (prop.PropertyType == typeof(int))
                boxed = int.Parse(val, System.Globalization.CultureInfo.InvariantCulture);
            else
            {
                Console.WriteLine("unsupported " + key);
                continue;
            }
            prop.SetValue(recipe, boxed);
        }
        catch (Exception ex)
        {
            Console.WriteLine("set-fail " + ex.GetType().FullName);
            return;
        }
    }
    // --set 改参后重算 hash：否则 sweep 报告头带陈旧 hash，事后无法按口径复盘当次跑的是什么配方。
    // 无 --set 时重算结果与载入 hash 一致（recipes/*.json 不写 hash 字段），逐位等价。
    recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
}

static string? Arg(string[] args, string prefix) =>
    (args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is string hit
        ? hit.Substring(prefix.Length) : null);

static bool Has(string[] args, params string[] flags) =>
    flags.Any(f => args.Any(a => a.Equals(f, StringComparison.OrdinalIgnoreCase)));

/// <summary>离线验证：读 "stem|x1|y1|x2|y2" -> typeId 的覆盖表；缺省 null（生产零变化）。</summary>
static IReadOnlyDictionary<string, int>? LoadTypeOverride(string? path)
{
    if (string.IsNullOrEmpty(path)) return null;
    var full = Path.GetFullPath(path);
    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(full));
    Console.WriteLine($"[TypeOverride] entries={dict?.Count ?? 0} from {full}");
    return dict;
}


static void Expect(List<string> fails, string name, bool ok, string? detail)
{
    if (ok) Console.WriteLine($"  OK {name}");
    else
    {
        Console.WriteLine($"  FAIL {name} got={detail}");
        fails.Add($"{name}: {detail}");
    }
}

static string FindPath(string relative)
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), relative),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relative)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relative)),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", relative)),
    };
    foreach (var c in candidates)
        if (Directory.Exists(c) || File.Exists(c))
            return Path.GetFullPath(c);
    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relative));
}

static void ResolvePlayback(AppConfig cfg)
{
    var root = cfg.Camera.PlaybackRootDir;
    if (Path.IsPathRooted(root) && Directory.Exists(root)) return;
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), root),
        Path.Combine(Directory.GetCurrentDirectory(), "data", "samples"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "samples")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "samples")),
    };
    foreach (var c in candidates)
    {
        if (Directory.Exists(c))
        {
            cfg.Camera.PlaybackRootDir = c;
            var man = Path.Combine(c, "Manifest.txt");
            if (File.Exists(man))
                cfg.Camera.PlaybackManifest = "Manifest.txt";
            return;
        }
    }
}
