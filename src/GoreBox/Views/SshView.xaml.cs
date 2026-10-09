using System.Windows;
using System.Windows.Controls;
using GoreBox.Models;

namespace GoreBox.Views;

/// <summary>
/// Вкладка «SSH»: держит страницу (терминал) для каждого созданного подключения и показывает
/// выбранное. Страницы живут отдельно друг от друга: у каждого свой адрес и своё SSH-соединение.
/// В трей соединения НЕ рвутся; рвутся при выходе и при удалении записи.
/// </summary>
public partial class SshView : UserControl
{
    private readonly Dictionary<string, SshPage> _pages = new();
    private SshEntry? _current;

    public SshView() => InitializeComponent();

    /// <summary>Подключение, открытое сейчас (или null — пустое состояние).</summary>
    public SshEntry? Current => _current;

    /// <summary>Показать страницу подключения; null — пустое состояние с подсказкой.</summary>
    public void ShowEntry(SshEntry? entry)
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
            page = new SshPage(entry);
            _pages[entry.Id] = page;
        }
        Host.Content = page;
    }

    /// <summary>Подключение удалено: рвём его соединение и, если оно было открыто, показываем пустое состояние.</summary>
    public void RemoveEntry(SshEntry entry)
    {
        if (_pages.Remove(entry.Id, out var page)) page.Shutdown();
        if (_current is not null && _current.Id == entry.Id) ShowEntry(null);
    }

    /// <summary>Закрыть все соединения (выход из приложения).</summary>
    public void ShutdownAll()
    {
        foreach (var page in _pages.Values) page.Shutdown();
    }
}
