using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Artsmodjo.Widget;

internal sealed record MenuEntry(string Header, Action? Action = null,
    Func<bool>? IsChecked = null, Func<IReadOnlyList<MenuEntry>>? Children = null);

internal static partial class NativeMenu
{
    internal static void Show(Window owner, IReadOnlyList<MenuEntry> entries)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var actions = new Dictionary<uint, Action>();
        uint nextId = 1, selected = 0;
        try
        {
            Populate(menu, entries, actions, ref nextId);
            if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // The foreground owner lets clicking outside dismiss a notification-area popup.
            SetForegroundWindow(hwnd);
            selected = TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, point.X, point.Y, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { DestroyMenu(menu); } // DestroyMenu also frees every attached submenu.
        if (selected != 0 && actions.TryGetValue(selected, out var action)) action();
    }

    private static void Populate(IntPtr menu, IReadOnlyList<MenuEntry> entries,
        Dictionary<uint, Action> actions, ref uint nextId)
    {
        foreach (var entry in entries)
        {
            if (entry.Header == "-")
            {
                if (!AppendMenuW(menu, 0x800, UIntPtr.Zero, null)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (entry.Children is not null)
            {
                var child = CreatePopupMenu();
                if (child == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Populate(child, entry.Children(), actions, ref nextId);
                    if (!AppendMenuW(menu, 0x10, (UIntPtr)child.ToInt64(), entry.Header))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                catch { DestroyMenu(child); throw; }
            }
            else
            {
                var id = nextId++;
                if (entry.Action is Action action) actions.Add(id, action);
                var flags = (entry.IsChecked?.Invoke() == true ? 8u : 0u) | (entry.Action is null ? 1u : 0u);
                if (!AppendMenuW(menu, flags, (UIntPtr)id, entry.Header)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string? text);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
