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

public sealed record SubscriptionFetchResult(string Name, List<ProxyProfile> Profiles, List<string> Errors);

/// <summary>Загрузка стандартных URI/JSON-подписок, в том числе форматов 3x-ui.</summary>
public sealed class SubscriptionService
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    public async Task<SubscriptionFetchResult> FetchAsync(string address, CancellationToken cancellationToken = default)
    {
        var url = address?.Trim() ?? "";
        if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Enter a valid HTTP or HTTPS subscription URL.", nameof(address));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VibeTun/1.2.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Subscription server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

        var content = await ReadContentAsync(response.Content, cancellationToken);
        var parsed = ParseContent(content);
        if (parsed.Profiles.Count == 0)
        {
            var detail = parsed.Errors.FirstOrDefault();
            throw new InvalidDataException(string.IsNullOrWhiteSpace(detail)
                ? "The response did not contain supported proxy profiles."
                : $"The response did not contain supported proxy profiles: {detail}");
        }

        return new SubscriptionFetchResult(ReadTitle(response, uri), parsed.Profiles, parsed.Errors);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
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
        var text = (content ?? "").Trim().TrimStart('\uFEFF').Trim();
        if (text.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) && Text.TryBase64(text[7..], out var prefixed))
            text = prefixed.TrimStart('\uFEFF').Trim();
        else if (Text.IsBase64Blob(text) && Text.TryBase64(text, out var decoded) && LooksLikeProfilePayload(decoded))
            text = decoded.TrimStart('\uFEFF').Trim();

        if (text.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var profiles = new List<ProxyProfile>();
                    var errors = new List<string>();
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add("A JSON subscription entry is not an object.");
                            continue;
                        }
                        var profile = JsonConfigConverter.Convert(item, item.GetRawText(), out var error);
                        if (profile is not null) profiles.Add(profile);
                        else if (!string.IsNullOrWhiteSpace(error)) errors.Add(error!);
                    }
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

    private static string ReadTitle(HttpResponseMessage response, Uri uri)
    {
        var value = response.Headers.TryGetValues("Profile-Title", out var responseValues)
            ? responseValues.FirstOrDefault()
            : null;
        value ??= response.Content.Headers.TryGetValues("Profile-Title", out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return uri.Host;

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) && Text.TryBase64(value[7..], out var decoded))
            value = decoded.Trim();
        else
        {
            try { value = Uri.UnescapeDataString(value); }
            catch (UriFormatException) { /* keep the header as-is */ }
        }

        return string.IsNullOrWhiteSpace(value) ? uri.Host : value;
    }
}
