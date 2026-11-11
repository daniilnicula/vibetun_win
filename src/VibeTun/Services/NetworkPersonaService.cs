using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>
/// Сетевой облик приложения: User-Agent, которым подписаны все исходящие HTTP-запросы.
/// Режим (настройка PersonaMode): auto — свежий облик раз в сутки из официальных источников
/// (Google для Chrome, Mozilla для Firefox); chrome/firefox/edge — фиксированный браузер,
/// версия подтягивается по тому же механизму; app — честный облик приложения без маскировки
/// (<see cref="ConstantsService.AppUserAgent"/>: сервер видит, что запрос делает VibeTun).
/// Результат кэшируется в data\ua.json; офлайн или сбой источника — кэш, затем запасная версия
/// из сборки. Сбои молчат: облик некритичен.
/// </summary>
public static class NetworkPersonaService
{
    private static readonly string CacheFile = Path.Combine(AppPaths.DataRoot, "ua.json");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    private sealed class PersonaCache
    {
        public DateTime AtUtc { get; set; }
        public string Source { get; set; } = "chrome";   // chrome | firefox — чей API дал версию
        public int Major { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly HttpClient Client = CreateClient();

    /// <summary>Текущий режим: auto | chrome | firefox | edge | app.</summary>
    public static string Mode { get; private set; } = "auto";

    /// <summary>Текущий User-Agent. До первой подгрузки — константа из сборки.</summary>
    public static string UserAgent { get; private set; } = ConstantsService.BrowserUserAgent;

    /// <summary>Когда облик в последний раз обновлялся из сети (UTC); null — ещё ни разу.</summary>
    public static DateTime? RefreshedAtUtc { get; private set; }

    /// <summary>
    /// Облик изменился (смена режима, свежая версия из сети). Может прийти из фонового потока —
    /// «Настройки» по нему обновляют строку «Обновлён» и подсказку с текущим заголовком.
    /// </summary>
    public static event Action? Changed;

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { Proxy = null, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

    /// <summary>
    /// Запрос версии браузера. Заголовок ставится на каждый запрос из текущего облика: у клиента
    /// нет собственного User-Agent, зашитого при создании, — иначе приложение светило бы двумя
    /// разными версиями браузера сразу.
    /// </summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return await Client.SendAsync(request, ct);
    }

    /// <summary>Точка входа при старте приложения: применяем сохранённый режим и тянем свежую версию, если протухла.</summary>
    public static void Initialize(string mode)
    {
        SetModeCore(mode);
        if (Mode == "app")
        {
            // без маскировки: заголовок называет приложение, версия браузера не нужна
            Set(ConstantsService.AppUserAgent, null);
            return;
        }

        var cached = ReadCache();
        if (cached is not null && TryBuildForMode(Mode, cached, out var ua))
        {
            Set(ua, cached.AtUtc);
            if (DateTime.UtcNow - cached.AtUtc < RefreshInterval) return;   // свежий — сеть не трогаем
        }
        else
        {
            // кэша под этот режим нет: до ответа сети — запасной облик выбранного браузера,
            // а не то, что осталось от прежнего режима (в том числе от «без маскировки»)
            Set(FallbackFor(Mode), null);
        }
        _ = RefreshCoreAsync();
    }

    private static void Set(string userAgent, DateTime? refreshedAtUtc)
    {
        lock (Gate)
        {
            UserAgent = userAgent;
            RefreshedAtUtc = refreshedAtUtc;
        }
        try { Changed?.Invoke(); } catch { /* подписчик — интерфейс; облик от него не зависит */ }
    }

    private static string FallbackFor(string mode) =>
        string.Format(CultureInfo.InvariantCulture, TemplateFor(mode), ConstantsService.FallbackBrowserMajor);

    private static string TemplateFor(string mode) => mode switch
    {
        "firefox" => ConstantsService.FirefoxUaTemplate,
        "edge" => ConstantsService.EdgeUaTemplate,
        _ => ConstantsService.ChromeUaTemplate,
    };

    /// <summary>Режим изменён в настройках: применяем сразу и фоном тянем версию под новый режим.</summary>
    public static void SetMode(string mode) => Initialize(mode);

    private static void SetModeCore(string mode)
    {
        Mode = ConstantsService.PersonaModes.Contains(mode) ? mode : "auto";
    }

    /// <summary>Кнопка «Обновить сейчас» в настройках. True — облик обновлён из сети.</summary>
    public static async Task<bool> RefreshNowAsync(CancellationToken ct = default) => await RefreshCoreAsync(ct);

    private static async Task<bool> RefreshCoreAsync(CancellationToken ct = default)
    {
        // Режим запоминаем на входе: пока идёт запрос, его могли сменить в настройках, и ответ
        // под прежний режим применять уже нельзя (особенно поверх режима «без маскировки»).
        var mode = Mode;
        if (mode == "app") return false;   // без маскировки версия не нужна
        try
        {
            // auto: Google, затем Mozilla; фиксированный режим — только свой источник
            if (mode is "auto" or "chrome" or "edge")
            {
                var chromeMajor = await TryFetchChromeMajorAsync(ct);
                if (chromeMajor is not null) return Apply(mode, "chrome", chromeMajor.Value);
            }
            if (mode is "auto" or "firefox")
            {
                var firefoxMajor = await TryFetchFirefoxMajorAsync(ct);
                if (firefoxMajor is not null) return Apply(mode, "firefox", firefoxMajor.Value);
            }
        }
        catch { /* облик некритичен: остаёмся на прежнем значении */ }

        // сеть/источники недоступны: поднимаем последний кэш (запасная версия уже выставлена)
        var cached = ReadCache();
        if (mode == Mode && cached is not null && TryBuildForMode(mode, cached, out var ua))
            Set(ua, cached.AtUtc);
        return false;
    }

    // Chrome: официальный API истории версий Google, stable-канал под Windows.
    // Edge синхронизирует мажорную версию с Chromium, поэтому этот источник подходит и ему.
    // В списке несколько стабильных версий — берём самую свежую мажорную.
    private static async Task<int?> TryFetchChromeMajorAsync(CancellationToken ct)
    {
        using var resp = await GetAsync(
            "https://versionhistory.googleapis.com/v1/chrome/platforms/win/channels/stable/versions", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        int? best = null;
        foreach (var item in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            var major = ParseMajor(item.GetProperty("version").GetString());
            if (major is not null && (best is null || major > best)) best = major;
        }
        return best;
    }

    // Firefox: официальный JSON с версиями Mozilla.
    private static async Task<int?> TryFetchFirefoxMajorAsync(CancellationToken ct)
    {
        using var resp = await GetAsync("https://product-details.mozilla.org/1.0/firefox_versions.json", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var version = doc.RootElement.GetProperty("LATEST_FIREFOX_VERSION").GetString();
        return ParseMajor(version);
    }

    private static int? ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var dot = version.IndexOf('.');
        var head = dot > 0 ? version[..dot] : version;
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    private static bool Apply(string mode, string source, int major)
    {
        var cache = new PersonaCache { AtUtc = DateTime.UtcNow, Source = source, Major = major };
        if (!TryBuildForMode(mode, cache, out var ua)) return false;

        // версия получена честно — кэш пригодится и новому режиму, даже если этот уже сменили
        WriteCache(cache);
        if (mode != Mode) return false;

        Set(ua, cache.AtUtc);
        return true;
    }

    private static bool TryBuildForMode(string mode, PersonaCache cache, out string ua)
    {
        ua = "";
        // версия из кэша подходит режиму, если её источник совпадает с источником режима;
        // в auto подходит любая; edge строится по версии Chromium
        var sourceOk = mode switch
        {
            "chrome" => cache.Source == "chrome",
            "edge" => cache.Source == "chrome",
            "firefox" => cache.Source == "firefox",
            _ => true,
        };
        if (!sourceOk || cache.Major <= 0) return false;

        // В auto шаблон берётся по источнику версии: номер от Mozilla — это версия Firefox, и
        // подставлять его в заголовок Chrome нельзя (вышла бы версия Chrome, которой ещё нет).
        var template = mode == "auto" ? TemplateFor(cache.Source) : TemplateFor(mode);
        ua = string.Format(CultureInfo.InvariantCulture, template, cache.Major);
        return true;
    }

    private static PersonaCache? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            return Json.Read<PersonaCache>(CacheFile);
        }
        catch { return null; }
    }

    /// <summary>Кэш пишем с UTF-8 BOM — чтобы Notepad открывал файл без искажений.</summary>
    private static void WriteCache(PersonaCache cache)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var json = JsonSerializer.Serialize(cache, Json.Options);
            File.WriteAllText(CacheFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch { /* кэш не записался — на следующем запуске подтянем снова */ }
    }
}
