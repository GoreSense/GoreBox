using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using GoreBox.Protos;

namespace GoreBox.Services;

/// <summary>
/// Клиент gRPC-режима ядра из исходников nekobox (nekoray): бинарник sing-box, запущенный как
/// <c>sing-box nekobox --token … --port …</c>, принимает конфиг и управляет инстансом по протоколу
/// libcore.proto — так работает десктопный NekoBox. Аутентификация — ровно одно значение заголовка
/// <c>nekoray_auth</c> с токеном (см. core/nekoray/go/grpc_server/auth/auth.go).
///
/// Ядро слушает 127.0.0.1 без TLS (h2c); если конфиг собран корректно, clash API из него
/// поднимается внутри инстанса и остаётся доступным по ClashApiPort, как при обычном run -c.
///
/// Режим не обязателен: обычные бинарники sing-box и расширенные форки его не имеют — CoreService
/// в этом случае продолжает работать через <c>run -c</c>.
/// </summary>
public sealed class NekoCoreRpcClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly LibcoreService.LibcoreServiceClient _client;
    private readonly Metadata _headers;

    static NekoCoreRpcClient()
    {
        // HTTP/2 без TLS (h2c): ядро слушает cleartext. На .NET 5 требовался явный switch,
        // на более новых версиях он безвреден.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    /// <param name="address">Адрес gRPC-сервера ядра, например http://127.0.0.1:19810.</param>
    /// <param name="token">Токен из -token; передаётся в заголовке nekoray_auth.</param>
    public NekoCoreRpcClient(string address, string token)
    {
        _channel = GrpcChannel.ForAddress(address);
        _client = new LibcoreService.LibcoreServiceClient(_channel);
        // Ровно одно значение на ключ — иначе auth.go отвечает Unauthenticated.
        _headers = new Metadata { { "nekoray_auth", token } };
    }

    /// <summary>
    /// Запуск инстанса из JSON-конфига (Start → boxmain.Create: разбор, instance.Start(),
    /// логи перенаправляются в neko.log). <paramref name="statsOutbounds"/> включает
    /// статистику трафика по тегам (QueryStats).
    /// </summary>
    /// <returns>Текст ошибки ядра или null, если инстанс запущен.</returns>
    public async Task<string?> StartAsync(string configJson, IEnumerable<string>? statsOutbounds = null,
        CancellationToken ct = default)
    {
        var req = new LoadConfigReq
        {
            CoreConfig = configJson,
            EnableNekorayConnections = false
        };
        if (statsOutbounds is not null)
            foreach (var tag in statsOutbounds)
                req.StatsOutbounds.Add(tag);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var resp = await _client.StartAsync(req, _headers, deadline: DateTime.UtcNow.AddSeconds(35),
            cancellationToken: cts.Token);
        return string.IsNullOrEmpty(resp.Error) ? null : resp.Error;
    }

    /// <summary>
    /// Остановка инстанса (Stop → cancel + Close). Процесс ядра при этом живёт дальше.
    /// Ждём не дольше 3 секунд: если ядро не ответило, его завершит процесс (см. CoreService).
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await _client.StopAsync(new EmptyReq(), _headers, deadline: DateTime.UtcNow.AddSeconds(4),
                cancellationToken: cts.Token);
        }
        catch (RpcException)
        {
            // Инстанс мог уже завершиться вместе с процессом — остановка не нужна.
        }
    }

    /// <summary>TCP-пинг адреса через ядро (Test/TcpPing): возвращает миллисекунды или -1.</summary>
    public async Task<int> TcpPingAsync(string address, int timeoutMs = 5000, CancellationToken ct = default)
        => await TestAsync(new TestReq
        {
            Mode = TestMode.TcpPing,
            Address = address,
            Timeout = timeoutMs
        }, timeoutMs + 5000, ct);

    /// <summary>UrlTest через ядро: миллисекунды до <paramref name="url"/> или -1.</summary>
    public async Task<int> UrlTestAsync(string inbound = "proxy-in", string url = "https://www.gstatic.com/generate_204",
        int timeoutMs = 10000, CancellationToken ct = default)
        => await TestAsync(new TestReq
        {
            Mode = TestMode.UrlTest,
            Inbound = inbound,
            Url = url,
            Timeout = timeoutMs
        }, timeoutMs + 5000, ct);

    private async Task<int> TestAsync(TestReq req, int deadlineMs, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(deadlineMs));
            var resp = await _client.TestAsync(req, _headers,
                deadline: DateTime.UtcNow.AddMilliseconds(deadlineMs), cancellationToken: cts.Token);
            return string.IsNullOrEmpty(resp.Error) ? resp.Ms : -1;
        }
        catch (RpcException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Счётчик трафика outbound'а (QueryStats; включается через stats_outbounds при Start):
    /// <paramref name="direct"/> — "up" или "down".
    /// </summary>
    public async Task<long> QueryStatsAsync(string tag, string direct, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var resp = await _client.QueryStatsAsync(new QueryStatsReq
            {
                Tag = tag,
                Direct = direct
            }, _headers, deadline: DateTime.UtcNow.AddSeconds(8), cancellationToken: cts.Token);
            return resp.Traffic;
        }
        catch (RpcException)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        try { _channel.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>Свободный TCP-порт на loopback.</summary>
internal static class FreePort
{
    public static int Get()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
