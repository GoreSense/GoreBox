using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoreBox.Utils;

namespace GoreBox.Views.Controls;

/// <summary>
/// Цветовой круг в духе Photoshop: кольцо оттенка снаружи и квадрат внутри
/// (по горизонтали насыщенность, по вертикали яркость). Оттенок — градусы 0..360,
/// 0° — красный, сверху, по часовой стрелке. Круг вписан в квадрат контрола.
/// </summary>
public sealed class ColorWheel : FrameworkElement
{
    private enum DragMode { None, Hue, Square }

    private readonly record struct WheelGeometry(double Cx, double Cy, double Outer, double Inner, Rect Square);

    private double _hue;
    private double _saturation = 1;
    private double _brightness = 1;
    private DragMode _drag = DragMode.None;
    private BitmapSource? _ring;
    private int _ringPixels;

    public ColorWheel()
    {
        IsHitTestVisible = true;
    }

    /// <summary>Оттенок, 0..360.</summary>
    public double Hue
    {
        get => _hue;
        set { _hue = ((value % 360) + 360) % 360; InvalidateVisual(); }
    }

    /// <summary>Насыщенность, 0..1 (горизонталь квадрата).</summary>
    public double Saturation
    {
        get => _saturation;
        set { _saturation = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    /// <summary>Яркость, 0..1 (вертикаль квадрата, внизу — 0).</summary>
    public double Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    /// <summary>Цвет изменён мышью. Программная установка значений событие не вызывает.</summary>
    public event Action? ColorChanged;

    private WheelGeometry? GetGeometry()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 60) return null;

        double c = size / 2;
        double outer = c - 1;
        double inner = outer - size * 0.09;            // толщина кольца ~9% размера
        double half = (inner - 6) / Math.Sqrt(2);      // квадрат, вписанный во внутренний круг с отступом
        return new WheelGeometry(c, c, outer, inner, new Rect(c - half, c - half, half * 2, half * 2));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var g = GetGeometry();
        if (g is null) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        var geo = g.Value;

        // прозрачная подложка: вся область контрола принимает мышь
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));

        // кольцо строим в физических пикселях, чтобы на масштабе 125–150% не было мыла
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        dc.DrawImage(GetRing((int)Math.Round(size * scale), scale), new Rect(0, 0, size, size));

        // квадрат: белый → чистый оттенок (слева направо), поверх — прозрачный → чёрный (сверху вниз)
        var sq = geo.Square;
        var pure = ColorMath.FromHsv(_hue, 1, 1);
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, pure, new Point(0, 0), new Point(1, 0)), null, sq);
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black,
            new Point(0, 0), new Point(0, 1)), null, sq);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), sq);

        // маркеры: оттенок на кольце и насыщенность/яркость в квадрате
        double rm = (geo.Outer + geo.Inner) / 2;
        double rad = _hue * Math.PI / 180;
        DrawMarker(dc, new Point(geo.Cx + rm * Math.Sin(rad), geo.Cy - rm * Math.Cos(rad)));
        DrawMarker(dc, new Point(sq.Left + _saturation * sq.Width, sq.Top + (1 - _brightness) * sq.Height));
    }

    private static void DrawMarker(DrawingContext dc, Point p)
    {
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1.5), p, 7, 7);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2), p, 5.5, 5.5);
    }

    /// <summary>Кольцо оттенков строим один раз на нужный размер (пиксели премультиплицированы).</summary>
    private BitmapSource GetRing(int px, double marginPx)
    {
        if (_ring is not null && _ringPixels == px) return _ring;

        var bmp = new WriteableBitmap(px, px, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new int[px * px];
        double c = px / 2.0;
        double outer = c - marginPx;
        double inner = outer - px * 0.09;

        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                double dx = x + 0.5 - c;
                double dy = y + 0.5 - c;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(outer - d + 0.5, 0, 1) * Math.Clamp(d - inner + 0.5, 0, 1);
                if (alpha <= 0) continue;

                double deg = Math.Atan2(dx, -dy) * 180 / Math.PI;
                if (deg < 0) deg += 360;

                var col = ColorMath.FromHsv(deg, 1, 1);
                int a = (int)Math.Round(alpha * 255);
                int r = col.R * a / 255;
                int gg = col.G * a / 255;
                int b = col.B * a / 255;
                pixels[y * px + x] = (a << 24) | (r << 16) | (gg << 8) | b;
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, px, px), pixels, px * 4, 0);
        bmp.Freeze();
        _ring = bmp;
        _ringPixels = px;
        return bmp;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var g = GetGeometry();
        if (g is null) return;
        var geo = g.Value;

        var p = e.GetPosition(this);
        double d = Math.Sqrt((p.X - geo.Cx) * (p.X - geo.Cx) + (p.Y - geo.Cy) * (p.Y - geo.Cy));
        if (d >= geo.Inner - 4 && d <= geo.Outer + 4) _drag = DragMode.Hue;
        else if (geo.Square.Contains(p)) _drag = DragMode.Square;
        else return;

        CaptureMouse();
        UpdateFrom(p, geo);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None) return;
        var g = GetGeometry();
        if (g is not null) UpdateFrom(e.GetPosition(this), g.Value);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        ReleaseMouseCapture();
    }

    private void UpdateFrom(Point p, WheelGeometry geo)
    {
        if (_drag == DragMode.Hue)
        {
            double deg = Math.Atan2(p.X - geo.Cx, -(p.Y - geo.Cy)) * 180 / Math.PI;
            _hue = (deg + 360) % 360;
        }
        else if (_drag == DragMode.Square)
        {
            _saturation = Math.Clamp((p.X - geo.Square.Left) / geo.Square.Width, 0, 1);
            _brightness = Math.Clamp(1 - (p.Y - geo.Square.Top) / geo.Square.Height, 0, 1);
        }

        InvalidateVisual();
        ColorChanged?.Invoke();
    }
}
