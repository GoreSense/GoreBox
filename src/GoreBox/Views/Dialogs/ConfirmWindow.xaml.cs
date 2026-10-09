using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GoreBox.Views.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string text, string okText, bool danger, bool okOnly)
    {
        InitializeComponent();
        TitleText.Text = title;
        Message.Text = text;

        if (okOnly)
        {
            var ok = new Button { Content = okText, Style = (Style)FindResource("BtnPrimary"), MinWidth = 96 };
            ok.Click += (_, _) =>
            {
                DialogResult = true;
                Close();
            };
            Buttons.Children.Add(ok);
            return;
        }

        var cancel = new Button { Content = "Отмена", Style = (Style)FindResource("BtnGhost"), MinWidth = 96 };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var confirm = new Button
        {
            Content = okText,
            Style = (Style)FindResource(danger ? "BtnDanger" : "BtnPrimary"),
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0)
        };
        confirm.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        Buttons.Children.Add(cancel);
        Buttons.Children.Add(confirm);
        confirm.Focus();
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
}
