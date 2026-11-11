using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shell;
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
    // сплэш холодного старта: нормальная геометрия, к которой окно морфится после загрузки
    private double _normalWidth;
    private double _normalHeight;
    private double _normalMinWidth;
    private double _normalMinHeight;
    private bool _splashMode;   // true от сплэша до конца морфинга: геометрию сохранять нельзя
    // во сколько раз сплэш меньше будущего окна: из его рамки окно потом растёт (масштаб обеих осей один)
    private double _splashScale = 1.0;

    public MainWindow()
    {
        InitializeComponent();
        // Тип окна диктует DWM, а не наши предпочтения: живой акрил Windows 10 рисует размытие
        // только в layered-окне (в обычном — сплошная светлая заливка), а системный бэкдроп
        // Windows 11 (mica) — только в обычном. Поэтому на Windows 10 остаётся layered-окно.
        if (!_useSystemCorners)
        {
            AllowsTransparency = true;
            Background = HitTestBrush;
        }
        UpdateRootChrome();
        // смена темы во время работы: синхронизируем вариант mica (тёмная/светлая)
        ThemeService.ThemeChanged += () => Safe(AcrylicBackdrop.SyncTheme);
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
            () => App.Settings.SaveSoon(), _dialogs, L.T("S0922"), L.T("S0923"));
        _sshMenu = new NavSectionMenu<SshEntry>(SshMenu, () => App.Settings.Current.SshItems,
            name => new SshEntry { Name = name }, OpenSshEntry, OnSshEntryDeleted,
            () => App.Settings.SaveSoon(), _dialogs, "SSH", L.T("S0924"),
            OnSshEntryConfigure, L.T("S0925"));

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

        // Общая подсветка переезжает между вкладками и вложенными строками панелей/SSH.
        _navMarker = new SlideMarker(NavHost, NavMarker, ActiveNavItem, _ => "AccentSoft");
        if (_panelMenu is not null) _panelMenu.EntrySelectionChanged += SyncMenuSelection;
        if (_sshMenu is not null) _sshMenu.EntrySelectionChanged += SyncMenuSelection;
        NavList.SelectionChanged += (_, _) => _navMarker?.Sync(!_navDragging);
        NavList.LayoutUpdated += (_, _) => _navMarker?.Sync(_navDragging);
        Motion.AttachHoverPress(PanelHeader);
        Motion.AttachHoverPress(SshHeader);

        // Холодный старт с видимым окном: сначала показываем компактный сплэш —
        // он рисуется раньше, чем backend успевает прогрузиться (см. InitializeAsync).
        // При автозапуске в трей (--minimized) всё как раньше: сплэша нет.
        if (!App.StartMinimized)
        {
            _splashMode = true;
            _normalWidth = Width;
            _normalHeight = Height;
            _normalMinWidth = MinWidth;
            _normalMinHeight = MinHeight;
            MinWidth = 0;
            MinHeight = 0;
            MainContent.Opacity = 0;
            Splash.Visibility = Visibility.Visible;
            if (IsMaximizedSaved())
            {
                // развёрнутому окну расти некуда — сплэш просто появится и сменится кроссфейдом
                Width = 400;
                Height = 300;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            else
            {
                // Сплэш — точная уменьшенная копия будущего окна: из такой рамки окно растёт
                // симметрично от центра, и масштаб содержимого весь рост совпадает с размером окна.
                var target = TargetWindowSize();
                _splashScale = SplashScaleOf(target.Width);
                Width = Math.Round(target.Width * _splashScale);
                Height = Math.Round(target.Height * _splashScale);
                // Содержимое раскладывается сразу в итоговом размере, хотя окно пока маленькое
                // (лишнее обрежется рамкой окна). Во время роста меняется только масштаб —
                // пересчитывать вёрстку всего приложения на каждом кадре слишком дорого.
                RootBorder.Width = target.Width;
                RootBorder.Height = target.Height;
                RootBorder.HorizontalAlignment = HorizontalAlignment.Left;
                RootBorder.VerticalAlignment = VerticalAlignment.Top;
                if (TryGetSavedRect(out var saved))
                {
                    // сплэш встаёт в центр будущего окна — рост пойдёт симметрично, без перелёта
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = ClampToScreen(saved.X + (saved.Width - Width) / 2, Width,
                        SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth);
                    Top = ClampToScreen(saved.Y + (saved.Height - Height) / 2, Height,
                        SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight);
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
        }
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

    /// <summary>Активный пункт навигации или выбранная строка внутри раздела «Панели»/«SSH».</summary>
    private FrameworkElement? ActiveNavItem()
    {
        var tag = _vm?.ActiveTab ?? (NavList.SelectedValue as string);
        return tag switch
        {
            "panel" => _panelMenu?.SelectedElement,
            "ssh" => _sshMenu?.SelectedElement,
            _ => NavList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
        };
    }

    private void OnLanguageChanged()
    {
        _panelMenu?.Rebuild();
        _sshMenu?.Rebuild();
        SyncMenuSelection();
    }

    public async Task InitializeAsync()
    {
        _vm = new MainViewModel(new DialogService(this));
        DataContext = _vm;
        _vm.Preferences.BlurChanged += ApplyBlur;
        // «Системные цвета Windows» переключили в настройках — открытый пикер показывает новый цвет
        _vm.Preferences.AccentChanged += () =>
        {
            if (AccentOverlay.Visibility == Visibility.Visible) SyncAccentFlyout();
        };
        L.Changed += OnLanguageChanged;
        Closed += (_, _) => L.Changed -= OnLanguageChanged;

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
        if (!App.StartMinimized)
        {
            // сплэш: размеры из настроек применим после загрузки, в MorphFromSplashAsync
            _vm.StartupProgress += OnStartupProgress;
            try
            {
                await _vm.InitializeAsync();
            }
            finally
            {
                _vm.StartupProgress -= OnStartupProgress;
                RestoreSelectedEntries();
                await MorphFromSplashAsync();
            }
            return;
        }
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

    /// <summary>Окно было развёрнуто (строка WindowState в настройках).</summary>
    private static bool IsMaximizedSaved()
        => string.Equals(App.Settings.Current.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сохранённые размеры+позиция окна, если они осмысленны (окно было на видимом экране).</summary>
    private static bool TryGetSavedRect(out Rect rect)
    {
        var settings = App.Settings.Current;
        rect = default;
        if (settings.WindowWidth <= 600 || settings.WindowHeight <= 400) return false;
        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top) return false;
        if (left <= SystemParameters.VirtualScreenLeft - 50 || top <= SystemParameters.VirtualScreenTop - 50 ||
            left >= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 ||
            top >= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100) return false;
        rect = new Rect(left, top, settings.WindowWidth, settings.WindowHeight);
        return true;
    }

    /// <summary>Вписать координату окна размером size в отрезок экрана [areaMin, areaMin + areaSize].</summary>
    private static double ClampToScreen(double value, double size, double areaMin, double areaSize)
        => areaSize <= size ? areaMin : Math.Clamp(value, areaMin, areaMin + areaSize - size);

    private static string Diag(string line) => "[diag] " + line;

    /// <summary>Размер, который окно примет после загрузки: сохранённый в настройках или из разметки.</summary>
    private Size TargetWindowSize()
    {
        var settings = App.Settings.Current;
        return new Size(settings.WindowWidth > 600 ? settings.WindowWidth : _normalWidth,
            settings.WindowHeight > 400 ? settings.WindowHeight : _normalHeight);
    }

    /// <summary>
    /// Во сколько раз сплэш меньше будущего окна. Окно растёт ровно из рамки сплэша, поэтому
    /// масштаб один по обеим осям; границы не дают сплэшу выродиться на очень большом окне.
    /// </summary>
    private static double SplashScaleOf(double width) => Math.Clamp(460.0 / width, 0.3, 0.6);

    /// <summary>Сплэш холодного старта: строка статуса + плавно догоняющая полоска.</summary>
    private void OnStartupProgress(double fraction, string statusKey)
    {
        SplashStatus.Text = L.T(statusKey);
        Motion.To(SplashProgress, ProgressBar.ValueProperty, fraction * 100, Motion.Scaled(300));
    }

    /// <summary>
    /// Рост сплэша в полное окно: рамка и содержимое растут вместе, сплэш уходит кроссфейдом.
    /// Растёт само окно (иначе системный фон — mica или акрил — появился бы сразу большим
    /// прямоугольником), но содержимое за это время не перевёрстывается: оно разложено в итоговом
    /// размере ещё на сплэше и лишь масштабируется тем же коэффициентом, на который выросло окно.
    /// Пересчёт вёрстки всего приложения на каждом кадре стоит дороже кадра — оттого окно раньше
    /// и расширялось рывками, по очереди в разные стороны. Рамка и содержимое идут вместе не
    /// потому, что запущены одинаковые анимации, а потому, что на каждом шаге считаются
    /// из одного и того же значения хода (см. <see cref="GrowFromCenterAsync"/>).
    /// </summary>
    private async Task MorphFromSplashAsync()
    {
        Motion.To(SplashProgress, ProgressBar.ValueProperty, 100, Motion.Scaled(260));
        var grow = Motion.Scaled(520);
        if (IsMaximizedSaved())
        {
            // развёрнутое окно не растёт, а просто появляется — плавно только кроссфейд
            WindowState = WindowState.Maximized;
            Motion.Run(MainContent, OpacityProperty, 0, 1, Motion.Scaled(420));
            Motion.Run(Splash, OpacityProperty, 1, 0, Motion.Scaled(300));
            await Task.Delay(grow);
        }
        else
        {
            var target = TargetWindowSize();
            double endLeft, endTop;
            if (TryGetSavedRect(out var saved))
            {
                // сплэш уже стоит в центре будущего окна (см. конструктор) — чистый рост без перелёта
                endLeft = saved.X;
                endTop = saved.Y;
            }
            else
            {
                // сохранённой геометрии нет — растём из центра вокруг текущей середины
                endLeft = Left + (Width - target.Width) / 2;
                endTop = Top + (Height - target.Height) / 2;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;
            await GrowFromCenterAsync(endLeft, endTop, target.Width, target.Height, grow, () =>
            {
                // кроссфейд начинается с первого настоящего кадра роста: сплэш уходит
                // за его первую половину, и интерфейс виден уже в полный размер
                Motion.Run(MainContent, OpacityProperty, 0, 1, Motion.Scaled(420));
                Motion.Run(Splash, OpacityProperty, 1, 0, Motion.Scaled(300));
            });
        }
        // минимумы возвращаются после роста: пока окно меньше минимума, они его распёрли бы
        MinWidth = _normalMinWidth;
        MinHeight = _normalMinHeight;
        Splash.Visibility = Visibility.Collapsed;
        Splash.Opacity = 1;
        _splashMode = false;
    }

    /// <summary>Рост окна из сплэша уже идёт: второй запуск ни к чему.</summary>
    private bool _growing;

    /// <summary>
    /// Рост окна от центра: шаг за шагом задаём и прямоугольник окна, и масштаб содержимого —
    /// из одного значения хода, чтобы рамка и содержимое не расходились. Содержимое при этом не
    /// перевёрстывается: оно разложено в итоговом размере ещё на сплэше и только масштабируется,
    /// поэтому шаг стоит дёшево. Рост начинается с первого настоящего кадра: пока приложение
    /// не нарисовало интерфейс ни разу, шаги всё равно не попадут на экран, а время съедят —
    /// и окно распахнется одним рывком.
    /// </summary>
    private Task GrowFromCenterAsync(double endLeft, double endTop, double endWidth, double endHeight,
        TimeSpan duration, Action? started = null)
    {
        if (_growing) return Task.CompletedTask;
        _growing = true;
        var startWidth = Width;
        var startHeight = Height;
        // содержимое разложено в итоговом размере, поэтому масштаб ровно по размеру окна
        var scale = new ScaleTransform(startWidth / endWidth, startHeight / endHeight);
        RootBorder.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var finished = new TaskCompletionSource();
        // предел: если отрисовка совсем встала, рост всё равно закончится итоговой геометрией
        var limit = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 3 + 500);
        var clock = new Stopwatch();
        DispatcherTimer? timer = null;
        var hwnd = new WindowInteropHelper(this).Handle;
        var steps = 0;

        void Finish()
        {
            if (timer is not null) timer.Stop();
            _growing = false;
            // итоговая геометрия выставляется явно, а не только держится последним шагом
            Left = endLeft;
            Top = endTop;
            Width = endWidth;
            Height = endHeight;
            // содержимое снова растягивается по окну и рисуется без масштаба
            RootBorder.RenderTransform = null;
            RootBorder.Width = double.NaN;
            RootBorder.Height = double.NaN;
            RootBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            RootBorder.VerticalAlignment = VerticalAlignment.Stretch;
            finished.TrySetResult();
        }

        void OnStep(object? sender, EventArgs args)
        {
            steps++;
            // отсчёт идёт с первого шага: время до него приложение занималось своей первой
            // отрисовкой и в рост не входило, иначе рост начинался бы сразу с середины
            if (!clock.IsRunning) clock.Start();
            var time = clock.Elapsed;
            var part = Math.Clamp(time.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var step = ease.Ease(part);
            var width = startWidth + (endWidth - startWidth) * step;
            var height = startHeight + (endHeight - startHeight) * step;
            // середина будущего окна неподвижна: рамка расходится от неё в обе стороны
            Left = endLeft + (endWidth - width) / 2;
            Top = endTop + (endHeight - height) / 2;
            Width = width;
            Height = height;
            scale.ScaleX = width / endWidth;
            scale.ScaleY = height / endHeight;
            GetWindowRect(hwnd, out var rect);
            AppLog.Write(Diag(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "step {0} t={1} part={2:0.###} wpf={3:0.#}x{4:0.#} hwnd={5}x{6} ({7:0.#}->{8:0.#})",
                steps, clock.ElapsedMilliseconds, part, width, height,
                rect.Right - rect.Left, rect.Bottom - rect.Top, startWidth, endWidth)));
            if (part < 1 && !_closed && time < limit) return;
            Finish();
        }

        var began = false;
        void OnFirstFrame(object? sender, EventArgs args)
        {
            if (began) return;
            began = true;
            CompositionTarget.Rendering -= OnFirstFrame;
            started?.Invoke();
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, OnStep, Dispatcher);
            timer.Start();
        }

        CompositionTarget.Rendering += OnFirstFrame;
        // страховка: если кадров нет вовсе, рост всё равно начнётся — окно не должно застрять сплэшем
        var fallback = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Normal,
            (sender, args) => { ((DispatcherTimer)sender).Stop(); OnFirstFrame(sender, args); }, Dispatcher);
        fallback.Start();
        return finished.Task;
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
    private AdornerLayer? _navDragLayer;
    private ReorderDrag.Preview? _navDragPreview;

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
            if (AdornerLayer.GetAdornerLayer(NavList) is { } layer)
            {
                _navDragLayer = layer;
                _navDragPreview = new ReorderDrag.Preview(NavList, dragged, e.GetPosition(dragged));
                layer.Add(_navDragPreview);
                _navDragPreview.MoveTo(pos);
            }
        }

        _navDragPreview?.MoveTo(pos);

        var target = NavItemNearest(pos.Y);
        if (target is null || ReferenceEquals(target, dragged)) return;

        var from = NavList.Items.IndexOf(dragged);
        var to = NavList.Items.IndexOf(target);
        if (from < 0 || to < 0) return;

        // при удалении выбранной вкладки WPF сбрасывает выбор — вернём его тем же значением
        var active = NavList.SelectedValue;
        var before = ReorderDrag.Capture(NavList, NavList.Items.OfType<ListBoxItem>());
        NavList.Items.RemoveAt(from);
        NavList.Items.Insert(to, dragged);
        NavList.SelectedValue = active;
        NavList.UpdateLayout();
        ReorderDrag.AnimateReflow(NavList, before, NavList.Items.OfType<ListBoxItem>());
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
        if (_navDragPreview is not null && _navDragLayer is not null) _navDragLayer.Remove(_navDragPreview);
        _navDragPreview = null;
        _navDragLayer = null;

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
            // слот в layout, а не анимированная позиция: иначе середина пролетает мимо
            // неподвижного курсора и просит обратный ход (та же правка, что у профилей)
            var centerY = item.TranslatePoint(new Point(0, item.ActualHeight / 2), NavList).Y;
            if (item.RenderTransform is TranslateTransform move) centerY -= move.Y;
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
        var color = App.Settings.Current.UseSystemAccent
            ? ThemeService.GetSystemAccentColor()
            : !string.IsNullOrWhiteSpace(stored) && ThemeService.TryParseColor(stored!, out var c)
                ? c
                : ThemeService.DefaultAccent;

        _syncingAccent = true;
        SystemAccentToggle.IsChecked = App.Settings.Current.UseSystemAccent;
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
        App.Settings.Current.UseSystemAccent = false;
        SystemAccentToggle.IsChecked = false;
        ThemeService.ApplyAccent(hex);
        App.Settings.Current.AccentColor = hex;
        App.Settings.SaveSoon();
        _vm?.Preferences.RaiseAccent();   // ручной цвет выключает системный: переключатель в настройках следует
    }

    private void UpdateAccentPreview(Color color)
    {
        AccentPreview.Background = new SolidColorBrush(color);
        AccentHexText.Text = color.ToString();
        AccentAlpha.BaseColor = color;   // полоска сама убирает альфу из заполнения
    }

    private void OnAccentReset(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.UseSystemAccent = false;
        App.Settings.Current.AccentColor = null;
        ThemeService.ApplyAccent(null);
        App.Settings.SaveSoon();
        _vm?.Preferences.RaiseAccent();
        SyncAccentFlyout();
    }

    /// <summary>
    /// «Использовать системные цвета Windows» в пикере. Сам режим живёт в настройках
    /// (<see cref="SettingsViewModel.UseSystemAccent"/>): тот же переключатель есть на вкладке
    /// «Настройки», и оба меняют одно свойство.
    /// </summary>
    private void OnAccentSystemToggle(object sender, RoutedEventArgs e)
    {
        if (_syncingAccent || _vm is null) return;
        _vm.Preferences.UseSystemAccent = SystemAccentToggle.IsChecked == true;
        SyncAccentFlyout();
    }

    /// <summary>Углами окна управляет DWM (Windows 11) или никто — квадратные углы у
    /// layered-окна Windows 10, где живёт акрил. Регион окна не задаём: на Windows 11 он
    /// портит системную тень, на Windows 10 ломал акрил.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRootChrome();

    private void UpdateRootChrome()
    {
        RootBorder.CornerRadius = new CornerRadius(0);
        RootBorder.Clip = null;
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
        PanelTab.ShowEntry(entry);
        if (_vm is not null) _vm.ActiveTab = "panel";
    }

    /// <summary>Клик по подключению в списке: показать его терминал и перейти на вкладку «SSH».</summary>
    private void OpenSshEntry(SshEntry entry)
    {
        App.Settings.Current.SelectedSshId = entry.Id;
        App.Settings.SaveSoon();
        SshTab.ShowEntry(entry);
        if (_vm is not null) _vm.ActiveTab = "ssh";
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

    /// <summary>Подсветка записей только в активном разделе; общая плашка переезжает к выбранной строке.</summary>
    private void SyncMenuSelection()
    {
        var activeTab = _vm?.ActiveTab;
        _panelMenu?.Select(activeTab == "panel" ? PanelTab.Current?.Id : null);
        _sshMenu?.Select(activeTab == "ssh" ? SshTab.Current?.Id : null);
        _navMarker?.Sync(true);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ------------------------------------------------------------- разворот окна
    // Окно без рамки (WindowStyle=None + WindowChrome): при WindowState.Maximized WPF
    // растягивает окно на весь монитор, и оно залезает под панель задач — как фуллскрин,
    // который такому приложению не положен. Две защиты:
    //  1) перехватываем WM_GETMINMAXINFO и ограничиваем максимум рабочей областью монитора;
    //  2) после разворачивания дополнительно прижимаем окно к рабочей области через SetWindowPos —
    //     местами система игнорирует ограничение из п. 1.
    // «Развернуть» ведёт себя как обычное разворачивание, панель задач остаётся видимой.

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // блюр левой панели: системный бэкдроп; без него панель остаётся непрозрачной.
        // AcrylicBackdrop.Apply сам выбирает API под ОС и сообщает, доступен ли бэкдроп. Mica на
        // Windows 11 после этого не трогаем; живой акрил Windows 10 ApplyBlur оставляет включённым
        // только при включённом размытии (без него окно зря платило бы за акрил при перетаскивании).
        _acrylicOk = AcrylicBackdrop.Apply(this);
        ApplyBlur(App.Settings.Current.BlurSidebar);
        // HwndSource.FromHwnd надёжнее PresentationSource.FromVisual: последний возвращает null,
        // если HWND создан до Show (запуск свёрнутым в трей и т.п.), и хук тогда не установится
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is HwndSource source)
            source.AddHook(WndProc);

        // Скругление углов на Windows 11 делает сам DWM (регион при блюре — в UpdateRootChrome)
        if (_useSystemCorners) ApplyDwmRoundCorners();
    }

    /// <summary>
    /// Почти прозрачная кисть: WPF не пропускает клики через неё, в отличие от Brushes.Transparent.
    /// </summary>
    private static readonly Brush HitTestBrush = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));

    /// <summary>Левая панель с размытием: оно включено в настройках и доступно в системе.</summary>
    private bool _glass;

    /// <summary>Системный бэкдроп (акрил или mica) удалось назначить окну; без него панель всегда непрозрачная.</summary>
    private bool _acrylicOk;

    /// <summary>Переключатель «Размытие левой панели» в настройках.</summary>
    private void ApplyBlur(bool on) => ApplyGlass(on && _acrylicOk);

    private void ApplyGlass(bool glass)
    {
        _glass = glass;
        RenderGlass(glass);
    }

    // ------------------------------------------------------- кадр во время перетаскивания
    // Windows 10: живой акрил композитор пересчитывает на каждый сдвиг окна. Если при этом WPF
    // отрисовывает содержимое асинхронно, оно отстаёт от HWND — на резком рывке окно «плывёт».
    // Лечится не снятием размытия, а синхронизацией кадра (см. SynchronizeFrameWithDwm).

    /// <summary>Windows 10 с включённым размытием: только такое окно и стоит синхронизировать.</summary>
    private bool IsLegacyGlass() => !_useSystemCorners && _glass;

    /// <summary>Идёт синхронизация кадра: повторный вход из вложенного цикла диспетчера не нужен.</summary>
    private bool _syncingFrame;

    /// <summary>Сколько миллисекунд текущее перетаскивание уже простояло в ожидании кадра DWM.</summary>
    private double _flushWaitedMs;

    /// <summary>Дольше этого кадр DWM не ждём: перетаскивание не должно вставать из-за ожидания.</summary>
    private const int FlushTimeoutMs = 100;

    /// <summary>Ожидание кадра DWM, запущенное в фоне (одно на всё окно).</summary>
    private static Task<int>? _flushTask;

    /// <summary>
    /// Дождаться кадра DWM, но не дольше <see cref="FlushTimeoutMs"/>. Ожидание идёт в отдельном
    /// потоке: на части машин DwmFlush не возвращается, пока не придёт новый кадр, — вызов прямо
    /// в WndProc остановил бы само перетаскивание. Незавершённое ожидание не дублируется: новый
    /// поток запускается, только когда предыдущее ожидание закончилось.
    /// </summary>
    private static bool WaitForDwmFrame()
    {
        if (_flushTask is null || _flushTask.IsCompleted) _flushTask = Task.Run(DwmFlush);
        try
        {
            return _flushTask.Wait(FlushTimeoutMs);
        }
        catch
        {
            return false;   // DWM недоступен — синхронизировать кадр нечем, окно ведёт себя как обычно
        }
    }

    /// <summary>
    /// Windows 10: дождаться, пока содержимое окна окажется нарисованным на новой позиции.
    /// Пока окно тащат, Windows крутит свой цикл — двигает HWND и ждёт следующего сообщения, —
    /// а WPF рисует по своему таймеру: кадр приходит позже, чем DWM забирает окно, и содержимое
    /// отстаёт от рамки. Поэтому на каждом сообщении перемещения: выполняем накопившуюся работу
    /// диспетчера, пустым Invoke с приоритетом Render завершаем проход отрисовки и ждём, пока DWM
    /// представит кадр, — следующий сдвиг не начинается, пока предыдущий кадр не на экране.
    /// Страховка: если перетаскивание уже простояло в ожидании 200 мс, до конца перетаскивания
    /// синхронизация уступает отрисовке — лучше пропущенный кадр, чем накопленная задержка.
    /// В покое акрил и кадры не трогаются, на Windows 11 (mica) тоже.
    /// </summary>
    private void SynchronizeFrameWithDwm()
    {
        if (_syncingFrame || _flushWaitedMs > 200) return;

        _syncingFrame = true;
        try
        {
            // накопившаяся работа диспетчера (компоновка, анимации), затем сам проход отрисовки
            Dispatcher.Invoke(static () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            Dispatcher.Invoke(static () => { },
                System.Windows.Threading.DispatcherPriority.Render);
            var before = DateTime.UtcNow;
            WaitForDwmFrame();
            _flushWaitedMs += (DateTime.UtcNow - before).TotalMilliseconds;
        }
        finally
        {
            _syncingFrame = false;
        }
    }

    /// <summary>Windows 10: отложенное снятие акрила (см. <see cref="SetLegacyAcrylic"/>).</summary>
    private System.Windows.Threading.DispatcherTimer? _legacyAcrylicOff;

    /// <summary>
    /// Windows 10: живой акрил держим включённым, только пока он виден (AcrylicBackdrop.SetLegacyAcrylic).
    /// Включается он сразу — до того, как слои окна станут прозрачными. Снимается с небольшой задержкой,
    /// когда кадр с закрашенными слоями уже на экране: иначе на миг сквозь окно был бы виден рабочий
    /// стол без размытия. Во время перетаскивания акрил остаётся включённым: отставание окна снимает
    /// синхронизация кадра с DWM (SynchronizeFrameWithDwm), а не снятие размытия.
    /// </summary>
    private void SetLegacyAcrylic(bool on)
    {
        if (_useSystemCorners || !_acrylicOk) return;
        if (on)
        {
            _legacyAcrylicOff?.Stop();
            AcrylicBackdrop.SetLegacyAcrylic(this, true);
            return;
        }

        if (_legacyAcrylicOff is null)
        {
            _legacyAcrylicOff = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _legacyAcrylicOff.Tick += (_, _) =>
            {
                _legacyAcrylicOff?.Stop();
                if (_closed) return;
                // за это время размытие могли вернуть — тогда акрил не трогаем
                if (!_glass) AcrylicBackdrop.SetLegacyAcrylic(this, false);
            };
        }
        _legacyAcrylicOff.Stop();
        _legacyAcrylicOff.Start();
    }

    /// <summary>Слои окна: прозрачные под размытие либо непрозрачные, цветом темы.</summary>
    private void RenderGlass(bool glass)
    {
        SetLegacyAcrylic(glass);
        if (glass)
        {
            if (_useSystemCorners)
            {
                // Windows 11: поверх mica — полупрозрачный оттенок темы. Mica тонируется от обоев,
                // но без слоя темы фон выглядит чужеродно; слишком плотный слой убивает фактуру.
                SidebarTint.SetResourceReference(Border.BackgroundProperty, "PanelGlassBlur");
                HeaderTint.SetResourceReference(Border.BackgroundProperty, "PanelGlassBlur");
                NavFade.SetResourceReference(Border.BackgroundProperty, "PanelGlassBlur");
            }
            else
            {
                // Windows 10: над живым акрилом затемняющих слоёв нет — панель прозрачная,
                // цвет и размытие даёт сам accent acrylic
                SidebarTint.Background = HitTestBrush;
                HeaderTint.Background = HitTestBrush;
                NavFade.Background = HitTestBrush;
            }
            RootBorder.Background = HitTestBrush;
            // фон самого окна почти прозрачный: после выключения там стоял непрозрачный WindowBg
            Background = HitTestBrush;
            ConnectionPanel.Glass = true;
        }
        else
        {
            SidebarTint.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            HeaderTint.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            NavFade.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            RootBorder.SetResourceReference(Border.BackgroundProperty, "WindowBg");
            SetResourceReference(BackgroundProperty, "WindowBg");
            ConnectionPanel.Glass = false;
        }
        UpdateRootChrome();
    }

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE = ROUND (только Windows 11).</summary>
    private void ApplyDwmRoundCorners() => DwmCorners.ApplyRound(this);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_SIZE = 0x0005;
        const int WM_MOVING = 0x0216;
        const int WM_ENTERSIZEMOVE = 0x0231;
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
        else if (msg == WM_ENTERSIZEMOVE)
        {
            // новое перетаскивание: бюджет ожидания кадров начинается заново
            _flushWaitedMs = 0;
        }
        else if (msg == WM_MOVING && IsLegacyGlass())
        {
            // Windows 10: живой акрил остаётся включённым — панель не перекрашивается. Отставание
            // снимает синхронизация кадра: WPF успевал отрисовать кадр позже, чем DWM собирал окно,
            // и содержимое отставало от HWND при резком движении. Сначала даём осесть отложенной
            // работе диспетчера, затем форсируем проход отрисовки и ждём, пока DWM представит кадр.
            SynchronizeFrameWithDwm();
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

    /// <summary>Ждёт, пока DWM представит текущий кадр композиции (синхронизация с перетаскиванием).</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        switch (App.Settings.Current.CloseBehavior)
        {
            case CloseBehavior.HideToTray: HideToTray(); return;
            case CloseBehavior.Exit: ExitApplication(); return;
        }

        // Спрашиваем: скрыть в трей или выйти из приложения.
        var dialog = new Dialogs.CloseDialogWindow { Owner = this };
        dialog.ShowDialog();
        var result = dialog.CloseChoice;
        if (result is null) return;   // Крестик или Esc закрывает только диалог.
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
        var confirmed = _dialogs?.Confirm(L.T("S0249"),
            L.T("S0926") +
            L.T("S0927"),
            okText: L.T("S0083"), danger: true) == true;
        if (!confirmed) return;

        PanelTab?.UnloadAll();
        await Task.Delay(500);   // даём процессу WebView2 освободить папку профиля
        var deleted = await PanelPage.ClearStoredLoginsAsync();
        _dialogs?.Info(L.T("S0928"),
            deleted
                ? L.T("S0929")
                : L.T("S0930"));
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
            _vm?.Tray.ShowHint("VibeTun", L.T("S0931") +
                                              L.T("S0932"));
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
            App.LogCrash(new TimeoutException(L.T("S0933")));
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
        // пока висит сплэш или идёт морфинг размеры переходные (400x300, кадры роста) — их сохранять нельзя
        if (_splashMode) return;
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
