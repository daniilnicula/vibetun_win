using System.Windows;
using System.Windows.Media;

namespace VibeTun.Utils;

/// <summary>Геометрия элементов интерфейса в экранных координатах (DIP) — для окон, которые «вырастают» из кнопки.</summary>
public static class ScreenGeometry
{
    /// <summary>Прямоугольник элемента на экране в DIP; null, если элемент ещё не показан.</summary>
    public static Rect? Of(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var fromDevice = FromDevice(element);
        if (fromDevice is null) return null;

        var corners = new[]
        {
            element.PointToScreen(new Point(0, 0)),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight))
        };
        fromDevice.Value.Transform(corners);
        return new Rect(corners[0], corners[1]);
    }

    /// <summary>Центр элемента на экране в DIP; null, если элемент ещё не показан.</summary>
    public static Point? CenterOf(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var fromDevice = FromDevice(element);
        if (fromDevice is null) return null;

        var center = new[] { element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2)) };
        fromDevice.Value.Transform(center);
        return center[0];
    }

    // PointToScreen отдаёт физические пиксели; переводим в независимые единицы WPF с учётом DPI.
    private static Matrix? FromDevice(FrameworkElement element) =>
        PresentationSource.FromVisual(element)?.CompositionTarget?.TransformFromDevice;
}
