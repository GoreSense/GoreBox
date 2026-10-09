using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using GoreBox.Utils;
using WinForms = System.Windows.Forms;

namespace GoreBox.Services;

/// <summary>
/// Иконка в системном трее: ЛКМ — открыть окно, ПКМ — меню с единственным пунктом «Закрыть»
/// (полный выход приложения). После <see cref="Dispose"/> события игнорируются.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private Icon? _iconOn;          // логотип в цвете индикатора «Подключено»: прокси работает
    private readonly Icon _iconOff; // серый: прокси выключен
    private bool _proxyOn;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _iconOff = LoadIcon("app-gray.ico") ?? ExeIcon();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _iconOff,   // при старте прокси выключен
            Visible = true,
            Text = "GoreBox"
        };

        var menu = new WinForms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new MinimalMenuRenderer()
        };

        var closeItem = new WinForms.ToolStripMenuItem("Закрыть")
        {
            ForeColor = Color.White,
            AutoSize = false,
            Size = new System.Drawing.Size(180, 30)
        };
        closeItem.Click += (_, _) =>
        {
            if (_disposed) return;
            ExitRequested?.Invoke();
        };
        menu.Items.Add(closeItem);

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (_disposed || e.Button != WinForms.MouseButtons.Left) return;
            OpenRequested?.Invoke();
        };
        _icon.DoubleClick += (_, _) =>
        {
            if (_disposed) return;
            OpenRequested?.Invoke();
        };
    }

    /// <summary>Значок из ресурсов приложения (папка Assets); null, если ресурса нет.</summary>
    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/" + fileName);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Icon(stream, 16, 16);
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Запасной значок: иконка самого exe (если ресурсы не нашлись).</summary>
    private static Icon ExeIcon()
    {
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (!string.IsNullOrEmpty(exe)) return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch { /* ignore */ }
        return SystemIcons.Application;
    }

    /// <summary>
    /// Значок в трее: тот же логотип, но в цвет индикатора «Подключено», когда прокси работает,
    /// серый логотип — когда выключен.
    /// </summary>
    public void SetProxyOn(bool on)
    {
        if (_disposed || _proxyOn == on) return;
        _proxyOn = on;
        try
        {
            if (on)
            {
                // пересобираем: цвет Success зависит от темы
                _iconOn?.Dispose();
                _iconOn = BuildOnIcon();
            }
            _icon.Icon = on ? _iconOn : _iconOff;
        }
        catch { /* ignore */ }
    }

    /// <summary>Тот же логотип, что и app.ico, но в цвете кисти Success (как индикатор «Подключено»).</summary>
    private static Icon BuildOnIcon()
    {
        var brush = System.Windows.Application.Current?.TryFindResource("Success") as System.Windows.Media.SolidColorBrush;
        var bright = brush is null
            ? Color.FromArgb(0x3B, 0xC0, 0x8A)
            : Color.FromArgb(255, brush.Color.R, brush.Color.G, brush.Color.B);
        var dark = Color.FromArgb(255,
            (byte)(bright.R * 0.2f), (byte)(bright.G * 0.2f), (byte)(bright.B * 0.2f));

        using var srcIcon = LoadIcon("app.ico");
        using var src = (srcIcon ?? ExeIcon()).ToBitmap();
        var dst = new Bitmap(src.Width, src.Height);
        for (var y = 0; y < src.Height; y++)
        {
            for (var x = 0; x < src.Width; x++)
            {
                var p = src.GetPixel(x, y);
                if (p.A == 0) continue;   // прозрачное — оставляем прозрачным
                // яркость исходного логотипа -> палитра Success: тёмная плитка, яркая метка
                var lum = (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) / 255f;
                var t = Math.Clamp((lum - 0.1f) / 0.52f, 0f, 1f);
                dst.SetPixel(x, y, Color.FromArgb(p.A,
                    (byte)(dark.R + (bright.R - dark.R) * t),
                    (byte)(dark.G + (bright.G - dark.G) * t),
                    (byte)(dark.B + (bright.B - dark.B) * t)));
            }
        }
        var h = dst.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void SetStatus(string text)
    {
        try
        {
            if (text.Length > 62) text = text[..59] + "…";
            _icon.Text = text;
        }
        catch { /* ignore */ }
    }

    public void ShowHint(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(3000);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { /* ignore */ }
    }

    /// <summary>Плоское тёмное меню без системных отступов-нейрослопа.</summary>
    private sealed class MinimalMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public MinimalMenuRenderer()
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Color.FromArgb(31, 34, 38));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(4, 2, e.Item.Width - 8, e.Item.Height - 4);
            using var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(214, 69, 69) : Color.FromArgb(31, 34, 38));
            e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(58, 64, 73));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
}
