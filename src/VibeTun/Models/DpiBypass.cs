using System.Text.Json.Serialization;

namespace VibeTun.Models;

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

    /// <summary>
    /// Обход поднят и пропускает трафик. Ничего не проверялось: проверки запускает пользователь
    /// кнопками «Тест» и «Подобрать» в окне сервиса.
    /// </summary>
    On,

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

/// <summary>
/// Где резать ClientHello — аналог <c>--dpi-desync-split-pos</c> из zapret.
/// Позиция определяет, какой именно сегмент DPI увидит первым и успеет ли он собрать имя сайта.
/// </summary>
public enum DpiSplitPosition
{
    /// <summary>Посередине имени из SNI (по умолчанию).</summary>
    Sni,

    /// <summary>Первый байт записи: самое мелкое дробление, у части DPI ломает разбор заголовка.</summary>
    FirstByte,

    /// <summary>Два байта от начала записи.</summary>
    SecondByte,

    /// <summary>Посередине домена второго уровня («google» в «www.google.com»).</summary>
    MidSld,

    /// <summary>Посередине всей записи ClientHello.</summary>
    Mid
}

/// <summary>
/// Как заставить фейк-пакет не дойти до сервера, но дойти до DPI — аналог
/// <c>--dpi-desync-fooling</c> из zapret. Значения складываются: чем больше «обманок»,
/// тем выше шанс, что сервер пакет выбросит, а DPI его всё равно разберёт.
/// </summary>
[Flags]
public enum DpiFooling
{
    /// <summary>Ничего не делаем: фейк уйдёт и серверу (так тоже бывает нужно — см. «после ClientHello»).</summary>
    None = 0,

    /// <summary>
    /// TTL: пакет дойдёт до DPI (он ближе) и умрёт, не дойдя до сервера. Основной приём.
    /// Значение TTL подбирается в настройках или считается автоматически.
    /// </summary>
    Ttl = 1,

    /// <summary>
    /// Битая контрольная сумма TCP: сервер такой пакет молча выбрасывает, DPI обычно
    /// контрольных сумм не проверяет. Страховка на случай, если TTL окажется великоват.
    /// </summary>
    BadSum = 2,

    /// <summary>
    /// Сдвинутый номер последовательности (seq минус 0x10000000): сервер считает пакет старым
    /// и не принимает его данные, а DPI видит содержимое. Аналог <c>fooling=badseq</c>.
    /// </summary>
    BadSeq = 4,

    /// <summary>Сброшенный флаг ACK: сервер молча игнорирует сегмент, DPI разбирает.</summary>
    DataNoAck = 8
}

/// <summary>
/// Где в потоке оказывается фейк-пакет (аналог seqovl из zapret): на месте первых байтов
/// настоящего ClientHello или раньше них — «в прошлом» окна.
/// </summary>
public enum DpiFakePlacement
{
    /// <summary>На месте первых байтов запроса: DPI принимает фейк за начало потока.</summary>
    Overlap,

    /// <summary>
    /// Раньше начала данных (как <c>--dpi-desync-split-seqovl</c>): DPI видит фейк до
    /// настоящего запроса, а сервер считает эти байты уже полученными и выбрасывает их.
    /// </summary>
    Ahead
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
    /// Фейк-пакет: DPI видит ClientHello с «чужим» именем, а сервер — нет.
    /// Отправляется отдельным TCP-сегментом в тот же поток (нужны права администратора),
    /// поэтому годится и для Discord, и для YouTube, где обычного разрыва не хватает.
    /// </summary>
    public bool FakeTtl { get; set; } = true;

    /// <summary>
    /// Слать фейк-пакет настоящим TCP-сегментом в тот же поток (аналог инъекции nfqws).
    /// Без прав администратора Windows не даёт создать raw-сокет — тогда фейк пропускается,
    /// и остаются только разрыв, disorder и мультисплит.
    /// </summary>
    public bool InjectFake { get; set; } = true;

    /// <summary>Чем «испортить» фейк, чтобы он не дошёл до сервера (см. <see cref="DpiFooling"/>).</summary>
    public DpiFooling FakeFooling { get; set; } = DpiFooling.Ttl;

    /// <summary>Где стоит фейк в потоке: на месте первых байтов или раньше них (seqovl).</summary>
    public DpiFakePlacement FakePlacement { get; set; } = DpiFakePlacement.Overlap;

    /// <summary>
    /// TTL фейк-пакета. Должен хватить, чтобы дойти до DPI, но не дойти до сайта.
    /// Точное значение зависит от провайдера — его подбирает DPI-тест.
    /// </summary>
    public int FakeTtlValue { get; set; } = 8;

    /// <summary>
    /// Сколько фейк-пакетов слать (некоторым DPI одного мало) — аналог
    /// <c>--dpi-desync-repeats</c> из zapret.
    /// </summary>
    public int FakeCount { get; set; } = 1;

    /// <summary>
    /// Свой SNI в фейк-пакете: DPI видит «чужое» имя, а настоящий ClientHello уходит следующим.
    /// Аналог <c>--dpi-desync-fake-tls-mod=sni=…</c> и <c>hostfakesplit</c> из zapret.
    /// Пусто — фейк идёт с настоящим именем сайта.
    /// </summary>
    public string FakeSni { get; set; } = "";

    /// <summary>
    /// Подбирать TTL фейк-пакета автоматически: сначала меряем число прыжков до площадки,
    /// затем ставим TTL на один меньше, чем нужно дойти до сервера, — фейк видят DPI,
    /// но не он (аналог <c>--dpi-desync-autottl</c>).
    /// </summary>
    public bool FakeAutoTtl { get; set; }

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

    /// <summary>
    /// Где резать ClientHello — аналог <c>--dpi-desync-split-pos</c>.
    /// Работает вместе с <see cref="SniSplit"/>/<see cref="TlsRecordSplit"/> и мультисплитом.
    /// </summary>
    public DpiSplitPosition SplitPosition { get; set; } = DpiSplitPosition.Sni;

    /// <summary>
    /// Отправлять вторую половину разрезанной записи раньше первой — аналог
    /// <c>--dpi-desync=disorder</c>/<c>multidisorder</c>.
    /// Делается через TTL: первые байты уходят с TTL=1 (до DPI и сервера не доходят),
    /// остаток уходит сразу, а «потерянное» начало ядро пересылает следом —
    /// так DPI получает сегменты не по порядку. Работает без прав администратора.
    /// </summary>
    public bool Disorder { get; set; }

    /// <summary>
    /// Отправить первый байт как срочные (out-of-band) данные: DPI видит запрос «с дыркой»,
    /// а сервер собирает его обычным образом. Аналог <c>--oob</c> из ByeDPI.
    /// </summary>
    public bool OobSplit { get; set; }

    /// <summary>Пауза между частями разделённого пакета (мс): чтобы они гарантированно ушли разными сегментами.</summary>
    public int SplitDelayMs { get; set; } = 5;

    /// <summary>
    /// Сколько стратегий пробовать на одно соединение, если сервер молчит: движок берёт
    /// подобранные профили сервиса по порядку. 1 — без подстраховки.
    /// </summary>
    public int FailoverCount { get; set; } = 3;

    /// <summary>
    /// Сколько ждать ответ сервера, прежде чем пробовать следующую стратегию (мс).
    /// Значение по умолчанию — из общего набора приёмов: 5 с хватает, чтобы ответ успел прийти
    /// после повторной передачи начала ClientHello.
    /// </summary>
    public int FailoverTimeoutMs { get; set; } = 5000;

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

    /// <summary>
    /// Сервер, которым спрашивать адреса доменов этого набора. Ставится движком из выбора DNS
    /// сервиса или «Общих» (раздел «DNS») и в настройках не хранится: пусто — как раньше,
    /// системный DNS или DoH обхода.
    /// </summary>
    [JsonIgnore]
    public string ResolveServer { get; set; } = "";

    public DpiBypassSettings Clone() => (DpiBypassSettings)MemberwiseClone();
}
