using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace VibeTun.Views.Controls;

/// <summary>
/// Плавное перетаскивание строк плоских списков (как у профилей): FLIP-анимация
/// перестроения + полупрозрачное превью за курсором. Используют левая панель
/// (NavSectionMenu), DNS и DPI. У профилей свой, более сложный вариант с группами
/// (см. ProfilesView) — его не трогаем.
/// </summary>
public static class ReorderDrag
{
    /// <summary>Снимок экранных позиций строк относительно хоста — «до» для FLIP.</summary>
    public static Dictionary<FrameworkElement, Point> Capture(FrameworkElement host,
        IEnumerable<FrameworkElement> rows)
    {
        var result = new Dictionary<FrameworkElement, Point>();
        foreach (var row in rows)
        {
            if (row.ActualWidth <= 0 && row.ActualHeight <= 0) continue;
            result[row] = row.TranslatePoint(new Point(0, 0), host);
        }
        return result;
    }

    /// <summary>
    /// FLIP: после смены порядка и UpdateLayout строки плавно доезжают со старых мест
    /// на новые (190мс, как перетаскивание профилей).
    /// </summary>
    public static void AnimateReflow(FrameworkElement host, Dictionary<FrameworkElement, Point> before,
        IEnumerable<FrameworkElement> rows)
    {
        foreach (var row in rows)
        {
            if (!before.TryGetValue(row, out var old)) continue;
            var now = row.TranslatePoint(new Point(0, 0), host);
            // текущий трансформ (хвост прошлой анимации) — не часть нового слота
            if (row.RenderTransform is TranslateTransform current)
            {
                now.X -= current.X;
                now.Y -= current.Y;
            }
            var dx = old.X - now.X;
            var dy = old.Y - now.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            var move = Motion.WritableTransform<TranslateTransform>(row);
            Motion.Run(move, TranslateTransform.XProperty, dx, 0, Motion.Scaled(190), EasingMode.EaseOut);
            Motion.Run(move, TranslateTransform.YProperty, dy, 0, Motion.Scaled(190), EasingMode.EaseOut);
        }
    }

    /// <summary>Полупрозрачный снимок строки, летящий за курсором (как у профилей).</summary>
    public sealed class Preview : Adorner
    {
        private readonly Brush _brush;
        private readonly Point _grab;
        private readonly Size _size;
        private Point _pointer;

        public Preview(UIElement adorned, FrameworkElement source, Point grabOffset) : base(adorned)
        {
            IsHitTestVisible = false;
            Opacity = 0.9;
            _brush = CaptureBrush(source);
            _grab = new Point(
                Math.Clamp(grabOffset.X, 0, source.ActualWidth),
                Math.Clamp(grabOffset.Y, 0, source.ActualHeight));
            _size = source.RenderSize;
        }

        public void MoveTo(Point pointer)
        {
            _pointer = pointer;
            InvalidateVisual();
        }

        private static Brush CaptureBrush(FrameworkElement source)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(source);
                var width = Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
                var height = Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                bitmap.Render(source);
                if (bitmap.CanFreeze) bitmap.Freeze();
                return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            }
            catch (InvalidOperationException)
            {
                return new VisualBrush(source) { Stretch = Stretch.Fill };
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_size.Width <= 0 || _size.Height <= 0) return;
            var topLeft = new Point(_pointer.X - _grab.X, _pointer.Y - _grab.Y);
            drawingContext.DrawRectangle(_brush, null, new Rect(topLeft, _size));
        }
    }
}
