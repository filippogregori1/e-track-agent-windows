namespace ActivityTracker.Core;

/// <summary>Una giornata da inviare: i tratti costruiti e il loro hash (quello registrato in <c>sync_days</c> a invio riuscito).</summary>
public sealed record PreparedInvio(string Giorno, IReadOnlyList<Tratto> Tratti)
{
    public string Hash { get; } = SyncPayloadBuilder.Hash(Tratti);
}

/// <summary>Il server ha detto che una sessione era già chiusa all'istante <c>At</c> (tratto «dopo la chiusura»).</summary>
public sealed record Truncation(string Session, DateTimeOffset At);

public enum TransmitKind
{
    /// <summary>Accettato; <c>Dropped</c> tratti tolti dopo un 422 (chiusure d'ufficio o tratti respinti uno per uno).</summary>
    Sent,
    /// <summary>Respinto (400/413, o 422 senza indice): la giornata non si ritenta finché i suoi tratti non cambiano.</summary>
    Rejected,
    /// <summary>Errore di rete, 5xx, 401…: si ritenta al giro successivo.</summary>
    Failed,
}

public sealed record TransmitOutcome(TransmitKind Kind, IReadOnlyList<Truncation> Truncations, RispostaSegmenti? Response = null,
                                     int Dropped = 0, string? Message = null, SyncFault? Fault = null);

/// <summary>
/// Il cuore del sync con equipe-track, senza UI né timer (li ha <c>SyncService</c>): registro delle letture di
/// <c>GET /api/agente/sessione</c>, scelta delle giornate da inviare, invio con recupero dai 422, esiti in <c>sync_days</c>.
///
/// Non è thread-safe: <c>Apply</c>, <c>PendingInvii</c> e <c>Finish</c> vanno chiamati dallo stesso thread (nell'app
/// quello dell'interfaccia); <c>TransmitAsync</c> è statica e non tocca lo stato, così la rete può girare altrove.
/// </summary>
public sealed class TrackingSync
{
    public Store Store { get; }
    public TrackingLedger Ledger { get; private set; }
    /// <summary>Ora del server − ora del PC, in secondi interi, dall'ultima lettura.</summary>
    public double ClockOffset { get; private set; }
    public DayClock GiornoClock { get; set; } = SyncPayloadBuilder.ServerDayClock;

    public TrackingSync(Store store, DateTimeOffset? now = null)
    {
        Store = store;
        var ledger = new TrackingLedger(store.LedgerIntervals());
        ledger.Prune((now ?? DateTimeOffset.UtcNow).AddSeconds(-TrackingLedger.Retention));
        Ledger = ledger;
    }

    // Letture della sessione

    /// <summary>Applica una risposta di <c>GET /api/agente/sessione</c> ricevuta a <paramref name="receivedAt"/>.
    /// Restituisce lo stato precedente (null se non si sapeva) e quello nuovo.</summary>
    public (ServerState? Previous, ServerState Current) Apply(RispostaSessione r, DateTimeOffset sentAt, DateTimeOffset receivedAt)
    {
        if (SyncJson.ParseInstant(r.Adesso) is { } server)
        {
            var midpoint = sentAt + (receivedAt - sentAt) / 2;
            ClockOffset = Math.Round((server - midpoint).TotalSeconds, MidpointRounding.AwayFromZero);
        }
        return Record(ServerState.From(r), receivedAt);
    }

    /// <summary>Registra uno stato all'istante dato (usato da <c>Apply</c> e, allo spegnimento, per confermare lo stato corrente).</summary>
    public (ServerState? Previous, ServerState Current) Record(ServerState state, DateTimeOffset t)
    {
        var previous = Ledger.Current(t);
        Ledger.Record(state, t);
        Ledger.Prune(t.AddSeconds(-TrackingLedger.Retention));
        Store.ReplaceLedger(Ledger.Intervals);
        return (previous, state);
    }

    public ServerState? CurrentState(DateTimeOffset now) => Ledger.Current(now);

    public List<TimeWindow> ContentWindows(DateTimeOffset now) => Ledger.ContentWindows(now);

    /// <summary>Il tempo coperto dal registro: i segmenti da leggere per costruire gli invii.</summary>
    public TimeWindow? DataRange
    {
        get
        {
            if (Ledger.Intervals.Count == 0) return null;
            var first = Ledger.Intervals[0];
            var last = Ledger.Intervals.Max(i => i.End);
            return new TimeWindow(first.StartDate, Epoch.FromSeconds(Math.Max(last, first.Start)));
        }
    }

    // Invii

    /// <summary>Le giornate da inviare: quelle i cui tratti differiscono dall'ultimo esito registrato (inviato o
    /// respinto), dalla più recente. Una giornata senza tratti si invia solo se prima ne aveva (per svuotarla sul server).</summary>
    public List<PreparedInvio> PendingInvii(IEnumerable<Segment> segments, IEnumerable<ProvisionalSlice> provisional, ActivityResolver resolver)
    {
        var builder = new SyncPayloadBuilder(resolver, GiornoClock);
        var byDay = builder.Tratti(segments, provisional, Ledger);
        var records = new Dictionary<string, SyncDayRecord>();
        foreach (var r in Store.SyncDays()) records.TryAdd(r.Day, r);
        var emptyHash = SyncPayloadBuilder.Hash([]);
        var output = new List<PreparedInvio>();
        var giorni = Ledger.Giorni(GiornoClock);
        giorni.UnionWith(byDay.Keys);
        foreach (var giorno in giorni.OrderByDescending(g => g, StringComparer.Ordinal))
        {
            var prepared = new PreparedInvio(giorno, byDay.TryGetValue(giorno, out var t) ? t : []);
            if (records.TryGetValue(giorno, out var record))
            {
                if (record.PayloadHash == prepared.Hash) continue;
            }
            else if (prepared.Hash == emptyHash)
            {
                continue;
            }
            output.Add(prepared);
        }
        return output;
    }

    /// <summary>
    /// Invia una giornata. Su un 422 con indice toglie il tratto respinto e ritenta (al massimo <paramref name="maxRetries"/>
    /// volte): se il server dice che il tratto cade dopo la chiusura della sua sessione (chiusura d'ufficio: mezzanotte
    /// UTC, otto ore), toglie quel tratto e i successivi della stessa sessione e lo segnala come <see cref="Truncation"/>.
    /// </summary>
    public static async Task<TransmitOutcome> TransmitAsync(PreparedInvio p, SyncClient client, double clockOffset, int maxRetries = 5,
                                                            CancellationToken ct = default)
    {
        var tratti = p.Tratti.ToList();
        var dropped = 0;
        var truncations = new List<Truncation>();
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var invio = SyncPayloadBuilder.Invio(p.Giorno, tratti, clockOffset);
            try
            {
                var r = await client.InviaSegmentiAsync(invio, ct).ConfigureAwait(false);
                return new TransmitOutcome(TransmitKind.Sent, truncations, r, dropped);
            }
            catch (SyncException e) when (e.Fault.Kind == SyncErrorKind.Unprocessable)
            {
                var message = e.Fault.Detail ?? "";
                if (e.Fault.Index is not { } i || i < 0 || i >= tratti.Count)
                    return new TransmitOutcome(TransmitKind.Rejected, truncations, Message: message);
                var t = tratti[i];
                if (t.Sessione is { } session && (message.Contains("chiusura", StringComparison.Ordinal) || message.Contains("istante attuale", StringComparison.Ordinal)))
                {
                    truncations.Add(new Truncation(session, t.Start));
                    var before = tratti.Count;
                    tratti.RemoveAll(x => x.Sessione == session && x.Start >= t.Start);
                    dropped += before - tratti.Count;
                }
                else
                {
                    tratti.RemoveAt(i);
                    dropped += 1;
                }
            }
            catch (SyncException e) when (e.Fault.Kind == SyncErrorKind.BadRequest)
            {
                return new TransmitOutcome(TransmitKind.Rejected, truncations, Message: e.Fault.Detail);
            }
            catch (SyncException e)
            {
                return new TransmitOutcome(TransmitKind.Failed, truncations, Fault: e.Fault);
            }
            catch (Exception e)
            {
                return new TransmitOutcome(TransmitKind.Failed, truncations, Fault: new SyncFault(SyncErrorKind.Network, e.Message));
            }
        }
        return new TransmitOutcome(TransmitKind.Rejected, truncations, Message: "troppi tratti respinti dal server");
    }

    /// <summary>Registra l'esito di un invio: le chiusure scoperte accorciano il registro, l'hash va in <c>sync_days</c>.</summary>
    public void Finish(PreparedInvio p, TransmitOutcome outcome, DateTimeOffset now)
    {
        var changed = false;
        foreach (var t in outcome.Truncations)
            if (Ledger.Truncate(t.Session, t.At)) changed = true;
        if (changed) Store.ReplaceLedger(Ledger.Intervals);
        switch (outcome.Kind)
        {
            case TransmitKind.Sent:
                Store.SaveSyncDay(new SyncDayRecord(p.Giorno, p.Hash, Epoch.Seconds(now)));
                break;
            case TransmitKind.Rejected:
                Store.SaveSyncDay(new SyncDayRecord(p.Giorno, p.Hash, Epoch.Seconds(now), true, outcome.Message));
                break;
        }
        if (Ledger.Intervals.Count > 0)
            Store.DeleteSyncDays(GiornoClock.DayString(Ledger.Intervals[0].StartDate.AddSeconds(-86_400)));
    }

    /// <summary>Credenziali nuove o scollegate: si dimenticano registro ed esiti.</summary>
    public void Reset()
    {
        Ledger = new TrackingLedger();
        ClockOffset = 0;
        Store.ReplaceLedger([]);
        Store.ClearSyncDays();
    }
}
