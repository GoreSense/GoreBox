using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Скачивание ядра sing-box (официального или расширенной сборки) с GitHub.</summary>
public static class CoreInstaller
{
    public const string UpstreamRepo = "SagerNet/sing-box";
    public const string ExtendedRepo = "shtorm-7/sing-box-extended";

    /// <summary>
    /// Форк с полной поддержкой AmneziaWG 2.0/3.x — включая random_trailers и disable_cookies,
    /// без которых сервер AWG 3.1 отбрасывает ответы на хендшейк. Релизные сборки под Windows
    /// собираются с тегом <c>with_awg</c> (Makefile.lx → LX_TAGS), а параметры обфускации у него
    /// лежат плоско в эндпоинте <c>wireguard</c>, как в .conf. Рядом с sing-box.exe в архиве лежит
    /// libcronet.dll — его переносит CopySiblings.
    /// </summary>
    public const string AwgRepo = "Leadaxe/sing-box-lx";

    private static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GoreBox/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string RepoFor(CoreFlavor flavor) => flavor == CoreFlavor.SingBoxExtended ? ExtendedRepo : UpstreamRepo;

    /// <summary>Репозиторий, из которого берём архив: явно заданный или стандартный для варианта.</summary>
    private static string RepoName(CoreFlavor flavor, string? repo) =>
        string.IsNullOrWhiteSpace(repo) ? RepoFor(flavor) : repo!;

    public static async Task<string> GetLatestTagAsync(CoreFlavor flavor, CancellationToken ct = default, string? repo = null)
    {
        using var client = Client();
        using var resp = await client.GetAsync($"https://api.github.com/repos/{RepoName(flavor, repo)}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
    }

    /// <summary>Архитектура архива в терминах Go: amd64 / arm64 / 386.</summary>
    private static string GoArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        Architecture.Arm => "armv7",
        _ => "amd64"
    };

    /// <summary>Архив точно для нашей архитектуры (и не «-purego», чтобы не взять урезанную сборку).</summary>
    private static bool IsArchMatch(string assetName, string arch) =>
        assetName.Contains($"-windows-{arch}.zip", StringComparison.OrdinalIgnoreCase) ||
        assetName.Contains($"-windows-{arch}-", StringComparison.OrdinalIgnoreCase);

    public static async Task<(string Url, string Name)> ResolveWindowsAssetAsync(CoreFlavor flavor,
        CancellationToken ct = default, string? repo = null)
    {
        using var client = Client();
        using var resp = await client.GetAsync($"https://api.github.com/repos/{RepoName(flavor, repo)}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("В релизе нет файлов");

        var candidates = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.GetProperty("name").GetString() ?? "",
                Url = a.GetProperty("browser_download_url").GetString() ?? ""
            })
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("Не найден архив для Windows в последнем релизе");

        var arch = GoArch();
        bool Plain(string n) => !n.Contains("purego", StringComparison.OrdinalIgnoreCase);

        var asset = candidates.FirstOrDefault(a => IsArchMatch(a.Name, arch) && Plain(a.Name))
                    ?? candidates.FirstOrDefault(a => IsArchMatch(a.Name, arch))
                    ?? candidates.FirstOrDefault(a => Plain(a.Name))
                    ?? candidates.First();

        return (asset.Url, asset.Name);
    }

    /// <summary>Куда класть ядро: официальная и расширенная сборки живут рядом и не перетирают друг друга.</summary>
    private static string TargetFor(CoreFlavor flavor) =>
        flavor == CoreFlavor.SingBoxExtended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;

    public static async Task<CoreInfo?> InstallLatestAsync(CoreService core, CoreFlavor flavor,
        IProgress<string>? progress = null, CancellationToken ct = default, string? repo = null)
    {
        progress?.Report("Поиск последней версии на GitHub…");
        var (url, name) = await ResolveWindowsAssetAsync(flavor, ct, repo);
        progress?.Report($"Скачивание {name}…");
        return await InstallFromUrlAsync(core, url, progress, ct, flavor);
    }

    /// <summary>
    /// Скачивает ядро с поддержкой AmneziaWG. Сначала форк sing-box-lx — единственный с готовыми
    /// Windows-сборками и AWG 3.x (random_trailers/disable_cookies); если в его релизе не нашлось
    /// архива под нашу архитектуру или GitHub недоступен, берём расширенную сборку shtorm-7 —
    /// она тянет AWG до 3.0, а параметры 3.1 придётся отключать на сервере.
    /// </summary>
    public static async Task<CoreInfo?> InstallAwgCoreAsync(CoreService core,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var info = await InstallLatestAsync(core, CoreFlavor.SingBoxExtended, progress, ct, AwgRepo);
            if (info is { Exists: true }) return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            progress?.Report($"Форк {AwgRepo} недоступен ({ex.Message}), пробую {ExtendedRepo}…");
        }

        return await InstallLatestAsync(core, CoreFlavor.SingBoxExtended, progress, ct, ExtendedRepo);
    }

    /// <summary>
    /// Распаковывает ядро, вшитое в приложение (ничего скачивать не нужно).
    /// </summary>
    public static async Task<CoreInfo?> InstallBundledAsync(CoreService core, CoreFlavor flavor,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var available = flavor == CoreFlavor.SingBoxExtended ? BundledCores.HasExtended : BundledCores.HasOfficial;
        if (!available) return null;

        progress?.Report("Распаковка встроенного ядра…");

        // перезаписываем поверх, чтобы гарантированно восстановить рабочую копию
        var target = TargetFor(flavor);
        try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }

        await BundledCores.EnsureExtractedAsync(flavor, progress, ct);
        return await core.DetectAsync(target, flavor);
    }

    public static async Task<CoreInfo?> InstallFromUrlAsync(CoreService core, string url,
        IProgress<string>? progress = null, CancellationToken ct = default, CoreFlavor? flavor = null)
    {
        AppPaths.EnsureCreated();
        var tempZip = Path.Combine(Path.GetTempPath(), "gorebox-core-" + Guid.NewGuid().ToString("N")[..6] + ".zip");

        using (var client = Client())
        using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tempZip);
            await resp.Content.CopyToAsync(fs, ct);
        }

        progress?.Report("Распаковка…");
        var info = await InstallFromZipAsync(core, tempZip, progress, ct, flavor);
        try { File.Delete(tempZip); } catch { /* ignore */ }
        return info;
    }

    public static async Task<CoreInfo?> InstallFromZipAsync(CoreService core, string zipPath,
        IProgress<string>? progress = null, CancellationToken ct = default, CoreFlavor? flavor = null)
    {
        AppPaths.EnsureCreated();
        var tempDir = Path.Combine(Path.GetTempPath(), "gorebox-core-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir, true);
            var exe = Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Contains("sing-box", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();

            if (exe is null) throw new InvalidOperationException("В архиве не найден исполняемый файл ядра");

            var target = TargetFor(flavor ?? core.Info.Flavor());

            // определяем сборку по имени файла внутри архива / по версии
            if (flavor is null)
            {
                var name = Path.GetFileName(exe);
                var probe = await core.DetectAsync(exe);
                if (probe is { Exists: true, IsExtended: true }) target = AppPaths.CoreExtendedBinary;
                else if (name.Contains("extended", StringComparison.OrdinalIgnoreCase)) target = AppPaths.CoreExtendedBinary;
            }

            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            File.Copy(exe, target, true);

            // Рядом с sing-box.exe в архиве лежит libcronet.dll (сборки «naive»): без него ядро
            // теряет часть функций, поэтому переносим все соседние файлы в каталог ядра.
            CopySiblings(exe, target);

            progress?.Report("Проверка версии ядра…");
            return await core.DetectAsync(target, flavor);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    private static void CopySiblings(string sourceExe, string targetExe)
    {
        try
        {
            var sourceDir = Path.GetDirectoryName(sourceExe);
            var targetDir = Path.GetDirectoryName(targetExe);
            if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(targetDir)) return;

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                if (string.Equals(file, sourceExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true); }
                catch { /* не критично */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Текст для инструкции по ручной установке.</summary>
    public static string ManualHint =>
        $"Скачайте архив ядра с https://github.com/{UpstreamRepo}/releases и положите sing-box.exe в {AppPaths.CoreDir}, " +
        $"а для AmneziaWG — с https://github.com/{AwgRepo}/releases (файл назвать sing-box-extended.exe, " +
        "и положить рядом libcronet.dll из того же архива), либо укажите путь к ядру в настройках.";
}
