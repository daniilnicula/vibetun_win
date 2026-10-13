using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>
/// Измерение задержки до сервера профиля.
/// Без TUN: TCP-коннект, при неудаче — ICMP.
/// При активном TUN прямой TCP-коннект бесполезен: локальный стек туннеля отвечает
/// на рукопожатие сам за ~1 мс, не доходя до сервера. Замер идёт через локальный вход
/// ядра (HTTP CONNECT до сервера профиля через текущий прокси) — это реальный RTT
/// до сервера через поднятый VPN.
/// </summary>
public sealed class LatencyService
{
    private readonly CoreService? _core;

    public LatencyService(CoreService? core = null) => _core = core;

    public async Task<int> MeasureAsync(ProxyProfile profile, CancellationToken ct = default)
    {
        var node = profile.Node;
        if (string.IsNullOrWhiteSpace(node.Server) || node.Port <= 0) return -1;

        if (_core is not null && _core.TunActive && _core.LocalListenPort > 0)
        {
            // SOCKS5 — до 4 с, HTTP CONNECT — до 3 с: при отказе входа не ждём 12 с на профиль
            var viaProxy = await TryViaLocalProxyAsync(node.Server, node.Port,
                _core.LocalListenAddress, _core.LocalListenPort, TimeSpan.FromSeconds(4), ct);
            if (viaProxy >= 0) return viaProxy;
            AppLog.Write($"Замер {node.Server}:{node.Port}: вход ядра недоступен — fallback на прямой замер (значение будет занижено TUN)");
        }

        var tcp = await TryTcpAsync(node.Server, node.Port, TimeSpan.FromSeconds(4), ct);
        if (tcp >= 0) return tcp;

        var icmp = await TryIcmpAsync(node.Server, ct);
        if (icmp >= 0) return icmp;

        return -1;
    }

    /// <summary>
    /// Рукопожатие с локальным входом ядра и CONNECT до host:port через него. Время — до
    /// ответа прокси на CONNECT: loopback (~0 мс) + реальный дозвон прокси до сервера.
    /// Сначала SOCKS5 (подходит и для mixed, и для socks входа), при отказе — HTTP CONNECT
    /// (режим http). Ядро при активном TUN само направит соединение по правилам маршрутизации.
    /// </summary>
    private static async Task<int> TryViaLocalProxyAsync(string host, int port,
        string listenAddress, int listenPort, TimeSpan timeout, CancellationToken ct)
    {
        var viaSocks = await TryViaSocks5Async(host, port, listenAddress, listenPort, timeout, ct);
        if (viaSocks >= 0) return viaSocks;
        return await TryViaHttpConnectAsync(host, port, listenAddress, listenPort,
            TimeSpan.FromSeconds(Math.Min(3, timeout.TotalSeconds)), ct);
    }

    private static string LoopbackOf(string listenAddress) =>
        listenAddress is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : listenAddress.Trim('[', ']');

    private static async Task<int> TryViaSocks5Async(string host, int port,
        string listenAddress, int listenPort, TimeSpan timeout, CancellationToken ct)
    {
        var tag = $"Замер {host}:{port} через {LoopbackOf(listenAddress)}:{listenPort} SOCKS5";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(LoopbackOf(listenAddress)), listenPort), linked.Token);

            // greeting: ver 5, один метод — без авторизации
            await socket.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, linked.Token);
            var resp = await RecvExactAsync(socket, 2, linked.Token);
            if (resp is null || resp[0] != 0x05 || resp[1] != 0x00)
            {
                AppLog.Write(tag + ": вход ядра не принял greeting" + (resp is null ? " (нет ответа)" : $" (ver={resp[0]}, method={resp[1]})"));
                return -1;
            }

            // CONNECT: доменная форма (ATYP 03) работает и для IP, и для имён
            var hostBytes = Encoding.ASCII.GetBytes(host);
            if (hostBytes.Length > 255) return -1;
            var req = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)hostBytes.Length };
            req.AddRange(hostBytes);
            req.AddRange(BitConverter.GetBytes((ushort)port).Reverse());   // сетевой порядок
            await socket.SendAsync(req.ToArray(), SocketFlags.None, linked.Token);

            var head = await RecvExactAsync(socket, 4, linked.Token);
            sw.Stop();
            if (head is null || head[0] != 0x05)
            {
                AppLog.Write(tag + ": нет ответа на CONNECT");
                return -1;
            }
            if (head[1] != 0x00)   // ошибка CONNECT: ядро не смогло достучаться до сервера
            {
                AppLog.Write(tag + $": вход отказал rep={head[1]} за {sw.ElapsedMilliseconds} мс — ядро не достучалось до {host}:{port} по своим маршрутам");
                return -1;
            }
            AppLog.Write(tag + $": OK {sw.ElapsedMilliseconds} мс");
            return (int)sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(tag + ": таймаут — вход ядра не ответил за " + (int)timeout.TotalSeconds + " с");
        }
        catch (Exception e)
        {
            AppLog.Write(tag + ": " + e.GetType().Name + " " + e.Message);
        }
        return -1;
    }

    private static async Task<int> TryViaHttpConnectAsync(string host, int port,
        string listenAddress, int listenPort, TimeSpan timeout, CancellationToken ct)
    {
        var tag = $"Замер {host}:{port} через {LoopbackOf(listenAddress)}:{listenPort} HTTP CONNECT";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(LoopbackOf(listenAddress)), listenPort), linked.Token);

            var request = Encoding.ASCII.GetBytes(
                $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
            await socket.SendAsync(request, SocketFlags.None, linked.Token);

            var buffer = new byte[512];
            var received = new List<byte>();
            var headersComplete = false;
            while (received.Count < 512)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, linked.Token);
                if (n <= 0) break;
                received.AddRange(buffer.AsSpan(0, n).ToArray());
                if (Encoding.ASCII.GetString(received.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    headersComplete = true;
                    break;
                }
            }
            sw.Stop();

            if (!headersComplete)
            {
                AppLog.Write(tag + ": нет ответа от входа ядра");
                return -1;
            }
            var head = Encoding.ASCII.GetString(received.ToArray());
            if (head.Contains(" 200", StringComparison.Ordinal))
            {
                AppLog.Write(tag + $": OK {sw.ElapsedMilliseconds} мс");
                return (int)sw.ElapsedMilliseconds;
            }
            var statusLine = head.Split('\r')[0];
            AppLog.Write(tag + $": вход отказал ({statusLine.Trim()}) — ядро не достучалось до {host}:{port}");
            return -1;
        }
        catch (OperationCanceledException)
        {
            AppLog.Write(tag + ": таймаут — вход ядра не ответил за " + (int)timeout.TotalSeconds + " с");
        }
        catch (Exception e)
        {
            AppLog.Write(tag + ": " + e.GetType().Name + " " + e.Message);
        }
        return -1;
    }

    private static async Task<byte[]?> RecvExactAsync(Socket socket, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await socket.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (n <= 0) return null;
            got += n;
        }
        return buf;
    }

    public static async Task<int> TryTcpAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out var direct)
                ? new[] { direct }
                : await Dns.GetHostAddressesAsync(host, ct);

            foreach (var address in addresses.Take(2))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                var sw = Stopwatch.StartNew();
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(timeout);
                    await socket.ConnectAsync(new IPEndPoint(address, port), linked.Token);
                    sw.Stop();
                    return (int)sw.ElapsedMilliseconds;
                }
                catch
                {
                    // пробуем следующий адрес
                }
                finally
                {
                    socket.Dispose();
                }
            }
        }
        catch { /* ignore */ }
        return -1;
    }

    public static async Task<int> TryIcmpAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            if (reply.Status == IPStatus.Success) return (int)reply.RoundtripTime;
        }
        catch { /* ignore */ }
        return -1;
    }
}
