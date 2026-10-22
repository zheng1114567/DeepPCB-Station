namespace DeepPCBStation.Core.Imaging;

/// <summary>
/// 灰度帧。DeepPCB 成对回放时 Pixels=test，TemplatePixels=temp。
/// </summary>
public sealed class FrameBuffer : IDisposable
{
    private bool _disposed;

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public string? SourcePath { get; }

    /// <summary>模板图（*_temp.jpg）像素；与 Pixels 同尺寸时有效。</summary>
    public byte[]? TemplatePixels { get; }
    public string? TemplatePath { get; }

    public bool HasTemplate =>
        TemplatePixels != null
        && TemplatePixels.Length >= Width * Height;

    public FrameBuffer(
        byte[] pixels,
        int width,
        int height,
        int? stride = null,
        string? sourcePath = null,
        byte[]? templatePixels = null,
        string? templatePath = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "宽高必须为正。");
        Stride = stride ?? width;
        var need = checked(Stride * height);
        if (pixels.Length < need)
            throw new ArgumentException($"像素缓冲不足：需要至少 {need} 字节，实际 {pixels.Length}。", nameof(pixels));
        Pixels = pixels;
        Width = width;
        Height = height;
        SourcePath = sourcePath;
        TemplatePixels = templatePixels;
        TemplatePath = templatePath;
    }

    public byte GetPixel(int x, int y) => Pixels[y * Stride + x];

    public FrameBuffer Clone()
    {
        var copy = new byte[Pixels.Length];
        Buffer.BlockCopy(Pixels, 0, copy, 0, Pixels.Length);
        byte[]? tpl = null;
        if (TemplatePixels != null)
        {
            tpl = new byte[TemplatePixels.Length];
            Buffer.BlockCopy(TemplatePixels, 0, tpl, 0, TemplatePixels.Length);
        }
        return new FrameBuffer(copy, Width, Height, Stride, SourcePath, tpl, TemplatePath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
