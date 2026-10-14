using System.Collections.ObjectModel;
using System.Diagnostics;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>
/// Раздел «DNS»: через какой DNS ядро узнаёт адреса сайтов. Три вкладки (<see cref="Tab"/>):
/// <list type="bullet">
/// <item>«Основные» — DNS для всех: наверху всегда «Системный» и «Через Proxy», ниже — провайдеры
/// (серверы из справочника приложения, у GeoHide — DNS-запросом к его именам) и свои DNS. Выбор
/// сохраняется в настройках, активное подключение перезапускается (см. <see cref="DnsChanged"/>).
/// Кнопка пинга меряет все серверы списка (<see cref="PingAllCommand"/>), внизу — DNS обхода DPI
/// (<see cref="DpiDoh"/>).</item>
/// <item>«Домены» (<see cref="Domains"/>) и «Приложения» (<see cref="Apps"/>) — свой DNS для отдельных
/// сайтов и программ. «Выбрать DNS» у записи открывает «Основные»: нажатие на DNS там достаётся записи
/// (<see cref="Choose"/>), и раздел возвращается во вкладку, откуда пришли.</item>
/// </list>
/// </summary>
public sealed class DnsViewModel : ObservableObject
{
    public const string TabMain = "main";
    public const string TabDomains = "domains";
    public const string TabApps = "apps";

    /// <summary>Список серверов провайдера старше этого перезапрашиваем при раскрытии.</summary>
    private static readonly TimeSpan ServersMaxAge = TimeSpan.FromMinutes(30);

    /// <summary>Сколько после выбора DNS подсвечена карточка записи во вкладке, куда вернулись.</summary>
    private static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(2);

    /// <summary>Сколько серверов пингуем одновременно.</summary>
    private const int PingParallelism = 24;

    private readonly IDialogService _dialogs;
    private readonly Func<bool> _tunActive;

    /// <summary>Замеры пинга за сессию — общие для всех строк (один адрес может быть в двух местах).</summary>
    private readonly DnsPingBoard _pings = new();

    /// <param name="tunActive">Подключение идёт через TUN: DNS-запросы перехватывает ядро, пинг был бы неверным.</param>
    public DnsViewModel(IDialogService dialogs, Func<bool>? tunActive = null)
    {
        _dialogs = dialogs;
        _tunActive = tunActive ?? (() => false);

        Pinned.Add(new DnsPinnedVm(DnsModes.System, "Системный",
            "Резолвер Windows — обычно DNS провайдера",
            "Адреса сайтов узнаёт Windows — обычно через DNS провайдера. В TUN-режиме запросы идут на 1.1.1.1 напрямую. " +
            "Для WireGuard/AmneziaWG берётся DNS из конфига профиля (например 8.8.8.8): запросы идут внутри туннеля, как в клиенте Amnezia."));
        Pinned.Add(new DnsPinnedVm(DnsModes.Proxy, "Через Proxy",
            "DNS-over-TLS 1.1.1.1 через активный профиль",
            "Запросы уходят через подключённый профиль по DNS-over-TLS (1.1.1.1): провайдер их не видит и не может подменить ответ."));

        SelectPinnedCommand = new RelayCommand(p => SelectPinned(p as DnsPinnedVm));
        SelectServerCommand = new RelayCommand(p => SelectServer(p as DnsServerVm));
        ToggleExpandCommand = new RelayCommand(p => ToggleExpand(p as DnsEntryVm));
        RefreshCommand = new RelayCommand(p => _ = RefreshAsync(p as DnsEntryVm, force: true));
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as DnsEntryVm));
        AddCustomCommand = new RelayCommand(_ => AddCustom());
        EditCommand = new RelayCommand(p => Edit(p as DnsEntryVm));
        RemoveCommand = new RelayCommand(p => Remove(p as DnsEntryVm));
        OpenSiteCommand = new RelayCommand(p => OpenSite(p as DnsEntryVm));
        PingAllCommand = new AsyncRelayCommand(PingAllAsync);

        AddDomainCommand = new RelayCommand(_ => AddDomains(), _ => _newDomainParts.Count > 0 && _newDomainProblem is null);
        RemoveDomainCommand = new RelayCommand(p => RemoveDomain(p as DnsDomainVm));
        AddAppFromExeCommand = new RelayCommand(_ => AddAppFromExe());
        AddAppsFromProcessesCommand = new RelayCommand(_ => AddAppsFromProcesses());
        RemoveAppCommand = new RelayCommand(p => RemoveApp(p as DnsAppVm));
        PickDnsCommand = new RelayCommand(p => StartPick(p as DnsTargetVm));
        CancelPickCommand = new RelayCommand(_ => CancelPick());
    }

    /// <summary>«Системный» и «Через Proxy» — отдельным списком над остальными, без перетаскивания.</summary>
    public ObservableCollection<DnsPinnedVm> Pinned { get; } = new();

    /// <summary>Провайдеры и свои DNS в порядке пользователя.</summary>
    public ObservableCollection<DnsEntryVm> Entries { get; } = new();

    public RelayCommand SelectPinnedCommand { get; }
    public RelayCommand SelectServerCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand AddCustomCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenSiteCommand { get; }

    /// <summary>Пинг всех серверов списка; пока идёт замер, кнопка недоступна.</summary>
    public AsyncRelayCommand PingAllCommand { get; }

    // ------------------------------------------------------------------ вкладки
    private string _tab = TabMain;

    /// <summary>Открытая вкладка: «main» — основные, «domains» — домены, «apps» — приложения.</summary>
    public string Tab
    {
        get => _tab;
        set
        {
            if (value is not (TabMain or TabDomains or TabApps) || value == _tab) return;
            // перешли в другую вкладку, пока выбирали DNS для записи, — выбор отменяется
            EndPick(restoreTab: false);
            SetTab(value);
        }
    }

    /// <summary>Привязки переключателя: выбрать можно только вкладку, снять выбор нельзя.</summary>
    public bool IsMainTab
    {
        get => _tab == TabMain;
        set
        {
            if (value) Tab = TabMain;
            else Raise();
        }
    }

    public bool IsDomainsTab
    {
        get => _tab == TabDomains;
        set
        {
            if (value) Tab = TabDomains;
            else Raise();
        }
    }

    public bool IsAppsTab
    {
        get => _tab == TabApps;
        set
        {
            if (value) Tab = TabApps;
            else Raise();
        }
    }

    private void SetTab(string value)
    {
        if (_tab == value) return;
        _tab = value;
        Raise(nameof(Tab));
        Raise(nameof(IsMainTab));
        Raise(nameof(IsDomainsTab));
        Raise(nameof(IsAppsTab));
    }

    // ---------------------------------------------------------- домены: свойства
    private string _newDomainPattern = "";
    private List<string> _newDomainParts = new();
    private string? _newDomainProblem;
    private string _newDomainKindLabel = "";

    /// <summary>Домены со своим DNS — вкладка «Домены»; сверху — добавленные последними.</summary>
    public ObservableCollection<DnsDomainVm> Domains { get; } = new();

    public bool HasDomains => Domains.Count > 0;

    /// <summary>Поле ввода новой записи: домен или regex, можно несколько через запятую.</summary>
    public string NewDomainPattern
    {
        get => _newDomainPattern;
        set
        {
            if (!SetProperty(ref _newDomainPattern, value ?? "")) return;
            CheckNewDomain();
        }
    }

    /// <summary>Тип введённой записи — рядом с полем, как в маршрутизации: «домен», «regex», «3 записи».</summary>
    public string NewDomainKindLabel
    {
        get => _newDomainKindLabel;
        private set => SetProperty(ref _newDomainKindLabel, value);
    }

    /// <summary>Что не так с введённым (IP, ошибка в regex); null — можно добавлять.</summary>
    public string? NewDomainProblem
    {
        get => _newDomainProblem;
        private set
        {
            if (SetProperty(ref _newDomainProblem, value)) Raise(nameof(HasNewDomainProblem));
        }
    }

    public bool HasNewDomainProblem => _newDomainProblem is not null;

    public RelayCommand AddDomainCommand { get; }
    public RelayCommand RemoveDomainCommand { get; }

    // ------------------------------------------------------ приложения: свойства
    /// <summary>Приложения со своим DNS — вкладка «Приложения».</summary>
    public ObservableCollection<DnsAppVm> Apps { get; } = new();

    public bool HasApps => Apps.Count > 0;

    public RelayCommand AddAppFromExeCommand { get; }
    public RelayCommand AddAppsFromProcessesCommand { get; }
    public RelayCommand RemoveAppCommand { get; }

    // ------------------------------------------------- выбор для записи: свойства
    /// <summary>
    /// «Выбрать DNS» / «Изменить» у домена или приложения: открываются «Основные», нажатие на DNS там
    /// достаётся записи, и раздел возвращается во вкладку записи.
    /// </summary>
    public RelayCommand PickDnsCommand { get; }

    /// <summary>Передумали выбирать (кнопка «Отмена» или Esc) — назад во вкладку записи.</summary>
    public RelayCommand CancelPickCommand { get; }

    /// <summary>Запись, для которой сейчас выбирают DNS в основном списке (null — выбирают основной DNS).</summary>
    public DnsTargetVm? PickingTarget => _picking;

    public bool IsPicking => _picking is not null;

    /// <summary>Заголовок плашки над основным списком, пока выбирают DNS для записи.</summary>
    public string PickingTitle => _picking is null ? "" : $"Выберите DNS для «{_picking.Name}»";

    /// <summary>Вторая строка плашки: что нажимать и куда потом вернёмся.</summary>
    public string PickingHint => _picking is null
        ? ""
        : $"Нажмите на «Системный», «Через Proxy», сервер провайдера или свой DNS — после выбора вернёмся во вкладку «{_picking.TabTitle}»";

    /// <summary>Значок в плашке: «domain» — глобус, «icon» — значок приложения, «app» — общий значок окна.</summary>
    public string PickingKind => _picking switch
    {
        DnsDomainVm => "domain",
        DnsAppVm { Icon: not null } => "icon",
        DnsAppVm => "app",
        _ => ""
    };

    public System.Windows.Media.ImageSource? PickingIcon => (_picking as DnsAppVm)?.Icon;

    public event Action<string, bool>? Notify;

    /// <summary>
    /// DNS ядра поменялся (выбор уже сохранён): основной или DNS домена/приложения. Аргумент — что
    /// поменялось, для строки состояния («DNS: системный DNS», «DNS для «youtube.com»: Quad9 · 9.9.9.9»).
    /// Подписчик (MainViewModel) перезапускает активное подключение.
    /// </summary>
    public event Action<string>? DnsChanged;

    /// <summary>
    /// Поменяли DNS обхода DPI (уже сохранено). Подписчик (MainViewModel) перезапускает движок
    /// обхода, если тот включён, — как при правке стратегий в «Настройках».
    /// </summary>
    public event Action? DpiDnsChanged;

    /// <summary>Настройки читаем каждый раз заново: при старте settings.json перечитывается целиком.</summary>
    private static DnsSettings Current => App.Settings.Current.Dns;

    /// <summary>Выбор, который показан в списке (режим и адрес): по нему видно, поменял ли DNS сброс настроек.</summary>
    private string _shownSelection = "";

    /// <summary>Запись, для которой выбирают DNS (см. <see cref="PickingTarget"/>).</summary>
    private DnsTargetVm? _picking;

    /// <summary>Вкладка, откуда пришли выбирать DNS для записи, — туда и вернёмся.</summary>
    private string? _returnTab;

    /// <summary>Что выбрано сейчас — для заголовка раздела, строки состояния и диагностики.</summary>
    public string SelectionText
    {
        get
        {
            var s = Current;
            if (s.Mode == DnsModes.Proxy) return "через Proxy (DNS-over-TLS 1.1.1.1)";
            if (s.Mode != DnsModes.Server || string.IsNullOrWhiteSpace(s.SelectedServer)) return "системный DNS";
            return ServerText(s.SelectedEntryId, s.SelectedServer);
        }
    }

    /// <summary>« · свой DNS у 3 доменов и 1 приложения» — рядом с основным DNS («Сейчас:») и в диагностике.</summary>
    public string RulesSummaryText
    {
        get
        {
            var domains = Domains.Count(d => d.Enabled && d.HasDns && d.IsUsable);
            var apps = Apps.Count(a => a.Enabled && a.HasDns);
            var parts = new List<string>();
            if (domains > 0) parts.Add($"{domains} {(OneOf(domains) ? "домена" : "доменов")}");
            if (apps > 0) parts.Add($"{apps} {(OneOf(apps) ? "приложения" : "приложений")}");
            return parts.Count == 0 ? "" : " · свой DNS у " + string.Join(" и ", parts);
        }
    }

    /// <summary>«у 1 / 21 / 101 домена», но «у 11 / 5 доменов».</summary>
    private static bool OneOf(int count) => count % 10 == 1 && count % 100 != 11;

    /// <summary>«1 запись», «3 записи», «5 записей».</summary>
    private static string Plural(int count, string one, string few, string many)
    {
        if (OneOf(count)) return one;
        var tens = count % 100;
        return count % 10 is >= 2 and <= 4 && tens is < 12 or > 14 ? few : many;
    }

    /// <summary>
    /// «Yandex DNS · 77.88.8.88 (Безопасный)»: название записи списка, адрес и вариант сервера
    /// провайдера. У своего DNS с названием-адресом — просто адрес.
    /// </summary>
    private string ServerText(string? entryId, string? server)
    {
        var address = server?.Trim() ?? "";
        var entry = Entries.FirstOrDefault(e => e.Id == entryId);
        var name = entry?.Name;
        var text = string.IsNullOrWhiteSpace(name) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)
            ? address
            : name + " · " + address;

        var label = entry?.Servers
            .FirstOrDefault(x => string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase))?.Label;
        return string.IsNullOrEmpty(label) ? text : text + " (" + label + ")";
    }

    // ------------------------------------------------------------------ загрузка
    /// <summary>
    /// Загрузка после чтения настроек. <paramref name="legacyRoute"/> — профиль маршрутизации, где DNS
    /// жил раньше: при первом запуске новой версии выбор (режим и свой сервер) переносится оттуда.
    /// </summary>
    public void Load(RouteProfile? legacyRoute)
    {
        var s = Current;
        s.Entries ??= new List<DnsEntry>();   // "Entries": null в settings.json, поправленном вручную
        if (!s.Initialized)
        {
            MigrateFrom(legacyRoute, s);
            s.Initialized = true;
        }

        DnsProviders.Normalize(s.Entries);

        if (s.Mode is not (DnsModes.System or DnsModes.Proxy or DnsModes.Server)) s.Mode = DnsModes.System;
        if (s.Mode == DnsModes.Server && string.IsNullOrWhiteSpace(s.SelectedServer)) s.Mode = DnsModes.System;
        if (s.Mode != DnsModes.Server)
        {
            s.SelectedEntryId = null;
            s.SelectedServer = null;
        }

        Entries.Clear();
        foreach (var model in s.Entries) Entries.Add(new DnsEntryVm(model, _pings));
        RefreshSelection();

        // домены и приложения со своим DNS: "Domains"/"Apps": null или битые строки в settings.json,
        // поправленном вручную
        s.Domains ??= new List<DnsDomainRule>();
        s.Domains.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Pattern));
        foreach (var rule in s.Domains) NormalizeDomain(rule);

        s.Apps ??= new List<DnsAppRule>();
        s.Apps.RemoveAll(a => a is null || (string.IsNullOrWhiteSpace(a.Path) && string.IsNullOrWhiteSpace(a.ProcessName)));
        foreach (var app in s.Apps) NormalizeApp(app);

        CancelPick();
        foreach (var rule in Domains) Detach(rule);
        Domains.Clear();
        foreach (var model in s.Domains) Domains.Add(CreateDomainVm(model));

        foreach (var app in Apps) Detach(app);
        Apps.Clear();
        foreach (var model in s.Apps) Apps.Add(CreateAppVm(model));

        RefreshTargetTexts();
        Raise(nameof(HasDomains));
        Raise(nameof(HasApps));
        Raise(nameof(RulesSummaryText));

        RaiseDpiDns();
        App.Settings.SaveSoon();
    }

    /// <summary>
    /// «Сбросить настройки» заменил весь settings.json значениями по умолчанию — перечитываем свой блок,
    /// иначе раздел показывал бы прежний выбор и записывал бы правки в уже выброшенный объект.
    /// true — DNS ядра от этого поменялся, и активное подключение нужно перезапустить.
    /// </summary>
    public bool ReloadAfterReset()
    {
        // домены и приложения сравниваем по карточкам: в настройках уже лежат значения по умолчанию
        var before = _shownSelection + "#" + RulesSignature();
        Load(null);   // прежний DNS из профиля маршрутизации второй раз не переносим
        var after = _shownSelection + "#" + RulesSignature();
        return !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Свои DNS доменов и приложений, которые уходят ядру, — одной строкой для сравнения.</summary>
    private string RulesSignature() =>
        string.Join(";", Domains
            .Where(d => d.Enabled && d.HasDns && d.IsUsable)
            .Select(d => d.Key + "|" + d.Choice.Mode + "|" + d.Choice.Server)) +
        "#" +
        string.Join(";", Apps.Select(a => a.Model)
            .Where(a => a.Enabled && DnsTargetVm.Chosen(a))
            .Select(a => a.Path + "|" + a.ProcessName + "|" + a.Mode + "|" + a.Server));

    /// <summary>Раздел открыли: у раскрытых провайдеров, чьи серверы меняются, обновляем устаревший список.</summary>
    public void OnOpened()
    {
        CancelPick();   // выбор DNS для записи, начатый до ухода из раздела, не продолжаем
        foreach (var entry in Entries.Where(e => e.IsDynamic && e.IsExpanded).ToList())
            _ = RefreshAsync(entry, force: false);
    }

    private static void MigrateFrom(RouteProfile? route, DnsSettings s)
    {
        var mode = route?.DnsMode ?? DnsModes.System;
        var server = route?.DnsServer?.Trim() ?? "";

        if (mode == "proxy")
        {
            s.Mode = DnsModes.Proxy;
        }
        else if (mode == "custom" && server.Length > 0)
        {
            var entry = new DnsEntry { Name = "Свой DNS", Address = server };
            s.Entries.Add(entry);
            s.Mode = DnsModes.Server;
            s.SelectedEntryId = entry.Id;
            s.SelectedServer = server;
        }
        else
        {
            // «Системный» и «Из конфига профиля» ядро обрабатывало одинаково
            s.Mode = DnsModes.System;
        }
    }

    /// <summary>
    /// Копия профиля маршрутизации с DNS из этого раздела — её и получает сборщик конфига ядра.
    /// Сам профиль не меняется.
    /// </summary>
    public RouteProfile ApplyTo(RouteProfile route)
    {
        var s = Current;
        var copy = route.Clone();
        if (s.Mode == DnsModes.Proxy)
        {
            copy.DnsMode = "proxy";
        }
        else if (s.Mode == DnsModes.Server && !string.IsNullOrWhiteSpace(s.SelectedServer))
        {
            copy.DnsMode = "custom";
            copy.DnsServer = s.SelectedServer.Trim();
        }
        else
        {
            copy.DnsMode = "system";
        }
        return copy;
    }

    /// <summary>
    /// Приложения со своим DNS — для сборщика конфига ядра: включённые и с выбранным DNS.
    /// Режим — в терминах профиля маршрутизации (system / proxy / custom), как у <see cref="ApplyTo"/>.
    /// </summary>
    public IReadOnlyList<AppDnsRoute> AppRoutes()
    {
        var result = new List<AppDnsRoute>();
        foreach (var app in Current.Apps ?? new List<DnsAppRule>())
        {
            if (app is null || !app.Enabled || !DnsTargetVm.Chosen(app)) continue;
            result.Add(new AppDnsRoute(app.ProcessName ?? "", app.Path ?? "", RouteDnsMode(app.Mode),
                app.Mode == DnsModes.Server ? app.Server?.Trim() : null));
        }
        return result;
    }

    /// <summary>
    /// Домены со своим DNS — для сборщика конфига ядра: включённые и с выбранным DNS, в порядке списка.
    /// Запись — как её ввёл пользователь: поле sing-box и порядок правил выбирает сборщик.
    /// </summary>
    public IReadOnlyList<DomainDnsRoute> DomainRoutes()
    {
        var result = new List<DomainDnsRoute>();
        foreach (var rule in Current.Domains ?? new List<DnsDomainRule>())
        {
            if (rule is null || !rule.Enabled || !DnsTargetVm.Chosen(rule) || string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            result.Add(new DomainDnsRoute(rule.Pattern.Trim(), RouteDnsMode(rule.Mode),
                rule.Mode == DnsModes.Server ? rule.Server?.Trim() : null));
        }
        return result;
    }

    /// <summary>Режим раздела → режим профиля маршрутизации, который понимает сборщик.</summary>
    private static string RouteDnsMode(string mode) => mode switch
    {
        DnsModes.Proxy => "proxy",
        DnsModes.Server => "custom",
        _ => "system"
    };

    // -------------------------------------------------------------------- выбор
    /// <summary>Нажатие на строку списка: провайдер раскрывается/сворачивается, свой DNS выбирается.</summary>
    public void Activate(DnsEntryVm entry)
    {
        if (entry.IsProvider) ToggleExpand(entry);
        else SelectCustom(entry);
    }

    private void SelectPinned(DnsPinnedVm? item)
    {
        if (item is not null) Choose(item.Mode, null, null);
    }

    private void SelectServer(DnsServerVm? server)
    {
        if (server is not null) Choose(DnsModes.Server, server.Entry.Id, server.Address);
    }

    private void SelectCustom(DnsEntryVm entry)
    {
        if (entry.IsCustom && !string.IsNullOrWhiteSpace(entry.Address))
            Choose(DnsModes.Server, entry.Id, entry.Address);
    }

    /// <summary>
    /// Нажали на DNS в основном списке. Если для домена или приложения выбирают DNS («Выбрать DNS»),
    /// он достаётся записи; иначе становится основным.
    /// </summary>
    private void Choose(string mode, string? entryId, string? server)
    {
        if (_picking is { } target) Assign(target, mode, entryId, server);
        else Select(mode, entryId, server);
    }

    /// <summary>Запомнить выбор, подсветить его акцентом и попросить перезапустить подключение.</summary>
    private void Select(string mode, string? entryId, string? server)
    {
        var s = Current;
        if (s.Mode == mode && s.SelectedEntryId == entryId &&
            string.Equals(s.SelectedServer, server, StringComparison.OrdinalIgnoreCase))
            return;   // уже выбран — перезапускать нечего

        s.Mode = mode;
        s.SelectedEntryId = entryId;
        s.SelectedServer = server;
        App.Settings.SaveSoon();
        RefreshSelection();
        DnsChanged?.Invoke("DNS: " + SelectionText);
    }

    private void RefreshSelection()
    {
        var s = Current;
        foreach (var item in Pinned) item.IsSelected = s.Mode == item.Mode;
        foreach (var entry in Entries) entry.SyncSelection(s);
        _shownSelection = s.Mode == DnsModes.Server ? s.Mode + " " + s.SelectedServer : s.Mode;
        Raise(nameof(SelectionText));
    }

    // --------------------------------------------------------------- провайдеры
    private void ToggleExpand(DnsEntryVm? entry)
    {
        if (entry is null || !entry.IsProvider) return;
        entry.IsExpanded = !entry.IsExpanded;
        App.Settings.SaveSoon();
        if (entry.IsExpanded) _ = RefreshAsync(entry, force: false);
    }

    /// <summary>
    /// DNS-запрос к именам провайдера — в ответе адреса его серверов (только у провайдеров, чьи
    /// серверы меняются; у остальных адреса в справочнике). Без <paramref name="force"/> свежий
    /// список (моложе <see cref="ServersMaxAge"/>) не перезапрашиваем.
    /// </summary>
    private async Task RefreshAsync(DnsEntryVm? entry, bool force)
    {
        if (entry is null || !entry.IsDynamic || entry.IsLoading) return;
        var info = DnsProviders.Find(entry.Id);
        if (info is null) return;

        var updatedAt = entry.Model.ServersUpdatedAt;
        var fresh = entry.Model.Servers.Count > 0 && updatedAt is { } at &&
                    DateTime.UtcNow - at.ToUniversalTime() < ServersMaxAge;
        if (!force && fresh) return;

        entry.IsLoading = true;
        try
        {
            var lookup = await DnsProviders.ResolveAsync(info);
            if (lookup.Servers.Count > 0)
            {
                entry.SetServers(lookup.Servers, lookup.Source);
                RefreshTargetTexts();   // у записей с сервером провайдера подпись берётся из его списка
                App.Settings.SaveSoon();
            }
            else
            {
                entry.Error = lookup.Error.Length > 0 ? lookup.Error : "провайдер не вернул адресов";
                if (force) Notify?.Invoke($"{entry.Name}: не удалось получить адреса — {entry.Error}", true);
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
        }
        finally
        {
            entry.IsLoading = false;
        }
    }

    private void OpenSite(DnsEntryVm? entry)
    {
        var url = entry?.Site;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke("Не удалось открыть сайт: " + ex.Message, true);
        }
    }

    // --------------------------------------------------------------------- пинг
    /// <summary>
    /// Пинг всех серверов списка: серверы провайдеров и свои DNS (каждый адрес — один раз).
    /// У провайдера в строке — лучший из его серверов, у каждого сервера — свой результат.
    /// </summary>
    private async Task PingAllAsync()
    {
        if (_tunActive())
        {
            Notify?.Invoke("Пинг DNS недоступен, пока работает TUN: ядро перехватывает DNS-запросы, и замер " +
                           "показал бы не те серверы. Отключитесь или выключите TUN и повторите.", true);
            return;
        }

        try
        {
            // у GeoHide серверы меняются — сначала обновляем устаревший или ещё не полученный список
            await Task.WhenAll(Entries.Where(e => e.IsDynamic).ToList().Select(e => RefreshAsync(e, force: false)));

            var targets = Entries.SelectMany(e => e.PingTargets())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0)
            {
                Notify?.Invoke("Пинговать нечего: в списке нет серверов", false);
                return;
            }

            foreach (var address in targets) _pings.SetPending(address);
            RefreshPings(null);

            using var gate = new SemaphoreSlim(PingParallelism);
            await Task.WhenAll(targets.Select(async address =>
            {
                await gate.WaitAsync();
                DnsPingResult result;
                try
                {
                    // замер — в пуле потоков: занятый UI-поток не должен прибавлять к времени ответа
                    result = await Task.Run(() => DnsPing.MeasureAsync(address));
                }
                catch (Exception ex)
                {
                    result = DnsPingResult.Fail(ex.Message);
                }
                finally
                {
                    gate.Release();
                }

                // продолжение — снова в UI-потоке: строки обновляем по мере ответов
                _pings.SetResult(address, result);
                RefreshPings(address);
            }));

            ReportPing(targets);
        }
        catch (Exception ex)
        {
            Notify?.Invoke("Пинг DNS не удался: " + ex.Message, true);
        }
        finally
        {
            _pings.ClearPending();
            RefreshPings(null);
        }
    }

    private void RefreshPings(string? address)
    {
        foreach (var entry in Entries) entry.RefreshPing(address);
    }

    /// <summary>Итог в строке состояния: сколько серверов ответило и какой быстрее всех.</summary>
    private void ReportPing(IReadOnlyCollection<string> targets)
    {
        var answered = new List<(string Address, int Ms)>();
        foreach (var address in targets)
            if (_pings.TryGet(address, out var result) && result.Ok)
                answered.Add((address, result.Ms));

        if (answered.Count == 0)
        {
            Notify?.Invoke("Пинг DNS: ни один сервер не ответил — проверьте подключение к интернету", true);
            return;
        }

        var best = answered.MinBy(a => a.Ms);
        var owner = Entries.FirstOrDefault(e => e.PingTargets().Contains(best.Address, StringComparer.OrdinalIgnoreCase));
        var name = owner is null || string.Equals(owner.Name, best.Address, StringComparison.OrdinalIgnoreCase)
            ? best.Address
            : owner.Name + " " + best.Address;
        Notify?.Invoke($"Пинг DNS: ответили {answered.Count} из {targets.Count}, быстрее всех — {name} " +
                       $"({DnsPingBoard.Format(best.Ms)})", false);
    }

    // ------------------------------------------------------------- свои DNS, порядок
    private void ToggleFavorite(DnsEntryVm? entry)
    {
        if (entry is null) return;
        entry.Favorite = !entry.Favorite;
        App.Settings.SaveSoon();
    }

    private void AddCustom()
    {
        var result = _dialogs.EditDnsServer("Свой DNS", "Добавить");
        if (result is not { } added) return;

        var model = new DnsEntry { Name = added.Name, Address = added.Address };
        var s = Current;
        s.Entries.Add(model);
        var entry = new DnsEntryVm(model, _pings);
        Entries.Add(entry);
        entry.SyncSelection(s);
        App.Settings.SaveSoon();
        Notify?.Invoke($"DNS «{model.Name}» добавлен — нажмите на него в списке, чтобы выбрать", false);
    }

    private void Edit(DnsEntryVm? entry)
    {
        if (entry is null || !entry.IsCustom) return;

        var result = _dialogs.EditDnsServer("Изменить DNS", "Сохранить", entry.Name, entry.Address);
        if (result is not { } edited) return;

        var addressChanged = !string.Equals(entry.Address, edited.Address, StringComparison.OrdinalIgnoreCase);
        entry.Update(edited.Name, edited.Address);

        var s = Current;
        var inUse = s.Mode == DnsModes.Server && s.SelectedEntryId == entry.Id;
        if (inUse && addressChanged) s.SelectedServer = edited.Address;

        // у доменов и приложений с этим DNS — тоже новый адрес
        var targets = TargetsUsing(entry);
        if (addressChanged)
            foreach (var target in targets) target.SetDns(DnsModes.Server, entry.Id, edited.Address);

        App.Settings.SaveSoon();
        RefreshSelection();
        RefreshTargetTexts();

        // поменяли адрес DNS, который сейчас используется, — ядру нужен новый
        if (!addressChanged) return;
        if (inUse) DnsChanged?.Invoke("DNS: " + SelectionText);
        else if (targets.Any(t => t.Enabled && t.IsUsable)) DnsChanged?.Invoke($"DNS «{entry.Name}»: новый адрес {edited.Address}");
    }

    private void Remove(DnsEntryVm? entry)
    {
        if (entry is null || !entry.IsCustom) return;

        var targets = TargetsUsing(entry);
        var question = $"Удалить «{entry.Name}» из списка?";
        if (targets.Count > 0)
            question += $"\n\nЭтот DNS выбран у {TargetNames(targets)}. Они вернутся на основной DNS.";
        if (!_dialogs.Confirm("Удалить DNS", question, "Удалить")) return;

        var s = Current;
        var inUse = s.Mode == DnsModes.Server && s.SelectedEntryId == entry.Id;
        s.Entries.Remove(entry.Model);
        Entries.Remove(entry);

        // у доменов и приложений с этим DNS его больше нет — они снова на основном
        foreach (var target in targets) target.ClearDns();
        RefreshTargetTexts();
        Raise(nameof(RulesSummaryText));

        if (inUse)
        {
            // удалили используемый DNS — возвращаемся к системному
            s.Mode = DnsModes.System;
            s.SelectedEntryId = null;
            s.SelectedServer = null;
            RefreshSelection();
            DnsChanged?.Invoke("DNS: " + SelectionText);
        }
        else if (targets.Any(t => t.Enabled && t.IsUsable))
        {
            DnsChanged?.Invoke($"Удалён DNS «{entry.Name}»: у {TargetNames(targets)} снова основной");
        }
        App.Settings.SaveSoon();
    }

    /// <summary>Перетаскивание: строка встаёт на новое место (на диск — по отпусканию, <see cref="CommitOrder"/>).</summary>
    public void MoveEntry(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Entries.Count || to >= Entries.Count || from == to) return;
        Entries.Move(from, to);
    }

    /// <summary>Перетаскивание закончено — сохраняем порядок.</summary>
    public void CommitOrder()
    {
        Current.Entries = Entries.Select(e => e.Model).ToList();
        App.Settings.SaveSoon();
    }

    // ------------------------------------------------------------------ домены
    /// <summary>Проверить введённое: тип записи рядом с полем и ошибка под ним (IP, неверный regex).</summary>
    private void CheckNewDomain()
    {
        _newDomainParts = DnsPattern.Split(_newDomainPattern);
        string? problem = null;
        var label = "";

        if (_newDomainParts.Count == 1)
        {
            if (DnsPattern.TryParse(_newDomainParts[0], out var parsed, out var error))
            {
                label = parsed.IsRegex ? "regex" : "домен";
            }
            else
            {
                problem = error;
                label = Text.NormalizeUserPattern(_newDomainParts[0]).kind == MatchKind.Ip ? "IP" : "ошибка";
            }
        }
        else if (_newDomainParts.Count > 1)
        {
            foreach (var part in _newDomainParts)
            {
                if (DnsPattern.TryParse(part, out _, out var error)) continue;
                problem = $"«{part}»: {error}";
                break;
            }
            var count = _newDomainParts.Count;
            label = problem is not null ? "ошибка" : count + " " + Plural(count, "запись", "записи", "записей");
        }

        NewDomainKindLabel = label;
        NewDomainProblem = problem;
        AddDomainCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Добавить введённые записи (одну или несколько через запятую) наверх списка — как в маршрутизации.
    /// DNS у новой записи ещё не выбран: под ней «Выбрать DNS».
    /// </summary>
    private void AddDomains()
    {
        var parts = DnsPattern.Split(NewDomainPattern);
        if (parts.Count == 0) return;

        var added = new List<DnsDomainVm>();
        var duplicates = new List<string>();
        foreach (var part in parts)
        {
            if (!DnsPattern.TryParse(part, out var parsed, out var error))
            {
                Notify?.Invoke($"«{part}»: {error}", true);
                return;   // кнопка в этом случае недоступна; ничего не добавляем
            }
            if (Domains.Any(d => d.Key == parsed.Key) || added.Any(d => d.Key == parsed.Key))
            {
                duplicates.Add(part);
                continue;
            }
            added.Add(CreateDomainVm(new DnsDomainRule { Pattern = part }));
        }

        // новые — наверху, в том порядке, в каком их ввели
        var s = Current;
        for (var i = 0; i < added.Count; i++)
        {
            s.Domains.Insert(i, added[i].Model);
            Domains.Insert(i, added[i]);
            RefreshTargetText(added[i]);
        }
        NewDomainPattern = "";

        if (added.Count > 0)
        {
            App.Settings.SaveSoon();
            Raise(nameof(HasDomains));
        }

        var already = duplicates.Count == 0
            ? ""
            : "уже в списке: " + string.Join(", ", duplicates.Take(3).Select(d => $"«{d}»")) +
              (duplicates.Count > 3 ? " и ещё " + (duplicates.Count - 3) : "");
        if (added.Count == 0)
            Notify?.Invoke("Уже в списке: " + already["уже в списке: ".Length..], false);
        else if (added.Count == 1)
            Notify?.Invoke($"«{added[0].Name}» добавлен — нажмите «Выбрать DNS»" + (already.Length > 0 ? "; " + already : ""), false);
        else
            Notify?.Invoke($"Добавлено записей: {added.Count} — у каждой нажмите «Выбрать DNS»" + (already.Length > 0 ? "; " + already : ""), false);
    }

    private void RemoveDomain(DnsDomainVm? rule)
    {
        if (rule is null) return;
        if (ReferenceEquals(_picking, rule)) EndPick(restoreTab: false);

        Current.Domains.Remove(rule.Model);
        Domains.Remove(rule);
        Detach(rule);
        App.Settings.SaveSoon();
        Raise(nameof(HasDomains));
        Raise(nameof(RulesSummaryText));

        // у домена работал свой DNS — без него ядру нужен новый конфиг
        if (rule.Enabled && rule.HasDns && rule.IsUsable) DnsChanged?.Invoke($"DNS для «{rule.Name}» убран: домен снова на основном");
        else Notify?.Invoke($"«{rule.Name}» убран из списка доменов", false);
    }

    /// <summary>Запись домена поправили в карточке: сохраняем и, если ядру уходит другое, переподключаемся.</summary>
    private void OnDomainPatternChanged(DnsDomainVm rule, bool changed)
    {
        App.Settings.SaveSoon();
        Raise(nameof(RulesSummaryText));
        if (!changed || !rule.Enabled || !rule.HasDns) return;

        if (rule.IsUsable) DnsChanged?.Invoke($"DNS для «{rule.Name}»: {rule.DnsText}");
        else DnsChanged?.Invoke($"Запись «{rule.Name}» с ошибкой — свой DNS для неё не действует");
    }

    private DnsDomainVm CreateDomainVm(DnsDomainRule model)
    {
        var rule = new DnsDomainVm(model);
        rule.EnabledChanged += OnTargetEnabledChanged;
        rule.PatternChanged += OnDomainPatternChanged;
        return rule;
    }

    /// <summary>Запись домена из settings.json: чиним то, что могли испортить руками.</summary>
    private static void NormalizeDomain(DnsDomainRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
        rule.Pattern = rule.Pattern.Trim();
        NormalizeChoice(rule);
    }

    // -------------------------------------------------------------- приложения
    private void AddAppFromExe()
    {
        var path = _dialogs.OpenFile("Приложения (*.exe)|*.exe|Все файлы (*.*)|*.*");
        if (path is not null) AddAppPath(path);
    }

    private void AddAppsFromProcesses()
    {
        foreach (var app in _dialogs.PickApplications())
            AddAppPath(app.Path, app.Title);
    }

    /// <summary>Добавить приложение по пути к .exe — как в маршрутизации. DNS у нового приложения ещё не выбран.</summary>
    public void AddAppPath(string exePath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;

        var existing = Apps.FirstOrDefault(a => string.Equals(a.Model.Path, exePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Notify?.Invoke($"«{existing.Name}» уже в списке приложений", false);
            return;
        }

        var (processName, title) = ProcessScanner.Describe(exePath);
        var model = new DnsAppRule
        {
            Name = displayName is { Length: > 0 } ? displayName : title,
            Path = exePath,
            ProcessName = processName
        };
        Current.Apps.Add(model);
        var app = CreateAppVm(model);
        Apps.Add(app);
        RefreshTargetText(app);
        App.Settings.SaveSoon();
        Raise(nameof(HasApps));
        Notify?.Invoke($"Приложение «{app.Name}» добавлено — нажмите «Выбрать DNS»", false);
    }

    private void RemoveApp(DnsAppVm? app)
    {
        if (app is null) return;
        if (ReferenceEquals(_picking, app)) EndPick(restoreTab: false);

        Current.Apps.Remove(app.Model);
        Apps.Remove(app);
        Detach(app);
        App.Settings.SaveSoon();
        Raise(nameof(HasApps));
        Raise(nameof(RulesSummaryText));

        // у приложения работал свой DNS — без него ядру нужен новый конфиг
        if (app.Enabled && app.HasDns) DnsChanged?.Invoke($"DNS для «{app.Name}» убран: приложение снова на основном");
        else Notify?.Invoke($"Приложение «{app.Name}» убрано", false);
    }

    private DnsAppVm CreateAppVm(DnsAppRule model)
    {
        var app = new DnsAppVm(model);
        app.EnabledChanged += OnTargetEnabledChanged;
        return app;
    }

    /// <summary>Строка приложения из settings.json: чиним то, что могли испортить руками.</summary>
    private static void NormalizeApp(DnsAppRule app)
    {
        if (string.IsNullOrWhiteSpace(app.Id)) app.Id = Guid.NewGuid().ToString("N");
        app.Name ??= "";
        app.Path ??= "";
        if (string.IsNullOrWhiteSpace(app.ProcessName))
            app.ProcessName = app.Path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(app.Path) : "";
        NormalizeChoice(app);
    }

    // ------------------------------------------------------ выбор DNS для записи
    /// <summary>
    /// «Выбрать DNS» у домена или приложения: открываем «Основные» с плашкой «Выберите DNS для …» —
    /// следующее нажатие на DNS там достанется записи (<see cref="Assign"/>), и раздел вернётся во
    /// вкладку, откуда пришли.
    /// </summary>
    private void StartPick(DnsTargetVm? target)
    {
        if (target is null) return;

        // во время выбора вкладка записи не видна; если выбор всё же начали заново — вернёмся туда же
        var back = _picking is not null ? _returnTab : _tab;
        _picking = target;
        _returnTab = back == TabMain ? null : back;
        SetTab(TabMain);
        RaisePicking();
    }

    /// <summary>Передумали выбирать DNS для записи: назад во вкладку, откуда пришли.</summary>
    public void CancelPick() => EndPick(restoreTab: true);

    /// <summary>Закончить выбор: нажатия в основном списке снова меняют основной DNS.</summary>
    private void EndPick(bool restoreTab)
    {
        if (_picking is null) return;
        var back = _returnTab;
        _picking = null;
        _returnTab = null;
        RaisePicking();
        if (restoreTab && back is not null) SetTab(back);
    }

    private void RaisePicking()
    {
        Raise(nameof(PickingTarget));
        Raise(nameof(IsPicking));
        Raise(nameof(PickingTitle));
        Raise(nameof(PickingHint));
        Raise(nameof(PickingKind));
        Raise(nameof(PickingIcon));
    }

    /// <summary>
    /// DNS из основного списка достаётся записи: выбор сохраняется, подключение перезапускается, раздел
    /// возвращается во вкладку записи, а её карточка ненадолго подсвечивается.
    /// </summary>
    private void Assign(DnsTargetVm target, string mode, string? entryId, string? server)
    {
        EndPick(restoreTab: true);

        var choice = target.Choice;
        var isServer = mode == DnsModes.Server;
        if (choice.Mode == mode && choice.EntryId == (isServer ? entryId : null) &&
            string.Equals(choice.Server?.Trim(), isServer ? server?.Trim() : null, StringComparison.OrdinalIgnoreCase))
        {
            target.Highlight(HighlightTime);
            Notify?.Invoke($"У «{target.Name}» уже этот DNS: {target.DnsText}", false);
            return;
        }

        target.SetDns(mode, entryId, server);
        RefreshTargetText(target);
        App.Settings.SaveSoon();
        Raise(nameof(RulesSummaryText));
        target.Highlight(HighlightTime);

        if (!target.Enabled)
            Notify?.Invoke($"DNS для «{target.Name}»: {target.DnsText} — заработает, когда включите галочку", false);
        else if (!target.IsUsable)
            Notify?.Invoke($"DNS для «{target.Name}»: {target.DnsText} — заработает, когда исправите запись", false);
        else
            DnsChanged?.Invoke($"DNS для «{target.Name}»: {target.DnsText}");
    }

    private void OnTargetEnabledChanged(DnsTargetVm target)
    {
        App.Settings.SaveSoon();
        Raise(nameof(RulesSummaryText));
        if (!target.HasDns || !target.IsUsable) return;
        DnsChanged?.Invoke(target.Enabled
            ? $"DNS для «{target.Name}» включён: {target.DnsText}"
            : $"DNS для «{target.Name}» выключен: {target.Noun} снова на основном");
    }

    /// <summary>Карточку убрали из списка — отписываемся от её событий.</summary>
    private void Detach(DnsTargetVm target)
    {
        target.EnabledChanged -= OnTargetEnabledChanged;
        if (target is DnsDomainVm rule) rule.PatternChanged -= OnDomainPatternChanged;
    }

    /// <summary>Все записи со своим DNS: сначала домены, потом приложения.</summary>
    private IEnumerable<DnsTargetVm> AllTargets() => Domains.Cast<DnsTargetVm>().Concat(Apps);

    /// <summary>Домены и приложения, у которых выбран DNS из этой строки списка.</summary>
    private List<DnsTargetVm> TargetsUsing(DnsEntryVm entry) =>
        AllTargets().Where(t => t.Choice.Mode == DnsModes.Server && t.Choice.EntryId == entry.Id).ToList();

    /// <summary>«youtube.com», «Discord» и ещё 2 — для сообщений.</summary>
    private static string TargetNames(IReadOnlyList<DnsTargetVm> targets)
    {
        var names = string.Join(", ", targets.Take(3).Select(t => $"«{t.Name}»"));
        return targets.Count > 3 ? names + " и ещё " + (targets.Count - 3) : names;
    }

    private void RefreshTargetTexts()
    {
        foreach (var target in AllTargets()) RefreshTargetText(target);
    }

    /// <summary>Подпись DNS в карточке записи — теми же словами, что в основном списке.</summary>
    private void RefreshTargetText(DnsTargetVm target)
    {
        var choice = target.Choice;
        if (!target.HasDns)
        {
            target.ShowDns("", null);
            return;
        }

        if (choice.Mode is DnsModes.System or DnsModes.Proxy)
        {
            var pinned = Pinned.First(p => p.Mode == choice.Mode);
            target.ShowDns(pinned.Name, pinned.Hint);
            return;
        }

        var entry = Entries.FirstOrDefault(e => e.Id == choice.EntryId);
        target.ShowDns(ServerText(choice.EntryId, choice.Server), entry?.IsProvider == true ? entry.Tagline : null);
    }

    /// <summary>Выбранный DNS записи из settings.json: режим без адреса и прочий мусор — «DNS не выбран».</summary>
    private static void NormalizeChoice(IDnsChoice choice)
    {
        if (choice.Mode is not (DnsModes.System or DnsModes.Proxy or DnsModes.Server)) choice.Mode = "";
        if (choice.Mode == DnsModes.Server && string.IsNullOrWhiteSpace(choice.Server)) choice.Mode = "";
        if (choice.Mode != DnsModes.Server)
        {
            choice.EntryId = null;
            choice.Server = null;
        }
    }

    // ------------------------------------------------------------ DNS обхода DPI
    /// <summary>
    /// Откуда движок обхода DPI берёт адреса сайтов: системный DNS провайдера или DoH. Это не DNS
    /// ядра: обход «Напрямую» резолвит сам (<see cref="DnsResolver"/>), а «Через профиль» отдаёт имя
    /// сайта ядру. Хранится в блоке обхода (<c>Dpi.Doh</c>), раньше выбирался в «Настройках».
    /// </summary>
    public DohMode DpiDoh
    {
        get => App.Settings.Current.Dpi.Doh;
        set
        {
            var dpi = App.Settings.Current.Dpi;
            if (dpi.Doh == value) return;
            dpi.Doh = value;
            App.Settings.SaveSoon();
            Raise();
            Raise(nameof(DpiDohIsCustom));
            OnDpiDnsChanged();
        }
    }

    /// <summary>Показывать поле своего URL DoH.</summary>
    public bool DpiDohIsCustom => App.Settings.Current.Dpi.Doh == DohMode.Custom;

    /// <summary>Свой URL DoH — сервер с JSON-ответом, как у Cloudflare и Google.</summary>
    public string DpiDohUrl
    {
        get => App.Settings.Current.Dpi.DohUrl;
        set
        {
            var dpi = App.Settings.Current.Dpi;
            var url = (value ?? "").Trim();
            if (dpi.DohUrl == url) return;
            dpi.DohUrl = url;
            App.Settings.SaveSoon();
            Raise();
            OnDpiDnsChanged();
        }
    }

    /// <summary>
    /// DNS обхода поменяли: кэш адресов сбрасываем — иначе ответы прежнего резолвера доживали бы
    /// до конца своего TTL, — и просим перезапустить обход (<see cref="DpiDnsChanged"/>).
    /// </summary>
    private void OnDpiDnsChanged()
    {
        DnsResolver.ClearCache();
        DpiDnsChanged?.Invoke();
    }

    /// <summary>Обновить привязки DNS обхода: после загрузки или сброса настроек.</summary>
    private void RaiseDpiDns()
    {
        Raise(nameof(DpiDoh));
        Raise(nameof(DpiDohIsCustom));
        Raise(nameof(DpiDohUrl));
    }
}
