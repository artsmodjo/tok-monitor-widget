using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Artsmodjo.Core;

namespace Artsmodjo.Widget;

internal static class RuntimeValidation
{
    private static string? _diagnosticPath;
    internal static void ConfigureDiagnostics(string directory)
    {
        Directory.CreateDirectory(directory);
        _diagnosticPath = Path.Combine(directory, "diagnostics.log");
        Mark($"start pid={Environment.ProcessId}");
    }
    internal static void RecordDiagnostic(string source, Exception error) => Mark(source + ": " + error);
    internal static void Mark(string stage)
    {
        try { if (_diagnosticPath is not null) File.AppendAllText(_diagnosticPath, $"{DateTimeOffset.UtcNow:O} {stage}{Environment.NewLine}"); }
        catch { }
    }
    internal static async Task<int> RunAsync(string outputDirectory)
    {
        var report = new ValidationReport { StartedAtUtc = DateTimeOffset.UtcNow };
        MainWindow? window = null;
        Window? background = null;
        Directory.CreateDirectory(outputDirectory);
        var settingsPath = Path.Combine(outputDirectory, "isolated-settings.json");
        var store = new SettingsStore(settingsPath);
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var settings = new WidgetSettings { IsTopmost = false, AcrylicEnabled = false, DemoDataEnabled = false, ReducedMotion = true, Edge = WidgetEdge.Left, MonitorDevice = "validation-monitor", NormalizedY = .37, IsPinned = true };
            Mark("before settings save"); await store.SaveAsync(settings); Mark("after settings save");
            var saved = store.Load();
            Check(!saved.IsTopmost && !saved.AcrylicEnabled && !saved.DemoDataEnabled && saved.ReducedMotion && saved.Edge == WidgetEdge.Left && saved.MonitorDevice == "validation-monitor" && saved.NormalizedY == .37 && saved.IsPinned, "Settings round trip", report);
            Mark("before constructor"); window = new MainWindow(new WidgetSettings(), store); Mark("after constructor");
            System.Windows.Application.Current.MainWindow = window;
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ContentRendered += (_, _) => rendered.TrySetResult();
            Mark("before Show"); window.Show(); Mark("after Show");
            if (await Task.WhenAny(rendered.Task, Task.Delay(15000)) != rendered.Task) throw new TimeoutException("Window did not render.");
            await rendered.Task; Mark("rendered");
            Check(await window.ProbeStartupReadinessAsync(), "Native tray registered, Windows popup opened, and ring automation accepts input", report);
            var vm = window.ViewModel;
            Check(window.UsageCard.ToolTip is null && window.ClaudeRing.ToolTip is null && window.CodexRing.ToolTip is null && window.RamRing.ToolTip is null, "Fixture elements have no WPF tooltips", report);
            Check(FrameworkElementAutomationPeer.CreatePeerForElement(window.ClaudeRing)?.GetHelpText() == "Claude — reference fixture" &&
                  FrameworkElementAutomationPeer.CreatePeerForElement(window.CodexRing)?.GetHelpText() == "Codex — reference fixture" &&
                  FrameworkElementAutomationPeer.CreatePeerForElement(window.RamRing)?.GetHelpText() == "RAM — reference fixture; no system sampling", "Ring fixture guidance remains available as automation help text", report);
            vm.Close(force: true); window.ApplyState(false);
            await Task.Delay(220);
            Check(window.IsVisible && Math.Abs(window.ActualWidth - WidgetLayout.CollapsedWidth * window.WidgetScale) < 1, $"Visible collapsed window (width {window.ActualWidth}, expected {WidgetLayout.CollapsedWidth * window.WidgetScale}, railLeft {System.Windows.Controls.Canvas.GetLeft(window.RailCanvas)})", report);
            Mark($"collapsed layout left={System.Windows.Controls.Canvas.GetLeft(window.RailCanvas)} rootWidth={window.RootCanvas.Width} windowWidth={window.ActualWidth}");
            CaptureMemorySnapshot(report, "ready-collapsed-settled");
            CaptureWindow(window, Path.Combine(outputDirectory, "window-collapsed.png"));
            CaptureRendered(window, Path.Combine(outputDirectory, "collapsed.png"), 1);
            vm.IsPinned = true; vm.Open(0); await WaitForWidth(window, WidgetLayout.ExpandedWidth);
            Check(Math.Abs(window.ActualWidth - WidgetLayout.ExpandedWidth * window.WidgetScale) < 1, $"Expand animation reaches final width (actual {window.ActualWidth:0.##}, expanded {vm.IsExpanded})", report);
            vm.IsPinned = true; vm.Close(); await Task.Delay(220);
            Check(vm.IsExpanded, "Pin preserves open card", report);
            CaptureRendered(window, Path.Combine(outputDirectory, "pinned.png"), 1);
            vm.IsPinned = false; vm.Close(); await WaitForWidth(window, WidgetLayout.CollapsedWidth);
            Check(Math.Abs(window.ActualWidth - WidgetLayout.CollapsedWidth * window.WidgetScale) < 1, "Collapse animation reaches final width", report);
            CaptureMemorySnapshot(report, "after-expand-collapse-and-screenshots");
            vm.IsPinned = true; vm.Open(0); await WaitForWidth(window, WidgetLayout.ExpandedWidth);
            var primary = WidgetMonitors.PrimaryScreen;
            vm.Edge = WidgetEdge.Left; window.ApplyState(false);
            Check(Math.Abs(window.PointToScreen(new Point()).X - primary.WorkingArea.Left) < 2, "Left docking at work area edge", report);
            CaptureRendered(window, Path.Combine(outputDirectory, "dock-left.png"), 1);
            vm.Edge = WidgetEdge.Right; window.ApplyState(false);
            Check(Math.Abs(window.PointToScreen(new Point(window.ActualWidth, 0)).X - primary.WorkingArea.Right) < 2, "Right docking at work area edge", report);
            ValidateRegion(window, report);
            vm.IsTopmost = false; Check(!window.Topmost, "Topmost off", report);
            vm.IsTopmost = true; Check(window.Topmost, "Topmost on", report);
            vm.DemoDataEnabled = false;
            Check(window.ClaudeRing.Percent is null && window.CodexRing.Percent is null && window.RamRing.Percent is null && window.SessionFill.Width == 0, "Disabled fixture displays unknown", report);
            CaptureRendered(window, Path.Combine(outputDirectory, "unknown.png"), 1);
            vm.DemoDataEnabled = true;
            Check(window.CardTitle.Text == "Claude Usage · Demo", "Demo fixture labels Claude card title", report);
            vm.SelectedIndicator = WidgetIndicator.Ram;
            Check(window.CardTitle.Text == "RAM reference · Demo", "Demo fixture labels RAM card title", report);
            vm.SelectedIndicator = WidgetIndicator.Claude;
            var toggle = window.TrayMenu.Single(item => item.Header == "Show / Hide");
            toggle.Action!(); Check(!window.IsVisible, "Tray hides window", report);
            toggle.Action!(); Check(window.IsVisible, "Tray restores window", report);
            window.TrayMenu.Single(item => item.Header == "Dock Left").Action!();
            Check(vm.Edge == WidgetEdge.Left, "Tray docks left", report);
            window.TrayMenu.Single(item => item.Header == "Dock Right").Action!();
            Check(vm.Edge == WidgetEdge.Right, "Tray docks right", report);
            foreach (var down in new[] { true, false })
            {
                var start = window.PointToScreen(new Point(window.ActualWidth - 40, 114));
                window.BeginRailDrag(start);
                window.MoveRailDrag(new Point(start.X, start.Y + (down ? 100001 : -100001)));
                await window.EndRailDragAsync(null);
                Check(Math.Abs(vm.NormalizedY - (down ? 1 : 0)) < .0001, down ? "Drag clamps bottom" : "Drag clamps top", report);
                Mark("before final save"); await window.SaveSettingsAsync(); Mark("after final save");
                Check(store.Load().NormalizedY == vm.NormalizedY, "Drag position persists", report);
            }
            var backgroundDrawing = new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 32, 32)));
            var group = new DrawingGroup(); group.Children.Add(backgroundDrawing);
            group.Children.Add(new GeometryDrawing(Brushes.DarkCyan, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            group.Children.Add(new GeometryDrawing(Brushes.DarkCyan, null, new RectangleGeometry(new Rect(16, 16, 16, 16))));
            var tiles = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 32, 32), ViewportUnits = BrushMappingMode.Absolute };
            background = new Window { Title = "Widget validation background", Topmost = true, Left = window.Left, Top = window.Top, Width = window.Width, Height = window.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = tiles };
            background.Show(); window.Owner = background;
            window.Activate();
            vm.AcrylicEnabled = false; await Task.Delay(300);
            Check(!window.AcrylicApplied, "Acrylic disabled state", report);
            CaptureWindow(window, Path.Combine(outputDirectory, "window-acrylic-off.png"));
            vm.AcrylicEnabled = true; await Task.Delay(300);
            report.AcrylicNativeAccepted = window.AcrylicApplied;
            CaptureWindow(window, Path.Combine(outputDirectory, "window-acrylic-on.png"));
            CheckAcrylicShape(Path.Combine(outputDirectory, "window-acrylic-off.png"), Path.Combine(outputDirectory, "window-acrylic-on.png"), window.ActualWidth, window.ActualHeight, window.WidgetScale, report);
            vm.AcrylicEnabled = false;
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 }) CaptureRendered(window, Path.Combine(outputDirectory, $"render-{scale:0.00}x.png"), scale);
            foreach (var percent in new double?[] { 0, 100, 125, null })
            {
                window.ClaudeRing.Percent = percent;
                CaptureRendered(window, Path.Combine(outputDirectory, $"ring-{percent?.ToString() ?? "unknown"}.png"), 1);
            }
            window.ClaudeRing.Percent = 73;
            window.ClaudeRing.Focus();
            CaptureRendered(window, Path.Combine(outputDirectory, "focus.png"), 1);
            await window.SaveSettingsAsync();
            await CheckNativeTrayCallbacksAsync(window, report);
            await CheckEscapeAsync(window, report);
            await CheckReadonlySettingsAndMonitorFallbackAsync(outputDirectory, report);
            report.Success = true;
        }
        catch (Exception error) { RecordDiagnostic("HarnessFailure", error); report.Errors.Add(error.ToString()); }
        finally
        {
            if (window is not null)
            {
                Mark("before final save"); await window.SaveSettingsAsync(); Mark("after final save");
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult(); window.Close();
                await Task.WhenAny(closed.Task, Task.Delay(3000)); window.Dispose();
            }
            background?.Close();
            report.FinishedAtUtc = DateTimeOffset.UtcNow;
            Mark("writing report"); File.WriteAllText(Path.Combine(outputDirectory, "runtime-validation-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return report.Success ? 0 : 1;
    }
    private static async Task CheckNativeTrayCallbacksAsync(MainWindow window, ValidationReport report)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Window has no HWND for tray callback validation.");
        var wasVisible = window.IsVisible;
        SendMessageW(hwnd, NativeTray.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | 0x0400));
        await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Background);
        Check(window.IsVisible != wasVisible, "Native NIN_SELECT toggled visibility through the HwndSource hook", report);
        SendMessageW(hwnd, NativeTray.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | 0x0400));
        await window.Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.Background);
        Check(window.IsVisible == wasVisible, "Second native NIN_SELECT restored visibility", report);
        Check(await NativeMenu.ProbeAsync(window, window.TrayMenu, () => SendMessageW(hwnd, NativeTray.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | 0x007B))),
            "Native WM_CONTEXTMENU opened the actual Windows popup menu", report);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    private static async Task CheckEscapeAsync(MainWindow window, ValidationReport report)
    {
        window.ViewModel.IsPinned = true; window.ViewModel.Open(0);
        await WaitForWidth(window, WidgetLayout.ExpandedWidth);
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no presentation source.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
        window.RaiseEvent(args);
        await WaitForWidth(window, WidgetLayout.CollapsedWidth);
        Check(!window.ViewModel.IsPinned && !window.ViewModel.IsExpanded && args.Handled, "Escape unpins and closes through the production key handler", report);
    }
    private static async Task CheckReadonlySettingsAndMonitorFallbackAsync(string outputDirectory, ValidationReport report)
    {
        var path = Path.Combine(outputDirectory, "future-schema-settings.json");
        var futureJson = JsonSerializer.Serialize(new WidgetSettings { SchemaVersion = 2, MonitorDevice = "future-schema-sentinel" });
        await File.WriteAllTextAsync(path, futureJson);
        var store = new SettingsStore(path);
        var rejected = false;
        try { _ = store.Load(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "SettingsStore rejected a newer schema version", report);
        var originalMainWindow = System.Windows.Application.Current.MainWindow;
        var fallback = new MainWindow(new WidgetSettings { MonitorDevice = "validation-unplugged", NormalizedY = 0.37 }, store, canPersist: false);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fallback.ContentRendered += (_, _) => rendered.TrySetResult();
        fallback.Closed += (_, _) => closed.TrySetResult();
        try
        {
            fallback.Show();
            if (await Task.WhenAny(rendered.Task, Task.Delay(10000)) != rendered.Task) throw new TimeoutException("Readonly fallback window did not render.");
            var primary = WidgetMonitors.PrimaryScreen;
            var dpi = VisualTreeHelper.GetDpi(fallback).DpiScaleY;
            Check(fallback.ViewModel.MonitorDevice == primary.DeviceName, "Missing saved monitor fell back to the primary monitor", report);
            var expectedTop = GeometryMath.RestoreTopFromNormalized(0.37, primary.WorkingArea.Top / dpi, primary.WorkingArea.Height / dpi, fallback.Height);
            Check(Math.Abs(fallback.Top - expectedTop) <= 2, "Fallback restored normalized position using the actual display DPI", report);
            await fallback.SaveSettingsAsync();
            Check(await File.ReadAllTextAsync(path) == futureJson, "Readonly settings preserved by explicit save", report);
            fallback.Close();
            if (await Task.WhenAny(closed.Task, Task.Delay(10000)) != closed.Task) throw new TimeoutException("Readonly fallback window did not close.");
            Check(await File.ReadAllTextAsync(path) == futureJson, "Readonly settings preserved after close", report);
        }
        finally
        {
            if (fallback.IsVisible) fallback.Close();
            fallback.Dispose(); System.Windows.Application.Current.MainWindow = originalMainWindow;
        }
    }
    private static void CheckAcrylicShape(string offPath, string onPath, double dipWidth, double dipHeight, double widgetScale, ValidationReport report)
    {
        var off = ReadPixels(offPath);
        var on = ReadPixels(onPath);
        if (off.Width != on.Width || off.Height != on.Height) throw new InvalidOperationException("Acrylic captures have different dimensions.");
        var outside = new[] { new Point(10, 205), new Point(342, 100), new Point(200, 205) };
        foreach (var p in outside)
        {
            var x = Math.Clamp((int)Math.Round(p.X * widgetScale * off.Width / dipWidth), 0, off.Width - 1);
            var y = Math.Clamp((int)Math.Round(p.Y * widgetScale * off.Height / dipHeight), 0, off.Height - 1);
            var a = PixelAt(off, x, y); var b = PixelAt(on, x, y);
            Check(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1,
                $"Outside acrylic clip at DIP ({p.X},{p.Y}) stayed unchanged: off={a}, on={b}.", report);
        }
        var insideX = Math.Clamp((int)Math.Round(200 * widgetScale * off.Width / dipWidth), 0, off.Width - 1);
        var insideY = Math.Clamp((int)Math.Round(150 * widgetScale * off.Height / dipHeight), 0, off.Height - 1);
        var insideOff = PixelAt(off, insideX, insideY); var insideOn = PixelAt(on, insideX, insideY);
        Check(Math.Abs(insideOff.R - insideOn.R) + Math.Abs(insideOff.G - insideOn.G) + Math.Abs(insideOff.B - insideOn.B) > 0,
            $"Inside acrylic clip at DIP (200,150) changed: off={insideOff}, on={insideOn}.", report);
    }
    private static (int Width, int Height, int Stride, byte[] Pixels) ReadPixels(string path)
    {
        BitmapSource frame;
        using (var input = File.OpenRead(path))
            frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return (converted.PixelWidth, converted.PixelHeight, stride, pixels);
    }
    private static (byte R, byte G, byte B) PixelAt((int Width, int Height, int Stride, byte[] Pixels) image, int x, int y)
    {
        var i = checked(y * image.Stride + x * 4);
        return (image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);
    }
    private static async Task WaitForWidth(MainWindow window, double target)
    {
        target *= window.WidgetScale;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            if (Math.Abs(window.ActualWidth - target) < 1) break;
        } while (clock.ElapsedMilliseconds < 2000);
        Mark($"width settle elapsed={clock.ElapsedMilliseconds} actual={window.ActualWidth} width={window.Width} base={window.GetAnimationBaseValue(Window.WidthProperty)} expanded={window.ViewModel.IsExpanded}");
    }
    private static void CaptureMemorySnapshot(ValidationReport report, string phase)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        report.MemorySnapshots.Add(new MemorySnapshot
        {
            Phase = phase,
            Utc = DateTimeOffset.UtcNow,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            ProcessCpuSeconds = process.TotalProcessorTime.TotalSeconds,
            ManagedBytesEstimate = GC.GetTotalMemory(false)
        });
    }
    private static void ValidateRegion(MainWindow window, ValidationReport report)
    {
        Check(window.RegionApplied, "Native window region applied", report);
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new InvalidOperationException("Cannot create test region.");
        try
        {
            Check(GetWindowRgn(new WindowInteropHelper(window).Handle, region) != 0, "Native window region readable", report);
            var dpi = VisualTreeHelper.GetDpi(window);
            foreach (var sample in new[] { (0d, 0d, false), (365d, 45.6d, true), (10d, 100d, true), (342d, 45.6d, false) })
                Check(PtInRegion(region, (int)Math.Round(sample.Item1 * window.WidgetScale * dpi.DpiScaleX), (int)Math.Round(sample.Item2 * window.WidgetScale * dpi.DpiScaleY)) == sample.Item3, $"Native region point {sample.Item1},{sample.Item2}", report);
        }
        finally { DeleteObject(region); }
    }
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    private static void Check(bool condition, string assertion, ValidationReport report)
    {
        if (!condition) throw new InvalidOperationException("Validation failed: " + assertion);
        report.Assertions.Add(assertion);
    }
    private static void CaptureRendered(MainWindow window, string path, double scale)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 230, 230)), null, bounds);
            var brush = new VisualBrush(window)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds,
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            };
            drawing.DrawRectangle(brush, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void CaptureWindow(MainWindow window, string path) => NativeWindowCapture.SavePng(new WindowInteropHelper(window).Handle, path);
    private sealed class ValidationReport
    {
        public bool Success { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset FinishedAtUtc { get; set; }
        public bool AcrylicNativeAccepted { get; set; }
        public List<MemorySnapshot> MemorySnapshots { get; } = new();
        public List<string> Assertions { get; } = new();
        public List<string> Errors { get; } = new();
    }
    private sealed class MemorySnapshot
    {
        public string Phase { get; set; } = "";
        public DateTimeOffset Utc { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        public double ProcessCpuSeconds { get; set; }
        public long ManagedBytesEstimate { get; set; }
    }
}
