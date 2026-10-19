using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Threading;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Корневой ViewModel: состояние приложения, вкладки, запуск/остановка прокси, трей.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ClashApiClient _api = new();
    private readonly DispatcherTimer _statsTimer;

    /// <summary>Обход DPI: локальный прокси с десинхронизацией (живёт отдельно от ядра).</summary>
    private readonly DpiBypassEngine _dpi = new();

    private string _activeTab = "profiles";
    private string _connectionTab = "proxy";
    private bool _isProxyOn;
    private bool _busy;
    private string _statusMessage = "";
    private bool _statusIsError;
    private int _activePing = -1;
    private string _trafficUploadText = "—";
    private string _trafficDownloadText = "";
    private int _connectionCount;
    private readonly DateTime _startedAt = DateTime.Now;
    private DateTime? _proxyStartedAt;

    private bool _dpiEnabled;
    private DpiBypassState _dpiState = DpiBypassState.Off;
    private DateTime? _dpiStartedAt;

    /// <summary>
    /// Кто последним взял системный прокси Windows: обход DPI или прокси-ядро.
    /// Оба режима могут быть включены одновременно, а системный прокси — один.
    /// </summary>
    private bool _dpiOwnsSystemProxy;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        ProfileStore = new ProfileStore();
        RouteStore = new RouteStore();
        SystemProxy = new SystemProxyService();
        Core = new CoreService();
        Latency = new LatencyService(Core);
        Tray = new TrayService();
        Logs = new LogFeed(App.Settings.Current.LogLimit);
        App.Settings.Changed += () => Logs.MaxLines = App.Settings.Current.LogLimit;

        Profiles = new ProfilesViewModel(ProfileStore, Latency, _dialogs, Logs);
        Routing = new RoutingViewModel(RouteStore, _dialogs);
        // пинг DNS при активном TUN бессмыслен: ядро перехватывает DNS-запросы (hijack-dns)
        Dns = new DnsViewModel(_dialogs, () => Core.TunActive);
        // пинг сервиса в разделе «DPI» идёт через поднятый обход, если он есть
        Dpi = new DpiViewModel(_dialogs, () => _dpi.Port);
        Preferences = new SettingsViewModel(App.Settings, Core, _dialogs);
        Updates = new UpdatesViewModel(_dialogs);

        Profiles.ConnectRequested += profile => _ = ConnectToAsync(profile);
        Profiles.Notify += (text, error) => ShowStatus(text, error);
        Profiles.PingUpdated += (profile, ms) =>
        {
            // Включая ошибки (-1): карточка и строка всегда показывают один результат.
            if (_activeProfile?.Id == profile.Id) ActivePing = ms;
        };
        Routing.Notify += (text, error) => ShowStatus(text, error);
        Routing.SettingsChanged += () => { /* маршрутизация перечитывается при следующем запуске */ };
        Routing.RulesPersisted += () => _ = ReapplyRoutingAsync();
        Dns.Notify += (text, error) => ShowStatus(text, error);
        Dns.DnsChanged += what => _ = ReapplyDnsAsync(what);
        Dpi.Notify += (text, error) => ShowStatus(text, error);
        // Сервисы и профили раздела «DPI» читаются движком на каждом соединении (StrategyResolver),
        // поэтому перезапуск не нужен: уже открытые соединения живут, новые берут новый набор приёмов.
        Dpi.Changed += () =>
        {
            RaiseDpiStats();   // у сервисов свой набор приёмов — подпись на карточке подключения меняется
            ShowStatus(L.F("dpi.log.servicesChanged", Dpi.Services.Count), false);
        };
        // «Выбрать DNS» в окне сервиса: уводим пользователя в раздел «DNS», выбор возвращает его обратно
        Dpi.DnsPickRequested += service => _ = PickDnsForServiceAsync(service);
        Preferences.Notify += (text, error) => ShowStatus(text, error);
        Preferences.ThemeChanged += () => Raise(nameof(IsDark));
        Preferences.RestartRequested += () => RestartRequested?.Invoke();
        Updates.Notify += (text, error) => ShowStatus(text, error);
        Updates.RestartRequested += () => RestartRequested?.Invoke();
        Updates.ExitRequested += () => ExitRequested?.Invoke();

        // правки «Общих» в разделе «DPI», выбор DNS в разделе «DNS» и DNS обхода применяются сразу:
        // движок перезапускаем, если он поднят
        Dpi.GeneralChanged += () => _ = RestartDpiIfRunningAsync();
        Dns.DpiDnsChanged += () => _ = RestartDpiIfRunningAsync();

        // «Сбросить настройки» заменил весь settings.json — раздел «DNS» перечитывает свой блок;
        // если DNS ядра от этого поменялся, а прокси поднят, переподключаемся, как при выборе в разделе
        Preferences.SettingsReset += () =>
        {
            if (Dns.ReloadAfterReset() && IsProxyOn) _ = ReapplyDnsAsync("DNS: " + Dns.SelectionText);
        };

        _dpi.Logged += line => AppendLog("DPI: " + line);
        // Домен соединения → профиль сервиса из раздела «DPI»; null — общий набор из настроек.
        // Настройки читаются на каждом соединении, поэтому правки применяются без перезапуска.
        _dpi.StrategyResolver = host => DpiServiceCatalog.StrategyFor(App.Settings.Current, host);

        // Запасные стратегии сервиса (то, что нашла кнопка «Подобрать»): если первая не откроет
        // поток, движок попробует следующую. Список тоже читается на каждом соединении.
        _dpi.StrategyListResolver = host => DpiServiceCatalog.StrategyListFor(App.Settings.Current, host);
        _dpi.RouteLabelResolver = host => DpiServiceCatalog.RouteLabelFor(App.Settings.Current, host);

        // BeginInvoke, а не Invoke: событие может прийти из потока пула, который в этот момент держит
        // блокировку процесса ядра (обработчик Exited). Ждать UI-поток там нельзя: получится взаимная блокировка.
        Core.StateChanged += _ =>
        {
            try { App.Current.Dispatcher.BeginInvoke(new Action(UpdateStateText)); }
            catch { /* приложение завершается */ }
        };
        Core.LogReceived += line => AppendLog(line);
        AppLog.Written += AppendLog;   // SSH и прочие части приложения пишут в тот же журнал

        Tray.OpenRequested += () => ShowWindowRequested?.Invoke();
        Tray.ExitRequested += () => ExitRequested?.Invoke();

        ToggleProxyCommand = new AsyncRelayCommand(ToggleProxyAsync, () => !Busy);
        ToggleDpiCommand = new AsyncRelayCommand(ToggleDpiAsync, () => !Busy);
        InstallCoreCommand = new AsyncRelayCommand(InstallCoreAsync, () => !Busy);
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        TelegramProxyCommand = new RelayCommand(_ => OpenTelegramProxy(), _ => ActiveProfile?.IsTelegramOnly == true);
        ShowLogCommand = new RelayCommand(_ => _dialogs.ShowCoreLog(Core.RecentLogText()));
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += async (_, _) => await UpdateStatsAsync();

        UpdateStateText();
    }

    public ProfileStore ProfileStore { get; }
    public RouteStore RouteStore { get; }
    public LatencyService Latency { get; }
    public SystemProxyService SystemProxy { get; }
    public CoreService Core { get; }
    public TrayService Tray { get; }

    /// <summary>Журнал приложения и ядра для панели «Логи» на вкладке «Профили».</summary>
    public LogFeed Logs { get; }

    public ProfilesViewModel Profiles { get; }
    public RoutingViewModel Routing { get; }

    /// <summary>Раздел «DNS»: выбранный DNS ядра, провайдеры и свои серверы.</summary>
    public DnsViewModel Dns { get; }

    /// <summary>Раздел «DPI»: сервисы со своим набором приёмов обхода и профили настроек.</summary>
    public DpiViewModel Dpi { get; }

    public SettingsViewModel Preferences { get; }
    public UpdatesViewModel Updates { get; }

    public AsyncRelayCommand ToggleProxyCommand { get; }
    public AsyncRelayCommand InstallCoreCommand { get; }

    /// <summary>Включить / выключить обход DPI.</summary>
    public AsyncRelayCommand ToggleDpiCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand TelegramProxyCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    /// <summary>Прогресс холодного старта для сплэша: доля 0..1 + ключ строки статуса.</summary>
    public event Action<double, string>? StartupProgress;

    /// <summary>Перезапуск приложения (нужен после смены места хранения данных).</summary>
    public event Action? RestartRequested;

    // --------------------------------------------------------------- состояние
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            // при перестановке вкладок WPF на миг сбрасывает выбор в null — такое значение игнорируем
            if (string.IsNullOrEmpty(value)) return;
            if (!SetProperty(ref _activeTab, value)) return;
            App.Settings.Current.LastTab = value;
            App.Settings.SaveSoon();
            if (string.Equals(value, "updates", StringComparison.Ordinal))
                _ = Updates.EnsureLoadedAsync();
            if (string.Equals(value, "dns", StringComparison.Ordinal))
                Dns.OnOpened();
            if (string.Equals(value, "dpi", StringComparison.Ordinal))
                Dpi.OnOpened();
        }
    }

    // ----------------------------- переключатель «Proxy» / «DPI bypass» -----------------------------
    /// <summary>Какая из двух вкладок открыта в левой карточке: "proxy" или "dpi".</summary>
    public string ConnectionTab
    {
        get => _connectionTab;
        private set
        {
            if (value != "proxy" && value != "dpi")
            {
                // переключатель — пара кнопок: даже при отказе возвращаем им правильное состояние
                Raise(nameof(ConnectionTabIsProxy));
                Raise(nameof(ConnectionTabIsDpi));
                return;
            }

            if (SetProperty(ref _connectionTab, value))
            {
                App.Settings.Current.ConnectionTab = value;
                App.Settings.SaveSoon();
            }

            Raise(nameof(ConnectionTabIsProxy));
            Raise(nameof(ConnectionTabIsDpi));
        }
    }

    /// <summary>Открыта вкладка «Proxy» (для пары кнопок-переключателей).</summary>
    public bool ConnectionTabIsProxy
    {
        get => _connectionTab != "dpi";
        set
        {
            if (value) ConnectionTab = "proxy";
            else { Raise(nameof(ConnectionTabIsProxy)); Raise(nameof(ConnectionTabIsDpi)); }
        }
    }

    /// <summary>Открыта вкладка «DPI bypass» (для пары кнопок-переключателей).</summary>
    public bool ConnectionTabIsDpi
    {
        get => _connectionTab == "dpi";
        set
        {
            if (value) ConnectionTab = "dpi";
            else { Raise(nameof(ConnectionTabIsProxy)); Raise(nameof(ConnectionTabIsDpi)); }
        }
    }

    // ------------------------------------------------------------------ обход DPI
    /// <summary>Обход DPI включён (локальный прокси с десинхронизацией поднят).</summary>
    public bool DpiEnabled
    {
        get => _dpiEnabled;
        private set
        {
            if (!SetProperty(ref _dpiEnabled, value)) return;
            Raise(nameof(DpiToggleText));
        }
    }

    /// <summary>Подпись главной кнопки вкладки «DPI bypass».</summary>
    public string DpiToggleText => DpiEnabled ? L.T("S0675") : L.T("S0676");

    public DpiBypassState DpiState
    {
        get => _dpiState;
        private set
        {
            if (!SetProperty(ref _dpiState, value)) return;
            Raise(nameof(DpiStateText));
            Raise(nameof(DpiBrushKey));
        }
    }

    /// <summary>Текст там, где на вкладке «Proxy» написано «Подключено».</summary>
    public string DpiStateText => DpiState switch
    {
        DpiBypassState.Broken => L.T("S0677"),
        DpiBypassState.Covered => L.T("S0678"),
        DpiBypassState.Checking => L.T("S0679"),
        DpiBypassState.Starting => L.T("S0680"),
        DpiBypassState.NoNetwork => L.T("S0681"),
        DpiBypassState.Error => L.T("S0682"),
        DpiBypassState.On => L.T("S1000"),
        _ => L.T("S0683")
    };

    /// <summary>Цвет кружочка слева: зелёный — сломано, красный — накрыт.</summary>
    public string DpiBrushKey => DpiState switch
    {
        DpiBypassState.Broken => "Success",
        DpiBypassState.Covered => "Danger",
        DpiBypassState.Error => "Danger",
        DpiBypassState.Checking => "Warning",
        DpiBypassState.Starting => "Warning",
        DpiBypassState.NoNetwork => "Warning",
        DpiBypassState.On => "Accent",
        _ => "TextMuted"
    };

    /// <summary>Адрес локального прокси обхода (показываем, чтобы можно было настроить браузер вручную).</summary>
    public string DpiPortText => "127.0.0.1:" + App.Settings.Current.Dpi.ListenPort;

    /// <summary>
    /// Какие стратегии сейчас включены: общий набор из настроек плюс число сервисов, у которых
    /// свой набор приёмов (он описан на вкладке «DPI»).
    /// </summary>
    public string DpiStrategiesText
    {
        get
        {
            var text = DpiTester.Describe(App.Settings.Current.Dpi);
            var count = App.Settings.Current.DpiServices.Services.Count(s => s.Enabled);
            return count == 0 ? text : text + " · " + L.F("dpi.servicesCount", count);
        }
    }

    public string DpiTrafficText => _dpi.BytesUp == 0 && _dpi.BytesDown == 0
        ? "—"
        : $"↑ {Text.Bytes(_dpi.BytesUp)}   ↓ {Text.Bytes(_dpi.BytesDown)}";

    public string DpiUptimeText => _dpiStartedAt is null
        ? "—"
        : (DateTime.Now - _dpiStartedAt.Value).ToString(@"hh\:mm\:ss");

    public string DpiSessionsText
    {
        get
        {
            var count = _dpi.SessionCount;
            return count == 0 ? L.T("S0685") : count + L.T("S0686");
        }
    }

    public bool IsDark => ThemeService.EffectiveMode != ThemeMode.Light;

    public string ThemeIcon => IsDark ? "🌙" : "☀";

    public bool IsProxyOn
    {
        get => _isProxyOn;
        private set
        {
            if (!SetProperty(ref _isProxyOn, value)) return;
            Raise(nameof(ProxyToggleText));
            Raise(nameof(StatusBrushKey));
        }
    }

    public string ProxyToggleText => IsProxyOn ? L.T("S0687") : L.T("S0688");

    /// <summary>Активный профиль — Telegram-прокси (MTProto).</summary>
    public bool IsTelegramProfile => ActiveProfile?.IsTelegramOnly == true;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            ToggleProxyCommand.RaiseCanExecuteChanged();
            InstallCoreCommand.RaiseCanExecuteChanged();
            ToggleDpiCommand.RaiseCanExecuteChanged();
        }
    }

    public ProxyProfile? ActiveProfile => _activeProfile;
    private ProxyProfile? _activeProfile;

    public string ActiveProfileName => ActiveProfile?.Name ?? L.T("S0689");
    public string ActiveProfileSubtitle
    {
        get
        {
            if (ActiveProfile is null) return L.T("S0690");
            var route = Routing.ActiveProfile;
            return $"{ActiveProfile.ProtocolLabel} · {ActiveProfile.AddressLabel} · {route.ListenAddress}:{route.ListenPort}";
        }
    }

    public string StateText { get; private set; } = L.T("S0691");
    public string StatusBrushKey { get; private set; } = "TextMuted";

    public int ActivePing
    {
        get => _activePing;
        private set
        {
            if (SetProperty(ref _activePing, value)) Raise(nameof(ActivePingText));
        }
    }

    public string ActivePingText => _activePing < 0 ? "— ms" : _activePing == 0 ? "<1 ms" : $"{_activePing} ms";

    public string TrafficUploadText
    {
        get => _trafficUploadText;
        private set => SetProperty(ref _trafficUploadText, value);
    }

    public string TrafficDownloadText
    {
        get => _trafficDownloadText;
        private set => SetProperty(ref _trafficDownloadText, value);
    }

    public string ConnectionCountText => _connectionCount == 0 ? "—" : _connectionCount + L.T("S0686");

    public string UptimeText => _proxyStartedAt is null
        ? "—"
        : (DateTime.Now - _proxyStartedAt.Value).ToString(@"hh\:mm\:ss");

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    // ------------------------------------------------------------------- старт
    public async Task InitializeAsync()
    {
        App.Settings.Load();
        AppPaths.EnsureCreated();
        ThemeService.Apply(App.Settings.Current.Theme);
        Raise(nameof(IsDark));
        Raise(nameof(ThemeIcon));

        _connectionTab = App.Settings.Current.ConnectionTab == "dpi" ? "dpi" : "proxy";
        Raise(nameof(ConnectionTab));
        Raise(nameof(ConnectionTabIsProxy));
        Raise(nameof(ConnectionTabIsDpi));

        Routing.Load();
        // DNS раньше жил в профиле маршрутизации: при первом запуске выбор переносится оттуда
        Dns.Load(Routing.ActiveProfile);
        Profiles.Load();
        SystemProxy.CleanupStale(Routing.Profiles.Select(p => p.ListenPort)
            .Append(App.Settings.Current.Dpi.ListenPort));
        Preferences.RefreshCoreInfo();
        _activeProfile = ActiveProfileFromSettings();
        Profiles.SetActive(_activeProfile?.Id);
        Raise(nameof(IsTelegramProfile));
        ActivePing = _activeProfile?.LastPingMs ?? -1;
        Raise(nameof(ActiveProfileName));
        Raise(nameof(ActiveProfileSubtitle));
        StartupProgress?.Invoke(0.25, "S1005");

        // ядра вшиты в приложение — при первом запуске просто распаковываем выбранный вариант
        StartupProgress?.Invoke(0.55, "S1006");
        try
        {
            if (BundledCores.HasOfficial || BundledCores.HasExtended)
                await BundledCores.EnsureExtractedAsync(App.Settings.Current.CoreFlavor);
        }
        catch
        {
            /* не критично: ядро можно установить вручную в настройках */
        }

        await Core.DetectAsync(App.Settings.Current.CorePath, App.Settings.Current.CoreFlavor);
        Preferences.RefreshCoreInfo();
        UpdateStateText();
        UpdateTrayStatus();

        // первая строка app.log: по ней видно, какая сборка приложения и какое ядро запущены
        AppendLog(L.F("S0692", AppInfo.Full) +
                  (Core.Info.Exists ? Core.Info.Path + ", " + Core.Info.Version : L.T("S0275")));

        // что сообщить о месте хранения данных: перенос или причину, по которой взята другая папка
        if (App.StartupNotice is { } storageNotice)
            ShowStatus(storageNotice, StorageLocation.FallbackReason is not null);

        if (!Core.Info.Exists)
            ShowStatus(L.T("S0693"), true);

        if (App.Settings.Current.RememberState && App.Settings.Current.ProxyWasActive && _activeProfile is not null)
        {
            ShowStatus(L.T("S0694"), false);
            StartupProgress?.Invoke(0.7, "S0694");
            await StartProxyAsync(autoRestore: true);
        }

        if (App.Settings.Current.RememberState && App.Settings.Current.Dpi.Enabled)
            // обход всегда забирает прокси себе: когда поднято и ядро — это цепочка
            // «браузер → DPI → ядро», а не «кто включён последним» (см. StartProxyAsync)
            _ = StartDpiAsync();

        _ = Updates.LoadReleasesAsync();
    }

    private ProxyProfile? ActiveProfileFromSettings()
    {
        var id = App.Settings.Current.ActiveProfileId;
        return Profiles.Items.FirstOrDefault(p => p.Id == id)?.Profile
               ?? Profiles.Items.FirstOrDefault()?.Profile;
    }

    // ------------------------------------------------------------------- прокси
    private async Task ToggleProxyAsync()
    {
        if (IsProxyOn) await StopProxyAsync();
        else await StartProxyAsync();
    }

    /// <summary>
    /// Наборы правил/приложений сохранены: если прокси активен, переподключаем ядро,
    /// чтобы маршруты вступили в силу сразу. Раньше изменения ждали ручного
    /// переподключения — из-за этого правило «DIRECT» для приложения не работало,
    /// пока пользователь не передёрнул подключение.
    /// </summary>
    private async Task ReapplyRoutingAsync()
    {
        if (!IsProxyOn || _activeProfile is null || Busy) return;

        // дребезг: переподключение одно на серию правок (тоглы, удаления и т.п.)
        _routingReapplyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _routingReapplyCts = cts;
        try
        {
            await Task.Delay(1200, cts.Token);
            if (cts.IsCancellationRequested) return;

            ShowStatus(L.T("S0695"), false);
            await StopProxyAsync();
            await StartProxyAsync();
        }
        catch (OperationCanceledException) { /* пришла новая правка — переподключимся по ней */ }
    }

    private CancellationTokenSource? _routingReapplyCts;

    /// <summary>
    /// В разделе «DNS» поменялся основной DNS или DNS домена/приложения (выбор уже сохранён). Если прокси
    /// поднят — переподключаем ядро, чтобы запросы сразу пошли через новый DNS; иначе он применится
    /// при следующем подключении. <paramref name="what"/> — что поменялось, для строки состояния.
    /// </summary>
    private async Task ReapplyDnsAsync(string what)
    {
        if (!IsProxyOn || _activeProfile is null)
        {
            ShowStatus(L.F("S0696", what), false);
            return;
        }

        // дребезг: несколько выборов подряд — одно переподключение, по последнему
        _dnsReapplyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dnsReapplyCts = cts;
        try
        {
            await Task.Delay(350, cts.Token);

            // идёт подключение или отключение — дожидаемся его, иначе новый DNS не применится
            for (var waited = 0; Busy && waited < 30_000; waited += 200)
                await Task.Delay(200, cts.Token);
            if (!IsProxyOn || _activeProfile is null || Busy) return;

            ShowStatus(L.F("S0697", what), false);
            await StopProxyAsync();
            await StartProxyAsync();
        }
        catch (OperationCanceledException) { /* выбрали другой DNS — переподключимся по нему */ }
    }

    private CancellationTokenSource? _dnsReapplyCts;

    private async Task ConnectToAsync(ProxyProfile profile)
    {
        _activeProfile = profile;
        ActivePing = profile.LastPingMs;
        Profiles.SetActive(profile.Id);
        Raise(nameof(ActiveProfileName));
        Raise(nameof(ActiveProfileSubtitle));
        Raise(nameof(IsTelegramProfile));
        TelegramProxyCommand.RaiseCanExecuteChanged();
        App.Settings.Current.ActiveProfileId = profile.Id;
        App.Settings.SaveSoon();

        if (IsProxyOn)
        {
            await StopProxyAsync();
            await StartProxyAsync();
        }
        else
        {
            await StartProxyAsync();
        }
    }

    private async Task StartProxyAsync(bool autoRestore = false)
    {
        if (Busy) return;
        if (_activeProfile is null)
        {
            ShowStatus(L.T("S0698"), true);
            return;
        }

        if (_activeProfile.IsTelegramOnly)
        {
            ShowStatus(L.T("S0699"), true);
            return;
        }

        var route = Routing.ActiveProfile;

        if (!Core.Info.Exists)
        {
            ShowStatus(L.T("S0693"), true);
            ActiveTab = "settings";
            return;
        }

        if (route.TunEnabled && !AdminHelper.IsElevated)
        {
            // Автовосстановление при старте не дёргает пользователя модальным диалогом:
            // сообщаем в статус, а права запрашиваем только при ручном подключении.
            if (autoRestore)
            {
                ShowStatus(L.T("S0700"), true);
                return;
            }
            var agree = _dialogs.ConfirmElevate(L.T("S0701"),
                L.T("S0702") +
                L.T("S0703"));
            if (agree)
            {
                App.Settings.Current.ProxyWasActive = true;
                App.Settings.SaveNow();
                if (!AdminHelper.RestartElevated())
                    ShowStatus(L.T("S0704"), true);
                else
                    ExitRequested?.Invoke();
            }
            return;
        }

        Busy = true;
        try
        {
            // AmneziaWG требует расширенной сборки ядра: проверяем и при необходимости ставим её
            // до сборки конфига, иначе параметры обфускации молча потеряются.
            if (!await EnsureCoreForProfileAsync(_activeProfile)) return;

            Routing.Persist();

            var clashPort = GetClashPort();
            // DNS выбирается в разделе «DNS»: сборщик получает копию профиля входа с ним,
            // а также домены и приложения со своим DNS
            var build = SingBoxConfigBuilder.Build(_activeProfile, Dns.ApplyTo(route), Routing.ActiveSet,
                App.Settings.Current, Core.Info, clashPort, Path.Combine(AppPaths.LogsDir, "core.log"),
                Dns.AppRoutes(), Dns.DomainRoutes());

            foreach (var warning in build.Warnings) AppendLog(L.T("S0705") + warning);

            if (build.Errors.Count > 0)
            {
                foreach (var problem in build.Errors) AppendLog(L.T("S0706") + problem);
                ShowStatus(build.Errors[0], true);
                _dialogs.Error(L.T("S0707"), string.Join("\n\n", build.Errors));
                return;
            }

            var check = await Core.ValidateAsync(build.Json);
            if (!check.Ok)
            {
                ShowStatus(L.T("S0708"), true);
                _dialogs.ShowCoreLog(check.Message);
                return;
            }

            // Системный прокси забирает себе ядро; если включён обход DPI — ниже вернём
            // владение ему (цепочка «браузер → DPI → ядро», см. DpiExitMode.Proxy).
            // При включённом TUN системный прокси не ставим: туннель и так захватывает весь трафик,
            // а прокси на 127.0.0.1 ломает UWP/WebView2-приложения (Яндекс Музыка и т.п.) —
            // для них loopback заблокирован изоляцией AppContainer, приложение видит «нет сети».
            _dpiOwnsSystemProxy = false;
            if (route.UseSystemProxy && !route.TunEnabled) SystemProxy.CaptureSnapshot();

            ShowStatus(L.T("S0709"), false);
            await Core.StartAsync(build.Json, clashPort);

            if (Core.State != ConnectionState.Running)
            {
                ShowStatus(L.T("S0710"), true);
                _dialogs.ShowCoreLog(Core.RecentLogText());
                return;
            }

            // IsProxyOn выставляем до настройки системного прокси: ApplySystemProxyAsync смотрит на него
            IsProxyOn = true;
            _proxyStartedAt = DateTime.Now;
            Core.ConnectedProfileId = _activeProfile.Id;
            Core.TunActive = route.TunEnabled;
            Core.LocalListenAddress = route.ListenAddress;
            Core.LocalListenPort = route.ListenPort;
            if (DpiEnabled)
            {
                // цепочка «браузер → DPI (свой список) → ядро (остальное)»: DPI владеет системным
                // прокси и узнаёт про поднявшееся ядро. Иначе ядро перехватило бы прокси, и DPI-домены
                // ушли бы в DIRECT без обхода — а порядок включения влиял бы на результат.
                _dpi.SetUpstream(BuildDpiUpstream());
                _dpiOwnsSystemProxy = App.Settings.Current.Dpi.UseSystemProxy;
            }
            if (route.UseSystemProxy || DpiEnabled) await ApplySystemProxyAsync();

            App.Settings.Current.ProxyWasActive = true;
            App.Settings.Current.ActiveProfileId = _activeProfile.Id;
            App.Settings.SaveSoon();

            _statsTimer.Start();
            UpdateStateText();
            UpdateTrayStatus();
            var statusNote = build.Warnings.Count > 0 ? build.Warnings[0] : null;
            if (statusNote is { Length: > 160 }) statusNote = statusNote[..160] + "…";
            ShowStatus(statusNote is null
                ? L.F("S0711", _activeProfile.Name)
                : L.F("S0712", _activeProfile.Name, statusNote),
                statusNote is not null);
            _ = VerifyTunnelAsync();

            // в режиме «через профиль» обход должен переключиться с прямого выхода на вход ядра
            _ = RefreshDpiUpstreamAsync();
        }
        catch (NotSupportedException ex)
        {
            ShowStatus(ex.Message, true);
        }
        catch (Exception ex)
        {
            ShowStatus(L.T("S0713") + ex.Message, true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// AmneziaWG работает только на расширенной сборке ядра. Если параметры обфускации отбросить,
    /// сервер не примет хендшейк: прокси покажет «Подключено», а интернет работать не будет.
    /// Поэтому ядро ищем на диске, распаковываем из приложения или — с согласия пользователя —
    /// скачиваем, и обязательно перепроверяем, что обфускация действительно поддерживается.
    /// Возвращает false, если запускать профиль нельзя.
    /// </summary>
    private async Task<bool> EnsureCoreForProfileAsync(ProxyProfile profile)
    {
        var needsAmnezia = profile.Node?.HasAwgParams == true ||
                           profile.Protocol.Equals(ProtocolIds.AmneziaWg, StringComparison.OrdinalIgnoreCase);
        if (!needsAmnezia) return true;

        // точный ответ даёт проверка самого ядра, а не строка версии
        await Core.ProbeAmneziaSupportAsync();

        // RandomTrailers/DisableCookies (AmneziaWG 3.1) понимает только форк с плоской схемой
        // (sing-box-lx) или эндпоинт type="awg" (форк Amnezia): расширенная сборка shtorm-7 такие
        // параметры молча отбрасывает, сервер с 3.1 шлёт ответ с хвостом, клиент его не принимает —
        // туннель поднимается и тут же умирает. Значит SupportsAmnezia для такого профиля мало.
        var needsAwg31 = profile.Node?.HasAwgExtendedOnlyParams == true;
        if (Core.Info.SupportsAmnezia && (!needsAwg31 || Core.Info.SupportsAwgTrailers)) return true;

        var reason = Core.Info.SupportsAmnezia
            ? L.T("S0714")
            : string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? L.T("S0715")
                : Core.Info.EndpointError!;

        // 1) расширенная сборка уже лежит в каталоге ядра
        var alreadyExtendedFile =
            string.Equals(Core.Info.Path, AppPaths.CoreExtendedBinary, StringComparison.OrdinalIgnoreCase);
        if (!alreadyExtendedFile ||
            (needsAwg31 && !Core.Info.SupportsAwgTrailers && !Core.Info.SupportsAwgEndpoint))
        {
            if (!alreadyExtendedFile && File.Exists(AppPaths.CoreExtendedBinary))
                ShowStatus(L.T("S0716"), false);

            // 2) если файла нет, но ядро вшито в приложение — распаковываем его
            if (!File.Exists(AppPaths.CoreExtendedBinary) && BundledCores.HasExtended)
            {
                ShowStatus(L.T("S0717"), false);
                try
                {
                    await BundledCores.EnsureExtractedAsync(CoreFlavor.SingBoxExtended);
                }
                catch (Exception ex)
                {
                    reason = L.T("S0718") + ex.Message;
                }
            }

            if (await TrySwitchToExtendedAsync(needsAwg31))
            {
                RememberCoreSwitch();
                return true;
            }

            reason = string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? L.T("S0719")
                : Core.Info.EndpointError;
        }

        // 3) скачиваем с GitHub
        var ask = _dialogs.Confirm(L.T("S0720"),
            L.T("S0721") +
            L.T("S0722") +
            L.T("S0723") + reason + ".\n\n" +
            L.F("S0724", CoreInstaller.AwgRepo),
            L.T("S0725"), danger: false);

        if (!ask)
        {
            ShowStatus(L.T("S0726"), true);
            return false;
        }

        ShowStatus(L.T("S0727"), false);
        try
        {
            IProgress<string> progress = new Progress<string>(text => ShowStatus(text, false));
            var info = await CoreInstaller.InstallAwgCoreAsync(Core, progress);
            if (info is { Exists: true } && await TrySwitchToExtendedAsync(needsAwg31))
            {
                RememberCoreSwitch();
                ShowStatus(L.T("S0728") + Core.Info.VersionNumber, false);
                return true;
            }

            reason = string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? L.T("S0729")
                : Core.Info.EndpointError;
        }
        catch (Exception ex)
        {
            reason = L.T("S0730") + ex.Message;
        }

        // needsAwg31 уже посчитан выше: RandomTrailers/DisableCookies из профиля
        _dialogs.Error(L.T("S0731"),
            reason + "\n\n" + CoreInstaller.ManualHint +
            L.F("S0732", CoreInstaller.AwgRepo) +
            L.F("S0733", CoreInstaller.ExtendedRepo) +
            L.T("S0734") +
            (needsAwg31
                ? L.T("S0735") +
                  L.F("S0736", CoreInstaller.AwgRepo) +
                  L.T("S0737") +
                  L.T("S0738")
                : ""));
        return false;
    }

    /// <summary>
    /// Переход на расширенную сборку ядра с проверкой, что обфускация в ней действительно есть.
    /// Если что-то пошло не так, возвращаем прежнее ядро — иначе приложение осталось бы вообще без ядра.
    /// </summary>
    /// <param name="requireAwg31">
    /// Профилю нужны RandomTrailers/DisableCookies: ядро, которое их не передаёт, не подходит,
    /// даже если блок "amnezia" оно разбирает.
    /// </param>
    private async Task<bool> TrySwitchToExtendedAsync(bool requireAwg31 = false)
    {
        if (!File.Exists(AppPaths.CoreExtendedBinary)) return false;

        var previous = Core.Info;
        await Core.DetectAsync(AppPaths.CoreExtendedBinary, CoreFlavor.SingBoxExtended);

        if (!Core.Info.Exists ||
            !string.Equals(Core.Info.Path, AppPaths.CoreExtendedBinary, StringComparison.OrdinalIgnoreCase))
        {
            await Core.DetectAsync(previous.Path, previous.Flavor());
            return false;
        }

        await Core.ProbeAmneziaSupportAsync(force: true);
        if (Core.Info.SupportsAmnezia && (!requireAwg31 || Core.Info.SupportsAwgTrailers)) return true;

        await Core.DetectAsync(previous.Path, previous.Flavor());
        return false;
    }

    private void RememberCoreSwitch()
    {
        App.Settings.Current.CoreFlavor = CoreFlavor.SingBoxExtended;
        App.Settings.Current.CorePath = Core.Info.Path;
        App.Settings.Current.CoreVersion = Core.Info.Version;
        App.Settings.SaveSoon();
        Preferences.RaiseCoreFlavor();
        Preferences.RefreshCoreInfo();
        ShowStatus(L.T("S0739"), false);
    }

    public async Task StopProxyAsync(bool keepStateFlag = false)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            // ядро было запущено — для страховки ниже (проверяем порт именно этого профиля)
            var routeOnStop = Routing.ActiveProfile;
            var coreWasRunning = IsProxyOn;

            // ВАЖНО: IsProxyOn сбрасываем ДО ApplySystemProxyAsync. Он решает, нужен ли системный прокси:
            // пока он true, ApplySystemProxyAsync снова включил бы прокси на 127.0.0.1:<порт ядра>,
            // а ядро через мгновение остановится. Итог — «залипший» прокси на мёртвый порт: браузеры,
            // Steam и прочие приложения, работающие через системный прокси, теряют интернет, хотя
            // ICMP (ping 8.8.8.8) и прямые подключения (RDP) продолжают работать.
            IsProxyOn = false;
            Core.ConnectedProfileId = null;
            Core.TunActive = false;   // дальнейшие замеры задержки — снова прямой TCP/ICMP

            // ядра больше нет — остальное обход отдаёт напрямую, а не в мёртвый порт
            if (DpiEnabled) _dpi.SetUpstream(BuildDpiUpstream());

            // сначала возвращаем прежний системный прокси: интернет работает, даже если ядро не ответит.
            // Если обход DPI включён, системный прокси тут же переходит к нему.
            _dpiOwnsSystemProxy = App.Settings.Current.Dpi.UseSystemProxy && DpiEnabled;
            await ApplySystemProxyAsync();

            // Ядро останавливается в фоне: системный прокси уже возвращён (интернет работает),
            // а мягкое завершение процесса (~2 с на снятие TUN/маршрутов) UI не держит.
            // Порядок относительно следующего запуска хранит _stopGate: старт дождётся конца остановки.
            _ = StopCoreInBackgroundAsync(coreWasRunning && !DpiEnabled ? routeOnStop.ListenPort : null);

            _proxyStartedAt = null;
            TrafficUploadText = "—";
            TrafficDownloadText = "";
            _connectionCount = 0;
            Raise(nameof(ConnectionCountText));
            Raise(nameof(UptimeText));
            MaybeStopStatsTimer();

            // ядра больше нет — обход в режиме «через профиль» переключается на прямой выход
            _ = RefreshDpiUpstreamAsync();

            if (!keepStateFlag)
            {
                App.Settings.Current.ProxyWasActive = false;
                App.Settings.SaveSoon();
            }

            UpdateStateText();
            UpdateTrayStatus();
            ShowStatus(L.T("S0740"), false);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Фоновая остановка ядра после отключения UI. Исключения гасятся внутри: метод вызывается
    /// без await, и необработанное исключение ушло бы в UnobservedTaskException.
    /// </summary>
    private async Task StopCoreInBackgroundAsync(int? staleProxyPort)
    {
        try
        {
            await Core.StopAsync();
        }
        catch (Exception ex)
        {
            AppendLog(L.T("S0327") + ex.Message);
        }

        // страховка: если системный прокси всё же указывает на порт нашего ядра (снимок не сохранился,
        // запись не откатилась, в TUN-режиме остался залипший прокси), отключаем его — ядро к этому
        // моменту уже остановлено, такой прокси не работает. Проверяется только порт этого профиля.
        if (staleProxyPort is { } port)
        {
            try
            {
                await Task.Run(() => SystemProxy.CleanupStale(new[] { port }));
            }
            catch (Exception ex)
            {
                AppendLog(L.T("S0327") + ex.Message);
            }
        }
    }

    /// <summary>
    /// Вернуть прежние настройки системного прокси. Выполняется в фоновом потоке, чтобы вызовы WinINet
    /// не задерживали окно.
    /// </summary>
    private Task RestoreSystemProxyAsync() => Task.Run(() =>
    {
        try
        {
            if (SystemProxy.IsEnabled) SystemProxy.RestoreSnapshot();
        }
        catch (Exception ex)
        {
            AppendLog(L.T("S0741") + ex.Message);
        }
    });

    /// <summary>
    /// Системный прокси Windows один, а режимов два: прокси-ядро и обход DPI.
    /// Отдаём его тому, кто включён последним (<see cref="_dpiOwnsSystemProxy"/>);
    /// если не включён никто — возвращаем прежние настройки.
    /// </summary>
    private Task ApplySystemProxyAsync(bool captureSnapshot = false)
    {
        var dpiPort = _dpi.Port;
        var useDpi = DpiEnabled && _dpiOwnsSystemProxy &&
                     App.Settings.Current.Dpi.UseSystemProxy && dpiPort > 0;

        var route = Routing.ActiveProfile;
        // TUN сам захватывает весь трафик — системный прокси не нужен и вреден
        // (ломает UWP/WebView2-приложения из-за loopback-изоляции AppContainer).
        var useProxy = !useDpi && IsProxyOn && route.UseSystemProxy && !route.TunEnabled;

        var host = useDpi ? "127.0.0.1" : route.ListenAddress.Trim();
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]" or "*") host = "127.0.0.1";
        var port = useDpi ? dpiPort : route.ListenPort;

        return Task.Run(() =>
        {
            try
            {
                if (captureSnapshot) SystemProxy.CaptureSnapshot();

                if (useDpi || useProxy) SystemProxy.Enable(host, port);
                else if (SystemProxy.IsEnabled) SystemProxy.RestoreSnapshot();
            }
            catch (Exception ex)
            {
                AppendLog(L.T("S0742") + ex.Message);
            }
        });
    }

    /// <summary>Таймер метрик нужен, пока работает хотя бы один из режимов.</summary>
    private void MaybeStopStatsTimer()
    {
        if (!IsProxyOn && !DpiEnabled) _statsTimer.Stop();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Порт Clash API ядра: фиксированный, чтобы повторные подключения получали тот же конфиг
    /// и пропускали проверку sing-box check (см. кэш в ValidateAsync). Если порт занят —
    /// случайный свободный, как раньше.
    /// </summary>
    private static int GetClashPort()
    {
        const int preferred = 16787;
        return IsPortFree(preferred) ? preferred : FindFreePort();
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ метрики
    private async Task UpdateStatsAsync()
    {
        if (!IsProxyOn && !DpiEnabled)
        {
            _statsTimer.Stop();
            return;
        }

        if (IsProxyOn)
        {
            var traffic = await _api.GetTrafficAsync(Core.ClashApiPort);
            if (traffic is not null)
            {
                TrafficUploadText = $"↑ {Text.Bytes(traffic.Upload)}";
                TrafficDownloadText = $"↓ {Text.Bytes(traffic.Download)}";
            }

            _connectionCount = await _api.GetActiveConnectionsAsync(Core.ClashApiPort);
            Raise(nameof(ConnectionCountText));
            Raise(nameof(UptimeText));
            // пинг активного профиля обновляется тем же таймером: тихий замер без записи на диск
            _ = RefreshActivePingQuietAsync();
        }

        if (DpiEnabled) RaiseDpiStats();
    }

    /// <summary>
    /// «Подключено» ещё не значит, что интернет работает: туннель поднимается даже тогда,
    /// когда сервер отвергает хендшейк (типично для AmneziaWG без параметров обфускации).
    /// Проверяем реальный запрос через ядро и, если он не прошёл, честно сообщаем об этом.
    /// </summary>
    private async Task VerifyTunnelAsync()
    {
        try
        {
            await Task.Delay(800);
            if (!IsProxyOn || Core.State != ConnectionState.Running) return;

            // первый хендшейк туннеля может занять секунды — таймаут проверки берём с запасом
            if (await Core.MeasureDelayAsync(timeoutMs: 12000) > 0 || await TryHttpThroughInboundAsync())
            {
                await RefreshActivePingAsync();
                return;
            }

            await RefreshActivePingAsync();

            ReportDeadTunnel();
        }
        catch
        {
            /* диагностика не должна ломать подключение */
        }
    }

    /// <summary>Запрос в интернет через локальный вход прокси (для SOCKS-режима недоступен).</summary>
    private async Task<bool> TryHttpThroughInboundAsync()
    {
        var route = Routing.ActiveProfile;
        if (route.Mode == ListenMode.Socks) return false;   // WebProxy в .NET не умеет SOCKS

        var host = route.ListenAddress.Trim();
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]" or "*") host = "127.0.0.1";

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://{host}:{route.ListenPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        foreach (var url in ConnectivityProbes)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if ((int)response.StatusCode is >= 200 and < 400) return true;
            }
            catch
            {
                /* пробуем следующий адрес */
            }
        }

        return false;
    }

    private static readonly string[] ConnectivityProbes =
    {
        "http://captive.apple.com/hotspot-detect.html",
        "https://cloudflare.com/cdn-cgi/trace",
        "https://www.gstatic.com/generate_204"
    };

    private void ReportDeadTunnel()
    {
        var node = _activeProfile?.Node;
        var awg = node?.HasAwgParams == true ||
                  (_activeProfile is not null &&
                   _activeProfile.Protocol.Equals(ProtocolIds.AmneziaWg, StringComparison.OrdinalIgnoreCase));

        var message = awg
            ? L.T("S0743") +
              L.T("S0744")
            : L.T("S0745");

        // Самая частая конкретная причина у AWG 3.1: сервер дописывает к хендшейку случайный
        // трейлер, а ядро клиента флаг random_trailers передать не может — ответы отбрасываются.
        if (node?.AwgRandomTrailers == true && !Core.Info.SupportsAwgTrailers)
            message = L.T("S0746") +
                      L.T("S0747") +
                      L.T("S0748") +
                      L.F("S0749", CoreInstaller.AwgRepo) +
                      L.T("S0750");

        AppendLog(message);
        AppendLog(L.F("S0751", Core.Info.Version, (Core.Info.SupportsAmnezia ? L.T("Extra952") : L.T("Extra953"))) +
                  L.F("S0752", (Core.Info.SupportsAwgTrailers ? L.T("Extra952") : L.T("Extra953"))) +
                  L.F("S0753", (Core.Info.AmneziaProbed?.ToString() ?? L.T("Extra954"))));
        AppendLog(Core.RecentLogText());
        ShowStatus(message, true);
    }

    private Task RefreshActivePingAsync()
    {
        // Один замер, одно сохранение и одно событие для обоих мест отображения.
        var profile = _activeProfile;
        return Profiles.PingAsync(profile is null ? null : Profiles.Find(profile.Id));
    }

    /// <summary>Тихий замер пинга по таймеру статистики. Ошибки гасятся: диагностика не должна шуметь.</summary>
    private async Task RefreshActivePingQuietAsync()
    {
        try
        {
            var profile = _activeProfile;
            await Profiles.PingAsync(profile is null ? null : Profiles.Find(profile.Id), quiet: true);
        }
        catch
        {
            /* тихий замер не должен ломать таймер статистики */
        }
    }

    // ============================================================== обход DPI
    /// <summary>Снимок настроек для движка: движок живёт своей копией, пока поднят.</summary>
    private DpiBypassSettings BuildDpiOptions() => App.Settings.Current.Dpi.Clone();

    /// <summary>Вход ядра, если выбран режим «через профиль»; null — идём напрямую.</summary>
    private DpiUpstream? BuildDpiUpstream()
    {
        if (App.Settings.Current.Dpi.ExitMode != DpiExitMode.Proxy) return null;
        if (!IsProxyOn) return null;   // ядро не поднято — работаем напрямую

        var route = Routing.ActiveProfile;
        var host = route.ListenAddress.Trim();
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]" or "*") host = "127.0.0.1";

        return new DpiUpstream
        {
            Host = host,
            Port = route.ListenPort,
            Socks = route.Mode == ListenMode.Socks
        };
    }

    /// <summary>
    /// «Выбрать DNS» в окне сервиса или «Общих»: переносим пользователя в раздел «DNS» — там он
    /// выбирает сервер так же, как для домена и приложения. После выбора раздел вернётся в «DPI»
    /// (нажимаем именно на сохранённый выбор: правки окна к этому моменту уже в настройках).
    /// </summary>
    private Task PickDnsForServiceAsync(DpiService service)
    {
        var section = App.Settings.Current.DpiServices;
        var isGeneral = service.Id == DpiServices.GeneralId;

        var choice = isGeneral
            ? section.GeneralDns
            : section.Services.FirstOrDefault(s => s.Id == service.Id)?.Dns;

        if (choice is null) return Task.CompletedTask;

        choice.Id = isGeneral ? DpiServices.GeneralId : service.Id;

        var name = isGeneral
            ? L.T("dpi.general")
            : service.Name.Length > 0 ? service.Name : L.T("dpi.noName");

        // ActiveTab выставляет вкладку и вызывает OnOpened раздела; выбор начинаем после этого,
        // иначе «DNS» отменил бы его как начатый до входа в раздел
        ActiveTab = "dns";
        Dns.StartPickForDpi(choice, name, () =>
        {
            ActiveTab = "dpi";
            Dpi.OnDnsPicked(service.Id);
        });

        return Task.CompletedTask;
    }

    private async Task ToggleDpiAsync()
    {
        if (DpiEnabled) await StopDpiAsync();
        else await StartDpiAsync();
    }

    /// <summary>
    /// Обход всегда забирает системный прокси себе: когда поднято и ядро, получается
    /// цепочка «браузер → DPI (свой список) → ядро (остальное)», и порядок включения
    /// ни на что не влияет. Ядро при старте тоже возвращает владение обходу.
    /// </summary>
    private async Task StartDpiAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            DpiState = DpiBypassState.Starting;
            RaiseDpiStats();

            await _dpi.StartAsync(BuildDpiOptions(), BuildDpiUpstream());

            DpiEnabled = true;
            // Никаких проверок при включении и во время работы: обход сразу пропускает трафик, а
            // проверяют его кнопки «Тест» и «Подобрать» в окне сервиса, когда это нужно пользователю.
            DpiState = DpiBypassState.On;
            _dpiStartedAt = DateTime.Now;
            _dpiOwnsSystemProxy = App.Settings.Current.Dpi.UseSystemProxy;
            App.Settings.Current.Dpi.Enabled = true;
            App.Settings.SaveSoon();

            await ApplySystemProxyAsync(captureSnapshot: true);

            RaiseDpiStats();
            UpdateTrayStatus();
            _statsTimer.Start();
            ShowStatus(L.F("S0757", DpiPortText, DpiStrategiesText), false);
        }
        catch (Exception ex)
        {
            DpiEnabled = false;
            _dpiStartedAt = null;
            DpiState = DpiBypassState.Error;
            ShowStatus(L.T("S0758") + ex.Message, true);
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task StopDpiAsync()
    {
        if (!DpiEnabled && !_dpi.IsRunning) return;
        Busy = true;
        try
        {
            await _dpi.StopAsync();

            var wasOwner = _dpiOwnsSystemProxy;
            DpiEnabled = false;
            _dpiStartedAt = null;
            _dpiOwnsSystemProxy = false;
            DpiState = DpiBypassState.Off;
            App.Settings.Current.Dpi.Enabled = false;
            App.Settings.SaveSoon();

            if (wasOwner) await ApplySystemProxyAsync();

            RaiseDpiStats();
            UpdateTrayStatus();
            MaybeStopStatsTimer();
            ShowStatus(L.T("S0759"), false);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RestartDpiEngineAsync(DpiBypassSettings options)
    {
        await _dpi.StopAsync();
        await _dpi.StartAsync(options, BuildDpiUpstream());
    }

    /// <summary>
    /// Обход в режиме «через профиль» зависит от того, поднято ли ядро: перезапускаем движок,
    /// чтобы он переподключился к нужному выходу.
    /// </summary>
    private async Task RefreshDpiUpstreamAsync()
    {
        if (!DpiEnabled) return;
        try
        {
            await RestartDpiEngineAsync(BuildDpiOptions());
        }
        catch (Exception ex)
        {
            AppendLog(L.T("S0775") + ex.Message);
        }
    }

    /// <summary>Настройки стратегий поменяли — перезапускаем движок, если он поднят.</summary>
    private async Task RestartDpiIfRunningAsync()
    {
        RaiseDpiStats();
        if (!DpiEnabled) return;

        try
        {
            await RestartDpiEngineAsync(BuildDpiOptions());
            RaiseDpiStats();
            ShowStatus(L.T("S0776"), false);
        }
        catch (Exception ex)
        {
            DpiState = DpiBypassState.Error;
            ShowStatus(L.T("S0777") + ex.Message, true);
        }
    }

    private void RaiseDpiStats()
    {
        Raise(nameof(DpiTrafficText));
        Raise(nameof(DpiUptimeText));
        Raise(nameof(DpiSessionsText));
        Raise(nameof(DpiPortText));
        Raise(nameof(DpiStrategiesText));
    }

    // ------------------------------------------------------------------- прочее
    private async Task InstallCoreAsync()
    {
        ActiveTab = "settings";
        await Preferences.InstallCorePublicAsync();
    }

    private void RestartElevated()
    {
        if (AdminHelper.IsElevated)
        {
            ShowStatus(L.T("S0778"), false);
            return;
        }

        var running = IsProxyOn;
        if (running) App.Settings.Current.ProxyWasActive = true;
        App.Settings.Current.RememberState = true;
        App.Settings.SaveNow();

        if (AdminHelper.RestartElevated()) ExitRequested?.Invoke();
        else ShowStatus(L.T("S0779"), true);
    }

    private void OpenTelegramProxy()
    {
        if (_activeProfile?.Node is not { } node) return;

        var link = _activeProfile.Protocol == ProtocolIds.MtProto && !string.IsNullOrWhiteSpace(node.MtSecret)
            ? $"tg://proxy?server={node.Server}&port={node.Port}&secret={node.MtSecret}"
            : $"tg://socks?server={node.Server}&port={node.Port}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
            ShowStatus(L.T("S0780"), false);
        }
        catch
        {
            try
            {
                System.Windows.Clipboard.SetText(link);
                ShowStatus(L.T("S0781"), false);
            }
            catch { /* ignore */ }
        }
    }

    private void CopyDiagnostics()
    {
        var text =
            $"VibeTun{Environment.NewLine}" +
            L.F("S0782", Core.Info.VersionShort, Environment.NewLine, Core.Info.Path, Environment.NewLine) +
            L.F("S0783", ActiveProfileName, Environment.NewLine, StateText, Environment.NewLine) +
            $"TUN: {Routing.ActiveProfile.TunEnabled}{Environment.NewLine}" +
            $"DNS: {Dns.SelectionText}{Dns.RulesSummaryText}{Environment.NewLine}" +
            Dpi.DiagnosticsText() +
            L.F("S0784", AdminHelper.IsElevated, Environment.NewLine) +
            new string('-', 40) + Environment.NewLine + Core.RecentLogText();
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowStatus(L.T("S0785"), false);
        }
        catch { /* ignore */ }
    }

    public void ShowStatus(string text, bool error)
    {
        AppendLog(text);   // каждое уведомление дублируем в журнал
        StatusMessage = text;
        StatusIsError = error;
        Raise(nameof(HasStatus));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 10 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (StatusMessage == text)
            {
                StatusMessage = "";
                Raise(nameof(HasStatus));
            }
        };
        timer.Start();
    }

    private void UpdateStateText()
    {
        (StateText, StatusBrushKey) = Core.State switch
        {
            ConnectionState.Running => (L.T("S0786"), "Success"),
            ConnectionState.Starting => (L.T("S0680"), "Warning"),
            ConnectionState.Stopping => (L.T("S0787"), "Warning"),
            ConnectionState.Error => (L.T("S0788"), "Danger"),
            _ => (L.T("S0691"), "TextMuted")
        };
        Raise(nameof(StateText));
        Raise(nameof(StatusBrushKey));
        UpdateTrayStatus();
    }

    private void UpdateTrayStatus()
    {
        var profile = _activeProfile?.Name ?? L.T("S0789");

        // обход DPI и прокси могут работать одновременно — в подписи трея говорим про оба
        var mode = Core.State == ConnectionState.Running
            ? DpiEnabled ? L.T("S0790") : null
            : DpiEnabled ? L.T("S0791") : null;

        Tray.SetStatus($"VibeTun — {profile} · {StateText}" + (mode is null ? "" : $" · {mode}"));

        // значок: цветной, пока работает ядро или обход; серый — когда не работает ничего
        Tray.SetProxyOn(Core.State == ConnectionState.Running || DpiEnabled);
    }

    internal override void RefreshLanguage()
    {
        UpdateStateText();
        base.RefreshLanguage();
    }

    private readonly object _logFileGate = new();

    /// <summary>Предел файла журнала: столько на диске держать бессмысленно, а место он ест.</summary>
    private const long LogFileLimitBytes = 4 * 1024 * 1024;

    private void AppendLog(string line)
    {
        Logs.Add(LogFormat.Shorten(line));   // only the on-screen copy is shortened/limited
        try
        {
            var path = Path.Combine(AppPaths.LogsDir, "app.log");
            lock (_logFileGate)
            {
                // журнал на диске ограничен: без этого фон из повторов забивал место и тормозил записью
                if (File.Exists(path) && new FileInfo(path).Length > LogFileLimitBytes) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Вызывается при завершении сеанса Windows (выключение/выход из системы).</summary>
    public void PrepareForSystemShutdown()
    {
        try
        {
            if (SystemProxy.IsEnabled) SystemProxy.RestoreSnapshot();
        }
        catch { /* ignore */ }

        try
        {
            App.Settings.SaveNow();
        }
        catch { /* ignore */ }
    }

    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();

        try
        {
            await _dpi.StopAsync();
        }
        catch (Exception ex)
        {
            AppendLog(L.T("S0792") + ex.Message);
        }

        // сначала возвращаем системный прокси: даже если ядро не ответит, интернет у пользователя останется
        await RestoreSystemProxyAsync();

        try
        {
            // StopAsync сам ограничен по времени; общий предел здесь — страховка от непредвиденного зависания
            await Core.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (Exception ex)
        {
            AppendLog(L.T("S0793") + ex.Message);
        }

        var settings = App.Settings.Current;
        if (settings.RememberState)
        {
            settings.ProxyWasActive = IsProxyOn;
            settings.ActiveProfileId = _activeProfile?.Id;
            settings.ActiveRouteProfileId = Routing.SelectedProfile?.Id;
        }
        App.Settings.SaveNow();
        Routing.Persist();

        Tray.Dispose();
    }
}
