using DeepPCBStation.Core.Detection;

namespace DeepPCBStation.Core.Imaging;

/// <summary>纯 C# 叠框：灰度 → BGR，画缺陷外接矩。</summary>
public static class OverlayRenderer
{
    public static byte[] RenderBgr(FrameBuffer gray, IReadOnlyList<DefectItem> defects)
    {
        var w = gray.Width;
        var h = gray.Height;
        var bgr = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var g = gray.GetPixel(x, y);
                var i = (y * w + x) * 3;
                bgr[i] = g;
                bgr[i + 1] = g;
                bgr[i + 2] = g;
            }
        }

        foreach (var d in defects)
        {
            // OPEN=红，MOUSEBITE=黄，其它=品红
            byte bb, gg, rr;
            if (string.Equals(d.TypeCode, "MOUSEBITE", StringComparison.OrdinalIgnoreCase))
            {
                bb = 0; gg = 255; rr = 255;
            }
            else if (string.Equals(d.TypeCode, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                bb = 0; gg = 0; rr = 255;
            }
            else
            {
                bb = 255; gg = 0; rr = 255;
            }
            DrawRect(bgr, w, h, d.X, d.Y, d.W, d.H, bb, gg, rr, 2);
        }
        return bgr;
    }

    private static void DrawRect(byte[] bgr, int w, int h, int x, int y, int rw, int rh,
        byte b, byte g, byte r, int thickness)
    {
        var x0 = Math.Max(0, Math.Min(x, w - 1));
        var y0 = Math.Max(0, Math.Min(y, h - 1));
        var x1 = Math.Max(0, Math.Min(x + Math.Max(rw, 1) - 1, w - 1));
        var y1 = Math.Max(0, Math.Min(y + Math.Max(rh, 1) - 1, h - 1));
        for (var t = 0; t < thickness; t++)
        {
            for (var xi = x0; xi <= x1; xi++)
            {
                Put(bgr, w, h, xi, y0 + t, b, g, r);
                Put(bgr, w, h, xi, y1 - t, b, g, r);
            }
            for (var yi = y0; yi <= y1; yi++)
            {
                Put(bgr, w, h, x0 + t, yi, b, g, r);
                Put(bgr, w, h, x1 - t, yi, b, g, r);
            }
        }
    }

    private static void Put(byte[] bgr, int w, int h, int x, int y, byte b, byte g, byte r)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        var i = (y * w + x) * 3;
        bgr[i] = b;
        bgr[i + 1] = g;
        bgr[i + 2] = r;
    }
}
