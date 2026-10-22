using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 读图/写 PNG（ImageSharp）。仅作帧搬运与叠图落盘，不是检测引擎。
/// </summary>
public static class ImageIo
{
    public static FrameBuffer LoadGray(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        img.Mutate(x => x.Grayscale());
        var w = img.Width;
        var h = img.Height;
        var pixels = new byte[w * h];
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                var dest = y * w;
                for (var x = 0; x < w; x++)
                    pixels[dest + x] = row[x].R;
            }
        });
        return new FrameBuffer(pixels, w, h, w, path);
    }

    public static void SaveGrayPng(string path, FrameBuffer gray)
    {
        using var img = new Image<L8>(gray.Width, gray.Height);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < gray.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < gray.Width; x++)
                    row[x] = new L8(gray.GetPixel(x, y));
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        img.SaveAsPng(path);
    }

    public static void SaveBgrPng(string path, byte[] bgr, int width, int height)
    {
        if (bgr.Length < width * height * 3)
            throw new ArgumentException("BGR 缓冲不足。");
        using var img = new Image<Rgb24>(width, height);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var src = y * width * 3;
                for (var x = 0; x < width; x++)
                {
                    var i = src + x * 3;
                    row[x] = new Rgb24(bgr[i + 2], bgr[i + 1], bgr[i]); // BGR→RGB
                }
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        img.SaveAsPng(path);
    }
}
