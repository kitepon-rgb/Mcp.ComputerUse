using System.Runtime.InteropServices;
using Mcp.ComputerUse.Native;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Mcp.ComputerUse.Core;

public sealed class ScreenCaptureService
{
    private readonly MonitorRegistry _registry;
    private readonly CoordinateMapper _mapper;

    public ScreenCaptureService(MonitorRegistry registry, CoordinateMapper mapper)
    {
        _registry = registry;
        _mapper = mapper;
    }

    public sealed record CaptureResult(byte[] PngBytes, ScalePlan Plan, int OrigWidth, int OrigHeight);

    public CaptureResult CaptureMonitor(int monitorIndex, bool downscale, bool grayscale)
    {
        var monitor = _registry.GetOrThrow(monitorIndex);
        int w = monitor.Bounds.Width;
        int h = monitor.Bounds.Height;

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            screenDc = Win32.GetDC(IntPtr.Zero);
            memDc = Win32.CreateCompatibleDC(screenDc);
            bmp = Win32.CreateCompatibleBitmap(screenDc, w, h);
            oldBmp = Win32.SelectObject(memDc, bmp);
            if (!Win32.BitBlt(memDc, 0, 0, w, h, screenDc, monitor.Bounds.X, monitor.Bounds.Y, Win32.SRCCOPY | Win32.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed for monitor {monitorIndex}.");

            byte[] pixels = ReadBgra(memDc, bmp, w, h);
            using var img = Image.LoadPixelData<Bgra32>(pixels, w, h);

            var plan = downscale
                ? _mapper.PlanFor(w, h, monitor.Bounds.X, monitor.Bounds.Y)
                : new ScalePlan(w, h, w, h, 1.0, 1.0, monitor.Bounds.X, monitor.Bounds.Y, "NATIVE");

            if (downscale && (plan.ScaledWidth != w || plan.ScaledHeight != h))
                img.Mutate(c => c.Resize(plan.ScaledWidth, plan.ScaledHeight, KnownResamplers.Lanczos3));

            if (grayscale)
                img.Mutate(c => c.Grayscale(GrayscaleMode.Bt709));

            using var ms = new MemoryStream();
            img.Save(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression });
            return new CaptureResult(ms.ToArray(), plan, w, h);
        }
        finally
        {
            if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) Win32.SelectObject(memDc, oldBmp);
            if (bmp != IntPtr.Zero) Win32.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] ReadBgra(IntPtr memDc, IntPtr bmp, int w, int h)
    {
        var bmi = new BITMAPINFO
        {
            bmiColors = new byte[1024],
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            }
        };

        int stride = w * 4;
        var buffer = new byte[stride * h];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Win32.GetDIBits(memDc, bmp, 0, (uint)h, handle.AddrOfPinnedObject(), ref bmi, Win32.DIB_RGB_COLORS);
        }
        finally { handle.Free(); }
        return buffer;
    }
}
