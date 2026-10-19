using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeTun.Views;

namespace VibeTun.Views.Dialogs;

/// <summary>
/// База всех модальных окон: та же рамка, что у главного окна.
/// Windows 11 — обычное окно с системными углами DWM (ROUND);
/// Windows 10 — layered-окно с прозрачным фоном, содержимое обрезается
/// по скруглённой рамке (радиус 12, как RootBorder в MainWindow).
/// Содержимое из XAML автоматически оборачивается в рамку WindowBg + Divider,
/// в XAML диалогов ничего докручивать не нужно.
/// </summary>
public class DialogWindow : Window
{
    private Border? _frame;

    public DialogWindow()
    {
        // AllowsTransparency нельзя менять после создания HWND — как в MainWindow.
        if (!DwmCorners.IsWindows11)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }
        Loaded += OnFrameLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmCorners.ApplyRound(this);
    }

    private void OnFrameLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFrameLoaded;
        // XAML задаёт окну WindowBg — на Windows 10 возвращаем прозрачность.
        if (!DwmCorners.IsWindows11)
            Background = Brushes.Transparent;
        if (_frame is not null || Content is not UIElement content) return;

        Content = null;
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            Child = content
        };
        _frame.SetResourceReference(BackgroundProperty, "WindowBg");
        _frame.SetResourceReference(BorderBrushProperty, "Divider");
        _frame.SizeChanged += (_, _) => UpdateFrame();
        Content = _frame;
        UpdateFrame();
    }

    private void UpdateFrame()
    {
        if (_frame is null) return;
        if (DwmCorners.IsWindows11)
        {
            _frame.CornerRadius = new CornerRadius(0);
            _frame.Clip = null;
            return;
        }

        const int radius = 12;
        _frame.CornerRadius = new CornerRadius(radius);
        if (_frame.ActualWidth <= 0 || _frame.ActualHeight <= 0) return;
        _frame.Clip = new RectangleGeometry(
            new Rect(0, 0, _frame.ActualWidth, _frame.ActualHeight), radius, radius);
    }
}
