using System.Windows;
using System.Windows.Controls;

namespace Taste.Controls;

public sealed class JustifiedWrapPanel : Panel
{
    public static readonly DependencyProperty MinGapProperty =
        DependencyProperty.Register(nameof(MinGap), typeof(double), typeof(JustifiedWrapPanel),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxGapProperty =
        DependencyProperty.Register(nameof(MaxGap), typeof(double), typeof(JustifiedWrapPanel),
            new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinGap { get => (double)GetValue(MinGapProperty); set => SetValue(MinGapProperty, value); }
    public double MaxGap { get => (double)GetValue(MaxGapProperty); set => SetValue(MaxGapProperty, value); }

    // Current column count from the last layout pass — lets callers (e.g. Up/Down
    // arrow-key handling) jump by a full row without duplicating the layout math.
    public int Columns { get; private set; } = 1;

    // Compute column count and clamped gap from available width.
    // MinGap is baked into the column formula so gap never falls below it.
    // MaxGap caps the gap; when hit, items left-align with the max gap applied.
    private (int cols, double gap) Layout(double width, double itemW)
    {
        int cols = Math.Max(1, (int)((width + MinGap) / (itemW + MinGap)));
        double gap = cols > 1 ? Math.Min((width - cols * itemW) / (cols - 1), MaxGap) : 0;
        return (cols, Math.Max(gap, 0));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return Size.Empty;

        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double itemW = InternalChildren[0].DesiredSize.Width;
        double itemH = InternalChildren[0].DesiredSize.Height;
        if (itemW == 0) return Size.Empty;

        var (cols, _) = Layout(availableSize.Width, itemW);
        Columns = cols;
        int rows = (InternalChildren.Count + cols - 1) / cols;

        return new Size(availableSize.Width, rows * itemH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;

        double itemW = InternalChildren[0].DesiredSize.Width;
        double itemH = InternalChildren[0].DesiredSize.Height;
        if (itemW == 0) return finalSize;

        var (cols, gap) = Layout(finalSize.Width, itemW);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            InternalChildren[i].Arrange(new Rect(col * (itemW + gap), row * itemH, itemW, itemH));
        }

        return finalSize;
    }
}
