using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using VibeTun.ViewModels;

namespace VibeTun.Views;

/// <summary>
/// Раздел «DNS» (вкладки «Основные», «Домены», «Приложения» — см. <see cref="DnsViewModel.Tab"/>).
/// Здесь только жесты основного списка — провайдеров и своих DNS: нажатие на строку (раскрыть
/// провайдера / выбрать свой DNS) и перетаскивание строк. «Системный» и «Через Proxy» — отдельный
/// список выше: они не перетаскиваются и всегда наверху. Пока для домена или приложения выбирают DNS,
/// Esc отменяет выбор и возвращает во вкладку записи.
/// </summary>
public partial class DnsView : UserControl
{
    private const string HeaderName = "EntryHeader";

    private DnsEntryVm? _pressed;     // строка, по шапке которой нажали ЛКМ
    private Point _pressPoint;
    private bool _dragging;

    public DnsView()
    {
        InitializeComponent();
    }

    private DnsViewModel? Vm => DataContext as DnsViewModel;

    /// <summary>Esc — передумали выбирать DNS для домена или приложения.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { IsPicking: true } vm) return;
        vm.CancelPick();
        e.Handled = true;
    }

    private void OnEntryPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var source = e.OriginalSource as DependencyObject;
        // у кнопок в строке (звезда, правка, удаление, серверы, «Обновить») свои нажатия
        if (FindWithin<ButtonBase>(source) is not null) return;

        if (FindHeader(source)?.DataContext is not DnsEntryVm entry) return;
        _pressed = entry;
        _pressPoint = e.GetPosition(EntryList);
    }

    private void OnEntryPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var dragged = _pressed;
        var vm = Vm;
        if (dragged is null || vm is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var pos = e.GetPosition(EntryList);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _pressPoint.Y) < 5) return;   // короткое движение — это обычное нажатие
            _dragging = true;
            dragged.IsDragging = true;
            EntryList.CaptureMouse();
        }

        AutoScroll(e);

        var from = vm.Entries.IndexOf(dragged);
        var to = TargetIndex(pos.Y, from);
        if (from < 0 || to < 0 || to == from) return;

        vm.MoveEntry(from, to);
        EntryList.UpdateLayout();   // следующее движение мыши должно видеть строки уже на новых местах
    }

    private void OnEntryPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // нажали и отпустили на той же шапке, не сдвинув — это нажатие, а не перетаскивание
        var pressed = _pressed;
        var clicked = !_dragging && pressed is not null &&
                      ReferenceEquals(FindHeader(e.OriginalSource as DependencyObject)?.DataContext, pressed);
        EndDrag();
        if (clicked && pressed is not null) Vm?.Activate(pressed);
    }

    private void OnEntryLostCapture(object sender, MouseEventArgs e)
    {
        // событие всплывает и от кнопок внутри строк — нас интересует только своё перетаскивание
        if (_dragging && !EntryList.IsMouseCaptured) EndDrag();
    }

    private void EndDrag()
    {
        var wasDragging = _dragging;
        var item = _pressed;
        _pressed = null;
        _dragging = false;
        if (item is not null) item.IsDragging = false;
        if (EntryList.IsMouseCaptured) EntryList.ReleaseMouseCapture();
        if (wasDragging) Vm?.CommitOrder();
    }

    /// <summary>
    /// Куда поставить перетаскиваемую строку: соседа обгоняем, только когда курсор прошёл его
    /// середину. Так строки разной высоты (раскрытый провайдер и короткий свой DNS) не «дрожат».
    /// </summary>
    private int TargetIndex(double y, int from)
    {
        if (from < 0) return -1;

        // вверх: самая верхняя из строк выше, чью середину курсор уже миновал
        for (var i = 0; i < from; i++)
            if (CenterOf(i) is { } center && y < center) return i;

        // вниз: самая нижняя из строк ниже, чью середину курсор миновал
        for (var i = EntryList.Items.Count - 1; i > from; i--)
            if (CenterOf(i) is { } center && y > center) return i;

        return from;
    }

    private double? CenterOf(int index)
    {
        if (EntryList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
            container.ActualHeight <= 0)
            return null;
        return container.TranslatePoint(new Point(0, container.ActualHeight / 2), EntryList).Y;
    }

    /// <summary>У края области прокрутки список сам едет вслед за перетаскиваемой строкой.</summary>
    private void AutoScroll(MouseEventArgs e)
    {
        const double edge = 28;
        const double step = 14;
        var y = e.GetPosition(ListScroll).Y;
        if (y < edge) ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset - step);
        else if (y > ListScroll.ViewportHeight - edge) ListScroll.ScrollToVerticalOffset(ListScroll.VerticalOffset + step);
    }

    /// <summary>Шапка строки (Grid «EntryHeader» из шаблона), внутри которой элемент.</summary>
    private FrameworkElement? FindHeader(DependencyObject? node)
    {
        while (node is not null && !ReferenceEquals(node, EntryList))
        {
            if (node is FrameworkElement { Name: HeaderName } header) return header;
            node = Parent(node);
        }
        return null;
    }

    /// <summary>Ближайший предок типа T, но не выше самого списка.</summary>
    private T? FindWithin<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && !ReferenceEquals(node, EntryList))
        {
            if (node is T typed) return typed;
            node = Parent(node);
        }
        return null;
    }

    /// <summary>Run внутри TextBlock — не Visual: для него идём по логическому дереву.</summary>
    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
