using System.IO;
using System.Text.Json;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Файловое хранилище профилей: один профиль = один .json (удобно шарить и бэкапить).</summary>
public sealed class ProfileStore
{
    public List<ProxyProfile> LoadAll()
    {
        AppPaths.EnsureCreated();
        var list = new List<ProxyProfile>();

        foreach (var file in Directory.EnumerateFiles(AppPaths.ProfilesDir, "*.json"))
        {
            var p = Json.Read<ProxyProfile>(file);
            if (p is null) continue;
            p.Node ??= new ProxyNode();
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileNameWithoutExtension(file);
            list.Add(p);
        }

        return list
            .OrderByDescending(p => p.Favorite)
            .ThenBy(p => p.Group)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public string PathFor(ProxyProfile profile) => Path.Combine(AppPaths.ProfilesDir, profile.Id + ".json");

    public void Save(ProxyProfile profile)
    {
        AppPaths.EnsureCreated();
        profile.UpdatedAt = DateTime.UtcNow;
        Json.Write(PathFor(profile), profile);
    }

    public void Delete(ProxyProfile profile)
    {
        var path = PathFor(profile);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
        catch { /* ignore */ }
    }

    /// <summary>Импорт из текста (ссылки, .conf, JSON, список ссылок, base64-подписка).</summary>
    public (List<ProxyProfile> Added, List<string> Errors) Import(string text)
    {
        var result = LinkParser.ParseMany(text);
        var added = new List<ProxyProfile>();

        foreach (var profile in result.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = profile.Node.Server.Length > 0 ? $"{profile.Node.Server}:{profile.Node.Port}" : "Профиль";

            profile.Name = UniqueName(profile.Name, profile);
            Save(profile);
            added.Add(profile);
        }

        return (added, result.Errors);
    }

    private string UniqueName(string name, ProxyProfile self)
    {
        var existing = LoadAll().Where(p => p.Id != self.Id)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains(name)) return name;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{name} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        return name + " " + Guid.NewGuid().ToString("N")[..4];
    }

    public ProxyProfile Duplicate(ProxyProfile source, string newName)
    {
        var copy = source.Clone();
        copy.Name = newName;
        Save(copy);
        return copy;
    }

    /// <summary>Экспорт профиля в виде ссылки/текста.</summary>
    public string Export(ProxyProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.RawJson)) return p.RawJson!;
        if (!string.IsNullOrWhiteSpace(p.Uri)) return p.Uri!;

        return p.Protocol switch
        {
            ProtocolIds.Vless => BuildVlessUri(p),
            ProtocolIds.Trojan => BuildTrojanUri(p),
            ProtocolIds.Shadowsocks => BuildSsUri(p),
            _ => Json.Serialize(p)
        };
    }

    private static string BuildVlessUri(ProxyProfile p)
    {
        var n = p.Node;
        var q = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) q.Add($"{k}={Uri.EscapeDataString(v!)}"); }
        Add("type", n.Network ?? "tcp");
        Add("security", n.Reality ? "reality" : n.Tls ? "tls" : "none");
        Add("sni", n.Sni);
        Add("fp", n.Fingerprint);
        Add("pbk", n.PublicKey);
        Add("sid", n.ShortId);
        Add("flow", n.Flow);
        Add("path", n.Path);
        Add("host", n.Host);
        Add("serviceName", n.ServiceName);
        Add("alpn", n.Alpn);
        return $"vless://{n.Uuid}@{n.Server}:{n.Port}?{string.Join("&", q)}#{Uri.EscapeDataString(p.Name)}";
    }

    private static string BuildTrojanUri(ProxyProfile p)
    {
        var n = p.Node;
        var q = new List<string>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) q.Add($"{k}={Uri.EscapeDataString(v!)}"); }
        Add("type", n.Network ?? "tcp");
        Add("security", n.Tls ? "tls" : "none");
        Add("sni", n.Sni);
        Add("fp", n.Fingerprint);
        Add("path", n.Path);
        Add("host", n.Host);
        Add("alpn", n.Alpn);
        return $"trojan://{Uri.EscapeDataString(n.Password ?? "")}@{n.Server}:{n.Port}?{string.Join("&", q)}#{Uri.EscapeDataString(p.Name)}";
    }

    private static string BuildSsUri(ProxyProfile p)
    {
        var n = p.Node;
        var creds = Text.Base64Url($"{n.Method}:{n.Password}");
        return $"ss://{creds}@{n.Server}:{n.Port}#{Uri.EscapeDataString(p.Name)}";
    }
}
