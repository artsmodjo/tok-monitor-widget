using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Artsmodjo.Widget;
internal static class NativeWindowCapture
{
    internal static void SavePng(IntPtr window, string path)
    {
        var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4)); // Capture physical pixels in PerMonitorV2 context.
        if (previousDpi == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (!GetWindowRect(window, out var rect)) Fail("GetWindowRect");
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            var stride = checked(width * 4); var bytes = checked(stride * height);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Window has no bounds.");
            screen = GetDC(IntPtr.Zero); if (screen == IntPtr.Zero) Fail("GetDC");
            memory = CreateCompatibleDC(screen); if (memory == IntPtr.Zero) Fail("CreateCompatibleDC");
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, SizeImage = (uint)bytes } };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) Fail("CreateDIBSection");
            old = SelectObject(memory, bitmap); if (old == IntPtr.Zero || old == new IntPtr(-1)) Fail("SelectObject");
            // CAPTUREBLT includes the widget's layered HWND; a plain SRCCOPY can omit it.
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 | 0x40000000)) Fail("BitBlt");
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bits, bytes, stride);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = File.Create(path); encoder.Save(output);
        }
        finally
        {
            // Restore the old selected bitmap before deleting our DIB. The screen DC is borrowed.
            if (old != IntPtr.Zero && old != new IntPtr(-1) && memory != IntPtr.Zero) _ = SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memory != IntPtr.Zero) _ = DeleteDC(memory);
            if (screen != IntPtr.Zero) _ = ReleaseDC(IntPtr.Zero, screen);
            _ = SetThreadDpiAwarenessContext(previousDpi);
        }
    }
    private static void Fail(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
