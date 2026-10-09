using System.Windows;
using System.Windows.Controls;
using GoreBox.Models;

namespace GoreBox.Views;

/// <summary>
/// Вкладка «Панели»: держит страницу (браузер) для каждой созданной панели и показывает
/// выбранную. Страницы живут отдельно друг от друга: у каждой свой URL и свой WebView2,
/// выгрузка в трей касается всех страниц сразу, удаление панели выгружает её страницу.
/// </summary>
public partial class PanelView : UserControl
{
    private readonly Dictionary<string, PanelPage> _pages = new();
    private PanelEntry? _current;

    public PanelView() => InitializeComponent();

    /// <summary>Панель, открытая сейчас (или null — пустое состояние).</summary>
    public PanelEntry? Current => _current;

    /// <summary>Показать страницу панели; null — пустое состояние с подсказкой.</summary>
    public void ShowEntry(PanelEntry? entry)
    {
        _current = entry;
        if (entry is null)
        {
            Host.Content = null;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        if (!_pages.TryGetValue(entry.Id, out var page))
        {
            page = new PanelPage(entry);
            _pages[entry.Id] = page;
        }
        Host.Content = page;
        _ = page.EnsureBrowserAsync();
    }

    /// <summary>Панель удалена: выгружаем её браузер и, если она была открыта, показываем пустое состояние.</summary>
    public void RemoveEntry(PanelEntry entry)
    {
        if (_pages.Remove(entry.Id, out var page)) page.UnloadBrowser();
        if (_current is not null && _current.Id == entry.Id) ShowEntry(null);
    }

    /// <summary>Возврат из трея: подгружаем страницу выбранной панели обратно.</summary>
    public void Restore()
    {
        if (_current is null) return;
        if (_pages.TryGetValue(_current.Id, out var page)) _ = page.EnsureBrowserAsync();
    }

    /// <summary>Выгрузить все страницы (сворачивание в трей, выход из приложения).</summary>
    public void UnloadAll()
    {
        foreach (var page in _pages.Values) page.UnloadBrowser();
    }
}
