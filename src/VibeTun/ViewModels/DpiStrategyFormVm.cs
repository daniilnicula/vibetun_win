using VibeTun.Models;

namespace VibeTun.ViewModels;

/// <summary>
/// Ручки обхода DPI в окне сервиса: те же поля, что у <see cref="DpiBypassSettings"/>, но с
/// уведомлениями об изменениях — по ним сразу видно, что именно уйдёт в движок.
/// Транспортные поля (порт, DNS обхода, режим выхода) остаются в настройках приложения:
/// сервис выбирает только приёмы десинхронизации.
/// </summary>
public sealed class DpiStrategyFormVm : ObservableObject
{
    private bool _sniSplit = true;
    private bool _tlsRecordSplit = true;
    private bool _fakeTtl = true;
    private bool _fakeAfter;
    private bool _fakeAutoTtl;
    private int _fakeTtlValue = 8;
    private int _fakeCount = 1;
    private string _fakeSni = "";
    private bool _clampMss;
    private int _mssValue = 64;
    private bool _multisplit;
    private int _multisplitSize = 8;
    private bool _disorder;
    private bool _oobSplit;
    private bool _injectFake = true;
    private DpiFooling _fakeFooling = DpiFooling.Ttl;
    private DpiFakePlacement _fakePlacement = DpiFakePlacement.Overlap;
    private int _failoverCount = 3;
    private int _failoverTimeoutMs = 1500;
    private int _splitDelayMs = 5;
    private DpiSplitPosition _splitPosition = DpiSplitPosition.Sni;

    /// <summary>Любое поле изменилось — окно обновляет описание набора приёмов.</summary>
    public event Action? Changed;

    /// <summary>
    /// Записать приёмы из формы в настройки: транспортные поля (порт, DNS обхода, режим выхода)
    /// у <paramref name="target"/> не трогаем — их задаёт общий раздел «Обход DPI».
    /// </summary>
    public void ApplyTo(DpiBypassSettings target)
    {
        target.SniSplit = _sniSplit;
        target.TlsRecordSplit = _tlsRecordSplit;
        target.FakeTtl = _fakeTtl;
        target.FakeAfter = _fakeAfter;
        target.FakeAutoTtl = _fakeAutoTtl;
        target.FakeTtlValue = _fakeTtlValue;
        target.FakeCount = _fakeCount;
        target.FakeSni = _fakeSni.Trim();
        target.ClampMss = _clampMss;
        target.MssValue = _mssValue;
        target.Multisplit = _multisplit;
        target.MultisplitSize = _multisplitSize;
        target.Disorder = _disorder;
        target.OobSplit = _oobSplit;
        target.InjectFake = _injectFake;
        target.FakeFooling = _fakeFooling;
        target.FakePlacement = _fakePlacement;
        target.FailoverCount = _failoverCount;
        target.FailoverTimeoutMs = _failoverTimeoutMs;
        target.SplitDelayMs = _splitDelayMs;
        target.SplitPosition = _splitPosition;
    }

    /// <summary>Текущий набор приёмов — то, что получит движок и профиль.</summary>
    public DpiBypassSettings Settings
    {
        get
        {
            var settings = new DpiBypassSettings();
            ApplyTo(settings);
            return settings;
        }
    }

    /// <summary>Загрузить набор приёмов (у профиля или у самого сервиса).</summary>
    public void Load(DpiBypassSettings settings)
    {
        _sniSplit = settings.SniSplit;
        _tlsRecordSplit = settings.TlsRecordSplit;
        _fakeTtl = settings.FakeTtl;
        _fakeAfter = settings.FakeAfter;
        _fakeAutoTtl = settings.FakeAutoTtl;
        _fakeTtlValue = Math.Clamp(settings.FakeTtlValue, 1, 255);
        _fakeCount = Math.Clamp(settings.FakeCount, 1, 10);
        _fakeSni = settings.FakeSni;
        _clampMss = settings.ClampMss;
        _mssValue = Math.Clamp(settings.MssValue, 64, 1500);
        _multisplit = settings.Multisplit;
        _multisplitSize = Math.Clamp(settings.MultisplitSize, 1, 512);
        _disorder = settings.Disorder;
        _oobSplit = settings.OobSplit;
        _injectFake = settings.InjectFake;
        _fakeFooling = settings.FakeFooling;
        _fakePlacement = settings.FakePlacement;
        _failoverCount = Math.Clamp(settings.FailoverCount, 1, 4);
        _failoverTimeoutMs = Math.Clamp(settings.FailoverTimeoutMs, 300, 8000);
        _splitDelayMs = Math.Clamp(settings.SplitDelayMs, 0, 250);
        _splitPosition = settings.SplitPosition;

        Raise(null);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ поля

    public bool SniSplit
    {
        get => _sniSplit;
        set => Set(ref _sniSplit, value);
    }

    public bool TlsRecordSplit
    {
        get => _tlsRecordSplit;
        set => Set(ref _tlsRecordSplit, value);
    }

    public bool FakeTtl
    {
        get => _fakeTtl;
        set => Set(ref _fakeTtl, value);
    }

    /// <summary>Фейк-пакет уходит после настоящего ClientHello, а не до него.</summary>
    public bool FakeAfter
    {
        get => _fakeAfter;
        set => Set(ref _fakeAfter, value);
    }

    /// <summary>TTL фейка считается по числу прыжков до площадки (аналог autottl у zapret).</summary>
    public bool FakeAutoTtl
    {
        get => _fakeAutoTtl;
        set => Set(ref _fakeAutoTtl, value);
    }

    public int FakeTtlValue
    {
        get => _fakeTtlValue;
        set => Set(ref _fakeTtlValue, Math.Clamp(value, 1, 255));
    }

    /// <summary>Сколько фейк-пакетов слать (аналог repeats у zapret).</summary>
    public int FakeCount
    {
        get => _fakeCount;
        set => Set(ref _fakeCount, Math.Clamp(value, 1, 10));
    }

    /// <summary>Чужое имя в фейк-пакете; пусто — фейк идёт с настоящим именем сайта.</summary>
    public string FakeSni
    {
        get => _fakeSni;
        set => Set(ref _fakeSni, value ?? "");
    }

    public bool ClampMss
    {
        get => _clampMss;
        set => Set(ref _clampMss, value);
    }

    public int MssValue
    {
        get => _mssValue;
        set => Set(ref _mssValue, Math.Clamp(value, 64, 1500));
    }

    public bool Multisplit
    {
        get => _multisplit;
        set => Set(ref _multisplit, value);
    }

    public int MultisplitSize
    {
        get => _multisplitSize;
        set => Set(ref _multisplitSize, Math.Clamp(value, 1, 512));
    }

    /// <summary>Вторая половина разрыва уходит первой (аналог disorder у zapret).</summary>
    public bool Disorder
    {
        get => _disorder;
        set => Set(ref _disorder, value);
    }

    /// <summary>
    /// Отправлять первый байт срочными данными (аналог <c>--oob</c> у ByeDPI). Приём
    /// экспериментальный: часть серверов на него отвечает ошибкой, поэтому он проверяется
    /// кнопкой «Подобрать» как любой другой.
    /// </summary>
    public bool OobSplit
    {
        get => _oobSplit;
        set => Set(ref _oobSplit, value);
    }

    /// <summary>
    /// Слать фейк-пакет настоящим сегментом в тот же поток (нужны права администратора).
    /// Выключено — фейк не отправляется вовсе: отдельным соединением DPI не обмануть.
    /// </summary>
    public bool InjectFake
    {
        get => _injectFake;
        set => Set(ref _injectFake, value);
    }

    /// <summary>Чем «испортить» фейк, чтобы он не дошёл до сервера: TTL, битая сумма, сдвинутый seq.</summary>
    public DpiFooling FakeFooling
    {
        get => _fakeFooling;
        set => Set(ref _fakeFooling, value);
    }

    /// <summary>
    /// Обманка фейка строкой: у списка в разметке тег — имя значения. Сочетание TTL и битой суммы
    /// в списке отмечено тегом «16» (это сумма значений <see cref="DpiFooling"/>).
    /// </summary>
    public string FakeFoolingTag
    {
        get => _fakeFooling == (DpiFooling.Ttl | DpiFooling.BadSum) ? "16" : _fakeFooling.ToString();
        set
        {
            if (!Enum.TryParse<DpiFooling>(value, out var parsed)) return;
            FakeFooling = parsed;
        }
    }

    /// <summary>Где стоит фейк: на месте первых байтов запроса или раньше них (seqovl).</summary>
    public DpiFakePlacement FakePlacement
    {
        get => _fakePlacement;
        set => Set(ref _fakePlacement, value);
    }

    /// <summary>Размещение фейка строкой: у списка в разметке тег — имя значения.</summary>
    public string FakePlacementTag
    {
        get => _fakePlacement.ToString();
        set
        {
            if (!Enum.TryParse<DpiFakePlacement>(value, out var parsed)) return;
            FakePlacement = parsed;
        }
    }

    /// <summary>
    /// Сколько стратегий пробовать на одно соединение, если сервер молчит: движок берёт
    /// подобранные профили сервиса по порядку (как очередь правил в zapret).
    /// </summary>
    public int FailoverCount
    {
        get => _failoverCount;
        set => Set(ref _failoverCount, Math.Clamp(value, 1, 4));
    }

    /// <summary>Сколько ждать ответа, прежде чем пробовать следующую стратегию, мс.</summary>
    public int FailoverTimeoutMs
    {
        get => _failoverTimeoutMs;
        set => Set(ref _failoverTimeoutMs, Math.Clamp(value, 300, 8000));
    }

    /// <summary>Пауза между частями разрыва, мс.</summary>
    public int SplitDelayMs
    {
        get => _splitDelayMs;
        set => Set(ref _splitDelayMs, Math.Clamp(value, 0, 250));
    }

    /// <summary>Позиция разрыва (аналог split-pos у zapret) — выпадающий список окна.</summary>
    public DpiSplitPosition SplitPosition
    {
        get => _splitPosition;
        set => Set(ref _splitPosition, value);
    }

    /// <summary>Та же позиция строкой: у ComboBox в разметке тег — имя значения.</summary>
    public string SplitPositionTag
    {
        get => _splitPosition.ToString();
        set
        {
            if (!Enum.TryParse<DpiSplitPosition>(value, out var parsed)) return;
            SplitPosition = parsed;
        }
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
        Raise(nameof(Settings));
        Changed?.Invoke();
    }
}
