using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>Результат пинга DNS-сервера.</summary>
/// <param name="Ms">Время ответа, мс; -1 — сервер не ответил.</param>
/// <param name="Error">Почему не ответил — для подсказки у «нет ответа».</param>
public readonly record struct DnsPingResult(int Ms, string Error)
{
    public bool Ok => Ms >= 0;

    public static DnsPingResult Fail(string error) => new(-1, error);
}

/// <summary>
/// Пинг DNS-серверов раздела «DNS». Обычный адрес (IP, udp://) проверяем настоящим DNS-запросом
/// по UDP — время до ответа сервера: ICMP тут не годится, многие DNS-серверы на ping не отвечают,
/// а бывает и наоборот — ping проходит, а порт 53 закрыт. Для tls:// и https:// — время
/// TCP-подключения к порту сервера (853/443), для quic:// и h3:// — ICMP.
/// Вызывать из пула потоков (Task.Run): иначе занятый UI-поток прибавлял бы к времени ответа.
/// </summary>
public static class DnsPing
{
    /// <summary>Имя для пробного запроса: оно есть в кэше любого резолвера, ответ короткий.</summary>
    private const string ProbeName = "google.com";

    /// <summary>
    /// Две попытки: первый запрос может уйти мимо кэша резолвера (рекурсия), второй показывает
    /// честное время до сервера. Берём лучшее.
    /// </summary>
    private const int Attempts = 2;

    /// <summary>DNS-сервер, который отвечает дольше, для работы всё равно не годится.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(3);

    public static async Task<DnsPingResult> MeasureAsync(string address, CancellationToken ct = default)
    {
        if (!DnsAddress.TryParseEndpoint(address, out var scheme, out var host, out var port))
            return DnsPingResult.Fail("не удалось разобрать адрес");

        try
        {
            switch (scheme)
            {
                case "tls":
                case "https":
                {
                    var tcpPort = port > 0 ? port : scheme == "tls" ? 853 : 443;
                    var ms = await LatencyService.TryTcpAsync(host, tcpPort, TcpTimeout, ct).ConfigureAwait(false);
                    return ms >= 0 ? new DnsPingResult(ms, "") : DnsPingResult.Fail($"нет TCP-подключения к порту {tcpPort}");
                }
                case "quic":
                case "h3":
                {
                    var ms = await LatencyService.TryIcmpAsync(host, ct).ConfigureAwait(false);
                    return ms >= 0 ? new DnsPingResult(ms, "") : DnsPingResult.Fail("сервер не ответил на ping (ICMP)");
                }
                default:
                    return await QueryAsync(host, port > 0 ? port : 53, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DnsPingResult.Fail(ex.Message);
        }
    }

    private static async Task<DnsPingResult> QueryAsync(string host, int port, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var ip))
        {
            // udp://dns.example — адрес сервера узнаём системным резолвером, как и ядро
            var resolved = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            ip = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? resolved.FirstOrDefault();
            if (ip is null) return DnsPingResult.Fail("не удалось узнать адрес " + host);
        }

        var endpoint = new IPEndPoint(ip, port);
        var best = -1;
        var error = "";
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (ms, attemptError, final) = await QueryOnceAsync(endpoint, ct).ConfigureAwait(false);
            if (ms >= 0) best = best < 0 ? ms : Math.Min(best, ms);
            else error = attemptError;
            if (final) break;
        }

        return best >= 0 ? new DnsPingResult(best, "") : DnsPingResult.Fail(error);
    }

    /// <summary>Один DNS-запрос. Final — повторять бессмысленно (порт закрыт, нет маршрута, отказ).</summary>
    private static async Task<(int Ms, string Error, bool Final)> QueryOnceAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var id = (ushort)Random.Shared.Next(0x10000);
        var query = BuildQuery(id);
        var buffer = new byte[2048];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);
        try
        {
            // «подключённый» UDP-сокет: доходят только пакеты от этого сервера, а закрытый порт
            // (ICMP port unreachable) сразу виден как ошибка, без ожидания таймаута
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);

            var sw = Stopwatch.StartNew();
            await socket.SendAsync(new ReadOnlyMemory<byte>(query), SocketFlags.None, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, timeout.Token).ConfigureAwait(false);

                // ответ на наш запрос: тот же ID и бит QR; остальное (опоздавшие ответы) пропускаем
                if (received < 12 || buffer[0] != (byte)(id >> 8) || buffer[1] != (byte)id || (buffer[2] & 0x80) == 0)
                    continue;

                var ms = (int)sw.ElapsedMilliseconds;
                var rcode = buffer[3] & 0x0F;
                return rcode switch
                {
                    0 or 3 => (ms, "", false),                                   // NOERROR, NXDOMAIN — сервер работает
                    5 => (-1, "сервер отклонил запрос (REFUSED)", true),
                    2 => (-1, "сервер не смог ответить (SERVFAIL)", false),
                    _ => (-1, $"ответ с ошибкой (RCODE {rcode})", false)
                };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var seconds = AttemptTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            return (-1, $"нет ответа за {seconds} с", false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
        {
            return (-1, $"порт {endpoint.Port} закрыт: сервер не принимает обычный DNS", true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable
                                             or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
        {
            return endpoint.AddressFamily == AddressFamily.InterNetworkV6
                ? (-1, "нет маршрута до сервера: IPv6 недоступен", true)
                : (-1, "нет маршрута до сервера", true);
        }
    }

    /// <summary>DNS-запрос A-записи <see cref="ProbeName"/> с флагом рекурсии.</summary>
    private static byte[] BuildQuery(ushort id)
    {
        var labels = ProbeName.Split('.');
        var query = new byte[12 + labels.Sum(l => l.Length + 1) + 1 + 4];
        query[0] = (byte)(id >> 8);
        query[1] = (byte)id;
        query[2] = 0x01;            // RD: рекурсивный запрос
        query[5] = 1;               // QDCOUNT = 1

        var i = 12;
        foreach (var label in labels)
        {
            query[i++] = (byte)label.Length;
            foreach (var c in label) query[i++] = (byte)c;
        }
        query[i++] = 0;             // конец имени
        query[i++] = 0;
        query[i++] = 1;             // QTYPE = A
        query[i++] = 0;
        query[i] = 1;               // QCLASS = IN
        return query;
    }
}
