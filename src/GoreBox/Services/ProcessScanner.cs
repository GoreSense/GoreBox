using System.Diagnostics;
using System.IO;

namespace GoreBox.Services;

public sealed record RunningApp(int Pid, string Name, string Path, string Title)
{
    public string Display => string.IsNullOrWhiteSpace(Title) ? Name : Title;
}

/// <summary>Поиск запущенных приложений (для добавления их в правила маршрутизации).</summary>
public static class ProcessScanner
{
    public static List<RunningApp> Scan(bool includeSystem = false)
    {
        var result = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownPid = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == ownPid) continue;

                string path;
                try
                {
                    path = process.MainModule?.FileName ?? "";
                }
                catch
                {
                    continue;   // недоступно (системный/чужой разряд)
                }

                if (string.IsNullOrEmpty(path)) continue;
                var lower = path.ToLowerInvariant();
                if (!includeSystem && (lower.Contains(@"\windows\system32") || lower.Contains(@"\windows\syswow64")
                                       || lower.Contains(@"\windows\winsxs"))) continue;
                if (lower.EndsWith("gorebox.exe")) continue;
                if (!seen.Add(path)) continue;

                string title = "";
                try { title = process.MainWindowTitle ?? ""; } catch { /* ignore */ }

                result.Add(new RunningApp(process.Id, Path.GetFileNameWithoutExtension(path), path, title));
            }
            catch
            {
                // процесс мог завершиться
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderByDescending(a => !string.IsNullOrWhiteSpace(a.Title))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Поиск процессов, соответствующих правилу приложения.</summary>
    public static bool IsRunning(string processName, string path)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        foreach (var process in Process.GetProcessesByName(name))
        {
            process.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Информация о файле по пути (имя exe, описание).</summary>
    public static (string ProcessName, string Title) Describe(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        string title = name;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) title = vi.FileDescription!;
        }
        catch { /* ignore */ }
        return (name, title);
    }
}
