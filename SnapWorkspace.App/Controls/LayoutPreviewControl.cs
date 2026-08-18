using System.Collections;
using System.Windows;
using System.Windows.Media;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Controls;

public sealed class LayoutPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(SnapLayoutDefinition),
        typeof(LayoutPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AssignedZoneIdsProperty = DependencyProperty.Register(
        nameof(AssignedZoneIds),
        typeof(IEnumerable),
        typeof(LayoutPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightAllZonesProperty = DependencyProperty.Register(
        nameof(HighlightAllZones),
        typeof(bool),
        typeof(LayoutPreviewControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public SnapLayoutDefinition? Layout
    {
        get => (SnapLayoutDefinition?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IEnumerable? AssignedZoneIds
    {
        get => (IEnumerable?)GetValue(AssignedZoneIdsProperty);
        set => SetValue(AssignedZoneIdsProperty, value);
    }

    public bool HighlightAllZones
    {
        get => (bool)GetValue(HighlightAllZonesProperty);
        set => SetValue(HighlightAllZonesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 132 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var layout = Layout;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(12, 15, 22)),
            new Pen(new SolidColorBrush(Color.FromRgb(47, 54, 69)), 1),
            bounds,
            10,
            10);
        if (layout is null || ActualWidth <= 8 || ActualHeight <= 8)
        {
            return;
        }

        var assigned = AssignedZoneIds?.Cast<object>()
            .Select(value => value?.ToString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        const double inset = 7;
        const double gap = 2.5;
        foreach (var zone in layout.Zones)
        {
            var rect = zone.Rect;
            var left = inset + rect.X * (ActualWidth - inset * 2);
            var top = inset + rect.Y * (ActualHeight - inset * 2);
            var width = rect.Width * (ActualWidth - inset * 2);
            var height = rect.Height * (ActualHeight - inset * 2);
            var zoneBounds = new Rect(
                left + gap / 2,
                top + gap / 2,
                Math.Max(2, width - gap),
                Math.Max(2, height - gap));
            var isAssigned = HighlightAllZones || assigned.Contains(zone.Id);
            var fill = isAssigned
                ? new SolidColorBrush(Color.FromRgb(113, 96, 234))
                : new SolidColorBrush(Color.FromArgb(55, 93, 102, 124));
            var border = isAssigned
                ? new Pen(new SolidColorBrush(Color.FromRgb(161, 148, 255)), 1)
                : new Pen(new SolidColorBrush(Color.FromRgb(76, 85, 105)), 1);
            drawingContext.DrawRoundedRectangle(fill, border, zoneBounds, 5, 5);
        }
    }
}
