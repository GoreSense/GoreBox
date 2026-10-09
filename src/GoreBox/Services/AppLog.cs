namespace GoreBox.Services;

/// <summary>
/// Общая точка записи событий приложения (SSH, профили, ядро). Вызывать можно из любого потока;
/// подписчик (главная модель) пишет строку в файл журнала и в панель логов.
/// </summary>
public static class AppLog
{
    public static event Action<string>? Written;

    public static void Write(string line)
    {
        try
        {
            Written?.Invoke(line);
        }
        catch (Exception)
        {
            // журнал не должен ломать работу приложения
        }
    }
}
