using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Artsmodjo.Core;
using Forms = System.Windows.Forms;

namespace Artsmodjo.Widget;

public partial class MainWindow : Window, IDisposable
{
    private readonly SettingsStore _settingsStore;
    internal readonly WidgetViewModel ViewModel;
    private readonly Forms.NotifyIcon _trayIcon;
    internal readonly Forms.ContextMenuStrip TrayMenu;
    private readonly DispatcherTimer _collapseTimer;
    private Forms.Screen _screen = Forms.Screen.PrimaryScreen!;
    private HwndSource? _source;
    private bool _disposed, _dragPending, _isDragging, _positioning;
    private Point _dragStartScreen;
    private double _dragStartTopPixels, _scale = 1;
    internal bool AcrylicApplied { get; private set; }

    public MainWindow(WidgetSettings settings, SettingsStore? store = null)
    {
        ViewModel = new WidgetViewModel(settings);
        _settingsStore = store ?? new SettingsStore();
        InitializeComponent();
        DataContext = ViewModel;
        Topmost = ViewModel.IsTopmost;
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsMouseOver && !IsKeyboardFocusWithin) ViewModel.Close(); };
        TrayMenu = CreateTrayMenu();
        _trayIcon = new Forms.NotifyIcon { Text = "ARTSMODJO Monitoring Widget", Icon = System.Drawing.SystemIcons.Application, ContextMenuStrip = TrayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => ToggleVisibility();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) => { SelectSavedMonitor(); SetDemoValues(); ApplyState(false); ApplyAcrylic(); };
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleY;
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessages);
    }
    private IntPtr WindowMessages(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Re-resolve monitor work areas after display/DPI changes; don't keep an unplugged Screen snapshot.
        if (message is 0x007E or 0x02E0) Dispatcher.BeginInvoke(() => { SelectSavedMonitor(); ApplyState(false); ApplyAcrylic(); });
        return IntPtr.Zero;
    }
    private void SelectSavedMonitor()
    {
        _screen = Array.Find(Forms.Screen.AllScreens, screen => screen.DeviceName == ViewModel.MonitorDevice) ?? Forms.Screen.PrimaryScreen!;
        ViewModel.MonitorDevice = _screen.DeviceName;
        _scale = Math.Min(1, _screen.WorkingArea.Height / (WidgetLayout.Height * DpiScale));
        RootCanvas.RenderTransform = new ScaleTransform(_scale, _scale);
        Height = WidgetLayout.Height * _scale;
        RestorePosition();
    }
    private void RestorePosition()
    {
        var area = _screen.WorkingArea;
        Top = GeometryMath.RestoreTopFromNormalized(ViewModel.NormalizedY, area.Top / DpiScale, area.Height / DpiScale, Height);
        DockAndLayout();
    }
    private void DockAndLayout()
    {
        if (!IsLoaded || _positioning) return;
        _positioning = true;
        try
        {
            var area = _screen.WorkingArea;
            Left = ViewModel.Edge == WidgetEdge.Left ? area.Left / DpiScale : area.Right / DpiScale - Width;
            Top = Math.Clamp(Top, area.Top / DpiScale, Math.Max(area.Top / DpiScale, area.Bottom / DpiScale - Height));
            var designWidth = Width / _scale;
            RootCanvas.Width = designWidth;
            var onLeft = ViewModel.Edge == WidgetEdge.Left;
            Canvas.SetLeft(RailCanvas, onLeft ? 0 : designWidth - WidgetLayout.Width);
            RailShape.RenderTransform = onLeft ? new ScaleTransform(-1, 1, WidgetLayout.Width / 2, 0) : Transform.Identity;
            Canvas.SetLeft(UsageCard, onLeft ? 144 : designWidth - WidgetLayout.ExpandedWidth);
            var selectedY = 114 + (int)ViewModel.SelectedIndicator * 137.5;
            Canvas.SetTop(UsageCard, selectedY - 89);
            Canvas.SetLeft(CardTail, onLeft ? 108 : designWidth - WidgetLayout.ExpandedWidth);
            Canvas.SetTop(CardTail, selectedY - 89);
            CardTail.RenderTransform = onLeft ? new ScaleTransform(-1, 1, 168, 0) : Transform.Identity;
            UpdateRegion();
        }
        finally { _positioning = false; }
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { if (RailCanvas is not null) DockAndLayout(); }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WidgetViewModel.IsExpanded): ApplyState(true); break;
            case nameof(WidgetViewModel.Edge): DockAndLayout(); break;
            case nameof(WidgetViewModel.SelectedIndicator): SetDemoValues(); DockAndLayout(); break;
            case nameof(WidgetViewModel.IsTopmost): Topmost = ViewModel.IsTopmost; break;
            case nameof(WidgetViewModel.AcrylicEnabled): ApplyAcrylic(); break;
            case nameof(WidgetViewModel.DemoDataEnabled): SetDemoValues(); break;
        }
        if (e.PropertyName == nameof(WidgetViewModel.Settings) && !_dragPending && IsLoaded) _ = SaveSettingsAsync();
    }
    internal void ApplyState(bool animate)
    {
        var expanded = ViewModel.IsExpanded;
        var target = (expanded ? WidgetLayout.ExpandedWidth : WidgetLayout.Width) * _scale;
        var current = Width;
        BeginAnimation(WidthProperty, null);
        Width = target;
        UsageCard.Visibility = CardTail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (animate && !ViewModel.ReducedMotion && SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(expanded ? 160 : 120)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            BeginAnimation(WidthProperty, animation);
        }
        DockAndLayout();
    }
    private void SetDemoValues()
    {
        var rings = new[] { ClaudeRing, CodexRing, RamRing };
        for (var index = 0; index < rings.Length; index++) rings[index].Percent = ViewModel.DemoDataEnabled ? RingPercent.DemoFixture[index] : null;
        var selected = (int)ViewModel.SelectedIndicator;
        CardTitle.Text = selected switch { 0 => "Claude Usage", 1 => "Codex Usage", _ => "RAM reference" };
        CardLogo.Data = RingControl.LogoFor(selected);
        var session = ViewModel.DemoDataEnabled ? RingPercent.DemoFixture[selected] : null;
        var weekly = ViewModel.DemoDataEnabled && selected == 0 ? 7d : (double?)null;
        SessionFill.Width = 276 * (session ?? 0) / 100;
        AllModelsFill.Width = 276 * (weekly ?? 0) / 100;
        SessionUsed.Text = session is double value ? $"{value:0}% Used" : "--% Used";
        AllModelsUsed.Text = weekly is double all ? $"{all:0}% Used" : "--% Used";
        SessionReset.Text = ViewModel.DemoDataEnabled && selected == 0 ? "Resets in 51 min" : "Reset unavailable";
        AllModelsReset.Text = weekly.HasValue ? "Resets Thu 12:00 AM" : "Reset unavailable";
        RamRing.AccessibleName = "RAM reference fixture, " + (RamRing.Percent.HasValue ? "52 percent" : "unknown");
    }
    private void ApplyAcrylic()
    {
        AcrylicApplied = AcrylicInterop.TrySet(this, ViewModel.AcrylicEnabled && !_isDragging);
        var glass = ViewModel.AcrylicEnabled && AcrylicApplied && !_isDragging;
        var surface = new SolidColorBrush(Color.FromArgb(glass ? (byte)210 : (byte)255, 0, 0, 0)); surface.Freeze();
        RailShape.Fill = CardTail.Fill = UsageCard.Background = surface;
        UpdateRegion();
    }
    private void UpdateRegion()
    {
        if (!IsLoaded) return;
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = new ScaleTransform(_scale, _scale) };
        var rail = RailShape.Data.Clone();
        var railTransform = new TransformGroup(); railTransform.Children.Add(RailShape.RenderTransform); railTransform.Children.Add(new TranslateTransform(Canvas.GetLeft(RailCanvas), 0));
        rail.Transform = railTransform; geometry.Children.Add(rail);
        if (ViewModel.IsExpanded)
        {
            geometry.Children.Add(new RectangleGeometry(new Rect(Canvas.GetLeft(UsageCard), Canvas.GetTop(UsageCard), 300, 178), 25, 25));
            var tail = CardTail.Data.Clone(); var transform = new TransformGroup(); transform.Children.Add(CardTail.RenderTransform); transform.Children.Add(new TranslateTransform(Canvas.GetLeft(CardTail), Canvas.GetTop(CardTail))); tail.Transform = transform; geometry.Children.Add(tail);
        }
        AcrylicInterop.UpdateRegion(this, geometry);
    }
    private void Window_MouseEnter(object sender, MouseEventArgs e) => _collapseTimer.Stop();
    private void Ring_MouseEnter(object sender, MouseEventArgs e) { if (!_dragPending && sender is RingControl ring) ViewModel.Open(ring.IconIndex); }
    private void Window_MouseLeave(object sender, MouseEventArgs e) { if (!_dragPending) { _collapseTimer.Stop(); _collapseTimer.Start(); } }
    private void Ring_OpenRequested(object sender, RoutedEventArgs e) { if (sender is RingControl ring) ViewModel.Open(ring.IconIndex); }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ViewModel.IsPinned = false; ViewModel.Close(true); e.Handled = true; }
    }
    private void Rail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartScreen = PointToScreen(e.GetPosition(this)); _dragStartTopPixels = Top * DpiScale; _dragPending = true; _isDragging = false;
        RailCanvas.CaptureMouse(); e.Handled = true;
    }
    private void Rail_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPending || e.LeftButton != MouseButtonState.Pressed) return;
        var current = PointToScreen(e.GetPosition(this)); var delta = current.Y - _dragStartScreen.Y;
        if (!_isDragging && Math.Abs(delta) < 4 * DpiScale) return;
        if (!_isDragging) { _isDragging = true; _collapseTimer.Stop(); ApplyAcrylic(); }
        var area = _screen.WorkingArea;
        Top = Math.Clamp(_dragStartTopPixels + delta, area.Top, Math.Max(area.Top, area.Bottom - Height * DpiScale)) / DpiScale;
    }
    private async void Rail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragPending) return;
        var dragged = _isDragging;
        _dragPending = _isDragging = false; RailCanvas.ReleaseMouseCapture();
        if (dragged) { ViewModel.NormalizedY = GeometryMath.NormalizeTop(Top * DpiScale, _screen.WorkingArea.Top, _screen.WorkingArea.Height, Height * DpiScale); await SaveSettingsAsync(); ApplyAcrylic(); }
        else { var hit = e.OriginalSource as DependencyObject; while (hit is not null && hit is not RingControl) hit = VisualTreeHelper.GetParent(hit); if (hit is RingControl ring) { ViewModel.Open(ring.IconIndex); ring.Focus(); } }
        e.Handled = true;
    }
    private void Rail_LostMouseCapture(object sender, MouseEventArgs e) { if (_dragPending) { _dragPending = _isDragging = false; ApplyAcrylic(); } }
    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / Hide", null, (_, _) => ToggleVisibility());
        menu.Items.Add("Pin card", null, (_, _) => ViewModel.TogglePinCommand.Execute(null));
        menu.Items.Add("Dock Left", null, (_, _) => ViewModel.Edge = WidgetEdge.Left);
        menu.Items.Add("Dock Right", null, (_, _) => ViewModel.Edge = WidgetEdge.Right);
        var displays = new Forms.ToolStripMenuItem("Display");
        foreach (var screen in Forms.Screen.AllScreens) displays.DropDownItems.Add(screen.DeviceName, null, (_, _) => { ViewModel.MonitorDevice = screen.DeviceName; SelectSavedMonitor(); ApplyState(false); });
        menu.Items.Add(displays);
        AddToggle("Always on top", () => ViewModel.IsTopmost, value => ViewModel.IsTopmost = value);
        AddToggle("Acrylic style", () => ViewModel.AcrylicEnabled, value => ViewModel.AcrylicEnabled = value);
        AddToggle("Demo reference data", () => ViewModel.DemoDataEnabled, value => ViewModel.DemoDataEnabled = value);
        AddToggle("Reduced motion", () => ViewModel.ReducedMotion, value => ViewModel.ReducedMotion = value);
        menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("Exit", null, (_, _) => Close());
        menu.Opening += (_, _) => { foreach (Forms.ToolStripItem item in menu.Items) if (item is Forms.ToolStripMenuItem toggle && toggle.Tag is Func<bool> read) toggle.Checked = read(); };
        return menu;
        void AddToggle(string label, Func<bool> read, Action<bool> write) { var item = new Forms.ToolStripMenuItem(label) { Tag = read }; item.Click += (_, _) => write(!read()); menu.Items.Add(item); }
    }
    internal void ToggleVisibility() { if (IsVisible) Hide(); else Show(); }
    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) { TrayMenu.Show(Forms.Cursor.Position); e.Handled = true; }
    internal async Task SaveSettingsAsync()
    {
        try { await _settingsStore.SaveAsync(ViewModel.CloneSnapshot()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { if (!_disposed) { _trayIcon.BalloonTipTitle = "Widget settings"; _trayIcon.BalloonTipText = "Settings could not be saved. " + error.Message; _trayIcon.ShowBalloonTip(3500); } }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _collapseTimer.Stop(); ViewModel.PropertyChanged -= ViewModel_PropertyChanged; _source?.RemoveHook(WindowMessages);
        _trayIcon.Visible = false; _trayIcon.Dispose(); TrayMenu.Dispose();
    }
    protected override void OnClosed(EventArgs e) { Dispose(); base.OnClosed(e); }
}
