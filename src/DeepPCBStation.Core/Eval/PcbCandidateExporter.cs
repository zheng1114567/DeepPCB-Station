using System.Globalization;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Eval;

/// <summary>
/// 候选块导出器：把检测候选连同标签与三通道证据图，导出为可直接训练的数据集。
/// </summary>
/// <remarks>
/// <b>用途</b>：为「DL 候选复判」准备训练数据（阶段 0 旁路，<b>不动任何生产判定</b>）。
///
/// <b>为什么需要它</b>：本项目正负样本极不平衡——official_test 上 TP 3033 / FP 83。
/// 只靠 83 个 FP 训练不出复判分类器。开启 <c>CollectRejected</c> 后，可以把被各道闸门
/// （edge / lop / aspect / thin / tip / area…）判掉的候选批量捞出来当负样本，
/// 这些恰恰是规则层认定为「鬼影」的样本，是负样本的主力来源。
///
/// <b>证据图设计</b>：每个候选输出一张 RGB 合成图
/// R = 实测图，G = 模板图（同坐标，DeepPCB 已预对齐），B = |实测−模板| × 2 截断。
/// 三通道一起给出「长什么样 / 应该长什么样 / 差在哪」，比单通道更有判别信息。
///
/// <b>目录按 ImageFolder 布局</b>（<c>defect/&lt;类型&gt;/</c> 与 <c>ghost/</c>），
/// 可直接被 torchvision <c>ImageFolder</c> 或 YOLO 分类训练加载。
/// </remarks>
public sealed class PcbCandidateExporter
{
    public sealed class Options
    {
        /// <summary>候选框外扩的上下文边距（像素）。给网络一点邻域信息。</summary>
        public int Margin { get; set; } = 12;

        /// <summary>IoU 达到此值才算命中 GT（与官方口径一致）。</summary>
        public double IouOk { get; set; } = 0.33;

        /// <summary>是否把被闸门判掉的候选作为负样本（鬼影）导出。<b>负样本主力。</b></summary>
        public bool IncludeRejected { get; set; } = true;

        /// <summary>每对图最多导出多少个被拒候选，避免负样本爆炸。</summary>
        public int MaxRejectedPerPair { get; set; } = 60;

        /// <summary>差分通道的放大倍数（弱缺陷的 |diff| 只有 30~110，需放大才可见）。</summary>
        public int DiffGain { get; set; } = 2;

        public int MaxPairs { get; set; } = int.MaxValue;
    }

    public sealed class Row
    {
        /// <summary>相对 outDir 的文件路径。</summary>
        public string File { get; set; } = "";

        /// <summary>1 = 真缺陷（命中 GT）；0 = 鬼影。</summary>
        public int Label { get; set; }

        /// <summary>六类类型名；鬼影为 "ghost"。</summary>
        public string Type { get; set; } = "";

        /// <summary>样本来源：tp / fp / rejected:&lt;reason&gt;。</summary>
        public string Source { get; set; } = "";

        public double Iou { get; set; }
        public double Area { get; set; }
        public double Conf { get; set; }
        public int BoxX { get; set; }
        public int BoxY { get; set; }
        public int BoxW { get; set; }
        public int BoxH { get; set; }
        public string Pair { get; set; } = "";
        public int PatchW { get; set; }
        public int PatchH { get; set; }
    }

    public sealed class Result
    {
        public int Pairs { get; set; }
        public int Positives { get; set; }
        public int Negatives { get; set; }
        public string OutDir { get; set; } = "";
        public string ManifestPath { get; set; } = "";
        public List<Row> Rows { get; } = new();

        /// <summary>按类型计数（含 ghost）。</summary>
        public SortedDictionary<string, int> ByType { get; } = new(StringComparer.Ordinal);

        /// <summary>按来源计数。</summary>
        public SortedDictionary<string, int> BySource { get; } = new(StringComparer.Ordinal);

        public string RenderMarkdown()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## 候选块导出（DL 复判训练数据）");
            sb.AppendLine();
            sb.AppendLine($"| 项 | 值 |");
            sb.AppendLine($"|----|-----|");
            sb.AppendLine($"| 参与图对 | {Pairs} |");
            sb.AppendLine($"| 正样本（命中 GT） | {Positives} |");
            sb.AppendLine($"| 负样本（鬼影） | {Negatives} |");
            sb.AppendLine($"| 正负比 | 1 : {(Positives > 0 ? (double)Negatives / Positives : 0):F2} |");
            sb.AppendLine($"| 输出目录 | {OutDir} |");
            sb.AppendLine();
            sb.AppendLine("### 按类型");
            sb.AppendLine();
            sb.AppendLine("| 类型 | 数量 |");
            sb.AppendLine("|------|------|");
            foreach (var kv in ByType) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            sb.AppendLine();
            sb.AppendLine("### 按来源");
            sb.AppendLine();
            sb.AppendLine("| 来源 | 数量 |");
            sb.AppendLine("|------|------|");
            foreach (var kv in BySource) sb.AppendLine($"| {kv.Key} | {kv.Value} |");
            return sb.ToString();
        }
    }

    public Result Run(
        string samplesDir, IDefectDetector detector, RecipeConfig recipe,
        string outDir, Options? opt = null)
    {
        opt ??= new Options();
        var result = new Result { OutDir = outDir };

        Directory.CreateDirectory(outDir);
        var defectDir = Path.Combine(outDir, "defect");
        var ghostDir = Path.Combine(outDir, "ghost");
        Directory.CreateDirectory(defectDir);
        Directory.CreateDirectory(ghostDir);

        // 开启拒收收集：负样本的来源
        if (detector is HalconPcbDefectDetector hal)
            hal.CollectRejected = opt.IncludeRejected;
        detector.ApplyRecipe(recipe);

        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(opt.MaxPairs).ToList();
        result.Pairs = pairs.Count;

        var idx = 0;
        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            var gts = PcbGoldenEvaluator.LoadAllBoxes(annPath);
            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var frame = new FrameBuffer(
                test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);

            var outcome = detector.Detect(frame);
            if (outcome.IsFault) continue;

            // ---------- 正/负：检出候选按 IoU 匹配 GT ----------
            var used = new bool[gts.Count];
            foreach (var d in outcome.Defects)
            {
                var best = -1.0;
                var bestIdx = -1;
                for (var i = 0; i < gts.Count; i++)
                {
                    if (used[i]) continue;
                    var iou = IoU(d.X, d.Y, d.X + d.W - 1, d.Y + d.H - 1,
                                  gts[i].X1, gts[i].Y1, gts[i].X2, gts[i].Y2);
                    if (iou > best) { best = iou; bestIdx = i; }
                }

                var hit = bestIdx >= 0 && best >= opt.IouOk;
                if (hit) used[bestIdx] = true;

                var typeName = hit
                    ? PcbGoldenEvaluator.ClassNameOf(gts[bestIdx].TypeId)
                    : "ghost";
                var rel = WritePatch(outDir, opt, test, temp,
                    d.X, d.Y, d.W, d.H, hit ? "defect" : "ghost", typeName, stem, idx,
                    out var pw, out var ph);

                result.Rows.Add(new Row
                {
                    File = rel,
                    Label = hit ? 1 : 0,
                    Type = typeName,
                    Source = hit ? "tp" : "fp",
                    Iou = Math.Max(0, best),
                    Area = d.W * (double)d.H,
                    Conf = d.Confidence > 0 ? d.Confidence : d.Score,
                    BoxX = d.X, BoxY = d.Y, BoxW = d.W, BoxH = d.H,
                    Pair = stem, PatchW = pw, PatchH = ph
                });
                idx++;
            }

            // ---------- 负样本主力：被闸门判掉的候选 ----------
            if (!opt.IncludeRejected || outcome.Rejected == null) continue;
            var n = 0;
            foreach (var rb in outcome.Rejected)
            {
                if (n >= opt.MaxRejectedPerPair) break;
                var b = rb.Blob;
                if (b.W <= 0 || b.H <= 0) continue;
                var rel = WritePatch(outDir, opt, test, temp,
                    b.X, b.Y, b.W, b.H, "ghost", "ghost", stem, idx,
                    out var pw, out var ph);
                result.Rows.Add(new Row
                {
                    File = rel,
                    Label = 0,
                    Type = "ghost",
                    Source = "rejected:" + rb.Reason,
                    Iou = 0,
                    Area = rb.Area > 0 ? rb.Area : b.W * (double)b.H,
                    Conf = b.Confidence > 0 ? b.Confidence : b.Score,
                    BoxX = b.X, BoxY = b.Y, BoxW = b.W, BoxH = b.H,
                    Pair = stem, PatchW = pw, PatchH = ph
                });
                idx++;
                n++;
            }
        }

        // ---------- 统计 ----------
        foreach (var r in result.Rows)
        {
            if (r.Label == 1) result.Positives++; else result.Negatives++;
            result.ByType[r.Type] = result.ByType.TryGetValue(r.Type, out var c) ? c + 1 : 1;
            var src = r.Source.Contains(':') ? r.Source.Split(':')[0] + ":*" : r.Source;
            result.BySource[src] = result.BySource.TryGetValue(src, out var c2) ? c2 + 1 : 1;
        }

        // ---------- 落清单与使用说明 ----------
        var manifest = Path.Combine(outDir, "manifest.csv");
        using (var w = new StreamWriter(manifest, false, new System.Text.UTF8Encoding(false)))
        {
            w.WriteLine("file,label,type,source,iou,area,conf,box_x,box_y,box_w,box_h,pair,patch_w,patch_h");
            foreach (var r in result.Rows)
                w.WriteLine(string.Join(",", new[]
                {
                    r.File,
                    r.Label.ToString(CultureInfo.InvariantCulture),
                    r.Type, r.Source,
                    r.Iou.ToString("F4", CultureInfo.InvariantCulture),
                    r.Area.ToString("F1", CultureInfo.InvariantCulture),
                    r.Conf.ToString("F4", CultureInfo.InvariantCulture),
                    r.BoxX.ToString(CultureInfo.InvariantCulture),
                    r.BoxY.ToString(CultureInfo.InvariantCulture),
                    r.BoxW.ToString(CultureInfo.InvariantCulture),
                    r.BoxH.ToString(CultureInfo.InvariantCulture),
                    r.Pair,
                    r.PatchW.ToString(CultureInfo.InvariantCulture),
                    r.PatchH.ToString(CultureInfo.InvariantCulture)
                }));
        }
        result.ManifestPath = manifest;

        File.WriteAllText(Path.Combine(outDir, "README.txt"),
            "候选块导出（DL 候选复判训练数据）\n" +
            "================================\n\n" +
            "目录（ImageFolder 布局，可直接被 torchvision.ImageFolder / YOLO 分类训练加载）\n" +
            "  defect/<类型>/   正样本：候选框 IoU>=阈值 命中 GT\n" +
            "  ghost/           负样本：未命中 GT 的 FP + 被闸门判掉的候选\n\n" +
            "每块图的三通道语义\n" +
            "  R = 实测图灰度\n" +
            "  G = 模板图灰度（同坐标；DeepPCB 已官方预对齐，故直接取同坐标）\n" +
            "  B = |实测 - 模板| * DiffGain，截断到 255\n\n" +
            "清单 manifest.csv 字段\n" +
            "  file,label,type,source,iou,area,conf,box_x,box_y,box_w,box_h,pair,patch_w,patch_h\n" +
            "  source: tp=命中GT / fp=未命中 / rejected:<reason>=被某道闸门判掉\n\n" +
            "已知限制（如实记录）\n" +
            "  - 模板通道按同坐标取，未做逐候选配准。DeepPCB 已预对齐故可行；\n" +
            "    换真实产线数据（有平移/旋转）必须先配准再取模板通道，否则 G 通道错位。\n" +
            "  - patch 尺寸随候选框变化，训练侧需自行 Resize 到统一输入尺寸。\n" +
            "  - 本工具为旁路：不改动任何生产判定与配方。\n");

        return result;
    }

    /// <summary>裁一块三通道证据图并落盘，返回相对路径。</summary>
    private static string WritePatch(
        string outDir, Options opt, FrameBuffer test, FrameBuffer temp,
        int bx, int by, int bw, int bh, string group, string typeName,
        string stem, int idx, out int pw, out int ph)
    {
        var w = test.Width;
        var h = test.Height;

        var x1 = Math.Max(0, bx - opt.Margin);
        var y1 = Math.Max(0, by - opt.Margin);
        var x2 = Math.Min(w - 1, bx + bw - 1 + opt.Margin);
        var y2 = Math.Min(h - 1, by + bh - 1 + opt.Margin);
        pw = Math.Max(1, x2 - x1 + 1);
        ph = Math.Max(1, y2 - y1 + 1);

        var bgr = new byte[pw * ph * 3];
        var p = 0;
        for (var y = y1; y <= y2; y++)
        {
            var rt = y * test.Stride;
            var rm = y * w;   // 模板为紧凑排布（stride == width）
            for (var x = x1; x <= x2; x++)
            {
                var tv = test.Pixels[rt + x];
                var mv = temp.Pixels[rm + x];
                var d = tv - mv;
                if (d < 0) d = -d;
                var dv = d * opt.DiffGain;
                if (dv > 255) dv = 255;
                bgr[p++] = (byte)dv;   // B
                bgr[p++] = mv;         // G
                bgr[p++] = tv;         // R
            }
        }

        var dir = group == "defect"
            ? Path.Combine(outDir, "defect", typeName)
            : Path.Combine(outDir, "ghost");
        Directory.CreateDirectory(dir);

        var name = $"{stem}__{idx:D6}.png";
        var full = Path.Combine(dir, name);
        ImageIo.SaveBgrPng(full, bgr, pw, ph);
        return Path.Combine(group, group == "defect" ? typeName + "/" + name : name)
                   .Replace('\\', '/');
    }

    /// <summary>与官方金标口径同源：直接复用 <see cref="PcbGoldenEvaluator.IoU"/>，不再本地复制一份。</summary>
    private static double IoU(int ax1, int ay1, int ax2, int ay2, int bx1, int by1, int bx2, int by2)
        => PcbGoldenEvaluator.IoU(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2);
}
