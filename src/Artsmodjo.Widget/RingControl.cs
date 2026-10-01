using System;
using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Artsmodjo.Core;

namespace Artsmodjo.Widget;

public sealed class RingControl : FrameworkElement
{
    private const double CenterX = 51, CenterY = 34, Radius = 27, StrokeWidth = 6, LogoSize = 24;
    private static readonly Brush TrackBrush = CreateBrush(Color.FromRgb(48, 48, 48));
    private static readonly Brush DefaultAccentBrush = CreateBrush(Color.FromRgb(255, 63, 0));
    private static readonly Brush ForegroundBrush = CreateBrush(Colors.White);
    private static readonly Brush FocusBrush = CreateBrush(Color.FromArgb(160, 255, 255, 255));
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Geometry?[] Logos = { LoadLogoGeometry("claude"), LoadLogoGeometry("openai"), LoadLogoGeometry("computer") };
    private Geometry? _arcGeometry;
    private double _arcSweep = double.NaN;
    public RingControl() { Width = WidgetLayout.Width; Height = 105; Focusable = true; Cursor = Cursors.Hand; }
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(double?), typeof(RingControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPercentChanged, CoercePercent));
    public double? Percent { get => (double?)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public static readonly DependencyProperty IconIndexProperty = DependencyProperty.Register(nameof(IconIndex), typeof(int), typeof(RingControl), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public int IconIndex { get => (int)GetValue(IconIndexProperty); set => SetValue(IconIndexProperty, value); }
    public static readonly DependencyProperty LogoGeometryProperty = DependencyProperty.Register(nameof(LogoGeometry), typeof(Geometry), typeof(RingControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Geometry? LogoGeometry { get => (Geometry?)GetValue(LogoGeometryProperty); set => SetValue(LogoGeometryProperty, value); }
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(RingControl), new FrameworkPropertyMetadata(DefaultAccentBrush, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public static readonly DependencyProperty AccessibleNameProperty = DependencyProperty.Register(nameof(AccessibleName), typeof(string), typeof(RingControl), new FrameworkPropertyMetadata(string.Empty));
    public string AccessibleName { get => (string)GetValue(AccessibleNameProperty); set => SetValue(AccessibleNameProperty, value); }
    public static readonly RoutedEvent OpenRequestedEvent = EventManager.RegisterRoutedEvent(nameof(OpenRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RingControl));
    public event RoutedEventHandler OpenRequested { add => AddHandler(OpenRequestedEvent, value); remove => RemoveHandler(OpenRequestedEvent, value); }
    public static Geometry? LogoFor(int index) => index is >= 0 and < 3 ? Logos[index] : null;
    protected override AutomationPeer OnCreateAutomationPeer() => new RingControlAutomationPeer(this);
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var center = new Point(CenterX, CenterY);
        context.DrawEllipse(null, new Pen(TrackBrush, StrokeWidth), center, Radius, Radius);
        if (RingPercent.ToSweepAngle(Percent) is double sweep)
        {
            var pen = new Pen(Accent, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (sweep >= 360) context.DrawEllipse(null, pen, center, Radius, Radius);
            else if (sweep > 0) { EnsureArcGeometry(sweep); context.DrawGeometry(null, pen, _arcGeometry); }
        }
        var logo = LogoGeometry ?? LogoFor(IconIndex);
        if (logo is not null && !logo.Bounds.IsEmpty)
        {
            var bounds = logo.Bounds;
            var scale = LogoSize / Math.Max(bounds.Width, bounds.Height);
            var matrix = new Matrix(scale, 0, 0, scale, CenterX - (bounds.X + bounds.Width / 2) * scale, CenterY - (bounds.Y + bounds.Height / 2) * scale);
            context.PushTransform(new MatrixTransform(matrix)); context.DrawGeometry(ForegroundBrush, null, logo); context.Pop();
        }
        var text = new FormattedText(Percent?.ToString("0.#", CultureInfo.InvariantCulture) + (Percent.HasValue ? "%" : "--%"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 20, ForegroundBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point(CenterX - text.Width / 2, 80));
        if (IsKeyboardFocused) context.DrawRoundedRectangle(null, new Pen(FocusBrush, 1), new Rect(0.5, 0.5, Math.Max(0, RenderSize.Width - 1), Math.Max(0, RenderSize.Height - 1)), 4, 4);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) { RaiseOpenRequested(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
    private void EnsureArcGeometry(double sweep)
    {
        if (_arcGeometry is not null && _arcSweep.Equals(sweep)) return;
        _arcSweep = sweep;
        var radians = (-90 + sweep) * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(CenterX, CenterY - Radius), false, false);
            context.ArcTo(new Point(CenterX + Radius * Math.Cos(radians), CenterY + Radius * Math.Sin(radians)), new Size(Radius, Radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); _arcGeometry = geometry;
    }
    private void RaiseOpenRequested() => RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this));
    private static object? CoercePercent(DependencyObject _, object? value) => value is double percent && double.IsFinite(percent) ? percent : null;
    private static void OnPercentChanged(DependencyObject value, DependencyPropertyChangedEventArgs _) { var control = (RingControl)value; control._arcGeometry = null; control._arcSweep = double.NaN; }
    private static Brush CreateBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Geometry? LoadLogoGeometry(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Artsmodjo.Widget.Assets.{name}.svg");
        if (stream is null) throw new InvalidOperationException("Missing embedded logo: " + name);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var geometry = new GeometryGroup();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "path" &&
                reader.GetAttribute("d") is string path && !string.IsNullOrWhiteSpace(path))
                geometry.Children.Add(Geometry.Parse("F1 " + path));
        }
        geometry.Freeze(); return geometry;
    }
    private sealed class RingControlAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public RingControlAutomationPeer(RingControl owner) : base(owner) { }
        protected override string GetNameCore()
        {
            var control = (RingControl)Owner;
            if (!string.IsNullOrWhiteSpace(control.AccessibleName)) return control.AccessibleName;
            return $"{control.IconIndex switch { 0 => "Claude", 1 => "Codex", _ => "RAM reference fixture" }}, {control.Percent?.ToString("0.#", CultureInfo.InvariantCulture) ?? "unknown"}";
        }
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke() { if (!((RingControl)Owner).IsEnabled) throw new ElementNotEnabledException(); ((RingControl)Owner).RaiseOpenRequested(); }
    }
}
