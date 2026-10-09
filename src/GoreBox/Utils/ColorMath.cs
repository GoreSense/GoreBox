using System.Windows.Media;

namespace GoreBox.Utils;

/// <summary>Перевод цвета между RGB и HSV (нужен цветовому кругу акцента).</summary>
public static class ColorMath
{
    /// <summary>RGB → HSV. Оттенок h — градусы [0..360), насыщенность s и яркость v — [0..1].</summary>
    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0)
        {
            h = 0;   // серый: оттенок не определён
            return;
        }

        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);

        if (h < 0) h += 360;
    }

    /// <summary>HSV → цвет. Прозрачность задаётся отдельно (a).</summary>
    public static Color FromHsv(double h, double s, double v, byte a = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double r, g, b;
        switch ((int)(h / 60) % 6)
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }

        return Color.FromArgb(a,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
