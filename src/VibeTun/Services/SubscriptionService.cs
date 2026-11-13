using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibeTun.Models;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>
/// Загруженная подписка. <see cref="Name"/> всегда заполнено: это название из заголовка Profile-Title,
/// а если панель его не прислала — хост ссылки. <see cref="Title"/> — только название от самой панели
/// (null, когда его нет): по нему предлагается имя новой группы, чтобы там не оказался IP-адрес.
/// </summary>
public sealed record SubscriptionFetchResult(string Name, List<ProxyProfile> Profiles, List<string> Errors)
{
    public string? Title { get; init; }

    /// <summary>Итоговый формат ответа после попытки вылечить JSON в ссылки: "links" | "json".</summary>
    public string Format { get; init; } = "links";
}

/// <summary>Загрузка стандартных URI/JSON-подписок, в том числе форматов 3x-ui.</summary>
public sealed class SubscriptionService
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Текст — ссылка на подписку: одна строка http(s)-адреса с непустым путём (например,
    /// 3x-ui: /&lt;sub-path&gt;/&lt;sub-id&gt;). Адрес без пути — http(s)://user:pass@proxy:port —
    /// это запись HTTP-прокси, а не подписка. Одно правило для окна добавления и для самого импорта.
    /// </summary>
    public static bool TryGetSubscriptionUrl(string? text, out string url)
    {
        url = text?.Trim() ?? "";
        if (url.Length == 0 || url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        return uri.AbsolutePath.Trim('/').Length > 0;
    }

    public async Task<SubscriptionFetchResult> FetchAsync(string address, CancellationToken cancellationToken = default)
    {
        var url = address?.Trim() ?? "";
        if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Enter a valid HTTP or HTTPS subscription URL.", nameof(address));

        Exception? lastError = null;

        // Пробуем через системный прокси, затем напрямую (как AppUpdateService.FetchWindowsReleasesAsync).
        // Через прокси — когда наш прокси включён, заодно обходятся блокировки панели подписки;
        // напрямую — когда системный прокси залип на нашем же остановленном входе (127.0.0.0:порт
        // после падения) или панель доступна только без прокси.
        foreach (var useProxy in new[] { true, false })
        {
            try
            {
                var (content, title) = await FetchStringAsync(uri, NetworkPersonaService.UserAgent, useProxy, cancellationToken);
                // Если пришёл JSON — лечим в ссылки: в JSON-подписке 3x-ui tuic/amneziawg
                // пропускаются панелью, из-за чего эти профили «не цеплялись»
                content = await HealJsonToLinksAsync(uri, content, cancellationToken);
                var parsed = ParseContent(content);
                if (parsed.Profiles.Count == 0)
                {
                    var detail = parsed.Errors.FirstOrDefault();
                    throw new InvalidDataException(string.IsNullOrWhiteSpace(detail)
                        ? "The response did not contain supported proxy profiles."
                        : $"The response did not contain supported proxy profiles: {detail}");
                }

                return new SubscriptionFetchResult(title ?? uri.Host, parsed.Profiles, parsed.Errors)
                {
                    Title = title,
                    Format = IsJsonPayload(content) ? "json" : "links"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                // ответ получен и разобран: повтор напрямую вернёт то же самое — не гоняем второй раз
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException("Subscription request failed.");
    }

    /// <summary>Один HTTP-GET подписки: тело ответа и заголовок Profile-Title.</summary>
    private static async Task<(string Content, string? Title)> FetchStringAsync(
        Uri uri, string userAgent, bool useProxy, CancellationToken cancellationToken)
    {
        using var client = CreateClient(useProxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // сетевой облик — общий браузерный UA, чтобы сервер подписки не видел VPN-клиента
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Subscription server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

        var content = await ReadContentAsync(response.Content, cancellationToken);
        return (content, ReadTitle(response));
    }

    /// <summary>
    /// Ответ — JSON, а нужны ссылки: панель 3x-ui пропускает tuic/amneziawg в JSON-подписке,
    /// поэтому эти профили оттуда не импортируются. Пробуем получить ссылочный вариант:
    /// /json/&lt;id&gt; → /sub/&lt;id&gt;, затем тот же адрес с чужим UA — админский JSON-regex
    /// обычно настроен под браузер, curl ему не подходит. Возвращает не-JSON, из которого
    /// реально собираются профили; иначе исходный ответ.
    /// </summary>
    private static async Task<string> HealJsonToLinksAsync(Uri primary, string content, CancellationToken cancellationToken)
    {
        if (!IsJsonPayload(content)) return content;

        var candidates = new List<(string Url, string UserAgent)>();
        var path = primary.AbsolutePath;
        if (path.StartsWith("/json/", StringComparison.OrdinalIgnoreCase))
        {
            var swapped = new UriBuilder(primary) { Path = "/sub/" + path["/json/".Length..] };
            candidates.Add((swapped.Uri.AbsoluteUri, NetworkPersonaService.UserAgent));
        }
        candidates.Add((primary.AbsoluteUri, "curl/8.5.0"));

        foreach (var (candidateUrl, candidateUserAgent) in candidates)
        {
            if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidateUri)) continue;
            foreach (var useProxy in new[] { true, false })
            {
                try
                {
                    var (alt, _) = await FetchStringAsync(candidateUri, candidateUserAgent, useProxy, cancellationToken);
                    // принимаем только ответ, из которого собираются профили и это не JSON
                    // (HTML-страница панели или Clash YAML так не пройдут)
                    if (!IsJsonPayload(alt) && ParseContent(alt).Profiles.Count > 0) return alt;
                    break; // ответ получен — повтор через другой прокси не нужен
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // прокси не помог — пробуем напрямую
                }
            }
        }
        return content;
    }

    /// <summary>Текст подписки после снятия base64-обёртки — общая нормализация для разбора и определения формата.</summary>
    private static string NormalizePayload(string content)
    {
        var text = (content ?? "").Trim().TrimStart('\uFEFF').Trim();
        if (text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) && Text.TryBase64(text[7..], out var prefixed))
            return prefixed.TrimStart('\uFEFF').Trim();
        if (Text.IsBase64Blob(text) && Text.TryBase64(text, out var decoded) && LooksLikeProfilePayload(decoded))
            return decoded.TrimStart('\uFEFF').Trim();
        return text;
    }

    private static bool IsJsonPayload(string content)
    {
        var text = NormalizePayload(content);
        return text.StartsWith('[') || text.StartsWith('{');
    }

    private static HttpClient CreateClient(bool useSystemProxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseProxy = useSystemProxy
        };
        if (!useSystemProxy) handler.Proxy = null;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static async Task<string> ReadContentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("The subscription response is too large.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("The subscription response is too large.");
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch (ArgumentException) { /* fall back to UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static ParseResult ParseContent(string content)
    {
        var text = NormalizePayload(content);

        if (text.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    // JSON-подписка: массив конфигов (3x-ui) или outbound'ов — все профили сразу
                    var profiles = JsonConfigConverter.ConvertAll(document.RootElement, out var errors);
                    return new ParseResult(profiles, errors);
                }
            }
            catch (JsonException)
            {
                // Let the normal parser produce the familiar import error.
            }
        }

        return LinkParser.ParseMany(text);
    }

    private static bool LooksLikeProfilePayload(string text)
    {
        var value = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return value.Contains("://", StringComparison.Ordinal) || value.StartsWith('{') || value.StartsWith('[') ||
               value.Contains("[Interface]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Название подписки из заголовка Profile-Title; null — панель его не прислала.</summary>
    private static string? ReadTitle(HttpResponseMessage response)
    {
        var value = response.Headers.TryGetValues("Profile-Title", out var responseValues)
            ? responseValues.FirstOrDefault()
            : null;
        value ??= response.Content.Headers.TryGetValues("Profile-Title", out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) && Text.TryBase64(value[7..], out var decoded))
            value = decoded.Trim();
        else
        {
            try { value = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { /* keep the header as-is */ }
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
