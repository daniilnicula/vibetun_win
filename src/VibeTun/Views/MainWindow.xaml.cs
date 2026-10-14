using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;
using VibeTun.ViewModels;
using VibeTun.Views.Controls;

namespace VibeTun.Views;

public partial class MainWindow : Window
{
    private readonly bool _useSystemCorners = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    private MainViewModel? _vm;
    private bool _reallyExit;
    private bool _closed;
    private DialogService? _dialogs;
    private NavSectionMenu<PanelEntry>? _panelMenu;
    private NavSectionMenu<SshEntry>? _sshMenu;
    private TrayPanelWindow? _trayPanel;
    private SlideMarker? _navMarker;

    public MainWindow()
    {
        InitializeComponent();
        // AllowsTransparency нельзя менять после создания HWND.
        // Windows 11: обычное окно с системными углами; Windows 10: layered-окно
        // с прозрачным фоном и обрезкой содержимого по скруглённому Border.
        if (!_useSystemCorners)
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }
        UpdateRootChrome();
        PreviewKeyDown += OnPreviewKeyDown;
        ApplyNavOrder(App.Settings.Current.NavOrder);
        // страховка для «Развернуть»: после изменения состояния окна дожимаем его до рабочей
        // области (см. ClampMaximizedToWorkArea) — WM_GETMINMAXINFO не всегда этого добивается
        StateChanged += (_, _) =>
        {
            ClampMaximizedToWorkArea();
            UpdateRootChrome();   // в развёрнутом состоянии уголки прямые
        };

        // выпадающие списки «Панели»/«SSH» в навигации
        _dialogs = new DialogService(this);
        _panelMenu = new NavSectionMenu<PanelEntry>(PanelMenu, () => App.Settings.Current.Panels,
            name => new PanelEntry { Name = name }, OpenPanelEntry, OnPanelEntryDeleted,
            () => App.Settings.SaveSoon(), _dialogs, "Панель", "Добавить панель");
        _sshMenu = new NavSectionMenu<SshEntry>(SshMenu, () => App.Settings.Current.SshItems,
            name => new SshEntry { Name = name }, OpenSshEntry, OnSshEntryDeleted,
            () => App.Settings.SaveSoon(), _dialogs, "SSH", "Добавить подключение",
            OnSshEntryConfigure, "Логин и пароль");

        // раскрытые списки остаются раскрытыми и после перезапуска
        if (App.Settings.Current.NavPanelsExpanded)
        {
            PanelMenuBox.Visibility = Visibility.Visible;
            PanelChevronRot.Angle = 180;
        }
        if (App.Settings.Current.NavSshExpanded)
        {
            SshMenuBox.Visibility = Visibility.Visible;
            SshChevronRot.Angle = 180;
        }

        // страницы листаются в сторону пункта: вниз — если пункт ниже прежнего в списке навигации
        MainPages.PositionOf = NavPositionOf;

        // подсветка выбранной вкладки переезжает между пунктами; разделы «Панели»/«SSH» сами не подсвечиваются
        _navMarker = new SlideMarker(NavHost, NavMarker, ActiveNavItem, _ => "AccentSoft");
        NavList.SelectionChanged += (_, _) => _navMarker?.Sync(!_navDragging);
        NavList.LayoutUpdated += (_, _) => _navMarker?.Sync(false);
        Motion.AttachHoverPress(PanelHeader);
        Motion.AttachHoverPress(SshHeader);
    }

    /// <summary>Место пункта навигации по его вкладке (с учётом порядка, заданного пользователем); −1 — нет такого.</summary>
    private int NavPositionOf(string? tag)
    {
        var index = 0;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag as string == tag) return index;
            index++;
        }
        return -1;
    }

    /// <summary>Пункт, который подсвечивается: выбранная вкладка, но не сами разделы «Панели»/«SSH».</summary>
    private FrameworkElement? ActiveNavItem()
    {
        if (NavList.SelectedItem is not ListBoxItem { Tag: string tag } item) return null;
        return tag is "panel" or "ssh" ? null : item;
    }

    public async Task InitializeAsync()
    {
        _vm = new MainViewModel(new DialogService(this));
        DataContext = _vm;

        // одиночный клик по иконке в трее — мини-панель у иконки (двойной — окно, см. TrayService)
        _trayPanel = new TrayPanelWindow { DataContext = _vm };
        _vm.Tray.PanelRequested += (anchor, clickedAt) => Safe(() => _trayPanel?.Toggle(anchor, clickedAt));

        // подсветка записей в списках следует за открытой вкладкой
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab)) SyncMenuSelection();
        };

        // вызовы приходят из трея (обычный поток WinForms-сообщений) — защищаемся от исключений,
        // иначе WPF/WinForms покажет системный диалог сбоя
        _vm.ExitRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(() => ExitApplication())));
        _vm.RestartRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(() => ExitApplication(restart: true))));
        _vm.ShowWindowRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(ShowFromTray)));

        UpdateLayout();
        var settings = App.Settings.Current;
        if (settings.WindowWidth > 600) Width = settings.WindowWidth;
        if (settings.WindowHeight > 400) Height = settings.WindowHeight;
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top &&
            left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (string.Equals(settings.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase))
            WindowState = WindowState.Maximized;

        await _vm.InitializeAsync();
        RestoreSelectedEntries();

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    public void PrepareForSystemShutdown() => _vm?.PrepareForSystemShutdown();

    /// <summary>Показать окно из трея (ЛКМ по иконке). После закрытия окна ничего не делаем.</summary>
    public void ShowFromTray()
    {
        if (_closed) return;

        _trayPanel?.HidePanel();
        if (!IsVisible) Show();
        // возврат из трея — подгружаем страницу выбранной панели обратно
        PanelTab?.Restore();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Выполнить действие, не давая исключению уйти в цикл сообщений WinForms.</summary>
    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && _vm.ActiveTab == "profiles")
        {
            _vm.ActiveTab = "profiles";
            _vm.Profiles.AddFromClipboardCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------- порядок вкладок
    private ListBoxItem? _navPressed;     // вкладка, по которой нажали ЛКМ
    private Point _navPressPoint;
    private bool _navDragging;

    /// <summary>
    /// Восстановить сохранённый порядок вкладок. Неизвестные ключи пропускаем, а вкладки, которых
    /// в сохранённом порядке ещё нет (появились в новой версии, например «DNS»), ставим сразу за той,
    /// что идёт перед ними в разметке, — а не в самый низ, под «Панели» и «SSH».
    /// </summary>
    private void ApplyNavOrder(IEnumerable<string>? order)
    {
        if (order is null) return;

        var markup = NavList.Items.OfType<ListBoxItem>()
            .Select(i => i.Tag as string ?? "")
            .Where(t => t.Length > 0)
            .ToList();
        var keys = order.Where(markup.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return;
        for (var i = 0; i < markup.Count; i++)
        {
            if (keys.Contains(markup[i])) continue;
            var anchor = i > 0 ? keys.IndexOf(markup[i - 1]) : -1;
            keys.Insert(anchor + 1, markup[i]);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var key in keys)
        {
            if (!applied.Add(key)) continue;
            var item = NavList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, key, StringComparison.Ordinal));
            if (item is null) continue;

            var from = NavList.Items.IndexOf(item);
            if (from != position)
            {
                NavList.Items.RemoveAt(from);
                NavList.Items.Insert(position, item);
            }
            position++;
        }
    }

    private void OnNavPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // внутри выпадающих списков «Панели»/«SSH» записи перетаскиваются по-своему —
        // перетаскивание самой вкладки отсюда не начинаем
        if (IsInsideMenu(e.OriginalSource as DependencyObject)) return;
        _navPressed = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _navPressPoint = e.GetPosition(NavList);
    }

    /// <summary>Находится ли элемент внутри выпадающего списка «Панели» или «SSH».</summary>
    private bool IsInsideMenu(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, PanelMenu) || ReferenceEquals(node, SshMenu)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Клик по заголовку раздела не выделяет вкладку (и не переключает вкладку):
    /// само нажатие нужно только для клика/перетаскивания, а не для выбора.</summary>
    private void OnNavHeaderMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnNavPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var dragged = _navPressed;
        if (dragged is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndNavDrag();
            return;
        }

        var pos = e.GetPosition(NavList);
        if (!_navDragging)
        {
            if (Math.Abs(pos.Y - _navPressPoint.Y) < 5) return;   // короткое движение — это обычный клик
            _navDragging = true;
            dragged.Opacity = 0.7;
            NavList.CaptureMouse();
        }

        var target = NavItemNearest(pos.Y);
        if (target is null || ReferenceEquals(target, dragged)) return;

        var from = NavList.Items.IndexOf(dragged);
        var to = NavList.Items.IndexOf(target);
        if (from < 0 || to < 0) return;

        // при удалении выбранной вкладки WPF сбрасывает выбор — вернём его тем же значением
        var active = NavList.SelectedValue;
        NavList.Items.RemoveAt(from);
        NavList.Items.Insert(to, dragged);
        NavList.SelectedValue = active;
    }

    private void OnNavPreviewMouseUp(object sender, MouseButtonEventArgs e) => EndNavDrag();

    private void OnNavLostCapture(object sender, MouseEventArgs e) => EndNavDrag();

    private void EndNavDrag()
    {
        var dragged = _navDragging;
        var item = _navPressed;
        _navPressed = null;
        _navDragging = false;
        if (item is not null) item.Opacity = 1;
        if (NavList.IsMouseCaptured) NavList.ReleaseMouseCapture();

        if (!dragged) return;
        App.Settings.Current.NavOrder = NavList.Items.OfType<ListBoxItem>()
            .Select(i => i.Tag as string ?? "")
            .Where(t => t.Length > 0)
            .ToList();
        App.Settings.SaveSoon();
    }

    /// <summary>Вкладка, центр которой ближе всех к точке по вертикали.</summary>
    private ListBoxItem? NavItemNearest(double y)
    {
        ListBoxItem? best = null;
        var bestDistance = double.MaxValue;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            var centerY = item.TranslatePoint(new Point(0, item.ActualHeight / 2), NavList).Y;
            var distance = Math.Abs(centerY - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = item;
            }
        }
        return best;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ------------------------------------------------------------- пикер акцента
    private bool _syncingAccent;

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (AccentOverlay.Visibility == Visibility.Visible)
        {
            AccentOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        SyncAccentFlyout();
        AccentOverlay.Visibility = Visibility.Visible;
    }

    private void OnAccentOverlayClick(object sender, MouseButtonEventArgs e)
        => AccentOverlay.Visibility = Visibility.Collapsed;

    /// <summary>Клик внутри карточки не должен закрывать пикер (всплытие гасим).</summary>
    private void AccentCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Показать сохранённый цвет в круге и полоске прозрачности (без сохранения).</summary>
    private void SyncAccentFlyout()
    {
        var stored = App.Settings.Current.AccentColor;
        var color = !string.IsNullOrWhiteSpace(stored) && ThemeService.TryParseColor(stored!, out var c)
            ? c
            : ThemeService.DefaultAccent;

        _syncingAccent = true;
        ColorMath.ToHsv(color, out var hue, out var sat, out var val);
        if (sat > 0.001) AccentWheel.Hue = hue;   // у серого оттенок не определён: круг не прыгает
        AccentWheel.Saturation = sat;
        AccentWheel.Brightness = val;
        AccentAlpha.Alpha = color.A / 255.0;
        _syncingAccent = false;
        UpdateAccentPreview(color);
    }

    private void OnAccentWheelChanged() => ApplyAccentFromControls();

    private void OnAccentAlphaChanged() => ApplyAccentFromControls();

    /// <summary>Собрать цвет из круга и полоски прозрачности, применить сразу и сохранить.</summary>
    private void ApplyAccentFromControls()
    {
        if (_syncingAccent || AccentPreview is null) return;

        var color = ColorMath.FromHsv(AccentWheel.Hue, AccentWheel.Saturation, AccentWheel.Brightness,
            (byte)Math.Round(AccentAlpha.Alpha * 255));
        UpdateAccentPreview(color);

        var hex = color.ToString();   // #AARRGGBB
        ThemeService.ApplyAccent(hex);
        App.Settings.Current.AccentColor = hex;
        App.Settings.SaveSoon();
    }

    private void UpdateAccentPreview(Color color)
    {
        AccentPreview.Background = new SolidColorBrush(color);
        AccentHexText.Text = color.ToString();
        AccentAlpha.BaseColor = color;   // полоска сама убирает альфу из заполнения
    }

    private void OnAccentReset(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.AccentColor = null;
        ThemeService.ApplyAccent(null);
        App.Settings.SaveSoon();
        SyncAccentFlyout();
    }

    /// <summary>На Windows 10 обрезаем содержимое по Border; на Windows 11 углами
    /// управляет DWM. В развёрнутом состоянии ручное скругление снимается.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRootChrome();

    private void UpdateRootChrome()
    {
        if (_useSystemCorners)
        {
            RootBorder.CornerRadius = new CornerRadius(0);
            RootBorder.Clip = null;
            return;
        }

        var radius = WindowState == WindowState.Maximized ? 0 : 12;
        RootBorder.CornerRadius = new CornerRadius(radius);
        if (RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0) return;
        RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), radius, radius);
    }

    // ------------------------------------------------------------- списки «Панели»/«SSH»

    private void OnPanelHeaderClick(object sender, MouseButtonEventArgs e)
        => ToggleNavSection(PanelMenuBox, PanelChevronRot, !App.Settings.Current.NavPanelsExpanded,
            expanded => App.Settings.Current.NavPanelsExpanded = expanded);

    private void OnSshHeaderClick(object sender, MouseButtonEventArgs e)
        => ToggleNavSection(SshMenuBox, SshChevronRot, !App.Settings.Current.NavSshExpanded,
            expanded => App.Settings.Current.NavSshExpanded = expanded);

    /// <summary>
    /// Раскрыть/свернуть выпадающий список раздела навигации: высота списка и шеврон анимируются,
    /// состояние запоминается. Список обрезается по своей высоте (ClipToBounds у обёртки).
    /// </summary>
    private static void ToggleNavSection(FrameworkElement section, RotateTransform chevron, bool expand,
        Action<bool> persist)
    {
        persist(expand);
        App.Settings.SaveSoon();
        Motion.Run(chevron, RotateTransform.AngleProperty, chevron.Angle, expand ? 180 : 0, Motion.Normal);

        if (expand)
        {
            // высоту замеряем без фиксированной высоты, затем раскрываем от нуля до неё
            section.Visibility = Visibility.Visible;
            section.Height = double.NaN;
            section.Measure(new Size(Math.Max(section.ActualWidth, 200), double.PositiveInfinity));
            Motion.Run(section, FrameworkElement.HeightProperty, 0, section.DesiredSize.Height, Motion.Normal,
                EasingMode.EaseInOut, () => section.Height = double.NaN);
        }
        else
        {
            Motion.Run(section, FrameworkElement.HeightProperty, section.ActualHeight, 0, Motion.Normal,
                EasingMode.EaseInOut, () =>
                {
                    section.Visibility = Visibility.Collapsed;
                    section.Height = double.NaN;
                });
        }
    }

    /// <summary>Клик по панели в списке: показать её браузер и перейти на вкладку «Панели».</summary>
    private void OpenPanelEntry(PanelEntry entry)
    {
        App.Settings.Current.SelectedPanelId = entry.Id;
        App.Settings.SaveSoon();
        if (_vm is not null) _vm.ActiveTab = "panel";
        PanelTab.ShowEntry(entry);
    }

    /// <summary>Клик по подключению в списке: показать его терминал и перейти на вкладку «SSH».</summary>
    private void OpenSshEntry(SshEntry entry)
    {
        App.Settings.Current.SelectedSshId = entry.Id;
        App.Settings.SaveSoon();
        if (_vm is not null) _vm.ActiveTab = "ssh";
        SshTab.ShowEntry(entry);
    }

    private void OnPanelEntryDeleted(PanelEntry entry)
    {
        PanelTab.RemoveEntry(entry);
        if (App.Settings.Current.SelectedPanelId == entry.Id) App.Settings.Current.SelectedPanelId = null;
        App.Settings.SaveSoon();
    }

    private void OnSshEntryDeleted(SshEntry entry)
    {
        SshTab.RemoveEntry(entry);
        if (App.Settings.Current.SelectedSshId == entry.Id) App.Settings.Current.SelectedSshId = null;
        App.Settings.SaveSoon();
    }

    /// <summary>Шестерёнка в строке SSH: логин и пароль конкретного подключения.</summary>
    private void OnSshEntryConfigure(SshEntry entry)
    {
        var window = new Dialogs.SshCredentialsWindow("SSH — «" + entry.Name + "»", entry.User, entry.Password)
        {
            Owner = this
        };
        if (window.ShowDialog() != true) return;
        entry.User = window.User;
        entry.Password = window.Password;
        App.Settings.SaveSoon();
    }

    /// <summary>При старте показываем последнюю открытую панель/подключение (если запись ещё есть).</summary>
    private void RestoreSelectedEntries()
    {
        var s = App.Settings.Current;

        var panel = s.Panels.FirstOrDefault(p => p.Id == s.SelectedPanelId);
        PanelTab.ShowEntry(panel);

        var ssh = s.SshItems.FirstOrDefault(x => x.Id == s.SelectedSshId);
        SshTab.ShowEntry(ssh);

        SyncMenuSelection();
    }

    /// <summary>Подсветка записей в списках «Панели»/«SSH» — строго по тому, что реально открыто.</summary>
    private void SyncMenuSelection()
    {
        _panelMenu?.Rebuild(PanelTab.Current?.Id);
        _sshMenu?.Rebuild(SshTab.Current?.Id);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ------------------------------------------------------------- разворот окна
    // Окно без рамки (WindowStyle=None + AllowsTransparency): при WindowState.Maximized WPF
    // растягивает окно на весь монитор, и оно залезает под панель задач — как фуллскрин,
    // который такому приложению не положен. Две защиты:
    //  1) перехватываем WM_GETMINMAXINFO и ограничиваем максимум рабочей областью монитора;
    //  2) после разворачивания дополнительно прижимаем окно к рабочей области через SetWindowPos —
    //     на слоёных окнах (AllowsTransparency) система местами игнорирует ограничение из п. 1.
    // «Развернуть» ведёт себя как обычное разворачивание, панель задач остаётся видимой.

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // HwndSource.FromHwnd надёжнее PresentationSource.FromVisual: последний возвращает null,
        // если HWND создан до Show (запуск свёрнутым в трей и т.п.), и хук тогда не установится
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is HwndSource source)
            source.AddHook(WndProc);

        // На Windows 11 не используем layered-окно: DWM скругляет его сам,
        // при этом сохраняется совместимость с FancyZones.
        if (_useSystemCorners) ApplyDwmRoundCorners();
    }

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = ROUND (только Windows 11).</summary>
    private void ApplyDwmRoundCorners()
    {
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            var hwnd = new WindowInteropHelper(this).Handle;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* DWM недоступен — оставляем системное оформление */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_SIZE = 0x0005;
        const int SIZE_MAXIMIZED = 2;

        if (msg == WM_GETMINMAXINFO)
        {
            // не выставляем handled: пусть WPF обработает сообщение следом (обновит свои кэши
            // min/max-размеров); наши значения в ptMaxPosition/ptMaxSize он не трогает
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    // рабочая область (rcWork) — монитор минус панель задач и док-панели
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
            }
        }
        else if (msg == WM_SIZE && wParam.ToInt32() == SIZE_MAXIMIZED)
        {
            // окно только что развернулось — проверим и, если залезло под панель задач, дожмём
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(ClampMaximizedToWorkArea));
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Если развёрнутое окно вылезло за рабочую область монитора (под панель задач),
    /// явно прижимаем его туда через SetWindowPos. Если геометрия уже правильная — ничего не делаем.
    /// </summary>
    private void ClampMaximizedToWorkArea()
    {
        if (WindowState != WindowState.Maximized) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var x = info.rcWork.Left;
        var y = info.rcWork.Top;
        var w = info.rcWork.Right - info.rcWork.Left;
        var h = info.rcWork.Bottom - info.rcWork.Top;

        if (GetWindowRect(hwnd, out var rect) &&
            rect.Left == x && rect.Top == y &&
            rect.Right - rect.Left == w && rect.Bottom - rect.Top == h)
            return;   // окно уже на месте — не трогаем

        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        switch (App.Settings.Current.CloseBehavior)
        {
            case CloseBehavior.HideToTray: HideToTray(); return;
            case CloseBehavior.Exit: ExitApplication(); return;
        }

        // Спрашиваем: скрыть в трей или выйти из приложения.
        var dialog = new Dialogs.CloseDialogWindow { Owner = this };
        var result = dialog.ShowDialog();
        if (result is null) return;   // Esc — остаёмся в приложении
        if (dialog.RememberMyChoice)
        {
            App.Settings.Current.CloseBehavior = result == true ? CloseBehavior.HideToTray : CloseBehavior.Exit;
            App.Settings.SaveSoon();
            _vm?.Preferences.OnCloseBehaviorChangedExternally();
        }
        if (result == true) HideToTray();
        else ExitApplication();
    }

    /// <summary>Удалить сохранённые входы панелей (куки-профиль WebView2: логины, сессии, кэш сайтов).</summary>
    public async Task ClearPanelLoginsAsync()
    {
        var confirmed = _dialogs?.Confirm("Удалить логины и пароли",
            "Будут удалены все сохранённые входы панелей — куки и данные сайтов встроенного браузера. " +
            "Сами панели и их адреса останутся. Продолжить?",
            okText: "Удалить", danger: true) == true;
        if (!confirmed) return;

        PanelTab?.UnloadAll();
        await Task.Delay(500);   // даём процессу WebView2 освободить папку профиля
        var deleted = await PanelPage.ClearStoredLoginsAsync();
        _dialogs?.Info("Логины и пароли",
            deleted
                ? "Сохранённые входы удалены. При следующем открытии сайтов панелей нужно будет войти заново."
                : "Папка профиля сейчас занята — очистка выполнится автоматически при следующем запуске приложения.");
    }

    private void HideToTray()
    {
        Hide();
        // свернули в трей — выгружаем страницы панелей, чтобы не ели ресурсы.
        // SSH-соединения при этом НЕ рвём.
        PanelTab?.UnloadAll();
        if (!App.Settings.Current.TrayHintShown)
        {
            App.Settings.Current.TrayHintShown = true;
            App.Settings.SaveSoon();
            _vm?.Tray.ShowHint("VibeTun", "Приложение свёрнуто в трей: клик по иконке — быстрая панель, " +
                                              "двойной клик — окно, правая кнопка — «Закрыть».");
        }
    }

    /// <summary>
    /// Полный выход: остановить ядро, вернуть прежние настройки системного прокси, убрать иконку из трея
    /// и только затем закрыть окно и завершить приложение.
    /// </summary>
    private void ExitApplication(bool restart = false)
    {
        if (_reallyExit) return;
        _reallyExit = true;
        App.RestartOnExit = restart;   // после освобождения мьютекса App запустит новый экземпляр
        StartExitWatchdog();
        _ = CloseSequenceAsync();
    }

    /// <summary>
    /// Страховка от зависания при выходе: если приложение не закрылось за 20 секунд, принудительно возвращаем
    /// системный прокси, останавливаем ядро и завершаем процесс. Работает в пуле потоков, поэтому срабатывает,
    /// даже когда UI-поток заблокирован.
    /// </summary>
    private void StartExitWatchdog()
    {
        var vm = _vm;
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
        {
            // в errors.log остаётся след: по нему видно, что выход не завершился сам
            App.LogCrash(new TimeoutException("Выход из VibeTun не завершился за 20 секунд — принудительное завершение."));
            try { vm?.PrepareForSystemShutdown(); } catch { /* ignore */ }   // вернёт прокси и сохранит настройки
            try { vm?.Core.Dispose(); } catch { /* ignore */ }               // завершит процесс ядра
            if (App.RestartOnExit) App.StartNewInstance();
            Environment.Exit(0);
        }, TaskScheduler.Default);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_reallyExit || _closed)
        {
            base.OnClosing(e);
            return;
        }

        // поведение при закрытии: свернуть в трей, выйти или спросить.
        // Из OnClosing (Alt+F4, системные закрытия) диалог не показываем — сворачиваем в трей,
        // выбор «скрыть/выйти» есть у кнопки-крестика.
        var behavior = App.Settings.Current.CloseBehavior;
        if (behavior == CloseBehavior.Ask) behavior = CloseBehavior.HideToTray;
        if (behavior == CloseBehavior.HideToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // пользователь хочет выйти — сначала корректно завершаем работу
        e.Cancel = true;
        ExitApplication();
    }

    private void SaveWindowGeometry()
    {
        var settings = App.Settings.Current;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
        }
        settings.WindowState = WindowState.ToString();
    }

    /// <summary>
    /// Последовательность выхода. Окно закрывается и приложение завершается в любом случае (finally):
    /// если какой-то шаг упал, приложение не должно остаться работать без окна.
    /// </summary>
    private async Task CloseSequenceAsync()
    {
        try
        {
            // перед выходом освобождаем браузеры панелей и ssh-сессии
            try { PanelTab?.UnloadAll(); } catch (Exception ex) { App.LogCrash(ex); }
            try { SshTab?.ShutdownAll(); } catch (Exception ex) { App.LogCrash(ex); }
            try { SaveWindowGeometry(); } catch (Exception ex) { App.LogCrash(ex); }

            if (_vm is not null)
            {
                try
                {
                    await _vm.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    App.LogCrash(ex);
                }
            }
        }
        finally
        {
            try { Close(); } catch { /* ignore */ }
            Application.Current.Shutdown();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        try
        {
            App.Settings.SaveNow();
        }
        catch { /* ignore */ }
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {

    }
}
