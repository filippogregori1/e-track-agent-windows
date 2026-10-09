using System.Diagnostics;
using ActivityTracker.Core;
using ActivityTracker.Windows.Native;
using Windows.Media.Control;

namespace ActivityTracker.Windows.Signals;

/// <summary>
/// Chi tiene acceso lo schermo con un video (regola 5: solo video, non audio). Raccoglie i segnali e li passa alla
/// regola pura <see cref="VideoOwnerPolicy"/> (in Core, verificata dai check):
/// <list type="bullet">
/// <item>sessioni multimediali di Windows in riproduzione (<c>Windows.Media.Control</c>), lette ogni 2 s su un thread
/// proprio, con l'app di origine e se ha una finestra visibile;</item>
/// <item>richiesta di sistema «schermo acceso» (<c>CallNtPowerInformation(SystemExecutionState)</c>), letta a ogni tick;</item>
/// <item>processi in esecuzione (per la lista di esclusione), ogni 5 s.</item>
/// </list>
/// </summary>
public sealed class VideoSignalReader : IDisposable
{
    private readonly string _selfAppId = Path.GetFileName(Environment.ProcessPath ?? "activity-tracker.exe").ToLowerInvariant();
    private volatile IReadOnlyList<MediaPlayback> _playing = [];
    private volatile IReadOnlyList<string> _running = [];
    private volatile string? _mediaError;
    private volatile bool _stopped;
    private readonly Thread _thread;

    public IReadOnlyList<string> Exclusions { get; set; }

    /// <summary>Ultimi segnali grezzi, per la diagnostica nelle Impostazioni.</summary>
    public bool LastDisplayRequired { get; private set; }
    public IReadOnlyList<MediaPlayback> LastPlaying => _playing;
    public VideoChoice? LastChoice { get; private set; }
    public string? MediaError => _mediaError;

    public VideoSignalReader(IReadOnlyList<string> exclusions)
    {
        Exclusions = exclusions;
        _thread = new Thread(Loop) { IsBackground = true, Name = "video-signals" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Il proprietario del video adesso, dato chi è in primo piano.</summary>
    public VideoChoice? Current(string? foregroundAppId, string? foregroundAppName)
    {
        LastDisplayRequired = Win32.IsDisplayRequired();
        LastChoice = VideoOwnerPolicy.Choose(LastDisplayRequired, _playing, foregroundAppId, foregroundAppName,
                                             _running, Exclusions, _selfAppId);
        return LastChoice;
    }

    public void Dispose() => _stopped = true;

    private void Loop()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        var lastProcesses = DateTime.MinValue;
        while (!_stopped)
        {
            try
            {
                manager ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
                var playing = new List<MediaPlayback>();
                foreach (var session in manager.GetSessions())
                {
                    var info = session.GetPlaybackInfo();
                    if (info?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                    var (appId, appName) = MediaApps.FromAumid(session.SourceAppUserModelId ?? "");
                    playing.Add(new MediaPlayback(appId, appName, ProcessNames.VisibleWindows(appId).Count > 0));
                }
                _playing = playing;
                _mediaError = null;
            }
            catch (Exception e)
            {
                _playing = [];
                _mediaError = "Sessioni multimediali non leggibili: " + e.Message;
                manager = null;
            }
            if ((DateTime.UtcNow - lastProcesses).TotalSeconds >= 5)
            {
                lastProcesses = DateTime.UtcNow;
                try
                {
                    _running = Process.GetProcesses().Select(p =>
                    {
                        using (p) return p.ProcessName;
                    }).ToList();
                }
                catch (Exception)
                {
                }
            }
            Thread.Sleep(2000);
        }
    }
}

/// <summary>
/// Dall'identificativo dell'app di una sessione multimediale (AUMID) all'eseguibile. Le app Win32 usano quasi
/// sempre il nome dell'eseguibile o del prodotto; Firefox usa un hash fisso.
/// </summary>
public static class MediaApps
{
    private static readonly Dictionary<string, (string AppId, string AppName)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ("chrome.exe", "Google Chrome"),
        ["msedge"] = ("msedge.exe", "Microsoft Edge"),
        ["brave"] = ("brave.exe", "Brave"),
        ["opera"] = ("opera.exe", "Opera"),
        ["vivaldi"] = ("vivaldi.exe", "Vivaldi"),
        ["308046b0af4a39cb"] = ("firefox.exe", "Firefox"),
        ["6f193ccc56814779"] = ("firefox.exe", "Firefox"),
        ["spotify"] = ("spotify.exe", "Spotify"),
        ["spotifyab.spotifymusic_zpdnekdrzrea0!spotify"] = ("spotify.exe", "Spotify"),
        ["microsoft.zunemusic_8wekyb3d8bbwe!microsoft.zunemusic"] = ("music.ui.exe", "Lettore multimediale"),
        ["microsoft.zunevideo_8wekyb3d8bbwe!microsoft.zunevideo"] = ("video.ui.exe", "Film e TV"),
    };

    public static (string AppId, string AppName) FromAumid(string aumid)
    {
        var a = aumid.Trim();
        if (Known.TryGetValue(a, out var known)) return known;
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.GetFileName(a.Replace('/', '\\')).ToLowerInvariant();
            return (exe, Path.GetFileNameWithoutExtension(exe));
        }
        // «Chrome.UserData.Profile1», «Brave.XYZ»: conta il prefisso.
        var prefix = a.Split('.', '!', '_')[0];
        if (Known.TryGetValue(prefix, out known)) return known;
        // App pacchettizzate: «Editore.App_hash!Id» → «App».
        var family = a.Split('!')[0].Split('_')[0];
        var friendly = family.Contains('.') ? family[(family.LastIndexOf('.') + 1)..] : family;
        return (a.ToLowerInvariant(), friendly.Length > 0 ? friendly : a);
    }
}
