using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>
/// Реестр протоколов для редактора профиля: базовый список и метаданные строк + то,
/// что поддерживает установленное ядро (probe типа) и что объявляет <c>sing-box schema</c>.
/// Новые протоколы ядра появляются в списке без обновления приложения: после смены ядра
/// вызывается <see cref="CoreService.ProbeOutboundTypesAsync"/>, который обновляет реестр
/// через <see cref="Update"/>. До первого пробы показывается только <see cref="Known"/>.
/// </summary>
public static class ProtocolRegistry
{
    /// <summary>Базовый список протоколов (текущий порядок редактора) — виден до первого пробы ядра.</summary>
    public static IReadOnlyList<string> Known { get; } = new[]
    {
        ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Trojan, ProtocolIds.Shadowsocks,
        ProtocolIds.Socks, ProtocolIds.Http, ProtocolIds.Hysteria2, ProtocolIds.Hysteria,
        ProtocolIds.Tuic, ProtocolIds.AnyTls, ProtocolIds.Wireguard, ProtocolIds.AmneziaWg,
        ProtocolIds.Ssh, ProtocolIds.MtProto, ProtocolIds.Tunnel
    };

    /// <summary>Показываются всегда, даже если ядро их не поддерживает (MTProto и сырой JSON — свои форматы).</summary>
    public static IReadOnlySet<string> AlwaysShow { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProtocolIds.MtProto, ProtocolIds.Tunnel };

    /// <summary>Инфраструктурные outbound'ы ядра (маршрутизация/селекторы) — не протоколы подключения.</summary>
    public static IReadOnlySet<string> Infra { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "direct", "block", "selector", "urltest", "dns", "redirect", "tproxy", "chain", "bridge"
        };

    private static readonly HashSet<string> KnownSet =
        Known.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TlsCapable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Trojan, ProtocolIds.Hysteria2,
            ProtocolIds.Hysteria, ProtocolIds.Tuic, ProtocolIds.AnyTls, ProtocolIds.Http
        };

    private static readonly HashSet<string> QuicSet =
        new(StringComparer.OrdinalIgnoreCase) { ProtocolIds.Hysteria2, ProtocolIds.Hysteria, ProtocolIds.Tuic };

    private static readonly HashSet<string> TransportableSet =
        new(StringComparer.OrdinalIgnoreCase) { ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Trojan };

    private static readonly HashSet<string> UuidRowSet =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Tuic, ProtocolIds.Socks, ProtocolIds.Http, ProtocolIds.Ssh
        };

    private static readonly HashSet<string> PasswordRowSet =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ProtocolIds.Trojan, ProtocolIds.Shadowsocks, ProtocolIds.Hysteria2, ProtocolIds.Hysteria, ProtocolIds.AnyTls
        };

    /// <summary>
    /// Снимок состояния: одна ссылка — читатели из UI получают согласованные данные
    /// без блокировок (проба ядра подменяет состояние целиком).
    /// </summary>
    private sealed class State
    {
        public static readonly State Empty = new();

        /// <summary>Типы, которые ядро приняло в check; null — ядро ещё не опрашивалось.</summary>
        public HashSet<string>? Supported;

        /// <summary>Порядок типов из <c>sing-box schema</c>; null — схема недоступна.</summary>
        public List<string>? SchemaOrder;

        /// <summary>Разрешённые верхнеуровневые ключи варианта outbounds из схемы; null — нет схемы.</summary>
        public Dictionary<string, HashSet<string>>? Fields;
    }

    private static State _state = State.Empty;

    /// <summary>Применить результат пробы ядра и разбора схемы (вызывается из CoreService).</summary>
    public static void Update(
        IEnumerable<string>? supported,
        IEnumerable<string>? schemaOrder,
        IReadOnlyDictionary<string, List<string>>? fields)
    {
        var st = new State
        {
            Supported = supported is null
                ? null
                : new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase),
            SchemaOrder = schemaOrder?.ToList(),
            Fields = null
        };
        if (fields is not null)
        {
            st.Fields = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, keys) in fields)
                st.Fields[type] = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        }
        _state = st;
    }

    /// <summary>Вернуться к базовому списку (нет ядра / сбой пробы).</summary>
    public static void Reset() => _state = State.Empty;

    /// <summary>
    /// Список протоколов для ComboBox редактора:
    /// <see cref="Known"/> ∩ поддерживаемые (AlwaysShow — всегда), затем extras из schema-порядка
    /// (поддерживаемые вне Known и Infra), затем AlwaysShow, затем текущий протокол, если его нет в списке.
    /// </summary>
    public static IReadOnlyList<string> ProfileList(string? current)
    {
        var st = _state;
        var list = new List<string>(Known.Count + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id)
        {
            if (seen.Add(id)) list.Add(id);
        }

        // Known ∩ поддерживаемые (до пробы — весь Known), AlwaysShow добавляются в конце
        foreach (var id in Known)
        {
            if (AlwaysShow.Contains(id)) continue;
            if (st.Supported is null || st.Supported.Contains(id)) Add(id);
        }

        // Новые типы ядра — в порядке их объявления в схеме
        if (st.Supported is not null)
        {
            var extras = st.Supported
                .Where(t => !KnownSet.Contains(t) && !Infra.Contains(t) && !AlwaysShow.Contains(t));
            if (st.SchemaOrder is { Count: > 0 })
            {
                var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < st.SchemaOrder.Count; i++) order[st.SchemaOrder[i]] = i;
                extras = extras.OrderBy(t => order.TryGetValue(t, out var idx) ? idx : int.MaxValue)
                    .ThenBy(t => t, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                extras = extras.OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
            }
            foreach (var t in extras) Add(t);
        }

        foreach (var t in AlwaysShow) Add(t);

        // Текущий протокол профиля должен присутствовать, иначе SelectedItem ComboBox'а пуст
        if (!string.IsNullOrWhiteSpace(current)) Add(current!);

        return list;
    }

    /// <summary>Протокол есть в базовом списке (не «новый из схемы»).</summary>
    public static bool IsKnown(string? id) => id is not null && KnownSet.Contains(id);

    /// <summary>Секция TLS: для известных — по списку, для неизвестных (новых типов) — показать, но выключенной.</summary>
    public static bool IsTlsCapable(string? id) =>
        id is not null && (!KnownSet.Contains(id) || TlsCapable.Contains(id));

    /// <summary>QUIC-протоколы (секция серверных портов).</summary>
    public static bool IsQuic(string? id) => id is not null && QuicSet.Contains(id);

    /// <summary>Протоколы с секцией транспорта (ws/grpc/…).</summary>
    public static bool IsTransportable(string? id) => id is not null && TransportableSet.Contains(id);

    /// <summary>Строка UUID: набор известных протоколов плюс любые неизвестные (новые типы).</summary>
    public static bool ShowUuidRow(string? id) => id is not null && (!KnownSet.Contains(id) || UuidRowSet.Contains(id));

    /// <summary>Строка Password (отдельная): только известные протоколы, чьи парольные строки нет в панели UUID.</summary>
    public static bool ShowPasswordRow(string? id) => id is not null && PasswordRowSet.Contains(id);

    /// <summary>Строка Method (cipher) — только Shadowsocks.</summary>
    public static bool ShowMethodRow(string? id) =>
        id is not null && id.Equals(ProtocolIds.Shadowsocks, StringComparison.OrdinalIgnoreCase);

    /// <summary>Строка Flow — только VLESS/VMess.</summary>
    public static bool ShowFlowRow(string? id) =>
        id is not null && (id.Equals(ProtocolIds.Vless, StringComparison.OrdinalIgnoreCase) ||
                           id.Equals(ProtocolIds.Vmess, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Разрешённые верхнеуровневые ключи outbound-варианта из схемы ядра.
    /// null — схема недоступна или типа нет в схеме: универсальный builder эмитирует всё.
    /// </summary>
    public static IReadOnlySet<string>? FieldsFor(string? type)
    {
        if (type is null) return null;
        var st = _state;
        if (st.Fields is null) return null;
        return st.Fields.TryGetValue(type, out var keys) ? keys : null;
    }
}
