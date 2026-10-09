using System.IO;
using GoreBox.Services;

namespace GoreBox.Utils;

public static class AppPaths
{
    private static string? _dataRoot;

    /// <summary>
    /// Корень всех данных приложения: профили, маршруты, настройки, ядро, логи, рантайм и профиль браузера «Панели».
    /// Выбирается при старте (см. <see cref="StorageLocation"/>): по умолчанию — папка «data» рядом с exe.
    /// </summary>
    public static string DataRoot => _dataRoot ??= StorageLocation.LocalDataDir;

    /// <summary>Задаётся один раз при старте, до загрузки любых данных.</summary>
    public static void SetDataRoot(string root) => _dataRoot = root;

    public static string ProfilesDir => Path.Combine(DataRoot, "profiles");
    public static string RoutesDir => Path.Combine(DataRoot, "routes");
    public static string RouteProfilesFile => Path.Combine(DataRoot, "routing-profiles.json");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CoreDir => Path.Combine(DataRoot, "core");
    public static string CoreBinary => Path.Combine(CoreDir, "sing-box.exe");

    /// <summary>Расширенная сборка ядра (sing-box-extended) — нужна для AmneziaWG.</summary>
    public static string CoreExtendedBinary => Path.Combine(CoreDir, "sing-box-extended.exe");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string RuntimeDir => Path.Combine(DataRoot, "run");
    public static string RuntimeConfig => Path.Combine(RuntimeDir, "config.json");
    public static string CacheFile => Path.Combine(RuntimeDir, "cache.db");

    public static string ExecutablePath => Environment.ProcessPath ?? "";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(RoutesDir);
        Directory.CreateDirectory(CoreDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RuntimeDir);
    }

    /// <summary>Имя для безопасного файла из пользовательского названия.</summary>
    public static string Slug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray();
        var slug = new string(chars);
        while (slug.Contains("  ")) slug = slug.Replace("  ", " ");
        return slug.Length > 64 ? slug[..64] : slug;
    }
}
