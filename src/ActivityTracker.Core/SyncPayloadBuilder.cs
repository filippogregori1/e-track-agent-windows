using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ActivityTracker.Core;

/// <summary>Un tratto pronto per l'invio, ancora in tempo del PC (lo scarto d'orologio si applica solo alla codifica).</summary>
public sealed record Tratto(string Giorno, string? Sessione, string? Nome, StatoAttivita Stato, DateTimeOffset Start, DateTimeOffset End)
{
    public double Duration => (End - Start).TotalSeconds;
}

/// <summary>
/// Trasforma i segmenti misurati (più la coda provvisoria, come fa il report) nei tratti del contratto di
/// equipe-track, incrociandoli con il registro delle letture di <c>GET /api/agente/sessione</c>:
/// <list type="bullet">
/// <item>dentro un intervallo <c>tracking</c>: attivo / passivo / senza_utilizzo, con id e giorno della sessione e il
/// nome dell'attività risolto dalle regole (lo stesso <see cref="ActivityResolver"/> del report);</item>
/// <item>dentro un intervallo <c>none</c>: <c>fuori_sessione</c>, senza sessione né nome — solo per quanto il PC era in uso;</item>
/// <item>in pausa, nei buchi fra letture e quando il PC è spento/bloccato (<c>off</c>): niente.</item>
/// </list>
/// Tratti consecutivi con stessa giornata, sessione, nome e stato si fondono in uno solo.
/// </summary>
public sealed class SyncPayloadBuilder
{
    /// <summary>Lunghezza massima accettata dal server per il nome dell'attività.</summary>
    public const int MaxActivityLength = 80;
    /// <summary>Il nome dei tratti <c>senza_utilizzo</c> (il contratto vuole un nome su ogni tratto in sessione).</summary>
    public const string UnusedName = "Senza utilizzo";
    /// <summary>Due tratti uguali separati da meno di tanto si fondono.</summary>
    public const double Contiguity = 1;
    /// <summary>Un tratto non dura più di una giornata (<c>LIMITI_AGENTE.durataSec</c>).</summary>
    public const double MaxTrattoSeconds = 86_400;
    /// <summary>Le giornate di equipe-track (<c>dayKey</c>, fuso Europe/Rome): servono solo per i tratti
    /// <c>fuori_sessione</c>, quelli in sessione prendono il giorno della sessione dal server.</summary>
    public static readonly DayClock ServerDayClock = new(DayClock.Rome);

    public ActivityResolver Resolver { get; }
    public DayClock GiornoClock { get; }

    public SyncPayloadBuilder(ActivityResolver resolver, DayClock? giornoClock = null)
    {
        Resolver = resolver;
        GiornoClock = giornoClock ?? ServerDayClock;
    }

    private sealed record Piece(DateTimeOffset Start, DateTimeOffset End, ActivityState State, Subject? Subject);

    /// <summary>I tratti di tutte le giornate coperte dal registro, per giorno, in ordine di inizio.</summary>
    public Dictionary<string, List<Tratto>> Tratti(IEnumerable<Segment> segments, IEnumerable<ProvisionalSlice>? provisional,
                                                   TrackingLedger ledger)
    {
        var measured = segments
            .Where(s => s.Duration > 0 && s.State != ActivityState.Off)
            .Select(s => new Piece(s.StartDate, s.EndDate, s.State, s.Subject))
            .ToList();
        // Coda non confermata: uso attivo provvisorio sull'app in primo piano, come nel report live.
        measured.AddRange((provisional ?? []).Where(p => p.Duration > 0)
            .Select(p => new Piece(p.Start, p.End, ActivityState.Active, p.Foreground)));
        measured = measured.OrderBy(p => p.Start).ToList();

        var raw = new List<Tratto>();
        foreach (var p in measured)
        {
            foreach (var iv in ledger.Intervals.Where(iv => iv.EndDate > p.Start && iv.StartDate < p.End))
            {
                var a = Epoch.Max(p.Start, iv.StartDate);
                var b = Epoch.Min(p.End, iv.EndDate);
                if (b <= a) continue;
                var state = iv.State;
                switch (state.Kind)
                {
                    case ServerStateKind.Tracking:
                        var (stato, nome) = p.State switch
                        {
                            ActivityState.Active => (StatoAttivita.Attivo, ActivityName(p.Subject)),
                            ActivityState.Passive => (StatoAttivita.Passivo, ActivityName(p.Subject)),
                            _ => (StatoAttivita.SenzaUtilizzo, UnusedName),
                        };
                        raw.Add(new Tratto(state.Giorno ?? "", state.Session, nome, stato, a, b));
                        break;
                    case ServerStateKind.None:
                        foreach (var piece in GiornoClock.Split(a, b))
                            raw.Add(new Tratto(piece.Day, null, null, StatoAttivita.FuoriSessione, piece.Start, piece.End));
                        break;
                    case ServerStateKind.Paused:
                        break;
                }
            }
        }

        var byDay = new Dictionary<string, List<Tratto>>();
        foreach (var t in Coalesce(raw))
        {
            if (!byDay.TryGetValue(t.Giorno, out var list)) byDay[t.Giorno] = list = [];
            list.AddRange(SplitLong(t));
        }
        foreach (var day in byDay.Keys.ToList())
        {
            byDay[day] = byDay[day].Where(t => Epoch.Round(t.Duration) >= 1).ToList();
            if (byDay[day].Count == 0) byDay.Remove(day);
        }
        return byDay;
    }

    /// <summary>Fonde i tratti consecutivi con la stessa chiave (giorno, sessione, nome, stato).</summary>
    public static List<Tratto> Coalesce(IEnumerable<Tratto> tratti)
    {
        var output = new List<Tratto>();
        foreach (var t in tratti.OrderBy(t => t.Start))
        {
            if (output.Count > 0)
            {
                var last = output[^1];
                if (last.Giorno == t.Giorno && last.Sessione == t.Sessione && last.Nome == t.Nome && last.Stato == t.Stato
                    && (t.Start - last.End).TotalSeconds <= Contiguity)
                {
                    output[^1] = last with { End = Epoch.Max(last.End, t.End) };
                    continue;
                }
            }
            output.Add(t);
        }
        return output;
    }

    public static List<Tratto> SplitLong(Tratto t)
    {
        if (t.Duration <= MaxTrattoSeconds) return [t];
        var output = new List<Tratto>();
        var cursor = t.Start;
        while (cursor < t.End)
        {
            var end = Epoch.Min(t.End, cursor.AddSeconds(MaxTrattoSeconds));
            output.Add(t with { Start = cursor, End = end });
            cursor = end;
        }
        return output;
    }

    // Invio

    /// <summary>Il corpo di <c>POST /api/agente/segmenti</c> per una giornata. <paramref name="clockOffset"/> (ora del
    /// server − ora del PC) sposta gli inizi sull'orologio del server, che controlla i tratti con ±2 min di scarto.</summary>
    public static InvioSegmenti Invio(string giorno, IEnumerable<Tratto> tratti, double clockOffset = 0) =>
        new(giorno, tratti.Select(t => new SegmentoInvio(
            t.Sessione, t.Nome,
            (int)Math.Min(MaxTrattoSeconds, Math.Max(1, Epoch.Round(t.Duration))),
            t.Stato, SyncJson.Instant(t.Start.AddSeconds(clockOffset)))).ToList());

    /// <summary>Hash stabile (SHA-256 esadecimale) dei tratti di una giornata, al millisecondo: cambia se cambia anche
    /// un solo tratto. Non dipende dallo scarto d'orologio, così una giornata ferma non si reinvia.</summary>
    public static string Hash(IEnumerable<Tratto> tratti)
    {
        var text = string.Join("\n", tratti.OrderBy(t => t.Start).Select(t => string.Join("|",
            t.Giorno, t.Sessione ?? "-", t.Nome ?? "-", t.Stato.Raw(),
            Epoch.Round(Epoch.Seconds(t.Start) * 1000).ToString(CultureInfo.InvariantCulture),
            Epoch.Round(Epoch.Seconds(t.End) * 1000).ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // Nome attività (privacy)

    /// <summary>
    /// Nome risolto dalle regole, con due argini aggiuntivi:
    /// se il resolver è caduto sul ripiego "eseguibile" (nome app assente) si usa <c>Sconosciuto</c>;
    /// i caratteri che il server rifiuta (<c>/ ? # @ ://</c>, caratteri di controllo) diventano spazi e il nome è
    /// troncato a 80 caratteri, così un giorno non resta bloccato in 422. Infine il filtro del server
    /// (<see cref="PrivacyFilter"/>) come ultima diga.
    /// </summary>
    public string ActivityName(Subject? subject)
    {
        var resolved = Resolver.Activity(subject);
        if (subject?.AppId is { Length: > 0 } app && string.Equals(resolved, app, StringComparison.OrdinalIgnoreCase))
            return ActivityResolver.UnknownLabel;
        var name = SanitizedActivity(resolved);
        return PrivacyFilter.RejectReason(name) is null ? name : ActivityResolver.UnknownLabel;
    }

    public static string SanitizedActivity(string raw)
    {
        var cleaned = raw.Replace("://", " ", StringComparison.Ordinal);
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(cleaned);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            if (element is "/" or "?" or "#" or "@" || element.Any(IsControlOrNewline)) sb.Append(' ');
            else sb.Append(element);
        }
        var collapsed = string.Join(" ", sb.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var result = collapsed.Trim();
        if (result.Length > MaxActivityLength) result = TruncateTextElements(result, MaxActivityLength).Trim();
        return result.Length == 0 ? ActivityResolver.UnknownLabel : result;
    }

    private static bool IsControlOrNewline(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>Al più <paramref name="max"/> unità UTF-16 (come conta <c>.length</c> il server), senza spezzare un carattere.</summary>
    private static string TruncateTextElements(string s, int max)
    {
        var sb = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            if (sb.Length + element.Length > max) break;
            sb.Append(element);
        }
        return sb.ToString();
    }
}

/// <summary>
/// LA PRIVACY DEI NOMI: la stessa regola del server (<c>motivoNomeRifiutato</c> in <c>agente.ts</c>). Tutto ciò che
/// contiene <c>/</c>, <c>?</c>, <c>@</c> o <c>://</c> ha la forma di un indirizzo, di un percorso o di una casella di
/// posta, e non esce dal PC.
/// </summary>
public static class PrivacyFilter
{
    public static string? RejectReason(string nome)
    {
        var n = nome.Trim();
        if (n.Length == 0) return "nome vuoto";
        if (n.Length > SyncPayloadBuilder.MaxActivityLength) return $"nome oltre {SyncPayloadBuilder.MaxActivityLength} caratteri";
        if (n.Contains("://", StringComparison.Ordinal)) return "il nome contiene «://»: sembra un indirizzo";
        foreach (var c in new[] { "/", "?", "@" })
            if (n.Contains(c, StringComparison.Ordinal)) return $"il nome contiene «{c}»: solo nomi aggregati, mai indirizzi";
        if (n.Any(ch => ch < 0x20 || ch == 0x7f)) return "il nome contiene caratteri di controllo";
        return null;
    }
}
