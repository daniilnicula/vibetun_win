using System.Text.Json;
using VibeTun.Models;

namespace VibeTun.Utils;

/// <summary>
/// Конвертация JSON-конфигов в профиль: поддерживаются
/// sing-box (outbounds/endpoints) и Xray/3x-ui (outbounds с protocol + streamSettings).
/// </summary>
public static class JsonConfigConverter
{
    private static readonly string[] NonProxyTypes =
        { "direct", "block", "dns", "selector", "urltest", "tun", "mixed", "tproxy", "redirect" };

    /// <summary>Xray-протоколы, которые не являются прокси-профилями; всё остальное
    /// (включая новые типы вроде tuic из обновлённых панелей) импортируется без правок кода.</summary>
    private static readonly string[] XrayNonProxyTypes = { "freedom", "blackhole", "dns" };

    public static ProxyProfile? Convert(JsonElement root, string raw, out string? error)
    {
        error = null;

        if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            var first = outbounds.EnumerateArray().FirstOrDefault(el =>
            {
                if (el.ValueKind != JsonValueKind.Object) return false;
                if (Prop(el, "protocol") is { } xrayProtocol)
                    return !XrayNonProxyTypes.Contains(xrayProtocol.ToLowerInvariant());
                return Prop(el, "type") is { } singBoxType && !NonProxyTypes.Contains(singBoxType.ToLowerInvariant());
            });

            if (first.ValueKind == JsonValueKind.Object)
            {
                return Prop(first, "protocol") is not null
                    ? FromXray(first, raw, out error)
                    : FromSingBox(first, raw, out error);
            }
        }

        // sing-box 1.11+: endpoints (wireguard и т.п.)
        if (root.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            var first = eps.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                var p = FromSingBoxEndpoint(first, raw, out error);
                if (p is not null) return p;
            }
        }

        // одиночный outbound без обёртки
        if (Prop(root, "type") is not null) return FromSingBox(root, raw, out error);
        if (Prop(root, "protocol") is not null) return FromXray(root, raw, out error);

        error = L.T("S0578");
        return null;
    }

    // ------------------------------------------------------------------ JSON-подписки
    /// <summary>
    /// Все прокси-профили из JSON-подписки или конфига: одиночный outbound, конфиг
    /// <c>{outbounds:[…]}</c> или массив конфигов (формат JSON-подписки 3x-ui — по одному
    /// конфигу на клиентский профиль). Инфраструктурные outbound'ы (freedom/blackhole/dns,
    /// direct/selector/…) пропускаются молча; имя профиля берётся из <c>remarks</c> конфига,
    /// когда профиль из него ровно один.
    /// </summary>
    public static List<ProxyProfile> ConvertAll(JsonElement root, out List<string> errors)
    {
        var result = new List<ProxyProfile>();
        errors = new List<string>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("A JSON subscription entry is not an object.");
                    continue;
                }
                Collect(item, item.GetRawText(), result, errors);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            Collect(root, root.GetRawText(), result, errors);
        }
        else
        {
            errors.Add(L.T("S0578"));
        }
        return result;
    }

    private static void Collect(JsonElement node, string raw, List<ProxyProfile> result, List<string> errors)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var before = result.Count;
        var isWrapper = false;

        if (node.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
        {
            isWrapper = true;
            foreach (var o in outbounds.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var xrayProtocol = Prop(o, "protocol");
                if (xrayProtocol is not null && XrayNonProxyTypes.Contains(xrayProtocol.ToLowerInvariant()))
                    continue;
                var singBoxType = Prop(o, "type");
                if (xrayProtocol is null && singBoxType is not null &&
                    NonProxyTypes.Contains(singBoxType.ToLowerInvariant()))
                    continue;
                Collect(o, o.GetRawText(), result, errors);
            }
        }

        if (node.TryGetProperty("endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
        {
            isWrapper = true;
            foreach (var ep in endpoints.EnumerateArray())
            {
                if (ep.ValueKind != JsonValueKind.Object) continue;
                var endpointProfile = FromSingBoxEndpoint(ep, ep.GetRawText(), out var endpointError);
                if (endpointProfile is not null) result.Add(endpointProfile);
                else if (!string.IsNullOrWhiteSpace(endpointError)) errors.Add(endpointError!);
            }
        }

        if (!isWrapper)
        {
            var profile = Convert(node, raw, out var error);
            if (profile is not null) result.Add(profile);
            else if (!string.IsNullOrWhiteSpace(error)) errors.Add(error!);
            return;
        }

        // единственному профилю конфига — имя из remarks (3x-ui подписывает им клиентские конфиги)
        if (result.Count - before == 1 && Prop(node, "remarks") is { } remarks &&
            !string.IsNullOrWhiteSpace(remarks))
        {
            result[result.Count - 1].Name = remarks;
        }
    }

    // ------------------------------------------------------------------ sing-box
    private static ProxyProfile? FromSingBox(JsonElement o, string raw, out string? error)
    {
        error = null;
        var type = (Prop(o, "type") ?? "").ToLowerInvariant();
        var tag = Prop(o, "tag") ?? type;
        var node = new ProxyNode
        {
            Server = Prop(o, "server") ?? "",
            Port = Num(o, "server_port") ?? 0,
        };
        var proto = type;

        switch (type)
        {
            case "vless":
                node.Uuid = Prop(o, "uuid");
                node.Flow = Prop(o, "flow");
                node.PacketEncoding = Prop(o, "packet_encoding");
                break;
            case "vmess":
                node.Uuid = Prop(o, "uuid");
                node.Extra["security"] = Prop(o, "security") ?? "auto";
                node.Extra["alterId"] = Num(o, "alter_id")?.ToString() ?? "0";
                break;
            case "trojan":
                node.Password = Prop(o, "password");
                break;
            case "shadowsocks":
                node.Method = Prop(o, "method");
                node.Password = Prop(o, "password");
                break;
            case "socks":
            case "http":
                node.Username = Prop(o, "username");
                node.Password = Prop(o, "password");
                break;
            case "hysteria2":
            case "hysteria":
                node.Password = Prop(o, "password") ?? Prop(o, "auth_str");
                node.UpMbps = Num(o, "up_mbps");
                node.DownMbps = Num(o, "down_mbps");
                if (o.TryGetProperty("obfs", out var obfs) && obfs.ValueKind == JsonValueKind.Object)
                {
                    node.Obfs = Prop(obfs, "type");
                    node.ObfsPassword = Prop(obfs, "password");
                }
                if (Prop(o, "server_ports") is { } sp) node.Extra["mport"] = sp;
                break;
            case "tuic":
                node.Uuid = Prop(o, "uuid");
                node.Password = Prop(o, "password");
                node.CongestionControl = Prop(o, "congestion_control");
                node.UdpRelayMode = Prop(o, "udp_relay_mode");
                break;
            case "anytls":
                node.Password = Prop(o, "password");
                break;
            case "wireguard":
                FillWireGuard(node, o);
                break;
            case "amnezia":
            case "amneziawg":
                // AmneziaWG-исходящий: uuid/пароль (если есть), адрес, пирсы и обфускация;
                // раньше тип падал в default, сервер оставался пустым и профиль отбрасывался
                proto = ProtocolIds.AmneziaWg;
                node.Uuid = Prop(o, "uuid");
                node.Password = Prop(o, "password");
                FillWireGuard(node, o);
                break;
            default:
                // динамически: новый outbound-тип (после обновления панели/ядра) — базовые поля
                // и всё, что не вошло в известные case'ы; инфраструктурные типы конфига — не профиль
                if (NonProxyTypes.Contains(type))
                {
                    error = L.F("S0579", type);
                    return null;
                }
                node.Uuid = Prop(o, "uuid") ?? node.Uuid;
                node.Username = Prop(o, "username") ?? node.Username;
                node.Password = Prop(o, "password") ?? node.Password;
                node.Method = Prop(o, "method") ?? node.Method;
                foreach (var p in o.EnumerateObject())
                {
                    if (p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or
                        JsonValueKind.True or JsonValueKind.False))
                        continue;
                    node.Extra[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? ""
                        : p.Value.GetRawText();
                }
                break;
        }

        FillTls(node, o);
        FillTransport(node, o);

        var profile = new ProxyProfile
        {
            Name = string.IsNullOrWhiteSpace(tag) ? $"{node.Server}:{node.Port}" : tag,
            Protocol = proto,
            RawJson = null,
            Node = node
        };
        // Сервер обязателен для любого типа: у wireguard/amnezia он приходит из
        // пиров (FillWireGuard), а если и оттуда пусто — профиль был бы нерабочим
        // (раньше для wireguard пустой сервер пропускался и создавался брак)
        if (node.Server.Length == 0)
        {
            error = L.T("S0580");
            return null;
        }
        return profile;
    }

    private static ProxyProfile? FromSingBoxEndpoint(JsonElement o, string raw, out string? error)
    {
        error = null;
        var type = (Prop(o, "type") ?? "").ToLowerInvariant();
        // sing-box-lx: «wireguard» (в т.ч. с блоком amnezia) плюс явные типы amnezia/amneziawg —
        // раньше любой тип кроме «wireguard» отбрасывался с ошибкой S0581
        if (type is not ("wireguard" or "amnezia" or "amneziawg"))
        {
            error = L.F("S0581", type);
            return null;
        }

        var node = new ProxyNode();
        FillWireGuard(node, o);
        node.Extra["endpoint_raw"] = "1";
        var profile = new ProxyProfile
        {
            Name = Prop(o, "tag") ?? $"WG {node.Server}",
            Protocol = type is "amnezia" or "amneziawg" || node.HasAwgParams
                ? ProtocolIds.AmneziaWg
                : ProtocolIds.Wireguard,
            Node = node,
            RawJson = null
        };
        profile.Node.Extra["full_json"] = raw;
        return profile;
    }

    private static void FillWireGuard(ProxyNode node, JsonElement o)
    {
        node.WgPrivateKey = Prop(o, "private_key");
        node.WgMtu = Num(o, "mtu");
        if (o.TryGetProperty("address", out var addr))
            node.WgAddress = string.Join(", ", StringList(addr));
        if (o.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array)
        {
            var peer = peers.EnumerateArray().FirstOrDefault();
            if (peer.ValueKind == JsonValueKind.Object)
            {
                node.Server = Prop(peer, "address") ?? node.Server;
                node.Port = Num(peer, "port") ?? node.Port;
                node.WgPeerPublicKey = Prop(peer, "public_key");
                node.WgPresharedKey = Prop(peer, "pre_shared_key");
                node.WgKeepalive = Num(peer, "persistent_keepalive_interval");
                if (peer.TryGetProperty("reserved", out var rsv) && rsv.ValueKind == JsonValueKind.Array)
                {
                    node.WgReserved = string.Join(",", rsv.EnumerateArray().Select(x => x.ToString()));
                    node.WgReservedSet = true;
                }
            }
        }
        else
        {
            if (o.TryGetProperty("peer", out var single) && single.ValueKind == JsonValueKind.Object)
            {
                node.Server = Prop(single, "address") ?? node.Server;
                node.Port = Num(single, "port") ?? node.Port;
                node.WgPeerPublicKey = Prop(single, "public_key");
                node.WgPresharedKey = Prop(single, "pre_shared_key");
                node.WgKeepalive = Num(single, "persistent_keepalive_interval");
            }
        }

        // AmneziaWG-параметры: плоско на объекте либо во вложенном блоке «amnezia»
        // (так их пишет sing-box-lx) — читаем оба варианта, иначе обфускация терялась
        JsonElement awg = o;
        if (o.TryGetProperty("amnezia", out var az) && az.ValueKind == JsonValueKind.Object)
            awg = az;
        int? ANum(string key) => Num(o, key) ?? Num(awg, key);
        string? AProp(string key) => Prop(o, key) ?? Prop(awg, key);
        node.AwgJc = ANum("jc");
        node.AwgJmin = ANum("jmin");
        node.AwgJmax = ANum("jmax");
        node.AwgS1 = ANum("s1");
        node.AwgS2 = ANum("s2");
        node.AwgS3 = ANum("s3");
        node.AwgS4 = ANum("s4");
        node.AwgH1 = AProp("h1");
        node.AwgH2 = AProp("h2");
        node.AwgH3 = AProp("h3");
        node.AwgH4 = AProp("h4");
        node.AwgI1 = AProp("i1");
        node.AwgI2 = AProp("i2");
    }

    private static void FillTls(ProxyNode node, JsonElement o)
    {
        if (!o.TryGetProperty("tls", out var tls) || tls.ValueKind != JsonValueKind.Object) return;
        node.Tls = TlsBool(tls, "enabled");
        node.Sni = Prop(tls, "server_name");
        node.Insecure = TlsBool(tls, "insecure");
        if (tls.TryGetProperty("alpn", out var alpn)) node.Alpn = string.Join(",", StringList(alpn));
        if (tls.TryGetProperty("utls", out var utls) && utls.ValueKind == JsonValueKind.Object)
            node.Fingerprint = Prop(utls, "fingerprint");
        if (tls.TryGetProperty("reality", out var reality) && reality.ValueKind == JsonValueKind.Object && TlsBool(reality, "enabled"))
        {
            node.Reality = true;
            node.PublicKey = Prop(reality, "public_key");
            node.ShortId = Prop(reality, "short_id");
        }
    }

    private static void FillTransport(ProxyNode node, JsonElement o)
    {
        if (!o.TryGetProperty("transport", out var tr) || tr.ValueKind != JsonValueKind.Object) return;
        node.Network = Prop(tr, "type");
        node.Path = Prop(tr, "path");
        node.ServiceName = Prop(tr, "service_name");
        if (tr.TryGetProperty("headers", out var hdrs) && hdrs.ValueKind == JsonValueKind.Object)
            node.Host = Prop(hdrs, "Host") ?? Prop(hdrs, "host");
    }

    // ----------------------------------------------------------------------- Xray
    private static ProxyProfile? FromXray(JsonElement o, string raw, out string? error)
    {
        error = null;
        var xproto = (Prop(o, "protocol") ?? "").ToLowerInvariant();
        // инфраструктурные outbound'ы конфига (freedom/blackhole/dns) — не профиль
        if (XrayNonProxyTypes.Contains(xproto))
        {
            error = L.F("S0582", xproto);
            return null;
        }
        var node = new ProxyNode();
        var proto = xproto switch
        {
            "vless" => ProtocolIds.Vless,
            "vmess" => ProtocolIds.Vmess,
            "trojan" => ProtocolIds.Trojan,
            "shadowsocks" => ProtocolIds.Shadowsocks,
            "socks" => ProtocolIds.Socks,
            "http" => ProtocolIds.Http,
            "wireguard" => ProtocolIds.Wireguard,
            "amneziawg" => ProtocolIds.AmneziaWg,
            "amnezia" => ProtocolIds.AmneziaWg,
            // динамически: протокол из новой панели (например tuic в 3x-ui) — имя берём как есть,
            // разбор настроек ниже универсальный (vnext/users или плоская форма 3x-ui)
            _ => xproto
        };

        if (proto.Length == 0)
        {
            error = L.F("S0582", xproto);
            return null;
        }

        if (o.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            var vnext = FirstOf(settings, "vnext") ?? FirstOf(settings, "servers");
            if (vnext is { } vn)
            {
                node.Server = Prop(vn, "address") ?? "";
                node.Port = Num(vn, "port") ?? 0;
                if (vn.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
                {
                    var u = users.EnumerateArray().FirstOrDefault();
                    if (u.ValueKind == JsonValueKind.Object)
                    {
                        node.Uuid = Prop(u, "id");
                        node.Flow = Prop(u, "flow");
                        node.Password = Prop(u, "password") ?? Prop(u, "pass");
                        node.Username = Prop(u, "user");
                        if (Num(u, "alterId") is { } aid) node.Extra["alterId"] = aid.ToString();
                        if (Prop(u, "security") is { } sc) node.Extra["security"] = sc;
                    }
                }
            }
            else
            {
                // 3x-ui JSON subscriptions use the flat Xray outbound settings form,
                // while imported configs commonly wrap credentials in vnext/users.
                node.Server = Prop(settings, "address") ?? Prop(settings, "servers") ?? "";
                node.Port = Num(settings, "port") ?? 0;
                node.Uuid = Prop(settings, "id") ?? Prop(settings, "uuid");
                node.Flow = Prop(settings, "flow");
                node.Password = Prop(settings, "password") ?? Prop(settings, "pass");
                node.Method = Prop(settings, "method");
                node.Username = Prop(settings, "user") ?? Prop(settings, "username");
                if (Num(settings, "alterId") is { } alterId) node.Extra["alterId"] = alterId.ToString();
                if (Prop(settings, "security") is { } security) node.Extra["security"] = security;
            }

            if (proto == ProtocolIds.Wireguard || proto == ProtocolIds.AmneziaWg)
            {
                node.WgPrivateKey = Prop(settings, "secretKey");
                if (settings.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array)
                {
                    var pr = peers.EnumerateArray().FirstOrDefault();
                    if (pr.ValueKind == JsonValueKind.Object)
                    {
                        node.WgPeerPublicKey = Prop(pr, "publicKey");
                        node.WgPresharedKey = Prop(pr, "preSharedKey");
                        var endpoint = Prop(pr, "endpoint") ?? "";
                        var idx = endpoint.LastIndexOf(':');
                        if (idx > 0)
                        {
                            node.Server = endpoint[..idx];
                            node.Port = Text.ToInt(endpoint[(idx + 1)..]);
                        }
                    }
                }
                if (settings.TryGetProperty("address", out var addrs)) node.WgAddress = string.Join(", ", StringList(addrs));

                // AmneziaWG-обфускация: плоским ключом в settings или вложенным блоком amnezia
                var awg = settings;
                if (settings.TryGetProperty("amnezia", out var az) && az.ValueKind == JsonValueKind.Object)
                    awg = az;
                node.AwgJc = Num(settings, "jc") ?? Num(awg, "jc");
                node.AwgJmin = Num(settings, "jmin") ?? Num(awg, "jmin");
                node.AwgJmax = Num(settings, "jmax") ?? Num(awg, "jmax");
                node.AwgS1 = Num(settings, "s1") ?? Num(awg, "s1");
                node.AwgS2 = Num(settings, "s2") ?? Num(awg, "s2");
                node.AwgS3 = Num(settings, "s3") ?? Num(awg, "s3");
                node.AwgS4 = Num(settings, "s4") ?? Num(awg, "s4");
                node.AwgH1 = Prop(settings, "h1") ?? Prop(awg, "h1");
                node.AwgH2 = Prop(settings, "h2") ?? Prop(awg, "h2");
                node.AwgH3 = Prop(settings, "h3") ?? Prop(awg, "h3");
                node.AwgH4 = Prop(settings, "h4") ?? Prop(awg, "h4");
                node.AwgI1 = Prop(settings, "i1") ?? Prop(awg, "i1");
                node.AwgI2 = Prop(settings, "i2") ?? Prop(awg, "i2");
                // wireguard с ключами обфускации — это уже AmneziaWG
                if (node.HasAwgParams || proto == ProtocolIds.AmneziaWg)
                    proto = ProtocolIds.AmneziaWg;
            }
        }

        if (o.TryGetProperty("streamSettings", out var stream) && stream.ValueKind == JsonValueKind.Object)
        {
            var network = Prop(stream, "network") ?? "tcp";
            node.Network = network;
            var security = (Prop(stream, "security") ?? "none").ToLowerInvariant();
            node.Tls = security is "tls" or "xtls";
            node.Reality = security == "reality";

            if (stream.TryGetProperty("tlsSettings", out var tls) && tls.ValueKind == JsonValueKind.Object)
            {
                node.Sni = Prop(tls, "serverName");
                if (Prop(tls, "fingerprint") is { } fp) node.Fingerprint = fp;
                node.Insecure = Text.ToBool(Prop(tls, "allowInsecure"));
                if (tls.TryGetProperty("alpn", out var alpn)) node.Alpn = string.Join(",", StringList(alpn));
            }

            if (stream.TryGetProperty("realitySettings", out var reality) && reality.ValueKind == JsonValueKind.Object)
            {
                node.Sni = Prop(reality, "serverName") ?? node.Sni;
                node.PublicKey = Prop(reality, "publicKey");
                node.ShortId = Prop(reality, "shortId");
                node.Fingerprint = Prop(reality, "fingerprint") ?? node.Fingerprint;
                node.Tls = true;
                node.Reality = true;
            }

            if (stream.TryGetProperty("wsSettings", out var ws) && ws.ValueKind == JsonValueKind.Object)
            {
                node.Path = Prop(ws, "path");
                if (ws.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object) node.Host = Prop(h, "Host");
            }
            if (stream.TryGetProperty("grpcSettings", out var grpc) && grpc.ValueKind == JsonValueKind.Object)
                node.ServiceName = Prop(grpc, "serviceName");
            if (stream.TryGetProperty("tcpSettings", out var tcp) && tcp.ValueKind == JsonValueKind.Object)
                node.HeaderType = Prop(tcp, "headerType");
            if (stream.TryGetProperty("xhttpSettings", out var xhttp) && xhttp.ValueKind == JsonValueKind.Object)
            {
                node.Path = Prop(xhttp, "path");
                node.Host = Prop(xhttp, "host");
                node.Network = "xhttp";
            }
            if (stream.TryGetProperty("httpSettings", out var hs) && hs.ValueKind == JsonValueKind.Object)
            {
                node.Path = Prop(hs, "path");
                node.Host = Prop(hs, "host");
            }
        }

        var tag = Prop(o, "tag");
        var name = string.IsNullOrWhiteSpace(tag) || string.Equals(tag, "proxy", StringComparison.OrdinalIgnoreCase)
            ? $"{node.Server}:{node.Port}"
            : tag;
        return new ProxyProfile
        {
            Name = name,
            Protocol = proto,
            Node = node
        };
    }

    // --------------------------------------------------------------------- utils
    private static string? Prop(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(",", StringList(v)),
            _ => null
        };
    }

    private static int? Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)v.GetDouble(),
            JsonValueKind.String => Text.ToIntOrNull(v.GetString()),
            _ => null
        };
    }

    private static bool TlsBool(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && Text.ToBool(v.GetString())));

    private static IEnumerable<string> StringList(JsonElement arr)
    {
        if (arr.ValueKind == JsonValueKind.String) return new[] { arr.GetString() ?? "" };
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString());
    }

    private static JsonElement? FirstOf(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var first = arr.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? first : null;
    }
}
