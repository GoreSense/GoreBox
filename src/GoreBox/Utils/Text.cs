using System.Globalization;
using System.Text;

namespace GoreBox.Utils;

public static class Text
{
    /// <summary>
    /// Декодирование значения из ссылки. Плюс НЕ превращается в пробел:
    /// в base64-ключах (REALITY pbk, WireGuard) символ «+» значимый.
    /// </summary>
    public static string UrlDecode(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var val = idx < 0 ? "" : pair[(idx + 1)..];
            if (key.Length == 0) continue;
            result[UrlDecode(key)] = UrlDecode(val);
        }
        return result;
    }

    /// <summary>Base64 (в т.ч. url-safe, без паддинга) → строка.</summary>
    public static bool TryBase64(string input, out string decoded)
    {
        decoded = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim().Replace('-', '+').Replace('_', '/').Replace(" ", "").Replace("\n", "").Replace("\r", "");
        var pad = s.Length % 4;
        if (pad > 0) s = s.PadRight(s.Length + (4 - pad), '=');
        try
        {
            var bytes = Convert.FromBase64String(s);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Base64Url(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Приводит ключ WireGuard / AmneziaWG к стандартному base64 с паддингом.
    /// Ядро sing-box декодирует <c>private_key</c>, <c>peers[].public_key</c>, <c>pre_shared_key</c>
    /// и <c>header_protection_key</c> строго через <c>base64.StdEncoding</c>, поэтому url-safe-алфавит
    /// («-»/«_»), отсутствующий «=» или переносы строк из буфера обмена роняют эндпоинт
    /// с ошибкой «decode private key» — туннель не поднимается вообще.
    /// Значения, которые base64 не являются, возвращаем как есть: пусть ядро сообщит об ошибке само.
    /// </summary>
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var compact = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (!char.IsWhiteSpace(ch) && ch != '"' && ch != '\'') compact.Append(ch);

        var text = compact.ToString().Replace('-', '+').Replace('_', '/');
        if (text.Length == 0) return "";

        var tail = text.Length % 4;
        if (tail == 1) return value.Trim();          // заведомо не base64
        if (tail > 0) text = text.PadRight(text.Length + (4 - tail), '=');

        try
        {
            _ = Convert.FromBase64String(text);
            return text;
        }
        catch
        {
            return value.Trim();
        }
    }

    public static string ToBase64(string input) => Convert.ToBase64String(Encoding.UTF8.GetBytes(input));

    public static bool IsBase64Blob(string s)
    {
        var t = s.Trim();
        if (t.Length < 24 || t.Contains(' ')) return false;
        return t.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_' or '\n' or '\r');
    }

    public static int ToInt(string? s, int def = 0)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static int? ToIntOrNull(string? s) => int.TryParse(s, out var v) ? v : null;

    public static bool ToBool(string? s, bool def = false)
    {
        if (string.IsNullOrWhiteSpace(s)) return def;
        var t = s.Trim().ToLowerInvariant();
        return t switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => def
        };
    }

    public static string Bytes(long b)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }

    /// <summary>Нормализует то, что ввёл пользователь ("vk", "vk.com", "*.vk.com", "1.2.3.4/24", "regex:ads.*").</summary>
    public static (string pattern, Models.MatchKind kind) NormalizeUserPattern(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return ("", Models.MatchKind.Domain);

        if (s.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            return (s[6..].Trim(), Models.MatchKind.Regex);

        // IP или подсеть
        if (System.Net.IPAddress.TryParse(s.Split('/')[0].Trim('[', ']'), out _))
            return (s, Models.MatchKind.Ip);

        if (s.Contains('/') && s.Count(c => c == '.') >= 1 && s.Split('/')[0].Count(c => c == '.') == 3)
            return (s, Models.MatchKind.Ip);

        // домен или regex: «youtube»/«ya» без точки — это regex, «youtube.com»/«.ru» — домен
        var hasDot = s.Contains('.');
        var looksLikeDomain = hasDot && s.Length > 2 && !s.Contains(' ') && !s.Contains('*');
        var isWildcard = s.StartsWith("*.") && hasDot;
        if (isWildcard) return (s[2..], Models.MatchKind.Domain);

        return looksLikeDomain ? (s, Models.MatchKind.Domain) : (s, Models.MatchKind.Regex);
    }
}
