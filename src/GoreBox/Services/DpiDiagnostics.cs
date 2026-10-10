using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Одна строка диагностики: на каком этапе умер домен.</summary>
public sealed class DpiDiagRow
{
    public string Domain = "";
    public string Doh = "";
    public string SystemDns = "";
    public string DnsVerdict = "";
    public string Tcp = "";
    public string Tls = "";
    public string HttpDirect = "";
    public string HttpVia = "";

    /// <summary>Открылся ли домен хотя бы как-нибудь (для итога).</summary>
    public bool OpenedDirect => IsHttp(HttpDirect);
    public bool OpenedVia => IsHttp(HttpVia);

    private static bool IsHttp(string value) =>
        value.Length >= 3 && char.IsDigit(value[0]) && value[..3] != "000";

    public List<string> Lines()
    {
        var list = new List<string> { Domain };

        if (DnsVerdict.Length > 0) list.Add($"   DNS: DoH [{Doh}] · система [{SystemDns}] · {DnsVerdict}");
        if (Tcp.Length > 0) list.Add($"   TCP: {Tcp}");
        if (Tls.Length > 0) list.Add($"   TLS: {Tls}");
        if (HttpDirect.Length > 0 || HttpVia.Length > 0)
            list.Add($"   HTTP: напрямую {HttpDirect} · через обход {HttpVia}");

        return list;
    }
}

/// <summary>
/// Диагностика: по каждому домену пишем, на каком именно этапе соединение умирает —
/// DNS (и тот же ли адрес даёт DoH), TCP, TLS (ServerHello / alert / сброс / таймаут),
/// HTTP. Плюс то же самое по HTTP через обход, чтобы сразу видеть, помогает ли он.
/// </summary>
public static class DpiDiagnostics
{
    public static readonly string[] DefaultDomains =
    {
        "youtube.com",
        "discord.com",
        "instagram.com",
        "x.com",
        "facebook.com",
        "web.telegram.org"
    };

    private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };

    /// <summary>Сколько ждём каждый этап и домен целиком.</summary>
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(12);

    public static async Task<List<DpiDiagRow>> RunAsync(
        IEnumerable<string> domains,
        int enginePort,
        DohMode doh,
        string dohUrl,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var tasks = domains
            .Select(domain => RunOneAsync(domain, enginePort, doh, dohUrl, progress, ct))
            .ToArray();

        var rows = await Task.WhenAll(tasks);
        return rows.ToList();
    }

    /// <summary>Одна строка итога: сколько доменов открылось так и сяк.</summary>
    public static string Summary(List<DpiDiagRow> rows)
    {
        var direct = rows.Count(r => r.OpenedDirect);
        var via = rows.Count(r => r.OpenedVia);
        var anywhere = rows.Count(r => r.OpenedDirect || r.OpenedVia);
        var dns = rows.Count(r => r.DnsVerdict.Contains("различаются", StringComparison.OrdinalIgnoreCase));

        return $"итог: открывается {anywhere} из {rows.Count} "
               + $"(напрямую {direct}, через обход {via})"
               + (dns > 0 ? $"; у {dns} домен(ов) DoH и системный DNS дают разные адреса" : "");
    }

    private static async Task<DpiDiagRow> RunOneAsync(
        string domain, int enginePort, DohMode doh, string dohUrl,
        Action<string>? progress, CancellationToken ct)
    {
        var row = new DpiDiagRow { Domain = domain };
        progress?.Invoke(domain);

        // ------------------------------------------------------------------- DNS
        using var dnsBudget = WithTimeout(ct, DnsTimeout);

        var system = await Safe(DnsResolver.ResolveSystemAsync(domain, dnsBudget.Token));
        row.SystemDns = system?.Text ?? "ошибка";

        DnsLookup? dohLookup = null;
        if (doh == DohMode.Off)
        {
            row.Doh = "выключен";
            row.DnsVerdict = "DoH выключен — адреса берём у провайдера";
        }
        else
        {
            dohLookup = await Safe(DnsResolver.ResolveDohAsync(domain, doh, dohUrl, dnsBudget.Token));
            row.Doh = dohLookup is null ? "DoH недоступен" : dohLookup.Text;
            row.DnsVerdict = Compare(system, dohLookup);
        }

        var lookup = dohLookup is { Ok: true } ? dohLookup : system;
        var addresses = lookup?.InConnectOrder ?? new List<IPAddress>();

        if (addresses.Count == 0)
        {
            row.Tcp = "некуда подключаться — DNS не дал адресов";
            return row;
        }

        // ------------------------------------------------------------------- TCP
        IPAddress? connected = null;
        var reasons = new List<string>();

        foreach (var address in addresses.Take(3))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var budget = WithTimeout(ct, ConnectTimeout);
                await client.ConnectAsync(address, 443, budget.Token);
                connected = address;
                row.Tcp = $"{address}: ok {sw.ElapsedMilliseconds} мс";
                break;
            }
            catch (Exception ex)
            {
                reasons.Add($"{address}: {Short(ex)}");
            }
        }

        if (connected is null)
        {
            row.Tcp = string.Join("; ", reasons);
            row.Tls = "—";
            row.HttpDirect = "—";
            row.HttpVia = enginePort > 0 ? "—" : "обход выключен";
            return row;
        }

        // ------------------------------------------------------------------- TLS
        row.Tls = await TlsStageAsync(connected, domain, ct);

        // ------------------------------------------------------------------ HTTP
        row.HttpDirect = await HttpStageAsync(domain, addresses, 0, ct);
        row.HttpVia = enginePort > 0
            ? await HttpStageAsync(domain, addresses, enginePort, ct)
            : "обход выключен";

        return row;
    }

    /// <summary>
    /// Этап TLS: свой ClientHello и смотрим, что пришло — ServerHello, alert с кодом,
    /// сброс или тишина. Именно здесь видно, режет ли DPI по содержимому.
    /// </summary>
    private static async Task<string> TlsStageAsync(IPAddress address, string host, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var budget = WithTimeout(ct, TlsTimeout);
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, 443, budget.Token);

            await client.GetStream().WriteAsync(DpiTester.BuildProbeHello(host), budget.Token);
            await client.GetStream().FlushAsync(budget.Token);

            var head = new byte[7];
            var read = 0;
            while (read < head.Length)
            {
                var n = await client.GetStream().ReadAsync(head.AsMemory(read, head.Length - read), budget.Token);
                if (n <= 0) break;
                read += n;
                if (read >= 5 && (head[0] == 0x16 || head[0] == 0x15)) break;
            }

            if (read == 0) return $"соединение закрыто, не прочитано ни байта ({sw.ElapsedMilliseconds} мс)";
            if (read < 5) return $"пришло {read} байт, это не TLS";

            if (head[0] == 0x16) return $"ServerHello через {sw.ElapsedMilliseconds} мс";

            if (head[0] == 0x15)
            {
                var code = read >= 7 ? head[6] : -1;
                return $"alert {code} через {sw.ElapsedMilliseconds} мс";
            }

            return $"не TLS, первый байт {head[0]:X2}";
        }
        catch (Exception ex)
        {
            return Short(ex) + $" ({sw.ElapsedMilliseconds} мс)";
        }
    }

    /// <summary>
    /// Этап HTTP: настоящее рукопожатие (SslStream) и GET /. Если сайт отвечает кодом,
    /// значит до него дошли пакеты — остальное уже не наша забота.
    /// </summary>
    private static async Task<string> HttpStageAsync(
        string domain, List<IPAddress> addresses, int enginePort, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TcpClient? client = null;

        try
        {
            using var budget = WithTimeout(ct, HttpTimeout);

            if (enginePort > 0)
            {
                client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, enginePort, budget.Token);

                var connect = Encoding.ASCII.GetBytes($"CONNECT {domain}:443 HTTP/1.1\r\nHost: {domain}:443\r\n\r\n");
                await client.GetStream().WriteAsync(connect, budget.Token);
                await client.GetStream().FlushAsync(budget.Token);

                var reply = Encoding.ASCII.GetString(await ReadUntilAsync(client.GetStream(), budget.Token));
                if (!reply.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !reply.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                    return "обход не открыл туннель: " + reply.Split("\r\n")[0];
            }
            else
            {
                foreach (var address in addresses.Take(3))
                {
                    try
                    {
                        client = new TcpClient(address.AddressFamily);
                        await client.ConnectAsync(address, 443, budget.Token);
                        break;
                    }
                    catch
                    {
                        Close(client);
                        client = null;
                    }
                }

                if (client is null) return "не подключились";
            }

            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = domain }, budget.Token);

            var request = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {domain}\r\nUser-Agent: Mozilla/5.0\r\nAccept: */*\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(request, budget.Token);
            await ssl.FlushAsync(budget.Token);

            var buffer = new byte[4096];
            var read = await ssl.ReadAsync(buffer.AsMemory(), budget.Token);
            if (read <= 0) return "сервер молчит";

            var head = Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 512));
            var line = head.Split("\r\n")[0];
            var parts = line.Split(' ');

            if (parts.Length >= 2 && parts[1].Length == 3 && int.TryParse(parts[1], out _))
            {
                var redirect = "";
                if (parts[1][0] == '3')
                {
                    var at = head.IndexOf("Location:", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var target = head[(at + 9)..].Split("\r\n")[0].Trim();
                        redirect = " → " + (target.Length > 40 ? target[..40] : target);
                    }
                }

                return $"{parts[1]} ({sw.ElapsedMilliseconds} мс){redirect}";
            }

            return "в ответе не HTTP: " + (line.Length > 40 ? line[..40] : line);
        }
        catch (Exception ex)
        {
            return Short(ex) + $" ({sw.ElapsedMilliseconds} мс)";
        }
        finally
        {
            Close(client);
        }
    }

    // ------------------------------------------------------------------ helpers
    private static string Compare(DnsLookup? system, DnsLookup? doh)
    {
        if (doh is null || !doh.Ok) return "DoH недоступен — работаем на системном DNS";
        if (system is null || !system.Ok) return "системный DNS молчит, DoH дал адреса";

        var a = system.Addresses.Select(x => x.ToString()).OrderBy(x => x).ToList();
        var b = doh.Addresses.Select(x => x.ToString()).OrderBy(x => x).ToList();

        return a.SequenceEqual(b) ? "адреса совпадают" : "адреса РАЗЛИЧАЮТСЯ — системный DNS подменяет ответ";
    }

    /// <summary>Отдельный бюджет времени на этап: источник токена, чтобы его можно было освободить.</summary>
    private static CancellationTokenSource WithTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
        return linked;
    }

    private static async Task<T?> Safe<T>(Task<T> task) where T : class
    {
        try { return await task; }
        catch { return null; }
    }

    private static async Task<byte[]> ReadUntilAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read <= 0) break;
            length += read;
            if (EndsWith(buffer, length, HeadEnd)) break;
        }

        return buffer[..length];
    }

    private static bool EndsWith(byte[] buffer, int length, byte[] pattern)
    {
        if (length < pattern.Length) return false;
        var start = length - pattern.Length;
        for (var i = 0; i < pattern.Length; i++)
            if (buffer[start + i] != pattern[i]) return false;
        return true;
    }

    private static void Close(TcpClient? client)
    {
        if (client is null) return;
        try { client.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Короткое объяснение ошибки: «таймаут», «сброс» и т.п. вместо английского из .NET.</summary>
    private static string Short(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "отказ",
            SocketError.ConnectionReset => "сброс (RST)",
            SocketError.ConnectionAborted => "сброс (RST)",
            SocketError.TimedOut => "таймаут",
            SocketError.HostNotFound => "DNS не знает имени",
            SocketError.TryAgain => "DNS временно не отвечает",
            SocketError.NetworkUnreachable => "сеть недоступна",
            SocketError.HostUnreachable => "хост недоступен",
            _ => "сеть: " + se.SocketErrorCode
        },
        OperationCanceledException => "таймаут",
        IOException io when io.InnerException is SocketException inner => Short(inner),
        AuthenticationException => "TLS не поднялся: " + ShortMessage(ex),
        _ => ShortMessage(ex)
    };

    private static string ShortMessage(Exception ex)
    {
        var text = ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 70 ? text[..70] + "…" : text;
    }
}
