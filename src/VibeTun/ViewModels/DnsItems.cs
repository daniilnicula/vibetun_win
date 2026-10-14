using System.Collections.ObjectModel;
using VibeTun.Models;
using VibeTun.Services;

namespace VibeTun.ViewModels;

/// <summary>Строки «Системный» и «Через Proxy»: всегда наверху списка, не перетаскиваются.</summary>
public sealed class DnsPinnedVm : ObservableObject
{
    private bool _isSelected;

    public DnsPinnedVm(string mode, string name, string subtitle, string hint)
    {
        Mode = mode;
        Name = name;
        Subtitle = subtitle;
        Hint = hint;
    }

    /// <summary><see cref="DnsModes.System"/> или <see cref="DnsModes.Proxy"/>.</summary>
    public string Mode { get; }

    public string Name { get; }
    public string Subtitle { get; }

    /// <summary>Подробности — во всплывающей подсказке.</summary>
    public string Hint { get; }

    public bool IsSystem => Mode == DnsModes.System;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// Результаты пинга DNS-серверов за сессию (по адресу). Отдельно от строк: строки серверов
/// пересоздаются при смене выбора, а замеры должны оставаться на месте. Только UI-поток.
/// </summary>
public sealed class DnsPingBoard
{
    private readonly Dictionary<string, DnsPingResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string address, out DnsPingResult result) => _results.TryGetValue(address.Trim(), out result);

    public bool IsPending(string address) => _pending.Contains(address.Trim());

    public void SetPending(string address) => _pending.Add(address.Trim());

    public void SetResult(string address, DnsPingResult result)
    {
        _results[address.Trim()] = result;
        _pending.Remove(address.Trim());
    }

    public void ClearPending() => _pending.Clear();

    /// <summary>«12 ms», «&lt;1 ms» — как у задержки профилей.</summary>
    public static string Format(int ms) => ms <= 0 ? "<1 ms" : ms + " ms";
}

/// <summary>
/// Один DNS-сервер провайдера. Адрес и выбор неизменяемые (при смене выбора строки пересоздаются),
/// меняется только пинг. INotifyPropertyChanged нужен и поэтому, и потому, что к объектам без него
/// WPF привязывается через PropertyDescriptor и держит их в памяти.
/// </summary>
public sealed class DnsServerVm : ObservableObject
{
    private bool _isPinging;
    private bool _hasPing;
    private int _pingMs = -1;
    private string? _pingTip;

    public DnsServerVm(DnsEntryVm entry, string address, string label, bool isSelected, bool isStale)
    {
        Entry = entry;
        Address = address;
        Label = label;
        IsSelected = isSelected;
        IsStale = isStale;
    }

    public DnsEntryVm Entry { get; }
    public string Address { get; }

    /// <summary>Вариант сервера: режим фильтрации («Семейный») или регион выхода («EU»).</summary>
    public string Label { get; }

    public bool IsSelected { get; }

    /// <summary>Выбран раньше, но в списке провайдера его уже нет (серверы сменились).</summary>
    public bool IsStale { get; }

    /// <summary>Подпись рядом с адресом: вариант сервера или почему строка устарела.</summary>
    public string Note => IsStale ? Entry.StaleNote : Label;

    // ------------------------------------------------------------------- пинг
    public bool IsPinging
    {
        get => _isPinging;
        private set => SetProperty(ref _isPinging, value);
    }

    /// <summary>Замер есть (удачный или нет).</summary>
    public bool HasPing
    {
        get => _hasPing;
        private set => SetProperty(ref _hasPing, value);
    }

    /// <summary>Время ответа, мс; -1 — не ответил или не измеряли.</summary>
    public int PingMs
    {
        get => _pingMs;
        private set => SetProperty(ref _pingMs, value);
    }

    /// <summary>Почему не ответил — подсказка у «нет ответа».</summary>
    public string? PingTip
    {
        get => _pingTip;
        private set => SetProperty(ref _pingTip, value);
    }

    public bool ShowPingValue => HasPing && PingMs >= 0 && !IsPinging;
    public bool PingFailed => HasPing && PingMs < 0 && !IsPinging;

    /// <summary>Показать замер этого адреса из общей таблицы.</summary>
    public void ShowPing(DnsPingBoard board)
    {
        IsPinging = board.IsPending(Address);
        if (board.TryGet(Address, out var result))
        {
            HasPing = true;
            PingMs = result.Ms;
            PingTip = result.Ok ? null : result.Error;
        }
        else
        {
            HasPing = false;
            PingMs = -1;
            PingTip = null;
        }
        Raise(nameof(ShowPingValue));
        Raise(nameof(PingFailed));
    }
}

/// <summary>Строка списка: провайдер (раскрывается списком серверов) или свой DNS.</summary>
public sealed class DnsEntryVm : ObservableObject
{
    private readonly DnsPingBoard _pings;
    private readonly DnsProviderInfo? _info;
    private bool _isSelected;
    private bool _isLoading;
    private bool _isDragging;
    private string _error = "";
    private string? _selectedServer;
    private bool _isPinging;
    private bool _hasPing;
    private int _pingMs = -1;
    private string? _pingTip;

    public DnsEntryVm(DnsEntry model, DnsPingBoard pings)
    {
        Model = model;
        _pings = pings;
        _info = model.IsProvider ? DnsProviders.Find(model.Id) : null;
        RebuildServers();
    }

    public DnsEntry Model { get; }

    public string Id => Model.Id;
    public bool IsProvider => Model.IsProvider;
    public bool IsCustom => !Model.IsProvider;
    public string Name => Model.Name;
    public string Address => Model.Address ?? "";

    /// <summary>Список серверов спрашиваем у провайдера (GeoHide): есть «Обновить» и строка состояния.</summary>
    public bool IsDynamic => _info?.IsDynamic == true;

    public string Tagline => _info?.Tagline ?? "";
    public string Description => _info?.Description ?? "";
    public string Site => _info?.Site ?? "";
    public bool HasSite => Site.Length > 0;

    /// <summary>Подпись выбранного сервера, которого больше нет в списке провайдера.</summary>
    public string StaleNote => IsDynamic ? "нет в свежем ответе провайдера" : "нет в списке провайдера";

    public bool Favorite
    {
        get => Model.Favorite;
        set
        {
            if (Model.Favorite == value) return;
            Model.Favorite = value;
            Raise();
        }
    }

    public bool IsExpanded
    {
        get => Model.Expanded;
        set
        {
            if (Model.Expanded == value) return;
            Model.Expanded = value;
            Raise();
            Raise(nameof(ShowServers));
        }
    }

    /// <summary>Показывать ли под строкой список серверов (только у раскрытого провайдера).</summary>
    public bool ShowServers => IsProvider && IsExpanded;

    /// <summary>Свой DNS — выбран он; провайдер — выбран один из его серверов.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value)) RaiseStatus();
        }
    }

    /// <summary>Строку сейчас перетаскивают — она полупрозрачная.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    /// <summary>Почему не удалось получить адреса провайдера (пусто — всё в порядке).</summary>
    public string Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value ?? "")) RaiseStatus();
        }
    }

    public bool HasError => _error.Length > 0;

    public ObservableCollection<DnsServerVm> Servers { get; } = new();

    /// <summary>Адресов ещё нет и запрос не идёт — подсказка «нажмите Обновить».</summary>
    public bool NoServers => IsDynamic && Servers.Count == 0 && !IsLoading;

    /// <summary>Вторая строка: адрес своего DNS или чем полезен провайдер (и как дела со списком серверов).</summary>
    public string Subtitle
    {
        get
        {
            if (!IsProvider) return Address;
            if (!IsDynamic || Model.Servers.Count > 0) return Tagline;
            if (IsLoading) return Tagline + " · запрос адресов…";
            return Tagline + (HasError ? " · адреса не получены" : " · нажмите, чтобы получить адреса");
        }
    }

    /// <summary>Выбранный сервер провайдера — подсвечивается акцентом рядом с подписью.</summary>
    public string SelectedNote =>
        IsProvider && IsSelected && !string.IsNullOrEmpty(_selectedServer) ? " · выбран " + _selectedServer : "";

    /// <summary>Строка под серверами: когда и чем получен список или что пошло не так.</summary>
    public string StatusText
    {
        get
        {
            if (!IsDynamic) return "";
            if (IsLoading) return "Запрашиваем адреса серверов у провайдера…";

            var at = Model.ServersUpdatedAt?.ToLocalTime().ToString("dd.MM HH:mm");
            if (HasError)
                return "Не удалось обновить: " + _error + (at is null ? "" : " · показан список от " + at);
            if (at is null)
                return Model.Servers.Count > 0 ? "Список сохранён прежней версией — обновится при раскрытии" : "Список серверов ещё не запрашивали";

            var source = string.IsNullOrWhiteSpace(Model.ServersSource) ? "" : " · " + Model.ServersSource;
            return "Обновлено " + at + source;
        }
    }

    public string RefreshText => IsLoading ? "Запрос…" : "Обновить";

    // ------------------------------------------------------------------- пинг
    /// <summary>Идёт замер: своего адреса или хотя бы одного из серверов провайдера.</summary>
    public bool IsPinging
    {
        get => _isPinging;
        private set => SetProperty(ref _isPinging, value);
    }

    /// <summary>Замер есть (удачный или нет).</summary>
    public bool HasPing
    {
        get => _hasPing;
        private set => SetProperty(ref _hasPing, value);
    }

    /// <summary>Свой DNS — его время ответа, провайдер — лучшее из серверов; -1 — нет ответа.</summary>
    public int PingMs
    {
        get => _pingMs;
        private set => SetProperty(ref _pingMs, value);
    }

    /// <summary>Подсказка: какой сервер провайдера быстрее всех или почему не ответил.</summary>
    public string? PingTip
    {
        get => _pingTip;
        private set => SetProperty(ref _pingTip, value);
    }

    public bool ShowPingValue => HasPing && PingMs >= 0 && !IsPinging;
    public bool PingFailed => HasPing && PingMs < 0 && !IsPinging;

    /// <summary>Что пингуется для этой строки: адрес своего DNS или все серверы провайдера.</summary>
    public IReadOnlyList<string> PingTargets()
    {
        if (IsCustom) return Address.Length > 0 ? new[] { Address } : Array.Empty<string>();
        return Servers.Select(s => s.Address).ToList();
    }

    /// <summary>Показать свежие замеры: всех адресов или только <paramref name="address"/>.</summary>
    public void RefreshPing(string? address = null)
    {
        foreach (var server in Servers)
            if (address is null || Same(server.Address, address))
                server.ShowPing(_pings);
        UpdatePingSummary();
    }

    private void UpdatePingSummary()
    {
        if (IsCustom)
        {
            IsPinging = Address.Length > 0 && _pings.IsPending(Address);
            if (Address.Length > 0 && _pings.TryGet(Address, out var result))
            {
                HasPing = true;
                PingMs = result.Ms;
                PingTip = result.Ok ? null : result.Error;
            }
            else
            {
                HasPing = false;
                PingMs = -1;
                PingTip = null;
            }
        }
        else
        {
            IsPinging = Servers.Any(s => s.IsPinging);
            HasPing = Servers.Any(s => s.HasPing);
            var best = Servers.Where(s => s.HasPing && s.PingMs >= 0).MinBy(s => s.PingMs);
            PingMs = best?.PingMs ?? -1;
            PingTip = best is not null
                ? "Быстрее всех: " + best.Address + (best.Label.Length > 0 ? " (" + best.Label + ")" : "") +
                  " — " + DnsPingBoard.Format(best.PingMs)
                : HasPing ? "Ни один сервер не ответил" : null;
        }
        Raise(nameof(ShowPingValue));
        Raise(nameof(PingFailed));
    }

    // ------------------------------------------------------------------ данные
    /// <summary>Правка своего DNS из окна «Изменить».</summary>
    public void Update(string name, string address)
    {
        Model.Name = name;
        Model.Address = address;
        Raise(nameof(Name));
        Raise(nameof(Address));
        Raise(nameof(Subtitle));
        UpdatePingSummary();   // у нового адреса свой замер (или никакого)
    }

    /// <summary>Свежий ответ провайдера: запоминаем (переживёт перезапуск) и перерисовываем серверы.</summary>
    public void SetServers(IEnumerable<DnsProviderServer> servers, string source)
    {
        var list = servers.ToList();
        Model.Servers = list.Select(s => s.Address).ToList();
        var labels = new Dictionary<string, string>();
        foreach (var server in list)
            if (server.Label.Length > 0) labels.TryAdd(server.Address, server.Label);
        Model.ServerLabels = labels;
        Model.ServersUpdatedAt = DateTime.UtcNow;
        Model.ServersSource = source;
        _error = "";
        Raise(nameof(Error));
        RebuildServers();
        RaiseStatus();
    }

    /// <summary>Подсветить то, что выбрано в настройках.</summary>
    public void SyncSelection(DnsSettings settings)
    {
        var mine = settings.Mode == DnsModes.Server &&
                   string.Equals(settings.SelectedEntryId, Model.Id, StringComparison.Ordinal);
        IsSelected = mine;
        _selectedServer = mine ? settings.SelectedServer : null;
        RebuildServers();
        Raise(nameof(SelectedNote));
    }

    /// <summary>Серверы провайдера: из справочника приложения или из последнего ответа провайдера.</summary>
    private IEnumerable<DnsProviderServer> ProviderServers()
    {
        if (_info is null) return Array.Empty<DnsProviderServer>();
        if (!_info.IsDynamic) return _info.Servers;

        var labels = Model.ServerLabels;
        return Model.Servers.Select(a =>
            new DnsProviderServer(a, labels is not null && labels.TryGetValue(a, out var label) ? label : ""));
    }

    private void RebuildServers()
    {
        if (!IsProvider)
        {
            UpdatePingSummary();
            return;
        }

        Servers.Clear();
        var list = ProviderServers().ToList();
        foreach (var server in list)
            Servers.Add(new DnsServerVm(this, server.Address, server.Label, Same(server.Address, _selectedServer), isStale: false));

        // выбранного адреса в списке нет (у провайдера сменились серверы): показываем его отдельной
        // строкой — видно, что сейчас используется, и можно выбрать актуальный
        if (!string.IsNullOrEmpty(_selectedServer) && !list.Any(s => Same(s.Address, _selectedServer)))
            Servers.Add(new DnsServerVm(this, _selectedServer, "", isSelected: true, isStale: true));

        foreach (var server in Servers) server.ShowPing(_pings);
        UpdatePingSummary();
        Raise(nameof(NoServers));
        Raise(nameof(Subtitle));
    }

    private void RaiseStatus()
    {
        Raise(nameof(HasError));
        Raise(nameof(StatusText));
        Raise(nameof(RefreshText));
        Raise(nameof(Subtitle));
        Raise(nameof(NoServers));
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Запись со своим DNS — домен или приложение (вкладки «Домены» и «Приложения»). DNS для неё выбирают
/// в «Основных»: «Выбрать DNS» открывает основной список, нажатие на DNS там достаётся записи, и раздел
/// возвращается во вкладку, откуда пришли. Модель — та же, что в настройках (<see cref="IDnsChoice"/>).
/// </summary>
public abstract class DnsTargetVm : ObservableObject
{
    private string _dnsText = "";
    private string? _dnsHint;
    private bool _isHighlighted;
    private int _highlightVersion;

    protected DnsTargetVm(IDnsChoice choice)
    {
        Choice = choice;
    }

    public IDnsChoice Choice { get; }

    public string Id => Choice.Id;

    /// <summary>Как запись называется в сообщениях и над основным списком: «Discord», «youtube.com».</summary>
    public abstract string Name { get; }

    /// <summary>Название вкладки записи — для подсказки «после выбора вернёмся во вкладку …».</summary>
    public abstract string TabTitle { get; }

    /// <summary>Чем запись снова станет без своего DNS — для сообщений: «домен снова на основном».</summary>
    public abstract string Noun { get; }

    /// <summary>Уходит ли запись в конфиг ядра (у домена — только правильная запись).</summary>
    public virtual bool IsUsable => true;

    /// <summary>Правило включено — галочка, как в маршрутизации.</summary>
    public bool Enabled
    {
        get => Choice.Enabled;
        set
        {
            if (Choice.Enabled == value) return;
            Choice.Enabled = value;
            Raise();
            EnabledChanged?.Invoke(this);
        }
    }

    /// <summary>Галочку переключили (уже записано в модель).</summary>
    public event Action<DnsTargetVm>? EnabledChanged;

    /// <summary>DNS выбран; иначе запись работает через основной.</summary>
    public bool HasDns => Chosen(Choice);

    /// <summary>У записи выбран DNS (режим и, для сервера, его адрес).</summary>
    public static bool Chosen(IDnsChoice choice) =>
        choice.Mode is DnsModes.System or DnsModes.Proxy ||
        (choice.Mode == DnsModes.Server && !string.IsNullOrWhiteSpace(choice.Server));

    /// <summary>Выбранный DNS: «Системный», «Через Proxy», «Yandex DNS · 77.88.8.8 (Базовый)».</summary>
    public string DnsText
    {
        get => _dnsText;
        private set => SetProperty(ref _dnsText, value);
    }

    /// <summary>Подробности о выбранном DNS — во всплывающей подсказке.</summary>
    public string? DnsHint
    {
        get => _dnsHint;
        private set => SetProperty(ref _dnsHint, value);
    }

    /// <summary>DNS только что выбран: после возврата во вкладку карточка ненадолго подсвечена акцентом.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        private set => SetProperty(ref _isHighlighted, value);
    }

    /// <summary>Подсветить карточку на <paramref name="duration"/> (повторный вызов продлевает подсветку).</summary>
    public async void Highlight(TimeSpan duration)
    {
        var version = ++_highlightVersion;
        IsHighlighted = true;
        await Task.Delay(duration);   // продолжение — снова в UI-потоке
        if (version == _highlightVersion) IsHighlighted = false;
    }

    /// <summary>Запомнить выбранный DNS (модель — та же, что в настройках). Пустой режим — DNS не выбран.</summary>
    public void SetDns(string mode, string? entryId, string? server)
    {
        var isServer = mode == DnsModes.Server;
        Choice.Mode = mode;
        Choice.EntryId = isServer ? entryId : null;
        Choice.Server = isServer ? server?.Trim() : null;
        Raise(nameof(HasDns));
    }

    public void ClearDns() => SetDns("", null, null);

    /// <summary>Подписи выбранного DNS: их собирает раздел — ему известны названия провайдеров.</summary>
    public void ShowDns(string text, string? hint)
    {
        DnsText = text;
        DnsHint = string.IsNullOrEmpty(hint) ? null : hint;
    }
}

/// <summary>Приложение со своим DNS — карточка во вкладке «Приложения», как в маршрутизации.</summary>
public sealed class DnsAppVm : DnsTargetVm
{
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconLoaded;

    public DnsAppVm(DnsAppRule model) : base(model)
    {
        Model = model;
    }

    public DnsAppRule Model { get; }

    public override string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.ProcessName : Model.Name;
    public override string TabTitle => "Приложения";
    public override string Noun => "приложение";

    public string Path => string.IsNullOrWhiteSpace(Model.Path) ? Model.ProcessName : Model.Path;

    /// <summary>Значок из .exe (null — файла нет или значок не извлёкся: тогда в плитке общий значок).</summary>
    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = Utils.IconHelper.ForExecutable(Model.Path);
            }
            return _icon;
        }
    }
}

/// <summary>
/// Домен со своим DNS — карточка во вкладке «Домены». Запись правится прямо в карточке, как в
/// маршрутизации; тип (домен или regex) определяется по содержимому (<see cref="Utils.DnsPattern"/>).
/// </summary>
public sealed class DnsDomainVm : DnsTargetVm
{
    private Utils.DnsPattern.Parsed? _parsed;
    private string _problem = "";

    public DnsDomainVm(DnsDomainRule model) : base(model)
    {
        Model = model;
        Parse();
    }

    public DnsDomainRule Model { get; }

    public override string Name => Model.Pattern;
    public override string TabTitle => "Домены";
    public override string Noun => "домен";

    /// <summary>С ошибкой в записи (IP, неверный regex) правило в конфиг ядра не попадает.</summary>
    public override bool IsUsable => _parsed is not null;

    /// <summary>Запись: правится в карточке. Пустую не принимаем — остаётся прежняя.</summary>
    public string Pattern
    {
        get => Model.Pattern;
        set
        {
            var text = Utils.DnsPattern.Clean(value);
            if (text.Length == 0 || text == Model.Pattern)
            {
                Raise();   // в поле — снова сохранённая запись (пустую или адрес страницы не оставляем)
                return;
            }

            var before = Key;
            Model.Pattern = text;
            Parse();
            Raise();
            Raise(nameof(Name));
            PatternChanged?.Invoke(this, before != Key);
        }
    }

    /// <summary>Запись поправили (уже в модели); второй аргумент — поменялось ли, что уходит ядру.</summary>
    public event Action<DnsDomainVm, bool>? PatternChanged;

    /// <summary>Что уходит ядру (поле и значение); пусто — запись с ошибкой.</summary>
    public string Key => _parsed?.Key ?? "";

    public bool IsRegex => _parsed?.IsRegex == true;

    /// <summary>«домен» / «regex»; у записи с ошибкой — «ошибка».</summary>
    public string KindLabel => _parsed is null ? "ошибка" : IsRegex ? "regex" : "домен";

    /// <summary>Что не так с записью (пусто — всё в порядке).</summary>
    public string Problem => _problem;

    public bool HasProblem => _parsed is null;

    /// <summary>Какие сайты попадают под запись — подсказка у типа.</summary>
    public string MatchHint
    {
        get
        {
            if (_parsed is null) return _problem;
            return _parsed.Field switch
            {
                "domain_suffix" => $"{_parsed.Value} и все его поддомены",
                "domain_keyword" => $"Домены, в имени которых есть «{_parsed.Value}»",
                _ => $"Домены, имя которых подходит под выражение {_parsed.Value}"
            };
        }
    }

    private void Parse()
    {
        _parsed = Utils.DnsPattern.TryParse(Model.Pattern, out var parsed, out var error) ? parsed : null;
        _problem = _parsed is null ? error : "";
        Raise(nameof(Key));
        Raise(nameof(IsRegex));
        Raise(nameof(KindLabel));
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        Raise(nameof(MatchHint));
        Raise(nameof(IsUsable));
    }
}
