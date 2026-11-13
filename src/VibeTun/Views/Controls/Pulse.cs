using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace VibeTun.Views.Controls;

/// <summary>
/// Пульсация элемента, когда есть обновление: подсвечиваемый элемент (точка в навигации,
/// кружочек секции «Обновление») мерцает между двумя уровнями прозрачности, пока прикреплённое
/// свойство <see cref="OnProperty"/> true. Обычные элементы (Ellipse) пульсируют сами; для Control
/// цель ищется как дочерний элемент <c>outline</c> шаблона, если шаблон ещё не применён —
/// запуск откладывается до Loaded.
/// </summary>
public static class Pulse
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached(
        "On", typeof(bool), typeof(Pulse), new PropertyMetadata(false, OnChanged));

    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);
    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);

    /// <summary>Элемент, который реально анимируется (outline из шаблона или сам элемент).</summary>
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(FrameworkElement), typeof(Pulse), new PropertyMetadata(null));

    /// <summary>Прозрачность до пульсации — восстанавливается при остановке.</summary>
    private static readonly DependencyProperty BaseOpacityProperty = DependencyProperty.RegisterAttached(
        "BaseOpacity", typeof(double), typeof(Pulse), new PropertyMetadata(double.NaN));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not bool on) return;
        if (on) Start(d);
        else Stop(d);
    }

    private static void Start(DependencyObject d)
    {
        if (d is not FrameworkElement fe) return;

        var target = ResolveTarget(fe, out var defer);
        if (target is null)
        {
            if (defer)
            {
                fe.Loaded -= OnElementLoaded;
                fe.Loaded += OnElementLoaded;
            }
            return;
        }
        Animate(d, target);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Loaded -= OnElementLoaded;
        if (GetOn(fe)) Start(fe);
    }

    /// <summary>
    /// Цель анимации: <c>outline</c> из шаблона, иначе сам элемент.
    /// <paramref name="defer"/> — шаблон ещё не применён, повторить после Loaded.
    /// </summary>
    private static FrameworkElement? ResolveTarget(FrameworkElement fe, out bool defer)
    {
        defer = false;
        if (fe is Control control)
        {
            var outline = control.Template?.FindName("outline", control) as FrameworkElement;
            if (outline is not null) return outline;
            if (!control.IsLoaded)
            {
                defer = true;
                return null;
            }
        }
        return fe;
    }

    private static void Animate(DependencyObject owner, FrameworkElement target)
    {
        if (!double.IsNaN((double)owner.GetValue(BaseOpacityProperty))) return; // уже пульсирует

        var baseOpacity = target.Opacity;
        owner.SetValue(BaseOpacityProperty, baseOpacity);
        owner.SetValue(TargetProperty, target);

        // базовая прозрачность 0 (контур скрыт) — пульсируем от 1 до 0.35, иначе от base до base*0.35
        var from = baseOpacity <= 0 ? 1.0 : baseOpacity;
        var to = baseOpacity <= 0 ? 0.35 : baseOpacity * 0.35;

        var animation = new DoubleAnimation(from, to, Motion.Scaled(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        target.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static void Stop(DependencyObject d)
    {
        if (d is FrameworkElement fe)
        {
            fe.Loaded -= OnElementLoaded;
        }

        if (d.GetValue(BaseOpacityProperty) is not double baseOpacity || double.IsNaN(baseOpacity)) return;
        d.ClearValue(BaseOpacityProperty);

        if (d.GetValue(TargetProperty) is not FrameworkElement target) return;
        d.ClearValue(TargetProperty);

        target.BeginAnimation(UIElement.OpacityProperty, null);
        if (Math.Abs(target.Opacity - baseOpacity) > 0.001) target.Opacity = baseOpacity;
    }
}
