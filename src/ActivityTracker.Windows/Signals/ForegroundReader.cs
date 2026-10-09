using System.Collections.Concurrent;
using System.Diagnostics;
using ActivityTracker.Windows.Native;

namespace ActivityTracker.Windows.Signals;

/// <summary>Un processo con una finestra: identità dell'app (eseguibile in minuscolo) e nome leggibile.</summary>
public sealed record AppWindow(IntPtr Hwnd, uint Pid, string AppId, string AppName, string Title);

/// <summary>
/// App in primo piano: <c>GetForegroundWindow</c> → processo → eseguibile → nome dalla descrizione del file
/// (es. <c>chrome.exe</c> → «Google Chrome»). Le app UWP stanno dentro <c>ApplicationFrameHost.exe</c>: si scende
/// nella finestra figlia per trovare il processo vero. Nessun permesso richiesto.
/// </summary>
public sealed class ForegroundReader
{
    /// <summary>Lunghezza massima del titolo salvato come suggerimento (come sul Mac).</summary>
    public const int MaxTitleLength = 200;

    private static readonly uint SelfPid = (uint)Environment.ProcessId;
    private AppWindow? _lastOther;

    /// <summary>
    /// L'app in primo piano. Le finestre dell'agente (report, Impostazioni) non contano: aprire il report dall'icona
    /// non deve spostare il tempo su «e-track agent», vale l'ultima app vera in primo piano (sul Mac il pannello
    /// della barra menu non cambia l'app in primo piano).
    /// </summary>
    public AppWindow? Current()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (Win32.ProcessId(hwnd) == SelfPid) return _lastOther;
        _lastOther = Describe(hwnd);
        return _lastOther;
    }

    public static AppWindow? Describe(IntPtr hwnd)
    {
        var pid = Win32.ProcessId(hwnd);
        if (pid == 0) return null;
        var process = ProcessNames.Resolve(pid);
        if (process is null) return null;
        if (process.Value.AppId == "applicationframehost.exe" && HostedUwpProcess(hwnd, pid) is { } hosted && ProcessNames.Resolve(hosted) is { } inner)
        {
            pid = hosted;
            process = inner;
        }
        var title = Win32.WindowText(hwnd);
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];
        return new AppWindow(hwnd, pid, process.Value.AppId, process.Value.AppName, title);
    }

    /// <summary>Il processo dell'app UWP dentro la cornice di ApplicationFrameHost.</summary>
    private static uint? HostedUwpProcess(IntPtr frame, uint framePid)
    {
        uint? found = null;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            var pid = Win32.ProcessId(child);
            if (pid != 0 && pid != framePid)
            {
                found = pid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

/// <summary>pid → (eseguibile in minuscolo, nome leggibile), con cache per percorso.</summary>
public static class ProcessNames
{
    private static readonly ConcurrentDictionary<string, string> NameByPath = new(StringComparer.OrdinalIgnoreCase);

    public static (string AppId, string AppName)? Resolve(uint pid)
    {
        var path = Win32.ProcessPath(pid);
        if (path is null)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var exe = p.ProcessName + ".exe";
                return (exe.ToLowerInvariant(), p.ProcessName);
            }
            catch (Exception)
            {
                return null;
            }
        }
        var appId = Path.GetFileName(path).ToLowerInvariant();
        return (appId, NameByPath.GetOrAdd(path, DisplayName));
    }

    /// <summary>Descrizione del file (es. «Google Chrome»), poi nome del prodotto, poi nome dell'eseguibile.</summary>
    private static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch (Exception)
        {
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Le finestre principali (visibili, non ridotte a icona, non nascoste da DWM) di un'app, in ordine Z.</summary>
    public static List<IntPtr> VisibleWindows(string appId)
    {
        var result = new List<IntPtr>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || Win32.IsCloaked(hwnd)) return true;
            if (Win32.GetWindowTextLengthW(hwnd) == 0) return true;
            var pid = Win32.ProcessId(hwnd);
            if (pid != 0 && Resolve(pid) is { } p && p.AppId == appId) result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
