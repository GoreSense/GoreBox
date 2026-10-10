namespace GoreBox.Models;

/// <summary>
/// Состояние обхода DPI. Показывается в левой карточке на вкладке «DPI bypass»
/// там же, где на вкладке «Proxy» написано «Подключено».
/// </summary>
public enum DpiBypassState
{
    /// <summary>Обход выключен.</summary>
    Off,

    /// <summary>Поднимается локальный прокси.</summary>
    Starting,

    /// <summary>Идёт DPI-тест.</summary>
    Checking,

    /// <summary>«Сломано» — блокировка обходится, заблокированные сайты открываются.</summary>
    Broken,

    /// <summary>«Накрыт» — DPI режет, обход не помогает.</summary>
    Covered,

    /// <summary>Интернета нет вообще: и контрольный адрес недоступен.</summary>
    NoNetwork,

    /// <summary>Локальный прокси не поднялся (порт занят и т.п.).</summary>
    Error
}

/// <summary>Куда движок отправляет трафик после десинхронизации.</summary>
public enum DpiExitMode
{
    /// <summary>Напрямую к сайту — классический zapret-режим, работает без VPN.</summary>
    Direct,

    /// <summary>
    /// В локальный вход ядра sing-box (активный профиль): системный прокси смотрит в обход,
    /// а дальше трафик идёт через профиль. Если ядро не поднято, движок идёт напрямую.
    /// </summary>
    Proxy
}

/// <summary>Откуда брать адреса сайтов: системный DNS или DoH (DNS поверх HTTPS).</summary>
public enum DohMode
{
    /// <summary>Системный DNS — то, что отдаёт провайдер.</summary>
    Off,

    /// <summary>cloudflare-dns.com.</summary>
    Cloudflare,

    /// <summary>dns.google.</summary>
    Google,

    /// <summary>Свой URL из настроек (любой DoH с JSON-ответом).</summary>
    Custom
}

/// <summary>Настройки обхода DPI. Отдельный блок в settings.json.</summary>
public sealed class DpiBypassSettings
{
    /// <summary>Обход был включён при выходе (восстанавливается вместе с прокси).</summary>
    public bool Enabled { get; set; }

    /// <summary>Порт локального HTTP/SOCKS-прокси с десинхронизацией.</summary>
    public int ListenPort { get; set; } = 9910;

    /// <summary>
    /// Резать ClientHello посередине имени домена из SNI. Для обычного HTTP режет
    /// запрос перед двоеточием в «Host:» — смысл тот же: DPI не видит имя в первом пакете.
    /// </summary>
    public bool SniSplit { get; set; } = true;

    /// <summary>
    /// Дополнительно разбивать один TLS record на два, чтобы DPI, собирающий запись
    /// по длине из заголовка, не увидел имя целиком.
    /// </summary>
    public bool TlsRecordSplit { get; set; } = true;

    /// <summary>
    /// Фейк-пакет: тот же ClientHello уходит с отдельного соединения с маленьким TTL —
    /// DPI его видит, а сайт нет. Включают, когда один split не помогает.
    /// </summary>
    public bool FakeTtl { get; set; } = true;

    /// <summary>
    /// TTL фейк-пакета. Должен хватить, чтобы дойти до DPI, но не дойти до сайта.
    /// Точное значение зависит от провайдера — его подбирает DPI-тест.
    /// </summary>
    public int FakeTtlValue { get; set; } = 8;

    /// <summary>Сколько фейк-пакетов слать (некоторым DPI одного мало).</summary>
    public int FakeCount { get; set; } = 1;

    /// <summary>
    /// Слать фейк-пакет ПОСЛЕ настоящего ClientHello, а не до него.
    /// Порядок подбирается перебором: у разных DPI работает по-разному.
    /// </summary>
    public bool FakeAfter { get; set; }

    /// <summary>
    /// Резать исходящие данные по MSS: система сама разобьёт ClientHello на мелкие
    /// сегменты, и имя сайта не попадёт в первый пакет целиком.
    /// </summary>
    public bool ClampMss { get; set; }

    /// <summary>Размер сегмента при <see cref="ClampMss"/> (TCP_MAXSEG), байт.</summary>
    public int MssValue { get; set; } = 64;

    /// <summary>
    /// «Мультисплит»: ClientHello уходит не двумя частями, а множеством мелких кусков
    /// с паузой между ними. Срывает DPI, который собирает запись TLS по длине из заголовка
    /// и поэтому не боится обычного разрыва на две части.
    /// </summary>
    public bool Multisplit { get; set; }

    /// <summary>Размер куска при <see cref="Multisplit"/>, байт.</summary>
    public int MultisplitSize { get; set; } = 8;

    /// <summary>Пауза между частями разделённого пакета (мс): чтобы они гарантированно ушли разными сегментами.</summary>
    public int SplitDelayMs { get; set; } = 5;

    /// <summary>
    /// Откуда брать адреса. Провайдерский DNS часто подменяет ответ на заблокированных
    /// сайтах; DoH такого не делает, потому что провайдер запроса не видит.
    /// </summary>
    public DohMode Doh { get; set; } = DohMode.Off;

    /// <summary>Свой URL DoH (при <see cref="Doh"/> = <see cref="DohMode.Custom"/>).</summary>
    public string DohUrl { get; set; } = "";

    /// <summary>Прописывать обход в системный прокси Windows, пока он включён.</summary>
    public bool UseSystemProxy { get; set; } = true;

    /// <summary>Куда идти после десинхронизации: напрямую или в активный профиль.</summary>
    public DpiExitMode ExitMode { get; set; } = DpiExitMode.Direct;

    public DpiBypassSettings Clone() => (DpiBypassSettings)MemberwiseClone();
}
