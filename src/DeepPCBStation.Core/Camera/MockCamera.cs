using DeepPCBStation.Core.Abstractions;
using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Camera;

/// <summary>
/// 无文件时的合成灰度帧。主路径请用 PairedFilePlaybackCamera。
/// </summary>
public sealed class MockCamera : ICamera
{
    private readonly CameraConfig _cfg;
    private readonly Random _rng = new(42);
    private bool _opened;

    public MockCamera(CameraConfig? cfg = null) => _cfg = cfg ?? new CameraConfig();

    public string Name => "MockCamera";
    public bool IsOpened => _opened;
    public int FrameWidth => _cfg.Width;
    public int FrameHeight => _cfg.Height;

    public void Open() => _opened = true;
    public void Close() => _opened = false;

    public FrameBuffer Grab()
    {
        if (!_opened)
            throw new InvalidOperationException("相机未打开，请先调用 Open()。");
        if (_cfg.SimulateExposureMs > 0)
            Thread.Sleep(_cfg.SimulateExposureMs);

        var w = _cfg.Width;
        var h = _cfg.Height;
        var pixels = new byte[w * h];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 140;

        var noise = _cfg.NoiseLevel;
        if (noise > 0)
        {
            for (var i = 0; i < pixels.Length; i++)
            {
                var v = pixels[i] + _rng.Next(-noise, noise + 1);
                pixels[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }

        if (_rng.NextDouble() < _cfg.DefectProbability)
            InjectDefect(pixels, w, h);

        return new FrameBuffer(pixels, w, h);
    }

    private void InjectDefect(byte[] pixels, int w, int h)
    {
        var dark = _rng.NextDouble() < 0.6;
        var cx = _rng.Next(50, w - 50);
        var cy = _rng.Next(50, h - 50);
        var rx = _rng.Next(8, 28);
        var ry = _rng.Next(8, 28);
        var color = (byte)(dark ? _rng.Next(10, 45) : _rng.Next(220, 255));
        for (var y = cy - ry; y <= cy + ry; y++)
        for (var x = cx - rx; x <= cx + rx; x++)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            var dx = (x - cx) / (double)rx;
            var dy = (y - cy) / (double)ry;
            if (dx * dx + dy * dy <= 1.0)
                pixels[y * w + x] = color;
        }
    }

    public void Dispose() => Close();
}
