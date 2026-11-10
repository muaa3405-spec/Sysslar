using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Tasky.Controls;

/// <summary>
/// A pill-shaped segmented control: its children (RadioButtons with the SegmentItem theme)
/// get equal widths, and a highlight pill slides behind the checked one.
/// </summary>
public sealed class SegmentedBar : Panel
{
    private const double Inset = 4;

    private readonly Border _track     = new() { IsHitTestVisible = false };
    private readonly Border _highlight = new() { IsHitTestVisible = false };

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(HighlightBrush));

    /// <summary>Left edge of the highlight, animated through Transitions.</summary>
    public static readonly StyledProperty<double> HighlightOffsetProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(HighlightOffset));

    static SegmentedBar()
    {
        AffectsArrange<SegmentedBar>(HighlightOffsetProperty);
    }

    public SegmentedBar()
    {
        Children.Add(_track);
        Children.Add(_highlight);
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = HighlightOffsetProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing   = new CubicEaseOut(),
            },
        };
        Children.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (var child in e.NewItems.OfType<ToggleButton>())
                child.IsCheckedChanged += (_, _) => UpdateHighlight(animate: true);
        };
    }

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public double HighlightOffset { get => GetValue(HighlightOffsetProperty); set => SetValue(HighlightOffsetProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackBrushProperty)     _track.Background     = TrackBrush;
        if (change.Property == HighlightBrushProperty) _highlight.Background = HighlightBrush;
    }

    private List<Control> Segments => Children.Skip(2).ToList();

    private double SegmentWidth(double totalWidth)
    {
        int n = Children.Count - 2;
        return n <= 0 ? 0 : (totalWidth - 2 * Inset) / n;
    }

    private int CheckedIndex
    {
        get
        {
            var segments = Segments;
            for (int i = 0; i < segments.Count; i++)
                if (segments[i] is ToggleButton { IsChecked: true }) return i;
            return -1;
        }
    }

    private void UpdateHighlight(bool animate)
    {
        double target = Inset + Math.Max(0, CheckedIndex) * SegmentWidth(Bounds.Width);
        _highlight.IsVisible = CheckedIndex >= 0;
        if (animate && Helpers.Motion.Enabled) { HighlightOffset = target; return; }
        var transitions = Transitions;
        Transitions = null;
        HighlightOffset = target;
        Transitions = transitions;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var segments = Segments;
        int n = Math.Max(1, segments.Count);
        double widest = 0, tallest = 0;
        var childAvailable = new Size(Math.Max(0, (availableSize.Width - 2 * Inset) / n), double.PositiveInfinity);
        foreach (var child in segments)
        {
            child.Measure(childAvailable);
            widest  = Math.Max(widest, child.DesiredSize.Width);
            tallest = Math.Max(tallest, child.DesiredSize.Height);
        }
        _track.Measure(availableSize);
        _highlight.Measure(availableSize);
        return new Size(widest * segments.Count + 2 * Inset, Math.Max(32, tallest) + 2 * Inset);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var segments = Segments;
        double w = SegmentWidth(finalSize.Width);
        double h = Math.Max(0, finalSize.Height - 2 * Inset);
        _track.CornerRadius = new CornerRadius(finalSize.Height / 2);
        _track.Arrange(new Rect(finalSize));
        _highlight.CornerRadius = new CornerRadius(h / 2);
        _highlight.Arrange(new Rect(HighlightOffset, Inset, w, h));
        for (int i = 0; i < segments.Count; i++)
            segments[i].Arrange(new Rect(Inset + i * w, Inset, w, h));

        // A new width moves the target: jump there without animating
        if (finalSize != _lastArranged)
        {
            _lastArranged = finalSize;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateHighlight(animate: false));
        }
        return finalSize;
    }

    private Size _lastArranged;
}
