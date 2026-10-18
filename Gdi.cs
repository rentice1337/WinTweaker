using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Картинка в памяти, которую Windows копирует сама (BitBlt / AlphaBlend), — в разы быстрее,
/// чем рисовать её через GDI+ (DrawImage, особенно с полупрозрачностью). На ней держатся фон окна
/// и снимки карточек в анимациях.
/// </summary>
sealed class Dib : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int blend);

    readonly IntPtr dc, dib, old;
    public readonly Bitmap Bitmap;
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    public Dib(int w, int h)
    {
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = Math.Max(1, w), biHeight = -Math.Max(1, h), biPlanes = 1, biBitCount = 32 };
        dc = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(dc, ref bi, 0, out var bits, IntPtr.Zero, 0);
        old = SelectObject(dc, dib);
        Bitmap = new Bitmap(Math.Max(1, w), Math.Max(1, h), Math.Max(1, w) * 4, PixelFormat.Format32bppPArgb, bits);
    }

    /// <summary>Скопировать кусок (sx, sy, w, h) в g в точку (x, y) — без смешивания.</summary>
    public void Blit(Graphics g, int x, int y, int w, int h, int sx, int sy)
    {
        var hdc = g.GetHdc();
        try { BitBlt(hdc, x, y, w, h, dc, sx, sy, 0x00CC0020 /* SRCCOPY */); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>Нарисовать всю картинку в (x, y) с общей прозрачностью alpha (0…1).</summary>
    public void Blend(Graphics g, int x, int y, double alpha)
    {
        byte a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        if (a == 0) return;
        var hdc = g.GetHdc();
        try
        {
            if (a == 255) BitBlt(hdc, x, y, Width, Height, dc, 0, 0, 0x00CC0020);
            else AlphaBlend(hdc, x, y, Width, Height, dc, 0, 0, Width, Height, a << 16); // AC_SRC_OVER, постоянная прозрачность
        }
        finally { g.ReleaseHdc(hdc); }
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        SelectObject(dc, old);
        DeleteObject(dib);
        DeleteDC(dc);
    }
}
