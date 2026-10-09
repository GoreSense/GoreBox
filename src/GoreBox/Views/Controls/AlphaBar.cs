using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GoreBox.Views.Controls;

/// <summary>
/// Полоска прозрачности: тонкая (6 px), без видимого ползунка. Заполнение слева показывает
/// долю непрозрачности. Тянуть можно в любом месте контрола (он выше полоски для удобства).
/// </summary>
public sealed class AlphaBar : FrameworkElement
{
    private const double StripHeight = 6;

    private double _alpha = 1;
    private Color _baseColor = Colors.White;
    private bool _dragging;

    public AlphaBar()
    {
        IsHitTestVisible = true;
    }

    /// <summary>Непрозрачность, 0..1.</summary>
    public double Alpha
    {
        get => _alpha;
        set { _alpha = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    /// <summary>Цвет заполнения. Альфа здесь игнорируется: заполнение всегда непрозрачное.</summary>
    public Color BaseColor
    {
        get => _baseColor;
        set { _baseColor = Color.FromArgb(255, value.R, value.G, value.B); InvalidateVisual(); }
    }

    /// <summary>Непрозрачность изменена мышью.</summary>
    public event Action? AlphaChanged;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // невидимая подложка на весь контрол — по ней удобно попадать
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double top = (h - StripHeight) / 2;
        double r = StripHeight / 2;
        var strip = new Rect(0, top, w, StripHeight);

        var track = TryFindResource("InputBg") as Brush ?? Brushes.Transparent;
        var edge = TryFindResource("InputBorder") as Brush ?? Brushes.Gray;
        dc.DrawRoundedRectangle(track, new Pen(edge, 1), strip, r, r);

        if (_alpha > 0)
            dc.DrawRoundedRectangle(new SolidColorBrush(_baseColor), null,
                new Rect(0, top, w * _alpha, StripHeight), r, r);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        SetFromX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void SetFromX(double x)
    {
        if (ActualWidth <= 0) return;
        _alpha = Math.Clamp(x / ActualWidth, 0, 1);
        InvalidateVisual();
        AlphaChanged?.Invoke();
    }
}
