using System.Windows.Media;
using VibeTun.Models;
using VibeTun.Services;

namespace VibeTun.ViewModels;

/// <summary>
/// Строка сервиса во вкладке «DPI»: имя, домен, значок сайта, выбранный профиль обхода и пинг.
/// Пинг живёт в самой строке: сервисов немного, отдельная таблица замеров, как в разделе «DNS»,
/// тут не нужна.
/// </summary>
public sealed class DpiServiceVm : ObservableObject
{
    private bool _isDragging;
    private bool _isPinging;
    private bool _hasPing;
    private int _pingMs = -1;
    private string? _pingTip;
    private ImageSource? _icon;
    private bool _iconRequested;

    public DpiServiceVm(DpiService model)
    {
        Model = model;
        _pingMs = model.LastPingMs;
        _hasPing = model.CheckedAt is not null;
    }

    public DpiService Model { get; }

    /// <summary>Флажок или звезда переключены прямо в строке — раздел сохраняет настройки.</summary>
    public Action? Toggled { get; set; }

    public string Id => Model.Id;

    public string Name => Model.Name;

    public string Domain => Model.Domain;

    /// <summary>
    /// Подпись домена: у сервиса без домена строка не работает — показываем подсказку.
    /// Доменов может быть несколько (страница и её служебные домены) — в строке видно
    /// первый и сколько всего, полный список — в подсказке.
    /// </summary>
    public string DomainText
    {
        get
        {
            if (Model.Domain.Length == 0) return L.T("dpi.noDomain");

            var list = DpiServiceCatalog.DomainList(Model.Domain);
            if (list.Count <= 1) return Model.Domain;

            return L.F("dpi.domainsMore", list[0], list.Count - 1);
        }
    }

    /// <summary>Все домены сервиса — для подсказки строки.</summary>
    public string DomainsTip => string.Join(", ", DpiServiceCatalog.DomainList(Model.Domain));

    /// <summary>Включён — профиль сервиса применяется к его трафику.</summary>
    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            Raise();
            Raise(nameof(Subtitle));
            Raise(nameof(EnabledHint));
            Toggled?.Invoke();
        }
    }

    public bool Favorite
    {
        get => Model.Favorite;
        set
        {
            if (Model.Favorite == value) return;
            Model.Favorite = value;
            Raise();
            Toggled?.Invoke();
        }
    }

    /// <summary>Строку сейчас перетаскивают — она полупрозрачная.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    /// <summary>Значок сайта: то, что браузер показывает слева от адреса страницы.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!SetProperty(ref _icon, value)) return;
            Raise(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Значок ещё не пробовали взять — список просит подгрузить его фоном.</summary>
    public bool IconRequested
    {
        get => _iconRequested;
        set => _iconRequested = value;
    }

    private string _profileName = "";

    /// <summary>Имя профиля обхода: по нему видно, каким приёмом пользуется сервис.</summary>
    public string ProfileName => _profileName.Length > 0 ? _profileName : L.T("dpi.common");

    /// <summary>Профиль ещё не выбран — работает общий набор из настроек.</summary>
    public bool HasProfile => _profileName.Length > 0;

    /// <summary>Акцент карточки: подпись профиля и строка состояния.</summary>
    public string Subtitle => HasProfile ? L.F("dpi.profileLabel", ProfileName) : L.T("dpi.commonHint");

    /// <summary>Доменов нет — по такому сервису обход не узнаёт трафик (подсказка в окне).</summary>
    public bool HasDomain => Model.Domain.Length > 0;

    /// <summary>Подсказка у переключателя: что значит включённый и выключенный сервис.</summary>
    public string EnabledHint => Enabled ? L.T("dpi.enabledHint") : L.T("dpi.disabledHint");

    public void ShowProfile(DpiStrategyProfile? profile)
    {
        _profileName = profile?.DisplayName ?? "";
        Raise(nameof(ProfileName));
        Raise(nameof(HasProfile));
        Raise(nameof(Subtitle));
    }

    // ------------------------------------------------------------------- пинг
    public bool IsPinging
    {
        get => _isPinging;
        private set
        {
            if (SetProperty(ref _isPinging, value)) RaisePing();
        }
    }

    /// <summary>Замер есть (удачный или нет).</summary>
    public bool HasPing
    {
        get => _hasPing;
        private set
        {
            if (SetProperty(ref _hasPing, value)) RaisePing();
        }
    }

    /// <summary>Время ответа, мс; -1 — не ответил или не измеряли.</summary>
    public int PingMs
    {
        get => _pingMs;
        private set
        {
            if (SetProperty(ref _pingMs, value)) RaisePing();
        }
    }

    /// <summary>Что произошло при замере — подсказка у значения.</summary>
    public string? PingTip
    {
        get => _pingTip;
        private set => SetProperty(ref _pingTip, value);
    }

    public bool ShowPingValue => HasPing && PingMs >= 0 && !IsPinging;
    public bool PingFailed => HasPing && PingMs < 0 && !IsPinging;

    public void ShowPing(DpiServiceTest result)
    {
        IsPinging = false;
        HasPing = true;
        PingMs = result.Answered ? result.Ms : -1;
        PingTip = result.Detail.Length > 0 ? result.Detail : null;
    }

    public void ShowPingPending() => IsPinging = true;

    /// <summary>Забыть замер (сервис правили — старый результат уже не про него).</summary>
    public void ClearPing()
    {
        IsPinging = false;
        HasPing = false;
        PingMs = -1;
        PingTip = null;
    }

    private void RaisePing()
    {
        Raise(nameof(ShowPingValue));
        Raise(nameof(PingFailed));
    }
}

/// <summary>
/// Строка «Общие» над списком сервисов: набор приёмов, который применяется ко всему трафику.
/// Переименовать её и задать домены нельзя — это не сервис, а общий набор раздела (в нём же
/// живут транспортные поля: порт, режим выхода и системный прокси).
/// </summary>
public sealed class DpiGeneralVm : ObservableObject
{
    /// <summary>Переключатель «Общих» — раздел сохраняет настройки и перезапускает движок.</summary>
    public Action? Toggled { get; set; }

    public string Name => L.T("dpi.general");

    /// <summary>«Общие» применяются ко всему трафику; выключены — приёмы только у сервисов.</summary>
    public bool Enabled
    {
        get => App.Settings.Current.DpiServices.GeneralEnabled;
        set
        {
            var section = App.Settings.Current.DpiServices;
            if (section.GeneralEnabled == value) return;
            section.GeneralEnabled = value;
            Raise();
            Raise(nameof(StateText));
            Raise(nameof(EnabledHint));
            Toggled?.Invoke();
        }
    }

    /// <summary>Набор приёмов «Общих» одной строкой (или состояние, когда они выключены).</summary>
    public string StateText => Enabled
        ? DpiTester.Describe(App.Settings.Current.Dpi)
        : L.T("dpi.generalOff");

    /// <summary>Что делает переключатель — подсказка у него.</summary>
    public string EnabledHint => Enabled ? L.T("dpi.generalOnHint") : L.T("dpi.generalOffHint");

    /// <summary>Выбранный DNS «Общих» (пусто — как у обхода в целом).</summary>
    public string DnsText
    {
        get
        {
            var text = DpiServiceCatalog.DnsTextFor(App.Settings.Current.DpiServices.GeneralDns, App.Settings.Current.Dns);
            return text.Length > 0 ? L.F("dpi.dns.chosen", text) : L.T("dpi.dns.none");
        }
    }

    public bool HasDns => DpiServiceCatalog
        .DnsTextFor(App.Settings.Current.DpiServices.GeneralDns, App.Settings.Current.Dns).Length > 0;

    /// <summary>Подпись строки: домены не нужны, «Общие» работают со всем трафиком.</summary>
    public string DomainText => L.T("dpi.generalScope");

    /// <summary>Настройки «Общих» поменялись — строку надо пересобрать (в том числе после смены языка).</summary>
    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(DomainText));
        Raise(nameof(Enabled));
        Raise(nameof(StateText));
        Raise(nameof(DnsText));
        Raise(nameof(HasDns));
        Raise(nameof(EnabledHint));
    }
}

/// <summary>
/// Профиль настроек обхода в списке профилей (окно сервиса и подписи строк): имя, набор приёмов
/// одной строкой и признак «подобран автоматически».
/// </summary>
public sealed class DpiProfileVm : ObservableObject
{
    private bool _isSelected;

    public DpiProfileVm(DpiStrategyProfile model) => Model = model;

    public DpiStrategyProfile Model { get; }

    public string Id => Model.Id;

    public string Name
    {
        get => Model.DisplayName;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || Model.DisplayName == trimmed) return;

            // пользователь переименовал встроенный профиль — дальше показываем введённое имя
            Model.Name = trimmed;
            Model.NameKey = null;
            Raise();
        }
    }

    /// <summary>Профиль подобран кнопкой «Подобрать» и перезаписывается при следующем подборе.</summary>
    public bool IsAuto => Model.Auto;

    /// <summary>Встроенный профиль: удалить нельзя (можно только скопировать и править копию).</summary>
    public bool IsBuiltIn => Model.BuiltIn;

    /// <summary>Набор приёмов одной строкой — как в описании теста.</summary>
    public string Summary => DpiTester.Describe(Model.Settings);

    /// <summary>Пояснение к набору приёмов (у встроенных профилей — переводимый ключ).</summary>
    public string NoteText => Model.Note is { Length: > 0 } note ? L.Text(note) : "";

    public bool HasNote => NoteText.Length > 0;

    /// <summary>Сервисы, которые пользуются этим профилем.</summary>
    private int _usage;

    public string UsageText => _usage > 0 ? L.F("dpi.profileUsage", _usage) : "";

    public bool InUse => _usage > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void SetUsage(int count)
    {
        if (_usage == count) return;
        _usage = count;
        Raise(nameof(UsageText));
        Raise(nameof(InUse));
    }

    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(Summary));
        Raise(nameof(IsAuto));
        Raise(nameof(IsBuiltIn));
        Raise(nameof(NoteText));
        Raise(nameof(HasNote));
    }
}
