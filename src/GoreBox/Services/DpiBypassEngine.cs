using System.Net;
using System.Net.Sockets;
using System.Text;
using GoreBox.Models;

namespace GoreBox.Services;

/// <summary>Локальный вход sing-box, через который движок отдаёт трафик в режиме «через профиль».</summary>
public sealed class DpiUpstream
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; }

    /// <summary>true — SOCKS5, false — HTTP CONNECT.</summary>
    public bool Socks { get; init; }
}

/// <summary>
/// Обход DPI: локальный HTTP/SOCKS-прокси, который режет трафик так, чтобы система
/// глубокого анализа пакетов не видела имя сайта целиком.
///
/// Три приёма (включаются независимо):
/// 1) <see cref="DpiBypassSettings.SniSplit"/> — TCP-разрыв ClientHello посередине имени из SNI
///    (для обычного HTTP — разрыв запроса перед «Host:»);
/// 2) <see cref="DpiBypassSettings.TlsRecordSplit"/> — то же, но с переупаковкой в два TLS-record,
///    чтобы DPI, собирающий запись по длине из заголовка, всё равно не увидел имя;
/// 3) <see cref="DpiBypassSettings.FakeTtl"/> — фейк-пакет: тот же ClientHello уходит с отдельного
///    соединения с маленьким TTL. DPI его видит и «отрабатывает» по нему, а до сайта пакет не доходит.
///
/// Всё делается обычными сокетами — драйверы (WinDivert) и права администратора не нужны.
/// </summary>
public sealed class DpiBypassEngine
{
    /// <summary>Буфер под ClientHello: он бывает больше килобайта (много расширений).</summary>
    private const int HelloBuffer = 16 * 1024;

    /// <summary>Сколько соединение может молчать, прежде чем мы его закроем.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Сколько ждём подключение до одного адреса, прежде чем пробовать следующий.</summary>
    private const int ConnectTimeoutMs = 2500;

    /// <summary>Сколько адресов из ответа DNS пробуем максимум.</summary>
    private const int MaxAddresses = 3;

    /// <summary>SOCKS5: «всё хорошо, туннель открыт».</summary>
    private static readonly byte[] SocksOk = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    /// <summary>SOCKS5: «не смог» (0x05 — general failure).</summary>
    private static readonly byte[] SocksFail = { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };
    private static readonly byte[] CrlfHost = { 13, 10, (byte)'H', (byte)'o', (byte)'s', (byte)'t' };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private DpiBypassSettings _options = new();
    private DpiUpstream? _upstream;
    private int _sessions;
    private long _up;
    private long _down;

    /// <summary>Служебные сообщения движка (подъём, ошибки, режим).</summary>
    public event Action<string>? Logged;

    public bool IsRunning { get; private set; }

    /// <summary>Порт, на котором слушаем (0, если ещё не поднят).</summary>
    public int Port { get; private set; }

    public int SessionCount => Volatile.Read(ref _sessions);

    public long BytesUp => Interlocked.Read(ref _up);

    public long BytesDown => Interlocked.Read(ref _down);

    public async Task StartAsync(DpiBypassSettings options, DpiUpstream? upstream, CancellationToken ct = default)
    {
        if (IsRunning) return;

        _options = options;
        _upstream = upstream is { Port: > 0 } ? upstream : null;

        var listener = new TcpListener(IPAddress.Loopback, options.ListenPort);
        listener.Start();   // порт занят — исключение уходит вызывающему, состояние не меняем

        _listener = listener;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsRunning = true;

        Log($"принимаем 127.0.0.1:{Port} · {StrategiesText()} · выход: {ExitText()}");

        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token));
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        try { _listener?.Stop(); }
        catch { /* ignore */ }

        Log("обход остановлен");

        await Task.Delay(100);   // даём сокету освободить порт, иначе повторный старт упадёт

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        Port = 0;
    }

    public string StrategiesText()
    {
        var parts = new List<string>();
        if (_options.SniSplit) parts.Add("SNI split");
        if (_options.TlsRecordSplit) parts.Add("record split");
        if (_options.FakeTtl)
        {
            var fake = $"fake TTL {_options.FakeTtlValue}";
            if (_options.FakeCount > 1) fake += $" ×{_options.FakeCount}";
            if (_options.FakeAfter) fake += " (после)";
            parts.Add(fake);
        }
        if (_options.Multisplit) parts.Add($"мультисплит {_options.MultisplitSize} байт");
        if (_options.ClampMss) parts.Add($"MSS {_options.MssValue}");
        return parts.Count == 0 ? "без обхода (прямая пересылка)" : string.Join(" · ", parts);
    }

    private string ExitText() =>
        _upstream is null
            ? "напрямую"
            : $"через {_upstream.Host}:{_upstream.Port} ({(_upstream.Socks ? "socks" : "http")})";

    private void Log(string line) => Logged?.Invoke(line);

    // ------------------------------------------------------------------ приём
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;   // слушатель остановлен
            }

            Interlocked.Increment(ref _sessions);
            var token = ct;
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, token); }
                catch (OperationCanceledException) { /* движок останавливают — это не ошибка */ }
                catch (Exception ex)
                {
                    if (ex.InnerException is not OperationCanceledException) Log("соединение: " + ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _sessions);
                    Close(client);
                }
            });
        }
    }

    // -------------------------------------------------------------- соединение
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();

        var head = new byte[8192];
        var headLen = 0;
        try
        {
            headLen = await stream.ReadAsync(head.AsMemory(), ct);
        }
        catch { /* клиент отвалился */ }

        if (headLen <= 0) return;

        string host = "";
        int port = 0;
        byte[]? pending = null;   // для обычного HTTP: переписанный запрос
        var socks = false;

        if (head[0] == 0x05)
        {
            socks = true;
            var handshake = await SocksHandshakeAsync(stream, head, headLen, ct);
            if (!handshake.ok) return;
            host = handshake.host;
            port = handshake.port;
        }
        else
        {
            var (buffer, length) = await ReadHeadAsync(stream, head, headLen, ct);
            var requestLine = Encoding.ASCII.GetString(buffer, 0, Math.Min(length, 4096));
            var eol = requestLine.IndexOf("\r\n", StringComparison.Ordinal);
            var line = eol < 0 ? requestLine : requestLine[..eol];
            var parts = line.Split(' ');
            if (parts.Length < 2) return;

            if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                SplitHostPort(parts[1], out host, out port);
            }
            else
            {
                if (parts.Length < 3 || !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return;
                host = uri.Host;
                port = uri.Port;
                pending = RewriteRequest(buffer, length, uri);
            }
        }

        if (host.Length == 0 || port <= 0) return;

        // К сайту подключаемся ДО ответа клиенту. Тогда о неудаче можно сказать честно
        // (502 для HTTP, 0x05 0x05 для SOCKS5), а не молча рвать соединение, которое
        // клиенту уже представили как рабочее.
        TcpClient target;
        try
        {
            target = await ConnectUpstreamAsync(host, port, ct);
        }
        catch (Exception ex)
        {
            Log($"{host}: не подключились — {ex.Message}");
            await RefuseAsync(stream, socks, ct);
            return;
        }

        try
        {
            await AcceptAsync(stream, socks, pending is null, ct);

            if (pending is not null)
            {
                // обычный HTTP: запрос уже собран, отправляем его (с разрывом по «Host:»)
                await SendFirstAsync(target.GetStream(), pending, pending.Length, ct);
                await PumpAsync(client, target, ct);
                return;
            }

            // Первые байты клиента — ClientHello. Читаем его до конца записи TLS:
            // один ReadAsync может вернуть только начало, и тогда резать нечего.
            var payload = new byte[HelloBuffer];
            var helloLen = 0;
            try { helloLen = await ReadRecordAsync(stream, payload, ct); }
            catch { /* клиент успел закрыться */ }

            if (helloLen <= 0) return;

            SendFakeIfNeeded(host, port, payload, helloLen, before: true, ct);
            await SendFirstAsync(target.GetStream(), payload, helloLen, ct);
            SendFakeIfNeeded(host, port, payload, helloLen, before: false, ct);

            await PumpAsync(client, target, ct);
        }
        finally
        {
            Close(target);
        }
    }

    /// <summary>Отказ клиенту: сайт недоступен, туннель мы не открывали.</summary>
    private static async Task RefuseAsync(NetworkStream stream, bool socks, CancellationToken ct)
    {
        try
        {
            if (socks) await stream.WriteAsync(SocksFail, ct);
            else await WriteAsciiAsync(stream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct);
        }
        catch { /* клиент уже ушёл */ }
    }

    /// <summary>
    /// Согласие клиенту: туннель открыт, соединение до сайта уже есть. Для обычного
    /// HTTP отвечать нечего — клиент ждёт ответ самого сайта.
    /// </summary>
    private static async Task AcceptAsync(NetworkStream stream, bool socks, bool tunnel, CancellationToken ct)
    {
        if (!tunnel) return;

        try
        {
            if (socks) await stream.WriteAsync(SocksOk, ct);
            else await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
            await stream.FlushAsync(ct);
        }
        catch { /* клиент уже ушёл */ }
    }

    /// <summary>
    /// Фейк-пакет, если он включён и сейчас его очередь. Внимание: это ОТДЕЛЬНОЕ
    /// соединение с другим портом-источником, поэтому на состояние настоящего потока
    /// в DPI оно не влияет — приём оставлен только для совместимости со старыми
    /// настройками. По-настоящему рабочий фейк требует подмены пакета (WinDivert).
    /// </summary>
    private void SendFakeIfNeeded(string host, int port, byte[] payload, int len, bool before, CancellationToken ct)
    {
        if (_upstream is not null || !_options.FakeTtl) return;
        if (_options.FakeAfter == before) return;   // «после» ждёт своей очереди, и наоборот

        if (!TlsHello.IsHandshakeRecord(payload, len)) return;

        var recordLen = TlsHello.RecordSize(payload, len);
        if (recordLen <= 0) return;

        _ = SendFakeAsync(host, port, payload, recordLen, ct);
    }

    /// <summary>
    /// Читает ровно одну запись TLS (длина — в её заголовке) либо всё, что есть, если
    /// это не TLS. Один вызов ReadAsync мог вернуть половину ClientHello, и тогда
    /// разрыв применялся к обрубку: DPI видел имя целиком в следующем сегменте.
    /// </summary>
    private static async Task<int> ReadRecordAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;

        while (read < TlsHello.RecordHeader)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n <= 0) return read;
            read += n;
        }

        var size = TlsHello.RecordSize(buffer, read);
        while (size > 0 && read < size && read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n <= 0) break;
            read += n;
            size = TlsHello.RecordSize(buffer, read);
        }

        return read;
    }
    // ------------------------------------------------------------------ SOCKS5
    /// <summary>
    /// Приветствие SOCKS5. Возвращает признак успеха и адрес назначения:
    /// параметры out в асинхронном методе запрещены (CS1988), поэтому кортеж.
    /// </summary>
    private async Task<(bool ok, string host, int port)> SocksHandshakeAsync(
        NetworkStream stream, byte[] head, int headLen, CancellationToken ct)
    {
        var host = "";
        var port = 0;

        if (headLen < 2) return (false, host, port);
        var methods = head[1];
        var need = 2 + methods;
        if (headLen < need)
        {
            if (!await ReadExactAsync(stream, head, headLen, need - headLen, ct)) return (false, host, port);
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        var request = new byte[4];
        if (!await ReadExactAsync(stream, request, 0, 4, ct)) return (false, host, port);
        if (request[0] != 0x05 || request[1] != 0x01) return (false, host, port);   // только CONNECT

        var atyp = request[3];
        if (atyp == 0x01)
        {
            var ipv4 = new byte[4];
            if (!await ReadExactAsync(stream, ipv4, 0, 4, ct)) return (false, host, port);
            host = new IPAddress(ipv4).ToString();
        }
        else if (atyp == 0x03)
        {
            var size = new byte[1];
            if (!await ReadExactAsync(stream, size, 0, 1, ct)) return (false, host, port);
            var name = new byte[size[0]];
            if (!await ReadExactAsync(stream, name, 0, name.Length, ct)) return (false, host, port);
            host = Encoding.ASCII.GetString(name);
        }
        else if (atyp == 0x04)
        {
            var ipv6 = new byte[16];
            if (!await ReadExactAsync(stream, ipv6, 0, 16, ct)) return (false, host, port);
            host = new IPAddress(ipv6).ToString();
        }
        else
        {
            return (false, host, port);
        }

        var portBytes = new byte[2];
        if (!await ReadExactAsync(stream, portBytes, 0, 2, ct)) return (false, host, port);
        port = (portBytes[0] << 8) | portBytes[1];

        // ответ об успехе не шлём: сначала реально подключаемся к сайту (см. HandleAsync)
        return (true, host, port);
    }

    // --------------------------------------------------------------- подключения
    /// <summary>Куда идти: напрямую к сайту или в локальный вход ядра (режим «через профиль»).</summary>
    private async Task<TcpClient> ConnectUpstreamAsync(string host, int port, CancellationToken ct) =>
        _upstream is null
            ? await ConnectDirectAsync(host, port, ct)
            : await ConnectViaUpstreamAsync(host, port, ct);

    /// <summary>
    /// Прямое подключение. Адреса берём по настройкам (DoH или системный DNS) и пробуем
    /// не один, а все по очереди: провайдер может не пускать до части адресов сайта,
    /// и брать первый из ответа — значит терять рабочие варианты.
    /// </summary>
    private async Task<TcpClient> ConnectDirectAsync(string host, int port, CancellationToken ct)
    {
        var lookup = await DnsResolver.ResolveAsync(host, _options.Doh, _options.DohUrl, ct);
        if (lookup.Addresses.Count == 0) throw new IOException("DNS не дал адрес: " + host);

        Exception? last = null;

        foreach (var address in lookup.InConnectOrder.Take(MaxAddresses))
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                if (_options.ClampMss) SetMss(client.Client, _options.MssValue);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeoutMs);
                await client.ConnectAsync(address, port, timeout.Token);
                client.NoDelay = true;
                return client;
            }
            catch (Exception ex)
            {
                last = ex;
                Close(client);
            }
        }

        throw new IOException($"{host}: не подключились ни к одному из адресов — {last?.Message}");
    }

    private async Task<TcpClient> ConnectViaUpstreamAsync(string host, int port, CancellationToken ct)
    {
        var up = _upstream!;
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(up.Host, up.Port, timeout.Token);
        client.NoDelay = true;

        var stream = client.GetStream();
        if (up.Socks)
        {
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var greeting = new byte[2];
            if (!await ReadExactAsync(stream, greeting, 0, 2, ct) || greeting[1] != 0x00)
                throw new IOException("вход ядра отказал в SOCKS5");

            var request = new List<byte> { 0x05, 0x01, 0x00 };
            if (IPAddress.TryParse(host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                request.Add(bytes.Length == 4 ? (byte)0x01 : (byte)0x04);
                request.AddRange(bytes);
            }
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                request.Add(0x03);
                request.Add((byte)name.Length);
                request.AddRange(name);
            }

            request.Add((byte)(port >> 8));
            request.Add((byte)(port & 0xFF));
            await stream.WriteAsync(request.ToArray(), ct);
            await stream.FlushAsync(ct);

            var reply = new byte[4];
            if (!await ReadExactAsync(stream, reply, 0, 4, ct) || reply[1] != 0x00)
                throw new IOException("вход ядра отклонил CONNECT");

            var rest = reply[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadLengthAsync(stream, ct),
                _ => -1
            };
            if (rest < 0) throw new IOException("непонятный ответ входа ядра");
            if (rest > 0 && !await ReadExactAsync(stream, new byte[rest], 0, rest, ct)) return client;
            var tail = new byte[2];
            await ReadExactAsync(stream, tail, 0, 2, ct);
            return client;
        }

        await WriteAsciiAsync(stream, $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n", ct);
        await stream.FlushAsync(ct);

        var (buffer, length) = await ReadHeadAsync(stream, new byte[2048], 0, ct);
        var answer = Encoding.ASCII.GetString(buffer, 0, length);
        if (!answer.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !answer.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            throw new IOException("вход ядра отклонил CONNECT: " + answer.Split("\r\n")[0]);

        return client;
    }

    /// <summary>Длина доменного имени в SOCKS-ответе (байт после ATYP).</summary>
    private static async Task<int> ReadLengthAsync(NetworkStream stream, CancellationToken ct)
    {
        var size = new byte[1];
        return await ReadExactAsync(stream, size, 0, 1, ct) ? size[0] : -1;
    }

    // ------------------------------------------------------------ десинхронизация
    /// <summary>
    /// Отправить первые байты соединения: ClientHello режем по SNI и (или) переупаковываем
    /// в две записи. Фейк-пакет отправляется отдельно — см. <see cref="SendFakeAsync"/>.
    /// </summary>
    private async Task SendFirstAsync(Stream target, byte[] data, int len, CancellationToken ct)
    {
        if (len <= 0) return;

        // В режиме «через профиль» трафик уходит в локальный вход ядра: десинхронизация
        // была бы применена к туннелю, а не к соединению с сайтом, и — главное — фейк-пакет
        // ушёл бы напрямую, минуя VPN. Здесь просто пересылаем как есть.
        if (_upstream is not null)
        {
            await target.WriteAsync(data.AsMemory(0, len), ct);
            await target.FlushAsync(ct);
            return;
        }

        // мультисплит: ClientHello уходит множеством мелких кусков с паузой — самый
        // мелкий уровень дробления, имеет смысл когда разрыв на две части не помогает
        if (_options.Multisplit && TlsHello.IsHandshakeRecord(data, len))
        {
            var recordLen = TlsHello.RecordSize(data, len);
            if (recordLen > 0)
            {
                await SendManyAsync(target, data, recordLen, Math.Clamp(_options.MultisplitSize, 1, 512), ct);

                // в первом чанке могло прийти больше одной записи — остаток шлём как есть
                if (len > recordLen) await target.WriteAsync(data.AsMemory(recordLen, len - recordLen), ct);
                await target.FlushAsync(ct);
                return;
            }
        }

        if (_options.SniSplit || _options.TlsRecordSplit)
        {
            if (TlsHello.IsHandshakeRecord(data, len))
            {
                var recordLen = TlsHello.RecordSize(data, len);
                var cut = recordLen > 0 ? TlsHello.SplitPosition(data, recordLen, _options.SniSplit) : -1;
                if (cut > 0)
                {
                    if (_options.TlsRecordSplit &&
                        TlsHello.TrySplitRecords(data, recordLen, cut, out var rec1, out var rec2))
                    {
                        await SendTwoAsync(target, rec1, rec2, ct);
                    }
                    else
                    {
                        await SendTwoAsync(target, data.AsMemory(0, cut), data.AsMemory(cut, recordLen - cut), ct);
                    }

                    // в первом чанке могло прийти больше одной записи — остаток шлём как есть
                    if (len > recordLen) await target.WriteAsync(data.AsMemory(recordLen, len - recordLen), ct);
                    await target.FlushAsync(ct);
                    return;
                }
            }
            else if (_options.SniSplit)
            {
                // обычный HTTP: режем запрос прямо перед двоеточием в «Host:»
                var colon = HttpHostSplit(data, len);
                if (colon > 0)
                {
                    await SendTwoAsync(target, data.AsMemory(0, colon), data.AsMemory(colon, len - colon), ct);
                    await target.FlushAsync(ct);
                    return;
                }
            }
        }

        await target.WriteAsync(data.AsMemory(0, len), ct);
        await target.FlushAsync(ct);
    }

    /// <summary>
    /// Фейк-пакет: тот же ClientHello, но с отдельного соединения и с TTL, которого хватит
    /// только до DPI. Рукопожатие делаем с обычным TTL (иначе соединение не поднимется),
    /// а TTL снижаем перед отправкой данных — сервер их не получит, а DPI увидит.
    /// </summary>
    private async Task SendFakeAsync(string host, int port, byte[] payload, int len, CancellationToken ct)
    {
        var count = Math.Clamp(_options.FakeCount, 1, 5);
        var tasks = new List<Task>(count);

        for (var i = 0; i < count; i++)
        {
            tasks.Add(SendOneFakeAsync(host, port, payload, len, ct));
            if (i + 1 < count)
            {
                try { await Task.Delay(20, CancellationToken.None); }
                catch { /* ignore */ }
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task SendOneFakeAsync(string host, int port, byte[] payload, int len, CancellationToken ct)
    {
        try
        {
            var lookup = await DnsResolver.ResolveAsync(host, _options.Doh, _options.DohUrl, ct);
            var address = lookup.InConnectOrder.FirstOrDefault();
            if (address is null) return;

            using var fake = new TcpClient(address.AddressFamily);
            fake.NoDelay = true;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await fake.ConnectAsync(address, port, timeout.Token);

            SetTtl(fake.Client, _options.FakeTtlValue);

            var stream = fake.GetStream();
            await stream.WriteAsync(payload.AsMemory(0, len), timeout.Token);
            await stream.FlushAsync(timeout.Token);

            // закрываемся со сбросом: сервер тут же выбросит незавершённую передачу
            fake.Client.LingerState = new LingerOption(true, 0);
        }
        catch
        {
            /* фейк — лучшая попытка; неудача ничего не ломает */
        }
    }

    /// <summary>
    /// TTL сокета. Число 4 вместо имени перечисления: в Windows это IP_TTL (IPPROTO_IP)
    /// и IPV6_UNICAST_HOPS (IPPROTO_IPV6); готового имени в .NET под оба случая нет.
    /// </summary>
    private static void SetTtl(Socket socket, int ttl)
    {
        try
        {
            var level = socket.AddressFamily == AddressFamily.InterNetworkV6
                ? SocketOptionLevel.IPv6
                : SocketOptionLevel.IP;
            socket.SetSocketOption(level, (SocketOptionName)IpTtl, Math.Clamp(ttl, 1, 255));
        }
        catch
        {
            /* не дали выставить TTL — фейк уйдёт с обычным, обход просто не сработает */
        }
    }

    /// <summary>
    /// Дробление по MSS: принудительный TCP_MAXSEG, чтобы система сама разбила
    /// ClientHello на мелкие сегменты. Ставится до соединения — иначе не действует.
    /// </summary>
    private static void SetMss(Socket socket, int mss)
    {
        try
        {
            // TCP_MAXSEG = 4 на уровне IPPROTO_TCP
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)TcpMaxSeg, Math.Clamp(mss, 64, 1500));
        }
        catch
        {
            /* не вышло — работаем без дробления по MSS */
        }
    }

    private const int IpTtl = 4;      // IP_TTL / IPV6_UNICAST_HOPS (Windows)
    private const int TcpMaxSeg = 4;  // TCP_MAXSEG (Windows)

    private async Task SendTwoAsync(Stream target, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second, CancellationToken ct)
    {
        await target.WriteAsync(first, ct);
        await target.FlushAsync(ct);

        if (_options.SplitDelayMs > 0)
        {
            try { await Task.Delay(_options.SplitDelayMs, ct); }
            catch { /* отмена — соединение всё равно закрывается */ }
        }

        await target.WriteAsync(second, ct);
        await target.FlushAsync(ct);
    }

    /// <summary>
    /// Множественный разрыв: тот же ClientHello, но кусками по <paramref name="chunk"/> байт
    /// с паузой между ними. Сокет с NoDelay отправляет каждый кусок отдельным сегментом,
    /// поэтому DPI видит имя сайта по частям и собрать его не успевает.
    /// </summary>
    private async Task SendManyAsync(Stream target, byte[] data, int len, int chunk, CancellationToken ct)
    {
        var sent = 0;

        while (sent < len)
        {
            var size = Math.Min(chunk, len - sent);
            await target.WriteAsync(data.AsMemory(sent, size), ct);
            await target.FlushAsync(ct);
            sent += size;

            if (sent >= len || _options.SplitDelayMs <= 0) continue;

            try { await Task.Delay(_options.SplitDelayMs, ct); }
            catch { /* отмена — соединение всё равно закрывается */ }
        }
    }

    // ------------------------------------------------------------------- перекачка
    /// <summary>Перекачка в обе стороны: ждём, пока замолчит любая из них.</summary>
    private async Task PumpAsync(TcpClient client, TcpClient target, CancellationToken ct)
    {
        var up = PumpOneAsync(client, target, true, ct);
        var down = PumpOneAsync(target, client, false, ct);
        await Task.WhenAny(up, down);
        await Task.WhenAll(up, down);
    }

    private async Task PumpOneAsync(TcpClient from, TcpClient to, bool upload, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await from.GetStream().ReadAsync(buffer.AsMemory(), ct).AsTask().WaitAsync(IdleTimeout, ct);
                }
                catch (TimeoutException)
                {
                    break;   // соединение молчит слишком долго
                }

                if (read <= 0) break;

                await to.GetStream().WriteAsync(buffer.AsMemory(0, read), ct);
                if (upload) Interlocked.Add(ref _up, read);
                else Interlocked.Add(ref _down, read);
            }
        }
        catch
        {
            /* соединение закрыто — нормальный конец перекачки */
        }
        finally
        {
            // half-close: без этого вторая сторона ждёт данных, которых уже не будет,
            // и соединение висит до таймаута
            try { to.Client.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------- helpers
    private static void Close(TcpClient client)
    {
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { client.Close(); } catch { /* ignore */ }
        try { client.Dispose(); } catch { /* ignore */ }
    }

    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var direct)) return direct;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0) return null;
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        }
        catch
        {
            return null;
        }
    }

    private static void SplitHostPort(string authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(authority)) return;

        var text = authority.Trim();
        var colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            host = text;
            port = 80;
            return;
        }

        host = text[..colon].Trim('[', ']');
        port = int.TryParse(text[(colon + 1)..], out var parsed) ? parsed : 0;
    }

    private static int HttpHostSplit(byte[] data, int len)
    {
        var idx = IndexOf(data, len, CrlfHost);
        if (idx < 0) return -1;

        var colon = idx + CrlfHost.Length;   // прямо после «Host» должно быть двоеточие
        return colon < len && data[colon] == (byte)':' ? colon : -1;
    }

    private static int IndexOf(byte[] data, int len, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= len; i++)
        {
            var j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    /// <summary>Дочитывает HTTP-заголовки до пустой строки.</summary>
    private static async Task<(byte[] buffer, int length)> ReadHeadAsync(
        NetworkStream stream, byte[] buffer, int length, CancellationToken ct)
    {
        while (true)
        {
            if (IndexOf(buffer, length, HeadEnd) >= 0) return (buffer, length);
            if (length >= 65536) return (buffer, length);

            if (length == buffer.Length)
            {
                var bigger = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                buffer = bigger;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read <= 0) return (buffer, length);
            length += read;
        }
    }

    /// <summary>Абсолютный URI в запросе → origin-form, служебные Proxy-* заголовки долой.</summary>
    private static byte[] RewriteRequest(byte[] data, int length, Uri uri)
    {
        var text = Encoding.ASCII.GetString(data, 0, length);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) end = text.Length;

        var lines = new List<string>(text[..end].Split("\r\n"));

        var parts = lines[0].Split(' ');
        if (parts.Length >= 3) lines[0] = $"{parts[0]} {uri.PathAndQuery} {parts[2]}";

        var hasHost = false;
        for (var i = 1; i < lines.Count; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;

            var name = lines[i][..colon];
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                i--;
            }
        }

        if (!hasHost) lines.Insert(1, "Host: " + uri.Authority);

        // keep-alive выключаем: иначе следующие запросы по тому же соединению ушли бы
        // без разрыва, и DPI увидел бы имя сайта целиком
        for (var i = lines.Count - 1; i >= 1; i--)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            if (lines[i][..colon].Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                lines[i][..colon].Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                lines[i][..colon].Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i);
        }
        lines.Insert(1, "Connection: close");

        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length, ct);
    }
}
