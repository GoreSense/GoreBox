using System.Text.Json;
using GoreBox.Models;

namespace GoreBox.Utils;

/// <summary>
/// Разбор JSON-конфигов AmneziaWG. Встречаются два вида:
///
/// 1. Клиентский JSON AmneziaVPN — внутри есть поле со готовым .conf (текст с [Interface]).
/// 2. Серверный JSON панели — <c>{ "protocol": "amneziawg", "settings": { "server": { "jc": …, "h1": … } } }</c>.
///    Адреса сервера и ключа клиента в нём обычно нет: что удалось собрать — заполняем,
///    об остальном честно предупреждаем, чтобы пользователь дописал в редакторе.
/// </summary>
public static class AmneziaJson
{
    public sealed record Result(ProxyProfile? Profile, List<string> Notes);

    private static readonly HashSet<string> AwgMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4",
        "headerprotectionkey", "contentpaddingaddition", "randomtrailers", "disablecookies",
        "i1", "i2", "i3", "i4", "i5"
    };

    /// <summary>Похоже ли на конфиг AmneziaWG (а не на обычный sing-box/Xray JSON).</summary>
    public static bool LooksLike(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (FindEmbeddedConf(root) is not null) return true;

        var keys = Collect(root);
        var hasAwg = keys.Any(k => AwgMarkers.Contains(k.Key));
        var hasServer = keys.Any(k => k.Key is "settings" or "server" or "amnezia");
        return hasAwg && (hasServer || keys.Any(k => k.Key is "privatekey" or "publickey"));
    }

    public static Result Parse(JsonElement root, string raw)
    {
        var notes = new List<string>();

        // 1) внутри уже лежит готовый .conf
        var conf = FindEmbeddedConf(root);
        if (conf is not null)
        {
            var fromConf = WgConfParser.Parse(conf, out var err);
            if (fromConf is null) notes.Add(err ?? "Не удалось разобрать вложенный .conf");
            return new Result(fromConf, notes);
        }

        // 2) собираем параметры из ключей
        var keys = Collect(root);

        var host = Pick(keys, "hostname", "host", "serveraddress", "server", "address", "endpoint", "ip");
        var port = PickInt(keys, "port", "serverport", "listenport");
        var serverKey = Pick(keys, "publickey", "serverpublickey", "peerpublickey", "wgpublickey", "public");
        var serverPrivateKey = PickPath(keys, "privatekey", "serverprivatekey");
        var clientPrivate = PickClientPrivate(keys);
        var psk = Pick(keys, "presharedkey", "pskkey", "psk");
        var dns1 = Pick(keys, "primarydns", "dns1", "dns");
        var dns2 = Pick(keys, "secondarydns", "dns2");
        var address = PickPath(keys, "address", "localaddress", "interfaceaddress");
        var subnetIp = Pick(keys, "subnetip", "subnet");
        var subnetCidr = Pick(keys, "subnetcidr");
        var mtu = PickInt(keys, "mtu");
        var keepalive = PickInt(keys, "persistentkeepalive", "keepalive");
        var allowedIps = Pick(keys, "allowedips");

        var dns = string.Join(", ", new[] { dns1, dns2 }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var keep = PickInt(keys, "persistentkeepalive");
        var awg = CollectAwg(keys);

        var node = new ProxyNode
        {
            Server = host ?? "",
            Port = port ?? 0,
            WgPrivateKey = clientPrivate,
            WgPeerPublicKey = serverKey,
            WgPresharedKey = psk,
            // из серверного JSON известна только подсеть (subnetIp/subnetCidr) — адрес клиента не выдумываем
            WgAddress = address,
            WgDns = string.IsNullOrWhiteSpace(dns) ? null : dns,
            WgMtu = mtu,
            WgKeepalive = keep ?? keepalive,
            WgReserved = Pick(keys, "reserved"),
            AwgJc = awg.Jc, AwgJmin = awg.Jmin, AwgJmax = awg.Jmax,
            AwgS1 = awg.S1, AwgS2 = awg.S2, AwgS3 = awg.S3, AwgS4 = awg.S4,
            AwgH1 = awg.H1, AwgH2 = awg.H2, AwgH3 = awg.H3, AwgH4 = awg.H4,
            AwgI1 = awg.I1, AwgI2 = awg.I2, AwgI3 = awg.I3, AwgI4 = awg.I4, AwgI5 = awg.I5,
            AwgHeaderProtectionKey = awg.HeaderKey,
            AwgContentPadding = awg.ContentPadding,
            AwgRekeyAfterTime = awg.RekeyAfter,
            AwgRekeyTimeout = awg.RekeyTimeout,
            AwgRejectAfterTime = awg.RejectAfter,
            AwgKeepaliveTimeout = awg.KeepaliveTimeout,
            AwgMaxHandshakeAttempts = awg.MaxHandshake,
            AwgRandomTrailers = awg.RandomTrailers,
            AwgDisableCookies = awg.DisableCookies
        };

        if (node.Server.Length == 0) notes.Add("в конфиге не найден адрес сервера — впишите его в профиле");
        if (string.IsNullOrWhiteSpace(node.WgPrivateKey)) notes.Add("нет приватного ключа клиента — впишите его в профиле");
        if (string.IsNullOrWhiteSpace(node.WgPeerPublicKey)) notes.Add("нет публичного ключа сервера");
        if (string.IsNullOrWhiteSpace(node.WgAddress))
        {
            var hint = subnetIp is not null ? $"{subnetIp}{(subnetCidr is not null ? "/" + subnetCidr : "")}" : null;
            notes.Add(hint is null
                ? "нет адреса интерфейса (Address) — обычно 10.8.1.X/32"
                : $"нет адреса интерфейса (Address) — в конфиге задана подсеть {hint}, адрес клиента впишите вручную (например 10.8.1.2/32)");
        }

        var isAwg = node.HasAwgParams || serverPrivateKey is not null;
        var prefix = isAwg ? "AmneziaWG" : "WG";
        var name = node.Server.Length > 0
            ? $"{prefix} · {node.Server}"
            : $"{prefix} (нужно дополнить)";

        var profile = new ProxyProfile
        {
            Name = name,
            Protocol = isAwg ? ProtocolIds.AmneziaWg : ProtocolIds.Wireguard,
            Node = node
        };

        return new Result(profile, notes);
    }

    // --------------------------------------------------------------- вспомогательное

    private sealed record Entry(string Key, string? Value, List<string> Path);

    /// <summary>Плоский список листовых значений JSON: нормализованный ключ, значение, путь.</summary>
    private static List<Entry> Collect(JsonElement root)
    {
        var list = new List<Entry>();
        Walk(root, new List<string>(), list, 0);
        return list;

        static void Walk(JsonElement element, List<string> path, List<Entry> list, int depth)
        {
            if (depth > 8) return;
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var child = new List<string>(path) { prop.Name.ToLowerInvariant() };
                        if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            Walk(prop.Value, child, list, depth + 1);
                        else
                            list.Add(new Entry(Normalize(prop.Name), Scalar(prop.Value), child));
                    }
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in element.EnumerateArray())
                        Walk(item, new List<string>(path) { i++.ToString() }, list, depth + 1);
                    break;
            }
        }

        static string Normalize(string key) =>
            new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        static string? Scalar(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "on",
            JsonValueKind.False => "off",
            _ => null
        };
    }

    private static string? Pick(List<Entry> keys, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = keys.FirstOrDefault(k => k.Key == name && !string.IsNullOrWhiteSpace(k.Value) &&
                                               !k.Path.Contains("private") && !k.Path.Contains("client") &&
                                               IsHostLike(k));
            if (hit is not null) return hit.Value!.Trim();
        }
        return null;

        static bool IsHostLike(Entry e) => e.Value is not null && !e.Value.Contains(' ') && e.Value.Length < 256;
    }

    /// <summary>Значение строго из объекта сервера (адрес, ключи сервера).</summary>
    private static string? PickPath(List<Entry> keys, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = keys.FirstOrDefault(k => k.Key == name && !string.IsNullOrWhiteSpace(k.Value) &&
                                               !k.Path.Contains("client"));
            if (hit is not null) return hit.Value!.Trim();
        }
        return null;
    }

    private static string? PickClientPrivate(List<Entry> keys)
    {
        var explicitKey = keys.FirstOrDefault(k => k.Key is "clientprivkey" or "clientprivatekey" &&
                                                   !string.IsNullOrWhiteSpace(k.Value));
        if (explicitKey is not null) return explicitKey.Value!.Trim();

        // «privateKey» без контекста сервера/клиента
        var plain = keys.FirstOrDefault(k => k.Key is "privatekey" && !string.IsNullOrWhiteSpace(k.Value) &&
                                             !k.Path.Contains("server") && !k.Path.Contains("interface"));
        return plain?.Value?.Trim();
    }

    private static int? PickInt(List<Entry> keys, params string[] names)
    {
        var value = PickPath(keys, names) ?? Pick(keys, names);
        return int.TryParse(value, out var number) ? number : null;
    }

    private sealed record Awg(int? Jc, int? Jmin, int? Jmax, int? S1, int? S2, int? S3, int? S4,
        string? H1, string? H2, string? H3, string? H4, string? I1, string? I2, string? I3, string? I4, string? I5,
        string? HeaderKey, string? ContentPadding, string? RekeyAfter, string? RekeyTimeout, string? RejectAfter,
        string? KeepaliveTimeout, string? MaxHandshake, bool RandomTrailers, bool DisableCookies);

    private static Awg CollectAwg(List<Entry> keys)
    {
        int? Num(string name) => int.TryParse(PickPath(keys, name), out var v) ? v : null;
        string? Str(string name) => PickPath(keys, name);
        bool Flag(string name) => PickPath(keys, name) is "on" or "true" or "yes" or "1";

        return new Awg(
            Num("jc"), Num("jmin"), Num("jmax"), Num("s1"), Num("s2"), Num("s3"), Num("s4"),
            Str("h1"), Str("h2"), Str("h3"), Str("h4"),
            Str("i1"), Str("i2"), Str("i3"), Str("i4"), Str("i5"),
            Str("headerprotectionkey"), Str("contentpaddingaddition"),
            Str("rekeyaftertime"), Str("rekeytimeout"), Str("rejectaftertime"),
            Str("keepalivetimeout"), Str("maxhandshakeattempts"),
            Flag("randomtrailers"), Flag("disablecookies"));
    }

    /// <summary>Ищет в JSON строку, содержащую готовый .conf WireGuard/AmneziaWG.</summary>
    private static string? FindEmbeddedConf(JsonElement element, int depth = 0)
    {
        if (depth > 6) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                return text is not null && text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) &&
                       text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
                    ? text
                    : null;

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var found = FindEmbeddedConf(prop.Value, depth + 1);
                    if (found is not null) return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindEmbeddedConf(item, depth + 1);
                    if (found is not null) return found;
                }
                break;
        }

        return null;
    }
}
