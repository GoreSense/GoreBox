using System.Windows.Media;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.ViewModels;

/// <summary>Строка списка профилей.</summary>
public sealed class ProfileItemVm : ObservableObject
{
    private int _pingMs;
    private bool _isActive;
    private bool _isPinging;

    public ProfileItemVm(ProxyProfile profile)
    {
        Profile = profile;
        _pingMs = profile.LastPingMs;
        Icon = IconHelper.ForExecutable(null);
    }

    public ProxyProfile Profile { get; }

    public string Id => Profile.Id;
    public string Name => Profile.Name;
    public string Protocol => Profile.Protocol;
    public string ProtocolLabel => Profile.ProtocolLabel;
    public string Badge => ProtocolDisplay.Badge(Profile.Protocol);
    public string Address => Profile.AddressLabel;

    public bool IsTelegramOnly => Profile.IsTelegramOnly;
    public bool Favorite => Profile.Favorite;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Profile.Group)) parts.Add(Profile.Group!);
            if (IsTelegramOnly) parts.Add("только Telegram");
            return string.Join(" · ", parts);
        }
    }

    public Brush AvatarBrush
    {
        get
        {
            var color = (Color)new ProtoColorConv().Convert(Profile.Protocol, typeof(Color), null!, System.Globalization.CultureInfo.InvariantCulture);
            return new LinearGradientBrush(color, Color.FromRgb(
                (byte)(color.R * 0.72), (byte)(color.G * 0.72), (byte)(color.B * 0.72)), 45);
        }
    }

    public ImageSource? Icon { get; }

    public int PingMs
    {
        get => _pingMs;
        set
        {
            if (SetProperty(ref _pingMs, value)) Raise(nameof(PingReady));
        }
    }

    public bool PingReady => _pingMs >= 0;

    public bool IsPinging
    {
        get => _isPinging;
        set => SetProperty(ref _isPinging, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(ProtocolLabel));
        Raise(nameof(Badge));
        Raise(nameof(Address));
        Raise(nameof(Favorite));
        Raise(nameof(Subtitle));
        Raise(nameof(AvatarBrush));
    }
}

internal static class ProtocolDisplay
{
    public static string Badge(string protocol) => (string)new ProtoBadgeConv()
        .Convert(protocol, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);
}
