using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;
using VibeTun.Views.Controls;

namespace VibeTun.Views.Dialogs;

/// <summary>Вариант протокола в выпадающем списке «Создать вручную».</summary>
public sealed record ProtocolOption(string Id, string Name)
{
    /// <summary>Поле выбора показывает название протокола, а не строку записи.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// «Добавить подключение»: одно окно с меню и шагами (файл, вставка, протокол, группа).
/// Окно вырастает из кнопки-источника и едет к центру главного окна. При переходе между шагами
/// оно плавно меняет размер, а содержимое уходит и приходит с затуханием (300 мс).
/// </summary>
public partial class AddConnectionWindow : DialogWindow
{
    /// <summary>Рамка DialogWindow: по 1 px с каждой стороны.</summary>
    private const double FrameInset = 2;

    /// <summary>Высота шапки с заголовком и кнопкой «×» (строка 0 корневой сетки).</summary>
    private const double HeaderHeight = 36;

    /// <summary>Ниже этой высоты содержимое заведомо не помещается (шапка + хотя бы одна строка) — значит, измерение не состоялось.</summary>
    private const double MinContentHeight = 60;

    /// <summary>Длительность всех анимаций окна (с учётом общей скорости анимаций приложения).</summary>
    private static TimeSpan Duration => Motion.Scaled(300);

    private readonly Rect? _origin;
    private readonly string _defaultGroupName;
    private readonly bool _hasMenu;
    private AddConnectionStep _step;
    private bool _busy;
    private bool _closing;
    private long _morphId;
    private bool _inFrame;

    public AddConnectionWindow(Rect? origin, AddConnectionStep start, string defaultGroupName)
    {
        InitializeComponent();
        _origin = origin;
        _defaultGroupName = defaultGroupName;
        _hasMenu = start == AddConnectionStep.Menu;
        _step = start;

        ProtocolCombo.ItemsSource = ProtocolOptions();
        ShowStep(start);
        PrepareStep(start, fromClipboard: false);
        Loaded += OnOpened;
    }

    /// <summary>Итог диалога; при закрытии без выбора — Cancel.</summary>
    public AddConnectionChoice Choice { get; private set; } = new(AddConnectionAction.Cancel);

    // ------------------------------------------------------------------ открытие и закрытие

    private void OnOpened(object sender, RoutedEventArgs e)
    {
        Loaded -= OnOpened;

        var center = OwnerCenter();
        var to = Around(center, SizeFor(_step));
        var from = _origin ?? Around(center, new Size(56, 40));

        // окно сразу в позиции кнопки (невидимо из-за Opacity=0), затем растёт до своего размера
        Opacity = 1;
        _busy = true;
        Morph(from, to, to.Size, release: true, done: () => Idle(FocusStep));
        FadeContent(1, Duration);
    }

    /// <summary>Закрыть окно: оно сжимается обратно к кнопке и только потом возвращает результат.</summary>
    private void Finish(AddConnectionChoice choice)
    {
        if (_busy || _closing) return;
        _closing = true;
        Choice = choice;

        var from = CurrentRect();
        var to = _origin ?? Around(Center(from), new Size(56, 40));
        FadeContent(0, Duration);
        Motion.To(this, OpacityProperty, 0, Duration);
        // содержимое остаётся в прежнем размере — окно просто сжимается к кнопке
        Morph(from, to, from.Size, release: false, done: () =>
        {
            DialogResult = choice.Action != AddConnectionAction.Cancel;
            Close();
        });
    }

    // ------------------------------------------------------------------ переходы между шагами

    /// <summary>Перейти на другой шаг: содержимое гаснет, окно меняет размер, новое содержимое проявляется.</summary>
    private void Navigate(AddConnectionStep next, bool fromClipboard = false)
    {
        if (_busy || _closing || next == _step) return;
        _busy = true;

        FadeContent(0, Duration, () =>
        {
            ShowStep(next);
            PrepareStep(next, fromClipboard);

            var from = CurrentRect();
            var to = Around(Center(from), SizeFor(next));
            Morph(from, to, to.Size, release: true);
            FadeContent(1, Duration, () => Idle(FocusStep));
        });
    }

    private void Back()
    {
        if (_hasMenu && _step != AddConnectionStep.Menu) Navigate(AddConnectionStep.Menu);
        else Finish(new AddConnectionChoice(AddConnectionAction.Cancel));
    }

    /// <summary>Показать нужный шаг и его заголовок (без анимации — её делает вызывающий код).</summary>
    private void ShowStep(AddConnectionStep step)
    {
        _step = step;
        StepMenu.Visibility = VisibilityOf(AddConnectionStep.Menu, step);
        StepFile.Visibility = VisibilityOf(AddConnectionStep.File, step);
        StepPaste.Visibility = VisibilityOf(AddConnectionStep.Paste, step);
        StepProtocol.Visibility = VisibilityOf(AddConnectionStep.Protocol, step);
        StepGroup.Visibility = VisibilityOf(AddConnectionStep.Group, step);
        HeaderTitle.Text = step switch
        {
            AddConnectionStep.Menu => L.T("S0924"),
            AddConnectionStep.File => L.T("S0152"),
            AddConnectionStep.Paste => L.T("S1026"),
            AddConnectionStep.Protocol => L.T("S1023"),
            _ => L.T("S0952"),
        };
    }

    private static System.Windows.Visibility VisibilityOf(AddConnectionStep panel, AddConnectionStep current) =>
        panel == current ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>Подготовить содержимое шага: подписи, предзаполнение поля, состояние кнопок.</summary>
    private void PrepareStep(AddConnectionStep step, bool fromClipboard)
    {
        switch (step)
        {
            case AddConnectionStep.File:
                FileError.Text = "";
                break;
            case AddConnectionStep.Paste:
                PasteSubtitle.Text = L.T(fromClipboard ? "S1013" : "S1015");
                PasteInput.Text = fromClipboard ? ReadClipboard() : "";
                PasteInput.CaretIndex = PasteInput.Text.Length;
                UpdatePasteStatus();
                break;
            case AddConnectionStep.Group:
                GroupName.Text = _defaultGroupName;
                GroupName.CaretIndex = GroupName.Text.Length;
                UpdateGroupButton();
                break;
        }
    }

    private void FocusStep()
    {
        switch (_step)
        {
            case AddConnectionStep.Paste:
                PasteInput.Focus();
                break;
            case AddConnectionStep.Group:
                GroupName.Focus();
                break;
        }
    }

    // ------------------------------------------------------------------ геометрия и анимация

    /// <summary>Размер окна под шаг: ширина — по шагу, высота — по содержимому.</summary>
    private Size SizeFor(AddConnectionStep step)
    {
        var width = step switch
        {
            AddConnectionStep.File => 420,
            AddConnectionStep.Paste => 440,
            AddConnectionStep.Group => 400,
            _ => 380,
        };

        // свежая разметка перед измерением: без неё панель может отдать нулевой размер
        UpdateLayout();
        var panel = PanelOf(step);
        panel.Measure(new Size(width - FrameInset, double.PositiveInfinity));
        var content = panel.DesiredSize.Height;

        if (content < MinContentHeight)
        {
            // запасной путь: меряем весь корень, пока его размер не зафиксирован анимацией
            ReleaseRoot();
            Root.Measure(new Size(width - FrameInset, double.PositiveInfinity));
            content = Math.Max(content, Root.DesiredSize.Height - HeaderHeight);
        }
        return new Size(width, HeaderHeight + Math.Max(content, MinContentHeight) + FrameInset);
    }

    private FrameworkElement PanelOf(AddConnectionStep step) => step switch
    {
        AddConnectionStep.File => StepFile,
        AddConnectionStep.Paste => StepPaste,
        AddConnectionStep.Protocol => StepProtocol,
        AddConnectionStep.Group => StepGroup,
        _ => StepMenu,
    };

    /// <summary>Центр главного окна (владельца) на экране; без владельца — центр рабочей области.</summary>
    private Point OwnerCenter()
    {
        if (Owner is FrameworkElement owner && ScreenGeometry.CenterOf(owner) is { } center) return center;
        var area = SystemParameters.WorkArea;
        return new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
    }

    private Rect CurrentRect() => new(Left, Top, Width, Height);

    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static Rect Around(Point center, Size size) =>
        new(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);

    /// <summary>
    /// Плавно вести положение и размер окна одним таймером кадров: все четыре значения (Left, Top, Width,
    /// Height) считаются из одного прогресса, поэтому окно не расходится по размеру и не прыгает.
    /// Содержимое фиксируется в <paramref name="contentSize"/> (размер окна), поэтому во время анимации
    /// оно не перекладывается. <paramref name="release"/> — после анимации вернуть обычную растяжку.
    /// </summary>
    private void Morph(Rect from, Rect to, Size contentSize, bool release, Action? done = null)
    {
        var id = unchecked(++_morphId);
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.VerticalAlignment = VerticalAlignment.Top;
        Root.Width = Math.Max(0, contentSize.Width - FrameInset);
        Root.Height = Math.Max(0, contentSize.Height - FrameInset);
        ApplyRect(from);

        var total = Duration.TotalMilliseconds;
        var clock = Stopwatch.StartNew();
        void Tick(object? sender, EventArgs e)
        {
            // ApplyRect меняет Height → SetWindowPos → WM_SIZE → WPF синхронно вызывает Rendering,
            // то есть этот же обработчик входит повторно. Вложенный вызов пропускаем: иначе кадры
            // записываются не по порядку, и окно может остаться с промежуточным размером.
            if (_inFrame) return;
            _inFrame = true;
            try
            {
                if (id != _morphId)
                {
                    CompositionTarget.Rendering -= Tick;
                    return;
                }
                var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / total);
                if (t < 1)
                {
                    ApplyRect(Lerp(from, to, EaseInOut(t)));
                    return;
                }
                CompositionTarget.Rendering -= Tick;
                ApplyRect(to);
                if (release) ReleaseRoot();
                done?.Invoke();
            }
            finally
            {
                _inFrame = false;
            }
        }
        CompositionTarget.Rendering += Tick;
    }

    private void ApplyRect(Rect rect)
    {
        Left = rect.X;
        Top = rect.Y;
        Width = rect.Width;
        Height = rect.Height;
    }

    private static Rect Lerp(Rect a, Rect b, double k) => new(
        a.X + (b.X - a.X) * k,
        a.Y + (b.Y - a.Y) * k,
        a.Width + (b.Width - a.Width) * k,
        a.Height + (b.Height - a.Height) * k);

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Конец анимации шага или открытия: снять блокировку. Размер уже выставлен анимацией.</summary>
    private void Idle(Action? then = null)
    {
        _busy = false;
        then?.Invoke();
    }

    /// <summary>Подогнать высоту под текущее содержимое (например, после появления строки статуса).</summary>
    private void RefitIfIdle()
    {
        if (!IsLoaded || _busy || _closing) return;
        var target = SizeFor(_step);
        var current = CurrentRect();
        if (Math.Abs(current.Width - target.Width) < 0.5 && Math.Abs(current.Height - target.Height) < 0.5) return;

        _busy = true;
        // без повторной подгонки по окончании: иначе второе измерение может уменьшить уже выросшее окно
        Morph(current, Around(Center(current), target), target, release: true, done: () => _busy = false);
    }

    private void ReleaseRoot()
    {
        Root.ClearValue(WidthProperty);
        Root.ClearValue(HeightProperty);
        Root.ClearValue(HorizontalAlignmentProperty);
        Root.ClearValue(VerticalAlignmentProperty);
    }

    /// <summary>Плавно проявить/спрятать содержимое и заголовок (кнопка «×» остаётся на месте).</summary>
    private void FadeContent(double to, TimeSpan duration, Action? done = null)
    {
        Motion.To(HeaderTitle, UIElement.OpacityProperty, to, duration);
        Motion.To(Body, UIElement.OpacityProperty, to, duration, done);
    }

    // ------------------------------------------------------------------ меню

    private void OnMenuFile(object sender, RoutedEventArgs e) => Navigate(AddConnectionStep.File);

    private void OnMenuClipboard(object sender, RoutedEventArgs e) =>
        Navigate(AddConnectionStep.Paste, fromClipboard: true);

    private void OnMenuManual(object sender, RoutedEventArgs e) =>
        Navigate(AddConnectionStep.Paste, fromClipboard: false);

    private void OnMenuProtocol(object sender, RoutedEventArgs e) => Navigate(AddConnectionStep.Protocol);

    private void OnMenuGroup(object sender, RoutedEventArgs e) => Navigate(AddConnectionStep.Group);

    // ------------------------------------------------------------------ файл

    private void OnPickFileClick(object sender, RoutedEventArgs e) => PickFiles();

    private void OnDropZoneClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) PickFiles();
    }

    private void PickFiles()
    {
        if (_busy || _closing) return;
        var dialog = new OpenFileDialog
        {
            Filter = L.T("S0918"),
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        ReadFiles(dialog.FileNames);
    }

    /// <summary>Прочитать выбранные файлы и сразу передать их содержимое; пустой результат — сообщение в окне.</summary>
    private void ReadFiles(IEnumerable<string> paths)
    {
        var parts = new List<string>();
        try
        {
            foreach (var path in paths) parts.Add(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            FileError.Text = L.F("S1027", ex.Message);
            RefitIfIdle();
            return;
        }

        var text = string.Join(Environment.NewLine, parts);
        if (LinkParser.ParseMany(text).Profiles.Count == 0 && !IsLink(text))
        {
            FileError.Text = L.T("S0915");
            RefitIfIdle();
            return;
        }
        Finish(new AddConnectionChoice(AddConnectionAction.FromFile, Text: text));
    }

    private void OnDropZoneDragEnter(object sender, DragEventArgs e) => SetDropHighlight(IsFileDrop(e));

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropZoneDragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (_busy || _closing || !IsFileDrop(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ReadFiles(files);
    }

    private static bool IsFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void SetDropHighlight(bool active) =>
        DropStroke.SetResourceReference(Shape.StrokeProperty, active ? "Accent" : "BorderStrong");

    // ------------------------------------------------------------------ вставка текста

    private void OnPasteChanged(object sender, TextChangedEventArgs e) => UpdatePasteStatus();

    private void UpdatePasteStatus()
    {
        var text = PasteInput.Text ?? "";
        PasteStatus.Text = "";
        PasteStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var count = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            PasteAdd.IsEnabled = false;
        }
        else
        {
            count = LinkParser.ParseMany(text).Profiles.Count;
            if (count > 0)
            {
                PasteStatus.Text = L.F("S0916", count);
                PasteAdd.IsEnabled = true;
            }
            else if (IsLink(text))
            {
                // ссылка на подписку: разберёт основной код приложения
                PasteStatus.Text = L.T("S1025");
                PasteAdd.IsEnabled = true;
            }
            else
            {
                PasteStatus.Text = LinkParser.ParseMany(text).Errors.FirstOrDefault() ?? L.T("S0915");
                PasteStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
                PasteAdd.IsEnabled = false;
            }
        }

        PasteAdd.Content = count > 1 ? L.F("S0917", count) : L.T("S0029");
        RefitIfIdle();
    }

    private void OnPasteAdd(object sender, RoutedEventArgs e)
    {
        if (!PasteAdd.IsEnabled) return;
        Finish(new AddConnectionChoice(AddConnectionAction.Paste, Text: PasteInput.Text));
    }

    private static string ReadClipboard()
    {
        try { return Clipboard.GetText(); }
        catch { return ""; }
    }

    /// <summary>Одна строка http(s)-адреса без пробелов — ссылка (в том числе на подписку).</summary>
    private static bool IsLink(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace) &&
               Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    // ------------------------------------------------------------------ протокол

    private static List<ProtocolOption> ProtocolOptions()
    {
        var ids = new[]
        {
            ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Trojan, ProtocolIds.Shadowsocks,
            ProtocolIds.Socks, ProtocolIds.Http, ProtocolIds.Hysteria2, ProtocolIds.Tuic,
            ProtocolIds.Wireguard, ProtocolIds.AmneziaWg, ProtocolIds.MtProto
        };
        var options = ids.Select(id => new ProtocolOption(id, ProtocolIds.Display(id))).ToList();
        options.Add(new ProtocolOption(ProtocolIds.Tunnel, L.T("S0156")));
        return options;
    }

    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        ProtocolHint.Visibility = ProtocolCombo.SelectedIndex < 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        if (ProtocolCombo.SelectedItem is not ProtocolOption option || _busy || _closing) return;

        // выбор протокола сразу ведёт к редактору; закрываем после того, как выпадающий список свернётся
        Dispatcher.BeginInvoke(new Action(() =>
            Finish(new AddConnectionChoice(AddConnectionAction.Protocol, Protocol: option.Id))));
    }

    // ------------------------------------------------------------------ группа

    private void OnGroupNameChanged(object sender, TextChangedEventArgs e) => UpdateGroupButton();

    private void UpdateGroupButton() => GroupCreate.IsEnabled = !string.IsNullOrWhiteSpace(GroupName.Text);

    private void OnGroupNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreateGroupNow();
    }

    private void OnCreateGroup(object sender, RoutedEventArgs e) => CreateGroupNow();

    private void CreateGroupNow()
    {
        var name = GroupName.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        Finish(new AddConnectionChoice(AddConnectionAction.Group, GroupName: name));
    }

    // ------------------------------------------------------------------ общие кнопки

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    private void OnClose(object sender, RoutedEventArgs e) => Finish(new AddConnectionChoice(AddConnectionAction.Cancel));

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Back();
        }
        base.OnKeyDown(e);
    }
}
