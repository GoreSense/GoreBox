using System.Windows.Threading;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Настройки приложения: загрузка, изменение, отложенная запись на диск.</summary>
public sealed class SettingsService
{
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _settings = new();

    public event Action? Changed;

    public SettingsService()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public AppSettings Current => _settings;

    public void Load()
    {
        _settings = Json.Read<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
    }

    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    public void SaveNow()
    {
        try
        {
            Json.Write(AppPaths.SettingsFile, _settings);
        }
        catch { /* ignore */ }
    }
}
