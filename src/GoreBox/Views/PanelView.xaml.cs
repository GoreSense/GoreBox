using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using GoreBox.Utils;

namespace GoreBox.Views;

/// <summary>
/// Вкладка «Панель»: браузер WebView2. URL запоминается, страница прогружается в фоне при
/// старте (чтобы при входе во вкладку она была сразу открыта), при сворачивании в трей браузер
/// выгружается полностью, при возврате из трея — подгружается заново.
/// </summary>
public partial class PanelView : UserControl
{
    private WebView2? _browser;
    private bool _busy;

    public PanelView() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await EnsureBrowserAsync();

    /// <summary>Создать браузер и открыть сохранённый URL (старт приложения и возврат из трея).</summary>
    public async Task EnsureBrowserAsync()
    {
        if (_busy || _browser is not null) return;
        _busy = true;
        try
        {
            var browser = new WebView2();
            BrowserHost.Children.Add(browser);
            _browser = browser;

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(AppPaths.DataRoot, "webview2"));
            await browser.EnsureCoreWebView2Async(env);

            var core = browser.CoreWebView2;
            core.NavigationStarting += (_, args) => RememberUrl(args.Uri);
            core.SourceChanged += (_, _) => SyncUrlBox();

            var url = Normalize(App.Settings.Current.PanelUrl);
            if (url is not null) core.Navigate(url);
        }
        catch (Exception ex)
        {
            // WebView2 Runtime отсутствует или не запустился — оставляем подсказку вместо браузера
            try { BrowserHost.Children.Clear(); } catch { /* ignore */ }
            _browser = null;
            var hint = new TextBlock
            {
                Text = "Не удалось запустить браузер: " + ex.Message + Environment.NewLine +
                       "Нужен Microsoft Edge WebView2 Runtime (обычно уже установлен в Windows 11).",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (TryFindResource("TxtMuted") is Style muted) hint.Style = muted;
            BrowserHost.Children.Add(hint);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Выгрузить страницу (сворачивание в трей): освобождаем ресурсы браузера.</summary>
    public void UnloadBrowser()
    {
        var browser = _browser;
        _browser = null;
        if (browser is null) return;
        BrowserHost.Children.Clear();
        try { browser.Dispose(); }
        catch { /* процесс WebView2 мог уже завершиться */ }
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitUrl();
    }

    private void OnUrlLostFocus(object sender, RoutedEventArgs e) => CommitUrl();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => CommitUrl();

    private void CommitUrl()
    {
        var url = Normalize(UrlBox.Text);
        if (url is null) return;
        App.Settings.Current.PanelUrl = url;   // запоминаем последний введённый адрес
        App.Settings.SaveSoon();
        UrlBox.Text = url;
        if (_browser?.CoreWebView2 is { } core) core.Navigate(url);
        else _ = EnsureBrowserAsync();
    }

    private void RememberUrl(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        App.Settings.Current.PanelUrl = uri;
        App.Settings.SaveSoon();
    }

    private void SyncUrlBox()
    {
        if (_browser?.CoreWebView2 is not { } core) return;
        if (UrlBox.IsKeyboardFocused) return;
        var src = core.Source;
        if (string.IsNullOrWhiteSpace(src) || src.Contains("about:blank", StringComparison.OrdinalIgnoreCase)) return;
        UrlBox.Text = src;
    }

    /// <summary>Схему https:// добавляем сами; строки без неё (yandex.ru) тоже открываем.</summary>
    private static string? Normalize(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.ToString() : null;
    }
}
