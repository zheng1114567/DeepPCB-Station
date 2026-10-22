using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Eval;

// 只读诊断：未命中 Pred 有多少像素能被 ±rad 邻域模板解释。
// 缺铜 test − max(temp)；多铜 min(temp) − test。不是成绩。
public sealed class PcbEdgeProfiler
{
    public void Run(string samplesDir, IDefectDetector detector, RecipeConfig recipe, int maxPairs, int rad = 2)
    {
        recipe.ContentHash = RecipeStoreHash(recipe);
        detector.ApplyRecipe(recipe);
        var thresh = recipe.DiffThreshold > 0 ? recipe.DiffThreshold : 30;

        var buckets = new Dictionary<string, int[]>();
        int fp = 0, tp = 0;

        foreach (var (stem, testPath, tempPath, annPath) in PcbGoldenEvaluator.DiscoverPairs(samplesDir).Take(maxPairs))
        {
            var gts = recipe.SixClass
                ? PcbGoldenEvaluator.LoadAllBoxes(annPath)
                : PcbGoldenEvaluator.LoadCommittedBoxes(annPath);
            using var test = ImageIo.LoadGray(testPath);
            using var temp = ImageIo.LoadGray(tempPath);
            var tempPx = temp.Pixels;
            if (recipe.RegisterShiftMax > 0)
            {
                tempPx = recipe.RegisterSubPixel
                    ? TileShiftRegistration.AlignSmooth(test.Pixels, temp.Pixels, test.Width, test.Height,
                        recipe.RegisterShiftMax, recipe.RegisterTiles, recipe.RegisterMinGain, out _)
                    : TileShiftRegistration.Align(test.Pixels, temp.Pixels, test.Width, test.Height,
                        recipe.RegisterShiftMax, recipe.RegisterTiles, recipe.RegisterMinGain, out _);
            }

            var frame = new FrameBuffer(test.Pixels, test.Width, test.Height, test.Stride, testPath,
                templatePixels: temp.Pixels, templatePath: tempPath);
            var outcome = detector.Detect(frame);
            if (outcome.IsFault) continue;
            var preds = outcome.Defects.ToList();
            var (predMatched, _) = PcbGoldenEvaluator.MatchOneToOne(preds, gts, PcbGoldenEvaluator.DefaultIouHit);
            for (var i = 0; i < preds.Count; i++)
            {
                var hit = predMatched[i];
                if (hit) tp++; else fp++;
                var frac = EdgeExplain.Fraction(preds[i], test.Pixels, tempPx, test.Width, test.Height, thresh, rad,
                    preds[i].TypeCode is "SHORT" or "COPPER" or "SPUR");
                var pol = preds[i].TypeCode is "SHORT" or "COPPER" or "SPUR" ? "neg" : "pos";
                var key = (hit ? "TP" : "FP") + "/" + pol;
                if (!buckets.TryGetValue(key, out var hist))
                    buckets[key] = hist = new int[6];
                var bin = frac >= 0.95 ? 5 : frac >= 0.8 ? 4 : frac >= 0.6 ? 3 : frac >= 0.4 ? 2 : frac >= 0.2 ? 1 : 0;
                hist[bin]++;
            }
        }

        Console.WriteLine($"edge-profile rad={rad} pairs≤{maxPairs} tp={tp} fp={fp}");
        foreach (var kv in buckets.OrderBy(k => k.Key))
        {
            var h = kv.Value;
            var n = h.Sum();
            Console.WriteLine(
                $"{kv.Key} n={n}  <0.2={h[0]}  0.2={h[1]}  0.4={h[2]}  0.6={h[3]}  ≥0.8={h[4]}  ≥0.95={h[5]}");
        }
    }

    private static string RecipeStoreHash(RecipeConfig recipe) =>
        DeepPCBStation.Core.Recipe.RecipeStore.ComputeHashStatic(recipe);
}
