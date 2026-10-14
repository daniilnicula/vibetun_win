using System.Collections.ObjectModel;
using System.Windows.Threading;
using VibeTun.ViewModels;

namespace VibeTun.Services;

/// <summary>
/// Журнал для панели «Логи» на вкладке «Профили»: последние строки, каждая — отдельным элементом списка.
/// Одинаковые строки подряд сворачиваются в одну с счётчиком «×N». Новые строки появляются анимацией
/// (см. <see cref="LogLine.IsFresh"/>).
/// Строки можно добавлять из любого потока; список обновляется примерно раз в 150 мс, только на UI-потоке.
/// Создавать нужно на UI-потоке (здесь создаётся таймер диспетчера).
/// </summary>
public sealed class LogFeed : ObservableObject
{
    public const int MaxLines = 1000;

    private readonly object _gate = new();
    private readonly List<(DateTime Time, string Text)> _pending = new();
    private readonly DispatcherTimer _timer;

    public LogFeed()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>Строки журнала по порядку, новые внизу. Меняется только на UI-потоке.</summary>
    public ObservableCollection<LogLine> Lines { get; } = new();

    /// <summary>Строки изменились (UI-поток): панель может прокрутить вниз.</summary>
    public event Action? Updated;

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var item = (DateTime.Now, line.TrimEnd());
        lock (_gate) _pending.Add(item);
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
        Lines.Clear();
        Updated?.Invoke();
    }

    /// <summary>Текст журнала целиком, одна строка на запись (с повторами «×N») — для кнопки «Копировать».</summary>
    public string Snapshot()
    {
        Flush();
        return string.Join(Environment.NewLine, Lines.Select(line => line.Plain));
    }

    private void Flush()
    {
        List<(DateTime Time, string Text)> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = new List<(DateTime Time, string Text)>(_pending);
            _pending.Clear();
        }

        foreach (var (time, text) in batch) Append(time, text);
        Updated?.Invoke();
    }

    private void Append(DateTime time, string text)
    {
        var key = LogFormat.DedupKey(text);
        if (Lines.Count > 0 && Lines[^1].Key == key)
        {
            Lines[^1].Repeat++;
            return;
        }

        Lines.Add(new LogLine(time, text) { IsFresh = true });
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }
}

/// <summary>Одна строка журнала: время, текст и число повторов подряд.</summary>
public sealed class LogLine : ObservableObject
{
    private int _repeat = 1;

    public LogLine(DateTime time, string text)
    {
        Time = time;
        Text = text;
        Key = LogFormat.DedupKey(text);
    }

    public DateTime Time { get; }

    public string Text { get; }

    /// <summary>Ключ для склейки повторов (без номера соединения).</summary>
    public string Key { get; }

    /// <summary>Сколько раз строка подряд повторилась; 1 — без повторов.</summary>
    public int Repeat
    {
        get => _repeat;
        set
        {
            if (SetProperty(ref _repeat, value)) Raise(nameof(Segments));
        }
    }

    /// <summary>Строка только что добавлена: при появлении в панели она выезжает слева. Сбрасывается после анимации.</summary>
    public bool IsFresh { get; set; }

    /// <summary>Цветные фрагменты для отображения.</summary>
    public IReadOnlyList<LogSegment> Segments => LogFormat.Segments(Time, Text, Repeat);

    /// <summary>Строка одним текстом, как в буфере обмена.</summary>
    public string Plain => Repeat > 1
        ? $"[{Time:HH:mm:ss}] {Text} ×{Repeat}"
        : $"[{Time:HH:mm:ss}] {Text}";
}
