using System.Windows;
using System.Windows.Media;

namespace VibeTun.Views.Controls;

/// <summary>
/// Прикреплённые свойства, которые плавно ведут элемент шаблона. Триггер ставит значение обычным Setter-ом,
/// а при его смене свойство запускает анимацию через <see cref="Motion"/> (с учётом общей скорости).
/// Значение по умолчанию — «покой»: когда триггер перестаёт действовать, элемент плавно возвращается к нему.
/// Это заменяет Storyboard в XAML-стилях: раскадровки нельзя заморозить, если у них динамические значения.
/// </summary>
public static class MotionProps
{
    /// <summary>Непрозрачность элемента (UIElement.Opacity). Покой — 0. Проявление 120 мс, исчезание 200 мс.</summary>
    public static readonly DependencyProperty OpacityProperty = DependencyProperty.RegisterAttached(
        "Opacity", typeof(double), typeof(MotionProps), new PropertyMetadata(0d, OnOpacityChanged));

    /// <summary>Масштаб по обеим осям (ScaleTransform в RenderTransform элемента). Покой — 1. Нажатие 80 мс, отпускание 220 мс.</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(MotionProps), new PropertyMetadata(1d, OnScaleChanged));

    /// <summary>Сдвиг по горизонтали (TranslateTransform.X в RenderTransform элемента). Покой — 0. 160 мс в обе стороны.</summary>
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.RegisterAttached(
        "OffsetX", typeof(double), typeof(MotionProps), new PropertyMetadata(0d, OnOffsetXChanged));

    public static double GetOpacity(DependencyObject d) => (double)d.GetValue(OpacityProperty);
    public static void SetOpacity(DependencyObject d, double value) => d.SetValue(OpacityProperty, value);

    public static double GetScale(DependencyObject d) => (double)d.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject d, double value) => d.SetValue(ScaleProperty, value);

    public static double GetOffsetX(DependencyObject d) => (double)d.GetValue(OffsetXProperty);
    public static void SetOffsetX(DependencyObject d, double value) => d.SetValue(OffsetXProperty, value);

    private static void OnOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        var to = (double)e.NewValue;
        var duration = Motion.Scaled(to > (double)e.OldValue ? 120 : 200);
        Motion.To(element, UIElement.OpacityProperty, to, duration);
    }

    private static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // масштаб — RenderTransform самого элемента (Border с ScaleTransform); трансформации не адресуются по имени
        if (d is not UIElement element || element.RenderTransform is not ScaleTransform) return;
        var scale = Motion.WritableTransform<ScaleTransform>(element);
        var to = (double)e.NewValue;
        var duration = Motion.Scaled(to < (double)e.OldValue ? 80 : 220);
        Motion.To(scale, ScaleTransform.ScaleXProperty, to, duration);
        Motion.To(scale, ScaleTransform.ScaleYProperty, to, duration);
    }

    private static void OnOffsetXChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || element.RenderTransform is not TranslateTransform) return;
        var move = Motion.WritableTransform<TranslateTransform>(element);
        Motion.To(move, TranslateTransform.XProperty, (double)e.NewValue, Motion.Scaled(160));
    }
}
