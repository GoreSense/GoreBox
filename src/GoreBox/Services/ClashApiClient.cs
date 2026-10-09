using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace GoreBox.Services;

public sealed record TrafficStats(long Upload, long Download)
{
    public long Total => Upload + Download;
}

/// <summary>Клиент к API sing-box (clash_api) — точный пинг и статистика трафика.</summary>
public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient _http;

    public ClashApiClient()
    {
        var handler = new HttpClientHandler
        {
            Proxy = null,
            UseProxy = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static string Base(int port) => $"http://127.0.0.1:{port}";

    public async Task<bool> PingAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Base(port) + "/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Реальная задержка через прокси-ядро (clash delay test).</summary>
    public async Task<int> MeasureDelayAsync(int port, string proxyTag = "proxy", int timeoutMs = 6000,
        CancellationToken ct = default)
    {
        try
        {
            var url = Uri.EscapeDataString("https://www.gstatic.com/generate_204");
            using var resp = await _http.GetAsync(
                $"{Base(port)}/proxies/{Uri.EscapeDataString(proxyTag)}/delay?timeout={timeoutMs}&url={url}", ct);
            if (!resp.IsSuccessStatusCode) return -1;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var ms))
                return ms;
        }
        catch { /* ignore */ }
        return -1;
    }

    public async Task<TrafficStats?> GetTrafficAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Base(port) + "/connections", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            long up = 0, down = 0;
            if (doc.RootElement.TryGetProperty("uploadTotal", out var u) && u.TryGetInt64(out var uv)) up = uv;
            if (doc.RootElement.TryGetProperty("downloadTotal", out var d) && d.TryGetInt64(out var dv)) down = dv;
            return new TrafficStats(up, down);
        }
        catch
        {
            return null;
        }
    }

    public async Task<int> GetActiveConnectionsAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(Base(port) + "/connections", ct);
            if (!resp.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
                return conns.GetArrayLength();
        }
        catch { /* ignore */ }
        return 0;
    }

    public void Dispose() => _http.Dispose();
}
