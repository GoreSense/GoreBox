namespace GoreBox.Models;

/// <summary>Действие маршрутизации для правила.</summary>
public enum RouteAction
{
    Proxy,
    Direct,
    Block
}

/// <summary>Тип сопоставления, который выбирает пользователь.</summary>
public enum MatchKind
{
    Domain,
    Ip,
    Keyword,
    Regex
}

/// <summary>Внутренний (нормализованный) вид записи для ядра.</summary>
public enum RouteKind
{
    Domain,
    DomainSuffix,
    DomainKeyword,
    DomainRegex,
    Ip,
    IpCidr
}

/// <summary>Тема оформления.</summary>
public enum ThemeMode
{
    Dark,
    Light
}

/// <summary>Состояние прокси-ядра.</summary>
public enum ConnectionState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>Тип входящего подключения локального прокси.</summary>
public enum ListenMode
{
    Mixed,
    Socks,
    Http
}

/// <summary>Какое ядро использовать/поддерживается.</summary>
public enum CoreFlavor
{
    /// <summary>Официальный sing-box (SagerNet/sing-box).</summary>
    SingBox,

    /// <summary>Расширенная сборка (sing-box-extended) — AmneziaWG, XHTTP и т.п.</summary>
    SingBoxExtended,

    /// <summary>Ядро, указанное пользователем вручную.</summary>
    Custom
}

public static class ProtocolIds
{
    public const string Vless = "vless";
    public const string Vmess = "vmess";
    public const string Trojan = "trojan";
    public const string Shadowsocks = "shadowsocks";
    public const string ShadowsocksR = "shadowsocksr";
    public const string Socks = "socks";
    public const string Http = "http";
    public const string Mixed = "mixed";
    public const string Hysteria = "hysteria";
    public const string Hysteria2 = "hysteria2";
    public const string Tuic = "tuic";
    public const string Wireguard = "wireguard";
    public const string AmneziaWg = "amneziawg";
    public const string MtProto = "mtproto";
    public const string AnyTls = "anytls";
    public const string Ssh = "ssh";
    public const string Tunnel = "tunnel";

    public static string Display(string id) => id.ToLowerInvariant() switch
    {
        Vless => "VLESS",
        Vmess => "VMess",
        Trojan => "Trojan",
        Shadowsocks => "Shadowsocks",
        ShadowsocksR => "ShadowsocksR",
        Socks => "SOCKS",
        Http => "HTTP",
        Mixed => "Mixed",
        Hysteria => "Hysteria",
        Hysteria2 => "Hysteria 2",
        Tuic => "TUIC",
        Wireguard => "WireGuard",
        AmneziaWg => "AmneziaWG",
        MtProto => "MTProto",
        AnyTls => "AnyTLS",
        Ssh => "SSH",
        Tunnel => "Custom (JSON)",
        _ => id
    };

    /// <summary>Протоколы, которые ядро не умеет использовать как системный прокси.</summary>
    public static bool IsTelegramOnly(string id) =>
        id.Equals(MtProto, StringComparison.OrdinalIgnoreCase);

    /// <summary>Транспорт работает поверх UDP (влияет на способ измерения пинга).</summary>
    public static bool IsUdpBased(string id) => id.ToLowerInvariant() switch
    {
        Hysteria2 or Hysteria or Tuic or Wireguard or AmneziaWg => true,
        _ => false
    };
}
