using System.IO;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>
/// Хранилище наборов правил (rulesets) и профилей маршрутизации.
/// Наборы — отдельные файлы в %AppData%\GoreBox\routes, их можно передавать между устройствами.
/// </summary>
public sealed class RouteStore
{
    public const string SetAllProxy = "Всё через прокси";
    public const string SetBypassRu = "Обход РФ";
    public const string SetBlockedOnly = "Только заблокированное";
    public const string SetAdBlock = "Блокировка рекламы";

    public List<RouteSet> LoadSets()
    {
        AppPaths.EnsureCreated();
        var sets = new List<RouteSet>();

        foreach (var file in Directory.EnumerateFiles(AppPaths.RoutesDir, "*.json"))
        {
            var set = Json.Read<RouteSet>(file);
            if (set is null) continue;
            set.Rules ??= new List<RouteRule>();
            set.Apps ??= new List<AppRule>();
            set.FileName = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(set.Name)) set.Name = Path.GetFileNameWithoutExtension(file);
            sets.Add(set);
        }

        return sets.OrderByDescending(s => s.BuiltIn).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string PathFor(RouteSet set)
    {
        var file = string.IsNullOrWhiteSpace(set.FileName) ? AppPaths.Slug(set.Name) + ".json" : set.FileName;
        return Path.Combine(AppPaths.RoutesDir, file);
    }

    public void Save(RouteSet set)
    {
        AppPaths.EnsureCreated();
        set.UpdatedAt = DateTime.UtcNow;

        // если переименовали — переносим содержимое в новый файл
        var desired = AppPaths.Slug(set.Name) + ".json";
        var oldPath = string.IsNullOrWhiteSpace(set.FileName) ? null : Path.Combine(AppPaths.RoutesDir, set.FileName);
        var newPath = Path.Combine(AppPaths.RoutesDir, desired);

        if (set.BuiltIn && oldPath is not null) newPath = oldPath;

        Json.Write(newPath, set);
        if (oldPath is not null && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
        {
            try { File.Delete(oldPath); } catch { /* ignore */ }
        }
        set.FileName = Path.GetFileName(newPath);
    }

    public void Delete(RouteSet set)
    {
        try
        {
            var path = PathFor(set);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
        catch { /* ignore */ }
    }

    public RouteSet Duplicate(RouteSet source, string newName)
    {
        var copy = new RouteSet
        {
            Name = newName,
            Description = source.Description,
            BuiltIn = false,
            Rules = source.Rules.Select(r => r.Clone()).ToList(),
            Apps = source.Apps.Select(a => a.Clone()).ToList()
        };
        Save(copy);
        return copy;
    }

    public RouteSet Import(string path)
    {
        var set = Json.Read<RouteSet>(path) ?? throw new InvalidOperationException("Файл не является набором правил");
        set.Id = Guid.NewGuid().ToString("N");
        set.BuiltIn = false;
        set.FileName = "";
        set.Name = MakeUniqueName(set.Name);
        Save(set);
        return set;
    }

    public void Export(RouteSet set, string path) => Json.Write(path, set);

    private string MakeUniqueName(string name)
    {
        var existing = LoadSets().Select(s => s.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (var i = 2; i < 500; i++)
            if (!existing.Contains($"{name} ({i})")) return $"{name} ({i})";
        return name;
    }

    // ------------------------------------------------------------------ профили
    public List<RouteProfile> LoadProfiles()
    {
        var list = Json.Read<List<RouteProfile>>(AppPaths.RouteProfilesFile);
        if (list is null || list.Count == 0)
        {
            list = new List<RouteProfile>
            {
                new()
                {
                    Name = "По умолчанию",
                    ListenAddress = "127.0.0.1",
                    ListenPort = 2080,
                    Mode = ListenMode.Mixed,
                    UseSystemProxy = true,
                    TunEnabled = false,
                    RouteSetId = BuiltInSets().First(s => s.Name == SetBypassRu).Id
                }
            };
            Json.Write(AppPaths.RouteProfilesFile, list);
        }
        return list;
    }

    public void SaveProfiles(List<RouteProfile> profiles) => Json.Write(AppPaths.RouteProfilesFile, profiles);

    // ---------------------------------------------------------------- встроенные
    private const string SeedMarker = "routes-seeded.json";

    public void SeedBuiltInsIfNeeded()
    {
        AppPaths.EnsureCreated();
        var markerPath = Path.Combine(AppPaths.DataRoot, SeedMarker);
        var seeded = Json.Read<List<string>>(markerPath) ?? new List<string>();

        foreach (var set in BuiltInSets())
        {
            var path = Path.Combine(AppPaths.RoutesDir, AppPaths.Slug(set.Name) + ".json");
            var alreadySeeded = seeded.Contains(set.Name, StringComparer.CurrentCultureIgnoreCase);
            if (alreadySeeded) continue;   // не воссоздаём то, что пользователь удалил

            if (!File.Exists(path))
            {
                set.FileName = Path.GetFileName(path);
                Json.Write(path, set);
            }
            seeded.Add(set.Name);
        }

        Json.Write(markerPath, seeded);
    }

    /// <summary>Гарантирует наличие активного профиля маршрутизации и корректную ссылку на набор.</summary>
    public void Normalize(List<RouteProfile> profiles, List<RouteSet> sets)
    {
        if (sets.Count == 0) return;
        var valid = sets.Select(s => s.Id).ToHashSet();
        foreach (var p in profiles)
            if (p.RouteSetId is null || !valid.Contains(p.RouteSetId))
                p.RouteSetId = sets.FirstOrDefault(s => s.Name == SetBypassRu)?.Id ?? sets[0].Id;
    }

    public static List<RouteSet> BuiltInSets() => new()
    {
        new RouteSet
        {
            Name = SetAllProxy,
            BuiltIn = true,
            Description = "Никаких исключений: весь трафик идёт через выбранный профиль.",
            Rules = new List<RouteRule>()
        },

        new RouteSet
        {
            Name = SetBypassRu,
            BuiltIn = true,
            Description = "Российские сервисы и локальные адреса — напрямую, всё остальное через прокси.",
            Rules = new List<RouteRule>
            {
                R(@"\.ru$", RouteAction.Direct, MatchKind.Regex, "Все зоны .ru"),
                R(@"\.рф$", RouteAction.Direct, MatchKind.Regex, "Все зоны .рф"),
                R("vk.com", RouteAction.Direct), R("vkvideo.ru", RouteAction.Direct),
                R("ok.ru", RouteAction.Direct), R("mail.ru", RouteAction.Direct),
                R("dzen.ru", RouteAction.Direct), R("yandex.ru", RouteAction.Direct, MatchKind.Keyword),
                R("yandex.net", RouteAction.Direct, MatchKind.Keyword),
                R("ya.ru", RouteAction.Direct), R("sberbank.ru", RouteAction.Direct, MatchKind.Keyword),
                R("sber.ru", RouteAction.Direct), R("tinkoff.ru", RouteAction.Direct),
                R("gosuslugi.ru", RouteAction.Direct), R("nalog.ru", RouteAction.Direct, MatchKind.Keyword),
                R("avito.ru", RouteAction.Direct), R("ozon.ru", RouteAction.Direct),
                R("wildberries.ru", RouteAction.Direct), R("rutube.ru", RouteAction.Direct),
                R("kinopoisk.ru", RouteAction.Direct), R("2gis.ru", RouteAction.Direct),
                R("ria.ru", RouteAction.Direct), R("rbc.ru", RouteAction.Direct),
                R("lenta.ru", RouteAction.Direct), R("habr.com", RouteAction.Direct),
                R("steampowered.com", RouteAction.Direct, MatchKind.Keyword, "Игры обычно быстрее напрямую"),
                R("mail.ru", RouteAction.Direct),
                R(@"\.su$", RouteAction.Direct, MatchKind.Regex)
            }
        },

        new RouteSet
        {
            Name = SetBlockedOnly,
            BuiltIn = true,
            Description = "Через прокси только список сервисов, остальное напрямую.",
            Rules = new List<RouteRule>
            {
                R("youtube.com", RouteAction.Proxy), R("youtu.be", RouteAction.Proxy),
                R("googlevideo.com", RouteAction.Proxy), R("ytimg.com", RouteAction.Proxy),
                R("ggpht.com", RouteAction.Proxy), R("gstatic.com", RouteAction.Proxy),
                R("google.com", RouteAction.Proxy), R("googleapis.com", RouteAction.Proxy),
                R("telegram.org", RouteAction.Proxy), R("t.me", RouteAction.Proxy),
                R("telegram.me", RouteAction.Proxy), R("telegra.ph", RouteAction.Proxy),
                R("tdesktop.com", RouteAction.Proxy, MatchKind.Keyword),
                R("discord.com", RouteAction.Proxy), R("discordapp.com", RouteAction.Proxy),
                R("discord.gg", RouteAction.Proxy), R("discord.media", RouteAction.Proxy),
                R("x.com", RouteAction.Proxy), R("twitter.com", RouteAction.Proxy),
                R("twimg.com", RouteAction.Proxy), R("t.co", RouteAction.Proxy),
                R("facebook.com", RouteAction.Proxy), R("fbcdn.net", RouteAction.Proxy),
                R("instagram.com", RouteAction.Proxy), R("cdninstagram.com", RouteAction.Proxy),
                R("meta.com", RouteAction.Proxy), R("whatsapp.com", RouteAction.Proxy),
                R("whatsapp.net", RouteAction.Proxy),
                R("openai.com", RouteAction.Proxy), R("chatgpt.com", RouteAction.Proxy),
                R("oaistatic.com", RouteAction.Proxy), R("oaiusercontent.com", RouteAction.Proxy),
                R("anthropic.com", RouteAction.Proxy), R("claude.ai", RouteAction.Proxy),
                R("reddit.com", RouteAction.Proxy), R("redd.it", RouteAction.Proxy),
                R("wikipedia.org", RouteAction.Proxy), R("wikimedia.org", RouteAction.Proxy),
                R("medium.com", RouteAction.Proxy), R("substack.com", RouteAction.Proxy),
                R("signal.org", RouteAction.Proxy), R("proton.me", RouteAction.Proxy),
                R("protonmail.com", RouteAction.Proxy),
                R("spotify.com", RouteAction.Proxy), R("scdn.co", RouteAction.Proxy),
                R("meduza.io", RouteAction.Proxy), R("bbc.com", RouteAction.Proxy),
                R("dw.com", RouteAction.Proxy), R("theins.ru", RouteAction.Proxy),
                R("svoboda.org", RouteAction.Proxy, MatchKind.Keyword),
                R("linkedin.com", RouteAction.Proxy), R("licdn.com", RouteAction.Proxy),
                R("patreon.com", RouteAction.Proxy), R("onlyfans.com", RouteAction.Proxy),
                R("pornhub.com", RouteAction.Proxy), R("xvideos.com", RouteAction.Proxy)
            }
        },

        new RouteSet
        {
            Name = SetAdBlock,
            BuiltIn = true,
            Description = "Блокировка рекламных и трекинговых доменов (можно совмещать с другими наборами).",
            Rules = new List<RouteRule>
            {
                R(@"^(.+\.)?(doubleclick|googleadservices|googlesyndication|google-analytics|adservice)\.(net|com|google\.com)$", RouteAction.Block, MatchKind.Regex, "Google Ads"),
                R(@"^(.+\.)?(adnxs|adsrvr|advertising|adform|criteo|taboola|outbrain|rubiconproject|pubmatic|smartadserver|yieldmo)\.", RouteAction.Block, MatchKind.Regex),
                R(@"^(.+\.)?(an|ads|ad|banners)\.yandex\.(ru|net|com)$", RouteAction.Block, MatchKind.Regex, "Яндекс.Реклама"),
                R(@"^(.+\.)?(mc\.yandex\.ru|ads\.vk\.com|ad\.mail\.ru|top-fwz1\.mail\.ru|r\.mail\.ru)$", RouteAction.Block, MatchKind.Regex),
                R(@"^(.+\.)?(sentry\.io|tracker|tracking|metrics|analytics|telemetry)\.", RouteAction.Block, MatchKind.Regex)
            }
        }
    };

    private static RouteRule R(string pattern, RouteAction action, MatchKind kind = MatchKind.Domain, string? note = null)
        => new() { Pattern = pattern, Action = action, Kind = kind, Note = note };
}
