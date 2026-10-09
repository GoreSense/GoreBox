using System.Windows;
using System.Windows.Input;
using GoreBox.Models;
using GoreBox.Services;
using GoreBox.ViewModels;

namespace GoreBox.Views.Dialogs;

public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _vm;
    private readonly ProxyProfile _target;

    public ProfileEditorWindow(ProxyProfile profile, LatencyService latencyService)
    {
        InitializeComponent();

        _target = profile;
        _vm = new ProfileEditorViewModel(profile, latencyService);
        DataContext = _vm;
        Title = "Профиль — " + profile.Name;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.Profile.Name))
        {
            MessageBox.Show(this, "Укажите название профиля.", "GoreBox", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.Node.Server) && _vm.Profile.Protocol != ProtocolIds.MtProto)
        {
            MessageBox.Show(this, "Укажите адрес сервера.", "GoreBox", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.ApplyTo(_target);
        DialogResult = true;
        Close();
    }
}
