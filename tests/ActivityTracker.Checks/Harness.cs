using ActivityTracker.Core;

namespace ActivityTracker.Checks;

/// <summary>Verifiche senza framework di test, come sul Mac: <c>ok</c> / <c>FAIL</c> riga per riga.</summary>
public static class H
{
    public static int Passes;
    public static int Failures;

    public static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            Passes++;
            Console.WriteLine($"  ok    {name}");
        }
        else
        {
            Failures++;
            Console.WriteLine($"  FAIL  {name}" + (detail.Length == 0 ? "" : $"  — {detail}"));
        }
    }

    /// <summary>Come <see cref="Check"/>, ma la condizione può lanciare: un errore conta come fallimento.</summary>
    public static void CheckT(string name, Func<bool> condition, Func<string>? detail = null)
    {
        try
        {
            var ok = condition();
            Check(name, ok, ok ? "" : detail?.Invoke() ?? "");
        }
        catch (Exception e)
        {
            Check(name, false, "errore: " + e.Message);
        }
    }

    public static void Section(string title) => Console.WriteLine($"\n== {title}");

    public static bool Approx(double a, double b, double tolerance = 0.001) => Math.Abs(a - b) <= tolerance;

    /// <summary>2024-03-10 10:00:00 UTC: un istante fisso, lontano da mezzanotte.</summary>
    public static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_710_064_800);
    public static readonly DayClock Utc = new(TimeZoneInfo.Utc);
    public static readonly DayClock RomeClock = new(DayClock.Rome);
    public static string Today => Utc.DayString(T0);

    public static readonly Subject AppA = new("com.example.a", "App A");
    public static readonly Subject AppB = new("com.example.b", "App B");
    public static readonly Subject ChromeYouTube = new("chrome.exe", "Google Chrome", "www.youtube.com");

    public static (ClassificationEngine, RecordingSink) MakeEngine(double threshold = 180)
    {
        var sink = new RecordingSink();
        return (new ClassificationEngine(sink, threshold, Utc), sink);
    }

    public static string Join<T>(IEnumerable<T> items) => "[" + string.Join(", ", items) + "]";
}

public sealed class RecordingSink : ISegmentSink
{
    public List<ConfirmedSlice> Slices { get; } = [];
    public void Append(ConfirmedSlice slice) => Slices.Add(slice);

    public double Total(ActivityState state, Func<ConfirmedSlice, bool>? predicate = null) =>
        Slices.Where(s => s.State == state && (predicate?.Invoke(s) ?? true)).Sum(s => s.Duration);

    public double Total(ActivityState state, string day) => Total(state, s => s.Day == day);
    public double All => Slices.Sum(s => s.Duration);
}

/// <summary>Simulatore: tick sintetici ogni secondo, tempi assoluti fissi.</summary>
public sealed class Simulation
{
    public ClassificationEngine Engine { get; }
    public DateTimeOffset Now { get; private set; }
    public DateTimeOffset LastInput { get; private set; }

    public Simulation(ClassificationEngine engine, DateTimeOffset start, bool sessionOpen = true, Subject? foreground = null)
    {
        Engine = engine;
        Now = start;
        LastInput = start;
        engine.Observe(new Observation(start, sessionOpen, start, foreground, null));
    }

    public void Run(int seconds, bool input, Subject? foreground, Subject? video = null, bool sessionOpen = true, bool displayAsleep = false)
    {
        for (var i = 0; i < seconds; i++)
        {
            Now = Now.AddSeconds(1);
            if (input) LastInput = Now;
            Engine.Observe(new Observation(Now, sessionOpen, LastInput, foreground, video, displayAsleep));
        }
    }

    /// <summary>Tempo che passa senza tick (timer sospeso).</summary>
    public void Jump(double seconds) => Now = Now.AddSeconds(seconds);
}
