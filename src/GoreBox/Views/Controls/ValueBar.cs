using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GoreBox.Views.Controls;

/// <summary>
/// Ползунок в стиле полоски прозрачности (<see cref="AlphaBar"/>): тонкая полоска без видимой ручки,
/// заполнение акцентным цветом слева. Значение находится в диапазоне Minimum..Maximum и
/// прилипает к шагу Step. Тянуть можно в любом месте контрола.
/// </summary>
public sealed class ValueBar : FrameworkElement
{
    private const double StripHeight = 6;

    private bool _dragging;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ValueBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ValueBar),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ValueBar),
        new FrameworkPropertyMetadata(0.1d, FrameworkPropertyMetadataOptions.AffectsRender));

    // TwoWay по умолчанию: привязка к модели получает значение, которое поменяла мышь.
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ValueBar),
        new FrameworkPropertyMetadata(0d,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Шаг значения; 0 — без прилипания.</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ValueBar()
    {
        IsHitTestVisible = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // невидимая подложка на весь контрол — по ней удобно попадать
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double top = (h - StripHeight) / 2;
        double r = StripHeight / 2;
        var strip = new Rect(0, top, w, StripHeight);

        var track = TryFindResource("InputBg") as Brush ?? Brushes.Transparent;
        var edge = TryFindResource("InputBorder") as Brush ?? Brushes.Gray;
        dc.DrawRoundedRectangle(track, new Pen(edge, 1), strip, r, r);

        double fraction = FractionOf(Value);
        if (fraction > 0)
        {
            var fill = TryFindResource("Accent") as Brush ?? Brushes.Gray;
            dc.DrawRoundedRectangle(fill, null, new Rect(0, top, w * fraction, StripHeight), r, r);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    private double FractionOf(double value)
    {
        double span = Maximum - Minimum;
        return span <= 0 ? 0 : Math.Clamp((value - Minimum) / span, 0, 1);
    }

    private void SetFromX(double x)
    {
        if (ActualWidth <= 0) return;

        double raw = Minimum + Math.Clamp(x / ActualWidth, 0, 1) * (Maximum - Minimum);
        double stepped = Step > 0 ? Math.Round((raw - Minimum) / Step) * Step + Minimum : raw;
        // округление до сотых убирает хвосты двоичной арифметики (0.30000000000000004 и т.п.)
        SetCurrentValue(ValueProperty, Math.Round(Math.Clamp(stepped, Minimum, Maximum), 2));
    }
}
