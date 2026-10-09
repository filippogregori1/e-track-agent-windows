namespace ActivityTracker.Core;

/// <summary>Astrazione minima della persistenza usata dal coalescer.</summary>
public interface ISegmentWriter
{
    void Save(Segment segment);
    void Save(IList<Segment> segments);
}

/// <summary>
/// Riceve gli intervalli confermati e li fonde in segmenti contigui con la stessa chiave.
/// Tiene in memoria la coda (<see cref="Tail"/>, estendibile) e i segmenti chiusi non ancora persistiti.
/// </summary>
public sealed class SegmentCoalescer : ISegmentSink
{
    /// <summary>Tolleranza di contiguità tra fine del segmento e inizio dell'intervallo successivo.</summary>
    public double ContiguityTolerance { get; }

    public Segment? Tail { get; private set; }
    private List<Segment> _completed = [];

    public SegmentCoalescer(double contiguityTolerance = 0.01)
    {
        ContiguityTolerance = contiguityTolerance;
    }

    public void Append(ConfirmedSlice slice)
    {
        if (slice.End <= slice.Start) return;
        if (Tail is { } t && t.SameKey(slice) && Math.Abs(t.End - Epoch.Seconds(slice.Start)) <= ContiguityTolerance)
        {
            t.End = Epoch.Seconds(slice.End);
            return;
        }
        if (Tail is { } old) _completed.Add(old);
        Tail = Segment.From(slice);
    }

    /// <summary>Segmenti in memoria (chiusi + coda), in ordine cronologico. Copie: chi legge non tocca lo stato.</summary>
    public List<Segment> Pending
    {
        get
        {
            var all = _completed.Select(s => s.Copy()).ToList();
            if (Tail is { } t) all.Add(t.Copy());
            return all;
        }
    }

    /// <summary>Scrive su <paramref name="store"/> i segmenti chiusi e aggiorna/inserisce la coda (che resta estendibile).</summary>
    public void Flush(ISegmentWriter store)
    {
        if (_completed.Count > 0)
        {
            store.Save(_completed);
            _completed = [];
        }
        if (Tail is { } t) store.Save(t);
    }

    /// <summary>
    /// Sovrappone lo stato in memoria alle righe lette dal DB: la coda già persistita sostituisce
    /// la propria riga (stesso id), i segmenti mai scritti vengono aggiunti.
    /// </summary>
    public List<Segment> Overlay(IEnumerable<Segment> rows)
    {
        var result = rows.ToList();
        foreach (var seg in Pending)
        {
            var idx = seg.Id is { } id ? result.FindIndex(r => r.Id == id) : -1;
            if (idx >= 0) result[idx] = seg;
            else result.Add(seg); // Mai scritto, oppure persistito in un giorno non letto: il builder filtra per giorno.
        }
        return result;
    }
}
