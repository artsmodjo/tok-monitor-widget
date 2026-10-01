using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Artsmodjo.Widget;

internal readonly record struct MonitorWorkArea(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}
internal sealed record WidgetMonitor(string DeviceName, MonitorWorkArea WorkingArea, bool Primary);
internal static class WidgetMonitors
{
    internal static WidgetMonitor[] AllScreens => Enumerate();
    internal static WidgetMonitor PrimaryScreen => Array.Find(AllScreens, screen => screen.Primary) ?? throw new InvalidOperationException("Windows reported no primary display.");
    private static WidgetMonitor[] Enumerate()
    {
        var monitors = new List<WidgetMonitor>();
        Exception? error = null;
        MonitorEnumProc callback = (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
            if (!GetMonitorInfoW(handle, ref info)) { error = new Win32Exception(Marshal.GetLastWin32Error()); return false; }
            // The app is PerMonitorV2: native rcWork coordinates use physical pixels and exclude appbars/taskbars.
            monitors.Add(new WidgetMonitor(info.DeviceName, new MonitorWorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw error ?? new Win32Exception(Marshal.GetLastWin32Error());
        return monitors.ToArray();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
}
