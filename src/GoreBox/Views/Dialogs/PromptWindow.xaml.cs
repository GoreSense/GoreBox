using System.Windows;
using System.Windows.Input;

namespace GoreBox.Views.Dialogs;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string caption, string initial)
    {
        InitializeComponent();
        TitleText.Text = title;
        Caption.Text = caption;
        Input.Text = initial;
    }

    public string Value { get; private set; } = "";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Input.Focus();
        Input.SelectAll();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
        if (e.Key == Key.Escape) OnCancel(sender, e);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Value = Input.Text ?? "";
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
