using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Nbn.Demos.Basics.Ui;

public sealed class MetricLineChart : Control
{
    private static readonly float[] NormalizedYAxisValues = [1.0f, 0.8f, 0.6f, 0.4f, 0.2f, 0.0f];
    private static readonly IBrush AxisBrush = new SolidColorBrush(Color.Parse("#D9D0C2"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6C7A89"));
    private static readonly Typeface LabelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface TitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public static readonly StyledProperty<IReadOnlyList<float>?> PrimaryValuesProperty =
        AvaloniaProperty.Register<MetricLineChart, IReadOnlyList<float>?>(nameof(PrimaryValues));

    public static readonly StyledProperty<IReadOnlyList<float>?> SecondaryValuesProperty =
        AvaloniaProperty.Register<MetricLineChart, IReadOnlyList<float>?>(nameof(SecondaryValues));

    public static readonly StyledProperty<IBrush?> PrimaryStrokeProperty =
        AvaloniaProperty.Register<MetricLineChart, IBrush?>(nameof(PrimaryStroke));

    public static readonly StyledProperty<IBrush?> SecondaryStrokeProperty =
        AvaloniaProperty.Register<MetricLineChart, IBrush?>(nameof(SecondaryStroke));

    public static readonly StyledProperty<string> EmptyTextProperty =
        AvaloniaProperty.Register<MetricLineChart, string>(nameof(EmptyText), "No chart data.");

    public static readonly StyledProperty<string> XAxisTitleProperty =
        AvaloniaProperty.Register<MetricLineChart, string>(nameof(XAxisTitle), "Generation");

    static MetricLineChart()
    {
        AffectsRender<MetricLineChart>(
            PrimaryValuesProperty,
            SecondaryValuesProperty,
            PrimaryStrokeProperty,
            SecondaryStrokeProperty,
            EmptyTextProperty,
            XAxisTitleProperty);
    }

    public MetricLineChart()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<float>? PrimaryValues
    {
        get => GetValue(PrimaryValuesProperty);
        set => SetValue(PrimaryValuesProperty, value);
    }

    public IReadOnlyList<float>? SecondaryValues
    {
        get => GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public IBrush? PrimaryStroke
    {
        get => GetValue(PrimaryStrokeProperty);
        set => SetValue(PrimaryStrokeProperty, value);
    }

    public IBrush? SecondaryStroke
    {
        get => GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public string EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public string XAxisTitle
    {
        get => GetValue(XAxisTitleProperty);
        set => SetValue(XAxisTitleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var labelFontSize = Math.Clamp(Math.Min(bounds.Width / 90d, bounds.Height / 55d), 8d, 10d);
        var titleFontSize = Math.Clamp(labelFontSize + 2d, 10d, 12d);

        var yLabelLayouts = NormalizedYAxisValues
            .Select(value => CreateLabelText(value.ToString("0.0", CultureInfo.InvariantCulture), labelFontSize))
            .ToArray();
        var maxYLabelWidth = yLabelLayouts.Max(layout => layout.Width);
        var xAxisTitleLayout = CreateTitleText(XAxisTitle, titleFontSize);
        var sampleXAxisLabelLayout = CreateLabelText("999", labelFontSize);

        const double leftTickLength = 6d;
        const double bottomTickLength = 6d;
        const double topMargin = 8d;
        const double rightMargin = 8d;
        const double yLabelGap = 6d;
        const double xLabelGap = 4d;
        const double titleGap = 8d;

        var leftMargin = maxYLabelWidth + yLabelGap + leftTickLength + 2d;
        var bottomMargin = bottomTickLength + xLabelGap + sampleXAxisLabelLayout.Height + titleGap + xAxisTitleLayout.Height;
        var plotRect = new Rect(
            leftMargin,
            topMargin,
            Math.Max(0d, bounds.Width - leftMargin - rightMargin),
            Math.Max(0d, bounds.Height - topMargin - bottomMargin));

        if (plotRect.Width <= 0 || plotRect.Height <= 0)
        {
            return;
        }

        var dataRect = new Rect(
            plotRect.Left + 2d,
            plotRect.Top + 6d,
            Math.Max(0d, plotRect.Width - 4d),
            Math.Max(0d, plotRect.Height - 12d));
        if (dataRect.Width <= 0 || dataRect.Height <= 0)
        {
            return;
        }

        var primaryValues = PrimaryValues ?? Array.Empty<float>();
        var secondaryValues = SecondaryValues ?? Array.Empty<float>();
        var generationCount = Math.Max(primaryValues.Count, secondaryValues.Count);

        DrawAxes(context, plotRect, dataRect, yLabelLayouts, xAxisTitleLayout, sampleXAxisLabelLayout.Height);
        DrawSeries(context, dataRect, primaryValues, new Pen(PrimaryStroke ?? new SolidColorBrush(Color.Parse("#0E7490")), 1.35, lineCap: PenLineCap.Round));
        DrawSeries(context, dataRect, secondaryValues, new Pen(SecondaryStroke ?? new SolidColorBrush(Color.Parse("#1D4ED8")), 1.35, dashStyle: new DashStyle([4d, 3d], 0d), lineCap: PenLineCap.Round));

        if (generationCount > 0)
        {
            DrawXAxisTicks(context, plotRect, dataRect, generationCount, labelFontSize);
        }
        else
        {
            DrawEmptyState(context, dataRect, labelFontSize);
        }
    }

    private void DrawAxes(
        DrawingContext context,
        Rect plotRect,
        Rect dataRect,
        IReadOnlyList<FormattedText> yLabelLayouts,
        FormattedText xAxisTitleLayout,
        double xAxisLabelHeight)
    {
        context.DrawLine(new Pen(AxisBrush, 1d), new Point(plotRect.Left, plotRect.Top), new Point(plotRect.Left, plotRect.Bottom));
        context.DrawLine(new Pen(AxisBrush, 1d), new Point(plotRect.Left, plotRect.Bottom), new Point(plotRect.Right, plotRect.Bottom));

        const double tickLength = 6d;
        for (var index = 0; index < NormalizedYAxisValues.Length; index++)
        {
            var value = NormalizedYAxisValues[index];
            var y = dataRect.Bottom - (value * dataRect.Height);
            var layout = yLabelLayouts[index];
            var labelOrigin = new Point(
                plotRect.Left - tickLength - 6d - layout.Width,
                Math.Clamp(y - (layout.Height / 2d), dataRect.Top - (layout.Height / 2d), dataRect.Bottom - layout.Height));
            context.DrawText(layout, labelOrigin);
            context.DrawLine(new Pen(AxisBrush, 1d), new Point(plotRect.Left, y), new Point(plotRect.Left + tickLength, y));
        }

        var xAxisTitleOrigin = new Point(
            plotRect.Left + ((plotRect.Width - xAxisTitleLayout.Width) / 2d),
            plotRect.Bottom + 6d + 4d + xAxisLabelHeight + 8d);
        context.DrawText(xAxisTitleLayout, xAxisTitleOrigin);
    }

    private void DrawSeries(DrawingContext context, Rect dataRect, IReadOnlyList<float> values, Pen pen)
    {
        if (values.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var x = values.Count == 1
                    ? dataRect.Left
                    : dataRect.Left + ((index * dataRect.Width) / (values.Count - 1d));
                var y = dataRect.Bottom - (Math.Clamp(values[index], 0f, 1f) * dataRect.Height);
                var point = new Point(x, y);
                if (index == 0)
                {
                    sink.BeginFigure(point, false);
                }
                else
                {
                    sink.LineTo(point);
                }
            }
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private void DrawXAxisTicks(DrawingContext context, Rect plotRect, Rect dataRect, int generationCount, double labelFontSize)
    {
        var tickIndices = BuildTickIndices(generationCount, dataRect.Width);
        var pen = new Pen(AxisBrush, 1d);

        foreach (var generationIndex in tickIndices)
        {
            var x = generationCount == 1
                ? dataRect.Left
                : dataRect.Left + ((generationIndex * dataRect.Width) / (generationCount - 1d));
            context.DrawLine(pen, new Point(x, plotRect.Bottom), new Point(x, plotRect.Bottom + 6d));

            var label = CreateLabelText((generationIndex + 1).ToString(CultureInfo.InvariantCulture), labelFontSize);
            var labelX = Math.Clamp(x - (label.Width / 2d), dataRect.Left, dataRect.Right - label.Width);
            var labelY = plotRect.Bottom + 6d + 4d;
            context.DrawText(label, new Point(labelX, labelY));
        }
    }

    private void DrawEmptyState(DrawingContext context, Rect dataRect, double labelFontSize)
    {
        var layout = CreateLabelText(EmptyText, labelFontSize);
        var origin = new Point(
            dataRect.Left + ((dataRect.Width - layout.Width) / 2d),
            dataRect.Top + ((dataRect.Height - layout.Height) / 2d));
        context.DrawText(layout, origin);
    }

    private static IReadOnlyList<int> BuildTickIndices(int generationCount, double plotWidth)
    {
        if (generationCount <= 1)
        {
            return generationCount == 0 ? Array.Empty<int>() : [0];
        }

        var maxByWidth = Math.Max(2, (int)Math.Floor(plotWidth / 42d) + 1);
        var tickCount = Math.Min(generationCount, Math.Min(14, maxByWidth));
        if (tickCount >= generationCount)
        {
            return Enumerable.Range(0, generationCount).ToArray();
        }

        var indices = new List<int>(tickCount);
        for (var slot = 0; slot < tickCount; slot++)
        {
            var index = (int)Math.Round(
                (slot * (generationCount - 1d)) / Math.Max(1d, tickCount - 1d),
                MidpointRounding.AwayFromZero);
            if (indices.Count == 0 || indices[^1] != index)
            {
                indices.Add(index);
            }
        }

        if (indices[^1] != generationCount - 1)
        {
            indices[^1] = generationCount - 1;
        }

        return indices;
    }

    private static FormattedText CreateLabelText(string text, double fontSize)
        => new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            fontSize,
            LabelBrush);

    private static FormattedText CreateTitleText(string text, double fontSize)
        => new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            TitleTypeface,
            fontSize,
            LabelBrush);
}
