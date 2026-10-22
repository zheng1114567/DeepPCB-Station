using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepPCBStation.Core.Detection;
using DeepPCBStation.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DeepPCBStation.Core.Inference;

/// <summary>
/// DL 分型（可选旁路）：对**已检出**的候选裁 patch → ResNet18 六分类 → 覆盖 TypeCode。
///
/// 边界（硬）：
///   · 只改类别标签，不改框、不改检出、不改置信度、不参与任何闸门；
///   · 默认不启用，由环境变量 DPCB_DL_TYPE=&lt;onnx 路径&gt; 打开，生产零变化；
///   · patch 生成参数必须与训练一致（Margin=12、DiffGain=2、letterbox 96、R/G/B 三通道）。
/// </summary>
public sealed class DlTypeClassifier : IDisposable
{
    public const int Size = 96;
    private const int PatchMargin = 12;
    private const int DiffGain = 2;

    private static readonly string[] Classes =
        { "OPEN", "SHORT", "MOUSEBITE", "SPUR", "COPPER", "PINHOLE" };

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public DlTypeClassifier(string modelPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("ONNX 模型不存在", modelPath);
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    /// <summary>按环境变量尝试创建；未配置或加载失败返回 null（不影响检测主线）。</summary>
    public static DlTypeClassifier? TryCreateFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("DPCB_DL_TYPE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return new DlTypeClassifier(path);
        }
        catch (Exception ex)
        {
            // 库不直接写控制台（上位机里看不见，CLI 里和评测报告抢 stdout）；
            // 走统一日志，宿主装了 sink 才落盘。回退行为不变。
            Diagnostics.StationLog.Warn(nameof(DlTypeClassifier),
                $"DPCB_DL_TYPE 模型加载失败，回退规则分型: {ex.Message}");
            return null;
        }
    }

    /// <summary>把类别与置信度写回候选。返回被改动的候选数。TypeConfidence 仅作分档参考，不参与判定。</summary>
    public int Apply(FrameBuffer frame, List<DefectItem> defects)
    {
        if (defects.Count == 0 || !frame.HasTemplate) return 0;
        var changed = 0;
        foreach (var d in defects)
        {
            var pred = Predict(frame, d);
            if (pred == null) continue;
            d.TypeConfidence = pred.Value.Probability;
            if (!string.Equals(pred.Value.Type, d.TypeCode, StringComparison.Ordinal))
            {
                d.TypeCode = pred.Value.Type;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>分型结果：类别 + softmax 置信度（本模型在 official_test 上 ECE=0.46%，可直接当可信度用）。</summary>
    public readonly struct TypePrediction
    {
        public TypePrediction(string type, double probability)
        {
            Type = type;
            Probability = probability;
        }

        public string Type { get; }
        public double Probability { get; }
    }

    private TypePrediction? Predict(FrameBuffer frame, DefectItem d)
    {
        var w = frame.Width;
        var h = frame.Height;
        var x1 = Math.Max(0, d.X - PatchMargin);
        var y1 = Math.Max(0, d.Y - PatchMargin);
        var x2 = Math.Min(w - 1, d.X + d.W - 1 + PatchMargin);
        var y2 = Math.Min(h - 1, d.Y + d.H - 1 + PatchMargin);
        var pw = x2 - x1 + 1;
        var ph = y2 - y1 + 1;
        if (pw <= 0 || ph <= 0) return null;

        // ---- 1. 原分辨率合成三通道（BGR，与 PcbCandidateExporter 完全一致）----
        var bgr = new byte[pw * ph * 3];
        var pixels = frame.Pixels;
        var stride = frame.Stride;
        var tpl = frame.TemplatePixels!;
        for (var y = 0; y < ph; y++)
        {
            var rt = (y1 + y) * stride;
            var rm = (y1 + y) * w;      // 模板为紧凑排布
            for (var x = 0; x < pw; x++)
            {
                var tv = pixels[rt + x1 + x];
                var mv = tpl[rm + x1 + x];
                var diff = Math.Abs(tv - mv) * DiffGain;
                if (diff > 255) diff = 255;
                var p = (y * pw + x) * 3;
                bgr[p] = (byte)diff;    // B
                bgr[p + 1] = mv;        // G
                bgr[p + 2] = tv;        // R
            }
        }

        // ---- 2. letterbox 到 96×96（保持长宽比，居中，黑边）----
        var scale = Math.Min((double)Size / pw, (double)Size / ph);
        var nw = Math.Max(1, (int)Math.Round(pw * scale));
        var nh = Math.Max(1, (int)Math.Round(ph * scale));
        var ox = (Size - nw) / 2;
        var oy = (Size - nh) / 2;

        var tensor = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        for (var dy = 0; dy < nh; dy++)
        {
            var fy = (dy + 0.5) / scale - 0.5;
            var sy = (int)Math.Floor(fy);
            var ay = fy - sy;
            var ya = Clamp(y1 + sy, y1, y2);
            var yb = Clamp(ya + 1, y1, y2);
            for (var dx = 0; dx < nw; dx++)
            {
                var fx = (dx + 0.5) / scale - 0.5;
                var sx = (int)Math.Floor(fx);
                var ax = fx - sx;
                var xa = Clamp(x1 + sx, x1, x2);
                var xb = Clamp(xa + 1, x1, x2);

                // bgr 布局为 [0]=diff [1]=模板 [2]=实测；训练侧 PIL 读 PNG 后是 RGB，
                // 即通道 0=实测、1=模板、2=diff —— 这里必须按训练语义重排，不能按数组顺序。
                var ch0 = Bilinear(bgr, 2, pw, xa, xb, ya, yb, ax, ay, x1, y1); // 实测
                var ch1 = Bilinear(bgr, 1, pw, xa, xb, ya, yb, ax, ay, x1, y1); // 模板
                var ch2 = Bilinear(bgr, 0, pw, xa, xb, ya, yb, ax, ay, x1, y1); // |diff|

                tensor[0, 0, oy + dy, ox + dx] = ch0 / 255f;
                tensor[0, 1, oy + dy, ox + dx] = ch1 / 255f;
                tensor[0, 2, oy + dy, ox + dx] = ch2 / 255f;
            }
        }

        // ---- 3. 推理 ----
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };
        using var results = _session.Run(inputs);
        var logits = results.First(v => v.Name == _outputName).AsTensor<float>();
        var best = 0;
        var bestVal = logits[0, 0];
        for (var c = 1; c < Classes.Length; c++)
        {
            if (logits[0, c] > bestVal) { bestVal = logits[0, c]; best = c; }
        }

        // softmax：减去最大值保证数值稳定；best 类的概率即置信度
        var sum = 0.0;
        for (var c = 0; c < Classes.Length; c++) sum += Math.Exp(logits[0, c] - bestVal);
        var probability = 1.0 / sum;
        return new TypePrediction(Classes[best], probability);
    }

    /// <summary>在 patch 坐标系（pw 宽）内做双线性；xa/xb/ya/yb 是原图坐标。</summary>
    private static float Bilinear(byte[] bgr, int channel, int pw, int xa, int xb, int ya, int yb,
        double ax, double ay, int x1, int y1)
    {
        float At(int px, int py) => bgr[((py - y1) * pw + (px - x1)) * 3 + channel];
        var top = At(xa, ya) + (At(xb, ya) - At(xa, ya)) * (float)ax;
        var bot = At(xa, yb) + (At(xb, yb) - At(xa, yb)) * (float)ax;
        return top + (bot - top) * (float)ay;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    public void Dispose() => _session.Dispose();
}
