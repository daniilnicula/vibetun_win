namespace VibeTun.Services;

/// <summary>
/// Единое место для констант приложения: сетевой облик запросов, репозитории GitHub,
/// фильтры ассетов релизов. Значения зашиты в сборку; правка — только здесь,
/// без дублирования по файлам.
/// </summary>
public static class ConstantsService
{
    // ----------------------------------------------------------- сетевой облик
    // Все исходящие HTTP-запросы маскируются под браузер: внешние серверы подписок
    // и DPI-посредники не должны видеть, что клиент — VPN/прокси-приложение.
    // Облик подтягивает NetworkPersonaService: версия браузера обновляется раз в сутки
    // из официальных источников. Версия ниже — запасной вариант на случай полного офлайна
    // и единственное место, где номер версии браузера зашит в сборку: сама строка User-Agent
    // всегда собирается из шаблонов ниже, второй захардкоженной строки с другой версией нет.
    public const int FallbackBrowserMajor = 124;

    /// <summary>Шаблоны облика под актуальную мажорную версию браузера ({0} — мажорная версия).</summary>
    public const string ChromeUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.0.0 Safari/537.36";
    public const string FirefoxUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0";
    // Edge синхронизирует мажорную версию с Chromium — версию берём из того же источника, что у Chrome.
    public const string EdgeUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.0.0 Safari/537.36 Edg/{0}.0.0.0";

    /// <summary>Запасной облик (Chrome) до первой подгрузки версии из сети.</summary>
    public static readonly string BrowserUserAgent =
        string.Format(System.Globalization.CultureInfo.InvariantCulture, ChromeUaTemplate, FallbackBrowserMajor);

    /// <summary>
    /// Честный облик приложения — режим «без маскировки». Заголовок прямо называет VPN-клиент,
    /// поэтому режим включается только явным выбором в настройках, и там же показано предупреждение.
    /// </summary>
    public const string AppUserAgent = "VibeTun/1.0";

    /// <summary>Доступные режимы маскировки (значение настройки PersonaMode).</summary>
    public static readonly string[] PersonaModes = ["auto", "chrome", "firefox", "edge", "app"];

    /// <summary>Accept для API GitHub (резервная лента релизов и скачивание ядра).</summary>
    public const string GithubAccept = "application/vnd.github+json";

    // ----------------------------------------------------------- GitHub: репозитории
    /// <summary>Репозиторий самого приложения (релизы и обновления).</summary>
    public const string AppRepo = "daniilnicula/vibetun_win";

    // (репозитории ядра — см. CoreRepo выше)

    /// <summary>
    /// Единственный репозиторий ядра: сюда сводятся все установки (официальная, расширенная, AWG) —
    /// у sing-box-lx есть готовые сборки под Windows и полная поддержка AmneziaWG 2.0/3.x.
    /// </summary>
    public const string CoreRepo = "Leadaxe/sing-box-lx";

    /// <summary>Совместимое имя: официальная сборка теперь берётся из <see cref="CoreRepo"/>.</summary>
    public const string SingBoxRepo = CoreRepo;

    /// <summary>Совместимое имя: расширенная сборка теперь берётся из <see cref="CoreRepo"/>.</summary>
    public const string SingBoxExtendedRepo = CoreRepo;

    /// <summary>Совместимое имя: AWG-форк — это тот же <see cref="CoreRepo"/>.</summary>
    public const string AwgRepo = CoreRepo;

    public const string GithubApiLatestReleaseUrl = "https://api.github.com/repos/{0}/releases/latest";

    // ----------------------------------------------------------- фильтры ассетов релизов
    /// <summary>Имя основного ассета приложения в релизе.</summary>
    public const string PreferredAssetName = "VibeTun.exe";

    /// <summary>Подстроки в имени ассета, говорящие, что он не под Windows.</summary>
    public static readonly string[] NonWindowsAssetKeywords = ["android", "linux", "darwin", "macos"];
}
