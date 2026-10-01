using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Artsmodjo.Widget;

internal sealed class NativeTray : IDisposable
{
    internal const int CallbackMessage = 0x8001;
    private readonly IntPtr _window;
    private readonly Action _toggle, _showContext;
    private readonly uint _taskbarCreated;
    private bool _added, _disposed;
    private readonly DispatcherTimer _shellRetry = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _shellRetryCount;
    internal uint TaskbarCreatedMessage => _taskbarCreated;
    internal bool IsRegistered => _added;
    internal NativeTray(IntPtr window, Action toggle, Action context)
    {
        _window = window; _toggle = toggle; _showContext = context;
        _shellRetry.Tick += (_, _) => { _shellRetry.Stop(); TryAddAfterShellRestart(); };
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal void TryAdd()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = CreateData(1 | 2 | 4 | 128);
        if (!Shell_NotifyIconW(0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the widget tray icon.");
        _added = true; data.Version = 4;
        // Version must be set after every add, including Explorer restart.
        if (!Shell_NotifyIconW(4, ref data))
        {
            _ = Shell_NotifyIconW(2, ref data); _added = false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Tray callback version is unavailable.");
        }
    }
    internal bool ProcessMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (_disposed) return false;
        if (unchecked((uint)message) == _taskbarCreated)
        {
            _added = false; _shellRetryCount = 0; _shellRetry.Stop();
            TryAddAfterShellRestart(); return true;
        }
        if (message != CallbackMessage || ((lParam.ToInt64() >> 16) & 0xffff) != 1) return false;
        // Version 4 packs the event in LOWORD and the icon ID in HIWORD of lParam.
        switch ((int)(lParam.ToInt64() & 0xffff))
        {
            case 0x400: case 0x401: _toggle(); return true;
            case 0x7b: SetForegroundWindow(_window); _showContext(); return true;
            default: return false;
        }
    }
    private void TryAddAfterShellRestart()
    {
        try { TryAdd(); _shellRetry.Stop(); }
        catch (Win32Exception) { if (_shellRetryCount++ < 3) _shellRetry.Start(); }
    }
    internal void ShowBalloon(string title, string message)
    {
        if (_disposed || !_added) return;
        var data = CreateData(16);
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = 0x82; // warning; honor the user's quiet-time settings.
        _ = Shell_NotifyIconW(1, ref data);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shellRetry.Stop();
        if (_added) { var data = CreateData(0); _ = Shell_NotifyIconW(2, ref data); _added = false; }
    }
    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1, Flags = flags, Callback = CallbackMessage,
        Icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)), // Shared system resource: never call DestroyIcon on this handle.
        Tip = "ARTSMODJO Monitoring Widget", Info = string.Empty, InfoTitle = string.Empty
    };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
