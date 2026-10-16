using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using VibeTun.Models;

namespace VibeTun.Services;

public enum DpiTestVerdict
{
    /// <summary>Блокировка обходится (или её нет вообще).</summary>
    Broken,

    /// <summary>DPI режет, подобрать вариант не удалось.</summary>
    Covered,

    /// <summary>Не работает даже контрольный адрес — интернета нет.</summary>
    NoNetwork
}

public sealed class DpiTestResult
{
    public DpiTestVerdict Verdict { get; init; }

    /// <summary>На ком проверяли.</summary>
    public string Host { get; init; } = "";

    /// <summary>Одна строка для статуса.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Подобранная рабочая комбинация (null — ничего не подошло).</summary>
    public DpiBypassSettings? Working { get; init; }

    /// <summary>«Накрыт», но перебор вариантов не запускали — его стоит запустить кнопкой.</summary>
    public bool NeedsScan { get; init; }

    /// <summary>
    /// Провайдер рвёт само TCP-соединение до площадки: тут не помогает никакой разрыв
    /// пакетов, нужен VPN (или режим обхода «через профиль»).
    /// </summary>
    public bool AddressBlocked { get; init; }

    /// <summary>Подробные строки для журнала.</summary>
    public List<string> Lines { get; init; } = new();
}

/// <summary>Чем кончился замер — это и есть ответ на вопрос «кто виноват».</summary>
public enum DpiProbeKind
{
    /// <summary>Сервер ответил — площадка доступна.</summary>
    Answered,

    /// <summary>
    /// TCP дошёл, но на ClientHello никто не ответил: классическая картина DPI,
    /// который рвёт соединение, увидев имя сайта.
    /// </summary>
    Silent,

    /// <summary>
    /// TCP не поднимается вообще: провайдер не пускает до самого адреса (или не отдаёт DNS).
    /// Разрыв пакетов тут бессилен — поможет только VPN.
    /// </summary>
    NoRoute
}

/// <summary>Один замер: дошли ли до площадки и ответил ли сервер на ClientHello.</summary>
public sealed class DpiProbeResult
{
    public string Host = "";
    public string Address = "";

    /// <summary>Замер шёл через локальный прокси обхода, а не напрямую к сайту.</summary>
    public bool ViaProxy;

    public bool Connected;
    public bool Answered;
    public long ConnectMs;
    public long AnswerMs;
    public string Error = "";

    /// <summary>Тип первой записи TLS в ответе: 0x16 — handshake.</summary>
    public int RecordType;

    /// <summary>Что выторговали при рукопожатии (версия TLS, шифр, ALPN) — для журнала.</summary>
    public string Tls = "";

    /// <summary>Соединение сброшено (RST): так выглядит блокировка, которая рвёт поток, а не молчит.</summary>
    public bool Reset;

    public bool Ok => Answered;

    /// <summary>Кто виноват: доступно, режут по содержимому или режут по адресу.</summary>
    public DpiProbeKind Kind =>
        Answered ? DpiProbeKind.Answered
        : Connected ? DpiProbeKind.Silent
        : DpiProbeKind.NoRoute;

    public string Describe()
    {
        if (Error.Length > 0 && !Connected) return L.T("S0447") + Error;
        if (Error.Length > 0) return L.F("S0448", ConnectMs, Error);
        if (Answered && Tls.Length > 0) return L.F("S0449", ConnectMs, Tls, AnswerMs);
        return L.F("S0450", ConnectMs, AnswerMs);
    }

    /// <summary>Одна строка для журнала: что произошло, на каком адресе и с какими цифрами.</summary>
    public string DescribeKind()
    {
        var lead = Kind switch
        {
            DpiProbeKind.Answered => L.T("S0451"),
            DpiProbeKind.Silent => ViaProxy
                ? L.T("S0452")
                : L.T("S0453"),
            _ => ViaProxy
                ? L.T("S0454")
                : L.T("S0455")
        };

        var address = Address.Length > 0 ? $" [{Address}]" : "";
        return $"{lead}{address}: {Describe()}";
    }

}

/// <summary>
/// DPI-тест. Разбирает ситуацию по частям — иначе «Накрыт» ничего не объясняет:
/// 1) есть ли интернет вообще (ответил ли кто-нибудь: контрольный адрес или площадка);
/// 2) как именно провайдер режет каждую площадку — по содержимому (TCP доходит, ответа нет)
///    или по адресу (TCP не поднимается, тут поможет только VPN);
/// 3) помогает ли обход с текущими настройками.
/// Если не помогает — перебирает варианты (TTL фейк-пакета, место разрыва, мультисплит,
/// дробление по MSS) и возвращает первый рабочий.
/// </summary>
public static class DpiTester
{
    /// <summary>
    /// Адреса домена для замера: сервером, выбранным для сервиса в разделе «DNS» (обычный DNS
    /// по адресу), либо как раньше — DoH обхода или системный DNS. Замер должен идти на те же
    /// адреса, что и живой обход: иначе результат подбора ничего не значит.
    /// </summary>
    private static async Task<DnsLookup> ResolveAsync(string host, DpiBypassSettings settings, CancellationToken ct)
    {
        if (settings.ResolveServer.Length > 0)
        {
            var server = await DnsResolver.ServerAddressAsync(settings.ResolveServer, ct);
            if (server is not null) return await DnsResolver.ResolveViaAsync(host, server, ct);
        }

        return await DnsResolver.ResolveAsync(host, settings.Doh, settings.DohUrl, ct);
    }

    /// <summary>Площадки, которые в РФ блокируются средствами DPI.</summary>
    public static readonly string[] BlockedHosts =
    {
        "discord.com",
        "soundcloud.com",
        "instagram.com",
        "youtube.com"
    };

    /// <summary>Контроль: то, что в РФ открывается и так. Публичный — на нём проверяется наш ClientHello.</summary>
    public static readonly string[] ControlHosts =
    {
        "cloudflare.com",
        "www.gstatic.com"
    };

    private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };

    /// <summary>Сколько ждём ответа на прямой замер, мс.</summary>
    /// <summary>
    /// Приветствие для замеров: его же использует диагностика. Публично, чтобы
    /// диагностика могла мерить TLS тем же пакетом, что и тест.
    /// </summary>
    public static byte[] BuildProbeHello(string host) => BuildClientHello(host);

    private const int DirectTimeoutMs = 6000;

    /// <summary>Сколько ждём ответа через обход, мс.</summary>
    private const int ViaTimeoutMs = 6000;

    /// <summary>Сколько ждём ответа на один вариант при переборе, мс.</summary>
    private const int ScanTimeoutMs = 5000;

    /// <param name="enginePort">Порт работающего обхода; 0 — мерить напрямую, без обхода.</param>
    /// <param name="apply">Перезапуск движка с новыми настройками (для перебора вариантов).</param>
    /// <param name="progress">Ход теста — в строку состояния.</param>
    /// <param name="scan">Перебирать ли варианты, если текущие настройки не помогают.</param>
    public static async Task<DpiTestResult> TestAsync(
        int enginePort,
        Func<DpiBypassSettings, Task> apply,
        DpiBypassSettings current,
        Action<string>? progress = null,
        bool scan = true,
        CancellationToken ct = default)
    {
        var lines = new List<string>();

        // ------------------------------------------------------------ 1. напрямую
        // Контроль и площадки мерим одной пачкой. Вывод «интернета нет» делается по всей
        // картине, а не по одному контрольному адресу: иначе тест упирался в контроль,
        // который провайдер не пускает (или который отвечает alert вместо ServerHello),
        // и говорил «интернета нет» там, где ютуб спокойно открывался.
        progress?.Invoke(L.T("S0457"));

        var everything = ControlHosts.Concat(BlockedHosts).ToArray();
        var probes = await ProbeAllAsync(everything, 0, DirectTimeoutMs, ct);
        var control = probes.Take(ControlHosts.Length).ToArray();
        var direct = probes.Skip(ControlHosts.Length).ToArray();

        foreach (var probe in control) lines.Add(L.F("S0458", probe.Host, probe.DescribeKind()));
        foreach (var probe in direct) lines.Add(L.F("S0459", probe.Host, probe.DescribeKind()));

        // интернет есть, если ответил хотя бы кто-то — контрольный адрес или площадка
        var alive = probes.FirstOrDefault(p => p.Ok);

        if (alive is null && enginePort > 0)
        {
            // никто не ответил напрямую — сначала пробуем через обход, и только потом
            // говорим «интернета нет»
            progress?.Invoke(L.T("S0460"));
            var viaAll = await ProbeAllAsync(everything, enginePort, ViaTimeoutMs, ct);
            foreach (var probe in viaAll) lines.Add(L.F("S0461", probe.Host, probe.DescribeKind()));

            alive = viaAll.FirstOrDefault(p => p.Ok);
            if (alive is not null) lines.Add(L.T("S0462"));
        }

        if (alive is null)
        {
            var reasons = string.Join("; ", probes.Select(p => $"{p.Host}: {p.Describe()}"));
            lines.Add(L.T("S0463"));
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.NoNetwork,
                Detail = L.T("S0464") + reasons,
                Lines = lines
            };
        }

        lines.Add(L.F("S0465", alive.Host, alive.Describe()));

        // ----------------------------------------- 2. кто заблокирован и как именно
        var byContent = direct.Where(p => p.Kind == DpiProbeKind.Silent).ToList();
        var byAddress = direct.Where(p => p.Kind == DpiProbeKind.NoRoute).ToList();

        if (byContent.Count == 0 && byAddress.Count == 0)
        {
            lines.Add(L.T("S0466"));
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = direct[0].Host,
                Detail = L.T("S0467"),
                Lines = lines
            };
        }

        // -------------------------------------------- 3. текущие настройки обхода
        progress?.Invoke(L.T("S0468"));
        var blocked = byContent.Concat(byAddress).Select(p => p.Host).ToArray();
        var via = await ProbeAllAsync(blocked, enginePort, ViaTimeoutMs, ct);
        foreach (var probe in via) lines.Add(L.F("S0461", probe.Host, probe.DescribeKind()));

        var opened = via.FirstOrDefault(p => p.Ok);
        if (opened is not null)
        {
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = opened.Host,
                Detail = L.F("S0469", opened.Host, opened.Describe()),
                Lines = lines
            };
        }

        var perHost = via.Select(probe => probe.Kind == DpiProbeKind.NoRoute
            ? L.F("S0470", probe.Host)
            : L.F("S0471", probe.Host));
        var detail = string.Join("; ", perHost);

        // ------------------------------------------------- 4. перебор вариантов
        // Перебираем только там, где TCP доходит: если провайдер рвёт само соединение,
        // никакая десинхронизация не поможет, и жечь на это минуту смысла нет.
        var workable = via.Where(p => p.Kind == DpiProbeKind.Silent).Select(p => p.Host).ToArray();

        if (workable.Length == 0)
        {
            lines.Add(L.T("S0472"));
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Covered,
                Host = via[0].Host,
                Detail = detail,
                AddressBlocked = true,
                Lines = lines
            };
        }

        if (!scan)
        {
            lines.Add(L.T("S0473"));
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Covered,
                Host = workable[0],
                Detail = detail,
                NeedsScan = true,
                Lines = lines
            };
        }

        var variants = Variants(current);
        lines.Add(L.F("S0474", variants.Count, string.Join(", ", workable)));

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            progress?.Invoke(L.F("S0475", i + 1, variants.Count, Describe(variant)));
            lines.Add(L.F("S0476", i + 1, variants.Count, Describe(variant)));

            try
            {
                await apply(variant);
            }
            catch (Exception ex)
            {
                lines.Add(L.T("S0477") + ex.Message);
                continue;
            }

            var probeAll = await ProbeAllAsync(workable, enginePort, ScanTimeoutMs, ct);
            foreach (var probe in probeAll) lines.Add(L.F("S0478", probe.Host, probe.DescribeKind()));

            var hit = probeAll.FirstOrDefault(p => p.Ok);
            if (hit is null) continue;

            lines.Add(L.F("S0479", Describe(variant)));
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = hit.Host,
                Detail = L.F("S0480", hit.Host, Describe(variant)),
                Working = variant,
                Lines = lines
            };
        }

        lines.Add(L.T("S0481"));
        return new DpiTestResult
        {
            Verdict = DpiTestVerdict.Covered,
            Host = workable[0],
            Detail = detail,
            Lines = lines
        };
    }
    private static Task<DpiProbeResult[]> ProbeAllAsync(string[] hosts, int enginePort, int timeoutMs, CancellationToken ct)
    {
        var probes = hosts
            .Select(host => ProbeAsync(host, enginePort, timeoutMs, ct))
            .ToArray();
        return Task.WhenAll(probes);
    }

    /// <summary>
    /// Замер: подключаемся к площадке (напрямую или через обход) и доводим рукопожатие TLS
    /// до конца. Успех — только поднятый канал до самого сайта: раньше за успех принимался
    /// и alert, и один байт ответа, поэтому «подобранная» стратегия могла не работать
    /// в браузере — ровно на это и была жалоба.
    /// </summary>
    public static async Task<DpiProbeResult> ProbeAsync(string host, int enginePort, int timeoutMs, CancellationToken ct)
    {
        var result = new DpiProbeResult { Host = host };
        var sw = Stopwatch.StartNew();
        TcpClient? client = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            client = new TcpClient();

            if (enginePort > 0)
            {
                // через обход: просим туннель у локального прокси. «Соединение» тут — это
                // соединение до своего же прокси, поэтому в журнале оно описывается иначе
                result.ViaProxy = true;
                await client.ConnectAsync(IPAddress.Loopback, enginePort, timeout.Token);
                var connect = Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
                await client.GetStream().WriteAsync(connect, timeout.Token);
                await client.GetStream().FlushAsync(timeout.Token);

                var reply = await ReadUntilAsync(client.GetStream(), HeadEnd, 8192, timeout.Token);
                var answer = Encoding.ASCII.GetString(reply);
                if (!answer.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !answer.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.T("S0482") + answer.Split("\r\n")[0]);
            }
            else
            {
                // адреса берём тем же резолвером, что и движок: если провайдер подменяет
                // DNS, замер должен это видеть, а не мерить подставной адрес
                var settings = App.Settings.Current.Dpi;
                var lookup = await ResolveAsync(host, settings, timeout.Token);
                var address = lookup.InConnectOrder.FirstOrDefault();
                if (address is null) throw new IOException(L.T("S0483") + host);

                result.Address = address.ToString();
                await client.ConnectAsync(address, 443, timeout.Token);
            }

            result.Connected = true;
            result.ConnectMs = sw.ElapsedMilliseconds;

            // сертификат не проверяем: важен сам факт рукопожатия с этим именем
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    SslApplicationProtocol.Http11,
                    SslApplicationProtocol.Http2
                }
            }, timeout.Token);

            result.AnswerMs = sw.ElapsedMilliseconds;
            result.Answered = true;
            result.RecordType = 0x16;

            var alpn = ssl.NegotiatedApplicationProtocol.Protocol;
            result.Tls = ssl.SslProtocol.ToString();
            if (alpn.Length > 0) result.Tls += " / " + Encoding.ASCII.GetString(alpn.Span);
        }
        catch (AuthenticationException ex)
        {
            // до сайта дошли (TCP поднялся), но рукопожатие не прошло: это alert сервера
            // или обрыв посреди рукопожатия — и то и другое не «работает»
            result.Error = Short(ex.Message);
        }
        catch (OperationCanceledException)
        {
            result.Error = L.T("S0440");
        }
        catch (SocketException ex)
        {
            result.Reset = ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            result.Error = ex.SocketErrorCode == SocketError.HostNotFound
                ? L.T("S0485")
                : L.T("S0445") + ex.SocketErrorCode;
        }
        catch (IOException ex) when (ex.InnerException is SocketException inner)
        {
            result.Reset = inner.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            result.Error = Short(ex.Message);
        }
        catch (Exception ex)
        {
            result.Error = Short(ex.Message);
        }
        finally
        {
            try { client?.Dispose(); }
            catch { /* ignore */ }
        }

        return result;
    }

    /// <summary>
    /// Проверка «открылся ли поток» нашим собственным ClientHello — ровно тем, который уходит
    /// и в фейк-пакете. Полное рукопожатие здесь не нужно: достаточно ServerHello от сервера.
    ///
    /// Смысл в том, что проверка идёт браузерным «приветствием», а не тем, что строит .NET:
    /// часть DPI разбирает ClientHello по виду, и тогда честнее мерить его же, а не чужой.
    /// </summary>
    public static async Task<DpiProbeResult> ProbeHelloAsync(
        string host, int enginePort, int timeoutMs, CancellationToken ct)
    {
        var result = new DpiProbeResult { Host = host };
        var sw = Stopwatch.StartNew();
        TcpClient? client = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            client = new TcpClient();

            if (enginePort > 0)
            {
                result.ViaProxy = true;
                await client.ConnectAsync(IPAddress.Loopback, enginePort, timeout.Token);
                var connect = Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
                await client.GetStream().WriteAsync(connect, timeout.Token);
                await client.GetStream().FlushAsync(timeout.Token);

                var reply = await ReadUntilAsync(client.GetStream(), HeadEnd, 8192, timeout.Token);
                var answer = Encoding.ASCII.GetString(reply);
                if (!answer.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !answer.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                    throw new IOException(L.T("S0482") + answer.Split("\r\n")[0]);
            }
            else
            {
                var settings = App.Settings.Current.Dpi;
                var lookup = await ResolveAsync(host, settings, timeout.Token);
                var address = lookup.InConnectOrder.FirstOrDefault();
                if (address is null) throw new IOException(L.T("S0483") + host);

                result.Address = address.ToString();
                await client.ConnectAsync(address, 443, timeout.Token);
            }

            result.Connected = true;
            result.ConnectMs = sw.ElapsedMilliseconds;

            var stream = client.GetStream();
            var hello = BuildClientHello(host);
            await stream.WriteAsync(hello, timeout.Token);
            await stream.FlushAsync(timeout.Token);

            // первый ответ: 0x16 — рукопожатие (ServerHello), 0x15 — alert
            var head = new byte[5];
            var read = 0;
            while (read < head.Length)
            {
                var got = await stream.ReadAsync(head.AsMemory(read), timeout.Token);
                if (got <= 0) break;
                read += got;
            }

            if (read == 0) throw new IOException(L.T("dpi.hello.eof"));

            result.AnswerMs = sw.ElapsedMilliseconds;
            result.RecordType = head[0];

            if (head[0] != 0x16) throw new IOException(L.F("S0427", head[0]));

            result.Answered = true;
            result.Tls = L.T("dpi.hello.serverHello");
        }
        catch (OperationCanceledException)
        {
            result.Error = L.T("S0440");
        }
        catch (SocketException ex)
        {
            result.Reset = ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            result.Error = ex.SocketErrorCode == SocketError.HostNotFound
                ? L.T("S0485")
                : L.T("S0445") + ex.SocketErrorCode;
        }
        catch (IOException ex) when (ex.InnerException is SocketException inner)
        {
            result.Reset = inner.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            result.Error = Short(ex.Message);
        }
        catch (Exception ex)
        {
            result.Error = Short(ex.Message);
        }
        finally
        {
            try { client?.Dispose(); }
            catch { /* ignore */ }
        }

        return result;
    }

    /// <summary>Сообщение об ошибке одной строкой: в журнале нужен смысл, а не стек.</summary>
    private static string Short(string message)
    {
        var text = message.Replace("\r", " ").Replace("\n", " ").Trim();
        var dot = text.IndexOf(" (.NET", StringComparison.Ordinal);
        if (dot > 0) text = text[..dot];
        return text.Length > 200 ? text[..200] : text;
    }

    private static async Task<byte[]> ReadUntilAsync(NetworkStream stream, byte[] pattern, int limit, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(1024, pattern.Length * 2)];
        var length = 0;

        while (length < limit)
        {
            if (length >= pattern.Length && EndsWith(buffer, length, pattern))
                return buffer[..length];

            if (length == buffer.Length)
            {
                var bigger = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                buffer = bigger;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read <= 0) break;
            length += read;
        }

        return buffer[..length];
    }

    private static bool EndsWith(byte[] buffer, int length, byte[] pattern)
    {
        if (length < pattern.Length) return false;
        var start = length - pattern.Length;
        for (var i = 0; i < pattern.Length; i++)
            if (buffer[start + i] != pattern[i]) return false;
        return true;
    }

    // --------------------------------------------------------------- варианты
    /// <summary>
    /// Набор приёмов для перебора — те же рецепты, что в стратегиях zapret и ByeDPI:
    /// <c>split</c>/<c>multisplit</c> в разных позициях, <c>tlsrec</c>, <c>disorder</c>, <c>oob</c>,
    /// <c>fake</c> с обманками (ttl/badsum/badseq), <c>split-seqovl</c> (фейк перед запросом),
    /// фейк с чужим SNI и дробление по MSS.
    ///
    /// Порядок — по «семействам»: сначала по одному представителю каждого приёма (так первые
    /// найденные стратегии будут разными по механике, а не вариантами одного и того же),
    /// затем остальные настройки тех же семейств.
    /// </summary>
    public static List<DpiBypassSettings> Strategies(DpiBypassSettings current) => Variants(current);

    private static List<DpiBypassSettings> Variants(DpiBypassSettings current)
    {
        var list = new List<DpiBypassSettings>();

        // --- 1. разрыв ClientHello: позиции как --dpi-desync-split-pos=1/2/midsld/sni
        list.Add(Variant(current, DpiSplitPosition.Sni));
        list.Add(Variant(current, DpiSplitPosition.FirstByte));
        list.Add(Variant(current, DpiSplitPosition.MidSld));
        list.Add(Variant(current, DpiSplitPosition.SecondByte));

        // --- 2. переупаковка в две записи TLS (--tlsrec у ByeDPI)
        list.Add(Variant(current, DpiSplitPosition.Sni, tlsrec: true));

        // --- 3. мультисплит: много мелких сегментов с паузой
        list.Add(Variant(current, DpiSplitPosition.FirstByte, multisplit: 8));
        list.Add(Variant(current, DpiSplitPosition.FirstByte, multisplit: 2));

        // --- 4. disorder: начало уходит с TTL=1 и до DPI не доходит
        list.Add(Variant(current, DpiSplitPosition.FirstByte, disorder: true));
        list.Add(Variant(current, DpiSplitPosition.Sni, disorder: true));

        // --- 5. срочные данные (--oob у ByeDPI): байт в позиции разреза уходит urgent
        list.Add(Variant(current, DpiSplitPosition.Sni, oob: true));
        list.Add(Variant(current, DpiSplitPosition.FirstByte, oob: true));

        // --- 6. дробление по MSS: система сама режет ClientHello на мелкие сегменты
        list.Add(Variant(current, DpiSplitPosition.Sni, mss: true));

        // --- 7. фейк-пакет: главный приём zapret (--dpi-desync=fake --fooling=ttl, autottl)
        list.Add(Fake(current));
        list.Add(Fake(current, repeats: 6));                                        // --dpi-desync-repeats=6
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, repeats: 3)); // --fake-tls=sni=…
        list.Add(Fake(current, repeats: 1, ahead: true, position: DpiSplitPosition.FirstByte));  // split-seqovl
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, ahead: true, position: DpiSplitPosition.FirstByte));
        list.Add(Fake(current, fooling: DpiFooling.BadSum, repeats: 3));
        list.Add(Fake(current, fooling: DpiFooling.BadSeq, repeats: 3));
        list.Add(Fake(current, fooling: DpiFooling.DataNoAck, repeats: 3));
        list.Add(Fake(current, fooling: DpiFooling.Ttl | DpiFooling.BadSum, repeats: 3));
        // «fake,multidisorder» из general (FAKE TLS AUTO): повторов 11 и разрыв сразу после disorder
        list.Add(Fake(current, fooling: DpiFooling.BadSeq, repeats: 11, disorder: true, position: DpiSplitPosition.FirstByte));
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, fooling: DpiFooling.BadSeq, repeats: 11,
            disorder: true, position: DpiSplitPosition.MidSld));
        // «fake,multisplit» из general (SIMPLE FAKE): фейк и мелкие куски записи
        list.Add(Fake(current, fooling: DpiFooling.BadSeq, repeats: 6, multisplit: 1, position: DpiSplitPosition.FirstByte));

        // --- 8. фейк с ручным TTL вместо авто-TTL (--dpi-desync-ttl=…)
        foreach (var ttl in new[] { 5, 3, 12, 16 })
            list.Add(Fake(current, ttl: ttl, autoTtl: false, repeats: 3));

        // --- 9. фейк вместе с разрывом: fakedsplit у zapret
        list.Add(Fake(current, position: DpiSplitPosition.Sni));
        list.Add(Fake(current, position: DpiSplitPosition.FirstByte));
        list.Add(Fake(current, position: DpiSplitPosition.MidSld, tlsrec: true));
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, position: DpiSplitPosition.Sni, tlsrec: true));
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, position: DpiSplitPosition.FirstByte, tlsrec: true));
        list.Add(Fake(current, fooling: DpiFooling.BadSum, position: DpiSplitPosition.FirstByte, oob: true));

        // --- 10. фейк вместе с мультисплитом и после настоящего ClientHello
        list.Add(Fake(current, multisplit: 8));
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, multisplit: 2));
        list.Add(Fake(current, after: true, repeats: 3));
        list.Add(Fake(current, decoy: DpiServiceCatalog.DefaultFakeSni, after: true, repeats: 6));
        list.Add(Fake(current, fooling: DpiFooling.BadSum, multisplit: 8));
        list.Add(Fake(current, autoTtl: false, ttl: 8, repeats: 6, mss: true));

        return list;
    }

    /// <summary>Вариант без фейка: только разрыв, disorder, срочные данные или MSS.</summary>
    private static DpiBypassSettings Variant(
        DpiBypassSettings current, DpiSplitPosition position, bool tlsrec = false, bool disorder = false,
        bool oob = false, int multisplit = 0, bool mss = false)
    {
        var clone = current.Clone();
        clone.SniSplit = true;
        clone.SplitPosition = position;
        clone.TlsRecordSplit = tlsrec;
        clone.Disorder = disorder;
        clone.OobSplit = oob;
        clone.Multisplit = multisplit > 0;
        clone.MultisplitSize = multisplit > 0 ? multisplit : current.MultisplitSize;
        clone.ClampMss = mss;
        clone.MssValue = 64;
        clone.FakeTtl = false;
        clone.FakeSni = "";
        clone.SplitDelayMs = current.SplitDelayMs <= 0 ? 5 : current.SplitDelayMs;
        return clone;
    }

    /// <summary>
    /// Вариант с фейк-пакетом: <c>--dpi-desync=fake</c> плюс обманка (fooling), повторы,
    /// чужое имя, размещение до/после запроса и необязательный разрыв рядом.
    /// </summary>
    private static DpiBypassSettings Fake(
        DpiBypassSettings current, bool split = true, int repeats = 1, string decoy = "",
        DpiFooling fooling = DpiFooling.Ttl, bool autoTtl = true, int ttl = 8, bool ahead = false,
        bool after = false, bool tlsrec = false, int multisplit = 0, bool mss = false,
        bool disorder = false, bool oob = false,
        DpiSplitPosition position = DpiSplitPosition.Sni)
    {
        var clone = current.Clone();
        clone.FakeTtl = true;
        clone.InjectFake = true;
        clone.FakeFooling = fooling;
        clone.FakePlacement = ahead ? DpiFakePlacement.Ahead : DpiFakePlacement.Overlap;
        clone.FakeAutoTtl = autoTtl;
        clone.FakeTtlValue = ttl;
        clone.FakeCount = Math.Clamp(repeats, 1, 10);
        clone.FakeAfter = after;
        clone.FakeSni = decoy;

        clone.SniSplit = split;
        clone.TlsRecordSplit = split && tlsrec;
        clone.SplitPosition = position;
        clone.Disorder = disorder;      // «fake,multidisorder» у zapret
        clone.OobSplit = oob;
        clone.Multisplit = multisplit > 0;
        clone.MultisplitSize = multisplit > 0 ? multisplit : current.MultisplitSize;
        clone.ClampMss = mss;
        clone.MssValue = 64;
        clone.SplitDelayMs = current.SplitDelayMs <= 0 ? 5 : current.SplitDelayMs;
        return clone;
    }

    public static string Describe(DpiBypassSettings s)
    {
        var parts = new List<string>();
        if (s.FakeTtl)
        {
            var fake = s.FakeAutoTtl ? L.T("dpi.strategy.autottl") : $"fake TTL {s.FakeTtlValue}";
            fake += " → " + DpiBypassEngine.FoolingText(s.FakeFooling);
            if (s.FakePlacement == DpiFakePlacement.Ahead) fake += ", " + L.T("dpi.place.ahead");
            if (s.FakeCount > 1) fake += $" ×{s.FakeCount}";
            if (s.FakeAfter) fake += L.T("S0486");
            if (s.FakeSni.Trim().Length > 0) fake += L.F("dpi.strategy.decoy", s.FakeSni.Trim());
            if (!s.InjectFake) fake += ", " + L.T("dpi.strategy.noinject");
            parts.Add(fake);
        }

        if (s.SniSplit) parts.Add(SplitLabel(s.SplitPosition));
        if (s.TlsRecordSplit) parts.Add("tlsrec");
        if (s.Disorder) parts.Add(L.T("dpi.strategy.disorder"));
        if (s.OobSplit) parts.Add(L.T("dpi.strategy.oob"));
        if (s.Multisplit) parts.Add(L.F("S0488", s.MultisplitSize));
        if (s.ClampMss) parts.Add($"MSS {s.MssValue}");
        return parts.Count == 0 ? L.T("S0489") : string.Join(" · ", parts);
    }

    /// <summary>Название позиции разрыва — как <c>--dpi-desync-split-pos</c> в zapret.</summary>
    private static string SplitLabel(DpiSplitPosition position) => position switch
    {
        DpiSplitPosition.FirstByte => "split 1",
        DpiSplitPosition.SecondByte => "split 2",
        DpiSplitPosition.MidSld => "split midsld",
        DpiSplitPosition.Mid => "split mid",
        _ => "SNI split"
    };

    // ------------------------------------------------------- свой ClientHello
    /// <summary>GREASE-расширение: браузеры вставляют такие, чтобы серверы не закрепляли формат.</summary>
    private const int Grease = 0x0a0a;

    /// <summary>Набор шифров как у браузера: сначала TLS 1.3, потом ECDHE.</summary>
    private static readonly byte[] Ciphers =
    {
        0x13, 0x01, 0x13, 0x02, 0x13, 0x03,
        0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30,
        0xcc, 0xa9, 0xcc, 0xa8,
        0xc0, 0x13, 0xc0, 0x14,
        0x00, 0x9c, 0x00, 0x9d, 0x00, 0x2f, 0x00, 0x35, 0x00, 0x0a
    };

    /// <summary>
    /// ClientHello как у браузера: TLS 1.3 + 1.2, обычный набор шифров, SNI, ALPN,
    /// key_share на P-256 и GREASE-расширения. Рукопожатие мы не доводим — важен сам
    /// факт ответа сервера, — но «приветствие» должно быть настоящим: на минимальный
    /// самодельный ClientHello серверы отвечают alert, и тест принимал это за блокировку.
    /// </summary>
    private static byte[] BuildClientHello(string host)
    {
        var random = new byte[32];
        Random.Shared.NextBytes(random);

        // браузеры шлют 32-байтный «старый» session_id — его ждёт часть посредников
        var sessionId = new byte[32];
        Random.Shared.NextBytes(sessionId);

        var extensions = new List<byte>();
        AddExtension(extensions, Grease, Array.Empty<byte>());                          // GREASE
        AddExtension(extensions, 0x0000, ServerName(host));                             // server_name
        AddExtension(extensions, 0x0017, Array.Empty<byte>());                          // extended_master_secret
        AddExtension(extensions, 0xff01, new byte[] { 0x00 });                          // renegotiation_info
        AddExtension(extensions, 0x000a, SupportedGroups());                            // supported_groups
        AddExtension(extensions, 0x000b, new byte[] { 0x01, 0x00 });                    // ec_point_formats
        AddExtension(extensions, 0x0023, Array.Empty<byte>());                          // session_ticket
        AddExtension(extensions, 0x0010, Alpn());                                       // ALPN: h2, http/1.1
        AddExtension(extensions, 0x0005, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 });  // status_request

        var keyShare = KeyShare();
        if (keyShare is not null) AddExtension(extensions, 0x0033, keyShare);            // key_share

        // supported_versions: GREASE, TLS 1.3, TLS 1.2 (длина списка — один байт)
        AddExtension(extensions, 0x002b, new byte[] { 0x06, 0x0a, 0x0a, 0x03, 0x04, 0x03, 0x03 });
        AddExtension(extensions, 0x000d, SignatureAlgorithms());                         // signature_algorithms
        AddExtension(extensions, 0x002d, new byte[] { 0x01, 0x01 });                     // psk_key_exchange_modes
        AddExtension(extensions, 0x001c, new byte[] { 0x40, 0x01 });                     // record_size_limit
        AddExtension(extensions, Grease, Array.Empty<byte>());                           // GREASE

        var body = new List<byte>();
        body.Add(0x03);
        body.Add(0x03);                                   // legacy_version: TLS 1.2
        body.AddRange(random);
        body.Add((byte)sessionId.Length);
        body.AddRange(sessionId);
        AddU16(body, Ciphers.Length);
        body.AddRange(Ciphers);
        body.Add(0x01);
        body.Add(0x00);                                   // compression: null
        AddU16(body, extensions.Count);
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };          // ClientHello
        handshake.Add((byte)(body.Count >> 16));
        handshake.Add((byte)((body.Count >> 8) & 0xFF));
        handshake.Add((byte)(body.Count & 0xFF));
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.Add((byte)(handshake.Count >> 8));
        record.Add((byte)(handshake.Count & 0xFF));
        record.AddRange(handshake);

        return record.ToArray();
    }

    private static byte[] ServerName(string host)
    {
        var name = Encoding.ASCII.GetBytes(host);

        var entry = new List<byte> { 0x00 };              // host_name
        AddU16(entry, name.Length);
        entry.AddRange(name);

        var list = new List<byte>();
        AddU16(list, entry.Count);
        list.AddRange(entry);
        return list.ToArray();
    }

    private static byte[] SupportedGroups()
    {
        var list = new List<byte>();
        AddU16(list, 6);
        AddU16(list, Grease);
        AddU16(list, 0x001d);                             // x25519
        AddU16(list, 0x0017);                             // secp256r1
        return list.ToArray();
    }

    private static byte[] Alpn()
    {
        var list = new List<byte>();
        AddU16(list, 12);
        list.Add(0x02);
        list.Add((byte)'h');
        list.Add((byte)'2');
        list.Add(0x08);
        foreach (var c in "http/1.1") list.Add((byte)c);
        return list.ToArray();
    }

    private static byte[] SignatureAlgorithms()
    {
        var algs = new byte[]
        {
            0x04, 0x03, 0x08, 0x04, 0x08, 0x05, 0x08, 0x06, 0x04, 0x01,
            0x05, 0x03, 0x05, 0x01, 0x02, 0x01, 0x02, 0x03
        };

        var list = new List<byte>();
        AddU16(list, algs.Length);
        list.AddRange(algs);
        return list.ToArray();
    }

    /// <summary>
    /// key_share: публичный ключ P-256, чтобы сервер сразу мог получить общую тайну.
    /// Без ключа часть серверов отвечает alert, и замер получается нечестным.
    /// </summary>
    private static byte[]? KeyShare()
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var spki = ecdh.PublicKey.ToByteArray();      // X.509 SubjectPublicKeyInfo
            if (spki.Length < 65) return null;

            var point = spki[^65..];                      // 0x04 || X || Y

            var entry = new List<byte>();
            AddU16(entry, 0x0017);                        // secp256r1
            AddU16(entry, point.Length);
            entry.AddRange(point);

            var list = new List<byte>();
            AddU16(list, entry.Count);
            list.AddRange(entry);
            return list.ToArray();
        }
        catch
        {
            return null;                                  // не вышло — шлём приветствие без key_share
        }
    }

    private static void AddExtension(List<byte> target, int type, byte[] data)
    {
        target.Add((byte)(type >> 8));
        target.Add((byte)(type & 0xFF));
        AddU16(target, data.Length);
        target.AddRange(data);
    }

    private static void AddU16(List<byte> target, int value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)(value & 0xFF));
    }
}
