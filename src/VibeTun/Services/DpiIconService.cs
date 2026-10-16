using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>
/// Значок сервиса для вкладки «DPI»: та же иконка, что браузер показывает слева от адреса страницы.
/// Значок скачивается у самого сайта (<c>/favicon.ico</c>), а если сайт его не отдаёт — из справочников
/// DuckDuckGo и Google. Файл кладётся в <c>&lt;данные&gt;/dpi-icons/&lt;домен&gt;.ico</c>, поэтому
/// второй раз он берётся с диска и без интернета.
/// </summary>
public static class DpiIconService
{
    /// <summary>Сколько ждём значок: он нужен для списка, задерживать раздел из-за него нельзя.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Значки за сессию; null — скачать не удалось (повторяем только по кнопке).</summary>
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string IconsDir => Path.Combine(AppPaths.DataRoot, "dpi-icons");

    /// <summary>Значок, который уже лежит в кэше (файл скачивали раньше). Список показывает его сразу.</summary>
    public static ImageSource? FromCache(string domain)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return null;
        if (Cache.TryGetValue(host, out var image)) return image;

        var path = FileFor(host);
        if (!File.Exists(path)) return null;

        var loaded = Decode(ReadAll(path));
        Cache[host] = loaded;
        return loaded;
    }

    /// <summary>Скачать значок сервиса (или взять готовый файл). null — не получилось.</summary>
    public static async Task<ImageSource?> GetAsync(string domain, bool force = false, CancellationToken ct = default)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return null;

        if (!force && Cache.TryGetValue(host, out var cached)) return cached;

        var path = FileFor(host);
        var bytes = File.Exists(path) && !force ? ReadAll(path) : await DownloadAsync(host, ct);

        var image = Decode(bytes);
        if (image is not null && bytes is { Length: > 0 } && !File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(IconsDir);
                File.WriteAllBytes(path, bytes);
            }
            catch { /* значок — украшение: без файла он просто скачается заново */ }
        }

        Cache[host] = image;
        return image;
    }

    /// <summary>Забыть значок: домен сервиса изменился или пользователь просит скачать заново.</summary>
    public static void Forget(string domain)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return;

        Cache.Remove(host);
        try
        {
            var path = FileFor(host);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }

    private static string FileFor(string host) => Path.Combine(IconsDir, AppPaths.Slug(host) + ".ico");

    private static byte[] ReadAll(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// Три источника по очереди: сам сайт, справочник DuckDuckGo, справочник Google.
    /// Сайт может быть закрыт DPI — тогда срабатывает справочник, он на другом домене.
    /// </summary>
    private static async Task<byte[]> DownloadAsync(string host, CancellationToken ct)
    {
        foreach (var url in Sources(host))
        {
            try
            {
                var response = await Http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0 && bytes.Length < 512 * 1024) return bytes;
            }
            catch { /* источник не ответил — пробуем следующий */ }
        }

        return Array.Empty<byte>();
    }

    private static IEnumerable<string> Sources(string host)
    {
        yield return $"https://{host}/favicon.ico";
        yield return $"https://icons.duckduckgo.com/ip3/{host}.ico";
        yield return $"https://www.google.com/s2/favicons?domain={host}&sz=64";
    }

    /// <summary>Байты → значок. Картинка замораживается: её можно отдать в привязку из любого потока.</summary>
    private static ImageSource? Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;   // не картинка (html страницы ошибки) — покажем общий значок
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler) { Timeout = Timeout };
        // часть сайтов отдаёт значок только «настоящему» браузеру
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/png,image/svg+xml,*/*");
        return client;
    }
}
