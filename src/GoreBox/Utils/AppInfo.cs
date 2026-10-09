using System.Reflection;

namespace GoreBox.Utils;

/// <summary>
/// Метка сборки приложения.
///
/// Меняется в каждом коммите, который влияет на поведение: первая строка app.log
/// («GoreBox 1.2.0 (neko-grpc) · ядро: …») сразу показывает, какой код запущен.
/// Без неё легко принять старый бинарник за новую сборку и чинить уже починенное.
/// </summary>
public static class AppInfo
{
    /// <summary>Короткая метка кода. Обновлять вместе с изменениями в логике подключения.</summary>
    public const string BuildStamp = "term-round";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Full => Version + " (" + BuildStamp + ")";
}
