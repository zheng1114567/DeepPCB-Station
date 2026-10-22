using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Camera;

/// <summary>
/// DeepPCB 成对回放：扫描 *_test.jpg，配对同 stem 的 *_temp.jpg。
/// Manifest 每行写 stem 或 test 文件名均可。
/// </summary>
public sealed class PairedFilePlaybackCamera : IPlaybackCamera
{
    private readonly CameraConfig _cfg;
    private readonly List<(string Test, string Temp)> _pairs = new();
    private int _index;
    private bool _opened;
    private int _w;
    private int _h;

    public PairedFilePlaybackCamera(CameraConfig cfg) => _cfg = cfg ?? new CameraConfig();

    public string Name => "PairedFilePlaybackCamera";
    public bool IsOpened => _opened;
    public int FrameWidth => _w;
    public int FrameHeight => _h;
    public int FrameCount => _pairs.Count;
    public int CurrentIndex => _index;

    public void Open()
    {
        _pairs.Clear();
        _index = 0;
        var root = _cfg.PlaybackRootDir;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Camera.PlaybackRootDir 未配置。");

        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"回放目录不存在: {root}");

        var stems = new List<string>();
        if (!string.IsNullOrWhiteSpace(_cfg.PlaybackManifest))
        {
            var man = Path.IsPathRooted(_cfg.PlaybackManifest)
                ? _cfg.PlaybackManifest
                : Path.Combine(root, _cfg.PlaybackManifest);
            if (!File.Exists(man))
                throw new FileNotFoundException("Manifest 不存在: " + man);
            foreach (var line in File.ReadAllLines(man))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                stems.Add(NormalizeStem(t));
            }
        }
        else
        {
            foreach (var p in Directory.EnumerateFiles(root, "*_test.jpg", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                stems.Add(NormalizeStem(p));
        }

        foreach (var stem in stems.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var test = ResolvePairFile(root, stem, "_test.jpg");
            var temp = ResolvePairFile(root, stem, "_temp.jpg");
            if (test == null || temp == null) continue;
            _pairs.Add((test, temp));
        }

        if (_pairs.Count == 0)
            throw new InvalidOperationException($"回放目录无可用成对图 (*_temp.jpg / *_test.jpg): {root}");

        using var first = ImageIo.LoadGray(_pairs[0].Test);
        _w = first.Width;
        _h = first.Height;
        _opened = true;
    }

    public void Close()
    {
        _opened = false;
        _pairs.Clear();
        _index = 0;
    }

    public void ResetPlayback() => _index = 0;

    public FrameBuffer Grab()
    {
        if (!_opened)
            throw new InvalidOperationException("相机未打开，请先调用 Open()。");
        if (_index >= _pairs.Count)
        {
            if (_cfg.PlaybackLoop)
                _index = 0;
            else
                throw new CameraNoImageException("NO_IMAGE: 回放已耗尽（可配置 playbackLoop=true）。");
        }

        var (testPath, tempPath) = _pairs[_index++];
        var test = ImageIo.LoadGray(testPath);
        var temp = ImageIo.LoadGray(tempPath);
        if (temp.Width != test.Width || temp.Height != test.Height)
        {
            temp.Dispose();
            test.Dispose();
            throw new InvalidOperationException($"成对图尺寸不一致: {testPath} vs {tempPath}");
        }

        _w = test.Width;
        _h = test.Height;
        // 消费 temp 像素进 FrameBuffer，再释放 temp 外壳
        var frame = new FrameBuffer(
            test.Pixels, test.Width, test.Height, test.Stride, testPath,
            templatePixels: temp.Pixels, templatePath: tempPath);
        test.Dispose();
        temp.Dispose();
        return frame;
    }

    private static string NormalizeStem(string pathOrStem)
    {
        var name = Path.GetFileName(pathOrStem.Trim().Replace('\\', '/'));
        if (name.EndsWith("_test.jpg", StringComparison.OrdinalIgnoreCase))
            return name.Substring(0, name.Length - "_test.jpg".Length);
        if (name.EndsWith("_temp.jpg", StringComparison.OrdinalIgnoreCase))
            return name.Substring(0, name.Length - "_temp.jpg".Length);
        if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(name);
        return name;
    }

    private static string? ResolvePairFile(string root, string stem, string suffix)
    {
        var direct = Path.Combine(root, stem + suffix);
        if (File.Exists(direct)) return direct;
        var hits = Directory.EnumerateFiles(root, stem + suffix, SearchOption.AllDirectories).Take(1).ToList();
        return hits.Count > 0 ? hits[0] : null;
    }

    public void Dispose() => Close();
}
