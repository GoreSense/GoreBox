using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoreBox.Models;
using GoreBox.Services;
using GoreBox.Utils;
using GoreBox.ViewModels;

namespace GoreBox.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _reallyExit;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        ApplyNavOrder(App.Settings.Current.NavOrder);
    }

    public async Task InitializeAsync()
    {
        _vm = new MainViewModel(new DialogService(this));
        DataContext = _vm;

        // вызовы приходят из трея (обычный поток WinForms-сообщений) — защищаемся от исключений,
        // иначе WPF/WinForms покажет системный диалог сбоя
        _vm.ExitRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(() => ExitApplication())));
        _vm.RestartRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(() => ExitApplication(restart: true))));
        _vm.ShowWindowRequested += () => Dispatcher.BeginInvoke(new Action(() => Safe(ShowFromTray)));

        UpdateLayout();
        var settings = App.Settings.Current;
        if (settings.WindowWidth > 600) Width = settings.WindowWidth;
        if (settings.WindowHeight > 400) Height = settings.WindowHeight;
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top &&
            left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (string.Equals(settings.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase))
            WindowState = WindowState.Maximized;

        await _vm.InitializeAsync();

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    public void PrepareForSystemShutdown() => _vm?.PrepareForSystemShutdown();

    /// <summary>Показать окно из трея (ЛКМ по иконке). После закрытия окна ничего не делаем.</summary>
    public void ShowFromTray()
    {
        if (_closed) return;

        if (!IsVisible) Show();
        // возврат из трея — подгружаем страницу панели обратно
        _ = PanelTab?.EnsureBrowserAsync();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Выполнить действие, не давая исключению уйти в цикл сообщений WinForms.</summary>
    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && _vm.ActiveTab == "profiles")
        {
            _vm.ActiveTab = "profiles";
            _vm.Profiles.AddFromClipboardCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------- порядок вкладок
    private ListBoxItem? _navPressed;     // вкладка, по которой нажали ЛКМ
    private Point _navPressPoint;
    private bool _navDragging;

    /// <summary>Восстановить сохранённый порядок вкладок. Неизвестные ключи пропускаем.</summary>
    private void ApplyNavOrder(IEnumerable<string>? order)
    {
        if (order is null) return;

        var applied = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var key in order)
        {
            if (!applied.Add(key)) continue;
            var item = NavList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, key, StringComparison.Ordinal));
            if (item is null) continue;

            var from = NavList.Items.IndexOf(item);
            if (from != position)
            {
                NavList.Items.RemoveAt(from);
                NavList.Items.Insert(position, item);
            }
            position++;
        }
    }

    private void OnNavPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _navPressed = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _navPressPoint = e.GetPosition(NavList);
    }

    private void OnNavPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var dragged = _navPressed;
        if (dragged is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndNavDrag();
            return;
        }

        var pos = e.GetPosition(NavList);
        if (!_navDragging)
        {
            if (Math.Abs(pos.Y - _navPressPoint.Y) < 5) return;   // короткое движение — это обычный клик
            _navDragging = true;
            dragged.Opacity = 0.7;
            NavList.CaptureMouse();
        }

        var target = NavItemNearest(pos.Y);
        if (target is null || ReferenceEquals(target, dragged)) return;

        var from = NavList.Items.IndexOf(dragged);
        var to = NavList.Items.IndexOf(target);
        if (from < 0 || to < 0) return;

        // при удалении выбранной вкладки WPF сбрасывает выбор — вернём его тем же значением
        var active = NavList.SelectedValue;
        NavList.Items.RemoveAt(from);
        NavList.Items.Insert(to, dragged);
        NavList.SelectedValue = active;
    }

    private void OnNavPreviewMouseUp(object sender, MouseButtonEventArgs e) => EndNavDrag();

    private void OnNavLostCapture(object sender, MouseEventArgs e) => EndNavDrag();

    private void EndNavDrag()
    {
        var dragged = _navDragging;
        var item = _navPressed;
        _navPressed = null;
        _navDragging = false;
        if (item is not null) item.Opacity = 1;
        if (NavList.IsMouseCaptured) NavList.ReleaseMouseCapture();

        if (!dragged) return;
        App.Settings.Current.NavOrder = NavList.Items.OfType<ListBoxItem>()
            .Select(i => i.Tag as string ?? "")
            .Where(t => t.Length > 0)
            .ToList();
        App.Settings.SaveSoon();
    }

    /// <summary>Вкладка, центр которой ближе всех к точке по вертикали.</summary>
    private ListBoxItem? NavItemNearest(double y)
    {
        ListBoxItem? best = null;
        var bestDistance = double.MaxValue;
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            var centerY = item.TranslatePoint(new Point(0, item.ActualHeight / 2), NavList).Y;
            var distance = Math.Abs(centerY - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = item;
            }
        }
        return best;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ------------------------------------------------------------- пикер акцента
    private bool _syncingAccent;

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (AccentOverlay.Visibility == Visibility.Visible)
        {
            AccentOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        SyncAccentFlyout();
        AccentOverlay.Visibility = Visibility.Visible;
    }

    private void OnAccentOverlayClick(object sender, MouseButtonEventArgs e)
        => AccentOverlay.Visibility = Visibility.Collapsed;

    /// <summary>Клик внутри карточки не должен закрывать пикер (всплытие гасим).</summary>
    private void AccentCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Показать сохранённый цвет в круге и полоске прозрачности (без сохранения).</summary>
    private void SyncAccentFlyout()
    {
        var stored = App.Settings.Current.AccentColor;
        var color = !string.IsNullOrWhiteSpace(stored) && ThemeService.TryParseColor(stored!, out var c)
            ? c
            : ThemeService.DefaultAccent;

        _syncingAccent = true;
        ColorMath.ToHsv(color, out var hue, out var sat, out var val);
        if (sat > 0.001) AccentWheel.Hue = hue;   // у серого оттенок не определён: круг не прыгает
        AccentWheel.Saturation = sat;
        AccentWheel.Brightness = val;
        AccentAlpha.Alpha = color.A / 255.0;
        _syncingAccent = false;
        UpdateAccentPreview(color);
    }

    private void OnAccentWheelChanged() => ApplyAccentFromControls();

    private void OnAccentAlphaChanged() => ApplyAccentFromControls();

    /// <summary>Собрать цвет из круга и полоски прозрачности, применить сразу и сохранить.</summary>
    private void ApplyAccentFromControls()
    {
        if (_syncingAccent || AccentPreview is null) return;

        var color = ColorMath.FromHsv(AccentWheel.Hue, AccentWheel.Saturation, AccentWheel.Brightness,
            (byte)Math.Round(AccentAlpha.Alpha * 255));
        UpdateAccentPreview(color);

        var hex = color.ToString();   // #AARRGGBB
        ThemeService.ApplyAccent(hex);
        App.Settings.Current.AccentColor = hex;
        App.Settings.SaveSoon();
    }

    private void UpdateAccentPreview(Color color)
    {
        AccentPreview.Background = new SolidColorBrush(color);
        AccentHexText.Text = color.ToString();
        AccentAlpha.BaseColor = color;   // полоска сама убирает альфу из заполнения
    }

    private void OnAccentReset(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.AccentColor = null;
        ThemeService.ApplyAccent(null);
        App.Settings.SaveSoon();
        SyncAccentFlyout();
    }

    /// <summary>Скруглённые углы окна: прозрачное окно + Border (CornerRadius=12),
    /// содержимое обрезаем геометрией, иначе углы будут квадратными.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0) return;
        RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 12, 12);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.CloseToTray) HideToTray();
        else ExitApplication();
    }

    private void HideToTray()
    {
        Hide();
        // свернули в трей — выгружаем страницу панели, чтобы не ела ресурсы.
        // SSH-соединение при этом НЕ рвём.
        PanelTab?.UnloadBrowser();
        if (!App.Settings.Current.TrayHintShown)
        {
            App.Settings.Current.TrayHintShown = true;
            App.Settings.SaveSoon();
            _vm?.Tray.ShowHint("GoreBox", "Приложение свёрнуто в трей: клик по иконке открывает окно, " +
                                              "правая кнопка — «Закрыть».");
        }
    }

    /// <summary>
    /// Полный выход: остановить ядро, вернуть прежние настройки системного прокси, убрать иконку из трея
    /// и только затем закрыть окно и завершить приложение.
    /// </summary>
    private void ExitApplication(bool restart = false)
    {
        if (_reallyExit) return;
        _reallyExit = true;
        App.RestartOnExit = restart;   // после освобождения мьютекса App запустит новый экземпляр
        StartExitWatchdog();
        _ = CloseSequenceAsync();
    }

    /// <summary>
    /// Страховка от зависания при выходе: если приложение не закрылось за 20 секунд, принудительно возвращаем
    /// системный прокси, останавливаем ядро и завершаем процесс. Работает в пуле потоков, поэтому срабатывает,
    /// даже когда UI-поток заблокирован.
    /// </summary>
    private void StartExitWatchdog()
    {
        var vm = _vm;
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
        {
            // в errors.log остаётся след: по нему видно, что выход не завершился сам
            App.LogCrash(new TimeoutException("Выход из GoreBox не завершился за 20 секунд — принудительное завершение."));
            try { vm?.PrepareForSystemShutdown(); } catch { /* ignore */ }   // вернёт прокси и сохранит настройки
            try { vm?.Core.Dispose(); } catch { /* ignore */ }               // завершит процесс ядра
            if (App.RestartOnExit) App.StartNewInstance();
            Environment.Exit(0);
        }, TaskScheduler.Default);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_reallyExit || _closed)
        {
            base.OnClosing(e);
            return;
        }

        // закрытие окна = сворачивание в трей (настройка включена по умолчанию)
        if (App.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // пользователь хочет выйти — сначала корректно завершаем работу
        e.Cancel = true;
        ExitApplication();
    }

    private void SaveWindowGeometry()
    {
        var settings = App.Settings.Current;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
        }
        settings.WindowState = WindowState.ToString();
    }

    /// <summary>
    /// Последовательность выхода. Окно закрывается и приложение завершается в любом случае (finally):
    /// если какой-то шаг упал, приложение не должно остаться работать без окна.
    /// </summary>
    private async Task CloseSequenceAsync()
    {
        try
        {
            // перед выходом освобождаем браузер и ssh-сессию
            try { PanelTab?.UnloadBrowser(); } catch (Exception ex) { App.LogCrash(ex); }
            try { SshTab?.Shutdown(); } catch (Exception ex) { App.LogCrash(ex); }
            try { SaveWindowGeometry(); } catch (Exception ex) { App.LogCrash(ex); }

            if (_vm is not null)
            {
                try
                {
                    await _vm.ShutdownAsync();
                }
                catch (Exception ex)
                {
                    App.LogCrash(ex);
                }
            }
        }
        finally
        {
            try { Close(); } catch { /* ignore */ }
            Application.Current.Shutdown();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
        try
        {
            App.Settings.SaveNow();
        }
        catch { /* ignore */ }
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {

    }
}
