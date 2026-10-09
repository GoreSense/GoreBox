using System.Windows;
using System.Windows.Controls;
using GoreBox.ViewModels;

namespace GoreBox.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncSshPassword();
        IsVisibleChanged += (_, e) =>
        {
            // пароль могли сохранить с вкладки SSH, пока экран настроек был скрыт
            if (e.NewValue is true) SyncSshPassword();
        };
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    /// <summary>PasswordBox нельзя привязать к строке напрямую: переносим значение из настроек в поле.</summary>
    private void SyncSshPassword()
    {
        var stored = Vm?.SshPassword ?? "";
        if (SshPasswordBox.Password != stored) SshPasswordBox.Password = stored;
    }

    private void OnSshPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.SshPassword = SshPasswordBox.Password;
    }
}
