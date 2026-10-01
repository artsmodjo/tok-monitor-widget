using System;

namespace Artsmodjo.Core;

public static class WidgetLayout
{
    public const double Width = 93;
    public const double Height = 528;
    public const double NotchScale = 0.4;
    public const double CollapsedWidth = Width * NotchScale;
    public const double CollapsedHeight = Height * NotchScale;
    public const double CardWidth = 300, CardHeight = 178, TailWidth = 36, CardGap = 15;
    public const double ExpandedWidth = CardWidth + TailWidth + CardGap + CollapsedWidth;
}

public readonly record struct WidgetPosition(double Left, double Top);

public static class GeometryMath
{
    public static WidgetPosition ClampPosition(double left, double top, double widgetWidth, double widgetHeight, double workLeft, double workTop, double workWidth, double workHeight)
    {
        var maxLeft = workLeft + Math.Max(0, workWidth - Math.Max(0, widgetWidth));
        var maxTop = workTop + Math.Max(0, workHeight - Math.Max(0, widgetHeight));
        return new WidgetPosition(ClampFinite(left, workLeft, maxLeft), ClampFinite(top, workTop, maxTop));
    }

    public static double RestoreTopFromNormalized(double normalizedY, double workTop, double workHeight, double widgetHeight)
    {
        var available = Math.Max(0, workHeight - Math.Max(0, widgetHeight));
        var fraction = double.IsFinite(normalizedY) ? Math.Clamp(normalizedY, 0, 1) : 0.5;
        return workTop + available * fraction;
    }

    public static double NormalizeTop(double top, double workTop, double workHeight, double widgetHeight)
    {
        var available = Math.Max(0, workHeight - Math.Max(0, widgetHeight));
        if (available == 0) return 0;
        return Math.Clamp(((double.IsFinite(top) ? top : workTop) - workTop) / available, 0, 1);
    }

    private static double ClampFinite(double value, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
}

public static class RingPercent
{
    public static double? ToSweepAngle(double? percent) => percent is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 100) * 3.6 : null;
    public static System.Collections.Generic.IReadOnlyList<double?> DemoFixture { get; } = Array.AsReadOnly(new double?[] { 73, 21, 52 });
}
