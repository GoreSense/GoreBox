using GoreBox.Models;
using GoreBox.Services;
using GoreBox.Utils;

namespace GoreBox.ViewModels;

/// <summary>Модель редактора профиля: работаем с копией, сохраняем по кнопке.</summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly LatencyService _latency;
    private int _ping = -1;
    private bool _pinging;

    public ProfileEditorViewModel(ProxyProfile source, LatencyService latency)
    {
        Profile = source.Clone();
        Profile.Id = source.Id;                 // сохраняем идентичность при правке
        Profile.CreatedAt = source.CreatedAt;
        Profile.RawJson = source.RawJson;
        Profile.LastPingMs = source.LastPingMs;
        _latency = latency;

        TestPingCommand = new AsyncRelayCommand(TestPingAsync, () => !_pinging);
    }

    public ProxyProfile Profile { get; }
    public ProxyNode Node => Profile.Node;

    public AsyncRelayCommand TestPingCommand { get; }

    public static IReadOnlyList<string> ProtocolList { get; } = new[]
    {
        ProtocolIds.Vless, ProtocolIds.Vmess, ProtocolIds.Trojan, ProtocolIds.Shadowsocks,
        ProtocolIds.Socks, ProtocolIds.Http, ProtocolIds.Hysteria2, ProtocolIds.Hysteria,
        ProtocolIds.Tuic, ProtocolIds.AnyTls, ProtocolIds.Wireguard, ProtocolIds.AmneziaWg,
        ProtocolIds.Ssh, ProtocolIds.MtProto, ProtocolIds.Tunnel
    };

    public string Protocol
    {
        get => Profile.Protocol;
        set
        {
            Profile.Protocol = value;
            Raise();
            Raise(nameof(IsTelegram));
            Raise(nameof(IsWireGuard));
            Raise(nameof(IsTlsCapable));
            Raise(nameof(IsQuic));
            Raise(nameof(IsTransportable));
            Raise(nameof(IsUdpBased));
            Raise(nameof(ProtocolHint));
        }
    }

    public string ProtocolHint => Profile.Protocol.ToLowerInvariant() switch
    {
        ProtocolIds.MtProto => "MTProto применяется только внутри Telegram — после сохранения нажмите «Открыть в Telegram».",
        ProtocolIds.AmneziaWg => "AmneziaWG требует расширенную сборку ядра (Настройки → Ядро).",
        ProtocolIds.ShadowsocksR => "SSR не поддерживается ядром sing-box.",
        ProtocolIds.Tunnel => "Tunnel (Custom) — вставьте JSON ядра в поле «JSON ядра» внизу редактора: сервер/порт сверху не заполняются.",
        _ => ""
    };

    public bool IsTelegram => Profile.Protocol == ProtocolIds.MtProto;

    public bool IsWireGuard => Profile.Protocol is ProtocolIds.Wireguard or ProtocolIds.AmneziaWg;
    public bool IsAmnezia => Profile.Protocol == ProtocolIds.AmneziaWg || Node.HasAwgParams;
    public bool IsTlsCapable => Profile.Protocol is ProtocolIds.Vless or ProtocolIds.Vmess or ProtocolIds.Trojan
        or ProtocolIds.Hysteria2 or ProtocolIds.Hysteria or ProtocolIds.Tuic or ProtocolIds.AnyTls or ProtocolIds.Http;
    public bool IsQuic => Profile.Protocol is ProtocolIds.Hysteria2 or ProtocolIds.Hysteria or ProtocolIds.Tuic;
    public bool IsTransportable => Profile.Protocol is ProtocolIds.Vless or ProtocolIds.Vmess or ProtocolIds.Trojan;
    public bool IsUdpBased => ProtocolIds.IsUdpBased(Profile.Protocol);

    public bool Tls
    {
        get => Node.Tls;
        set
        {
            if (Node.Tls == value) return;
            Node.Tls = value;
            Raise();
        }
    }

    public bool Reality
    {
        get => Node.Reality;
        set
        {
            if (Node.Reality == value) return;
            Node.Reality = value;
            if (value) Node.Tls = true;
            Raise();
            Raise(nameof(Tls));
        }
    }

    public bool Insecure
    {
        get => Node.Insecure;
        set
        {
            if (Node.Insecure == value) return;
            Node.Insecure = value;
            Raise();
        }
    }

    public string RawUri
    {
        get => Profile.Uri ?? "";
        set
        {
            Profile.Uri = value;
            Raise();
        }
    }

    public string RawJson
    {
        get => Profile.RawJson ?? "";
        set
        {
            Profile.RawJson = string.IsNullOrWhiteSpace(value) ? null : value;
            Raise();
        }
    }

    public int Ping
    {
        get => _ping;
        private set
        {
            if (SetProperty(ref _ping, value)) Raise(nameof(PingText));
        }
    }

    public string PingText => _ping < 0 ? "не измерялось" : _ping == 0 ? "<1 ms" : $"{_ping} ms";

    public async Task TestPingAsync()
    {
        _pinging = true;
        TestPingCommand.RaiseCanExecuteChanged();
        Ping = -1;
        Raise(nameof(PingText));
        try
        {
            Ping = await _latency.MeasureAsync(Profile);
        }
        finally
        {
            _pinging = false;
            TestPingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Применить изменения к исходному профилю.</summary>
    public void ApplyTo(ProxyProfile target)
    {
        target.Name = Profile.Name;
        target.Protocol = Profile.Protocol;
        target.Node = Profile.Node;
        target.Uri = Profile.Uri;
        target.RawJson = Profile.RawJson;
        target.Group = Profile.Group;
        target.UpdatedAt = DateTime.UtcNow;
    }
}
