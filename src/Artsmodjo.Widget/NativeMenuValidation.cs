using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Artsmodjo.Widget;

internal static partial class NativeMenu
{
    // Opt-in benchmark/integration probe only. Production menus have no timer or automatic cancellation.
    internal static async Task<bool> ProbeAsync(Window owner, IReadOnlyList<MenuEntry> entries, Action? openPopup = null)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var uiThread = GetWindowThreadProcessId(hwnd, out var processId);
        if (uiThread == 0 || processId != (uint)Environment.ProcessId) throw new InvalidOperationException("Menu probe requires this process's own window.");
        var poll = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var seen = false;
            try
            {
                while (clock.ElapsedMilliseconds < 2000)
                {
                    var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                    if (GetGUIThreadInfo(uiThread, ref info) && (info.Flags & 0x14) != 0 && info.MenuOwner == hwnd)
                    { seen = true; break; }
                    await Task.Delay(10).ConfigureAwait(false);
                }
                return seen;
            }
            finally
            {
                // WM_CANCELMODE cancels the isolated owner's menu loop, including a probe timeout.
                PostMessageW(hwnd, 0x001F, IntPtr.Zero, IntPtr.Zero);
            }
        });
        try { if (openPopup is null) Show(owner, entries); else openPopup(); }
        finally { await poll; }
        return await poll;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
