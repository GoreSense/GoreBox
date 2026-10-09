using System.Windows;
using System.Windows.Input;

namespace GoreBox.Views.Dialogs;

public partial class CoreLogWindow : Window
{
    public CoreLogWindow(string title, string text)
    {
        InitializeComponent();
        TitleText.Text = title;
        LogText.Text = text;
        LogText.CaretIndex = LogText.Text.Length;
        LogText.ScrollToEnd();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text ?? ""); } catch { /* ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
