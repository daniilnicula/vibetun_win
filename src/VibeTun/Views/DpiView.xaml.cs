using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using VibeTun.ViewModels;
using VibeTun.Views.Controls;

namespace VibeTun.Views;

/// <summary>
/// Раздел «DPI»: список сервисов со своими приёмами обхода. Здесь только жесты списка — нажатие
/// на строку открывает окно сервиса, а перетаскивание строки меняет порядок (как в разделе «DNS»).
/// </summary>
public partial class DpiView : UserControl
{
    private const string HeaderName = "EntryHeader";

    private DpiServiceVm? _pressed;     // строка, по шапке которой нажали ЛКМ
    private Point _pressPoint;
    private bool _dragging;
    private AdornerLayer? _dragLayer;
    private ReorderDrag.Preview? _dragPreview;

    public DpiView()
    {
        InitializeComponent();
    }

    private DpiViewModel? Vm => DataContext as DpiViewModel;

    private void OnEntryPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // у кнопок и флажка в строке свои нажатия
        if (FindWithin<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;

        if (FindHeader(e.OriginalSource as DependencyObject)?.DataContext is not DpiServiceVm service) return;
        _pressed = service;
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
            ShowPreview(dragged, e);
        }

        AutoScroll(e);
        _dragPreview?.MoveTo(pos);

        var from = vm.Services.IndexOf(dragged);
        var to = TargetIndex(pos.Y, from);
        if (from < 0 || to < 0 || to == from) return;

        var before = ReorderDrag.Capture(EntryList, DragRows());
        vm.MoveEntry(from, to);
        EntryList.UpdateLayout();   // следующее движение мыши должно видеть строки уже на новых местах
        ReorderDrag.AnimateReflow(EntryList, before, DragRows());
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
        if (_dragPreview is not null && _dragLayer is not null) _dragLayer.Remove(_dragPreview);
        _dragPreview = null;
        _dragLayer = null;
        if (wasDragging) Vm?.CommitOrder();
    }

    /// <summary>Превью перетаскиваемой строки за курсором (как у профилей).</summary>
    private void ShowPreview(DpiServiceVm dragged, MouseEventArgs e)
    {
        if (EntryList.ItemContainerGenerator.ContainerFromItem(dragged) is not FrameworkElement row) return;
        if (AdornerLayer.GetAdornerLayer(EntryList) is not { } layer) return;
        _dragLayer = layer;
        _dragPreview = new ReorderDrag.Preview(EntryList, row, e.GetPosition(row));
        layer.Add(_dragPreview);
        _dragPreview.MoveTo(e.GetPosition(EntryList));
    }

    private IEnumerable<FrameworkElement> DragRows()
    {
        var vm = Vm;
        if (vm is null) yield break;
        foreach (var service in vm.Services)
            if (EntryList.ItemContainerGenerator.ContainerFromItem(service) is FrameworkElement row)
                yield return row;
    }

    /// <summary>
    /// Куда поставить перетаскиваемую строку: соседа обгоняем, только когда курсор прошёл его середину,
    /// — так строки не «дрожат».
    /// </summary>
    private int TargetIndex(double y, int from)
    {
        if (from < 0) return -1;

        for (var i = 0; i < from; i++)
            if (CenterOf(i) is { } center && y < center) return i;

        for (var i = EntryList.Items.Count - 1; i > from; i--)
            if (CenterOf(i) is { } center && y > center) return i;

        return from;
    }

    private double? CenterOf(int index)
    {
        if (EntryList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
            container.ActualHeight <= 0)
            return null;
        var center = container.TranslatePoint(new Point(0, container.ActualHeight / 2), EntryList).Y;
        // слот в layout, а не анимированная позиция: иначе середина пролетает мимо
        // неподвижного курсора и просит обратный ход (та же правка, что у профилей)
        if (container.RenderTransform is TranslateTransform move) center -= move.Y;
        return center;
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
