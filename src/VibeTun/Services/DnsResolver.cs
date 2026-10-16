using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>Одна dns-запись, какой её вернул сервер.</summary>
public sealed class DnsLookup
{
    public string Host = "";
    public List<IPAddress> Addresses = new();

    /// <summary>Откуда адреса: DoH (какой), системный DNS или кэш.</summary>
    public string Source = "";
    public string Error = "";
    public int TtlSeconds;

    public bool Ok => Addresses.Count > 0;

    /// <summary>Адреса уже попали в журнал — второй раз о том же не пишем.</summary>
    public bool Logged { get; set; }

    public string Text => Addresses.Count == 0
        ? (Error.Length > 0 ? Error : L.T("S0389"))
        : string.Join(", ", Addresses.Select(a => a.ToString()));

    /// <summary>Адреса в порядке попыток: сначала IPv4 — IPv6 часто есть в ответе, но не ездит.</summary>
    public List<IPAddress> InConnectOrder =>
        Addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Concat(Addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork))
            .ToList();
}

/// <summary>
/// Резолвер, который не зависит от системного DNS провайдера: адреса можно спросить
/// по DoH (DNS поверх HTTPS). Нужен затем, что провайдерский DNS часто подменяет
/// ответ на заблокированных сайтах — и обход пакетов тут уже ни при чём.
/// </summary>
public static class DnsResolver
{
    private const string CloudflareUrl = "https://cloudflare-dns.com/dns-query";
    private const string GoogleUrl = "https://dns.google/dns-query";

    /// <summary>Ниже этого значения кэш не имеет смысла, выше — адрес успевает протухнуть.</summary>
    private const int MinTtlSeconds = 60;
    private const int MaxTtlSeconds = 3600;

    private sealed record CacheEntry(List<IPAddress> Addresses, DateTime ExpiresAt, string Source);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    /// <summary>
    /// Когда DoH последний раз не ответил. Если он закрыт провайдером, ходить в него на каждом
    /// новом имени нельзя: каждый запрос ждал бы таймаута. Пока передышка не вышла, работаем
    /// системным DNS — и так же возвращаемся к DoH, когда он снова отвечает.
    /// </summary>
    private static DateTime _dohFailedAt = DateTime.MinValue;
    private static readonly TimeSpan DohCooldown = TimeSpan.FromMinutes(10);

    private static HttpClient? _http;

    /// <summary>
    /// Свой клиент: без системного прокси. DoH не должен идти через наш же обход —
    /// иначе движок, чтобы узнать адрес сайта, сначала спросил бы адрес самого DoH.
    /// </summary>
    private static HttpClient Http => _http ??= new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>Адреса с учётом настроек: DoH если включён, иначе системный DNS.</summary>
    public static async Task<DnsLookup> ResolveAsync(string host, DohMode mode, string customUrl, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return new DnsLookup { Host = host, Addresses = { literal }, Source = L.T("S0390") };

        if (Cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return new DnsLookup
            {
                Host = host,
                Addresses = new List<IPAddress>(cached.Addresses),
                Source = cached.Source + L.T("S0391"),
                TtlSeconds = (int)(cached.ExpiresAt - DateTime.UtcNow).TotalSeconds
            };

        if (mode != DohMode.Off && DateTime.UtcNow - _dohFailedAt > DohCooldown)
        {
            var doh = await ResolveDohAsync(host, mode, customUrl, ct);
            if (doh.Ok)
            {
                _dohFailedAt = DateTime.MinValue;
                return doh;
            }

            _dohFailedAt = DateTime.UtcNow;   // DoH не отвечает: не тратим на него время впустую
        }

        return await ResolveSystemAsync(host, ct);
    }

    /// <summary>Системный DNS — то, что отдаёт провайдер.</summary>
    public static async Task<DnsLookup> ResolveSystemAsync(string host, CancellationToken ct = default)
    {
        var result = new DnsLookup { Host = host, Source = L.T("S0386") };
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            result.Addresses.AddRange(addresses);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (result.Ok) Remember(host, result.Addresses, MinTtlSeconds, result.Source);
        return result;
    }

    /// <summary>DoH: DNS через HTTPS, провайдер его не видит и подменить не может.</summary>
    public static async Task<DnsLookup> ResolveDohAsync(string host, DohMode mode, string customUrl, CancellationToken ct = default)
    {
        var result = new DnsLookup { Host = host, Source = "DoH " + Name(mode) };

        var baseUrl = mode switch
        {
            DohMode.Google => GoogleUrl,
            DohMode.Custom => customUrl.Trim(),
            _ => CloudflareUrl
        };

        if (baseUrl.Length == 0)
        {
            result.Error = L.T("S0392");
            return result;
        }

        try
        {
            // A и AAAA спрашиваем сразу: если IPv6 есть в ответе, но не ездит, соединяться
            // будем по IPv4 — порядок попыток выбирается уже при подключении
            var ipv4 = QueryAsync(baseUrl, host, "A", ct);
            var ipv6 = QueryAsync(baseUrl, host, "AAAA", ct);
            await Task.WhenAll(ipv4, ipv6);

            var a = await ipv4;
            var aaaa = await ipv6;

            result.Addresses.AddRange(a.Addresses);
            result.Addresses.AddRange(aaaa.Addresses);

            var ttls = new[] { a.Ttl, aaaa.Ttl }.Where(t => t > 0).ToList();
            result.TtlSeconds = ttls.Count > 0 ? ttls.Min() : 0;

            if (result.Addresses.Count == 0) result.Error = L.T("S0393");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (result.Ok) Remember(host, result.Addresses, result.TtlSeconds, result.Source);
        return result;
    }

    /// <summary>Сколько ждём ответа от выбранного сервера, мс: дальше работает системный DNS.</summary>
    private const int UdpTimeoutMs = 2000;

    /// <summary>
    /// Спросить адреса у конкретного сервера обычным DNS (UDP 53). Так работают сервисы раздела
    /// «DPI» и «Общие» с выбранным DNS: адреса их доменов берутся у выбранного сервера, а не
    /// у провайдера, который как раз и подменяет ответ на заблокированных сайтах.
    /// Сервер не ответил — спрашиваем системный DNS: обход не должен зависеть от одного сервера.
    /// </summary>
    public static async Task<DnsLookup> ResolveViaAsync(string host, IPAddress server, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return new DnsLookup { Host = host, Addresses = { literal }, Source = L.T("S0390") };

        if (Cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return new DnsLookup
            {
                Host = host,
                Addresses = new List<IPAddress>(cached.Addresses),
                Source = cached.Source + L.T("S0391"),
                TtlSeconds = (int)(cached.ExpiresAt - DateTime.UtcNow).TotalSeconds
            };

        var result = new DnsLookup { Host = host, Source = L.F("dpi.dns.serverSource", server) };

        try
        {
            var ipv4 = AskAsync(host, server, 1, ct);      // A
            var ipv6 = AskAsync(host, server, 28, ct);     // AAAA
            await Task.WhenAll(ipv4, ipv6);

            var a = await ipv4;
            var aaaa = await ipv6;

            result.Addresses.AddRange(a.Addresses);
            result.Addresses.AddRange(aaaa.Addresses);

            var ttls = new[] { a.Ttl, aaaa.Ttl }.Where(t => t > 0).ToList();
            result.TtlSeconds = ttls.Count > 0 ? ttls.Min() : 0;

            if (result.Addresses.Count == 0) result.Error = L.T("S0393");
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (result.Ok)
        {
            Remember(host, result.Addresses, result.TtlSeconds, result.Source);
            return result;
        }

        var fallback = await ResolveSystemAsync(host, ct);
        fallback.Source = L.F("dpi.dns.fellBack", server);
        return fallback;
    }

    /// <summary>
    /// Адрес сервера из настроек: выбранный в разделе «DNS» сервер может быть записан и именем
    /// («tls://dns.adguard-dns.com»). Приводим его к адресу — иначе выбор молча пропадал бы.
    /// </summary>
    public static async Task<IPAddress?> ServerAddressAsync(string text, CancellationToken ct = default)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var literal)) return literal;

        var lookup = await ResolveSystemAsync(trimmed, ct);
        return lookup.Ok ? lookup.Addresses[0] : null;
    }

    /// <summary>Один запрос к серверу по UDP: A или AAAA. Молчание сервера — пустой ответ.</summary>
    private static async Task<(List<IPAddress> Addresses, int Ttl)> AskAsync(
        string host, IPAddress server, int type, CancellationToken ct)
    {
        var list = new List<IPAddress>();
        var ttl = 0;

        try
        {
            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(server, 53));

            var query = BuildQuery(host, type);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(UdpTimeoutMs);

            await socket.SendAsync(query, SocketFlags.None, timeout.Token);

            var buffer = new byte[2048];
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            ParseAnswers(buffer, received, list, ref ttl);
        }
        catch
        {
            // сервер молчит или ответ непонятный: адреса добёрет системный DNS
        }

        return (list, ttl);
    }

    /// <summary>Запрос DNS в двоичном виде: один вопрос A или AAAA, рекурсия разрешена.</summary>
    private static byte[] BuildQuery(string host, int type)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var buffer = new List<byte>(32 + host.Length);

        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)id);
        buffer.Add(0x01);   // обычный запрос
        buffer.Add(0x00);   // рекурсия разрешена
        buffer.Add(0x00); buffer.Add(0x01);   // один вопрос
        buffer.Add(0x00); buffer.Add(0x00);   // ответов нет
        buffer.Add(0x00); buffer.Add(0x00);   // ссылок нет
        buffer.Add(0x00); buffer.Add(0x00);   // доп. записей нет

        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) continue;
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0x00);                     // конец имени
        buffer.Add((byte)(type >> 8));
        buffer.Add((byte)type);
        buffer.Add(0x00); buffer.Add(0x01);   // класс IN

        return buffer.ToArray();
    }

    /// <summary>Разобрать ответ: адреса и наименьший TTL из записей A и AAAA.</summary>
    private static void ParseAnswers(byte[] data, int length, List<IPAddress> list, ref int ttl)
    {
        if (length < 12) return;
        if ((data[3] & 0x0F) != 0) return;   // сервер ответил ошибкой

        var questions = (data[4] << 8) | data[5];
        var answers = (data[6] << 8) | data[7];
        var offset = 12;

        for (var i = 0; i < questions && offset < length; i++)
        {
            offset = SkipName(data, length, offset) + 4;   // имя, тип и класс вопроса
        }

        for (var i = 0; i < answers; i++)
        {
            offset = SkipName(data, length, offset);
            if (offset + 10 > length) return;

            var type = (data[offset] << 8) | data[offset + 1];
            var seconds = (data[offset + 4] << 24) | (data[offset + 5] << 16) |
                          (data[offset + 6] << 8) | data[offset + 7];
            var dataLength = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;

            if (offset + dataLength > length) return;

            if (type == 1 && dataLength == 4)
                list.Add(new IPAddress(data.AsSpan(offset, 4)));
            else if (type == 28 && dataLength == 16)
                list.Add(new IPAddress(data.AsSpan(offset, 16)));

            if (seconds > 0 && (ttl == 0 || seconds < ttl)) ttl = seconds;
            offset += dataLength;
        }
    }

    /// <summary>Пропустить имя в ответе: метки до нуля или указатель на другое имя.</summary>
    private static int SkipName(byte[] data, int length, int offset)
    {
        while (offset < length)
        {
            var label = data[offset];   // длина метки или указатель (старшие два бита — 11)
            if (label == 0) return offset + 1;
            if ((label & 0xC0) == 0xC0) return offset + 2;
            offset += 1 + label;
        }

        return offset;
    }

    private static async Task<(List<IPAddress> Addresses, int Ttl)> QueryAsync(
        string baseUrl, string host, string type, CancellationToken ct)
    {
        var url = baseUrl + (baseUrl.Contains('?') ? "&" : "?") + $"name={Uri.EscapeDataString(host)}&type={type}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("accept", "application/dns-json");

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);

        var list = new List<IPAddress>();
        var ttl = 0;

        if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return (list, ttl);

        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("TTL", out var ttlNode) && ttlNode.TryGetInt32(out var seconds) && seconds > 0)
                ttl = ttl == 0 ? seconds : Math.Min(ttl, seconds);

            if (!answer.TryGetProperty("data", out var data)) continue;
            if (IPAddress.TryParse(data.GetString() ?? "", out var address)) list.Add(address);
        }

        return (list, ttl);
    }

    private static void Remember(string host, List<IPAddress> addresses, int ttlSeconds, string source)
    {
        var ttl = ttlSeconds <= 0 ? MinTtlSeconds : Math.Clamp(ttlSeconds, MinTtlSeconds, MaxTtlSeconds);
        Cache[host] = new CacheEntry(addresses, DateTime.UtcNow.AddSeconds(ttl), source);
    }

    /// <summary>Очистить кэш — например, после смены провайдера или DNS.</summary>
    public static void ClearCache() => Cache.Clear();

    private static string Name(DohMode mode) => mode switch
    {
        DohMode.Cloudflare => "Cloudflare",
        DohMode.Google => "Google",
        DohMode.Custom => L.T("S0394"),
        _ => L.T("S0395")
    };
}
