using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using GoreBox.Models;
using GoreBox.Utils;

namespace GoreBox.Views.Dialogs;

public partial class ImportWindow : Window
{
    private readonly ObservableCollection<ProxyProfile> _parsed = new();

    public ImportWindow(string? prefill)
    {
        InitializeComponent();
        Preview.ItemsSource = _parsed;

        if (!string.IsNullOrWhiteSpace(prefill))
        {
            Input.Text = prefill;
            Parse();
        }
        Input.Focus();
    }

    public List<ProxyProfile> Profiles { get; } = new();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Parse();

    private void Parse()
    {
        _parsed.Clear();
        var result = LinkParser.ParseMany(Input.Text ?? "");

        foreach (var profile in result.Profiles) _parsed.Add(profile);
        if (result.Profiles.Count == 0 && Input.Text?.Length > 0)
            _parsed.Add(new ProxyProfile { Name = "Ничего не распознано", Protocol = "" });

        Errors.Text = string.Join(Environment.NewLine, result.Errors.Take(4));
        PreviewTitle.Text = $"Распознанные профили: {result.Profiles.Count}";
        AddButton.IsEnabled = result.Profiles.Count > 0;
        AddButton.Content = result.Profiles.Count > 1 ? $"Добавить {result.Profiles.Count}" : "Добавить";
    }

    private void OnPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Input.Text = Input.Text?.Length > 0 ? Input.Text + Environment.NewLine + text : text;
                Input.CaretIndex = Input.Text.Length;
            }
        }
        catch { /* ignore */ }
    }

    private void OnFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Текст и конфиги (*.txt;*.json;*.conf;*.vpn;*.wg)|*.txt;*.json;*.conf;*.vpn;*.wg|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            Input.Text = Input.Text?.Length > 0 ? Input.Text + Environment.NewLine + text : text;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GoreBox", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Input.Text = "";
        Parse();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var result = LinkParser.ParseMany(Input.Text ?? "");
        if (result.Profiles.Count == 0) return;

        Profiles.AddRange(result.Profiles);
        DialogResult = true;
        Close();
    }
}
