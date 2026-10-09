using System.Text.Json.Serialization;

namespace GoreBox.Models;

/// <summary>
/// Профиль подключения (одна серверная конфигурация).
/// Хранится отдельным json-файлом в %AppData%\GoreBox\profiles\&lt;id&gt;.json
/// </summary>
public sealed class ProxyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Один из <see cref="ProtocolIds"/>.</summary>
    public string Protocol { get; set; } = ProtocolIds.Vless;

    /// <summary>Исходная ссылка (если профиль добавлен ссылкой).</summary>
    public string? Uri { get; set; }

    /// <summary>Разобранные параметры сервера.</summary>
    public ProxyNode Node { get; set; } = new();

    /// <summary>Полный JSON ядра (для профилей типа "tunnel"/импортированных конфигов).</summary>
    public string? RawJson { get; set; }

    /// <summary>Тег/папка для группировки в списке.</summary>
    public string? Group { get; set; }

    /// <summary>Последнее измеренное значение пинга, мс (-1 = не измерялось).</summary>
    public int LastPingMs { get; set; } = -1;

    /// <summary>Отмечен ли профиль (избранное).</summary>
    public bool Favorite { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsTelegramOnly => ProtocolIds.IsTelegramOnly(Protocol);

    [JsonIgnore]
    public string AddressLabel => Node.Server is { Length: > 0 } s
        ? $"{s}:{Node.Port}"
        : "—";

    [JsonIgnore]
    public string ProtocolLabel => ProtocolIds.Display(Protocol);

    public ProxyProfile Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        Protocol = Protocol,
        Uri = Uri,
        Node = Node.Clone(),
        RawJson = RawJson,
        Group = Group,
        LastPingMs = -1,
        Favorite = Favorite,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}

/// <summary>Параметры одного узла. Поля не для всех протоколов — лишние просто игнорируются.</summary>
public sealed class ProxyNode
{
    public string Server { get; set; } = "";
    public int Port { get; set; }

    // Общее
    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }
    public string? Username { get; set; }
    public string? Flow { get; set; }

    // Транспорт / TLS
    public string? Network { get; set; }              // tcp / ws / grpc / http(upgrade) / quic / xhttp
    public string? Path { get; set; }
    public string? Host { get; set; }
    public string? ServiceName { get; set; }
    public string? HeaderType { get; set; }
    public string? Sni { get; set; }
    public string? Alpn { get; set; }
    public string? Fingerprint { get; set; }
    public bool Tls { get; set; }
    public bool Reality { get; set; }
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public bool Insecure { get; set; }
    public string? PacketEncoding { get; set; }

    // QUIC-протоколы
    public int? UpMbps { get; set; }
    public int? DownMbps { get; set; }
    public string? Obfs { get; set; }
    public string? ObfsPassword { get; set; }

    // TUIC
    public string? CongestionControl { get; set; }
    public string? UdpRelayMode { get; set; }

    // WireGuard / AmneziaWG
    public string? WgPrivateKey { get; set; }
    public string? WgPeerPublicKey { get; set; }
    public string? WgPresharedKey { get; set; }
    public string? WgAddress { get; set; }     // 10.13.13.2/24[, ...]
    public string? WgDns { get; set; }
    public int? WgMtu { get; set; }
    public int? WgKeepalive { get; set; }
    public bool WgReservedSet { get; set; }
    public string? WgReserved { get; set; }    // "0,0,0"
    public string? WgAllowedIps { get; set; }  // "0.0.0.0/0, ::/0" из .conf

    // AmneziaWG obfuscation
    public int? AwgJc { get; set; }
    public int? AwgJmin { get; set; }
    public int? AwgJmax { get; set; }
    public int? AwgS1 { get; set; }
    public int? AwgS2 { get; set; }
    public int? AwgS3 { get; set; }
    public int? AwgS4 { get; set; }
    public string? AwgH1 { get; set; }
    public string? AwgH2 { get; set; }
    public string? AwgH3 { get; set; }
    public string? AwgH4 { get; set; }
    public string? AwgI1 { get; set; }
    public string? AwgI2 { get; set; }
    public string? AwgI3 { get; set; }
    public string? AwgI4 { get; set; }
    public string? AwgI5 { get; set; }

    // AmneziaWG 1.5 / 2.0 (поддерживается расширенным ядром; часть полей может игнорироваться его версией)
    public string? AwgHeaderProtectionKey { get; set; }
    public string? AwgContentPadding { get; set; }        // "11-28"
    public string? AwgRekeyAfterTime { get; set; }        // "102-112"
    public string? AwgRekeyTimeout { get; set; }          // "3-5"
    public string? AwgRejectAfterTime { get; set; }       // "150-218"
    public string? AwgKeepaliveTimeout { get; set; }      // "11-18"
    public string? AwgMaxHandshakeAttempts { get; set; }  // "19-40"
    public bool AwgRandomTrailers { get; set; }
    public bool AwgDisableCookies { get; set; }

    /// <summary>Исходный текст конфига WireGuard/AmneziaWG (.conf), если профиль добавлен файлом.</summary>
    public string? WgConfigRaw { get; set; }

    // MTProto
    public string? MtSecret { get; set; }

    // SSH
    public string? SshPrivateKeyPath { get; set; }

    /// <summary>Произвольные параметры (нестандартные поля из ссылки).</summary>
    public Dictionary<string, string> Extra { get; set; } = new();

    [JsonIgnore]
    public bool HasAwgParams =>
        AwgJc is not null || AwgJmin is not null || AwgJmax is not null ||
        AwgS1 is not null || AwgS2 is not null || AwgS3 is not null || AwgS4 is not null ||
        HasText(AwgH1) || HasText(AwgH2) || HasText(AwgH3) || HasText(AwgH4) ||
        HasText(AwgI1) || HasText(AwgI2) || HasText(AwgI3) || HasText(AwgI4) || HasText(AwgI5) ||
        HasText(AwgHeaderProtectionKey) || HasText(AwgContentPadding) ||
        HasText(AwgRekeyAfterTime) || HasText(AwgRekeyTimeout) || HasText(AwgRejectAfterTime) ||
        HasText(AwgKeepaliveTimeout) || HasText(AwgMaxHandshakeAttempts) ||
        AwgRandomTrailers || AwgDisableCookies;

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Обязательные параметры обфускации AmneziaWG 1.x, которых не хватает в профиле.
    /// У профиля поколения 2.0/3.x их и не должно быть — там обфускация делается
    /// шифрованием заголовка и паддингом, поэтому пустой набор 1.x не считается ошибкой.
    /// </summary>
    [JsonIgnore]
    public List<string> MissingAwgParams
    {
        get
        {
            var missing = new List<string>();

            var hasAwg1 = AwgJc is not null || AwgJmin is not null || AwgJmax is not null ||
                          AwgS1 is not null || AwgS2 is not null || AwgS3 is not null || AwgS4 is not null ||
                          HasText(AwgH1) || HasText(AwgH2) || HasText(AwgH3) || HasText(AwgH4) ||
                          HasText(AwgI1) || HasText(AwgI2) || HasText(AwgI3) || HasText(AwgI4) || HasText(AwgI5);
            if (!hasAwg1 && HasAwg2Params) return missing;

            if (AwgJc is null) missing.Add("Jc");
            if (AwgJmin is null) missing.Add("Jmin");
            if (AwgJmax is null) missing.Add("Jmax");
            if (AwgS1 is null) missing.Add("S1");
            if (AwgS2 is null) missing.Add("S2");
            if (AwgS3 is null) missing.Add("S3");
            if (AwgS4 is null) missing.Add("S4");
            if (!HasText(AwgH1)) missing.Add("H1");
            if (!HasText(AwgH2)) missing.Add("H2");
            if (!HasText(AwgH3)) missing.Add("H3");
            if (!HasText(AwgH4)) missing.Add("H4");
            return missing;
        }
    }

    /// <summary>Поля AmneziaWG 1.5/2.0, которые понимает не всякая расширенная сборка ядра.</summary>
    [JsonIgnore]
    public bool HasAwgExtendedOnlyParams => AwgRandomTrailers || AwgDisableCookies;

    /// <summary>Есть ли в профиле параметры AmneziaWG 2.0 (шифрование заголовка, паддинг, таймеры).</summary>
    [JsonIgnore]
    public bool HasAwg2Params =>
        HasText(AwgHeaderProtectionKey) || HasText(AwgContentPadding) || HasText(AwgRekeyAfterTime) ||
        HasText(AwgRekeyTimeout) || HasText(AwgRejectAfterTime) || HasText(AwgKeepaliveTimeout) ||
        HasText(AwgMaxHandshakeAttempts);

    public ProxyNode Clone()
    {
        var copy = (ProxyNode)MemberwiseClone();
        copy.Extra = new Dictionary<string, string>(Extra);
        return copy;
    }
}
