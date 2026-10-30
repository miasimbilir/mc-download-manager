using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MCDM.App;

/// Each browser's own icon, read from its installed exe (nothing of theirs ships with the app). Where that does not
/// work (not Windows, the exe not found, an icon without the expected format) the globe stands in.
public static class BrowserIcons
{
    private static readonly Dictionary<string, Bitmap?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static Control View(BrowserKind kind, double size = 16)
    {
        if (For(kind) is { } bitmap)
            return new Image { Source = bitmap, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        return new PathIcon { Data = Icons.Get("IconGlobe"), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }

    public static Bitmap? For(BrowserKind kind)
    {
        if (!OperatingSystem.IsWindows() || kind.ExecutablePath is not { } exe) return null;
        if (cache.TryGetValue(exe, out var known)) return known;
        Bitmap? bitmap = null;
        try { bitmap = Extract(exe, 32); } catch { }
        cache[exe] = bitmap;
        return bitmap;
    }

    private static Bitmap? Extract(string path, int size)
    {
        if (SHDefExtractIcon(path, 0, 0, out var icon, out var small, (uint)(size | (size << 16))) != 0 || icon == IntPtr.Zero) return null;
        if (small != IntPtr.Zero) DestroyIcon(small);
        try
        {
            if (!GetIconInfo(icon, out var info)) return null;
            try
            {
                if (info.hbmColor == IntPtr.Zero) return null;     // a black-and-white icon
                var header = new BITMAPINFO { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
                var pixels = new byte[size * size * 4];
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    if (GetDIBits(dc, info.hbmColor, 0, (uint)size, pixels, ref header, 0) == 0) return null;
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                // An icon from before alpha channels: every pixel opaque.
                var hasAlpha = false;
                for (int i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
                if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

                var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using (var frame = bitmap.Lock())
                    for (int row = 0; row < size; row++)
                        Marshal.Copy(pixels, row * size * 4, frame.Address + row * frame.RowBytes, size * 4);
                return bitmap;
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            }
        }
        finally { DestroyIcon(icon); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    /// BITMAPINFOHEADER followed by room for the three colour masks GetDIBits may write.
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        public uint mask0, mask1, mask2, mask3;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
