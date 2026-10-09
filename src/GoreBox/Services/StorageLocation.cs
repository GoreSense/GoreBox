using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Где хранятся данные приложения: профили, маршруты, настройки, ядро, логи, браузер «Панели».</summary>
public enum StorageMode
{
    /// <summary>Локальная папка: %LOCALAPPDATA%\GoreBox.</summary>
    Local,

    /// <summary>Папка «data» рядом с исполняемым файлом (по умолчанию).</summary>
    Exe,

    /// <summary>Папка, которую выбрал пользователь; запоминается.</summary>
    Custom,
}

/// <summary>
/// Выбор места хранения. Лежит в %LOCALAPPDATA%\GoreBox\location.json — файле, который читается
/// до того, как становится известна папка данных.
/// </summary>
public sealed class StorageConfig
{
    public StorageMode Mode { get; set; } = StorageMode.Exe;

    /// <summary>Папка для режима Custom. Сохраняется и при смене режима, чтобы выбор не терялся.</summary>
    public string? CustomPath { get; set; }

    /// <summary>
    /// Папки, из которых нужно скопировать данные при следующем запуске, если в новой папке данных их ещё нет.
    /// Заполняется при смене места хранения и при первом запуске версии с выбором места.
    /// </summary>
    public List<string> PendingMigrationFrom { get; set; } = new();
}

/// <summary>Определение папки данных при старте, перенос данных и смена места хранения.</summary>
public static class StorageLocation
{
    private const string BootstrapFileName = "location.json";
    private const string DataFolderName = "data";

    // эти папки не переносим: run пересоздаётся, logs не нужны, кэши браузера тяжёлые и не хранят данных
    private static readonly string[] SkipTopLevel = { "run", "logs" };

    private static readonly HashSet<string> SkipAnyLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnCache", "Crashpad",
    };

    /// <summary>%LOCALAPPDATA%\GoreBox — доступна всегда, здесь же лежит location.json.</summary>
    public static string LocalDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoreBox");

    /// <summary>Папка «data» рядом с исполняемым файлом.</summary>
    public static string ExeDataDir => Path.Combine(
        Path.GetDirectoryName(AppPaths.ExecutablePath) ?? AppContext.BaseDirectory, DataFolderName);

    /// <summary>Выбор пользователя, как он записан на диск.</summary>
    public static StorageConfig Config { get; private set; } = new();

    /// <summary>Почему выбранная папка не используется (например, нет прав записи), иначе null.</summary>
    public static string? FallbackReason { get; private set; }

    /// <summary>Папка, которая будет использоваться после перезапуска.</summary>
    public static string DesiredRoot => RootFor(Config) ?? LocalDataDir;

    /// <summary>Выбранная папка отличается от текущей и вступит в силу после перезапуска.</summary>
    public static bool IsPending => FallbackReason is null && !SamePath(DesiredRoot, AppPaths.DataRoot);

    /// <summary>
    /// Определить папку данных и выполнить отложенный перенос. Вызывается в начале запуска, до загрузки настроек.
    /// Возвращает текст для пользователя (что перенесли или почему взята другая папка) либо null.
    /// </summary>
    public static string? Initialize()
    {
        string? notice = null;
        try
        {
            var stored = Json.Read<StorageConfig>(BootstrapFile);
            var firstRun = stored is null;
            var config = stored ?? new StorageConfig { Mode = StorageMode.Exe };
            if (firstRun) config.PendingMigrationFrom = LegacyDirs().ToList();

            var desired = RootFor(config) ?? LocalDataDir;
            var effective = desired;
            FallbackReason = null;
            if (!IsWritable(desired))
            {
                effective = LocalDataDir;
                FallbackReason = $"Папка «{desired}» недоступна для записи, поэтому данные хранятся в «{LocalDataDir}».";
                notice = FallbackReason;
            }

            AppPaths.SetDataRoot(effective);
            Config = config;

            if (config.PendingMigrationFrom.Count > 0)
            {
                // переносим только если в новой папке данных ещё ничего нет: существующие файлы не трогаем
                var copied = 0;
                if (!HasData(effective))
                {
                    foreach (var source in config.PendingMigrationFrom)
                        copied += CopyMissing(source, effective);
                }

                config.PendingMigrationFrom = new List<string>();
                if (copied > 0) notice = Join(notice, $"Данные скопированы в «{effective}» (файлов: {copied}).");
                Save();
            }
            else if (firstRun)
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            AppPaths.SetDataRoot(LocalDataDir);
            notice = "Не удалось определить место хранения данных: " + ex.Message;
        }

        return notice;
    }

    /// <summary>
    /// Выбрать новое место хранения. Данные переедут при следующем запуске.
    /// Возвращает текст ошибки либо null, если всё сохранено.
    /// </summary>
    public static string? TryChange(StorageMode mode, string? customPath)
    {
        var next = new StorageConfig
        {
            Mode = mode,
            CustomPath = mode == StorageMode.Custom ? customPath?.Trim() : Config.CustomPath,
        };

        var desired = RootFor(next);
        if (desired is null) return "Не указана папка для данных.";
        if (!IsWritable(desired)) return $"Папка «{desired}» недоступна для записи. Выберите другую.";

        var current = AppPaths.DataRoot;
        if (!SamePath(desired, current))
            next.PendingMigrationFrom = new List<string> { current };

        Config = next;
        Save();
        return null;
    }

    // ------------------------------------------------------------------ служебные

    private static string BootstrapFile => Path.Combine(LocalDataDir, BootstrapFileName);

    /// <summary>Старое место данных (до выбора хранилища): %APPDATA%\GoreBox и %LOCALAPPDATA%\GoreBox.</summary>
    private static IEnumerable<string> LegacyDirs()
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoreBox");
        foreach (var dir in new[] { roaming, LocalDataDir })
        {
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    private static string? RootFor(StorageConfig config) => config.Mode switch
    {
        StorageMode.Exe => ExeDataDir,
        StorageMode.Custom => FullPathOrNull(config.CustomPath),
        _ => LocalDataDir,
    };

    private static string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Save()
    {
        try
        {
            Json.Write(BootstrapFile, Config);
        }
        catch (Exception)
        {
            // не критично: при следующем запуске перенос может повториться, но данные не потеряются
        }
    }

    private static bool HasData(string root) =>
        File.Exists(Path.Combine(root, "settings.json")) ||
        File.Exists(Path.Combine(root, "routing-profiles.json")) ||
        HasJson(Path.Combine(root, "profiles")) ||
        HasJson(Path.Combine(root, "routes"));

    private static bool HasJson(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();

    /// <summary>Скопировать из source в target всё, чего в target ещё нет. Возвращает число скопированных файлов.</summary>
    private static int CopyMissing(string source, string target)
    {
        if (!Directory.Exists(source) || SamePath(source, target) || IsInside(target, source)) return 0;

        var copied = 0;
        CopyTree(source, target, string.Empty, ref copied);
        return copied;
    }

    private static void CopyTree(string sourceRoot, string targetRoot, string relative, ref int copied)
    {
        var sourceDir = Path.Combine(sourceRoot, relative);
        string[] files;
        string[] dirs;
        try
        {
            files = Directory.GetFiles(sourceDir);
            dirs = Directory.GetDirectories(sourceDir);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (relative.Length == 0 && name.Equals(BootstrapFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            var targetFile = Path.Combine(targetRoot, relative, name);
            if (File.Exists(targetFile)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile) ?? targetRoot);
                File.Copy(file, targetFile, overwrite: false);
                copied++;
            }
            catch (Exception)
            {
                // файл занят или места нет — пропускаем, остальное переносим
            }
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (relative.Length == 0 && SkipTopLevel.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (SkipAnyLevel.Contains(name)) continue;
            CopyTree(sourceRoot, targetRoot, Path.Combine(relative, name), ref copied);
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsInside(string child, string parent)
    {
        try
        {
            var c = Path.GetFullPath(child).TrimEnd('\\') + '\\';
            var p = Path.GetFullPath(parent).TrimEnd('\\') + '\\';
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : first + " " + second;
}
