using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>
/// Инъекция TCP-сегментов в уже установленное соединение — то, что делает nfqws из zapret
/// и без чего фейк-пакет бесполезен: «фейк» должен уйти <b>в том же потоке</b> (те же адреса
/// и порты, тот же номер последовательности), иначе DPI видит чужое соединение и не обманывается.
///
/// Обычный сокет так не умеет: он пишет байты в свой поток по порядку. Поэтому сегмент
/// собирается вручную (IP + TCP заголовки) и отправляется raw-сокетом. Windows разрешает такое
/// только процессу с правами администратора — если прав нет, инъекция просто не выполняется,
/// и остаются сокетные приёмы (разрыв, disorder, мультисплит).
///
/// Фейк не должен дойти до сервера, но должен быть разобран DPI. Для этого в сегменте
/// портится то, что проверяет сервер, и не портится то, что проверяет DPI:
/// приём «TTL меньше нужного», битая контрольная сумма TCP, сдвинутый seq (см. <see cref="DpiFooling"/>).
/// </summary>
public sealed class DpiRawFlow : IDisposable
{
    private const int SioRcvAll = unchecked((int)0x98000001);   // «принимать все пакеты» (SIO_RCVALL)
    private const int HeaderMax = 60;          // IP + TCP заголовки без опций у нас всегда 40
    private const int Syn = 0x02;
    private const int Ack = 0x10;
    private const int Push = 0x08;

    /// <summary>Сколько ждём SYN/ACK, чтобы узнать номера последовательности.</summary>
    private static readonly TimeSpan LearnTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Сколько ждём, когда видно, что сырой сокет пакеты не отдаёт: ждать нечего, фейк уходит
    /// «слепым» (это рабочий режим, а не сбой), и держать соединение лишние 700 мс незачем.
    /// </summary>
    private static readonly TimeSpan BlindTimeout = TimeSpan.FromMilliseconds(30);

    /// <summary>Сколько потоков подряд без SYN/ACK — и ждать перестаём.</summary>
    private const int BlindRunsBeforeGivingUp = 3;

    /// <summary>
    /// Каждый N-й раз всё равно ждём: слежение может заработать (отключили антивирус,
    /// обновился драйвер) — тогда фейк снова пойдёт с номерами последовательности.
    /// </summary>
    private const int RecheckEvery = 32;

    private static int _blindRuns;
    private static int _blindWaits;

    private static readonly object SendGate = new();
    private static Socket? _sender;
    private static bool? _available;

    // Счётчики для журнала: по ним видно, работает ли слежение вообще.
    // «Принято 0» — сырой сокет не отдаёт пакеты, «принято N, разобрано 0» — пакеты
    // не разбираются (не тот формат или не тот интерфейс), «разобрано N» — всё в порядке.
    private static long _packets;
    private static long _tcpPackets;
    private static long _candidates;
    private static long _matched;
    private static string _captureNote = "";

    private readonly CancellationTokenSource _cts = new();
    private readonly Socket? _capture;
    private readonly IPAddress _local;
    private readonly IPAddress _remote;
    private readonly int _localPort;
    private readonly int _remotePort;
    private volatile bool _learned;

    private uint _seq;      // наш номер последовательности (ISN)
    private uint _peerSeq;  // номер последовательности сервера

    private DpiRawFlow(Socket? capture, IPAddress local, IPAddress remote, int localPort, int remotePort)
    {
        _capture = capture;
        _local = local;
        _remote = remote;
        _localPort = localPort;
        _remotePort = remotePort;
    }

    /// <summary>
    /// Слежение за потоком сработало хотя бы раз: raw-сокеты не только разрешены, но и отдают
    /// SYN/ACK. По этому признаку видно разницу между «нет прав» и «не поймали номер
    /// последовательности» — во втором случае фейк тоже не уходит, но права тут ни при чём.
    /// </summary>
    public static bool LearnedOnce { get; private set; }

    /// <summary>Сколько датаграмм принял сырой сокет (для журнала).</summary>
    public static long Packets => Interlocked.Read(ref _packets);

    /// <summary>Сколько принятых датаграмм оказались IPv4/TCP.</summary>
    public static long TcpPackets => Interlocked.Read(ref _tcpPackets);

    /// <summary>Сколько IPv4/TCP датаграмм относились к нашему потоку (адреса и порты совпали).</summary>
    public static long Candidates => Interlocked.Read(ref _candidates);

    /// <summary>Сколько раз в потоке опознан SYN/ACK — после этого номера последовательности известны.</summary>
    public static long Matched => Interlocked.Read(ref _matched);

    /// <summary>Чем кончилось включение «принимать всё» (или почему не вышло) — для журнала.</summary>
    public static string CaptureNote => _captureNote;

    /// <summary>Windows вообще разрешает raw-сокеты? Результат запоминаем: проверка не бесплатная.</summary>
    public static bool Available
    {
        get
        {
            if (_available is { } known) return known;
            _available = Probe();
            return _available.Value;
        }
    }

    /// <summary>
    /// Начать слежение за соединением ДО подключения (иначе SYN/ACK с номерами
    /// последовательности пролетит мимо). Возвращает null, если инъекция недоступна —
    /// это не ошибка, просто работаем сокетными приёмами.
    /// </summary>
    public static DpiRawFlow? TryBegin(IPAddress local, IPAddress remote, int localPort, int remotePort)
    {
        if (!Available) return null;
        if (local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
            return null;   // сборка пакетов сделана для IPv4; для IPv6 оставляем сокетные приёмы

        Socket? capture = null;
        try
        {
            capture = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            capture.Bind(new IPEndPoint(local, 0));
            capture.ReceiveBufferSize = 1 << 20;
            try
            {
                // «принимать всё» на этом интерфейсе: так SYN/ACK точно попадёт в сокет,
                // даже если Windows отдаёт часть пакетов другим raw-сокетам
                capture.IOControl(SioRcvAll, BitConverter.GetBytes(1), null);
                _captureNote = "rcvall";
            }
            catch (Exception ex)
            {
                // Без этого режима Windows может не отдавать сырому сокету чужие пакеты —
                // в журнале это будет видно по «принято 0».
                _captureNote = "no-rcvall " + ex.GetType().Name;
            }
        }
        catch
        {
            capture?.Dispose();
            _available = false;
            return null;
        }

        var flow = new DpiRawFlow(capture, local, remote, localPort, remotePort);
        _ = flow.CaptureLoopAsync();
        return flow;
    }

    /// <summary>Номера последовательности уже известны — фейк получит правильные seq/ack.</summary>
    public bool Learned => _learned;

    /// <summary>
    /// Дать слежению время поймать SYN/ACK (подключение уже произошло). Если пакеты не отдаются
    /// сырому сокету, ожидание сокращается до минимума: иначе каждое соединение к площадке
    /// задерживалось бы на <see cref="LearnTimeout"/> без всякой пользы.
    /// </summary>
    public async Task LearnAsync(CancellationToken ct = default)
    {
        var wait = LearnTimeout;
        if (!LearnedOnce && Volatile.Read(ref _blindRuns) >= BlindRunsBeforeGivingUp)
        {
            var tries = Interlocked.Increment(ref _blindWaits);
            wait = tries % RecheckEvery == 0 ? LearnTimeout : BlindTimeout;
        }

        var until = DateTime.UtcNow + wait;
        while (!_learned && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            await Task.Delay(15, ct);
    }

    /// <summary>
    /// Отправить фейк-пакет: те же адреса и порты, тот же номер последовательности, что у первых
    /// байтов настоящего ClientHello. Возвращает число отправленных байт (0 — не удалось).
    /// </summary>
    public int SendFake(byte[] payload, int length, int ttl, DpiFooling fooling, DpiFakePlacement placement)
    {
        if (length <= 0 || length > 1400) return 0;

        // Номера последовательности неизвестны — сегмент всё равно отправляем («слепой» фейк):
        // DPI разбирает полезную нагрузку, а не номер, а сервер такой сегмент выбрасывает,
        // потому что случайный номер почти наверняка вне окна приёма. Обманки badsum/noack
        // добавляются вызывающим: они гарантируют, что сервер сегмент не примет.
        var seq = _learned ? _seq + 1 : RandomSeq();
        var ack = _learned ? _peerSeq + 1 : 0;

        // «в прошлом» окна: фейк занимает байты ДО начала настоящих данных — сервер считает их
        // уже полученными и выбрасывает, а DPI видит ClientHello раньше настоящего (seqovl у zapret)
        if (placement == DpiFakePlacement.Ahead) seq -= (uint)length;
        if ((fooling & DpiFooling.BadSeq) != 0) seq -= 0x10000000;
        if ((fooling & DpiFooling.DataNoAck) != 0) ack = 0;

        var packet = BuildPacket(payload, length, seq, ack, ttl, fooling);
        return SendRaw(packet, _remote);
    }

    public void Dispose()
    {
        // счёт по «поймали или нет»: по нему следующее соединение решает, ждать ли SYN/ACK
        if (_learned) Interlocked.Exchange(ref _blindRuns, 0);
        else Interlocked.Increment(ref _blindRuns);

        // _cts не освобождаем: слежение за потоком может в этот момент читать его токен,
        // а сборщик мусора сам уберёт источник — он без таймера
        try { _cts.Cancel(); } catch { /* уже завершено */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ приём

    /// <summary>
    /// Слежение за потоком: нам нужен только SYN/ACK сервера — в нём лежат оба номера
    /// последовательности (наш ISN = ack-1, ISN сервера = seq).
    /// </summary>
    private async Task CaptureLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _capture!.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                if (read <= 0) continue;

                Interlocked.Increment(ref _packets);
                if (!TryParse(buffer.AsSpan(0, read))) continue;

                Interlocked.Increment(ref _matched);
                _learned = true;
            }
        }
        catch { /* соединение закрыли или сокет сняли — слежение больше не нужно */ }
    }

    private bool TryParse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 40) return false;
        if ((packet[0] >> 4) != 4) return false;

        var ipLength = (packet[0] & 0x0F) * 4;
        if (ipLength < 20 || packet.Length < ipLength + 20) return false;
        if (packet[9] != 6) return false;   // не TCP

        Interlocked.Increment(ref _tcpPackets);

        var src = new IPAddress(packet.Slice(12, 4));
        var dst = new IPAddress(packet.Slice(16, 4));
        if (!src.Equals(_remote) || !dst.Equals(_local)) return false;

        var tcp = packet[ipLength..];
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
        if (srcPort != _remotePort || dstPort != _localPort) return false;

        Interlocked.Increment(ref _candidates);

        var flags = tcp[13];
        if ((flags & Syn) == 0 || (flags & Ack) == 0) return false;

        var peerSeq = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
        var ourAck = BinaryPrimitives.ReadUInt32BigEndian(tcp[8..]);
        if (ourAck == 0) return false;

        _peerSeq = peerSeq;
        _seq = ourAck - 1;
        LearnedOnce = true;

        // слежение работает: ожидание SYN/ACK снова полное, фейк пойдёт с номерами
        Interlocked.Exchange(ref _blindRuns, 0);
        Interlocked.Exchange(ref _blindWaits, 0);
        return true;
    }

    // ------------------------------------------------------------------ отправка

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Tcp);
            return true;
        }
        catch
        {
            return false;   // нет прав администратора (или raw-сокеты запрещены политикой)
        }
    }

    private static uint RandomSeq() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    /// <summary>Собрать IPv4 + TCP сегмент с указанными seq/ack и полезной нагрузкой.</summary>
    private byte[] BuildPacket(byte[] payload, int length, uint seq, uint ack, int ttl, DpiFooling fooling)
    {
        var total = 40 + length;
        var packet = new byte[total];
        packet[0] = 0x45;                                     // IPv4, длина заголовка 20
        packet[1] = 0;                                        // DSCP/ECN
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)Random.Shared.Next(1, 0xFFFF));
        packet[6] = 0x40;                                     // DF
        packet[8] = (byte)Math.Clamp(ttl, 1, 255);
        packet[9] = 6;                                        // TCP
        _local.GetAddressBytes().CopyTo(packet, 12);
        _remote.GetAddressBytes().CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), 0);   // контрольная сумма ниже

        var tcp = packet.AsSpan(20);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = 5 << 4;                                     // длина TCP заголовка 20
        var flags = Push | Ack;
        if ((fooling & DpiFooling.DataNoAck) != 0) flags = Push;   // без ACK сервер сегмент игнорирует
        tcp[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..], 64240);   // окно как у настоящего потока
        payload.AsSpan(0, Math.Min(length, payload.Length)).CopyTo(tcp[20..]);

        // контрольная сумма IP — всегда корректная: её проверяют промежуточные узлы
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), Checksum(packet.AsSpan(0, 20)));

        var tcpSum = TcpChecksum(packet, 20, length);
        // BadSum: сервер пакет выбросит, DPI контрольную сумму не считает
        if ((fooling & DpiFooling.BadSum) != 0) tcpSum = (ushort)(tcpSum ^ 0x1234);
        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..], tcpSum);

        return packet;
    }

    private static int SendRaw(byte[] packet, IPAddress destination)
    {
        try
        {
            lock (SendGate)
            {
                _sender ??= new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Tcp);
                return _sender.SendTo(packet, new IPEndPoint(destination, 0));
            }
        }
        catch
        {
            return 0;   // права отозвали или сокет закрылся: обход просто останется без фейка
        }
    }

    /// <summary>Обычная контрольная сумма интернета (RFC 1071) по 16-битным словам.</summary>
    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if ((data.Length & 1) != 0) sum += (uint)(data[^1] << 8);

        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>Контрольная сумма TCP считается по псевдозаголовку (адреса, протокол, длина).</summary>
    private static ushort TcpChecksum(byte[] packet, int ipLength, int payloadLength)
    {
        uint sum = 0;
        sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12, 2));   // src IP
        sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14, 2));
        sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16, 2));   // dst IP
        sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(18, 2));
        sum += 6;                                                            // протокол TCP
        sum += (uint)(20 + payloadLength);                                   // длина TCP

        var tcp = packet.AsSpan(ipLength, 20);
        for (var i = 0; i + 1 < tcp.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(tcp[i..]);
        if ((tcp.Length & 1) != 0) sum += (uint)(tcp[^1] << 8);

        var payload = packet.AsSpan(ipLength + 20, payloadLength);
        for (var i = 0; i + 1 < payload.Length; i += 2)
            sum += BinaryPrimitives.ReadUInt16BigEndian(payload[i..]);
        if ((payload.Length & 1) != 0) sum += (uint)(payload[^1] << 8);

        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
