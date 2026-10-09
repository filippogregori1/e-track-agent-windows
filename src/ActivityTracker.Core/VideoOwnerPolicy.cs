namespace ActivityTracker.Core;

/// <summary>Una sessione multimediale di Windows in riproduzione (Global System Media Transport Controls).</summary>
/// <param name="AppId">Eseguibile in minuscolo (es. <c>chrome.exe</c>), o l'id dell'app se non si risale al processo.</param>
/// <param name="HasVisibleWindow">L'app ha almeno una finestra visibile e non ridotta a icona.</param>
public sealed record MediaPlayback(string AppId, string? AppName, bool HasVisibleWindow);

/// <summary>Chi tiene acceso lo schermo con un video, e da quale segnale lo si è dedotto.</summary>
public sealed record VideoChoice(string AppId, string? AppName, string Source);

/// <summary>
/// LA REGOLA DEL VIDEO SU WINDOWS (equivalente di «Attribuzione del video al processo X» del Mac).
///
/// Sul Mac ogni asserzione «schermo acceso» dice anche quale processo l'ha presa. Su Windows l'elenco per processo
/// delle richieste di alimentazione (<c>powercfg /requests</c>) richiede l'amministratore, e l'agente non lo è.
/// Si incrociano quindi due segnali leggibili da un utente normale:
///
///  1. le sessioni multimediali in riproduzione (SMTC: le stesse dei controlli del volume di Windows), che dicono
///     QUALE app sta riproducendo; contano solo quelle di app con una finestra visibile e non escluse
///     (la lista di esclusione contiene i lettori solo audio: Spotify, Musica, …);
///  2. la richiesta di sistema «schermo acceso» (<c>ES_DISPLAY_REQUIRED</c>), che dice CHE qualcuno tiene acceso
///     lo schermo (lettori video, videochiamate) ma non chi: se c'è e nessuna sessione multimediale la spiega,
///     il video si attribuisce all'app in primo piano — a meno che giri un programma escluso che tiene acceso lo
///     schermo senza video (PowerToys Awake, Caffeine, …).
///
/// Se più app riproducono, vince quella in primo piano; altrimenti la prima in ordine di nome (deterministico),
/// come la regola 5 del Mac.
/// </summary>
public static class VideoOwnerPolicy
{
    public static VideoChoice? Choose(bool displayRequired, IEnumerable<MediaPlayback> playing, string? foregroundAppId,
                                      string? foregroundAppName, IEnumerable<string> runningProcesses,
                                      IEnumerable<string> exclusions, string? selfAppId = null)
    {
        var excluded = new HashSet<string>(exclusions.Select(Normalize).Where(s => s.Length > 0));
        if (selfAppId is not null) excluded.Add(Normalize(selfAppId));
        bool IsExcluded(string? id, string? name) =>
            (id is not null && excluded.Contains(Normalize(id))) || (name is not null && excluded.Contains(Normalize(name)));

        var candidates = playing
            .Where(p => p.HasVisibleWindow && !IsExcluded(p.AppId, p.AppName))
            .OrderBy(p => p.AppId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > 0)
        {
            var fg = foregroundAppId is null ? null : candidates.FirstOrDefault(c => Normalize(c.AppId) == Normalize(foregroundAppId));
            var chosen = fg ?? candidates[0];
            return new VideoChoice(chosen.AppId, chosen.AppName, "sessione multimediale");
        }

        if (!displayRequired || foregroundAppId is null) return null;
        if (runningProcesses.Any(p => excluded.Contains(Normalize(p)))) return null;
        if (IsExcluded(foregroundAppId, foregroundAppName)) return null;
        return new VideoChoice(foregroundAppId, foregroundAppName, "schermo tenuto acceso");
    }

    /// <summary>Confronto senza maiuscole e senza «.exe»: «Spotify», «spotify.exe» e «SPOTIFY.EXE» sono lo stesso.</summary>
    public static string Normalize(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        return t.EndsWith(".exe", StringComparison.Ordinal) ? t[..^4] : t;
    }
}
