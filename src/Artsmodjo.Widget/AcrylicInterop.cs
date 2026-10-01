using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Artsmodjo.Widget;

public static class AcrylicInterop
{
    public static bool TrySet(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var policy = new AccentPolicy { State = enabled ? 4 : 0, Flags = enabled ? 2 : 0, GradientColor = enabled ? 0xCC000000u : 0 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new CompositionAttributeData { Attribute = 19, Data = pointer, SizeOfData = (nuint)size };
            return SetWindowCompositionAttribute(handle, ref data);
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public static bool UpdateRegion(Window window, Geometry visibleGeometry)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var flattened = visibleGeometry.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
        var points = new List<NativePoint>();
        var counts = new List<int>();
        var dpi = VisualTreeHelper.GetDpi(window);
        foreach (var figure in flattened.Figures)
        {
            if (!figure.IsClosed) continue;
            var startCount = points.Count;
            AddPoint(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment lines) foreach (var point in lines.Points) AddPoint(point);
                else if (segment is LineSegment line) AddPoint(line.Point);
            }
            counts.Add(points.Count - startCount);
        }
        if (points.Count < 3) return false;
        var region = CreatePolyPolygonRgn(points.ToArray(), counts.ToArray(), counts.Count, flattened.FillRule == FillRule.EvenOdd ? 1 : 2);
        if (region == IntPtr.Zero) return false;
        // SetWindowRgn transfers ownership to Windows on success. Free only a failed transfer.
        if (SetWindowRgn(handle, region, true) != 0) return true;
        DeleteObject(region); return false;
        void AddPoint(Point point)
        {
            var transformed = flattened.Transform.Transform(point);
            points.Add(new NativePoint { X = (int)Math.Round(transformed.X * dpi.DpiScaleX), Y = (int)Math.Round(transformed.Y * dpi.DpiScaleY) });
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State; public int Flags; public uint GradientColor; public int AnimationId; }
    // cbData is SIZE_T, so it must remain pointer-sized on the x64 build.
    [StructLayout(LayoutKind.Sequential)] private struct CompositionAttributeData { public int Attribute; public IntPtr Data; public nuint SizeOfData; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    // This Windows 10 composition hook is undocumented; failure keeps the opaque reference surface.
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowCompositionAttribute(IntPtr window, ref CompositionAttributeData data);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreatePolyPolygonRgn([In] NativePoint[] points, [In] int[] counts, int count, int fillMode);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
