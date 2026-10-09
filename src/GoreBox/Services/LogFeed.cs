using System.Windows.Threading;
using GoreBox.ViewModels;

namespace GoreBox.Services;

/// <summary>
/// Журнал для панели «Логи» на вкладке «Профили»: хранит последние строки и отдаёт их одним текстом.
/// Строки можно добавлять из любого потока; текст обновляется примерно раз в 150 мс, только на UI-потоке.
/// Создавать нужно на UI-потоке (здесь создаётся таймер диспетчера).
/// </summary>
public sealed class LogFeed : ObservableObject
{
    public const int MaxLines = 1000;

    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private string _text = "";

    public LogFeed()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    /// <summary>Весь текст журнала, новые строки внизу.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line.TrimEnd()}";
        lock (_gate)
        {
            _lines.Add(stamped);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            _dirty = true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _dirty = true;
        }
        Flush();
    }

    /// <summary>Текст журнала целиком — для кнопки «Копировать».</summary>
    public string Snapshot()
    {
        lock (_gate) return string.Join(Environment.NewLine, _lines);
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            text = string.Join(Environment.NewLine, _lines);
        }
        Text = text;
    }
}
