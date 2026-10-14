namespace VibeTun.Models;

/// <summary>Какой DNS получает ядро — значения <see cref="DnsSettings.Mode"/>.</summary>
public static class DnsModes
{
    /// <summary>«Системный»: резолвер Windows (для WireGuard/AmneziaWG — DNS из конфига профиля в туннеле).</summary>
    public const string System = "system";

    /// <summary>«Через Proxy»: DNS-over-TLS 1.1.1.1 через активный профиль.</summary>
    public const string Proxy = "proxy";

    /// <summary>Конкретный сервер из списка: адрес провайдера или свой DNS.</summary>
    public const string Server = "server";
}

/// <summary>
/// Раздел «DNS» (хранится в settings.json): что выбрано и список провайдеров и своих DNS.
/// Ядру уходит только выбранный адрес — см. <see cref="SelectedServer"/>.
/// </summary>
public sealed class DnsSettings
{
    /// <summary>system | proxy | server (см. <see cref="DnsModes"/>).</summary>
    public string Mode { get; set; } = DnsModes.System;

    /// <summary>Запись списка, из которой выбран сервер (провайдер или свой DNS).</summary>
    public string? SelectedEntryId { get; set; }

    /// <summary>Адрес выбранного сервера — именно он попадает в конфиг ядра (IP или tls://, https://…).</summary>
    public string? SelectedServer { get; set; }

    /// <summary>Провайдеры и свои DNS в порядке списка; порядок меняется перетаскиванием.</summary>
    public List<DnsEntry> Entries { get; set; } = new();

    /// <summary>
    /// Раздел уже настроен. В settings.json старых версий блока нет: тогда выбор один раз
    /// переносится из профиля маршрутизации, где DNS жил раньше (режим и свой сервер).
    /// </summary>
    public bool Initialized { get; set; }

    /// <summary>Домены со своим DNS (вкладка «Домены»); сверху — добавленные последними.</summary>
    public List<DnsDomainRule> Domains { get; set; } = new();

    /// <summary>Приложения со своим DNS (вкладка «Приложения»).</summary>
    public List<DnsAppRule> Apps { get; set; } = new();
}

/// <summary>
/// Свой DNS записи вкладок «Домены» и «Приложения»: что выбрано для неё в основном списке.
/// Режим пустой — DNS ещё не выбран, запись работает через основной.
/// </summary>
public interface IDnsChoice
{
    string Id { get; }

    /// <summary>Правило включено (галочка в карточке).</summary>
    bool Enabled { get; set; }

    /// <summary>Пусто — DNS не выбран; иначе как у <see cref="DnsSettings.Mode"/>: system | proxy | server.</summary>
    string Mode { get; set; }

    /// <summary>Запись списка, из которой выбран сервер (провайдер или свой DNS).</summary>
    string? EntryId { get; set; }

    /// <summary>Адрес выбранного сервера — он и попадает в конфиг ядра.</summary>
    string? Server { get; set; }
}

/// <summary>
/// Домен со своим DNS: DNS-запросы к нему и прямые подключения к нему идут через выбранный здесь DNS
/// вместо основного. Запись — домен («youtube.com», «.ru», «*.example.org») или regex («youtube»,
/// «regex:^ads\.»); тип определяется так же, как в маршрутизации (см. <c>Utils.DnsPattern</c>).
/// </summary>
public sealed class DnsDomainRule : IDnsChoice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Что ввёл пользователь (адрес сайта уже обрезан до имени домена).</summary>
    public string Pattern { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Пусто — DNS ещё не выбран; иначе system | proxy | server.</summary>
    public string Mode { get; set; } = "";

    public string? EntryId { get; set; }

    public string? Server { get; set; }
}

/// <summary>
/// Приложение со своим DNS: его DNS-запросы и прямые подключения идут через выбранный здесь
/// DNS вместо основного. DNS выбирается из основного списка — так же, как основной.
/// </summary>
public sealed class DnsAppRule : IDnsChoice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Полный путь к .exe.</summary>
    public string Path { get; set; } = "";

    /// <summary>Имя процесса без расширения (discord, steam, ...).</summary>
    public string ProcessName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Пусто — DNS ещё не выбран (приложение работает через основной);
    /// иначе как у <see cref="DnsSettings.Mode"/>: system | proxy | server.
    /// </summary>
    public string Mode { get; set; } = "";

    /// <summary>Запись списка, из которой выбран сервер (провайдер или свой DNS).</summary>
    public string? EntryId { get; set; }

    /// <summary>Адрес выбранного сервера — он и попадает в конфиг ядра.</summary>
    public string? Server { get; set; }
}

/// <summary>
/// Свой DNS приложения — то, что получает сборщик конфига ядра (<c>SingBoxConfigBuilder</c>).
/// <paramref name="DnsMode"/> — как у <see cref="RouteProfile.DnsMode"/>: system | proxy | custom.
/// </summary>
public sealed record AppDnsRoute(string ProcessName, string Path, string DnsMode, string? DnsServer);

/// <summary>
/// Свой DNS домена — для сборщика конфига ядра. <paramref name="Pattern"/> — запись как её ввёл
/// пользователь (поле sing-box из неё получает сборщик); <paramref name="DnsMode"/> — system | proxy | custom.
/// </summary>
public sealed record DomainDnsRoute(string Pattern, string DnsMode, string? DnsServer);

/// <summary>Строка списка DNS: провайдер (адреса серверов — из справочника или DNS-запросом) или свой сервер.</summary>
public sealed class DnsEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>
    /// Встроенный провайдер: его серверы — из справочника приложения или ответ на DNS-запрос
    /// к его именам. Провайдера нельзя удалить или изменить, только выбрать один из серверов.
    /// </summary>
    public bool IsProvider { get; set; }

    /// <summary>
    /// Устарело: имя провайдера из прежних версий (одно на провайдера). Теперь имена для запроса и
    /// постоянные адреса — в справочнике приложения (DnsProviders); при загрузке поле очищается.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>Свой DNS: IP или адрес вида tls://, https://.</summary>
    public string? Address { get; set; }

    public bool Favorite { get; set; }

    /// <summary>Провайдер раскрыт в списке (состояние переживает перезапуск).</summary>
    public bool Expanded { get; set; }

    /// <summary>
    /// Последний полученный список серверов провайдера, чьи адреса спрашиваем у него самого, —
    /// показываем сразу, без нового запроса. У провайдеров с постоянными адресами пусто: они в справочнике.
    /// </summary>
    public List<string> Servers { get; set; } = new();

    /// <summary>Подписи серверов из того же ответа: адрес → регион выхода (например «EU»).</summary>
    public Dictionary<string, string>? ServerLabels { get; set; }

    /// <summary>Когда получен список серверов (UTC).</summary>
    public DateTime? ServersUpdatedAt { get; set; }

    /// <summary>Откуда получен список: системный DNS или DoH.</summary>
    public string? ServersSource { get; set; }
}
