using System.Diagnostics;
using System.Security.Principal;
using GoreBox.Utils;

namespace GoreBox.Services;

public static class AdminHelper
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Перезапуск приложения с правами администратора (нужно для TUN).</summary>
    public static bool RestartElevated(string arguments = "")
    {
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppPaths.DataRoot
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
