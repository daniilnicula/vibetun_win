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
    // из официальных источников; эта строка — запасной вариант на случай полного офлайна.
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    /// <summary>Шаблоны облика под актуальную мажорную версию браузера ({0} — мажорная версия).</summary>
    public const string ChromeUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.0.0 Safari/537.36";
    public const string FirefoxUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{0}.0) Gecko/20100101 Firefox/{0}.0";
    // Edge синхронизирует мажорную версию с Chromium — версию берём из того же источника, что у Chrome.
    public const string EdgeUaTemplate =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0}.0.0.0 Safari/537.36 Edg/{0}.0.0.0";

    /// <summary>Честный облик приложения — режим «без маскировки».</summary>
    public const string AppUserAgent = "VibeTun/1.0";

    /// <summary>Доступные режимы маскировки (значение настройки PersonaMode).</summary>
    public static readonly string[] PersonaModes = ["auto", "chrome", "firefox", "edge", "app"];

    /// <summary>Accept для API GitHub (резервная лента релизов и скачивание ядра).</summary>
    public const string GithubAccept = "application/vnd.github+json";

    // ----------------------------------------------------------- GitHub: репозитории
    /// <summary>Репозиторий самого приложения (релизы и обновления).</summary>
    public const string AppRepo = "daniilnicula/vibetun_win";

    /// <summary>Официальный sing-box.</summary>
    public const string SingBoxRepo = "SagerNet/sing-box";

    /// <summary>Расширенная сборка sing-box (AmneziaWG и прочие расширения).</summary>
    public const string SingBoxExtendedRepo = "shtorm-7/sing-box-extended";

    /// <summary>Форк с полной поддержкой AmneziaWG 2.0/3.x (random_trailers, disable_cookies).</summary>
    public const string AwgRepo = "Leadaxe/sing-box-lx";

    public const string GithubApiLatestReleaseUrl = "https://api.github.com/repos/{0}/releases/latest";

    // ----------------------------------------------------------- фильтры ассетов релизов
    /// <summary>Имя основного ассета приложения в релизе.</summary>
    public const string PreferredAssetName = "VibeTun.exe";

    /// <summary>Подстроки в имени ассета, говорящие, что он не под Windows.</summary>
    public static readonly string[] NonWindowsAssetKeywords = ["android", "linux", "darwin", "macos"];
}
