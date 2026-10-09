using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GoreBox.Models;

namespace GoreBox.Utils;

/// <summary>Tag (double / int / CornerRadius / "12,8") -&gt; CornerRadius. Lets one button template serve many radii.</summary>
public sealed class RadiusConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case CornerRadius cr: return cr;
            case double d: return new CornerRadius(d);
            case int i: return new CornerRadius(i);
            case string s when s.Contains(','):
                var p = s.Split(',');
                if (p.Length == 4 && double.TryParse(p[0], out var a) && double.TryParse(p[1], out var b)
                    && double.TryParse(p[2], out var c) && double.TryParse(p[3], out var e))
                    return new CornerRadius(a, b, c, e);
                return new CornerRadius(8);
            case string s2 when double.TryParse(s2, out var v): return new CornerRadius(v);
            default: return new CornerRadius(8);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>RouteAction -&gt; brush.</summary>
public sealed class ActionBrushConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            RouteAction.Proxy => "Accent",
            RouteAction.Direct => "TextSecondary",
            RouteAction.Block => "Danger",
            _ => "TextSecondary"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>RouteAction -&gt; short human text.</summary>
public sealed class ActionTextConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RouteAction.Proxy => "PROXY",
            RouteAction.Direct => "DIRECT",
            RouteAction.Block => "BLOCK",
            _ => "—"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows element unless action == parameter (used by segmented toggles that hide the active item).</summary>
public sealed class ActionToVisibilityConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Latency in ms -&gt; status brush.</summary>
public sealed class PingBrushConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value is int i ? i : -1;
        var key = ms < 0 ? "TextMuted" : ms < 120 ? "Success" : ms < 350 ? "Warning" : "Danger";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InvBoolToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class NullOrEmptyToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return empty ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NotNullToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Deterministic accent colour from a protocol name.</summary>
public sealed class ProtoColorConv : IValueConverter
{
    // фирменные цвета плиток протоколов (как на макете)
    private static readonly Dictionary<string, string> Tile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vmess"] = "#4457AB",
        ["vless"] = "#2F8072",
        ["trojan"] = "#A63434",
        ["shadowsocks"] = "#6B51D6",
        ["wireguard"] = "#3E7BD6",
        ["hysteria"] = "#5C39B5",
        ["hysteria2"] = "#5C39B5",
        ["http"] = "#617493",
        ["mixed"] = "#40454F",
        ["tunnel"] = "#EF8A22",
        ["tun"] = "#3F9C50",
        ["mtproto"] = "#3C82D6",
        ["amneziawg"] = "#8C5DF5",
        ["tuic"] = "#305B60",
        ["shadowsocksr"] = "#6B51D6",
        ["socks"] = "#E08A3C",
        ["ssh"] = "#3FB9A4",
        ["anytls"] = "#D96A9B",
    };

    private static readonly string[] Palette =
    {
        "#5C96FF", "#7C6BF0", "#3FB9A4", "#E08A3C", "#D96A9B", "#4FA8DE", "#9C7BE8", "#5FBF6B"
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        if (Tile.TryGetValue(s, out var fixedCol))
            return (Color)ColorConverter.ConvertFromString(fixedCol);

        var hash = 17;
        foreach (var ch in s) hash = hash * 31 + ch;
        var col = Palette[Math.Abs(hash) % Palette.Length];
        return (Color)ColorConverter.ConvertFromString(col);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Protocol name → vector glyph brush (белая иконка на плитке); null — глифа нет, показываем буквы.</summary>
public sealed class ProtoGlyphConv : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ProtoGlyphs.Brush(value?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Буквенный бейдж виден только если для протокола нет векторного глифа.</summary>
public sealed class ProtoGlyphVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ProtoGlyphs.Brush(value?.ToString()) is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Набор протокольных глифов (24×24): заливка или контур, масштаб Uniform.</summary>
internal static class ProtoGlyphs
{
    // Stroke=false — фигура заливается; Stroke=true — контур толщиной W.
    private static readonly Dictionary<string, (string Data, bool Stroke, double W)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vmess"] = ("M4.4,5 L9.5,5 L12,14.6 L14.5,5 L19.6,5 L12,19 Z", false, 0),
        ["vless"] = ("M5,5 L10,5 L12.1,13.4 L14.2,5 L19,5 L13.8,19 L10.2,19 Z", false, 0),
        ["trojan"] = ("M4.6,12.6 a7.4,7.4 0 0 1 14.8,0 v7.4 h-4 V15 h-3.6 v5 H4.6 Z M9.6,5 L11.2,3 L13.6,3.3 L14.6,5 Z", false, 0),
        ["shadowsocks"] = ("M16.6,7.6 C15.2,5.7 13,5.3 11.2,6.1 C9,7.1 8.4,9.5 9.8,10.9 C11.2,12.3 14.6,11.9 16,13.3 C17.4,14.7 16.6,17.5 14.2,18.3 C12.4,18.9 10.2,18.5 8.6,16.9", true, 2.7),
        ["wireguard"] = ("M12,3.4 L19.4,7.7 V16.3 L12,20.6 L4.6,16.3 V7.7 Z M15.5,12 A3.5,3.5 0 1 1 8.5,12 A3.5,3.5 0 1 1 15.5,12 M12,6.9 V8.6 M12,15.4 V17.1 M6.9,12 H8.6 M15.4,12 H17.1 M8.4,8.4 L9.7,9.7 M14.3,14.3 L15.6,15.6 M15.6,8.4 L14.3,9.7 M9.7,14.3 L8.4,15.6", true, 1.5),
        ["hysteria"] = ("M7.4,5.6 H11.2 L9.3,18.6 H5.5 Z M15.6,5.6 H19.4 L17.5,18.6 H13.7 Z M8.9,10.7 H17.4 L16.9,14.1 H8.4 Z M2,11.1 H5.1 V12.7 H2 Z M0.9,14.6 H4.6 V16.2 H0.9 Z", false, 0),
        ["hysteria2"] = ("M7.4,5.6 H11.2 L9.3,18.6 H5.5 Z M15.6,5.6 H19.4 L17.5,18.6 H13.7 Z M8.9,10.7 H17.4 L16.9,14.1 H8.4 Z M2,11.1 H5.1 V12.7 H2 Z M0.9,14.6 H4.6 V16.2 H0.9 Z", false, 0),
        ["http"] = ("M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 M12,4 C8.6,6.7 8.6,17.3 12,20 C15.4,17.3 15.4,6.7 12,4 Z M4.1,12 H19.9 M6,8.2 C8.4,9.3 15.6,9.3 18,8.2 M6,15.8 C8.4,14.7 15.6,14.7 18,15.8", true, 1.7),
        ["mixed"] = ("M4,9 L10.6,5.8 L10.6,7.6 L17,7.6 L17,10.4 L10.6,10.4 L10.6,12.2 Z M20,15.4 L13.4,12.2 L13.4,14 L7,14 L7,16.8 L13.4,16.8 L13.4,18.6 Z", false, 0),
        ["tunnel"] = ("M4.6,19.4 V12.4 a7.4,7.4 0 0 1 14.8,0 v7 M12,13.8 V15.8 M12,17.4 V19.4", true, 2.6),
        ["tun"] = ("M5.6,17.8 a6.4,6.4 0 0 1 12.8,0 M13.7,14 A1.7,1.7 0 1 1 10.3,14 A1.7,1.7 0 1 1 13.7,14 M12,15.7 V17.7", true, 2.6),
        ["mtproto"] = ("M21,4.2 L3.5,11.5 L9.8,13.9 L18.5,7.1 L11.5,14.1 L11.3,18.7 L14.1,16.6 L18.6,19.9 Z", false, 0),
        ["amneziawg"] = ("M12,4.4 L4.3,19.6 L8.5,19.6 L12,12 L15.5,19.6 L19.7,19.6 Z", false, 0),
        ["tuic"] = ("M7.3,8.8 H16.4 M16.4,8.8 L14,6.7 M16.4,8.8 L14,10.9 M16.9,10 L12.6,17.6 M12.6,17.6 L15.87,16.01 M12.6,17.6 L12.31,13.97 M12.6,17.6 L6.9,10 M6.9,10 L6.61,13.63 M6.9,10 L10.17,11.59", true, 2.3),
        ["shadowsocksr"] = ("M16.6,7.6 C15.2,5.7 13,5.3 11.2,6.1 C9,7.1 8.4,9.5 9.8,10.9 C11.2,12.3 14.6,11.9 16,13.3 C17.4,14.7 16.6,17.5 14.2,18.3 C12.4,18.9 10.2,18.5 8.6,16.9", true, 2.7),
        ["socks"] = ("M13.4,12 A4.2,4.2 0 1 1 5,12 A4.2,4.2 0 1 1 13.4,12 M19,12 A4.2,4.2 0 1 1 10.6,12 A4.2,4.2 0 1 1 19,12", true, 2),
        ["ssh"] = ("M5.5,4.5 H18.5 A2,2 0 0 1 20.5,6.5 V17.5 A2,2 0 0 1 18.5,19.5 H5.5 A2,2 0 0 1 3.5,17.5 V6.5 A2,2 0 0 1 5.5,4.5 Z M7.4,9.4 L10.6,12.6 L7.4,15.8 M13,15.8 H16.4", true, 1.8),
        ["anytls"] = ("M6.5,10.8 H17.5 V18.8 A2.4,2.4 0 0 1 15.1,21.2 H8.9 A2.4,2.4 0 0 1 6.5,18.8 Z M8.8,10.8 V8.4 A3.2,3.2 0 0 1 15.2,8.4 V10.8 M12,14 V16.6", true, 2),
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Brush? Brush(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol)) return null;
        return Cache.GetOrAdd(protocol, Make);
    }

    private static Brush? Make(string protocol)
    {
        if (!Map.TryGetValue(protocol, out var spec)) return null;
        Geometry geo;
        try { geo = Geometry.Parse(spec.Data); }
        catch { return null; }

        Drawing drawing = spec.Stroke
            ? new GeometryDrawing(null, new Pen(Brushes.White, spec.W)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }, geo)
            : new GeometryDrawing(Brushes.White, null, geo);

        var brush = new DrawingBrush(drawing)
        {
            Viewbox = new Rect(0, 0, 24, 24),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Uniform,
        };
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}

/// <summary>Protocol name -&gt; short badge label (sing-box style tag).</summary>
public sealed class ProtoBadgeConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value?.ToString() ?? "").Trim();
        if (s.Length == 0) return "?";
        return s.ToLowerInvariant() switch
        {
            "wireguard" => "WG",
            "amneziawg" => "AWG",
            "shadowsocks" => "SS",
            "shadowsocksr" => "SSR",
            "socks" => "SOCKS",
            "http" => "HTTP",
            "vless" => "VLESS",
            "vmess" => "VMess",
            _ => s.Length <= 4 ? s.ToUpperInvariant() : s[..4].ToUpperInvariant()
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PingTextConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value is int i ? i : -1;
        return ms switch
        {
            < 0 => "—",
            0 => "<1 ms",
            _ => $"{ms} ms"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Latency -&gt; bar width for the compact latency indicator.</summary>
public sealed class LatencyToWidthConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value is int i ? i : -1;
        if (ms < 0) return 0d;
        var clamped = Math.Clamp(ms, 1, 500);
        return 6 + (44 * (1.0 - (clamped - 1) / 499.0));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EnumEqualsConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        var p = parameter.ToString()!;
        return string.Equals(value.ToString(), p, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null) return parameter;
        return Binding.DoNothing;
    }
}

public sealed class StringEqualsConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Значение входит в список параметров через запятую → Visibility.</summary>
public sealed class InListToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var v = value?.ToString() ?? "";
        var list = (parameter?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Contains(v, StringComparer.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Строка == параметр → Visibility (условные поля настроек).</summary>
public sealed class StringEqualsToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Ключ кисти (строка) → Brush из ресурсов приложения.</summary>
public sealed class BrushKeyConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value?.ToString() ?? "TextMuted") as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Строка == параметр → Visible (переключение вкладок).</summary>
public sealed class StringToVisConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>RouteAction → короткая подпись (PROXY/DIRECT/BLOCK).</summary>
public sealed class RouteKindTextConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            RouteKind.Domain => "домен",
            RouteKind.DomainSuffix => "суффикс",
            RouteKind.DomainKeyword => "ключ",
            RouteKind.DomainRegex => "regex",
            RouteKind.Ip => "IP",
            RouteKind.IpCidr => "подсеть",
            _ => "—"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MatchKindTextConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            MatchKind.Domain => "Домен",
            MatchKind.Ip => "IP / подсеть",
            MatchKind.Keyword => "Ключевое слово",
            MatchKind.Regex => "Regex",
            _ => "—"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToOpacityConv : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
