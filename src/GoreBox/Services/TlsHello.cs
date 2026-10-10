namespace GoreBox.Services;

/// <summary>
/// Разбор ClientHello: поиск SNI и переразбиение одной записи TLS на две.
///
/// Схема ClientHello:
/// <code>
/// [0]     0x16 — тип записи (handshake)
/// [1..2]  версия записи (0x03 0x01 / 0x03 0x03)
/// [3..4]  длина полезной нагрузки записи
/// [5]     0x01 — тип сообщения (ClientHello)
/// [6..8]  длина сообщения рукопожатия (3 байта)
/// [9...]  тело ClientHello
/// </code>
/// Тело: версия (2), random (32), session_id (1 + N), cipher_suites (2 + N),
/// compression (1 + N), extensions (2 + N), где расширение server_name имеет тип 0.
/// </summary>
public static class TlsHello
{
    /// <summary>Длина заголовка записи TLS.</summary>
    public const int RecordHeader = 5;

    /// <summary>Длина заголовка сообщения рукопожатия.</summary>
    public const int HandshakeHeader = 4;

    /// <summary>На сколько байт от начала тела режем, если SNI найти не удалось.</summary>
    public const int DefaultSplitBytes = 2;

    /// <summary>Первый байт похож на запись handshake TLS.</summary>
    public static bool IsHandshakeRecord(byte[] b, int len) =>
        len >= 6 && b[0] == 0x16 && b[1] == 0x03;

    /// <summary>
    /// Полный размер первой записи TLS в буфере. Возвращает -1, если запись ещё не дочитана
    /// (или это вообще не TLS): резать недочитанное нельзя.
    /// </summary>
    public static int RecordSize(byte[] b, int len)
    {
        if (len < RecordHeader || b[0] != 0x16) return -1;
        var payload = (b[3] << 8) | b[4];
        var total = RecordHeader + payload;
        return len >= total ? total : -1;
    }

    /// <summary>
    /// Смещение строки хоста в расширении SNI (абсолютное, от начала буфера).
    /// -1 — SNI нет или буфер не похож на ClientHello.
    /// </summary>
    public static int SniOffset(byte[] b, int len, out int sniLength)
    {
        sniLength = 0;
        if (len < RecordHeader + HandshakeHeader + 2 || b[0] != 0x16) return -1;

        var p = RecordHeader;
        if (b[p] != 0x01) return -1;                              // не ClientHello

        var hsLen = (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
        var body = p + HandshakeHeader;
        var end = body + hsLen;
        if (end > len) end = len;

        var q = body;
        if (q + 2 > end) return -1;
        q += 2;                                                   // legacy_version
        if (q + 32 > end) return -1;
        q += 32;                                                  // random
        if (q >= end) return -1;

        var sessionLen = b[q];
        q += 1 + sessionLen;
        if (q + 2 > end) return -1;

        var cipherLen = (b[q] << 8) | b[q + 1];
        q += 2 + cipherLen;
        if (q >= end) return -1;

        var compressLen = b[q];
        q += 1 + compressLen;
        if (q + 2 > end) return -1;

        var extLen = (b[q] << 8) | b[q + 1];
        q += 2;
        var extEnd = q + extLen;
        if (extEnd > end) extEnd = end;

        while (q + 4 <= extEnd)
        {
            var type = (b[q] << 8) | b[q + 1];
            var size = (b[q + 2] << 8) | b[q + 3];
            var data = q + 4;
            if (data + size > extEnd) break;

            if (type == 0 && data + 2 <= data + size)              // server_name
            {
                var r = data;
                var listLen = (b[r] << 8) | b[r + 1];
                var listEnd = r + 2 + listLen;
                if (listEnd > data + size) listEnd = data + size;
                r += 2;

                while (r + 3 <= listEnd)
                {
                    var nameType = b[r];
                    var nameLen = (b[r + 1] << 8) | b[r + 2];
                    var nameStart = r + 3;
                    if (nameStart + nameLen > listEnd) break;
                    if (nameType == 0)
                    {
                        sniLength = nameLen;
                        return nameStart;
                    }
                    r = nameStart + nameLen;
                }
            }

            q = data + size;
        }

        return -1;
    }

    /// <summary>
    /// Где резать запись (абсолютное смещение от начала буфера): середина имени из SNI,
    /// либо <see cref="DefaultSplitBytes"/> байт от начала тела, если SNI нет.
    /// Возвращает -1, если резать некуда.
    /// </summary>
    public static int SplitPosition(byte[] b, int len, bool splitAtSni)
    {
        if (len < RecordHeader + HandshakeHeader + 4) return -1;

        if (splitAtSni)
        {
            var sni = SniOffset(b, len, out var sniLength);
            if (sni > 0 && sniLength > 0)
            {
                var atSni = sni + Math.Max(1, sniLength / 2);
                if (atSni > RecordHeader + HandshakeHeader && atSni < len - 1) return atSni;
            }
        }

        var fallback = RecordHeader + HandshakeHeader + DefaultSplitBytes;
        if (fallback >= len - 1) return -1;
        return fallback;
    }

    /// <summary>
    /// Разбить ClientHello на две записи TLS с тем же содержимым: сообщение рукопожатия
    /// остаётся одним (длина в его заголовке не меняется), но фрагментируется по записям.
    /// </summary>
    /// <param name="b">Буфер с одной записью ClientHello.</param>
    /// <param name="len">Длина записи (<see cref="RecordSize"/>).</param>
    /// <param name="cut">Абсолютное смещение, где резать тело сообщения (<see cref="SplitPosition"/>).</param>
    public static bool TrySplitRecords(byte[] b, int len, int cut, out byte[] first, out byte[] second)
    {
        first = Array.Empty<byte>();
        second = Array.Empty<byte>();

        var bodyStart = RecordHeader + HandshakeHeader;
        if (len < bodyStart + 8 || b[0] != 0x16) return false;

        var hsLen = (b[RecordHeader + 1] << 16) | (b[RecordHeader + 2] << 8) | b[RecordHeader + 3];
        if (hsLen <= 0 || bodyStart + hsLen > len) return false;

        var headLen = cut - bodyStart;
        if (headLen <= 0 || headLen >= hsLen) return false;
        var tailLen = hsLen - headLen;

        first = new byte[bodyStart + headLen];
        first[0] = 0x16;
        first[1] = b[1];
        first[2] = b[2];
        var firstPayload = HandshakeHeader + headLen;
        first[3] = (byte)(firstPayload >> 8);
        first[4] = (byte)(firstPayload & 0xFF);
        first[RecordHeader] = 0x01;
        first[RecordHeader + 1] = (byte)(hsLen >> 16);
        first[RecordHeader + 2] = (byte)((hsLen >> 8) & 0xFF);
        first[RecordHeader + 3] = (byte)(hsLen & 0xFF);
        Buffer.BlockCopy(b, bodyStart, first, bodyStart, headLen);

        second = new byte[RecordHeader + tailLen];
        second[0] = 0x16;
        second[1] = b[1];
        second[2] = b[2];
        second[3] = (byte)(tailLen >> 8);
        second[4] = (byte)(tailLen & 0xFF);
        Buffer.BlockCopy(b, cut, second, RecordHeader, tailLen);

        return true;
    }
}
