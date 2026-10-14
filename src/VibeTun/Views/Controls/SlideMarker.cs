using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeTun.Views.Controls;

/// <summary>
/// Подсветка «текущего» элемента, которая переезжает от прежнего элемента к новому, а не появляется
/// заново на новом месте. Один маркер (Border) на весь список или ряд сегментов: элемент хоста
/// кладётся первым ребёнком и подсвечивает пункт, который отдаёт <c>active</c>.
/// Цвет заливки может зависеть от элемента (<c>keyOf</c> — ключ ресурса кисти): тогда цвет плавно меняется.
/// </summary>
public sealed class SlideMarker
{
    private readonly FrameworkElement _host;
    private readonly Border _marker;
    private readonly Func<FrameworkElement?> _active;
    private readonly Func<FrameworkElement, string> _keyOf;
    private readonly TranslateTransform _move = new();
    private readonly Tween _tween = new();

    private bool _visible;
    private bool _hasRect;
    private bool _animating;
    private bool _colorLerp;
    private int _token;

    private Rect _from;       // откуда едем
    private Rect _to;         // куда едем (или уже приехали)
    private Rect _shown;      // что нарисовано сейчас
    private string _key = "";      // ключ цвета цели
    private string _paintKey = ""; // ключ цвета, который нарисован сейчас
    private Color? _colorFrom;
    private Color? _colorTo;

    public SlideMarker(FrameworkElement host, Border marker, Func<FrameworkElement?> active,
        Func<FrameworkElement, string> keyOf)
    {
        _host = host;
        _marker = marker;
        _active = active;
        _keyOf = keyOf;

        _marker.HorizontalAlignment = HorizontalAlignment.Left;
        _marker.VerticalAlignment = VerticalAlignment.Top;
        _marker.IsHitTestVisible = false;
        _marker.RenderTransform = _move;
        _marker.Visibility = Visibility.Collapsed;
        _tween.OnValue = OnTween;
    }

    /// <summary>
    /// Привести подсветку к активному элементу. Повторный вызов с тем же положением ничего не делает,
    /// поэтому его можно звать из LayoutUpdated. animate=false — сразу (раскладка, перетаскивание).
    /// </summary>
    public void Sync(bool animate)
    {
        var item = _active();
        var target = item is null ? null : TargetOf(item);
        if (item is null || target is null)
        {
            Hide(animate);
            return;
        }

        var key = _keyOf(item);
        var rect = target.Value;

        if (!_visible)
        {
            // появление: сначала встаём на место, затем проявляемся
            _visible = true;
            _marker.Visibility = Visibility.Visible;
            Place(rect, key);
            Fade(1, animate);
            return;
        }

        if (rect == _to && key == _key) return;
        Move(rect, key, animate);
    }

    private void Move(Rect target, string key, bool animate)
    {
        var fromKey = _paintKey;
        if (!animate || !_hasRect)
        {
            Place(target, key);
            return;
        }

        StopMove();
        var token = _token;

        _from = _shown;
        _to = target;
        _key = key;
        _colorFrom = ColorOf(fromKey);
        _colorTo = ColorOf(key);
        _colorLerp = key != fromKey && _colorFrom is not null && _colorTo is not null;
        _animating = true;

        var animation = new DoubleAnimation(0, 1, Motion.Slide)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) =>
        {
            if (token == _token) Finish();
        };
        _tween.BeginAnimation(Tween.ValueProperty, animation);
    }

    /// <summary>Мгновенно поставить подсветку в точку (без анимации), отменив идущий переезд.</summary>
    private void Place(Rect rect, string key)
    {
        StopMove();
        _to = rect;
        _key = key;
        Paint(rect);
        SetKey(key);
    }

    private void Finish()
    {
        _animating = false;
        Paint(_to);
        SetKey(_key);
    }

    private void StopMove()
    {
        _token++;
        _animating = false;
        _tween.BeginAnimation(Tween.ValueProperty, null);
    }

    private void OnTween(double t)
    {
        if (!_animating) return;
        Paint(Lerp(_from, _to, t));
        if (_colorLerp && _colorFrom is { } a && _colorTo is { } b)
            _marker.Background = new SolidColorBrush(Lerp(a, b, t));
    }

    private void Hide(bool animate)
    {
        if (!_visible) return;
        _visible = false;
        StopMove();
        Fade(0, animate);
    }

    private void Fade(double to, bool animate)
    {
        if (!animate)
        {
            _marker.BeginAnimation(UIElement.OpacityProperty, null);
            _marker.Opacity = to;
            if (to == 0) _marker.Visibility = Visibility.Collapsed;
            return;
        }

        Motion.Run(_marker, UIElement.OpacityProperty, _marker.Opacity, to, Motion.Normal, EasingMode.EaseOut,
            to == 0 ? () =>
            {
                if (!_visible) _marker.Visibility = Visibility.Collapsed;
            } : null);
    }

    private void Paint(Rect rect)
    {
        _shown = rect;
        _hasRect = true;
        _move.X = rect.X;
        _move.Y = rect.Y;
        _marker.Width = rect.Width;
        _marker.Height = rect.Height;
    }

    private void SetKey(string key)
    {
        _paintKey = key;
        _marker.SetResourceReference(Border.BackgroundProperty, key);
    }

    /// <summary>Положение элемента в координатах хоста; null — элемент не нарисован.</summary>
    private Rect? TargetOf(FrameworkElement item)
    {
        if (!item.IsVisible || item.ActualWidth <= 0 || item.ActualHeight <= 0) return null;
        try
        {
            var origin = item.TranslatePoint(new Point(0, 0), _host);
            return new Rect(origin.X, origin.Y, item.ActualWidth, item.ActualHeight);
        }
        catch (InvalidOperationException)
        {
            return null;   // элемент вне дерева хоста
        }
    }

    private Color? ColorOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return (_host.TryFindResource(key) as SolidColorBrush)?.Color;
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        Math.Max(0, a.Width + (b.Width - a.Width) * t),
        Math.Max(0, a.Height + (b.Height - a.Height) * t));

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        Mix(a.A, b.A, t), Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Clamp(a + (b - a) * t + 0.5, 0, 255);

    /// <summary>Невидимый носитель прогресса 0..1: его значение приходит в OnTween на каждом кадре.</summary>
    private sealed class Tween : Animatable
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(double), typeof(Tween), new PropertyMetadata(0.0, OnValueChanged));

        public Action<double>? OnValue { get; set; }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((Tween)d).OnValue?.Invoke((double)e.NewValue);

        protected override Freezable CreateInstanceCore() => new Tween();
    }
}
