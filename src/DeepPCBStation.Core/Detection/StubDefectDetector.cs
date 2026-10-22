using System.Diagnostics;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Detection;

/// <summary>
/// 仅保无 Halcon SDK 时编译 / CI / SoftPLC 冒烟。禁止作为交付演示卖点。
/// 有模板时做简易绝对值差分 Blob（不含配准/形态学/分型，只够跑通链路）；
/// 无模板时**不产出任何框**——宁可"没检出"，也绝不编一个。
/// </summary>
public sealed class StubDefectDetector : IDefectDetector
{
    private RecipeConfig _recipe = new();

    public string EngineName => "Stub";

    public void ApplyRecipe(RecipeConfig recipe) => _recipe = recipe;

    public DetectionOutcome Detect(FrameBuffer frame)
    {
        var sw = Stopwatch.StartNew();
        // 无模板 → 空结果。旧实现按文件名（含 "_test"）在画面中央造一个假缺陷框，
        // 那段代码一旦被真实路径碰到，产线上会看到一片"永远报 NG"的板，
        // 框的位置与图像内容无关且无从追溯。已删除：假框比没检出危险得多。
        var defects = frame.HasTemplate ? DiffBlobs(frame) : new List<DefectItem>();

        sw.Stop();
        return new DetectionOutcome
        {
            IsOk = defects.Count == 0,
            Defects = defects,
            EngineMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private List<DefectItem> DiffBlobs(FrameBuffer frame)
    {
        var thresh = _recipe.DiffThreshold > 0 ? _recipe.DiffThreshold : 30;
        var minArea = Math.Max(1, _recipe.MinBlobArea);
        var maxArea = _recipe.MaxBlobArea > 0 ? _recipe.MaxBlobArea : 20000;
        var w = frame.Width;
        var h = frame.Height;
        var mask = new bool[w * h];
        var tpl = frame.TemplatePixels!;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var a = frame.Pixels[y * frame.Stride + x];
                var b = tpl[y * w + x];
                mask[y * w + x] = Math.Abs(a - b) >= thresh;
            }
        }

        var visited = new bool[w * h];
        var defects = new List<DefectItem>();
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!mask[i] || visited[i]) continue;

                var q = new Queue<(int x, int y)>();
                q.Enqueue((x, y));
                visited[i] = true;
                var minX = x; var maxX = x; var minY = y; var maxY = y;
                var area = 0;
                var runs = new List<(int row, int c0, int c1)>();
                while (q.Count > 0)
                {
                    var (cx, cy) = q.Dequeue();
                    area++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                    foreach (var (dx, dy) in dirs)
                    {
                        var nx = cx + dx; var ny = cy + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        var ni = ny * w + nx;
                        if (!mask[ni] || visited[ni]) continue;
                        visited[ni] = true;
                        q.Enqueue((nx, ny));
                    }
                }

                if (area < minArea || area > maxArea) continue;
                var bw = maxX - minX + 1;
                var bh = maxY - minY + 1;
                for (var ry = minY; ry <= maxY; ry++)
                {
                    var c0 = -1; var c1 = -1;
                    for (var cx = minX; cx <= maxX; cx++)
                    {
                        if (mask[ry * w + cx])
                        {
                            if (c0 < 0) c0 = cx;
                            c1 = cx;
                        }
                    }
                    if (c0 >= 0) runs.Add((ry, c0, c1));
                }

                // 长宽比启发式：非可靠分类器（与 Halcon 路径同口径）；仅叠图。
                var aspect = bw >= bh ? (double)bw / bh : (double)bh / bw;
                defects.Add(new DefectItem
                {
                    TypeCode = aspect >= 2.2 ? "OPEN" : "MOUSEBITE",
                    X = minX,
                    Y = minY,
                    W = bw,
                    H = bh,
                    Score = area,
                    RunRows = runs.Select(r => r.row).ToArray(),
                    RunColBegin = runs.Select(r => r.c0).ToArray(),
                    RunColEnd = runs.Select(r => r.c1).ToArray()
                });
            }
        }
        return defects;
    }

}
