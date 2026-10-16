using System.Text.Json.Serialization;

namespace VibeTun.Models;

/// <summary>
/// Именованный профиль настроек обхода DPI: набор приёмов десинхронизации под конкретный сервис
/// или под всю сеть. Профили можно создавать, переименовывать, удалять, экспортировать и
/// импортировать — они лежат прямо в <c>settings.json</c> и переносятся файлом JSON.
/// </summary>
public sealed class DpiStrategyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Имя профиля. У встроенных пусто — вместо него подставляется <see cref="NameKey"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Ключ локализации имени встроенного профиля. Пользователь переименовал профиль — ключ очищается
    /// и дальше показывается <see cref="Name"/>.
    /// </summary>
    public string? NameKey { get; set; }

    /// <summary>Подбор сделан кнопкой «Подобрать» — профиль перезаписывается при следующем подборе.</summary>
    public bool Auto { get; set; }

    /// <summary>
    /// Домен сервиса, для которого профиль найден подбором: по нему движок собирает запасные
    /// стратегии для этого домена (если первая не откроет поток, попробует следующую).
    /// </summary>
    public string Domain { get; set; } = "";

    /// <summary>Порядок применения: меньше — раньше. Первый профиль сервиса пробуется первым.</summary>
    public int Rank { get; set; }

    /// <summary>Встроенный профиль: его можно изменить копией, но не удалить из списка.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Чем этот набор приёмов отличается — подсказка в списке профилей.</summary>
    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Настройки десинхронизации; транспортные поля берутся из общего раздела «Обход DPI».</summary>
    public DpiBypassSettings Settings { get; set; } = new();

    /// <summary>Имя для интерфейса: у встроенных переводится, у пользовательских — как ввёл пользователь.</summary>
    [JsonIgnore]
    public string DisplayName => NameKey is { Length: > 0 } key ? L.T(key) : Name;

    public DpiStrategyProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        Auto = Auto,
        Domain = Domain,
        Rank = Rank,
        BuiltIn = BuiltIn,
        Note = Note,
        UpdatedAt = UpdatedAt,
        Settings = Settings.Clone()
    };

    /// <summary>Копия для правки: новый Id (профиль-дубликат) или тот же (правка на месте).</summary>
    public DpiStrategyProfile Copy(bool newId)
    {
        var copy = Clone();
        if (newId) copy.Id = Guid.NewGuid().ToString("N");
        copy.NameKey = null;
        copy.BuiltIn = false;
        return copy;
    }
}

/// <summary>
/// Сервис во вкладке «DPI»: домен, для которого применяется свой профиль обхода, и его состояние
/// (включён, избранное, последний пинг). Аналог отдельного bat-файла zapret со своим hostlist:
/// у каждого сервиса свой набор приёмов.
/// </summary>
public sealed class DpiService : INavEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Подпись сервиса (Discord, YouTube, …).</summary>
    public string Name { get; set; } = "";

    /// <summary>Домен, по которому сервис узнаётся: «discord.com», «youtube.com», «*.discord.media».</summary>
    public string Domain { get; set; } = "";

    /// <summary>Выключенный сервис остаётся в списке, но его профиль к трафику не применяется.</summary>
    public bool Enabled { get; set; } = true;

    public bool Favorite { get; set; }

    /// <summary>Профиль настроек (<see cref="DpiStrategyProfile.Id"/>); null — работает общий набор.</summary>
    public string? ProfileId { get; set; }

    /// <summary>
    /// Свои настройки обхода — именно они применяются к трафику этого сервиса. Профиль выше играет
    /// роль источника: выбрали профиль — его набор приёмов скопирован сюда, дальше сервис правится
    /// независимо (сервисы не «дёргают» друг друга через общий профиль).
    /// Возвращаем экземпляр всегда: в старом settings.json поля может не быть.
    /// </summary>
    public DpiBypassSettings Settings
    {
        get => _settings ??= new DpiBypassSettings();
        set => _settings = value ?? new DpiBypassSettings();
    }

    private DpiBypassSettings? _settings;

    /// <summary>
    /// DNS сервиса: каким сервером спрашивать адреса его доменов. Выбирается в разделе «DNS»
    /// так же, как DNS для домена и приложения (см. <see cref="IDnsChoice"/>). Пусто — адреса
    /// берутся как у обхода в целом: системный DNS или DoH из раздела «DNS».
    /// </summary>
    public DpiDnsChoice Dns { get; set; } = new();

    /// <summary>Пинг сервиса сохраняем вместе с ним: в списке сразу видно прошлый результат.</summary>
    public DpiService CloneDeep()
    {
        var copy = (DpiService)MemberwiseClone();
        copy.Settings = Settings.Clone();
        copy.Dns = Dns.Clone();
        return copy;
    }

    /// <summary>Когда сервис проверяли последний раз.</summary>
    public DateTime? CheckedAt { get; set; }

    /// <summary>Последний замер доступности, мс; -1 — не ответил.</summary>
    public int LastPingMs { get; set; } = -1;

    /// <summary>Короткое пояснение последнего замера (для подсказки у «нет ответа»).</summary>
    public string? LastNote { get; set; }

    public DpiService Clone() => CloneDeep();
}

/// <summary>
/// Выбранный DNS сервиса раздела «DPI» и «Общих»: та же запись, что у домена и приложения
/// в разделе «DNS» (режим, провайдер и адрес сервера). Пустой режим — свой DNS не выбран.
/// </summary>
public sealed class DpiDnsChoice : IDnsChoice
{
    /// <summary>Идентификатор сервиса, которому принадлежит выбор («general» — «Общие»).</summary>
    public string Id { get; set; } = "";

    /// <summary>Правило включено. У сервиса это всегда true: работу решает его переключатель.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Пусто — DNS не выбран; иначе как у <see cref="DnsSettings.Mode"/>: system | proxy | server.</summary>
    public string Mode { get; set; } = "";

    /// <summary>Запись списка раздела «DNS», из которой выбран сервер (провайдер или свой DNS).</summary>
    public string? EntryId { get; set; }

    /// <summary>Адрес выбранного сервера — им движок обхода и спрашивает адреса доменов сервиса.</summary>
    public string? Server { get; set; }

    public DpiDnsChoice Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Mode = Mode,
        EntryId = EntryId,
        Server = Server
    };
}

/// <summary>
/// Раздел «DPI» в <c>settings.json</c>: сервисы и профили настроек обхода. Экземпляр есть всегда
/// (в старом файле блока нет) — как и <see cref="DnsSettings"/> у раздела «DNS».
/// </summary>
public sealed class DpiServices
{
    /// <summary>Идентификатор «Общих» — набора приёмов, который применяется ко всем сайтам.</summary>
    public const string GeneralId = "general";

    /// <summary>Сервисы в порядке пользователя; порядок меняется перетаскиванием.</summary>
    public List<DpiService> Services { get; set; } = new();

    /// <summary>Профили настроек обхода (встроенные + созданные пользователем).</summary>
    public List<DpiStrategyProfile> Profiles { get; set; } = new();

    /// <summary>
    /// «Общие» включены — набор приёмов применяется ко всему трафику. Выключены — приёмы
    /// применяются только к доменам добавленных сервисов, остальное идёт без десинхронизации.
    /// </summary>
    public bool GeneralEnabled { get; set; } = true;

    /// <summary>DNS «Общих»: каким сервером спрашивать адреса, когда работает общий набор приёмов.</summary>
    public DpiDnsChoice GeneralDns { get; set; } = new() { Id = GeneralId };

    /// <summary>Значения по умолчанию «Общих» уже применены (один раз при обновлении программы).</summary>
    public bool GeneralDefaults { get; set; }

    /// <summary>
    /// Раздел уже наполнялся: встроенные профили добавляются один раз, чтобы удалённые пользователем
    /// не возвращались при каждом запуске.
    /// </summary>
    public bool Initialized { get; set; }

    /// <summary>
    /// Версия набора встроенных профилей. Обновление программы добавляет только новые профили
    /// (и только один раз), поэтому правки и удаления пользователя не затираются.
    /// </summary>
    public int PresetsVersion { get; set; }
}
