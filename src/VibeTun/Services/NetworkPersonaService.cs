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
/// версия подтягивается по тому же механизму; app — честный облик приложения без маскировки.
/// Результат кэшируется в data\ua.json; офлайн или сбой источника — кэш, затем константа
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // и сами запросы версий идут под текущим обликом
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ConstantsService.BrowserUserAgent);
        return client;
    }

    /// <summary>Точка входа при старте приложения: применяем сохранённый режим и тянем свежую версию, если протухла.</summary>
    public static void Initialize(string mode)
    {
        SetModeCore(mode);
        if (Mode == "app") return;
        var cached = ReadCache();
        if (cached is not null && TryBuildForMode(Mode, cached, out var ua))
        {
            lock (Gate)
            {
                UserAgent = ua;
                RefreshedAtUtc = cached.AtUtc;
            }
            if (DateTime.UtcNow - cached.AtUtc < RefreshInterval) return;   // свежий — сеть не трогаем
        }
        _ = RefreshCoreAsync();
    }

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
        if (Mode == "app") return false;   // без маскировки версия не нужна
        try
        {
            // auto: Google, затем Mozilla; фиксированный режим — только свой источник
            if (Mode is "auto" or "chrome" or "edge")
            {
                var chromeMajor = await TryFetchChromeMajorAsync(ct);
                if (chromeMajor is not null) return Apply("chrome", chromeMajor.Value);
            }
            if (Mode is "auto" or "firefox")
            {
                var firefoxMajor = await TryFetchFirefoxMajorAsync(ct);
                if (firefoxMajor is not null) return Apply("firefox", firefoxMajor.Value);
            }
        }
        catch { /* облик некритичен: остаёмся на прежнем значении */ }

        // сеть/источники недоступны: поднимаем последний кэш (константа — запасной вариант)
        var cached = ReadCache();
        if (cached is not null && TryBuildForMode(Mode, cached, out var ua))
        {
            lock (Gate)
            {
                UserAgent = ua;
                RefreshedAtUtc = cached.AtUtc;
            }
        }
        return false;
    }

    // Chrome: официальный API истории версий Google, stable-канал под Windows.
    // Edge синхронизирует мажорную версию с Chromium, поэтому этот источник подходит и ему.
    // В списке несколько стабильных версий — берём самую свежую мажорную.
    private static async Task<int?> TryFetchChromeMajorAsync(CancellationToken ct)
    {
        using var resp = await Client.GetAsync(
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
        using var resp = await Client.GetAsync("https://product-details.mozilla.org/1.0/firefox_versions.json", ct);
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

    private static bool Apply(string source, int major)
    {
        var cache = new PersonaCache { AtUtc = DateTime.UtcNow, Source = source, Major = major };
        if (!TryBuildForMode(Mode, cache, out var ua)) return false;
        lock (Gate)
        {
            UserAgent = ua;
            RefreshedAtUtc = cache.AtUtc;
        }
        WriteCache(cache);
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

        var template = mode switch
        {
            "firefox" => ConstantsService.FirefoxUaTemplate,
            "edge" => ConstantsService.EdgeUaTemplate,
            _ => ConstantsService.ChromeUaTemplate,
        };
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
