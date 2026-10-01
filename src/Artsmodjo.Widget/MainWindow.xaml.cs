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
using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Artsmodjo.Widget;

public partial class MainWindow : Window, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly bool _canPersist;
    internal readonly WidgetViewModel ViewModel;
    private NativeTray? _trayIcon;
    internal readonly IReadOnlyList<MenuEntry> TrayMenu;
    private readonly DispatcherTimer _collapseTimer;
    private WidgetMonitor _screen = WidgetMonitors.PrimaryScreen;
    private HwndSource? _source;
    private bool _disposed, _dragPending, _isDragging, _positioning;
    private Point _dragStartScreen;
    private double _dragStartTopPixels, _scale = 1;
    private Task<bool> _settingsSaveTail = Task.FromResult(true);
    private bool _shutdownSaveStarted, _allowClose;
    private int _applyStateGeneration;
    internal bool RegionApplied { get; private set; }
    internal bool AcrylicApplied { get; private set; }

    public MainWindow(WidgetSettings settings, SettingsStore? store = null, bool canPersist = true)
    {
        _canPersist = canPersist;
        ViewModel = new WidgetViewModel(settings);
        _settingsStore = store ?? new SettingsStore();
        InitializeComponent();
        Width = WidgetLayout.CollapsedWidth;
        Height = RootCanvas.Height = WidgetLayout.CollapsedHeight;
        RailCanvas.RenderTransform = new ScaleTransform(WidgetLayout.NotchScale, WidgetLayout.NotchScale);
        DataContext = ViewModel;
        Topmost = ViewModel.IsTopmost;
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsMouseOver && !IsKeyboardFocusWithin) ViewModel.Close(); };
        TrayMenu = CreateTrayMenu();


        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) => { SelectSavedMonitor(); SetDemoValues(); ApplyState(false); ApplyAcrylic(); };
    }

    internal double WidgetScale => _scale;
    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleY;
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessages);
        _trayIcon = new NativeTray(new WindowInteropHelper(this).Handle, ToggleVisibility, ShowTrayMenu);
        _trayIcon.TryAdd();
    }
    private IntPtr WindowMessages(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_trayIcon?.ProcessMessage(message, wParam, lParam) == true) { handled = true; return IntPtr.Zero; }
        // Re-resolve monitor work areas after display/DPI changes; don't keep an unplugged Screen snapshot.
        if (message is 0x007E or 0x02E0) Dispatcher.BeginInvoke(() => { SelectSavedMonitor(); ApplyState(false); ApplyAcrylic(); });
        return IntPtr.Zero;
    }
    private void SelectSavedMonitor()
    {
        _screen = Array.Find(WidgetMonitors.AllScreens, screen => screen.DeviceName == ViewModel.MonitorDevice) ?? WidgetMonitors.PrimaryScreen;
        ViewModel.MonitorDevice = _screen.DeviceName;
        _scale = Math.Min(1, _screen.WorkingArea.Height / (WidgetLayout.CollapsedHeight * DpiScale));
        RootCanvas.RenderTransform = new ScaleTransform(_scale, _scale);
        Height = WidgetLayout.CollapsedHeight * _scale;
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
            Canvas.SetLeft(RailCanvas, onLeft ? 0 : designWidth - WidgetLayout.CollapsedWidth);
            RailShape.RenderTransform = onLeft ? new ScaleTransform(-1, 1, WidgetLayout.Width / 2, 0) : Transform.Identity;
            Canvas.SetLeft(UsageCard, onLeft ? WidgetLayout.CollapsedWidth + WidgetLayout.CardGap + WidgetLayout.TailWidth : designWidth - WidgetLayout.ExpandedWidth);
            var selectedY = (114 + (int)ViewModel.SelectedIndicator * 137.5) * WidgetLayout.NotchScale;
            var cardTop = Math.Clamp(selectedY - WidgetLayout.CardHeight / 2, 0, WidgetLayout.CollapsedHeight - WidgetLayout.CardHeight);
            Canvas.SetTop(UsageCard, cardTop);
            Canvas.SetLeft(CardTail, onLeft ? WidgetLayout.CollapsedWidth + WidgetLayout.CardGap : designWidth - WidgetLayout.ExpandedWidth);
            Canvas.SetTop(CardTail, cardTop);
            var tip = selectedY - cardTop;
            var anchor = Math.Clamp(tip, 64, 114);
            CardTail.Data = Geometry.Parse(FormattableString.Invariant($"M 300,{anchor - 39} C 300,{anchor - 14} 315,{tip - 9} 336,{tip} C 315,{tip + 9} 300,{anchor + 14} 300,{anchor + 39} Z"));
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
        var generation = ++_applyStateGeneration;
        var expanded = ViewModel.IsExpanded;
        var target = (expanded ? WidgetLayout.ExpandedWidth : WidgetLayout.CollapsedWidth) * _scale;
        var current = Width;
        BeginAnimation(WidthProperty, null);
        Width = target;
        UsageCard.Visibility = CardTail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (animate && !ViewModel.ReducedMotion && SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(expanded ? 160 : 120)) { FillBehavior = FillBehavior.HoldEnd, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) =>
            {
                if (generation != _applyStateGeneration) return;
                BeginAnimation(WidthProperty, null); Width = target; DockAndLayout();
            };
            BeginAnimation(WidthProperty, animation);
        }
        DockAndLayout();
    }
    private void SetDemoValues()
    {
        var rings = new[] { ClaudeRing, CodexRing, RamRing };
        for (var index = 0; index < rings.Length; index++) rings[index].Percent = ViewModel.DemoDataEnabled ? RingPercent.DemoFixture[index] : null;
        var selected = (int)ViewModel.SelectedIndicator;
        var title = selected switch { 0 => "Claude Usage", 1 => "Codex Usage", _ => "RAM reference" };
        CardTitle.Text = ViewModel.DemoDataEnabled ? title + " · Demo" : title;
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
        var requested = ViewModel.AcrylicEnabled && !_isDragging;
        AcrylicApplied = AcrylicInterop.TrySet(this, requested) && requested;
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
        var railTransform = new TransformGroup(); railTransform.Children.Add(RailShape.RenderTransform); railTransform.Children.Add(RailCanvas.RenderTransform); railTransform.Children.Add(new TranslateTransform(Canvas.GetLeft(RailCanvas), 0));
        rail.Transform = railTransform; geometry.Children.Add(rail);
        if (ViewModel.IsExpanded)
        {
            geometry.Children.Add(new RectangleGeometry(new Rect(Canvas.GetLeft(UsageCard), Canvas.GetTop(UsageCard), 300, 178), 25, 25));
            var tail = CardTail.Data.Clone(); var transform = new TransformGroup(); transform.Children.Add(CardTail.RenderTransform); transform.Children.Add(new TranslateTransform(Canvas.GetLeft(CardTail), Canvas.GetTop(CardTail))); tail.Transform = transform; geometry.Children.Add(tail);
        }
        RegionApplied = AcrylicInterop.UpdateRegion(this, geometry);
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
        BeginRailDrag(PointToScreen(e.GetPosition(this))); e.Handled = true;
    }
    private void Rail_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragPending && e.LeftButton == MouseButtonState.Pressed) MoveRailDrag(PointToScreen(e.GetPosition(this)));
    }
    private async void Rail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragPending) return;
        e.Handled = true;
        // Capture release can change OriginalSource; resolve the visible target first.
        await EndRailDragAsync(InputHitTest(e.GetPosition(this)) as DependencyObject);
    }
    internal void BeginRailDrag(Point screen)
    {
        _dragStartScreen = screen; _dragStartTopPixels = Top * DpiScale;
        _dragPending = true; _isDragging = false; RailCanvas.CaptureMouse();
    }
    internal void MoveRailDrag(Point screen)
    {
        if (!_dragPending) return;
        var delta = screen.Y - _dragStartScreen.Y;
        if (!_isDragging && Math.Abs(delta) < 4 * DpiScale) return;
        if (!_isDragging) { _isDragging = true; _collapseTimer.Stop(); ApplyAcrylic(); }
        var area = _screen.WorkingArea;
        Top = Math.Clamp(_dragStartTopPixels + delta, area.Top, Math.Max(area.Top, area.Bottom - Height * DpiScale)) / DpiScale;
    }
    internal async Task EndRailDragAsync(DependencyObject? releaseHit)
    {
        if (!_dragPending) return;
        var dragged = _isDragging;
        _dragPending = _isDragging = false; RailCanvas.ReleaseMouseCapture();
        if (dragged)
        {
            ViewModel.NormalizedY = GeometryMath.NormalizeTop(Top * DpiScale, _screen.WorkingArea.Top, _screen.WorkingArea.Height, Height * DpiScale);
            await SaveSettingsAsync(); ApplyAcrylic();
        }
        else
        {
            var hit = releaseHit;
            while (hit is not null && hit is not RingControl) hit = VisualTreeHelper.GetParent(hit);
            if (hit is RingControl ring) { ViewModel.Open(ring.IconIndex); ring.Focus(); }
        }
    }
    private void Rail_LostMouseCapture(object sender, MouseEventArgs e) { if (_dragPending) { _dragPending = _isDragging = false; ApplyAcrylic(); } }
    private IReadOnlyList<MenuEntry> CreateTrayMenu() => new MenuEntry[]
    {
        new("Show / Hide", ToggleVisibility),
        new("Pin card", () => ViewModel.TogglePinCommand.Execute(null), () => ViewModel.IsPinned),
        new("Dock Left", () => ViewModel.Edge = WidgetEdge.Left, () => ViewModel.Edge == WidgetEdge.Left),
        new("Dock Right", () => ViewModel.Edge = WidgetEdge.Right, () => ViewModel.Edge == WidgetEdge.Right),
        new("Display", Children: () =>
        {
            var entries = new List<MenuEntry>();
            foreach (var monitor in WidgetMonitors.AllScreens)
                entries.Add(new MenuEntry(monitor.DeviceName, () => { ViewModel.MonitorDevice = monitor.DeviceName; SelectSavedMonitor(); ApplyState(false); }, () => monitor.DeviceName == ViewModel.MonitorDevice));
            return entries;
        }),
        new("Always on top", () => ViewModel.IsTopmost = !ViewModel.IsTopmost, () => ViewModel.IsTopmost),
        new("Acrylic style", () => ViewModel.AcrylicEnabled = !ViewModel.AcrylicEnabled, () => ViewModel.AcrylicEnabled),
        new("Demo reference data", () => ViewModel.DemoDataEnabled = !ViewModel.DemoDataEnabled, () => ViewModel.DemoDataEnabled),
        new("Reduced motion", () => ViewModel.ReducedMotion = !ViewModel.ReducedMotion, () => ViewModel.ReducedMotion),
        new("-"), new("Exit", Close)
    };
    internal async Task<bool> ProbeStartupReadinessAsync()
    {
        if (_trayIcon?.IsRegistered != true) return false;
        var wasExpanded = ViewModel.IsExpanded;
        var wasPinned = ViewModel.IsPinned;
        var passed = false;
        try
        {
            passed = await NativeMenu.ProbeAsync(this, TrayMenu);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(ClaudeRing);
            var invoke = peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            if (passed && invoke is not null)
            {
                if (ViewModel.IsExpanded) ViewModel.Close(force: true);
                invoke.Invoke();
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
                passed = ViewModel.IsExpanded;
            }
            else passed = false;
        }
        catch { passed = false; }
        finally
        {
            if (ViewModel.IsExpanded != wasExpanded)
                if (wasExpanded) ViewModel.Open(0); else ViewModel.Close(force: true);
        }
        return passed && ViewModel.IsExpanded == wasExpanded && ViewModel.IsPinned == wasPinned;
    }
    private void ShowTrayMenu() => NativeMenu.Show(this, TrayMenu);
    internal void ToggleVisibility() { if (IsVisible) Hide(); else Show(); }
    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e) { ShowTrayMenu(); e.Handled = true; }
    internal Task SaveSettingsAsync() => QueueSettingsSaveAsync();
    private Task<bool> QueueSettingsSaveAsync()
    {
        if (!_canPersist) return Task.FromResult(false);
        var pending = SaveSettingsAfterAsync(_settingsSaveTail, ViewModel.CloneSnapshot());
        _settingsSaveTail = pending; return pending;
    }
    private async Task<bool> SaveSettingsAfterAsync(Task<bool> previous, WidgetSettings snapshot)
    {
        // Preserve write order, including the final shutdown snapshot.
        await previous;
        try { await _settingsStore.SaveAsync(snapshot); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowSettingsSaveFailure(error); return false; }
    }
    private void ShowSettingsSaveFailure(Exception error)
    {
        if (_disposed) return;
        _trayIcon?.ShowBalloon("Widget settings", "Settings could not be saved. " + error.Message);
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || e.Cancel) return;
        e.Cancel = true;
        if (_shutdownSaveStarted) return;
        _shutdownSaveStarted = true;
        await QueueSettingsSaveAsync();
        _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _collapseTimer.Stop(); ViewModel.PropertyChanged -= ViewModel_PropertyChanged; _source?.RemoveHook(WindowMessages);
        _trayIcon?.Dispose();
    }
    protected override void OnClosed(EventArgs e) { Dispose(); base.OnClosed(e); }
}
