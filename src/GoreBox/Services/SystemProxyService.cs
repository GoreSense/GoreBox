using System.Runtime.InteropServices;
using Microsoft.Win32;
using GoreBox.Utils;

namespace GoreBox.Services;

/// <summary>Системный прокси Windows (WinINET): включение, выключение и восстановление прежних значений.</summary>
public sealed class SystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, false);
            return key?.GetValue("ProxyEnable") is int v && v == 1;
        }
    }

    public string? CurrentServer
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, false);
            return key?.GetValue("ProxyServer") as string;
        }
    }

    public void Enable(string host, int port, string bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;192.168.*;<local>")
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, true) ?? Registry.CurrentUser.CreateSubKey(RegPath);
        if (key is null) return;
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        Refresh();
    }

    /// <summary>Отключает системный прокси. restoreServer — вернуть прежнее значение ProxyServer.</summary>
    public void Disable(string? restoreServer = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, true) ?? Registry.CurrentUser.CreateSubKey(RegPath);
        if (key is null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        if (restoreServer is not null)
            key.SetValue("ProxyServer", restoreServer, RegistryValueKind.String);
        Refresh();
    }

    /// <summary>
    /// Если приложение было завершено аварийно, системный прокси может остаться указывать на наш порт.
    /// При старте убираем такой «залипший» прокси, чтобы не оставить пользователя без интернета.
    /// </summary>
    public void CleanupStale(IEnumerable<int> ourPorts)
    {
        try
        {
            if (!IsEnabled) return;
            var server = CurrentServer;
            if (string.IsNullOrWhiteSpace(server)) return;

            var stale = ourPorts.Any(port =>
                server!.Contains($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                server.Contains($"localhost:{port}", StringComparison.OrdinalIgnoreCase));

            if (!stale) return;

            using var key = Registry.CurrentUser.OpenSubKey(RegPath, true);
            key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            Refresh();
        }
        catch { /* ignore */ }
    }

    private static void Refresh()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch { /* ignore */ }
    }

    // -------------------------------------------------- сохранение прежнего состояния
    private sealed class ProxySnapshot
    {
        public bool Enabled { get; set; }
        public string? Server { get; set; }
        public string? Override { get; set; }
    }

    private static string SnapshotPath => Path.Combine(AppPaths.RuntimeDir, "proxy-backup.json");

    public void CaptureSnapshot()
    {
        AppPaths.EnsureCreated();
        if (File.Exists(SnapshotPath)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, false);
        var snapshot = new ProxySnapshot
        {
            Enabled = key?.GetValue("ProxyEnable") is int v && v == 1,
            Server = key?.GetValue("ProxyServer") as string,
            Override = key?.GetValue("ProxyOverride") as string
        };
        Services.Json.Write(SnapshotPath, snapshot);
    }

    /// <summary>Возврат прежних настроек прокси (вызывается при выходе и при сбое).</summary>
    public void RestoreSnapshot()
    {
        if (!File.Exists(SnapshotPath)) return;
        var snapshot = Services.Json.Read<ProxySnapshot>(SnapshotPath);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, true) ?? Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is not null && snapshot is not null)
            {
                key.SetValue("ProxyEnable", snapshot.Enabled ? 1 : 0, RegistryValueKind.DWord);
                if (snapshot.Server is not null) key.SetValue("ProxyServer", snapshot.Server, RegistryValueKind.String);
                if (snapshot.Override is not null) key.SetValue("ProxyOverride", snapshot.Override, RegistryValueKind.String);
            }
            Refresh();
        }
        catch { /* ignore */ }
        finally
        {
            try { File.Delete(SnapshotPath); } catch { /* ignore */ }
        }
    }
}
