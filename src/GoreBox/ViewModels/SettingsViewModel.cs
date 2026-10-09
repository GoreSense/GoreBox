using System.Diagnostics;
using System.IO;
using GoreBox.Models;
using GoreBox.Services;
using GoreBox.Utils;

namespace GoreBox.ViewModels;

/// <summary>Вкладка «Настройки»: тема, автозапуск, состояние, ядро, служебные действия.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly CoreService _core;
    private readonly IDialogService _dialogs;
    private bool _busy;

    public SettingsViewModel(SettingsService settings, CoreService core, IDialogService dialogs)
    {
        _settings = settings;
        _core = core;
        _dialogs = dialogs;

        InstallCoreCommand = new AsyncRelayCommand(InstallCoreAsync, () => !Busy);
        RestoreBundledCoreCommand = new AsyncRelayCommand(RestoreBundledCoreAsync, () => !Busy);
        ChooseCoreFileCommand = new RelayCommand(_ => ChooseCoreFile());
        CheckCoreCommand = new AsyncRelayCommand(CheckCoreAsync, () => !Busy);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync, () => !Busy);
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as string));
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        ChooseCustomFolderCommand = new RelayCommand(_ => PickCustomStorage());
        RestartNowCommand = new RelayCommand(_ => RestartRequested?.Invoke());
    }

    public event Action<string, bool>? Notify;
    public event Action? ThemeChanged;

    public AsyncRelayCommand InstallCoreCommand { get; }

    /// <summary>Восстановить ядро из вшитой в приложение копии.</summary>
    public AsyncRelayCommand RestoreBundledCoreCommand { get; }
    public RelayCommand ChooseCoreFileCommand { get; }
    public AsyncRelayCommand CheckCoreCommand { get; }
    public AsyncRelayCommand InstallFromFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!SetProperty(ref _busy, value)) return;
            InstallCoreCommand.RaiseCanExecuteChanged();
            CheckCoreCommand.RaiseCanExecuteChanged();
            InstallFromFileCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------- тема
    public bool IsDark
    {
        get => _settings.Current.Theme == ThemeMode.Dark;
        set
        {
            if (value == IsDark) return;
            _settings.Current.Theme = value ? ThemeMode.Dark : ThemeMode.Light;
            _settings.SaveSoon();
            ThemeService.Apply(_settings.Current.Theme);
            Raise();
            Raise(nameof(ThemeLabel));
            ThemeChanged?.Invoke();
        }
    }

    public string ThemeLabel => IsDark ? "Тёмная" : "Светлая";

    // ---------------------------------------------------------------- поведение
    public bool RunAtStartup
    {
        get => _settings.Current.RunAtStartup;
        set
        {
            if (_settings.Current.RunAtStartup == value) return;
            _settings.Current.RunAtStartup = value;
            AutostartService.Apply(value, _settings.Current.StartMinimized);
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool StartMinimized
    {
        get => _settings.Current.StartMinimized;
        set
        {
            if (_settings.Current.StartMinimized == value) return;
            _settings.Current.StartMinimized = value;
            if (_settings.Current.RunAtStartup) AutostartService.Apply(true, value);
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool CloseToTray
    {
        get => _settings.Current.CloseToTray;
        set
        {
            if (_settings.Current.CloseToTray == value) return;
            _settings.Current.CloseToTray = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool RememberState
    {
        get => _settings.Current.RememberState;
        set
        {
            if (_settings.Current.RememberState == value) return;
            _settings.Current.RememberState = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool PreciseLatencyTest
    {
        get => _settings.Current.PreciseLatencyTest;
        set
        {
            if (_settings.Current.PreciseLatencyTest == value) return;
            _settings.Current.PreciseLatencyTest = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool AutoUpdateCore
    {
        get => _settings.Current.AutoUpdateCore;
        set
        {
            if (_settings.Current.AutoUpdateCore == value) return;
            _settings.Current.AutoUpdateCore = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    // ---------------------------------------------------------------- ядро
    public CoreFlavor CoreFlavor
    {
        get => _settings.Current.CoreFlavor;
        set
        {
            if (_settings.Current.CoreFlavor == value) return;
            _settings.Current.CoreFlavor = value;
            _settings.SaveSoon();
            Raise();
            Raise(nameof(IsExtendedFlavor));
        }
    }

    public bool IsExtendedFlavor
    {
        get => _settings.Current.CoreFlavor == CoreFlavor.SingBoxExtended;
        set
        {
            var flavor = value ? CoreFlavor.SingBoxExtended : CoreFlavor.SingBox;
            if (_settings.Current.CoreFlavor == flavor) return;
            _settings.Current.CoreFlavor = flavor;
            _settings.SaveSoon();
            Raise();
            Raise(nameof(CoreFlavor));

            // переключаемся на ядро выбранного варианта, если оно уже есть на диске
            _ = SwitchCoreAsync(flavor);
        }
    }

    private async Task SwitchCoreAsync(CoreFlavor flavor)
    {
        var extended = flavor == CoreFlavor.SingBoxExtended;
        var expected = extended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;
        var bundled = extended ? BundledCores.HasExtended : BundledCores.HasOfficial;

        // что берём: файл варианта в каталоге ядра или путь, который пользователь указал сам
        var source = FindCoreFile(extended, _settings.Current.CorePath);

        // ядра выбранного варианта нет ни там, ни там, но оно вшито в приложение — распаковываем
        if (source is null && bundled)
        {
            IProgress<string> unpack = new Progress<string>(s => Notify?.Invoke(s, false));
            await CoreInstaller.InstallBundledAsync(_core, flavor, unpack);
            source = FindCoreFile(extended, _settings.Current.CorePath);
        }

        // иначе переключатель «Расширенная сборка» молча ничего не делает: предлагаем скачать
        if (source is null)
        {
            var download = _dialogs.Confirm("Ядро не установлено",
                extended
                    ? "Ядро с поддержкой AmneziaWG не найдено на диске. " +
                      $"Скачать его с GitHub (github.com/{CoreInstaller.AwgRepo})?"
                    : "Официальный sing-box не найден на диске. Скачать его с GitHub?",
                "Скачать", danger: false);

            if (!download)
            {
                Notify?.Invoke(extended
                    ? "Расширенное ядро не установлено — AmneziaWG работать не будет"
                    : "Ядро не установлено", true);
                RefreshCoreInfo();
                return;
            }

            Busy = true;
            try
            {
                IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
                var downloaded = flavor == CoreFlavor.SingBoxExtended
                    ? await CoreInstaller.InstallAwgCoreAsync(_core, progress)
                    : await CoreInstaller.InstallLatestAsync(_core, flavor, progress);
                if (downloaded is not { Exists: true })
                {
                    Notify?.Invoke("Не удалось скачать ядро", true);
                    return;
                }
            }
            catch (Exception ex)
            {
                _dialogs.Error("Ошибка загрузки ядра", ex.Message + "\n\n" + CoreInstaller.ManualHint);
                return;
            }
            finally
            {
                Busy = false;
            }

            source = File.Exists(expected) ? expected : _settings.Current.CorePath;
        }

        var info = await _core.DetectAsync(source, flavor);
        if (info is not { Exists: true })
        {
            Notify?.Invoke("Ядро не найдено после установки", true);
            return;
        }

        // строка версии — не гарантия: спрашиваем у самого ядра, понимает ли оно обфускацию
        if (extended) await _core.ProbeAmneziaSupportAsync(force: true);

        _settings.Current.CorePath = info.Path;
        _settings.Current.CoreVersion = info.Version;
        _settings.SaveSoon();
        RefreshCoreInfo();

        Notify?.Invoke(extended
            ? (_core.Info.SupportsAmnezia
                ? "Расширенное ядро включено: AmneziaWG поддерживается"
                : "Ядро переключено, но обфускацию AmneziaWG оно не подтвердило"
                  + (string.IsNullOrWhiteSpace(_core.Info.EndpointError) ? "" : ": " + _core.Info.EndpointError))
            : "Включён официальный sing-box: " + _core.Info.VersionNumber,
            extended && !_core.Info.SupportsAmnezia);
    }

    /// <summary>
    /// Ищем файл ядра нужного варианта: сначала стандартное имя в каталоге ядра, затем путь
    /// из настроек (если по имени он подходит под выбранный вариант).
    /// </summary>
    private static string? FindCoreFile(bool extended, string? configured)
    {
        var expected = extended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;
        if (File.Exists(expected)) return expected;

        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured)) return null;

        var name = Path.GetFileName(configured);
        var looksExtended = name.Contains("extended", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("awg", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("-lx", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("amnezia", StringComparison.OrdinalIgnoreCase);
        return looksExtended == extended ? configured : null;
    }

    public string CoreVersionText => _core.Info.Exists
        ? _core.Info.Version
        : "Ядро не найдено";

    public string CorePathText => _core.Info.Exists ? _core.Info.Path : CoreInstaller.ManualHint;

    /// <summary>Есть ли в этой сборке приложения вшитые ядра.</summary>
    public bool HasBundledCores => BundledCores.HasOfficial || BundledCores.HasExtended;

    public string BundledCoresText => (BundledCores.HasOfficial, BundledCores.HasExtended) switch
    {
        (true, true) => "Ядра (sing-box и sing-box-extended) вшиты в приложение — интернет при первом запуске не нужен.",
        (true, false) => "Ядро sing-box вшито в приложение.",
        (false, true) => "Расширенное ядро (AmneziaWG) вшито в приложение.",
        _ => "Вшитых ядер нет: приложение скачает ядро с GitHub."
    };

    public bool CoreMissing => !_core.Info.Exists;

    public string CoreSupportText
    {
        get
        {
            if (!_core.Info.Exists) return "Установите ядро, чтобы включить прокси.";

            var parts = new List<string>
            {
                $"правила 1.11+: {(_core.Info.SupportsModernRules ? "да" : "нет")}",
                $"endpoints: {(_core.Info.SupportsEndpoints ? "да" : "нет")}",
                $"AmneziaWG: {(_core.Info.SupportsAnyAwg ? "да" : "нет")}" +
                (_core.Info.AmneziaProbed is null ? " (по версии, нажмите «Проверить»)" : " (проверено)"),
                $"AWG 3.1: {(_core.Info.SupportsAwgTrailers ? "да" : "нет")}"
            };
            var text = string.Join(" · ", parts);

            // от схемы конфига зависит, какие параметры обфускации вообще удастся передать
            if (_core.Info.SupportsAwgFlat)
                text += "\nСхема: параметры AmneziaWG прямо в эндпоинте wireguard (форк sing-box-lx) — " +
                        "доступны все, включая RandomTrailers/DisableCookies из AWG 3.1.";
            else if (_core.Info.SupportsAwgEndpoint)
                text += "\nСхема: отдельный эндпоинт type=\"awg\" (форк Amnezia) — доступны все параметры, включая AWG 3.1.";
            else if (_core.Info.SupportsAmnezia)
                text += "\nСхема: эндпоинт wireguard с вложенным блоком amnezia (sing-box-extended) — AWG до 3.0, " +
                        "RandomTrailers/DisableCookies это ядро отбрасывает.";

            if (!string.IsNullOrWhiteSpace(_core.Info.EndpointError))
                text += "\nЭндпоинт WireGuard в этом ядре не собирается: " + _core.Info.EndpointError;

            return text;
        }
    }

    // ---------------------------------------------------------------------- SSH

    /// <summary>
    /// Логин для вкладки SSH. Если заполнен, используется вместо «user@» из адреса.
    /// Это те же поля, в которые вкладка SSH запоминает данные при подключении.
    /// </summary>
    public string SshLogin
    {
        get => _settings.Current.SshUser;
        set
        {
            var v = value ?? "";
            if (_settings.Current.SshUser == v) return;
            _settings.Current.SshUser = v;
            _settings.SaveSoon();
        }
    }

    /// <summary>Пароль для вкладки SSH (в поле видны только звёздочки). Если пусто, вкладка спросит при подключении.</summary>
    public string SshPassword
    {
        get => _settings.Current.SshPassword;
        set
        {
            var v = value ?? "";
            if (_settings.Current.SshPassword == v) return;
            _settings.Current.SshPassword = v;
            _settings.SaveSoon();
        }
    }

    // --------------------------------------------------- место хранения данных

    /// <summary>Нужен перезапуск, чтобы выбранное место хранения вступило в силу.</summary>
    public event Action? RestartRequested;

    public RelayCommand ChooseCustomFolderCommand { get; }
    public RelayCommand RestartNowCommand { get; }

    public bool StorageIsLocal
    {
        get => StorageLocation.Config.Mode == StorageMode.Local;
        set
        {
            if (value) ApplyStorage(StorageMode.Local, null);
            else RaiseStorage();
        }
    }

    public bool StorageIsExe
    {
        get => StorageLocation.Config.Mode == StorageMode.Exe;
        set
        {
            if (value) ApplyStorage(StorageMode.Exe, null);
            else RaiseStorage();
        }
    }

    public bool StorageIsCustom
    {
        get => StorageLocation.Config.Mode == StorageMode.Custom;
        set
        {
            if (value) PickCustomStorage();
            else RaiseStorage();
        }
    }

    public bool IsCustomStorage => StorageLocation.Config.Mode == StorageMode.Custom;

    public string LocalDataDirText => StorageLocation.LocalDataDir;

    public string ExeDataDirText => StorageLocation.ExeDataDir;

    public string CustomDataDirText => string.IsNullOrWhiteSpace(StorageLocation.Config.CustomPath)
        ? "папка ещё не выбрана"
        : StorageLocation.Config.CustomPath!;

    /// <summary>Где данные лежат сейчас.</summary>
    public string CurrentDataRootText => "Сейчас данные хранятся в: " + AppPaths.DataRoot;

    /// <summary>Выбрано другое место: оно применится после перезапуска.</summary>
    public bool StoragePending => StorageLocation.IsPending;

    public string StoragePendingText =>
        "После перезапуска данные будут храниться в: " + StorageLocation.DesiredRoot +
        ". Если там ещё нет данных, они будут скопированы.";

    public bool HasStorageNotice => !string.IsNullOrEmpty(StorageLocation.FallbackReason);

    public string StorageNoticeText => StorageLocation.FallbackReason ?? "";

    private void PickCustomStorage()
    {
        var start = StorageLocation.Config.CustomPath;
        var picked = _dialogs.PickFolder("Папка для данных GoreBox",
            string.IsNullOrWhiteSpace(start) ? AppPaths.DataRoot : start);
        if (string.IsNullOrWhiteSpace(picked))
        {
            RaiseStorage();   // отмена: переключатель возвращаем к прежнему варианту
            return;
        }
        ApplyStorage(StorageMode.Custom, picked);
    }

    private void ApplyStorage(StorageMode mode, string? customPath)
    {
        var error = StorageLocation.TryChange(mode, customPath);
        RaiseStorage();
        if (error is not null)
        {
            _dialogs.Error("Место хранения не изменено", error);
            return;
        }
        if (!StorageLocation.IsPending) return;

        Notify?.Invoke("Место хранения выбрано. Оно изменится после перезапуска GoreBox.", false);
        var restartNow = _dialogs.Confirm(
            "Перезапуск",
            "Новое место хранения вступит в силу после перезапуска GoreBox.\n\nПерезапустить сейчас?",
            "Перезапустить",
            danger: false);
        if (restartNow) RestartRequested?.Invoke();
    }

    private void RaiseStorage()
    {
        Raise(nameof(StorageIsLocal));
        Raise(nameof(StorageIsExe));
        Raise(nameof(StorageIsCustom));
        Raise(nameof(IsCustomStorage));
        Raise(nameof(CustomDataDirText));
        Raise(nameof(CurrentDataRootText));
        Raise(nameof(StoragePending));
        Raise(nameof(StoragePendingText));
        Raise(nameof(HasStorageNotice));
        Raise(nameof(StorageNoticeText));
    }

    /// <summary>Подтянуть переключатель варианта ядра после его программной смены.</summary>
    public void RaiseCoreFlavor()
    {
        Raise(nameof(CoreFlavor));
        Raise(nameof(IsExtendedFlavor));
    }

    public void RefreshCoreInfo()
    {
        Raise(nameof(CoreVersionText));
        Raise(nameof(CorePathText));
        Raise(nameof(CoreMissing));
        Raise(nameof(CoreSupportText));
    }

    /// <summary>Обновить зависимые свойства темы (вызывается из главного VM).</summary>
    public void RaiseTheme()
    {
        Raise(nameof(IsDark));
        Raise(nameof(ThemeLabel));
    }

    /// <summary>Публичная обёртка установки ядра (для кнопки в статус-баре).</summary>
    public Task InstallCorePublicAsync() => InstallCoreAsync();

    /// <summary>Переустановить ядро из вшитой в приложение копии.</summary>
    private async Task RestoreBundledCoreAsync()
    {
        Busy = true;
        try
        {
            IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
            var flavor = _settings.Current.CoreFlavor;
            var info = await CoreInstaller.InstallBundledAsync(_core, flavor, progress);

            if (info is { Exists: true })
            {
                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke("Ядро восстановлено из приложения: " + info.Version, false);
            }
            else
            {
                Notify?.Invoke("Вшитого ядра для выбранного варианта нет — скачайте с GitHub", true);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error("Не удалось восстановить ядро", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task CheckCoreAsync()
    {
        Busy = true;
        try
        {
            await _core.DetectAsync(_settings.Current.CorePath);
            // точная проверка AmneziaWG: спрашиваем у самого ядра, а не у строки версии
            if (_core.Info.SupportsEndpoints) await _core.ProbeAmneziaSupportAsync(force: true);

            _settings.Current.CoreVersion = _core.Info.Version;
            _settings.SaveSoon();
            RefreshCoreInfo();
            Notify?.Invoke(_core.Info.Exists ? "Ядро найдено: " + _core.Info.Version : "Ядро не найдено", !_core.Info.Exists);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallCoreAsync()
    {
        Busy = true;
        try
        {
            IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
            progress.Report("Загрузка ядра с GitHub…");
            var flavor = _settings.Current.CoreFlavor;
            var info = flavor == CoreFlavor.SingBoxExtended
                ? await CoreInstaller.InstallAwgCoreAsync(_core, progress)
                : await CoreInstaller.InstallLatestAsync(_core, flavor, progress);
            if (info is { Exists: true })
            {
                if (flavor == CoreFlavor.SingBoxExtended) await _core.ProbeAmneziaSupportAsync(force: true);

                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke("Ядро установлено: " + info.Version, false);
            }
            else
            {
                Notify?.Invoke("Не удалось установить ядро", true);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error("Ошибка загрузки ядра", ex.Message + "\n\n" + CoreInstaller.ManualHint);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallFromFileAsync()
    {
        var path = _dialogs.OpenFile("Архив ядра (*.zip)|*.zip|Исполняемый файл (*.exe)|*.exe|Все файлы (*.*)|*.*");
        if (path is null) return;

        Busy = true;
        try
        {
            CoreInfo? info;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(AppPaths.CoreDir);
                File.Copy(path, AppPaths.CoreBinary, true);
                info = await _core.DetectAsync(AppPaths.CoreBinary);
            }
            else
            {
                info = await CoreInstaller.InstallFromZipAsync(_core, path);
            }

            if (info is { Exists: true })
            {
                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke("Ядро установлено: " + info.Version, false);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error("Не удалось установить ядро", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void ChooseCoreFile()
    {
        var path = _dialogs.OpenFile("Ядро sing-box (sing-box.exe)|sing-box.exe;*.exe|Все файлы (*.*)|*.*");
        if (path is null) return;

        _settings.Current.CorePath = path;
        _settings.SaveSoon();
        _ = CheckCoreAsync();
    }

    private void OpenFolder(string? kind)
    {
        var path = kind switch
        {
            "core" => AppPaths.CoreDir,
            "logs" => AppPaths.LogsDir,
            _ => AppPaths.DataRoot
        };
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Error("Не удалось открыть папку", ex.Message);
        }
    }

    private void ResetSettings()
    {
        if (!_dialogs.Confirm("Сбросить настройки", "Вернуть все настройки приложения к значениям по умолчанию?",
                "Сбросить")) return;

        var fresh = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties())
            prop.SetValue(_settings.Current, prop.GetValue(fresh));
        _settings.SaveSoon();
        ThemeService.Apply(_settings.Current.Theme);
        AutostartService.Apply(false, false);
        Notify?.Invoke("Настройки сброшены", false);
        ThemeChanged?.Invoke();
        RefreshCoreInfo();
        Raise(nameof(IsDark));
        Raise(nameof(ThemeLabel));
    }
}
