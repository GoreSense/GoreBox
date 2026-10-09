using System.Text.Json;
using System.Text.RegularExpressions;
using GoreBox.Models;

namespace GoreBox.Utils;

public sealed record ParseResult(List<ProxyProfile> Profiles, List<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Разбор ссылок подписки и конфигов: vless/vmess/trojan/ss/hysteria2/tuic/wireguard/awg/socks/http/ssh/mtproto,
/// а также WireGuard .conf, sing-box JSON и Xray (3x-ui) JSON.
/// </summary>
public static partial class LinkParser
{
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://")]
    private static partial Regex SchemeRegex();

    public static ParseResult ParseMany(string text)
    {
        var profiles = new List<ProxyProfile>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new ParseResult(profiles, errors);

        var trimmed = text.Trim();

        // 0) ссылка AmneziaVPN: vpn://<base64 от .conf или JSON>
        if (trimmed.StartsWith("vpn://", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var chunk in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var decoded = DecodeVpnLink(chunk);
                if (decoded is null)
                {
                    errors.Add("Не удалось декодировать ссылку vpn:// (ожидается base64 от конфига WireGuard/AmneziaWG)");
                    continue;
                }

                var nested = ParseMany(decoded);
                profiles.AddRange(nested.Profiles);
                errors.AddRange(nested.Errors);
            }
            return new ParseResult(profiles, errors);
        }

        // 1) JSON (sing-box, Xray/3x-ui или AmneziaWG — в последнем .conf лежит внутри строки).
        //    Важно проверять JSON раньше .conf: иначе маркеры [Interface]/PrivateKey внутри JSON
        //    заставят разбирать сам JSON как .conf.
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(trimmed);

                // AmneziaWG (клиентский JSON AmneziaVPN или серверный JSON панели)
                if (AmneziaJson.LooksLike(doc.RootElement))
                {
                    var amnezia = AmneziaJson.Parse(doc.RootElement, trimmed);
                    if (amnezia.Profile is not null) profiles.Add(amnezia.Profile);
                    errors.AddRange(amnezia.Notes.Select(n => "AmneziaWG: " + n));
                }
                else
                {
                    var profile = JsonConfigConverter.Convert(doc.RootElement, trimmed, out var err);
                    if (profile is not null) profiles.Add(profile);
                    else errors.Add(err ?? "Не удалось разобрать JSON-конфиг");
                }
            }
            catch (JsonException ex)
            {
                errors.Add("Ошибка JSON: " + ex.Message);
            }

            if (profiles.Count > 0) return new ParseResult(profiles, errors);
            if (!LooksLikeWireGuard(trimmed)) return new ParseResult(profiles, errors);
            errors.Clear();   // дальше разберём как .conf — старые замечания уже не важны
        }

        // 2) WireGuard / AmneziaWG .conf
        if (LooksLikeWireGuard(trimmed))
        {
            var p = WgConfParser.Parse(trimmed, out var err);
            if (p is not null) profiles.Add(p);
            else errors.Add(err ?? "Не удалось разобрать WireGuard-конфиг");
            return new ParseResult(profiles, errors);
        }

        // 3) base64-подписка или base64 от конфига целиком (одна строка без схемы)
        if (!trimmed.Contains('\n') && !SchemeRegex().IsMatch(trimmed) && Text.IsBase64Blob(trimmed)
            && Text.TryBase64(trimmed, out var sub) && (sub.Contains("://") || LooksLikeWireGuard(sub)))
        {
            return ParseMany(sub);
        }

        foreach (var rawLine in trimmed.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            // построчный base64 (некоторые панели отдают по строке на конфиг)
            if (!SchemeRegex().IsMatch(line) && Text.IsBase64Blob(line) && Text.TryBase64(line, out var dec)
                && (dec.Contains("://") || LooksLikeWireGuard(dec)))
            {
                var nested = ParseMany(dec);
                profiles.AddRange(nested.Profiles);
                errors.AddRange(nested.Errors);
                continue;
            }

            var profile = ParseSingle(line, out var error);
            if (profile is not null) profiles.Add(profile);
            else errors.Add(error ?? $"Не распознано: {Shorten(line)}");
        }

        return new ParseResult(profiles, errors);
    }

    public static ProxyProfile? ParseSingle(string line, out string? error)
    {
        error = null;
        try
        {
            var scheme = SchemeRegex().Match(line).Value.TrimEnd(':', '/').ToLowerInvariant();
            return scheme switch
            {
                "vless" => ParseVless(line),
                "vmess" => ParseVmess(line, out error),
                "trojan" or "trojan-go" => ParseTrojan(line),
                "ss" => ParseShadowsocks(line, out error),
                "ssr" => ParseShadowsocksR(line, out error),
                "socks" or "socks5" or "socks4" => ParseSocksHttp(line, ProtocolIds.Socks),
                "http" or "https" => ParseSocksHttp(line, ProtocolIds.Http),
                "hysteria2" or "hy2" => ParseHysteria2(line),
                "hysteria" or "hy" => ParseHysteria1(line),
                "tuic" => ParseTuic(line),
                "anytls" => ParseAnytls(line),
                "ssh" => ParseSsh(line),
                "wireguard" or "wg" => ParseWireGuardUri(line, out error),
                "awg" or "amneziawg" => ParseWireGuardUri(line, out error, amnezia: true),
                "tg" => ParseTelegram(line, out error),
                "vpn" => ParseVpnLink(line, out error),
                _ => ParseUnknown(line, out error)
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string Shorten(string s) => s.Length <= 64 ? s : s[..61] + "…";

    private static string DecodeFragment(Uri uri, string fallback)
    {
        var name = Text.UrlDecode(uri.Fragment.TrimStart('#'));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static bool TryParseUri(string line, out Uri uri)
    {
        uri = null!;
        try
        {
            uri = new Uri(line);
            return true;
        }
        catch
        {
            try
            {
                uri = new Uri(line.Replace("://", "://").Split('#')[0] + (line.Contains('#') ? "#" + line.Split('#')[^1] : ""));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ VLESS
    private static ProxyProfile ParseVless(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var uuid = uri.UserInfo;
        var port = uri.Port > 0 ? uri.Port : 443;
        var security = (Get(q, "security") ?? "none").ToLowerInvariant();

        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = port,
            Uuid = uuid,
            Flow = Get(q, "flow"),
            Network = Get(q, "type") ?? "tcp",
            Tls = security is "tls" or "reality" or "xtls",
            Reality = security == "reality",
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Fingerprint = Get(q, "fp"),
            PublicKey = Get(q, "pbk"),
            ShortId = Get(q, "sid"),
            Alpn = Get(q, "alpn"),
            Path = Get(q, "path"),
            Host = Get(q, "host"),
            ServiceName = Get(q, "serviceName"),
            HeaderType = Get(q, "headerType"),
            PacketEncoding = Get(q, "packetEncoding"),
            Insecure = Text.ToBool(Get(q, "allowInsecure")) || Text.ToBool(Get(q, "insecure")),
        };
        ApplyExtra(node, q, "type", "security", "flow", "sni", "peer", "fp", "pbk", "sid", "alpn", "path", "host",
            "serviceName", "headerType", "allowInsecure", "insecure", "packetEncoding", "encryption", "mode", "spx");

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{port}"),
            Protocol = ProtocolIds.Vless,
            Uri = line,
            Node = node
        };
    }

    // ------------------------------------------------------------------ VMess
    private static ProxyProfile? ParseVmess(string line, out string? error)
    {
        error = null;
        var payload = line["vmess://".Length..].Trim();

        if (!Text.TryBase64(payload, out var json) || !json.TrimStart().StartsWith('{'))
        {
            // vmess://uuid@host:port?... (нестандартный, но встречается)
            if (payload.Contains('@'))
            {
                var profile = ParseVless(line);
                profile.Protocol = ProtocolIds.Vmess;
                return profile;
            }
            error = "VMess-ссылка не в формате base64(JSON)";
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string name) => r.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        } : "";

        var net = S("net");
        if (net.Length == 0) net = "tcp";
        var tls = S("tls") is "tls" or "true" or "1";

        var node = new ProxyNode
        {
            Server = S("add"),
            Port = Text.ToInt(S("port"), 443),
            Uuid = S("id"),
            Flow = NullIfEmpty(S("flow")),
            Network = net,
            Path = NullIfEmpty(S("path")),
            Host = NullIfEmpty(S("host")),
            ServiceName = NullIfEmpty(S("serviceName")),
            HeaderType = NullIfEmpty(S("type")),
            Sni = NullIfEmpty(string.IsNullOrEmpty(S("sni")) ? S("peer") : S("sni")),
            Alpn = NullIfEmpty(S("alpn")),
            Fingerprint = NullIfEmpty(S("fp")),
            Tls = tls,
            Insecure = Text.ToBool(S("allowInsecure")) || S("verify_cert") is { Length: > 0 } vc && !Text.ToBool(vc),
            PublicKey = NullIfEmpty(S("pbk")),
            ShortId = NullIfEmpty(S("sid")),
            PacketEncoding = NullIfEmpty(S("packetEncoding")),
        };
        node.Extra["alterId"] = S("aid");
        node.Extra["security"] = string.IsNullOrEmpty(S("scy")) ? "auto" : S("scy");
        if (S("v").Length > 0) node.Extra["v"] = S("v");
        if (node.Server.Length == 0)
        {
            error = "В VMess-конфиге не указан адрес сервера";
            return null;
        }

        var name = S("ps");
        return new ProxyProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{node.Server}:{node.Port}" : Text.UrlDecode(name),
            Protocol = ProtocolIds.Vmess,
            Uri = line,
            Node = node
        };
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ------------------------------------------------------------------ Trojan
    private static ProxyProfile ParseTrojan(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var security = Get(q, "security") ?? "tls";
        var port = uri.Port > 0 ? uri.Port : 443;

        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = port,
            Password = Text.UrlDecode(uri.UserInfo),
            Network = Get(q, "type") ?? "tcp",
            Tls = security is "tls" or "reality" or "xtls",
            Reality = security == "reality",
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Fingerprint = Get(q, "fp"),
            PublicKey = Get(q, "pbk"),
            ShortId = Get(q, "sid"),
            Alpn = Get(q, "alpn"),
            Path = Get(q, "path"),
            Host = Get(q, "host"),
            ServiceName = Get(q, "serviceName"),
            Flow = Get(q, "flow"),
            Insecure = Text.ToBool(Get(q, "allowInsecure")) || Text.ToBool(Get(q, "insecure")),
        };
        ApplyExtra(node, q, "type", "security", "sni", "peer", "fp", "pbk", "sid", "alpn", "path", "host",
            "serviceName", "allowInsecure", "insecure", "mode");

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{port}"),
            Protocol = ProtocolIds.Trojan,
            Uri = line,
            Node = node
        };
    }

    // ------------------------------------------------------------- Shadowsocks
    private static ProxyProfile? ParseShadowsocks(string line, out string? error)
    {
        error = null;
        var body = line["ss://".Length..];
        var fragment = "";
        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragment = Text.UrlDecode(body[(hashIdx + 1)..]);
            body = body[..hashIdx];
        }

        var query = "";
        var qIdx = body.IndexOf('?');
        if (qIdx >= 0)
        {
            query = body[(qIdx + 1)..];
            body = body[..qIdx];
        }

        string method, password, host;
        int port;

        if (body.Contains('@'))
        {
            var at = body.LastIndexOf('@');
            var userInfo = body[..at];
            var hostPort = body[(at + 1)..];

            if (userInfo.Contains(':'))
            {
                var parts = userInfo.Split(':', 2);
                method = parts[0];
                password = parts[1];
            }
            else if (Text.TryBase64(userInfo, out var dec) && dec.Contains(':'))
            {
                var parts = dec.Split(':', 2);
                method = parts[0];
                password = parts[1];
            }
            else
            {
                error = "Не удалось разобрать логин/пароль Shadowsocks";
                return null;
            }

            (host, port) = SplitHostPort(hostPort);
        }
        else if (Text.TryBase64(body, out var decoded) && decoded.Contains('@'))
        {
            var at = decoded.LastIndexOf('@');
            var creds = decoded[..at].Split(':', 2);
            method = creds[0];
            password = creds.Length > 1 ? creds[1] : "";
            (host, port) = SplitHostPort(decoded[(at + 1)..]);
        }
        else
        {
            error = "Неверный формат ss:// ссылки";
            return null;
        }

        var q = Text.ParseQuery(query);
        var node = new ProxyNode
        {
            Server = host,
            Port = port,
            Method = method,
            Password = password,
        };

        var plugin = Get(q, "plugin");
        if (!string.IsNullOrEmpty(plugin))
        {
            var parts = plugin.Split(';');
            node.Extra["plugin"] = parts[0];
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2) node.Extra["plugin:" + kv[0]] = kv[1];
            }
        }

        return new ProxyProfile
        {
            Name = string.IsNullOrWhiteSpace(fragment) ? $"{host}:{port}" : fragment,
            Protocol = ProtocolIds.Shadowsocks,
            Uri = line,
            Node = node
        };
    }

    private static ProxyProfile? ParseShadowsocksR(string line, out string? error)
    {
        error = null;
        var body = line["ssr://".Length..].Trim();
        if (!Text.TryBase64(body, out var decoded))
        {
            error = "SSR: не удалось декодировать base64";
            return null;
        }

        var main = decoded.Split("/?")[0];
        var parts = main.Split(':');
        if (parts.Length < 6)
        {
            error = "SSR: неверный формат";
            return null;
        }

        var port = Text.ToInt(parts[1]);
        var proto = parts[2];
        var method = parts[3];
        var obfs = parts[4];
        var password = Text.TryBase64(parts[5], out var pw) ? pw : parts[5];

        var query = decoded.Contains("/?") ? Text.ParseQuery(decoded.Split("/?", 2)[1]) : new();
        var name = query.TryGetValue("remarks", out var r) && Text.TryBase64(r, out var rn) ? rn : $"{parts[0]}:{port}";

        var node = new ProxyNode
        {
            Server = parts[0],
            Port = port,
            Method = method,
            Password = password,
            Extra =
            {
                ["ssr_protocol"] = proto,
                ["ssr_obfs"] = obfs
            }
        };

        return new ProxyProfile
        {
            Name = name,
            Protocol = ProtocolIds.ShadowsocksR,
            Uri = line,
            Node = node
        };
    }

    private static (string host, int port) SplitHostPort(string s)
    {
        s = s.Trim().Trim('/');
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            var h = s[1..end];
            var p = end + 2 <= s.Length ? Text.ToInt(s[(end + 2)..]) : 0;
            return (h, p);
        }
        var idx = s.LastIndexOf(':');
        if (idx < 0) return (s, 0);
        return (s[..idx], Text.ToInt(s[(idx + 1)..]));
    }

    // ------------------------------------------------------- SOCKS / HTTP / SSH
    private static ProxyProfile ParseSocksHttp(string line, string protocol)
    {
        var uri = new Uri(line);
        var port = uri.Port > 0 ? uri.Port
            : protocol == ProtocolIds.Http ? 8080 : 1080;
        var user = "";
        var pass = "";
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Text.UrlDecode(parts[0]);
            pass = parts.Length > 1 ? Text.UrlDecode(parts[1]) : "";
        }

        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = port,
            Username = user,
            Password = pass,
            Tls = line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{port}"),
            Protocol = protocol,
            Uri = line,
            Node = node
        };
    }

    private static ProxyProfile ParseSsh(string line)
    {
        var uri = new Uri(line);
        var parts = uri.UserInfo.Split(':', 2);
        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 22,
            Username = Text.UrlDecode(parts[0]),
            Password = parts.Length > 1 ? Text.UrlDecode(parts[1]) : "",
        };
        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
            Protocol = ProtocolIds.Ssh,
            Uri = line,
            Node = node
        };
    }

    // --------------------------------------------------------------- Hysteria2
    private static ProxyProfile ParseHysteria2(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var auth = Text.UrlDecode(uri.UserInfo);
        if (string.IsNullOrEmpty(auth) && q.TryGetValue("auth", out var a)) auth = a;

        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            Password = auth,
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Insecure = Text.ToBool(Get(q, "insecure")) || Text.ToBool(Get(q, "allowInsecure")),
            Obfs = Get(q, "obfs"),
            ObfsPassword = Get(q, "obfs-password") ?? Get(q, "obfsPassword"),
            UpMbps = Text.ToIntOrNull(Get(q, "up")) ?? Text.ToIntOrNull(Get(q, "upmbps")),
            DownMbps = Text.ToIntOrNull(Get(q, "down")) ?? Text.ToIntOrNull(Get(q, "downmbps")),
            Tls = true,
        };
        if (Get(q, "mport") is { Length: > 0 } mp) node.Extra["mport"] = mp;
        if (Get(q, "hopInterval") is { Length: > 0 } hi) node.Extra["hop_interval"] = hi;

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
            Protocol = ProtocolIds.Hysteria2,
            Uri = line,
            Node = node
        };
    }

    private static ProxyProfile ParseHysteria1(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            Password = Get(q, "auth") ?? Text.UrlDecode(uri.UserInfo),
            Sni = Get(q, "peer") ?? Get(q, "sni"),
            Insecure = Text.ToBool(Get(q, "insecure")) || Text.ToBool(Get(q, "allowInsecure")),
            UpMbps = Text.ToIntOrNull(Get(q, "upmbps")),
            DownMbps = Text.ToIntOrNull(Get(q, "downmbps")),
            Obfs = Get(q, "obfs"),
            ObfsPassword = Get(q, "obfsparam") ?? Get(q, "obfsParam"),
            Tls = true,
        };
        if (Get(q, "protocol") is { Length: > 0 } pr) node.Extra["protocol"] = pr;   // udp | faketcp
        if (Get(q, "alpn") is { Length: > 0 } al) node.Alpn = al;

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
            Protocol = ProtocolIds.Hysteria,
            Uri = line,
            Node = node
        };
    }

    // -------------------------------------------------------------------- TUIC
    private static ProxyProfile ParseTuic(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var user = Text.UrlDecode(uri.UserInfo);
        var uuid = "";
        var pass = "";
        if (!string.IsNullOrEmpty(user))
        {
            var parts = user.Split(':', 2);
            uuid = parts[0];
            pass = parts.Length > 1 ? parts[1] : "";
        }

        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            Uuid = uuid,
            Password = pass,
            Sni = Get(q, "sni"),
            Alpn = Get(q, "alpn") ?? "h3",
            Insecure = Text.ToBool(Get(q, "allow_insecure")) || Text.ToBool(Get(q, "insecure")),
            CongestionControl = Get(q, "congestion_control") ?? Get(q, "congestion"),
            UdpRelayMode = Get(q, "udp_relay_mode"),
            Tls = true,
        };
        if (Text.ToBool(Get(q, "disable_sni"))) node.Extra["disable_sni"] = "true";

        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
            Protocol = ProtocolIds.Tuic,
            Uri = line,
            Node = node
        };
    }

    private static ProxyProfile ParseAnytls(string line)
    {
        var uri = new Uri(line);
        var q = Text.ParseQuery(uri.Query);
        var node = new ProxyNode
        {
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            Password = Text.UrlDecode(uri.UserInfo),
            Sni = Get(q, "sni"),
            Insecure = Text.ToBool(Get(q, "insecure")) || Text.ToBool(Get(q, "allowInsecure")),
            Tls = true,
        };
        return new ProxyProfile
        {
            Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
            Protocol = ProtocolIds.AnyTls,
            Uri = line,
            Node = node
        };
    }

    // -------------------------------------------------------------- WireGuard
    /// <summary>
    /// Ссылки wireguard:// и awg://. Для AmneziaWG важен весь набор параметров обфускации:
    /// без S3/S4 или I1–I5 сервер не примет хендшейк, поэтому читаем их все.
    /// </summary>
    private static ProxyProfile? ParseWireGuardUri(string line, out string? error, bool amnezia = false)
    {
        error = null;
        try
        {
            var uri = new Uri(line);
            var raw = Text.ParseQuery(uri.Query);
            var q = NormalizeQuery(raw);

            var user = Text.UrlDecode(uri.UserInfo);
            var node = new ProxyNode
            {
                Server = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 51820,
                WgPrivateKey = Get(q, "privatekey") ?? (user.Length > 0 ? user : null),
                WgPeerPublicKey = Get(q, "publickey") ?? Get(q, "peerkey") ?? Get(q, "peerpublickey"),
                WgPresharedKey = Get(q, "presharedkey") ?? Get(q, "psk"),
                WgAddress = Get(q, "address") ?? Get(q, "ip") ?? Get(q, "localaddress"),
                WgDns = Get(q, "dns"),
                WgMtu = Text.ToIntOrNull(Get(q, "mtu")),
                WgReserved = Get(q, "reserved"),
                WgAllowedIps = Get(q, "allowedips") ?? Get(q, "allowedip"),
                WgKeepalive = Text.ToIntOrNull(Get(q, "persistentkeepalive") ?? Get(q, "keepalive")),

                // AmneziaWG 1.x
                AwgJc = Text.ToIntOrNull(Get(q, "jc")),
                AwgJmin = Text.ToIntOrNull(Get(q, "jmin")),
                AwgJmax = Text.ToIntOrNull(Get(q, "jmax")),
                AwgS1 = Text.ToIntOrNull(Get(q, "s1")),
                AwgS2 = Text.ToIntOrNull(Get(q, "s2")),
                AwgS3 = Text.ToIntOrNull(Get(q, "s3")),
                AwgS4 = Text.ToIntOrNull(Get(q, "s4")),
                AwgH1 = Get(q, "h1"),
                AwgH2 = Get(q, "h2"),
                AwgH3 = Get(q, "h3"),
                AwgH4 = Get(q, "h4"),
                AwgI1 = Get(q, "i1"),
                AwgI2 = Get(q, "i2"),
                AwgI3 = Get(q, "i3"),
                AwgI4 = Get(q, "i4"),
                AwgI5 = Get(q, "i5"),

                // AmneziaWG 2.0 / 3.x
                AwgHeaderProtectionKey = Get(q, "headerprotectionkey") ?? Get(q, "hpk"),
                AwgContentPadding = Get(q, "contentpaddingaddition") ?? Get(q, "contentpadding"),
                AwgRekeyAfterTime = Get(q, "rekeyaftertime"),
                AwgRekeyTimeout = Get(q, "rekeytimeout"),
                AwgRejectAfterTime = Get(q, "rejectaftertime"),
                AwgKeepaliveTimeout = Get(q, "keepalivetimeout"),
                AwgMaxHandshakeAttempts = Get(q, "maxhandshakeattempts"),
                AwgRandomTrailers = Text.ToBool(Get(q, "randomtrailers")),
                AwgDisableCookies = Text.ToBool(Get(q, "disablecookies"))
            };

            // awg:// без параметров тоже AmneziaWG; wg:// с параметрами обфускации — тем более
            var isAwg = amnezia || node.HasAwgParams;
            foreach (var (key, value) in raw)
                if (!IsKnownWireGuardKey(key)) node.Extra[key] = value;

            return new ProxyProfile
            {
                Name = DecodeFragment(uri, $"{uri.Host}:{node.Port}"),
                Protocol = isAwg ? ProtocolIds.AmneziaWg : ProtocolIds.Wireguard,
                Uri = line,
                Node = node
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static readonly HashSet<string> KnownWireGuardKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "privatekey", "publickey", "peerkey", "peerpublickey", "presharedkey", "psk",
        "address", "ip", "localaddress", "dns", "mtu", "reserved", "allowedips", "allowedip",
        "persistentkeepalive", "keepalive",
        "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4",
        "i1", "i2", "i3", "i4", "i5",
        "headerprotectionkey", "hpk", "contentpaddingaddition", "contentpadding",
        "rekeyaftertime", "rekeytimeout", "rejectaftertime", "keepalivetimeout",
        "maxhandshakeattempts", "randomtrailers", "disablecookies"
    };

    private static bool IsKnownWireGuardKey(string key) =>
        KnownWireGuardKeys.Contains(key.Replace("_", "").Replace("-", "").Trim());

    /// <summary>Пары ссылки с ключами без «_»/«-»: private_key и privatekey становятся одним ключом.</summary>
    private static Dictionary<string, string> NormalizeQuery(Dictionary<string, string> query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in query)
        {
            var plain = key.Replace("_", "").Replace("-", "").Trim();
            if (plain.Length == 0 || result.ContainsKey(plain)) continue;
            result[plain] = value;
        }
        return result;
    }

    // ----------------------------------------------------------------- MTProto
    private static ProxyProfile? ParseTelegram(string line, out string? error)
    {
        error = null;
        string query;
        var isSocks = line.Contains("tg://socks", StringComparison.OrdinalIgnoreCase);
        if (line.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
            query = line[(line.IndexOf('?') + 1 >= 0 ? line.IndexOf('?') + 1 : 0)..];
        else
        {
            var q = line.IndexOf('?');
            query = q >= 0 ? line[(q + 1)..] : "";
        }

        var p = Text.ParseQuery(query);
        if (!p.TryGetValue("server", out var server) || !p.TryGetValue("port", out var portStr))
        {
            error = "MTProto-ссылка без server/port";
            return null;
        }

        var node = new ProxyNode
        {
            Server = server,
            Port = Text.ToInt(portStr),
            Username = p.TryGetValue("user", out var u) ? u : null,
            Password = p.TryGetValue("pass", out var pw) ? pw : null,
            MtSecret = p.TryGetValue("secret", out var s) ? s : null,
        };

        return new ProxyProfile
        {
            Name = $"MTProto · {server}",
            Protocol = isSocks ? ProtocolIds.Socks : ProtocolIds.MtProto,
            Uri = line,
            Node = node
        };
    }

    private static ProxyProfile? ParseUnknown(string line, out string? error)
    {
        error = null;
        // Попытка "host:port:user:pass" — встречается в старых списках socks/http
        var parts = line.Split(':');
        if (parts.Length is 2 or 4 && parts[0].Count(c => c == '.') == 3 && int.TryParse(parts[1], out var port))
        {
            var node = new ProxyNode
            {
                Server = parts[0],
                Port = port,
                Username = parts.Length == 4 ? parts[2] : null,
                Password = parts.Length == 4 ? parts[3] : null,
            };
            return new ProxyProfile
            {
                Name = $"{parts[0]}:{port}",
                Protocol = ProtocolIds.Socks,
                Uri = line,
                Node = node
            };
        }

        error = "Неизвестный протокол ссылки";
        return null;
    }

    // ------------------------------------------------------------------ helpers
    private static string? Get(Dictionary<string, string> q, string key)
        => q.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static void ApplyExtra(ProxyNode node, Dictionary<string, string> q, params string[] known)
    {
        foreach (var (k, v) in q)
        {
            if (known.Contains(k, StringComparer.OrdinalIgnoreCase)) continue;
            node.Extra[k] = v;
        }
    }

    /// <summary>vpn:// — формат экспорта клиента AmneziaVPN: base64 от .conf (иногда от JSON).</summary>
    private static ProxyProfile? ParseVpnLink(string line, out string? error)
    {
        var decoded = DecodeVpnLink(line);
        if (decoded is null)
        {
            error = "Ссылка vpn:// не декодируется — вероятно, скопирована не полностью";
            return null;
        }

        var nested = ParseMany(decoded);
        if (nested.Profiles.Count > 0)
        {
            error = null;
            return nested.Profiles[0];
        }

        error = nested.Errors.FirstOrDefault() ?? "Внутри ссылки vpn:// не найден конфиг WireGuard/AmneziaWG";
        return null;
    }

    /// <summary>Декодирует полезную нагрузку vpn://-ссылки (base64 или base64url, с паддингом и без).</summary>
    public static string? DecodeVpnLink(string line)
    {
        var index = line.IndexOf("://", StringComparison.Ordinal);
        if (index < 0) return null;
        var payload = line[(index + 3)..].Trim().Trim('"');
        if (payload.Length == 0) return null;

        if (Text.TryBase64(payload, out var decoded)) return decoded;

        // Amnezia иногда отдаёт ссылку с URL-кодированием
        try
        {
            var unescaped = Uri.UnescapeDataString(payload);
            if (Text.TryBase64(unescaped, out var second)) return second;
        }
        catch { /* ignore */ }

        return null;
    }

    /// <summary>Текст похож на .conf WireGuard/AmneziaWG.</summary>
    private static bool LooksLikeWireGuard(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase);
}
