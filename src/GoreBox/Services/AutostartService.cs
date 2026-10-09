using Microsoft.Win32;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Автозапуск приложения вместе с Windows (HKCU\...\Run).</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GoreBox";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void Apply(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (!enabled)
        {
            if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
            return;
        }

        var exe = AppPaths.ExecutablePath;
        if (string.IsNullOrEmpty(exe)) return;

        var args = startMinimized ? " --minimized" : "";
        key.SetValue(ValueName, $"\"{exe}\"{args}", RegistryValueKind.String);
    }
}
