namespace ActivityTracker.Core;

/// <summary>
/// Metodo del resto maggiore: ripartisce <c>units</c> (1000 = per mille) proporzionalmente ai pesi, con somma esatta.
/// Tie-break deterministico: a parità di resto vince la voce con peso maggiore, poi l'etichetta in ordine
/// alfabetico, poi l'indice.
/// </summary>
public static class LargestRemainder
{
    public readonly record struct Entry(double Weight, string Label);

    public static int[] Apportion(IReadOnlyList<Entry> entries, int units = 1000)
    {
        if (entries.Count == 0) return [];
        // Pesi in millesimi interi: aritmetica esatta, nessun errore di virgola mobile nei confronti.
        var weights = entries.Select(e => Math.Max(0, Epoch.Round(e.Weight * 1000))).ToArray();
        var total = weights.Sum();
        if (total <= 0) return new int[entries.Count];

        var floors = new long[entries.Count];
        var remainders = new long[entries.Count];
        for (var i = 0; i < weights.Length; i++)
        {
            var numerator = weights[i] * units;
            floors[i] = numerator / total;
            remainders[i] = numerator % total;
        }
        var leftover = units - floors.Sum();
        var order = Enumerable.Range(0, entries.Count).ToList();
        order.Sort((a, b) =>
        {
            if (remainders[a] != remainders[b]) return remainders[b].CompareTo(remainders[a]);
            if (weights[a] != weights[b]) return weights[b].CompareTo(weights[a]);
            var byLabel = string.CompareOrdinal(entries[a].Label, entries[b].Label);
            return byLabel != 0 ? byLabel : a.CompareTo(b);
        });
        var result = floors.Select(f => (int)f).ToArray();
        foreach (var idx in order)
        {
            if (leftover <= 0) break;
            result[idx] += 1;
            leftover -= 1;
        }
        return result;
    }
}
