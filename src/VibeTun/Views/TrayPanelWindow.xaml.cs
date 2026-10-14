using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace VibeTun.Views;

/// <summary>
/// Мини-панель у иконки в трее. Открывается одиночным кликом по иконке, закрывается,
/// когда пользователь нажимает вне окна (потеря активации), по Esc или повторному клику по иконке.
/// Двойной клик открывает полное окно приложения (его обрабатывает <see cref="Services.TrayService"/>).
/// </summary>
public partial class TrayPanelWindow : Window
{
    /// <summary>Отступ от края рабочей области и от иконки.</summary>
    private const double Gap = 8;

    /// <summary>Если панель закрылась незадолго до клика по иконке — клик её «закрывает», а не открывает заново.</summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(500);

    private DateTime _hiddenAtUtc = DateTime.MinValue;
    private bool _hiding;   // защита от повторного входа: Hide() может вызвать Deactivated
    private System.Drawing.Point _anchor;   // точка у иконки, относительно которой стоит панель

    public TrayPanelWindow()
    {
        InitializeComponent();

        // клик вне окна: окно теряет активацию — прячем его
        Deactivated += (_, _) => HidePanel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HidePanel();
        };
        // содержимое меняет высоту (вкладка Proxy <-> DPI bypass): держим панель у иконки,
        // а не у верхнего края — при панели задач внизу она растёт вверх
        SizeChanged += (_, _) =>
        {
            if (IsVisible) MoveNearAnchor();
        };
        // окно живёт всё время работы приложения: закрытие (Alt+F4 и т.п.) только прячет
        Closing += (_, e) =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            e.Cancel = true;
            HidePanel();
        };
    }

    /// <summary>
    /// Переключить панель по клику на иконку.
    /// <paramref name="anchor"/> — позиция курсора в экранных (физических) пикселях, <paramref name="clickedAtUtc"/> — момент клика.
    /// </summary>
    public void Toggle(System.Drawing.Point anchor, DateTime clickedAtUtc)
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }

        // клик по иконке, который и привёл к закрытию панели (деактивация при нажатии на трей), — не открываем снова
        if (clickedAtUtc - _hiddenAtUtc < ReopenGuard) return;

        ShowAt(anchor);
    }

    /// <summary>Спрятать панель (без выхода из приложения).</summary>
    public void HidePanel()
    {
        if (_hiding || !IsVisible) return;
        _hiding = true;
        try
        {
            _hiddenAtUtc = DateTime.UtcNow;
            Hide();
        }
        finally
        {
            _hiding = false;
        }
    }

    private void ShowAt(System.Drawing.Point anchor)
    {
        _anchor = anchor;

        // сначала показываем невидимым и без прыжков: размеры известны только после раскладки
        Opacity = 0;
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        MoveNearAnchor();
        Opacity = 1;

        // чтобы клик вне окна гарантированно его закрыл, панель должна стать активной
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        Activate();
        Focus();
    }

    /// <summary>Поставить панель у иконки по текущему размеру (вызывается и при смене размера содержимого).</summary>
    private void MoveNearAnchor()
    {
        var (x, y) = Place(_anchor, ActualWidth, ActualHeight);
        Left = x;
        Top = y;
    }

    /// <summary>
    /// Координаты панели в DIP: над иконкой, если панель задач внизу (как обычно), под ней — если сверху;
    /// по горизонтали центрируем на курсоре и держим внутри рабочей области нужного монитора.
    /// </summary>
    private (double X, double Y) Place(System.Drawing.Point anchor, double width, double height)
    {
        // физические пиксели -> DIP (учитываем масштаб экрана)
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var screen = WinForms.Screen.FromPoint(anchor).WorkingArea;
        var topLeft = fromDevice.Transform(new Point(screen.Left, screen.Top));
        var bottomRight = fromDevice.Transform(new Point(screen.Right, screen.Bottom));
        var work = new Rect(topLeft, bottomRight);
        var cursor = fromDevice.Transform(new Point(anchor.X, anchor.Y));

        var x = Math.Clamp(cursor.X - width / 2, work.Left + Gap, Math.Max(work.Left + Gap, work.Right - width - Gap));

        double y;
        if (cursor.Y >= work.Bottom)          // панель задач внизу
            y = work.Bottom - height - Gap;
        else if (cursor.Y <= work.Top)        // панель задач вверху
            y = work.Top + Gap;
        else                                  // сбоку или иконка в переполнении — рядом с курсором
            y = Math.Clamp(cursor.Y - height / 2, work.Top + Gap, Math.Max(work.Top + Gap, work.Bottom - height - Gap));

        return (x, y);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
