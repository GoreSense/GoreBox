namespace GoreBox.Models;

/// <summary>Запись выпадающего списка навигации (имя + стабильный id).</summary>
public interface INavEntry
{
    string Id { get; set; }
    string Name { get; set; }
}

/// <summary>Панель во вкладке «Панели»: именованная запись со своим браузером и URL.</summary>
public sealed class PanelEntry : INavEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>Подключение во вкладке «SSH»: именованная запись со своим терминалом.
/// Логин, пароль и размер шрифта — свои для каждой записи (заполняются шестерёнкой).</summary>
public sealed class SshEntry : INavEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public double FontSize { get; set; } = 13;
}
