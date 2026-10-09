using System.Windows;
using System.Windows.Media;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Переключение светлой/тёмной темы и акцентного цвета во время работы приложения.</summary>
public static class ThemeService
{
    public static ThemeMode Current { get; private set; } = ThemeMode.Dark;

    // Словарь акцента лежит в конце MergedDictionaries и перекрывает кисти темы.
    private static ResourceDictionary? _accentDict;
    private static string? _accentHex;

    public static void Apply(ThemeMode mode)
    {
        Current = mode;
        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var uri = new Uri($"Themes/{(mode == ThemeMode.Light ? "Light" : "Dark")}.xaml", UriKind.Relative);
        var replacement = new ResourceDictionary { Source = uri };

        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Themes/Dark.xaml") || src.Contains("Themes/Light.xaml"))
            {
                dicts[i] = replacement;
                ReapplyAccent(app);
                return;
            }
        }
        dicts.Insert(0, replacement);
        ReapplyAccent(app);
    }

    /// <summary>Цвет темы по умолчанию (для кнопки «по умолчанию» в пикере).</summary>
    public static Color DefaultAccent => Current == ThemeMode.Light
        ? Color.FromRgb(0x2E, 0x6E, 0xE0)
        : Color.FromRgb(0x5C, 0x96, 0xFF);

    /// <summary>
    /// Применить пользовательский акцент поверх палитры темы (или убрать override, если
    /// <paramref name="hex"/> пуст). Прозрачность (AA) сохраняется как есть.
    /// </summary>
    public static void ApplyAccent(string? hex)
    {
        _accentHex = string.IsNullOrWhiteSpace(hex) ? null : hex;
        if (Application.Current is { } app) ReapplyAccent(app);
    }

    private static void ReapplyAccent(Application app)
    {
        var dicts = app.Resources.MergedDictionaries;

        if (_accentDict is not null)
        {
            dicts.Remove(_accentDict);
            _accentDict = null;
        }
        if (_accentHex is null) return;

        if (!TryParseColor(_accentHex, out var color)) return;

        var hover = Lighten(color, 0.14f);
        var pressed = Darken(color, 0.12f);
        var soft = Color.FromArgb(0x38, color.R, color.G, color.B);

        var dict = new ResourceDictionary
        {
            ["ColAccent"] = color,
            ["ColAccentHover"] = hover,
            ["ColAccentPressed"] = pressed,
            ["Accent"] = Freeze(new SolidColorBrush(color)),
            ["AccentHover"] = Freeze(new SolidColorBrush(hover)),
            ["AccentPressed"] = Freeze(new SolidColorBrush(pressed)),
            ["AccentSoft"] = Freeze(new SolidColorBrush(soft)),
            ["LogoGradient"] = MakeGradient(hover, pressed)
        };

        _accentDict = dict;
        dicts.Add(dict);   // в конце — перекрывает палитру темы
    }

    private static SolidColorBrush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush MakeGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>#RRGGBB или #AARRGGBB (формат Color.ToString(), в нём цвет и хранится).</summary>
    public static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString("#" + s);
            if (s.Length == 6) color.A = 255;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Color Lighten(Color c, float amount) => Adjust(c, amount, lighter: true);
    private static Color Darken(Color c, float amount) => Adjust(c, amount, lighter: false);

    private static Color Adjust(Color c, float amount, bool lighter)
    {
        static byte Mix(byte ch, float k, bool lighter) => (byte)Math.Clamp((int)(lighter ? ch + (255 - ch) * k : ch * (1 - k)), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R, amount, lighter), Mix(c.G, amount, lighter), Mix(c.B, amount, lighter));
    }
}
