using GoreBox.Models;

namespace GoreBox.Utils;

/// <summary>Разбор .conf WireGuard / AmneziaWG (в т.ч. экспорт из клиента Amnezia).</summary>
public static class WgConfParser
{
    public static ProxyProfile? Parse(string text, out string? error)
    {
        error = null;
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx < 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (section == "peer") peer[key] = val;
            else iface[key] = val;
        }

        if (iface.Count == 0)
        {
            error = "Пустой WireGuard-конфиг";
            return null;
        }

        (string host, int port) = (peer.TryGetValue("Endpoint", out var ep) ? SplitHostPort(ep) : ("", 0));
        if (host.Length == 0)
        {
            error = "В [Peer] не указан Endpoint";
            return null;
        }

        var node = new ProxyNode
        {
            Server = host,
            Port = port == 0 ? 51820 : port,
            WgPrivateKey = Get(iface, "PrivateKey"),
            WgAddress = Get(iface, "Address"),
            WgDns = Get(iface, "DNS"),
            WgMtu = Text.ToIntOrNull(Get(iface, "MTU")),
            WgPeerPublicKey = Get(peer, "PublicKey"),
            WgPresharedKey = Get(peer, "PresharedKey"),
            WgKeepalive = Text.ToIntOrNull(Get(peer, "PersistentKeepalive")),
            WgReserved = Get(peer, "Reserved"),
            WgAllowedIps = Get(peer, "AllowedIPs"),
            AwgJc = Text.ToIntOrNull(Get(iface, "Jc")),
            AwgJmin = Text.ToIntOrNull(Get(iface, "Jmin")),
            AwgJmax = Text.ToIntOrNull(Get(iface, "Jmax")),
            AwgS1 = Text.ToIntOrNull(Get(iface, "S1")),
            AwgS2 = Text.ToIntOrNull(Get(iface, "S2")),
            AwgS3 = Text.ToIntOrNull(Get(iface, "S3")),
            AwgS4 = Text.ToIntOrNull(Get(iface, "S4")),
            AwgH1 = Get(iface, "H1"),
            AwgH2 = Get(iface, "H2"),
            AwgH3 = Get(iface, "H3"),
            AwgH4 = Get(iface, "H4"),
            AwgI1 = Get(iface, "I1"),
            AwgI2 = Get(iface, "I2"),
            AwgI3 = Get(iface, "I3"),
            AwgI4 = Get(iface, "I4"),
            AwgI5 = Get(iface, "I5"),
            AwgHeaderProtectionKey = Get(iface, "HeaderProtectionKey"),
            AwgContentPadding = Get(iface, "ContentPaddingAddition"),
            AwgRekeyAfterTime = Get(iface, "RekeyAfterTime"),
            AwgRekeyTimeout = Get(iface, "RekeyTimeout"),
            AwgRejectAfterTime = Get(iface, "RejectAfterTime"),
            AwgKeepaliveTimeout = Get(iface, "KeepaliveTimeout"),
            AwgMaxHandshakeAttempts = Get(iface, "MaxHandshakeAttempts"),
            AwgRandomTrailers = IsOn(Get(iface, "RandomTrailers")),
            AwgDisableCookies = IsOn(Get(iface, "DisableCookies")),
            WgConfigRaw = text.Trim(),
        };

        var isAmnezia = node.HasAwgParams;
        var name = peer.TryGetValue("Endpoint", out var e2) ? e2 : host;

        return new ProxyProfile
        {
            Name = $"{(isAmnezia ? "AmneziaWG" : "WG")} · {name}",
            Protocol = isAmnezia ? ProtocolIds.AmneziaWg : ProtocolIds.Wireguard,
            Uri = null,
            Node = node
        };

        static string? Get(Dictionary<string, string> d, string key)
            => d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        static bool IsOn(string? v) =>
            v is not null && (v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                              v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                              v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v == "1");
    }

    private static (string host, int port) SplitHostPort(string s)
    {
        s = s.Trim();
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            var host = s[1..end];
            var p = end + 2 <= s.Length ? Text.ToInt(s[(end + 2)..]) : 0;
            return (host, p);
        }
        var idx = s.LastIndexOf(':');
        if (idx < 0) return (s, 0);
        return (s[..idx], Text.ToInt(s[(idx + 1)..]));
    }
}
