using System.Collections.Concurrent;
using ActivityTracker.Core;
using ActivityTracker.Windows.Native;

namespace ActivityTracker.Windows.Signals;

/// <summary>
/// Dominio della scheda attiva dei browser, letto dalla barra degli indirizzi con UI Automation (l'equivalente
/// Windows degli Apple Events del Mac; nessun permesso da concedere). Si legge solo il testo della barra, se ne tiene
/// solo l'host (<see cref="ActivityResolver.HostFromAddressBar"/>): l'indirizzo completo non lascia questa classe.
///
/// UI Automation può essere lenta (decine di ms) e non deve fermare il campionatore: le letture girano su un thread
/// proprio, il campionatore chiede la finestra che gli interessa e riceve l'ultimo valore letto (al più ~1 s fa).
/// </summary>
public sealed class BrowserUrlReader : IDisposable
{
    /// <summary>Browser supportati: eseguibile → nome.</summary>
    public static readonly IReadOnlyDictionary<string, string> Browsers = new Dictionary<string, string>
    {
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["brave.exe"] = "Brave",
        ["firefox.exe"] = "Firefox",
        ["opera.exe"] = "Opera",
        ["vivaldi.exe"] = "Vivaldi",
        ["arc.exe"] = "Arc",
    };

    private const double CacheSeconds = 2.5;
    private const int LoopMilliseconds = 700;

    public bool IsEnabled { get; }

    private readonly ConcurrentDictionary<IntPtr, (DateTime At, string? Domain)> _cache = new();
    private readonly ConcurrentDictionary<IntPtr, (DateTime At, string AppId)> _wanted = new();
    private readonly ConcurrentDictionary<string, string> _lastDomains = new();
    private readonly Dictionary<IntPtr, IUIAutomationElement> _addressBars = [];
    private readonly Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopped;
    private IUIAutomation? _uia;

    /// <summary>Ultimo errore di UI Automation (mostrato nella diagnostica delle Impostazioni).</summary>
    public string? LastError { get; private set; }

    public BrowserUrlReader(bool enabled)
    {
        IsEnabled = enabled;
        if (!enabled) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "browser-url" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static bool IsSupported(string? appId) => appId is not null && Browsers.ContainsKey(appId);

    public string? LastKnownDomain(string appId) => _lastDomains.TryGetValue(appId, out var d) ? d : null;

    /// <summary>Host della scheda attiva della finestra <paramref name="hwnd"/>, o null se non ancora letto / non web.</summary>
    public string? ActiveTabDomain(string appId, IntPtr hwnd)
    {
        if (!IsEnabled || !IsSupported(appId) || hwnd == IntPtr.Zero) return null;
        var first = !_wanted.ContainsKey(hwnd);
        _wanted[hwnd] = (DateTime.UtcNow, appId);
        if (first) _wake.Set();
        return _cache.TryGetValue(hwnd, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < CacheSeconds ? c.Domain : null;
    }

    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
    }

    private void Loop()
    {
        try
        {
            _uia = Uia.Create();
        }
        catch (Exception e)
        {
            LastError = "UI Automation non disponibile: " + e.Message;
            return;
        }
        while (!_stopped)
        {
            _wake.WaitOne(LoopMilliseconds);
            if (_stopped) break;
            var now = DateTime.UtcNow;
            foreach (var (hwnd, want) in _wanted.ToArray())
            {
                if ((now - want.At).TotalSeconds > CacheSeconds)
                {
                    _wanted.TryRemove(hwnd, out _);
                    _cache.TryRemove(hwnd, out _);
                    _addressBars.Remove(hwnd);
                    continue;
                }
                var domain = Read(hwnd);
                _cache[hwnd] = (DateTime.UtcNow, domain);
                if (domain is not null) _lastDomains[want.AppId] = domain;
            }
        }
    }

    /// <summary>Testo della barra degli indirizzi → host. L'elemento trovato si tiene per la finestra: le letture
    /// successive costano una sola chiamata.</summary>
    private string? Read(IntPtr hwnd)
    {
        if (_uia is null) return null;
        try
        {
            if (!_addressBars.TryGetValue(hwnd, out var bar))
            {
                bar = FindAddressBar(hwnd);
                if (bar is null) return null;
                _addressBars[hwnd] = bar;
            }
            if (bar.GetCurrentPropertyValue(Uia.UIA_ValueValuePropertyId, out var value) != 0)
            {
                _addressBars.Remove(hwnd);
                return null;
            }
            LastError = null;
            return ActivityResolver.HostFromAddressBar(value as string);
        }
        catch (Exception e)
        {
            _addressBars.Remove(hwnd);
            LastError = e.Message;
            return null;
        }
    }

    private IUIAutomationElement? FindAddressBar(IntPtr hwnd)
    {
        if (_uia!.ElementFromHandle(hwnd, out var root) != 0 || root is null) return null;
        // Firefox: la barra ha un id stabile. Chromium (Chrome, Edge, Brave, Opera, Vivaldi): la prima casella di testo
        // della finestra è la omnibox.
        if (_uia.CreatePropertyCondition(Uia.UIA_AutomationIdPropertyId, "urlbar-input", out var byId) == 0 && byId is not null
            && root.FindFirst(Uia.TreeScope_Descendants, byId, out var urlbar) == 0 && urlbar is not null)
            return urlbar;
        if (_uia.CreatePropertyCondition(Uia.UIA_ControlTypePropertyId, Uia.UIA_EditControlTypeId, out var isEdit) == 0 && isEdit is not null
            && root.FindFirst(Uia.TreeScope_Descendants, isEdit, out var edit) == 0)
            return edit;
        return null;
    }
}
