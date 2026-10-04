using System.Runtime.InteropServices;

namespace SongLink.Host.Adapters.UiaPick.Ocr;

/// <summary>
/// 屏幕截图（GDI BitBlt，纯 P/Invoke，无第三方依赖）。返回 BGRA 像素供 IOcrEngine 识别。
/// 真机使用；本文件可编译，调用需桌面会话（CI/无会话下不测）。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr hdcSrc, int x2, int y2, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpv, ref BITMAPINFOHEADER lpbmi, uint usage);

    private const int SRCCOPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes;
        public ushort biBitCount; public uint biCompression; public uint biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }

    public static (byte[] Bgra, int Width, int Height) CaptureFullScreen()
    {
        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            BitBlt(memDc, 0, 0, w, h, screenDc, 0, 0, SRCCOPY);
            var bih = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, // 负 = 自顶向下
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            var pixels = new byte[w * h * 4];
            GetDIBits(memDc, bmp, 0, (uint)h, pixels, ref bih, 0);
            return (pixels, w, h);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
