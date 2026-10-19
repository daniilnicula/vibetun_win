using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Views.Controls;

namespace VibeTun.Views;

/// <summary>
/// Выпадающий список раздела навигации («Панели», «SSH»): строки записей (клик по строке —
/// открыть, справа карандаш — переименовать, [шестерёнка — настройки] и крестик — удалить) и
/// кнопка «+» внизу списка. Строки можно перетаскивать мышью за мышкой вверх/вниз — порядок
/// сохраняется. Список пустой — остаётся одна «+» под заголовком раздела. Содержимое рисуется
/// кодом, состояние записей живёт в настройках (AppSettings.Panels / AppSettings.SshItems).
/// </summary>
public sealed class NavSectionMenu<T> where T : class, INavEntry
{
    private readonly Panel _host;
    private readonly Func<List<T>> _items;
    private readonly Dictionary<string, (Grid Row, TextBlock Name)> _rows = new(StringComparer.Ordinal);
    private bool _built;
    private readonly Func<string, T> _create;
    private readonly Action<T> _open;
    private readonly Action<T> _deleted;
    private readonly Action _persist;
    private readonly IDialogService _dialogs;
    private readonly string _titleText;
    private string _title => L.Text(_titleText);
    private readonly string _addLabelText;
    private string _addLabel => L.Text(_addLabelText);
    private readonly Action<T>? _configure;
    private readonly string _configureLabelText;
    private string _configureLabel => L.Text(_configureLabelText);

    // перетаскивание строк мышью
    private T? _dragItem;
    private bool _dragging;
    private Point _pressPoint;
    private AdornerLayer? _dragLayer;
    private ReorderDrag.Preview? _dragPreview;

    /// <summary>Запись, открытая сейчас.</summary>
    public string? SelectedId { get; private set; }

    /// <summary>Строка текущей записи — цель общей анимированной подсветки навигации.</summary>
    public FrameworkElement? SelectedElement { get; private set; }

    /// <summary>Открытая запись сменилась или была удалена.</summary>
    public event Action? EntrySelectionChanged;

    public NavSectionMenu(Panel host, Func<List<T>> items, Func<string, T> create,
        Action<T> open, Action<T> deleted, Action persist,
        IDialogService dialogs, string title, string addLabel,
        Action<T>? configure = null, string configureLabel = "")
    {
        _host = host;
        _items = items;
        _create = create;
        _open = open;
        _deleted = deleted;
        _persist = persist;
        _dialogs = dialogs;
        _titleText = title;
        _addLabelText = addLabel;
        _configure = configure;
        _configureLabelText = configureLabel;
    }

    /// <summary>Перерисовать список, оставив текущий выбор.</summary>
    public void Rebuild() => Rebuild(SelectedId);

    /// <summary>Перерисовать список с выбранной записью (null — выбора нет).</summary>
    public void Rebuild(string? selectedId)
    {
        SelectedId = selectedId;
        SelectedElement = null;
        _built = false;
        _rows.Clear();
        _host.Children.Clear();
        foreach (var item in _items())
        {
            var visual = BuildRow(item);
            _rows[item.Id] = visual;
            _host.Children.Add(visual.Row);
        }
        _host.Children.Add(BuildAddRow());
        _built = true;
        ApplySelection(selectedId);

        // При добавлении строки она должна получить координаты до того, как общий маркер начнёт к ней ехать.
        if (_host.IsLoaded && _host.IsVisible) _host.UpdateLayout();
    }

    /// <summary>Выбрать строку без пересоздания визуального дерева.</summary>
    public void Select(string? selectedId)
    {
        if (!_built)
        {
            Rebuild(selectedId);
            return;
        }

        ApplySelection(selectedId);
    }

    private void ApplySelection(string? selectedId)
    {
        SelectedId = selectedId;
        SelectedElement = null;
        foreach (var (id, visual) in _rows)
        {
            var selected = string.Equals(id, selectedId, StringComparison.Ordinal);
            visual.Name.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            visual.Name.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextSecondary");
            if (selected) SelectedElement = visual.Row;
        }
    }

    // ------------------------------------------------------------- строки

    private (Grid Row, TextBlock Name) BuildRow(T item)
    {
        var row = new Grid
        {
            Height = 30,
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = item.Name,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = item.Name,
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.Normal,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        row.Children.Add(name);

        var column = 1;
        var edit = IconButton(L.T("S0934"), "M2,10 L2.4,7.6 L8.2,1.8 L10.2,3.8 L4.4,9.6 Z M2.4,7.6 L4.4,9.6",
            () => Rename(item));
        Grid.SetColumn(edit, column++);
        row.Children.Add(edit);

        if (_configure is not null)
        {
            // шестерёнка: настройки записи (для SSH — логин и пароль)
            var gear = IconButton(_configureLabel, GearGeometry, () => _configure(item));
            Grid.SetColumn(gear, column++);
            row.Children.Add(gear);
        }

        var delete = IconButton(L.T("S0083"), "M2,2 L10,10 M10,2 L2,10", () => Delete(item));
        Grid.SetColumn(delete, column);
        row.Children.Add(delete);

        // клик по строке (кроме кнопок — они сами обрабатывают мышь) открывает запись;
        // удержание с движением перетаскивает строку вверх/вниз
        row.MouseLeftButtonDown += (_, e) => OnRowDown(row, item, e);
        row.MouseMove += (_, e) => OnRowMove(row, e);
        row.MouseLeftButtonUp += (_, e) => OnRowUp(row, item, e);
        return (row, name);
    }

    private UIElement BuildAddRow()
    {
        var row = new Grid
        {
            Height = 30,
            Margin = new Thickness(0, 1, 0, 0),
            Background = Brushes.Transparent,
        };
        row.Children.Add(IconButton(_addLabel, "M6,1.2 V10.8 M1.2,6 H10.8", Add));
        return row;
    }

    private Button IconButton(string tooltip, string geometry, Action onClick)
    {
        var path = new System.Windows.Shapes.Path
        {
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse(geometry),
        };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextSecondary");

        var btn = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Content = path,
            ToolTip = tooltip,
        };
        btn.SetResourceReference(Button.StyleProperty, "BtnIcon");
        btn.Click += (_, e) =>
        {
            e.Handled = true;   // кнопка не должна открывать строку
            onClick();
        };
        return btn;
    }

    // ------------------------------------------------------------- перетаскивание

    private void OnRowDown(Grid row, T item, MouseButtonEventArgs e)
    {
        _dragItem = item;
        _dragging = false;
        _pressPoint = e.GetPosition(_host);
        e.Handled = true;   // не даём ListBoxItem выделиться (вкладка не переключается без клика)
    }

    private void OnRowMove(Grid row, MouseEventArgs e)
    {
        if (_dragItem is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetDrag(row);
            return;
        }

        var pos = e.GetPosition(_host);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _pressPoint.Y) < 5) return;   // короткое движение — обычный клик
            _dragging = true;
            row.CaptureMouse();
            row.Opacity = 0.7;
            if (AdornerLayer.GetAdornerLayer(_host) is { } layer)
            {
                _dragLayer = layer;
                _dragPreview = new ReorderDrag.Preview(_host, row, e.GetPosition(row));
                layer.Add(_dragPreview);
                _dragPreview.MoveTo(pos);
            }
        }

        _dragPreview?.MoveTo(pos);

        // едем по строкам: встаём туда, чей центр ближе к мыши
        var rows = Rows();
        var from = rows.IndexOf(row);
        if (from < 0) return;
        var to = 0;
        var best = double.MaxValue;
        for (var i = 0; i < rows.Count; i++)
        {
            var d = Math.Abs(RowCenterY(rows[i]) - pos.Y);
            if (d < best)
            {
                best = d;
                to = i;
            }
        }
        if (to == from) return;

        var list = _items();
        var index = list.IndexOf(_dragItem);
        if (index < 0 || index >= list.Count) return;
        var before = ReorderDrag.Capture(_host, rows);
        list.RemoveAt(index);
        list.Insert(Math.Min(to, list.Count), _dragItem);
        _host.Children.Remove(row);
        _host.Children.Insert(Math.Min(to, _host.Children.Count - 1), row);   // «+» остаётся последней
        _host.UpdateLayout();
        ReorderDrag.AnimateReflow(_host, before, Rows());
    }

    /// <summary>
    /// Центр строки в её layout-слоте, а не анимированная позиция: иначе середина пролетает
    /// мимо неподвижного курсора и просит обратный ход (та же правка, что у профилей).
    /// </summary>
    private double RowCenterY(Grid row)
    {
        var center = row.TranslatePoint(new Point(0, row.ActualHeight / 2), _host).Y;
        if (row.RenderTransform is TranslateTransform move) center -= move.Y;
        return center;
    }

    private void OnRowUp(Grid row, T item, MouseButtonEventArgs e)
    {
        var dragging = _dragging;
        ResetDrag(row);
        if (dragging)
        {
            e.Handled = true;
            // модель и строки уже пересобраны вживую — Rebuild только убил бы
            // долетающую FLIP-анимацию и моргнул бы списком
            _persist();
        }
        else
        {
            Open(item);
        }
    }

    private void ResetDrag(Grid row)
    {
        if (_dragItem is null) return;
        _dragItem = null;
        _dragging = false;
        row.Opacity = 1;
        if (row.IsMouseCaptured) row.ReleaseMouseCapture();
        if (_dragPreview is not null && _dragLayer is not null) _dragLayer.Remove(_dragPreview);
        _dragPreview = null;
        _dragLayer = null;
    }

    /// <summary>Строки списка (без кнопки «+» внизу).</summary>
    private List<Grid> Rows()
    {
        var rows = new List<Grid>();
        for (var i = 0; i < _host.Children.Count - 1; i++)
            if (_host.Children[i] is Grid g) rows.Add(g);
        return rows;
    }

    // ------------------------------------------------------------- действия

    /// <summary>Создать запись с первым свободным именем («Панель 1», «Панель 2», …) и сразу открыть.</summary>
    private void Add()
    {
        var item = _create(NextName());
        _items().Add(item);
        _persist();
        Rebuild();
        Open(item);
    }

    private void Open(T item)
    {
        _open(item);
        Select(item.Id);
        EntrySelectionChanged?.Invoke();
    }

    private void Rename(T item)
    {
        var name = _dialogs.PromptText(_title, L.T("S0027"), item.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        item.Name = name.Trim();
        _persist();
        Rebuild();
    }

    private void Delete(T item)
    {
        if (!_dialogs.Confirm(_title, L.F("S0823", item.Name), L.T("S0083"))) return;
        _items().Remove(item);
        _persist();
        if (string.Equals(SelectedId, item.Id, StringComparison.Ordinal)) SelectedId = null;
        _deleted(item);
        Rebuild();
        EntrySelectionChanged?.Invoke();
    }

    private string NextName()
    {
        var items = _items();
        for (var i = 1; ; i++)
        {
            var name = $"{_title} {i}";
            if (items.All(x => !string.Equals(x.Name, name, StringComparison.Ordinal))) return name;
        }
    }

    /// <summary>Шестерёнка: зубчатый венец (8 зубцов) и центральное отверстие.</summary>
    private const string GearGeometry =
        "M9.12,5.28 L10.37,5.5 L10.37,6.5 L9.12,6.72 A3.2,3.2 0 0 1 8.71,7.7 " +
        "L9.44,8.74 L8.74,9.44 L7.7,8.71 A3.2,3.2 0 0 1 6.72,9.12 " +
        "L6.5,10.37 L5.5,10.37 L5.28,9.12 A3.2,3.2 0 0 1 4.3,8.71 " +
        "L3.26,9.44 L2.56,8.74 L3.29,7.7 A3.2,3.2 0 0 1 2.88,6.72 " +
        "L1.63,6.5 L1.63,5.5 L2.88,5.28 A3.2,3.2 0 0 1 3.29,4.3 " +
        "L2.56,3.26 L3.26,2.56 L4.3,3.29 A3.2,3.2 0 0 1 5.28,2.88 " +
        "L5.5,1.63 L6.5,1.63 L6.72,2.88 A3.2,3.2 0 0 1 7.7,3.29 " +
        "L8.74,2.56 L9.44,3.26 L8.71,4.3 A3.2,3.2 0 0 1 9.12,5.28 Z " +
        "M7.6,6 A1.6,1.6 0 1 1 4.4,6 A1.6,1.6 0 1 1 7.6,6";
}
