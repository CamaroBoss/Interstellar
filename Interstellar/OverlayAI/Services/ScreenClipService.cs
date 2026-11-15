using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OverlayAI.Services;

internal sealed record ScreenClip(byte[] Jpeg, BitmapSource Preview, int Width, int Height);

/// <summary>Fast one-shot GDI capture of the primary monitor. No continuous capture session is created.</summary>
internal static class ScreenClipService
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public static ScreenClip CapturePrimaryScreen()
    {
        // Match the original quick-clip behavior: capture the primary monitor only.
        const int left = 0;
        const int top = 0;
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
            throw new InvalidOperationException("Windows returned an invalid desktop size.");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("Could not access the desktop display.");

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        IntPtr pixelBuffer = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) throw new InvalidOperationException("Could not create a screen capture context.");
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the screen capture bitmap.");
            previous = SelectObject(memoryDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new InvalidOperationException("Could not select the screen bitmap.");

            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("Windows could not copy the desktop pixels.");

            // GetDIBits requires the bitmap to be deselected from every device context.
            SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;

            int stride = checked(width * 4);
            int byteCount = checked(stride * height);
            pixelBuffer = Marshal.AllocHGlobal(byteCount);
            var info = new BITMAPINFO
            {
                Header = new BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    Width = width,
                    Height = -height, // top-down BGRA rows
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB,
                    SizeImage = (uint)byteCount
                }
            };

            int rows = GetDIBits(screenDc, bitmap, 0, (uint)height, pixelBuffer, ref info, DIB_RGB_COLORS);
            if (rows != height) throw new InvalidOperationException("Windows returned an incomplete screen image.");

            var pixels = new byte[byteCount];
            Marshal.Copy(pixelBuffer, pixels, 0, byteCount);
            // BI_RGB does not define alpha; make every BGRA pixel fully opaque for WPF and clipboard use.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            var preview = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            preview.Freeze();

            // Keep the full-resolution preview on the clipboard, but send a lighter AI image.
            // Large 4K / multi-monitor screenshots otherwise add noticeable upload and inference latency.
            BitmapSource aiSource = preview;
            double scale = Math.Min(1.0, 2560.0 / Math.Max(width, height));
            if (scale < 1.0)
            {
                var resized = new TransformedBitmap(preview, new ScaleTransform(scale, scale));
                resized.Freeze();
                aiSource = resized;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(aiSource));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new ScreenClip(stream.ToArray(), preview, width, height);
        }
        finally
        {
            if (pixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pixelBuffer);
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines, IntPtr bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
}
