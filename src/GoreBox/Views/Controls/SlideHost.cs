using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GoreBox.Views.Controls;

/// <summary>
/// Контейнер страниц-вкладок: переключение «листает» страницы — уходящая уезжает в сторону,
/// приходящая въезжает с противоположной. Страницы помечаются <c>ctl:SlideHost.Key="..."</c>,
/// текущая задаётся <see cref="SelectedKey"/>.
/// Направление: если задан <see cref="PositionOf"/> (позиция страницы по ключу), то от старой позиции к новой;
/// иначе — по порядку страниц в разметке. <see cref="Vertical"/> — листание вверх/вниз вместо влево/вправо.
/// <see cref="AnimateHeight"/> — для контейнеров переменной высоты: высота плавно подстраивается
/// под новую страницу.
/// </summary>
public class SlideHost : Grid
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(SlideHost), new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(SlideHost),
        new PropertyMetadata(null, (d, e) => ((SlideHost)d).OnSelectedKeyChanged(e.NewValue as string)));

    public static readonly DependencyProperty AnimateHeightProperty = DependencyProperty.Register(
        nameof(AnimateHeight), typeof(bool), typeof(SlideHost), new PropertyMetadata(false));

    private UIElement? _current;

    public SlideHost()
    {
        ClipToBounds = true;   // уезжающая страница не должна вылезать за края
        Loaded += (_, _) => ShowInstant(SelectedKey);
    }

    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public bool AnimateHeight
    {
        get => (bool)GetValue(AnimateHeightProperty);
        set => SetValue(AnimateHeightProperty, value);
    }

    /// <summary>Листать по вертикали (страницы уезжают вверх/вниз), а не по горизонтали.</summary>
    public bool Vertical { get; set; }

    /// <summary>
    /// Позиция страницы по ключу, например, место пункта в списке навигации. Отрицательное значение — неизвестно.
    /// </summary>
    public Func<string?, int>? PositionOf { get; set; }

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private void OnSelectedKeyChanged(string? key)
    {
        var to = FindPage(key);
        var from = _current;
        var size = AxisSize();
        if (!IsLoaded || from is null || to is null || ReferenceEquals(from, to) || size <= 0)
        {
            ShowInstant(key);
            return;
        }

        Slide(from, to, DirectionBetween(from, to), size);
    }

    /// <summary>Без анимации: показать одну страницу и сбросить сдвиги.</summary>
    private void ShowInstant(string? key)
    {
        var page = FindPage(key);
        foreach (UIElement child in Children)
        {
            child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
            var move = MoveOf(child);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = 0;
            move.Y = 0;
        }
        _current = page;
    }

    /// <summary>+1 — страница «дальше» по списку (листаем вперёд), −1 — назад.</summary>
    private int DirectionBetween(UIElement from, UIElement to)
    {
        if (PositionOf is { } position)
        {
            var a = position(GetKey(from));
            var b = position(GetKey(to));
            if (a >= 0 && b >= 0 && a != b) return b > a ? 1 : -1;
        }
        return Children.IndexOf(to) > Children.IndexOf(from) ? 1 : -1;
    }

    private void Slide(UIElement from, UIElement to, int direction, double size)
    {
        var property = AxisProperty();
        var toMove = MoveOf(to);
        var fromMove = MoveOf(from);

        // если страница ещё едет (быстрые клики) — продолжаем с её текущего места
        var toStart = to.Visibility == Visibility.Visible ? Offset(toMove) : direction * size;
        var fromStart = Offset(fromMove);

        to.Visibility = Visibility.Visible;
        Motion.Run(toMove, property, toStart, 0, Motion.Slide, EasingMode.EaseInOut);
        Motion.Run(fromMove, property, fromStart, -direction * size, Motion.Slide,
            EasingMode.EaseInOut, () =>
            {
                if (!ReferenceEquals(_current, from)) from.Visibility = Visibility.Collapsed;
            });

        _current = to;
        if (AnimateHeight) AnimateHeightTo(to);
    }

    /// <summary>Высота контейнера: от текущей к высоте новой страницы.</summary>
    private void AnimateHeightTo(UIElement to)
    {
        var fromHeight = ActualHeight;
        to.Measure(new Size(ActualWidth, double.PositiveInfinity));
        var toHeight = to.DesiredSize.Height;

        Motion.Run(this, HeightProperty, fromHeight, toHeight, Motion.Slide, EasingMode.EaseInOut,
            () => Height = double.NaN);
    }

    private double AxisSize() => Vertical ? ActualHeight : ActualWidth;

    private DependencyProperty AxisProperty() => Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

    private double Offset(TranslateTransform move) => Vertical ? move.Y : move.X;

    /// <summary>Сдвиг страницы; изменяемый даже если трансформация пришла из шаблона (см. Motion.WritableTransform).</summary>
    private static TranslateTransform MoveOf(UIElement element) => Motion.WritableTransform<TranslateTransform>(element);

    private UIElement? FindPage(string? key)
    {
        foreach (UIElement child in Children)
            if (string.Equals(GetKey(child), key, StringComparison.Ordinal)) return child;
        return null;
    }
}
