using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Одна dns-запись, какой её вернул сервер.</summary>
public sealed class DnsLookup
{
    public string Host = "";
    public List<IPAddress> Addresses = new();

    /// <summary>Откуда адреса: DoH (какой), системный DNS или кэш.</summary>
    public string Source = "";
    public string Error = "";
    public int TtlSeconds;

    public bool Ok => Addresses.Count > 0;

    public string Text => Addresses.Count == 0
        ? (Error.Length > 0 ? Error : "адресов нет")
        : string.Join(", ", Addresses.Select(a => a.ToString()));

    /// <summary>Адреса в порядке попыток: сначала IPv4 — IPv6 часто есть в ответе, но не ездит.</summary>
    public List<IPAddress> InConnectOrder =>
        Addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Concat(Addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork))
            .ToList();
}

/// <summary>
/// Резолвер, который не зависит от системного DNS провайдера: адреса можно спросить
/// по DoH (DNS поверх HTTPS). Нужен затем, что провайдерский DNS часто подменяет
/// ответ на заблокированных сайтах — и обход пакетов тут уже ни при чём.
/// </summary>
public static class DnsResolver
{
    private const string CloudflareUrl = "https://cloudflare-dns.com/dns-query";
    private const string GoogleUrl = "https://dns.google/dns-query";

    /// <summary>Ниже этого значения кэш не имеет смысла, выше — адрес успевает протухнуть.</summary>
    private const int MinTtlSeconds = 60;
    private const int MaxTtlSeconds = 3600;

    private sealed record CacheEntry(List<IPAddress> Addresses, DateTime ExpiresAt, string Source);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private static HttpClient? _http;

    /// <summary>
    /// Свой клиент: без системного прокси. DoH не должен идти через наш же обход —
    /// иначе движок, чтобы узнать адрес сайта, сначала спросил бы адрес самого DoH.
    /// </summary>
    private static HttpClient Http => _http ??= new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    /// <summary>Адреса с учётом настроек: DoH если включён, иначе системный DNS.</summary>
    public static async Task<DnsLookup> ResolveAsync(string host, DohMode mode, string customUrl, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
            return new DnsLookup { Host = host, Addresses = { literal }, Source = "это адрес" };

        if (Cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return new DnsLookup
            {
                Host = host,
                Addresses = new List<IPAddress>(cached.Addresses),
                Source = cached.Source + " (кэш)",
                TtlSeconds = (int)(cached.ExpiresAt - DateTime.UtcNow).TotalSeconds
            };

        if (mode != DohMode.Off)
        {
            var doh = await ResolveDohAsync(host, mode, customUrl, ct);
            if (doh.Ok) return doh;
        }

        return await ResolveSystemAsync(host, ct);
    }

    /// <summary>Системный DNS — то, что отдаёт провайдер.</summary>
    public static async Task<DnsLookup> ResolveSystemAsync(string host, CancellationToken ct = default)
    {
        var result = new DnsLookup { Host = host, Source = "системный DNS" };
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            result.Addresses.AddRange(addresses);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (result.Ok) Remember(host, result.Addresses, MinTtlSeconds, result.Source);
        return result;
    }

    /// <summary>DoH: DNS через HTTPS, провайдер его не видит и подменить не может.</summary>
    public static async Task<DnsLookup> ResolveDohAsync(string host, DohMode mode, string customUrl, CancellationToken ct = default)
    {
        var result = new DnsLookup { Host = host, Source = "DoH " + Name(mode) };

        var baseUrl = mode switch
        {
            DohMode.Google => GoogleUrl,
            DohMode.Custom => customUrl.Trim(),
            _ => CloudflareUrl
        };

        if (baseUrl.Length == 0)
        {
            result.Error = "не задан свой URL DoH";
            return result;
        }

        try
        {
            // A и AAAA спрашиваем сразу: если IPv6 есть в ответе, но не ездит, соединяться
            // будем по IPv4 — порядок попыток выбирается уже при подключении
            var ipv4 = QueryAsync(baseUrl, host, "A", ct);
            var ipv6 = QueryAsync(baseUrl, host, "AAAA", ct);
            await Task.WhenAll(ipv4, ipv6);

            var a = await ipv4;
            var aaaa = await ipv6;

            result.Addresses.AddRange(a.Addresses);
            result.Addresses.AddRange(aaaa.Addresses);

            var ttls = new[] { a.Ttl, aaaa.Ttl }.Where(t => t > 0).ToList();
            result.TtlSeconds = ttls.Count > 0 ? ttls.Min() : 0;

            if (result.Addresses.Count == 0) result.Error = "DoH не вернул адресов";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (result.Ok) Remember(host, result.Addresses, result.TtlSeconds, result.Source);
        return result;
    }

    private static async Task<(List<IPAddress> Addresses, int Ttl)> QueryAsync(
        string baseUrl, string host, string type, CancellationToken ct)
    {
        var url = baseUrl + (baseUrl.Contains('?') ? "&" : "?") + $"name={Uri.EscapeDataString(host)}&type={type}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("accept", "application/dns-json");

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);

        var list = new List<IPAddress>();
        var ttl = 0;

        if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return (list, ttl);

        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("TTL", out var ttlNode) && ttlNode.TryGetInt32(out var seconds) && seconds > 0)
                ttl = ttl == 0 ? seconds : Math.Min(ttl, seconds);

            if (!answer.TryGetProperty("data", out var data)) continue;
            if (IPAddress.TryParse(data.GetString() ?? "", out var address)) list.Add(address);
        }

        return (list, ttl);
    }

    private static void Remember(string host, List<IPAddress> addresses, int ttlSeconds, string source)
    {
        var ttl = ttlSeconds <= 0 ? MinTtlSeconds : Math.Clamp(ttlSeconds, MinTtlSeconds, MaxTtlSeconds);
        Cache[host] = new CacheEntry(addresses, DateTime.UtcNow.AddSeconds(ttl), source);
    }

    /// <summary>Очистить кэш — например, после смены провайдера или DNS.</summary>
    public static void ClearCache() => Cache.Clear();

    private static string Name(DohMode mode) => mode switch
    {
        DohMode.Cloudflare => "Cloudflare",
        DohMode.Google => "Google",
        DohMode.Custom => "свой",
        _ => "выключен"
    };
}
