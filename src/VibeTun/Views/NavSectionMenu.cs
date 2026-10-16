using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeTun.Models;
using VibeTun.Services;

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
        }

        // едем по строкам: встаём туда, чей центр ближе к мыши
        var rows = Rows();
        var from = rows.IndexOf(row);
        if (from < 0) return;
        var to = 0;
        var best = double.MaxValue;
        for (var i = 0; i < rows.Count; i++)
        {
            var centerY = rows[i].TranslatePoint(new Point(0, rows[i].ActualHeight / 2), _host).Y;
            var d = Math.Abs(centerY - pos.Y);
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
        list.RemoveAt(index);
        list.Insert(Math.Min(to, list.Count), _dragItem);
        _host.Children.Remove(row);
        _host.Children.Insert(Math.Min(to, _host.Children.Count - 1), row);   // «+» остаётся последней
    }

    private void OnRowUp(Grid row, T item, MouseButtonEventArgs e)
    {
        var dragging = _dragging;
        ResetDrag(row);
        if (dragging)
        {
            e.Handled = true;
            _persist();
            Rebuild();
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

    /// <summary>Силуэт шестерёнки: центральный кружок и восемь зубцов-спиц.</summary>
    private const string GearGeometry =
        "M6,7.6 A1.6,1.6 0 1 1 6,4.4 A1.6,1.6 0 1 1 6,7.6 " +
        "M6,1 V2.3 M6,9.7 V11 M1,6 H2.3 M9.7,6 H11 " +
        "M2.45,2.45 L3.35,3.35 M8.65,8.65 L9.55,9.55 M9.55,2.45 L8.65,3.35 M3.35,8.65 L2.45,9.55";
}
