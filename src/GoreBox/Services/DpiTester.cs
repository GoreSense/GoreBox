using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GoreBox.Models;

namespace GoreBox.Services;

public enum DpiTestVerdict
{
    /// <summary>Блокировка обходится (или её нет вообще).</summary>
    Broken,

    /// <summary>DPI режет, подобрать вариант не удалось.</summary>
    Covered,

    /// <summary>Не работает даже контрольный адрес — интернета нет.</summary>
    NoNetwork
}

public sealed class DpiTestResult
{
    public DpiTestVerdict Verdict { get; init; }

    /// <summary>На ком проверяли.</summary>
    public string Host { get; init; } = "";

    /// <summary>Одна строка для статуса.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Подобранная рабочая комбинация (null — ничего не подошло).</summary>
    public DpiBypassSettings? Working { get; init; }

    /// <summary>«Накрыт», но перебор вариантов не запускали — его стоит запустить кнопкой.</summary>
    public bool NeedsScan { get; init; }

    /// <summary>
    /// Провайдер рвёт само TCP-соединение до площадки: тут не помогает никакой разрыв
    /// пакетов, нужен VPN (или режим обхода «через профиль»).
    /// </summary>
    public bool AddressBlocked { get; init; }

    /// <summary>Подробные строки для журнала.</summary>
    public List<string> Lines { get; init; } = new();
}

/// <summary>Чем кончился замер — это и есть ответ на вопрос «кто виноват».</summary>
public enum DpiProbeKind
{
    /// <summary>Сервер ответил — площадка доступна.</summary>
    Answered,

    /// <summary>
    /// TCP дошёл, но на ClientHello никто не ответил: классическая картина DPI,
    /// который рвёт соединение, увидев имя сайта.
    /// </summary>
    Silent,

    /// <summary>
    /// TCP не поднимается вообще: провайдер не пускает до самого адреса (или не отдаёт DNS).
    /// Разрыв пакетов тут бессилен — поможет только VPN.
    /// </summary>
    NoRoute
}

/// <summary>Один замер: дошли ли до площадки и ответил ли сервер на ClientHello.</summary>
public sealed class DpiProbeResult
{
    public string Host = "";
    public string Address = "";

    /// <summary>Замер шёл через локальный прокси обхода, а не напрямую к сайту.</summary>
    public bool ViaProxy;

    public bool Connected;
    public bool Answered;
    public long ConnectMs;
    public long AnswerMs;
    public string Error = "";

    /// <summary>Тип первой записи TLS в ответе: 0x16 — handshake, 0x15 — alert.</summary>
    public int RecordType;

    /// <summary>Код alert, если сервер ответил им (0 — ответа не было или это не alert).</summary>
    public int AlertCode;

    public bool Ok => Answered;

    /// <summary>Кто виноват: доступно, режут по содержимому или режут по адресу.</summary>
    public DpiProbeKind Kind =>
        Answered ? DpiProbeKind.Answered
        : Connected ? DpiProbeKind.Silent
        : DpiProbeKind.NoRoute;

    public string Describe()
    {
        if (Error.Length > 0 && !Connected) return "соединение не поднялось: " + Error;
        if (Error.Length > 0) return $"TCP {ConnectMs} мс, ответа нет: {Error}";
        if (RecordType == 0x15)
        {
            var alert = AlertCode > 0 ? $"alert {AlertCode} ({AlertName(AlertCode)})" : "alert";
            return $"TCP {ConnectMs} мс, сервер ответил {alert} через {AnswerMs} мс";
        }
        return $"TCP {ConnectMs} мс, ответ через {AnswerMs} мс";
    }

    /// <summary>Одна строка для журнала: что произошло, на каком адресе и с какими цифрами.</summary>
    public string DescribeKind()
    {
        var lead = Kind switch
        {
            DpiProbeKind.Answered => "открывается",
            DpiProbeKind.Silent => ViaProxy
                ? "туннель через обход поднят, ответа нет"
                : "TCP доходит, ответа нет — режет по содержимому",
            _ => ViaProxy
                ? "обход не поднял туннель"
                : "TCP не поднимается — режет по адресу"
        };

        var address = Address.Length > 0 ? $" [{Address}]" : "";
        return $"{lead}{address}: {Describe()}";
    }

    /// <summary>Человеческое название alert — по нему видно, чем серверу не угодил ClientHello.</summary>
    private static string AlertName(int code) => code switch
    {
        0 => "close_notify",
        40 => "handshake_failure",
        50 => "illegal_parameter",
        51 => "unknown_ca",
        70 => "protocol_version",
        80 => "internal_error",
        86 => "inappropriate_fallback",
        109 => "missing_extension",
        112 => "unrecognized_name",
        120 => "no_application_protocol",
        _ => "другой"
    };
}

/// <summary>
/// DPI-тест. Разбирает ситуацию по частям — иначе «Накрыт» ничего не объясняет:
/// 1) есть ли интернет вообще (ответил ли кто-нибудь: контрольный адрес или площадка);
/// 2) как именно провайдер режет каждую площадку — по содержимому (TCP доходит, ответа нет)
///    или по адресу (TCP не поднимается, тут поможет только VPN);
/// 3) помогает ли обход с текущими настройками.
/// Если не помогает — перебирает варианты (TTL фейк-пакета, место разрыва, мультисплит,
/// дробление по MSS) и возвращает первый рабочий.
/// </summary>
public static class DpiTester
{
    /// <summary>Площадки, которые в РФ блокируются средствами DPI.</summary>
    public static readonly string[] BlockedHosts =
    {
        "discord.com",
        "soundcloud.com",
        "instagram.com",
        "youtube.com"
    };

    /// <summary>Контроль: то, что в РФ открывается и так.</summary>
    private static readonly string[] ControlHosts =
    {
        "cloudflare.com",
        "www.gstatic.com"
    };

    private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };

    /// <summary>Сколько ждём ответа на прямой замер, мс.</summary>
    /// <summary>
    /// Приветствие для замеров: его же использует диагностика. Публично, чтобы
    /// диагностика могла мерить TLS тем же пакетом, что и тест.
    /// </summary>
    public static byte[] BuildProbeHello(string host) => BuildClientHello(host);

    private const int DirectTimeoutMs = 6000;

    /// <summary>Сколько ждём ответа через обход, мс.</summary>
    private const int ViaTimeoutMs = 6000;

    /// <summary>Сколько ждём ответа на один вариант при переборе, мс.</summary>
    private const int ScanTimeoutMs = 5000;

    /// <param name="enginePort">Порт работающего обхода; 0 — мерить напрямую, без обхода.</param>
    /// <param name="apply">Перезапуск движка с новыми настройками (для перебора вариантов).</param>
    /// <param name="progress">Ход теста — в строку состояния.</param>
    /// <param name="scan">Перебирать ли варианты, если текущие настройки не помогают.</param>
    public static async Task<DpiTestResult> TestAsync(
        int enginePort,
        Func<DpiBypassSettings, Task> apply,
        DpiBypassSettings current,
        Action<string>? progress = null,
        bool scan = true,
        CancellationToken ct = default)
    {
        var lines = new List<string>();

        // ------------------------------------------------------------ 1. напрямую
        // Контроль и площадки мерим одной пачкой. Вывод «интернета нет» делается по всей
        // картине, а не по одному контрольному адресу: иначе тест упирался в контроль,
        // который провайдер не пускает (или который отвечает alert вместо ServerHello),
        // и говорил «интернета нет» там, где ютуб спокойно открывался.
        progress?.Invoke("проверяем площадки напрямую");

        var everything = ControlHosts.Concat(BlockedHosts).ToArray();
        var probes = await ProbeAllAsync(everything, 0, DirectTimeoutMs, ct);
        var control = probes.Take(ControlHosts.Length).ToArray();
        var direct = probes.Skip(ControlHosts.Length).ToArray();

        foreach (var probe in control) lines.Add($"контроль {probe.Host}: {probe.DescribeKind()}");
        foreach (var probe in direct) lines.Add($"напрямую {probe.Host}: {probe.DescribeKind()}");

        // интернет есть, если ответил хотя бы кто-то — контрольный адрес или площадка
        var alive = probes.FirstOrDefault(p => p.Ok);

        if (alive is null && enginePort > 0)
        {
            // никто не ответил напрямую — сначала пробуем через обход, и только потом
            // говорим «интернета нет»
            progress?.Invoke("проверяем через обход");
            var viaAll = await ProbeAllAsync(everything, enginePort, ViaTimeoutMs, ct);
            foreach (var probe in viaAll) lines.Add($"через обход {probe.Host}: {probe.DescribeKind()}");

            alive = viaAll.FirstOrDefault(p => p.Ok);
            if (alive is not null) lines.Add("напрямую не ответил никто — связь появилась только через обход");
        }

        if (alive is null)
        {
            var reasons = string.Join("; ", probes.Select(p => $"{p.Host}: {p.Describe()}"));
            lines.Add("не ответил никто: ни контроль, ни площадки — ни напрямую, ни через обход");
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.NoNetwork,
                Detail = "интернета нет: " + reasons,
                Lines = lines
            };
        }

        lines.Add($"интернет есть: {alive.Host} — {alive.Describe()}");

        // ----------------------------------------- 2. кто заблокирован и как именно
        var byContent = direct.Where(p => p.Kind == DpiProbeKind.Silent).ToList();
        var byAddress = direct.Where(p => p.Kind == DpiProbeKind.NoRoute).ToList();

        if (byContent.Count == 0 && byAddress.Count == 0)
        {
            lines.Add("все площадки открываются и без обхода — DPI сейчас ничего не режет");
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = direct[0].Host,
                Detail = "блокировки нет: площадки открываются напрямую",
                Lines = lines
            };
        }

        // -------------------------------------------- 3. текущие настройки обхода
        progress?.Invoke("проверяем обход");
        var blocked = byContent.Concat(byAddress).Select(p => p.Host).ToArray();
        var via = await ProbeAllAsync(blocked, enginePort, ViaTimeoutMs, ct);
        foreach (var probe in via) lines.Add($"через обход {probe.Host}: {probe.DescribeKind()}");

        var opened = via.FirstOrDefault(p => p.Ok);
        if (opened is not null)
        {
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = opened.Host,
                Detail = $"{opened.Host} отвечает ({opened.Describe()})",
                Lines = lines
            };
        }

        var perHost = via.Select(probe => probe.Kind == DpiProbeKind.NoRoute
            ? $"{probe.Host}: провайдер не пускает до адреса — пакетами не помочь, нужен VPN"
            : $"{probe.Host}: DPI режет ClientHello");
        var detail = string.Join("; ", perHost);

        // ------------------------------------------------- 4. перебор вариантов
        // Перебираем только там, где TCP доходит: если провайдер рвёт само соединение,
        // никакая десинхронизация не поможет, и жечь на это минуту смысла нет.
        var workable = via.Where(p => p.Kind == DpiProbeKind.Silent).Select(p => p.Host).ToArray();

        if (workable.Length == 0)
        {
            lines.Add("TCP до площадок не поднимается — перебор вариантов бессмысленен");
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Covered,
                Host = via[0].Host,
                Detail = detail,
                AddressBlocked = true,
                Lines = lines
            };
        }

        if (!scan)
        {
            lines.Add("перебор вариантов пропущен — нужен полный «DPI тест»");
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Covered,
                Host = workable[0],
                Detail = detail,
                NeedsScan = true,
                Lines = lines
            };
        }

        var variants = Variants(current);
        lines.Add($"текущие настройки не помогают — перебираем {variants.Count} вариантов на {string.Join(", ", workable)}");

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            progress?.Invoke($"вариант {i + 1} из {variants.Count}: {Describe(variant)}");
            lines.Add($"вариант {i + 1}/{variants.Count}: {Describe(variant)}");

            try
            {
                await apply(variant);
            }
            catch (Exception ex)
            {
                lines.Add("  движок не перезапустился: " + ex.Message);
                continue;
            }

            var probeAll = await ProbeAllAsync(workable, enginePort, ScanTimeoutMs, ct);
            foreach (var probe in probeAll) lines.Add($"  через обход {probe.Host}: {probe.DescribeKind()}");

            var hit = probeAll.FirstOrDefault(p => p.Ok);
            if (hit is null) continue;

            lines.Add($"подошёл вариант: {Describe(variant)}");
            return new DpiTestResult
            {
                Verdict = DpiTestVerdict.Broken,
                Host = hit.Host,
                Detail = $"{hit.Host} открылся на варианте «{Describe(variant)}»",
                Working = variant,
                Lines = lines
            };
        }

        lines.Add("ни один вариант не помог");
        return new DpiTestResult
        {
            Verdict = DpiTestVerdict.Covered,
            Host = workable[0],
            Detail = detail,
            Lines = lines
        };
    }
    private static Task<DpiProbeResult[]> ProbeAllAsync(string[] hosts, int enginePort, int timeoutMs, CancellationToken ct)
    {
        var probes = hosts
            .Select(host => ProbeAsync(host, enginePort, timeoutMs, ct))
            .ToArray();
        return Task.WhenAll(probes);
    }

    /// <summary>
    /// Замер: подключаемся к площадке (напрямую или через обход) и ждём ответ сервера
    /// на свой ClientHello. Дошёл ответ — значит блокировки нет или она обойдена.
    /// </summary>
    public static async Task<DpiProbeResult> ProbeAsync(string host, int enginePort, int timeoutMs, CancellationToken ct)
    {
        var result = new DpiProbeResult { Host = host };
        var sw = Stopwatch.StartNew();
        TcpClient? client = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);

            client = new TcpClient();

            if (enginePort > 0)
            {
                // через обход: просим туннель у локального прокси. «Соединение» тут — это
                // соединение до своего же прокси, поэтому в журнале оно описывается иначе
                result.ViaProxy = true;
                await client.ConnectAsync(IPAddress.Loopback, enginePort, timeout.Token);
                var connect = Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
                await client.GetStream().WriteAsync(connect, timeout.Token);
                await client.GetStream().FlushAsync(timeout.Token);

                var reply = await ReadUntilAsync(client.GetStream(), HeadEnd, 8192, timeout.Token);
                var answer = Encoding.ASCII.GetString(reply);
                if (!answer.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
                    !answer.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("прокси не открыл туннель: " + answer.Split("\r\n")[0]);
            }
            else
            {
                // адреса берём тем же резолвером, что и движок: если провайдер подменяет
                // DNS, замер должен это видеть, а не мерить подставной адрес
                var settings = App.Settings.Current.Dpi;
                var lookup = await DnsResolver.ResolveAsync(host, settings.Doh, settings.DohUrl, timeout.Token);
                var address = lookup.InConnectOrder.FirstOrDefault();
                if (address is null) throw new IOException("DNS не отдал адрес: " + host);

                result.Address = address.ToString();
                await client.ConnectAsync(address, 443, timeout.Token);
            }

            result.Connected = true;
            result.ConnectMs = sw.ElapsedMilliseconds;

            var hello = BuildClientHello(host);
            await client.GetStream().WriteAsync(hello, timeout.Token);
            await client.GetStream().FlushAsync(timeout.Token);

            // ждём заголовок записи: 0x16 (handshake) или 0x15 (alert), дальше 0x03
            var head = new byte[7];
            var read = 0;
            while (read < 5)
            {
                var n = await client.GetStream().ReadAsync(head.AsMemory(read, head.Length - read), timeout.Token);
                if (n <= 0) break;
                read += n;
            }

            result.AnswerMs = sw.ElapsedMilliseconds;
            result.RecordType = read > 0 ? head[0] : 0;

            // Любая запись TLS в ответе — пакеты дошли: сервер мог прислать alert вместо
            // ServerHello, но для проверки блокировки это всё равно ответ.
            if (read >= 5 && head[1] == 0x03 && (head[0] == 0x16 || head[0] == 0x15))
            {
                result.Answered = true;

                // alert дочитываем до конца: его код говорит, чем серверу не угодил ClientHello
                if (head[0] == 0x15)
                {
                    while (read < head.Length)
                    {
                        var n = await client.GetStream().ReadAsync(head.AsMemory(read, head.Length - read), timeout.Token);
                        if (n <= 0) break;
                        read += n;
                    }

                    if (read >= 7) result.AlertCode = head[6];
                }
            }
            else if (read > 0) result.Error = $"в ответе не TLS ({head[0]:X2} {head[1]:X2})";
            else result.Error = "сервер молчит";
        }
        catch (OperationCanceledException)
        {
            result.Error = "таймаут";
        }
        catch (SocketException ex)
        {
            result.Error = ex.SocketErrorCode == SocketError.HostNotFound
                ? "DNS не знает такого имени"
                : "сеть: " + ex.SocketErrorCode;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            try { client?.Dispose(); }
            catch { /* ignore */ }
        }

        return result;
    }

    private static async Task<byte[]> ReadUntilAsync(NetworkStream stream, byte[] pattern, int limit, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(1024, pattern.Length * 2)];
        var length = 0;

        while (length < limit)
        {
            if (length >= pattern.Length && EndsWith(buffer, length, pattern))
                return buffer[..length];

            if (length == buffer.Length)
            {
                var bigger = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                buffer = bigger;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read <= 0) break;
            length += read;
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

    // --------------------------------------------------------------- варианты
    private static List<DpiBypassSettings> Variants(DpiBypassSettings current)
    {
        var list = new List<DpiBypassSettings>();

        // основной рабочий приём для ТСПУ — фейк-пакет с TTL, которого хватит до DPI,
        // но не до сайта; перебираем типичные значения
        foreach (var ttl in new[] { 8, 5, 12, 3, 16 })
            list.Add(Variant(current, ttl));

        // тот же фейк, но уже после настоящего ClientHello
        list.Add(Variant(current, 8, after: true));

        // два фейк-пакета подряд: некоторым DPI одного мало
        list.Add(Variant(current, 8, count: 2));

        // один split без фейка
        list.Add(Variant(current, 0, fake: false));
        list.Add(Variant(current, 0, fake: false, recordSplit: false));

        // фейк плюс дробление по MSS: система сама режет ClientHello на мелкие сегменты
        list.Add(Variant(current, 8, mss: true));
        list.Add(Variant(current, 0, fake: false, mss: true));

        // мультисплит: много мелких кусков с паузой. Срывает DPI, который собирает
        // запись TLS по длине из заголовка и поэтому не боится разрыва на две части
        list.Add(Variant(current, 0, fake: false, multisplit: 8));
        list.Add(Variant(current, 8, multisplit: 8));
        list.Add(Variant(current, 0, fake: false, multisplit: 2));
        list.Add(Variant(current, 0, fake: false, multisplit: 24));

        return list;
    }

    private static DpiBypassSettings Variant(
        DpiBypassSettings current, int ttl, bool fake = true, bool sniSplit = true,
        bool recordSplit = true, bool after = false, int count = 1, bool mss = false,
        int multisplit = 0)
    {
        var clone = current.Clone();
        clone.FakeTtl = fake;
        clone.FakeTtlValue = ttl;
        clone.SniSplit = sniSplit;
        clone.TlsRecordSplit = recordSplit;
        clone.FakeAfter = after;
        clone.FakeCount = count;
        clone.ClampMss = mss;
        clone.Multisplit = multisplit > 0;
        clone.MultisplitSize = multisplit > 0 ? multisplit : current.MultisplitSize;
        return clone;
    }

    public static string Describe(DpiBypassSettings s)
    {
        var parts = new List<string>();
        if (s.FakeTtl)
        {
            var fake = $"fake TTL {s.FakeTtlValue}" + (s.FakeCount > 1 ? $" ×{s.FakeCount}" : "");
            parts.Add(s.FakeAfter ? fake + " после разрыва" : fake);
        }
        parts.Add(s.SniSplit ? "SNI split" : "split 2 байта");
        if (s.TlsRecordSplit) parts.Add("record split");
        if (s.Multisplit) parts.Add($"мультисплит по {s.MultisplitSize} байт");
        if (s.ClampMss) parts.Add($"MSS {s.MssValue}");
        return parts.Count == 0 ? "без обхода" : string.Join(" · ", parts);
    }

    // ------------------------------------------------------- свой ClientHello
    /// <summary>GREASE-расширение: браузеры вставляют такие, чтобы серверы не закрепляли формат.</summary>
    private const int Grease = 0x0a0a;

    /// <summary>Набор шифров как у браузера: сначала TLS 1.3, потом ECDHE.</summary>
    private static readonly byte[] Ciphers =
    {
        0x13, 0x01, 0x13, 0x02, 0x13, 0x03,
        0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30,
        0xcc, 0xa9, 0xcc, 0xa8,
        0xc0, 0x13, 0xc0, 0x14,
        0x00, 0x9c, 0x00, 0x9d, 0x00, 0x2f, 0x00, 0x35, 0x00, 0x0a
    };

    /// <summary>
    /// ClientHello как у браузера: TLS 1.3 + 1.2, обычный набор шифров, SNI, ALPN,
    /// key_share на P-256 и GREASE-расширения. Рукопожатие мы не доводим — важен сам
    /// факт ответа сервера, — но «приветствие» должно быть настоящим: на минимальный
    /// самодельный ClientHello серверы отвечают alert, и тест принимал это за блокировку.
    /// </summary>
    private static byte[] BuildClientHello(string host)
    {
        var random = new byte[32];
        Random.Shared.NextBytes(random);

        // браузеры шлют 32-байтный «старый» session_id — его ждёт часть посредников
        var sessionId = new byte[32];
        Random.Shared.NextBytes(sessionId);

        var extensions = new List<byte>();
        AddExtension(extensions, Grease, Array.Empty<byte>());                          // GREASE
        AddExtension(extensions, 0x0000, ServerName(host));                             // server_name
        AddExtension(extensions, 0x0017, Array.Empty<byte>());                          // extended_master_secret
        AddExtension(extensions, 0xff01, new byte[] { 0x00 });                          // renegotiation_info
        AddExtension(extensions, 0x000a, SupportedGroups());                            // supported_groups
        AddExtension(extensions, 0x000b, new byte[] { 0x01, 0x00 });                    // ec_point_formats
        AddExtension(extensions, 0x0023, Array.Empty<byte>());                          // session_ticket
        AddExtension(extensions, 0x0010, Alpn());                                       // ALPN: h2, http/1.1
        AddExtension(extensions, 0x0005, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 });  // status_request

        var keyShare = KeyShare();
        if (keyShare is not null) AddExtension(extensions, 0x0033, keyShare);            // key_share

        // supported_versions: GREASE, TLS 1.3, TLS 1.2 (длина списка — один байт)
        AddExtension(extensions, 0x002b, new byte[] { 0x06, 0x0a, 0x0a, 0x03, 0x04, 0x03, 0x03 });
        AddExtension(extensions, 0x000d, SignatureAlgorithms());                         // signature_algorithms
        AddExtension(extensions, 0x002d, new byte[] { 0x01, 0x01 });                     // psk_key_exchange_modes
        AddExtension(extensions, 0x001c, new byte[] { 0x40, 0x01 });                     // record_size_limit
        AddExtension(extensions, Grease, Array.Empty<byte>());                           // GREASE

        var body = new List<byte>();
        body.Add(0x03);
        body.Add(0x03);                                   // legacy_version: TLS 1.2
        body.AddRange(random);
        body.Add((byte)sessionId.Length);
        body.AddRange(sessionId);
        AddU16(body, Ciphers.Length);
        body.AddRange(Ciphers);
        body.Add(0x01);
        body.Add(0x00);                                   // compression: null
        AddU16(body, extensions.Count);
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };          // ClientHello
        handshake.Add((byte)(body.Count >> 16));
        handshake.Add((byte)((body.Count >> 8) & 0xFF));
        handshake.Add((byte)(body.Count & 0xFF));
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.Add((byte)(handshake.Count >> 8));
        record.Add((byte)(handshake.Count & 0xFF));
        record.AddRange(handshake);

        return record.ToArray();
    }

    private static byte[] ServerName(string host)
    {
        var name = Encoding.ASCII.GetBytes(host);

        var entry = new List<byte> { 0x00 };              // host_name
        AddU16(entry, name.Length);
        entry.AddRange(name);

        var list = new List<byte>();
        AddU16(list, entry.Count);
        list.AddRange(entry);
        return list.ToArray();
    }

    private static byte[] SupportedGroups()
    {
        var list = new List<byte>();
        AddU16(list, 6);
        AddU16(list, Grease);
        AddU16(list, 0x001d);                             // x25519
        AddU16(list, 0x0017);                             // secp256r1
        return list.ToArray();
    }

    private static byte[] Alpn()
    {
        var list = new List<byte>();
        AddU16(list, 12);
        list.Add(0x02);
        list.Add((byte)'h');
        list.Add((byte)'2');
        list.Add(0x08);
        foreach (var c in "http/1.1") list.Add((byte)c);
        return list.ToArray();
    }

    private static byte[] SignatureAlgorithms()
    {
        var algs = new byte[]
        {
            0x04, 0x03, 0x08, 0x04, 0x08, 0x05, 0x08, 0x06, 0x04, 0x01,
            0x05, 0x03, 0x05, 0x01, 0x02, 0x01, 0x02, 0x03
        };

        var list = new List<byte>();
        AddU16(list, algs.Length);
        list.AddRange(algs);
        return list.ToArray();
    }

    /// <summary>
    /// key_share: публичный ключ P-256, чтобы сервер сразу мог получить общую тайну.
    /// Без ключа часть серверов отвечает alert, и замер получается нечестным.
    /// </summary>
    private static byte[]? KeyShare()
    {
        try
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var spki = ecdh.PublicKey.ToByteArray();      // X.509 SubjectPublicKeyInfo
            if (spki.Length < 65) return null;

            var point = spki[^65..];                      // 0x04 || X || Y

            var entry = new List<byte>();
            AddU16(entry, 0x0017);                        // secp256r1
            AddU16(entry, point.Length);
            entry.AddRange(point);

            var list = new List<byte>();
            AddU16(list, entry.Count);
            list.AddRange(entry);
            return list.ToArray();
        }
        catch
        {
            return null;                                  // не вышло — шлём приветствие без key_share
        }
    }

    private static void AddExtension(List<byte> target, int type, byte[] data)
    {
        target.Add((byte)(type >> 8));
        target.Add((byte)(type & 0xFF));
        AddU16(target, data.Length);
        target.AddRange(data);
    }

    private static void AddU16(List<byte> target, int value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)(value & 0xFF));
    }
}
