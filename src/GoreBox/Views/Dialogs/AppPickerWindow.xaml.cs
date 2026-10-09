using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using GoreBox.Services;
using GoreBox.Utils;
using GoreBox.ViewModels;

namespace GoreBox.Views.Dialogs;

public partial class AppPickerWindow : Window
{
    private readonly ObservableCollection<AppEntry> _all = new();
    private readonly ObservableCollection<AppEntry> _view = new();

    public AppPickerWindow()
    {
        InitializeComponent();
        List.ItemsSource = _view;
        Loaded += (_, _) => Refresh();
    }

    public List<RunningApp> Picked { get; } = new();

    private void Refresh()
    {
        _all.Clear();
        foreach (var app in ProcessScanner.Scan())
            _all.Add(new AppEntry(app));

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = (Search.Text ?? "").Trim();
        _view.Clear();
        foreach (var entry in _all)
        {
            if (q.Length == 0 ||
                entry.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                _view.Add(entry);
        }
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        var selected = _all.Count(e => e.IsSelected);
        Counter.Text = selected == 0 ? "Ничего не выбрано" : $"Выбрано: {selected}";
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Приложения (*.exe)|*.exe|Все файлы (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            if (_all.Any(a => string.Equals(a.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            var (name, title) = ProcessScanner.Describe(file);
            var entry = new AppEntry(new RunningApp(0, name, file, title)) { IsSelected = true };
            _all.Insert(0, entry);
        }
        ApplyFilter();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _all.Where(x => x.IsSelected))
            Picked.Add(new RunningApp(entry.Pid, entry.Name, entry.Path, entry.Title));

        DialogResult = Picked.Count > 0;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public sealed class AppEntry : ObservableObject
    {
        private bool _selected;

        public AppEntry(RunningApp app)
        {
            Pid = app.Pid;
            Name = app.Name;
            Path = app.Path;
            Title = string.IsNullOrWhiteSpace(app.Title) ? app.Name : app.Title;
            Icon = IconHelper.ForExecutable(app.Path);
        }

        public int Pid { get; }

        public string PidLabel => Pid > 0 ? "PID " + Pid : "файл";
        public string Name { get; }
        public string Path { get; }
        public string Title { get; }
        public ImageSource? Icon { get; }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value))
                    (Application.Current.Windows.OfType<AppPickerWindow>().FirstOrDefault())?.UpdateCounter();
            }
        }
    }
}
