using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Threading;
using GoreBox.Models;
using GoreBox.Services;
using GoreBox.Utils;

namespace GoreBox.ViewModels;

/// <summary>Корневой ViewModel: состояние приложения, вкладки, запуск/остановка прокси, трей.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ClashApiClient _api = new();
    private readonly DispatcherTimer _statsTimer;

    private string _activeTab = "profiles";
    private bool _isProxyOn;
    private bool _busy;
    private string _statusMessage = "";
    private bool _statusIsError;
    private int _activePing = -1;
    private string _trafficText = "—";
    private int _connectionCount;
    private readonly DateTime _startedAt = DateTime.Now;
    private DateTime? _proxyStartedAt;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        ProfileStore = new ProfileStore();
        RouteStore = new RouteStore();
        Latency = new LatencyService();
        SystemProxy = new SystemProxyService();
        Core = new CoreService();
        Tray = new TrayService();
        Logs = new LogFeed();

        Profiles = new ProfilesViewModel(ProfileStore, Latency, _dialogs, Logs);
        Routing = new RoutingViewModel(RouteStore, _dialogs);
        Preferences = new SettingsViewModel(App.Settings, Core, _dialogs);

        Profiles.ConnectRequested += profile => _ = ConnectToAsync(profile);
        Profiles.Notify += (text, error) => ShowStatus(text, error);
        Routing.Notify += (text, error) => ShowStatus(text, error);
        Routing.SettingsChanged += () => { /* маршрутизация перечитывается при следующем запуске */ };
        Preferences.Notify += (text, error) => ShowStatus(text, error);
        Preferences.ThemeChanged += () => Raise(nameof(IsDark));
        Preferences.RestartRequested += () => RestartRequested?.Invoke();

        // BeginInvoke, а не Invoke: событие может прийти из потока пула, который в этот момент держит
        // блокировку процесса ядра (обработчик Exited). Ждать UI-поток там нельзя: получится взаимная блокировка.
        Core.StateChanged += _ =>
        {
            try { App.Current.Dispatcher.BeginInvoke(new Action(UpdateStateText)); }
            catch { /* приложение завершается */ }
        };
        Core.LogReceived += line => AppendLog(line);
        AppLog.Written += AppendLog;   // SSH и прочие части приложения пишут в тот же журнал

        Tray.OpenRequested += () => ShowWindowRequested?.Invoke();
        Tray.ExitRequested += () => ExitRequested?.Invoke();

        ToggleProxyCommand = new AsyncRelayCommand(ToggleProxyAsync, () => !Busy);
        ToggleThemeCommand = new RelayCommand(() => IsDark = !IsDark);
        TestPingCommand = new AsyncRelayCommand(TestActivePingAsync, () => ActiveProfile is not null && !Busy);
        InstallCoreCommand = new AsyncRelayCommand(InstallCoreAsync, () => !Busy);
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        TelegramProxyCommand = new RelayCommand(_ => OpenTelegramProxy(), _ => ActiveProfile?.IsTelegramOnly == true);
        ShowLogCommand = new RelayCommand(_ => _dialogs.ShowCoreLog(Core.RecentLogText()));
        CopyDiagnosticsCommand = new RelayCommand(_ => CopyDiagnostics());

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += async (_, _) => await UpdateStatsAsync();

        UpdateStateText();
    }

    public ProfileStore ProfileStore { get; }
    public RouteStore RouteStore { get; }
    public LatencyService Latency { get; }
    public SystemProxyService SystemProxy { get; }
    public CoreService Core { get; }
    public TrayService Tray { get; }

    /// <summary>Журнал приложения и ядра для панели «Логи» на вкладке «Профили».</summary>
    public LogFeed Logs { get; }

    public ProfilesViewModel Profiles { get; }
    public RoutingViewModel Routing { get; }
    public SettingsViewModel Preferences { get; }

    public AsyncRelayCommand ToggleProxyCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public AsyncRelayCommand TestPingCommand { get; }
    public AsyncRelayCommand InstallCoreCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand TelegramProxyCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    /// <summary>Перезапуск приложения (нужен после смены места хранения данных).</summary>
    public event Action? RestartRequested;

    // --------------------------------------------------------------- состояние
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            // при перестановке вкладок WPF на миг сбрасывает выбор в null — такое значение игнорируем
            if (string.IsNullOrEmpty(value)) return;
            if (!SetProperty(ref _activeTab, value)) return;
            App.Settings.Current.LastTab = value;
            App.Settings.SaveSoon();
        }
    }

    public bool IsDark
    {
        get => App.Settings.Current.Theme == ThemeMode.Dark;
        set
        {
            if (value == IsDark) return;
            App.Settings.Current.Theme = value ? ThemeMode.Dark : ThemeMode.Light;
            App.Settings.SaveSoon();
            ThemeService.Apply(App.Settings.Current.Theme);
            Raise();
            Raise(nameof(ThemeIcon));
            Preferences.RaiseTheme();
        }
    }

    public string ThemeIcon => IsDark ? "🌙" : "☀";

    public bool IsProxyOn
    {
        get => _isProxyOn;
        private set
        {
            if (!SetProperty(ref _isProxyOn, value)) return;
            Raise(nameof(ProxyToggleText));
            Raise(nameof(StatusBrushKey));
        }
    }

    public string ProxyToggleText => IsProxyOn ? "Отключить" : "Подключить";

    /// <summary>Активный профиль — Telegram-прокси (MTProto).</summary>
    public bool IsTelegramProfile => ActiveProfile?.IsTelegramOnly == true;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            ToggleProxyCommand.RaiseCanExecuteChanged();
            InstallCoreCommand.RaiseCanExecuteChanged();
            TestPingCommand.RaiseCanExecuteChanged();
        }
    }

    public ProxyProfile? ActiveProfile => _activeProfile;
    private ProxyProfile? _activeProfile;

    public string ActiveProfileName => ActiveProfile?.Name ?? "Профиль не выбран";
    public string ActiveProfileSubtitle
    {
        get
        {
            if (ActiveProfile is null) return "Добавьте профиль во вкладке «Профили»";
            var route = Routing.ActiveProfile;
            return $"{ActiveProfile.ProtocolLabel} · {ActiveProfile.AddressLabel} · {route.ListenAddress}:{route.ListenPort}";
        }
    }

    public string StateText { get; private set; } = "Отключено";
    public string StatusBrushKey { get; private set; } = "TextMuted";

    public int ActivePing
    {
        get => _activePing;
        private set
        {
            if (SetProperty(ref _activePing, value)) Raise(nameof(ActivePingText));
        }
    }

    public string ActivePingText => _activePing < 0 ? "— ms" : _activePing == 0 ? "<1 ms" : $"{_activePing} ms";

    public string TrafficText
    {
        get => _trafficText;
        private set => SetProperty(ref _trafficText, value);
    }

    public string ConnectionCountText => _connectionCount == 0 ? "—" : _connectionCount + " соединений";

    public string UptimeText => _proxyStartedAt is null
        ? "—"
        : (DateTime.Now - _proxyStartedAt.Value).ToString(@"hh\:mm\:ss");

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    // ------------------------------------------------------------------- старт
    public async Task InitializeAsync()
    {
        App.Settings.Load();
        AppPaths.EnsureCreated();
        ThemeService.Apply(App.Settings.Current.Theme);
        Raise(nameof(IsDark));
        Raise(nameof(ThemeIcon));

        Routing.Load();
        Profiles.Load();
        SystemProxy.CleanupStale(Routing.Profiles.Select(p => p.ListenPort));
        Preferences.RefreshCoreInfo();
        _activeProfile = ActiveProfileFromSettings();
        Profiles.SetActive(_activeProfile?.Id);
        Raise(nameof(IsTelegramProfile));
        ActivePing = _activeProfile?.LastPingMs ?? -1;
        Raise(nameof(ActiveProfileName));
        Raise(nameof(ActiveProfileSubtitle));

        // ядра вшиты в приложение — при первом запуске просто распаковываем выбранный вариант
        try
        {
            if (BundledCores.HasOfficial || BundledCores.HasExtended)
                await BundledCores.EnsureExtractedAsync(App.Settings.Current.CoreFlavor);
        }
        catch
        {
            /* не критично: ядро можно установить вручную в настройках */
        }

        await Core.DetectAsync(App.Settings.Current.CorePath, App.Settings.Current.CoreFlavor);
        Preferences.RefreshCoreInfo();
        UpdateStateText();
        UpdateTrayStatus();

        // первая строка app.log: по ней видно, какая сборка приложения и какое ядро запущены
        AppendLog($"GoreBox {AppInfo.Full} · ядро: " +
                  (Core.Info.Exists ? Core.Info.Path + ", " + Core.Info.Version : "не найдено"));

        // что сообщить о месте хранения данных: перенос или причину, по которой взята другая папка
        if (App.StartupNotice is { } storageNotice)
            ShowStatus(storageNotice, StorageLocation.FallbackReason is not null);

        if (!Core.Info.Exists)
            ShowStatus("Ядро не найдено — установите его в настройках", true);

        if (App.Settings.Current.RememberState && App.Settings.Current.ProxyWasActive && _activeProfile is not null)
        {
            ShowStatus("Восстановление подключения…", false);
            await StartProxyAsync();
        }
    }

    private ProxyProfile? ActiveProfileFromSettings()
    {
        var id = App.Settings.Current.ActiveProfileId;
        return Profiles.Items.FirstOrDefault(p => p.Id == id)?.Profile
               ?? Profiles.Items.FirstOrDefault()?.Profile;
    }

    // ------------------------------------------------------------------- прокси
    private async Task ToggleProxyAsync()
    {
        if (IsProxyOn) await StopProxyAsync();
        else await StartProxyAsync();
    }

    private async Task ConnectToAsync(ProxyProfile profile)
    {
        _activeProfile = profile;
        Profiles.SetActive(profile.Id);
        Raise(nameof(ActiveProfileName));
        Raise(nameof(ActiveProfileSubtitle));
        Raise(nameof(IsTelegramProfile));
        TelegramProxyCommand.RaiseCanExecuteChanged();
        App.Settings.Current.ActiveProfileId = profile.Id;
        App.Settings.SaveSoon();

        if (IsProxyOn)
        {
            await StopProxyAsync();
            await StartProxyAsync();
        }
        else
        {
            await StartProxyAsync();
        }
    }

    private async Task StartProxyAsync()
    {
        if (Busy) return;
        if (_activeProfile is null)
        {
            ShowStatus("Сначала выберите профиль", true);
            return;
        }

        if (_activeProfile.IsTelegramOnly)
        {
            ShowStatus("MTProto работает только внутри Telegram — нажмите «Открыть в Telegram»", true);
            return;
        }

        var route = Routing.ActiveProfile;

        if (!Core.Info.Exists)
        {
            ShowStatus("Ядро не найдено — установите его в настройках", true);
            ActiveTab = "settings";
            return;
        }

        if (route.TunEnabled && !AdminHelper.IsElevated)
        {
            var agree = _dialogs.ConfirmElevate("Требуются права администратора",
                "Для TUN-режима и маршрутизации приложений нужно перезапустить приложение с правами администратора. " +
                "Перезапустить сейчас?");
            if (agree)
            {
                App.Settings.Current.ProxyWasActive = true;
                App.Settings.SaveNow();
                if (!AdminHelper.RestartElevated())
                    ShowStatus("Не удалось перезапустить с правами администратора", true);
                else
                    ExitRequested?.Invoke();
            }
            return;
        }

        Busy = true;
        try
        {
            // AmneziaWG требует расширенной сборки ядра: проверяем и при необходимости ставим её
            // до сборки конфига, иначе параметры обфускации молча потеряются.
            if (!await EnsureCoreForProfileAsync(_activeProfile)) return;

            Routing.Persist();

            var clashPort = FindFreePort();
            var build = SingBoxConfigBuilder.Build(_activeProfile, route, Routing.ActiveSet,
                App.Settings.Current, Core.Info, clashPort, Path.Combine(AppPaths.LogsDir, "core.log"));

            foreach (var warning in build.Warnings) AppendLog("Конфиг: " + warning);

            if (build.Errors.Count > 0)
            {
                foreach (var problem in build.Errors) AppendLog("Конфиг (ошибка): " + problem);
                ShowStatus(build.Errors[0], true);
                _dialogs.Error("Подключение невозможно", string.Join("\n\n", build.Errors));
                return;
            }

            var check = await Core.ValidateAsync(build.Json);
            if (!check.Ok)
            {
                ShowStatus("Ядро отклонило конфигурацию", true);
                _dialogs.ShowCoreLog(check.Message);
                return;
            }

            if (route.UseSystemProxy) SystemProxy.CaptureSnapshot();

            ShowStatus("Запуск ядра…", false);
            await Core.StartAsync(build.Json, clashPort);

            if (Core.State != ConnectionState.Running)
            {
                ShowStatus("Ядро не запустилось", true);
                _dialogs.ShowCoreLog(Core.RecentLogText());
                return;
            }

            if (route.UseSystemProxy)
                SystemProxy.Enable(route.ListenAddress, route.ListenPort);

            IsProxyOn = true;
            _proxyStartedAt = DateTime.Now;
            App.Settings.Current.ProxyWasActive = true;
            App.Settings.Current.ActiveProfileId = _activeProfile.Id;
            App.Settings.SaveSoon();

            _statsTimer.Start();
            UpdateStateText();
            UpdateTrayStatus();
            var statusNote = build.Warnings.Count > 0 ? build.Warnings[0] : null;
            if (statusNote is { Length: > 160 }) statusNote = statusNote[..160] + "…";
            ShowStatus(statusNote is null
                ? $"Подключено: {_activeProfile.Name}"
                : $"Подключено: {_activeProfile.Name} — {statusNote}",
                statusNote is not null);
            _ = VerifyTunnelAsync();
        }
        catch (NotSupportedException ex)
        {
            ShowStatus(ex.Message, true);
        }
        catch (Exception ex)
        {
            ShowStatus("Ошибка запуска: " + ex.Message, true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// AmneziaWG работает только на расширенной сборке ядра. Если параметры обфускации отбросить,
    /// сервер не примет хендшейк: прокси покажет «Подключено», а интернет работать не будет.
    /// Поэтому ядро ищем на диске, распаковываем из приложения или — с согласия пользователя —
    /// скачиваем, и обязательно перепроверяем, что обфускация действительно поддерживается.
    /// Возвращает false, если запускать профиль нельзя.
    /// </summary>
    private async Task<bool> EnsureCoreForProfileAsync(ProxyProfile profile)
    {
        var needsAmnezia = profile.Node?.HasAwgParams == true ||
                           profile.Protocol.Equals(ProtocolIds.AmneziaWg, StringComparison.OrdinalIgnoreCase);
        if (!needsAmnezia) return true;

        // точный ответ даёт проверка самого ядра, а не строка версии
        await Core.ProbeAmneziaSupportAsync();

        // RandomTrailers/DisableCookies (AmneziaWG 3.1) понимает только форк с плоской схемой
        // (sing-box-lx) или эндпоинт type="awg" (форк Amnezia): расширенная сборка shtorm-7 такие
        // параметры молча отбрасывает, сервер с 3.1 шлёт ответ с хвостом, клиент его не принимает —
        // туннель поднимается и тут же умирает. Значит SupportsAmnezia для такого профиля мало.
        var needsAwg31 = profile.Node?.HasAwgExtendedOnlyParams == true;
        if (Core.Info.SupportsAmnezia && (!needsAwg31 || Core.Info.SupportsAwgTrailers)) return true;

        var reason = Core.Info.SupportsAmnezia
            ? "текущее ядро не поддерживает RandomTrailers/DisableCookies (AmneziaWG 3.1)"
            : string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? "текущее ядро не поддерживает обфускацию AmneziaWG"
                : Core.Info.EndpointError!;

        // 1) расширенная сборка уже лежит в каталоге ядра
        var alreadyExtendedFile =
            string.Equals(Core.Info.Path, AppPaths.CoreExtendedBinary, StringComparison.OrdinalIgnoreCase);
        if (!alreadyExtendedFile ||
            (needsAwg31 && !Core.Info.SupportsAwgTrailers && !Core.Info.SupportsAwgEndpoint))
        {
            if (!alreadyExtendedFile && File.Exists(AppPaths.CoreExtendedBinary))
                ShowStatus("Переключение на расширенное ядро (AmneziaWG)…", false);

            // 2) если файла нет, но ядро вшито в приложение — распаковываем его
            if (!File.Exists(AppPaths.CoreExtendedBinary) && BundledCores.HasExtended)
            {
                ShowStatus("Подготовка расширенного ядра (AmneziaWG)…", false);
                try
                {
                    await BundledCores.EnsureExtractedAsync(CoreFlavor.SingBoxExtended);
                }
                catch (Exception ex)
                {
                    reason = "не удалось распаковать встроенное ядро: " + ex.Message;
                }
            }

            if (await TrySwitchToExtendedAsync(needsAwg31))
            {
                RememberCoreSwitch();
                return true;
            }

            reason = string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? "расширенное ядро не подтвердило поддержку AmneziaWG"
                : Core.Info.EndpointError;
        }

        // 3) скачиваем с GitHub
        var ask = _dialogs.Confirm("Нужно ядро с поддержкой AmneziaWG",
            "AmneziaWG не работает на официальном sing-box: без параметров обфускации (Jc, S1–S4, H1–H4, I1–I5) " +
            "сервер не примет хендшейк, и прокси окажется «подключён, но ничего не грузит».\n\n" +
            "Сейчас: " + reason + ".\n\n" +
            $"Скачать ядро с AmneziaWG (github.com/{CoreInstaller.AwgRepo})?",
            "Скачать", danger: false);

        if (!ask)
        {
            ShowStatus("Без расширенного ядра AmneziaWG не подключить", true);
            return false;
        }

        ShowStatus("Скачивание ядра с AmneziaWG…", false);
        try
        {
            IProgress<string> progress = new Progress<string>(text => ShowStatus(text, false));
            var info = await CoreInstaller.InstallAwgCoreAsync(Core, progress);
            if (info is { Exists: true } && await TrySwitchToExtendedAsync(needsAwg31))
            {
                RememberCoreSwitch();
                ShowStatus("Расширенное ядро установлено: " + Core.Info.VersionNumber, false);
                return true;
            }

            reason = string.IsNullOrWhiteSpace(Core.Info.EndpointError)
                ? "скачанное ядро не подтвердило поддержку AmneziaWG"
                : Core.Info.EndpointError;
        }
        catch (Exception ex)
        {
            reason = "не удалось скачать ядро: " + ex.Message;
        }

        // needsAwg31 уже посчитан выше: RandomTrailers/DisableCookies из профиля
        _dialogs.Error("AmneziaWG недоступен",
            reason + "\n\n" + CoreInstaller.ManualHint +
            $"\n\nФорк с AmneziaWG 3.x: https://github.com/{CoreInstaller.AwgRepo}/releases " +
            $"\nРасширенная сборка: https://github.com/{CoreInstaller.ExtendedRepo}/releases " +
            "(архив sing-box-…-windows-amd64.zip, файл положить в каталог ядра как sing-box-extended.exe)." +
            (needsAwg31
                ? "\n\nВ профиле включены RandomTrailers/DisableCookies (AmneziaWG 3.1). Их понимает форк " +
                  $"{CoreInstaller.AwgRepo} (сборка с тегом with_awg) и форк Amnezia " +
                  "(https://github.com/hoaxisr/amnezia-box, эндпоинт type=\"awg\" — только сборки под Linux). " +
                  "Если скачать не удалось, выключите RandomTrailers на сервере и пересохраните профиль."
                : ""));
        return false;
    }

    /// <summary>
    /// Переход на расширенную сборку ядра с проверкой, что обфускация в ней действительно есть.
    /// Если что-то пошло не так, возвращаем прежнее ядро — иначе приложение осталось бы вообще без ядра.
    /// </summary>
    /// <param name="requireAwg31">
    /// Профилю нужны RandomTrailers/DisableCookies: ядро, которое их не передаёт, не подходит,
    /// даже если блок "amnezia" оно разбирает.
    /// </param>
    private async Task<bool> TrySwitchToExtendedAsync(bool requireAwg31 = false)
    {
        if (!File.Exists(AppPaths.CoreExtendedBinary)) return false;

        var previous = Core.Info;
        await Core.DetectAsync(AppPaths.CoreExtendedBinary, CoreFlavor.SingBoxExtended);

        if (!Core.Info.Exists ||
            !string.Equals(Core.Info.Path, AppPaths.CoreExtendedBinary, StringComparison.OrdinalIgnoreCase))
        {
            await Core.DetectAsync(previous.Path, previous.Flavor());
            return false;
        }

        await Core.ProbeAmneziaSupportAsync(force: true);
        if (Core.Info.SupportsAmnezia && (!requireAwg31 || Core.Info.SupportsAwgTrailers)) return true;

        await Core.DetectAsync(previous.Path, previous.Flavor());
        return false;
    }

    private void RememberCoreSwitch()
    {
        App.Settings.Current.CoreFlavor = CoreFlavor.SingBoxExtended;
        App.Settings.Current.CorePath = Core.Info.Path;
        App.Settings.Current.CoreVersion = Core.Info.Version;
        App.Settings.SaveSoon();
        Preferences.RaiseCoreFlavor();
        Preferences.RefreshCoreInfo();
        ShowStatus("Для AmneziaWG включена расширенная сборка ядра", false);
    }

    public async Task StopProxyAsync(bool keepStateFlag = false)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            _statsTimer.Stop();

            // сначала возвращаем прежний системный прокси: интернет работает, даже если ядро не ответит
            await RestoreSystemProxyAsync();

            try
            {
                await Core.StopAsync();
            }
            catch (Exception ex)
            {
                AppendLog("Ошибка остановки ядра: " + ex.Message);
            }

            IsProxyOn = false;
            _proxyStartedAt = null;
            TrafficText = "—";
            _connectionCount = 0;
            Raise(nameof(ConnectionCountText));
            Raise(nameof(UptimeText));

            if (!keepStateFlag)
            {
                App.Settings.Current.ProxyWasActive = false;
                App.Settings.SaveSoon();
            }

            UpdateStateText();
            UpdateTrayStatus();
            ShowStatus("Прокси отключён", false);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// Вернуть прежние настройки системного прокси. Выполняется в фоновом потоке, чтобы вызовы WinINet
    /// не задерживали окно.
    /// </summary>
    private Task RestoreSystemProxyAsync() => Task.Run(() =>
    {
        try
        {
            if (SystemProxy.IsEnabled) SystemProxy.RestoreSnapshot();
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось вернуть системный прокси: " + ex.Message);
        }
    });

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ------------------------------------------------------------------ метрики
    private async Task UpdateStatsAsync()
    {
        if (!IsProxyOn)
        {
            _statsTimer.Stop();
            return;
        }

        var traffic = await _api.GetTrafficAsync(Core.ClashApiPort);
        if (traffic is not null)
            TrafficText = $"↑ {Text.Bytes(traffic.Upload)}   ↓ {Text.Bytes(traffic.Download)}";

        _connectionCount = await _api.GetActiveConnectionsAsync(Core.ClashApiPort);
        Raise(nameof(ConnectionCountText));
        Raise(nameof(UptimeText));
    }

    /// <summary>
    /// «Подключено» ещё не значит, что интернет работает: туннель поднимается даже тогда,
    /// когда сервер отвергает хендшейк (типично для AmneziaWG без параметров обфускации).
    /// Проверяем реальный запрос через ядро и, если он не прошёл, честно сообщаем об этом.
    /// </summary>
    private async Task VerifyTunnelAsync()
    {
        try
        {
            await Task.Delay(800);
            if (!IsProxyOn || Core.State != ConnectionState.Running) return;

            // первый хендшейк туннеля может занять секунды — таймаут проверки берём с запасом
            if (await Core.MeasureDelayAsync(timeoutMs: 12000) > 0 || await TryHttpThroughInboundAsync())
            {
                await RefreshActivePingAsync();
                return;
            }

            if (_activeProfile is not null)
                ActivePing = await Latency.MeasureAsync(_activeProfile);

            ReportDeadTunnel();
        }
        catch
        {
            /* диагностика не должна ломать подключение */
        }
    }

    /// <summary>Запрос в интернет через локальный вход прокси (для SOCKS-режима недоступен).</summary>
    private async Task<bool> TryHttpThroughInboundAsync()
    {
        var route = Routing.ActiveProfile;
        if (route.Mode == ListenMode.Socks) return false;   // WebProxy в .NET не умеет SOCKS

        var host = route.ListenAddress.Trim();
        if (host.Length == 0 || host is "0.0.0.0" or "::" or "[::]" or "*") host = "127.0.0.1";

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://{host}:{route.ListenPort}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        foreach (var url in ConnectivityProbes)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if ((int)response.StatusCode is >= 200 and < 400) return true;
            }
            catch
            {
                /* пробуем следующий адрес */
            }
        }

        return false;
    }

    private static readonly string[] ConnectivityProbes =
    {
        "http://captive.apple.com/hotspot-detect.html",
        "https://cloudflare.com/cdn-cgi/trace",
        "https://www.gstatic.com/generate_204"
    };

    private void ReportDeadTunnel()
    {
        var node = _activeProfile?.Node;
        var awg = node?.HasAwgParams == true ||
                  (_activeProfile is not null &&
                   _activeProfile.Protocol.Equals(ProtocolIds.AmneziaWg, StringComparison.OrdinalIgnoreCase));

        var message = awg
            ? "Туннель запущен, но трафик через него не идёт. Для AmneziaWG так бывает, когда параметры " +
              "обфускации не совпадают с серверными или ядро собрано без поддержки AWG."
            : "Туннель запущен, но трафик через него не идёт: сервер не отвечает или профиль настроен неверно.";

        // Самая частая конкретная причина у AWG 3.1: сервер дописывает к хендшейку случайный
        // трейлер, а ядро клиента флаг random_trailers передать не может — ответы отбрасываются.
        if (node?.AwgRandomTrailers == true && !Core.Info.SupportsAwgTrailers)
            message = "Туннель запущен, но трафик не идёт. В профиле включены RandomTrailers (AmneziaWG 3.1), " +
                      "а это ядро их не поддерживает: сервер присылает хендшейк со случайным хвостом, клиент " +
                      "считает пакет нераспознанным и отбрасывает. Выключите RandomTrailers на сервере и " +
                      $"пересохраните профиль — либо установите ядро с AWG 3.1 (github.com/{CoreInstaller.AwgRepo}): " +
                      "Настройки → Ядро → «Расширенная (AmneziaWG)» → «Скачать».";

        AppendLog(message);
        AppendLog($"Ядро: {Core.Info.Version} · AmneziaWG: {(Core.Info.SupportsAmnezia ? "да" : "нет")} · " +
                  $"AWG 3.1 (random_trailers): {(Core.Info.SupportsAwgTrailers ? "да" : "нет")} · " +
                  $"проверка ядра: {(Core.Info.AmneziaProbed?.ToString() ?? "не выполнялась")}");
        AppendLog(Core.RecentLogText());
        ShowStatus(message, true);
    }

    private async Task RefreshActivePingAsync()
    {
        if (App.Settings.Current.PreciseLatencyTest && Core.State == ConnectionState.Running)
        {
            var ms = await Core.MeasureDelayAsync();
            if (ms > 0)
            {
                ActivePing = ms;
                if (_activeProfile is not null)
                {
                    _activeProfile.LastPingMs = ms;
                    ProfileStore.Save(_activeProfile);
                    Profiles.Find(_activeProfile.Id)?.SetPingSafe(ms);
                }
                return;
            }
        }

        if (_activeProfile is not null)
            ActivePing = await Latency.MeasureAsync(_activeProfile);
    }

    private async Task TestActivePingAsync()
    {
        if (_activeProfile is null) return;
        Busy = true;
        try
        {
            ShowStatus("Проверка задержки…", false);
            await RefreshActivePingAsync();
            var item = Profiles.Find(_activeProfile.Id);
            if (item is not null && !item.IsPinging)
                await Profiles.PingAsync(item);
            ShowStatus(ActivePing < 0 ? "Сервер не ответил" : "Задержка: " + ActivePingText, ActivePing < 0);
        }
        finally
        {
            Busy = false;
        }
    }

    // ------------------------------------------------------------------- прочее
    private async Task InstallCoreAsync()
    {
        ActiveTab = "settings";
        await Preferences.InstallCorePublicAsync();
    }

    private void RestartElevated()
    {
        if (AdminHelper.IsElevated)
        {
            ShowStatus("Приложение уже запущено с правами администратора", false);
            return;
        }

        var running = IsProxyOn;
        if (running) App.Settings.Current.ProxyWasActive = true;
        App.Settings.Current.RememberState = true;
        App.Settings.SaveNow();

        if (AdminHelper.RestartElevated()) ExitRequested?.Invoke();
        else ShowStatus("Перезапуск с правами администратора отклонён", true);
    }

    private void OpenTelegramProxy()
    {
        if (_activeProfile?.Node is not { } node) return;

        var link = _activeProfile.Protocol == ProtocolIds.MtProto && !string.IsNullOrWhiteSpace(node.MtSecret)
            ? $"tg://proxy?server={node.Server}&port={node.Port}&secret={node.MtSecret}"
            : $"tg://socks?server={node.Server}&port={node.Port}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
            ShowStatus("Ссылка отправлена в Telegram", false);
        }
        catch
        {
            try
            {
                System.Windows.Clipboard.SetText(link);
                ShowStatus("Ссылка скопирована в буфер — вставьте её в Telegram", false);
            }
            catch { /* ignore */ }
        }
    }

    private void CopyDiagnostics()
    {
        var text =
            $"GoreBox{Environment.NewLine}" +
            $"Ядро: {Core.Info.VersionShort}{Environment.NewLine}Путь: {Core.Info.Path}{Environment.NewLine}" +
            $"Профиль: {ActiveProfileName}{Environment.NewLine}Состояние: {StateText}{Environment.NewLine}" +
            $"TUN: {Routing.ActiveProfile.TunEnabled}{Environment.NewLine}" +
            $"Админ: {AdminHelper.IsElevated}{Environment.NewLine}" +
            new string('-', 40) + Environment.NewLine + Core.RecentLogText();
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowStatus("Диагностика скопирована", false);
        }
        catch { /* ignore */ }
    }

    public void ShowStatus(string text, bool error)
    {
        AppendLog(text);   // каждое уведомление дублируем в журнал
        StatusMessage = text;
        StatusIsError = error;
        Raise(nameof(HasStatus));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 10 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (StatusMessage == text)
            {
                StatusMessage = "";
                Raise(nameof(HasStatus));
            }
        };
        timer.Start();
    }

    private void UpdateStateText()
    {
        (StateText, StatusBrushKey) = Core.State switch
        {
            ConnectionState.Running => ("Подключено", "Success"),
            ConnectionState.Starting => ("Запуск…", "Warning"),
            ConnectionState.Stopping => ("Остановка…", "Warning"),
            ConnectionState.Error => ("Ошибка ядра", "Danger"),
            _ => ("Отключено", "TextMuted")
        };
        Raise(nameof(StateText));
        Raise(nameof(StatusBrushKey));
        UpdateTrayStatus();
    }

    private void UpdateTrayStatus()
    {
        var profile = _activeProfile?.Name ?? "профиль не выбран";
        Tray.SetStatus($"GoreBox — {profile} · {StateText}");
        // значок: цветной, пока ядро подключено; серый — когда прокси выключен или упал
        Tray.SetProxyOn(Core.State == ConnectionState.Running);
    }

    private void AppendLog(string line)
    {
        Logs.Add(line);   // панель «Логи» на вкладке «Профили»
        try
        {
            var path = Path.Combine(AppPaths.LogsDir, "app.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    /// <summary>Вызывается при завершении сеанса Windows (выключение/выход из системы).</summary>
    public void PrepareForSystemShutdown()
    {
        try
        {
            if (SystemProxy.IsEnabled) SystemProxy.RestoreSnapshot();
        }
        catch { /* ignore */ }

        try
        {
            App.Settings.SaveNow();
        }
        catch { /* ignore */ }
    }

    public async Task ShutdownAsync()
    {
        _statsTimer.Stop();

        // сначала возвращаем системный прокси: даже если ядро не ответит, интернет у пользователя останется
        await RestoreSystemProxyAsync();

        try
        {
            // StopAsync сам ограничен по времени; общий предел здесь — страховка от непредвиденного зависания
            await Core.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (Exception ex)
        {
            AppendLog("Ядро не остановлено при выходе: " + ex.Message);
        }

        var settings = App.Settings.Current;
        if (settings.RememberState)
        {
            settings.ProxyWasActive = IsProxyOn;
            settings.ActiveProfileId = _activeProfile?.Id;
            settings.ActiveRouteProfileId = Routing.SelectedProfile?.Id;
        }
        App.Settings.SaveNow();
        Routing.Persist();

        Tray.Dispose();
    }
}

internal static class ProfileItemExtensions
{
    public static void SetPingSafe(this ProfileItemVm? item, int ms)
    {
        if (item is null) return;
        item.PingMs = ms;
    }
}
