using System.Drawing;
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
    private readonly Icon _iconOn;    // цветной: прокси работает
    private readonly Icon _iconOff;   // серый: прокси выключен
    private bool _proxyOn;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _iconOn = LoadIcon("app.ico") ?? ExeIcon();
        _iconOff = LoadIcon("app-gray.ico") ?? _iconOn;

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

    /// <summary>Значок в трее: цветной, когда прокси работает, серый — когда выключен.</summary>
    public void SetProxyOn(bool on)
    {
        if (_disposed || _proxyOn == on) return;
        _proxyOn = on;
        try { _icon.Icon = on ? _iconOn : _iconOff; }
        catch { /* ignore */ }
    }

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
