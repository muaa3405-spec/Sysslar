using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Tasky.Controls;

/// <summary>
/// A line icon from Resources/Icons.axaml (a 24×24 geometry drawn as a stroke).
/// The colour follows the inherited Foreground, so a button's hover style can recolour it.
/// </summary>
public sealed class LineIcon : Control
{
    private const double Grid = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 18);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.9);

    /// <summary>Also fill the shape (for solid glyphs such as the play triangle).</summary>
    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<LineIcon, bool>(nameof(IsFilled));

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, IsFilledProperty);
        AffectsMeasure<LineIcon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsFilled { get => GetValue(IsFilledProperty); set => SetValue(IsFilledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is null || Foreground is null) return;
        double scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        if (scale <= 0) return;
        double dx = (Bounds.Width - Grid * scale) / 2, dy = (Bounds.Height - Grid * scale) / 2;

        // StrokeThickness is in grid units, so the line scales with the icon like an SVG would
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(IsFilled ? Foreground : null, pen, Data);
    }
}
