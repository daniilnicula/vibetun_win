using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeTun.Views.Controls;

/// <summary>
/// Общие помощники анимаций интерфейса (только WPF, без сторонних библиотек).
/// Анимация ведёт свойство через <see cref="DoubleAnimation"/> с <see cref="FillBehavior.Stop"/>:
/// итоговое значение записывается локально до старта, поэтому по окончании ничего не «откатывается»,
/// а прерванная анимация продолжается с текущего видимого значения (GetValue отдаёт анимированное).
/// </summary>
public static class Motion
{
    /// <summary>Допустимый диапазон множителя скорости.</summary>
    public const double MinSpeed = 0.3;
    public const double MaxSpeed = 2.0;

    /// <summary>Текущий множитель скорости: 1 — как задумано, 2 — вдвое быстрее, 0.3 — медленнее.</summary>
    public static double Speed { get; private set; } = 1.0;

    /// <summary>Быстрые переходы: нажатие, начало наведения.</summary>
    public static TimeSpan Fast => Scaled(110);

    /// <summary>Обычные переходы: подсветка при наведении, шеврон, раскрытие разделов.</summary>
    public static TimeSpan Normal => Scaled(200);

    /// <summary>Переезд подсветки и «листание» страниц.</summary>
    public static TimeSpan Slide => Scaled(280);

    /// <summary>
    /// Задать скорость всех анимаций интерфейса: код (Motion.Run/To, переезды подсветки, листание страниц)
    /// и прикреплённые свойства MotionProps, которыми ведут XAML-стили. Значение ограничивается диапазоном и округляется до 0.1.
    /// </summary>
    public static void SetSpeed(double speed)
    {
        Speed = Math.Round(Math.Clamp(speed, MinSpeed, MaxSpeed), 1);
    }

    /// <summary>Длительность при базовой скорости 1× в миллисекундах, с учётом текущего множителя.</summary>
    public static TimeSpan Scaled(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds / Speed);

    /// <summary>Анимировать свойство от from к to; to остаётся итоговым значением.</summary>
    public static void Run(DependencyObject target, DependencyProperty property, double from, double to,
        TimeSpan duration, EasingMode easing = EasingMode.EaseOut, Action? done = null)
    {
        target.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easing },
            FillBehavior = FillBehavior.Stop,
        };
        if (done is not null) animation.Completed += (_, _) => done();

        switch (target)
        {
            case UIElement element:
                element.BeginAnimation(property, animation);
                break;
            case Animatable animatable:
                animatable.BeginAnimation(property, animation);
                break;
        }
    }

    /// <summary>Плавно повести свойство от текущего видимого значения к to.</summary>
    public static void To(DependencyObject target, DependencyProperty property, double to, TimeSpan duration,
        Action? done = null)
    {
        var from = (double)target.GetValue(property);
        Run(target, property, from, to, duration, EasingMode.EaseOut, done);
    }

    /// <summary>
    /// Трансформация элемента, которую можно менять. Трансформации, объявленные в XAML внутри шаблона или
    /// DataTemplate, WPF замораживает (IsFrozen), менять их нельзя — тогда на самом элементе ставится
    /// изменяемая трансформация того же типа, она и возвращается. Шаблон при этом не трогается.
    /// </summary>
    public static T WritableTransform<T>(UIElement element) where T : Transform, new()
    {
        if (element.RenderTransform is T current && !current.IsFrozen) return current;

        var fresh = new T();
        element.RenderTransform = fresh;
        return fresh;
    }

    /// <summary>Масштаб по обеим осям — для «вдавливания» кнопки.</summary>
    public static void PressScale(ScaleTransform scale, double value)
    {
        To(scale, ScaleTransform.ScaleXProperty, value, Fast);
        To(scale, ScaleTransform.ScaleYProperty, value, Fast);
    }

    /// <summary>
    /// Наведение и нажатие для Border-шапки без собственного шаблона (заголовки «Панели»/«SSH»):
    /// подсветка проявляется плавно, при нажатии шапка чуть сжимается. Шапка должна содержать Panel,
    /// подсветка кладётся в него первым ребёнком.
    /// </summary>
    public static void AttachHoverPress(Border header)
    {
        if (header.Child is not Panel panel) return;

        var hover = new Border
        {
            CornerRadius = header.CornerRadius,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        hover.SetResourceReference(Border.BackgroundProperty, "CardHover");
        panel.Children.Insert(0, hover);

        var scale = new ScaleTransform(1, 1);
        header.RenderTransformOrigin = new Point(0.5, 0.5);
        header.RenderTransform = scale;

        header.MouseEnter += (_, _) => To(hover, UIElement.OpacityProperty, 1, Fast);
        header.MouseLeave += (_, _) =>
        {
            To(hover, UIElement.OpacityProperty, 0, Normal);
            PressScale(scale, 1);
        };
        // подписка с handledEventsToo: у шапки уже есть обработчик, который помечает нажатие как обработанное
        header.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => PressScale(scale, 0.97)), true);
        header.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((_, _) => PressScale(scale, 1)), true);
    }
}
