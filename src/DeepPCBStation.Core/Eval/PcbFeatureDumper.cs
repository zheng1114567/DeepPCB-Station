using System.Globalization;
using System.Text;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using DeepPCBStation.Core.Recipe;

namespace DeepPCBStation.Core.Eval;

/// <summary>
/// 候选特征落盘（诊断/拟合用）：跑一遍检测器，把每个通过闸门的候选的线宽归一化特征
/// 连同「同图最优 IoU + 对应 GT 类型」写成 TSV。
///
/// 用途：在 <c>data/full</c> 的 1000 对训练集上拟合 <see cref="PcbTypeTree"/> 的阈值与
/// <see cref="PcbConfidence"/> 的权重；<c>data/official_test</c> 的 500 对只做验收，禁止在此 dump 上拟合。
/// </summary>
public sealed class PcbFeatureDumper
{
    public sealed class Result
    {
        public int Pairs { get; set; }
        public int Candidates { get; set; }
        public int Matched { get; set; }
        public string TsvPath { get; set; } = "";
    }

    public Result Run(
        string samplesDir,
        IDefectDetector detector,
        RecipeConfig recipe,
        string outDir,
        int maxPairs = int.MaxValue,
        ISet<string>? excludeStems = null)
    {
        if (detector is not HalconPcbDefectDetector halcon)
            throw new InvalidOperationException("特征 dump 仅支持 Halcon 引擎（特征依赖铜距场与铜网标号）。");

        recipe.ContentHash = RecipeStore.ComputeHashStatic(recipe);
        detector.ApplyRecipe(recipe);

        var pairs = PcbGoldenEvaluator.DiscoverPairs(samplesDir)
            .Where(p => excludeStems == null || !excludeStems.Contains(p.stem))
            .Take(maxPairs)
            .ToList();

        var res = new Result { Pairs = pairs.Count };
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"features_{DateTime.Now:yyyyMMdd_HHmmss}.tsv");
        res.TsvPath = path;

        var sb = new StringBuilder();
        sb.AppendLine(DefectFeatureRow.TsvHeader);

        foreach (var (stem, testPath, tempPath, annPath) in pairs)
        {
            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var frame = new FrameBuffer(
                test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);

            var sink = new List<DefectFeatureRow>();
            halcon.FeatureSink = sink;
            try
            {
                var outcome = halcon.Detect(frame);
                if (outcome.IsFault) continue;
            }
            finally
            {
                halcon.FeatureSink = null;
            }

            var gts = PcbGoldenEvaluator.LoadAllBoxes(annPath).ToList();
            foreach (var row in sink)
            {
                var bestIou = -1.0;
                var bestType = 0;
                foreach (var g in gts)
                {
                    var v = Iou(row.X, row.Y, row.W, row.H, g.X1, g.Y1, g.X2, g.Y2);
                    if (v > bestIou) { bestIou = v; bestType = g.TypeId; }
                }
                row.MatchIou = bestIou;
                row.GtType = bestIou >= PcbIcdarEvaluator.IouConstraint ? bestType : 0;
                if (row.GtType > 0) res.Matched++;
                row.Pair = stem;
                sb.AppendLine(row.ToTsv());
                res.Candidates++;
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return res;
    }

    private static double Iou(int x, int y, int w, int h, int gx1, int gy1, int gx2, int gy2)
    {
        var x1 = x;
        var y1 = y;
        var x2 = x + w - 1;
        var y2 = y + h - 1;
        var ix1 = Math.Max(x1, gx1);
        var iy1 = Math.Max(y1, gy1);
        var ix2 = Math.Min(x2, gx2);
        var iy2 = Math.Min(y2, gy2);
        var iw = ix2 - ix1 + 1;
        var ih = iy2 - iy1 + 1;
        if (iw <= 0 || ih <= 0) return 0;
        double inter = (double)iw * ih;
        double a1 = (double)Math.Max(1, x2 - x1 + 1) * Math.Max(1, y2 - y1 + 1);
        double a2 = (double)Math.Max(1, gx2 - gx1 + 1) * Math.Max(1, gy2 - gy1 + 1);
        return inter / (a1 + a2 - inter);
    }
}
