using ActivityTracker.Core;
using Microsoft.Data.Sqlite;
using static ActivityTracker.Checks.H;

namespace ActivityTracker.Checks;

/// <summary>Motore, mezzanotte, regole, resto maggiore, formato, report, storage: gli stessi casi del Mac.</summary>
public static class CoreChecks
{
    public static void Run()
    {
        Engine();
        Midnight();
        Resolver();
        Remainder();
        Formatter();
        Builder();
        StoreAndCoalescing();
    }

    private static void Engine()
    {
        Section("Motore: input continuo");
        {
            var (engine, sink) = MakeEngine();
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(600, true, AppA);
            engine.Finish(sim.Now);
            Check("10 min di input su App A → 600 s attivi", Approx(sink.Total(ActivityState.Active), 600), $"{sink.Total(ActivityState.Active)}");
            Check("tutto attribuito ad App A", Approx(sink.Total(ActivityState.Active, s => s.Subject == AppA), 600));
            Check("nessun altro stato", sink.All == sink.Total(ActivityState.Active));
            Check("coda vuota alla fine", engine.ProvisionalSlices.Count == 0);
        }

        Section("Motore: retroattività (regola 3)");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(179, false, AppA);
            Check("sotto soglia: niente confermato, 179 s in coda provvisoria",
                  sink.Slices.Count == 0 && Approx(engine.ProvisionalSlices.Sum(s => s.Duration), 179));
            Check("sotto soglia: non ancora inattivo", !engine.IsIdleConfirmed);
            sim.Run(1, false, AppA);
            Check("alla soglia: tutta la coda diventa unused (180 s)", Approx(sink.Total(ActivityState.Unused), 180), $"{sink.Total(ActivityState.Unused)}");
            Check("alla soglia: coda svuotata e stato inattivo", engine.ProvisionalSlices.Count == 0 && engine.IsIdleConfirmed);
            sim.Run(420, false, AppA);
            Check("10 min fermo senza video → 600 s unused, 0 attivi (non 3+7)",
                  Approx(sink.Total(ActivityState.Unused), 600) && Approx(sink.Total(ActivityState.Active), 0),
                  $"unused={sink.Total(ActivityState.Unused)} active={sink.Total(ActivityState.Active)}");
        }

        Section("Motore: gap < N poi input");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(120, false, AppA);
            sim.Run(1, true, AppA);
            Check("2 min fermo poi input → gap conteggiato attivo (121 s)", Approx(sink.Total(ActivityState.Active), 121), $"{sink.Total(ActivityState.Active)}");
            Check("nessun unused", Approx(sink.Total(ActivityState.Unused), 0));
        }

        Section("Motore: video in riproduzione (passivo)");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: ChromeYouTube);
            sim.Run(1200, false, ChromeYouTube, ChromeYouTube);
            Check("20 min fermo con video YouTube → 1200 s passivi", Approx(sink.Total(ActivityState.Passive), 1200), $"{sink.Total(ActivityState.Passive)}");
            Check("passivo attribuito al proprietario del video", Approx(sink.Total(ActivityState.Passive, s => s.Subject?.Domain == "www.youtube.com"), 1200));
            Check("nessun attivo né unused", Approx(sink.Total(ActivityState.Active), 0) && Approx(sink.Total(ActivityState.Unused), 0));

            var (engine2, sink2) = MakeEngine(180);
            var sim2 = new Simulation(engine2, T0, foreground: AppA);
            sim2.Run(1200, false, AppA, null);
            Check("audio-only (video nil) 20 min fermo → 1200 s unused", Approx(sink2.Total(ActivityState.Unused), 1200), $"{sink2.Total(ActivityState.Unused)}");
        }

        Section("Motore: video attribuito al proprietario, non all'app in primo piano");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppB);
            sim.Run(300, false, AppB, ChromeYouTube);
            Check("passivo su YouTube anche con App B in primo piano",
                  Approx(sink.Total(ActivityState.Passive, s => s.Subject == ChromeYouTube), 300) && Approx(sink.Total(ActivityState.Passive, s => s.Subject == AppB), 0));
        }

        Section("Motore: sessione chiusa e buchi");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(300, true, AppA);
            sim.Run(600, false, null, sessionOpen: false);
            sim.Run(300, true, AppA);
            engine.Finish(sim.Now);
            Check("lock a metà → 600 s off", Approx(sink.Total(ActivityState.Off), 600), $"{sink.Total(ActivityState.Off)}");
            Check("lock a metà → 600 s attivi (fuori dal 100% il resto)", Approx(sink.Total(ActivityState.Active), 600), $"{sink.Total(ActivityState.Active)}");

            var (engine2, sink2) = MakeEngine(180);
            var sim2 = new Simulation(engine2, T0, foreground: AppA);
            sim2.Run(10, true, AppA);
            sim2.Jump(60);
            sim2.Run(1, true, AppA);
            Check("buco di 61 s tra due tick → tutto il buco è off", Approx(sink2.Total(ActivityState.Off), 61), $"{sink2.Total(ActivityState.Off)}");
            Check("buco: i 10 s precedenti restano attivi", Approx(sink2.Total(ActivityState.Active), 10), $"{sink2.Total(ActivityState.Active)}");

            var (engine3, sink3) = MakeEngine(180);
            var sim3 = new Simulation(engine3, T0, foreground: AppA);
            sim3.Run(100, false, AppA);
            sim3.Run(10, false, null, sessionOpen: false);
            Check("coda < N chiusa da lock → 100 s attivi", Approx(sink3.Total(ActivityState.Active), 100), $"{sink3.Total(ActivityState.Active)}");
            Check("coda < N chiusa da lock → 10 s off", Approx(sink3.Total(ActivityState.Off), 10));

            var (engine4, sink4) = MakeEngine(180);
            var sim4 = new Simulation(engine4, T0, foreground: AppA);
            sim4.Run(400, false, AppA);
            sim4.Run(10, false, null, sessionOpen: false);
            Check("coda ≥ N poi lock → 400 s unused (soglia già scattata)", Approx(sink4.Total(ActivityState.Unused), 400) && Approx(sink4.Total(ActivityState.Active), 0));

            var (engine5, sink5) = MakeEngine(180);
            var sim5 = new Simulation(engine5, T0, foreground: AppA);
            sim5.Run(4, true, AppA);
            sim5.Jump(3); // 4 s tra due tick: entro la tolleranza di 5 s
            sim5.Run(1, true, AppA);
            Check("buco ≤ 5 s → nessun off", Approx(sink5.Total(ActivityState.Off), 0) && Approx(sink5.Total(ActivityState.Active), 8));
        }

        Section("Motore: display spento prima della soglia");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(300, true, AppA);
            sim.Run(120, false, AppA);
            sim.Run(60, false, null, sessionOpen: false, displayAsleep: true);
            Check("display spento dopo 120 s < N → 120 s unused (retroattivo)", Approx(sink.Total(ActivityState.Unused), 120), $"{sink.Total(ActivityState.Unused)}");
            Check("display spento dopo 120 s < N → restano attivi solo i 300 s con input", Approx(sink.Total(ActivityState.Active), 300), $"{sink.Total(ActivityState.Active)}");
            Check("display spento → da lì in poi off", Approx(sink.Total(ActivityState.Off), 60), $"{sink.Total(ActivityState.Off)}");

            var (engine2, sink2) = MakeEngine(180);
            var sim2 = new Simulation(engine2, T0, foreground: AppA);
            sim2.Run(90, false, AppA, ChromeYouTube);
            sim2.Run(10, false, null, sessionOpen: false, displayAsleep: true);
            Check("display spento dopo 90 s con video → 90 s passivi al proprietario del video",
                  Approx(sink2.Total(ActivityState.Passive, s => s.Subject == ChromeYouTube), 90) && Approx(sink2.Total(ActivityState.Active), 0),
                  $"passive={sink2.Total(ActivityState.Passive)} active={sink2.Total(ActivityState.Active)}");

            var (engine3, sink3) = MakeEngine(180);
            var sim3 = new Simulation(engine3, T0, foreground: AppA);
            sim3.Run(120, false, AppA);
            sim3.Run(10, false, null, sessionOpen: false, displayAsleep: false);
            Check("stessi 120 s chiusi da blocco (display acceso) → restano attivi (regola invariata)",
                  Approx(sink3.Total(ActivityState.Active), 120) && Approx(sink3.Total(ActivityState.Unused), 0), $"{sink3.Total(ActivityState.Active)}");
        }

        Section("Motore: soglia modificabile a runtime");
        {
            var (engine, sink) = MakeEngine(180);
            engine.Threshold = 60;
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(60, false, AppA);
            Check("soglia 60 s → scatta dopo 60 s", Approx(sink.Total(ActivityState.Unused), 60) && engine.IsIdleConfirmed);
            engine.Threshold = 10;
            Check("soglia minima 30 s", engine.Threshold == 30);
        }

        Section("Motore: cambio app nella coda");
        {
            var (engine, sink) = MakeEngine(180);
            var sim = new Simulation(engine, T0, foreground: AppA);
            sim.Run(50, false, AppA);
            sim.Run(50, false, AppB);
            sim.Run(1, true, AppB);
            Check("ogni secondo della coda sulla propria app (A=50, B=51)",
                  Approx(sink.Total(ActivityState.Active, s => s.Subject == AppA), 50) && Approx(sink.Total(ActivityState.Active, s => s.Subject == AppB), 51),
                  $"A={sink.Total(ActivityState.Active, s => s.Subject == AppA)} B={sink.Total(ActivityState.Active, s => s.Subject == AppB)}");
        }
    }

    private static void Midnight()
    {
        Section("Mezzanotte");
        var dayStart = Utc.StartOfDay(T0);
        var nextDay = Utc.NextMidnight(T0);
        Check("nextMidnight è +1 giorno dall'inizio giorno", Approx((nextDay - dayStart).TotalSeconds, 86_400));
        var pieces = Utc.Split(nextDay.AddSeconds(-120), nextDay.AddSeconds(120));
        Check("split a cavallo di mezzanotte → 2 pezzi", pieces.Count == 2);
        Check("split: pezzi di 120 s su giorni diversi",
              pieces.Count == 2 && Approx((pieces[0].End - pieces[0].Start).TotalSeconds, 120)
              && Approx((pieces[1].End - pieces[1].Start).TotalSeconds, 120) && pieces[0].Day != pieces[1].Day);
        Check("split: fine esattamente a mezzanotte → 1 pezzo", Utc.Split(nextDay.AddSeconds(-60), nextDay).Count == 1);
        Check("split: intervallo vuoto → 0 pezzi", Utc.Split(nextDay, nextDay).Count == 0);

        var (engine, sink) = MakeEngine(180);
        var start = nextDay.AddSeconds(-120);
        var sim = new Simulation(engine, start, foreground: AppA);
        sim.Run(240, true, AppA);
        var day1 = Utc.DayString(start);
        var day2 = Utc.DayString(nextDay);
        Check("segmenti spezzati a mezzanotte: 120 s giorno 1, 120 s giorno 2",
              Approx(sink.Total(ActivityState.Active, day1), 120) && Approx(sink.Total(ActivityState.Active, day2), 120),
              $"d1={sink.Total(ActivityState.Active, day1)} d2={sink.Total(ActivityState.Active, day2)}");
        Check("nessun pezzo attraversa la mezzanotte", sink.Slices.All(s => Utc.DayString(s.Start) == s.Day && s.End <= Utc.NextMidnight(s.Start)));

        // 1_710_115_000 = 2024-03-10 23:56:40 UTC = 2024-03-11 00:56:40 a Roma (UTC+1).
        var instant = DateTimeOffset.FromUnixTimeSeconds(1_710_115_000);
        Check("giorno locale con fuso iniettato (Roma)", RomeClock.DayString(instant) == "2024-03-11", RomeClock.DayString(instant));
        Check("giorno locale UTC per lo stesso istante", Utc.DayString(instant) == "2024-03-10");
        // Ora legale: a Roma il 31 marzo 2024 dura 23 ore.
        var dst = RomeClock.StartOfDay(DateTimeOffset.Parse("2024-03-31T12:00:00+02:00"));
        Check("giornata dell'ora legale (Roma) lunga 23 h", Approx((RomeClock.NextMidnight(dst) - dst).TotalHours, 23));
    }

    private static void Resolver()
    {
        Section("Resolver");
        var resolver = new ActivityResolver(ActivityRule.Seed);
        string Name(Subject s) => resolver.Activity(s);
        Check("m.youtube.com → YouTube", Name(new Subject(Domain: "m.youtube.com")) == "YouTube");
        Check("youtu.be → YouTube", Name(new Subject(Domain: "youtu.be")) == "YouTube");
        Check("www.youtube.com → YouTube", Name(new Subject(Domain: "www.youtube.com")) == "YouTube");
        Check("notyoutube.com NON è sottodominio", Name(new Subject(Domain: "notyoutube.com")) == "notyoutube.com");
        Check("dominio sconosciuto → host senza www", Name(new Subject(Domain: "www.example.org")) == "example.org");
        Check("app senza dominio → nome app", Name(new Subject("explorer.exe", "Esplora risorse")) == "Esplora risorse");
        Check("regola app: zoom.exe → Zoom", Name(new Subject("zoom.exe", "Zoom Meetings")) == "Zoom");
        Check("regola app: ms-teams.exe → Teams", Name(new Subject("ms-teams.exe", "Microsoft Teams")) == "Teams");
        Check("regola app senza maiuscole/minuscole", Name(new Subject("Zoom.EXE", "Zoom Meetings")) == "Zoom");
        Check("regola title: Firefox con 'YouTube' nel titolo",
              Name(new Subject("firefox.exe", "Firefox", TitleHint: "Lofi beats - YouTube — Mozilla Firefox")) == "YouTube");
        Check("title case-insensitive", Name(new Subject(AppName: "Firefox", TitleHint: "qualcosa su YOUTUBE")) == "YouTube");
        Check("priorità domain > title", Name(new Subject(Domain: "github.com", TitleHint: "YouTube")) == "GitHub");
        Check("priorità title > app", Name(new Subject("zoom.exe", TitleHint: "YouTube")) == "YouTube");
        Check("subject nil → Sconosciuto", resolver.Activity(null) == ActivityResolver.UnknownLabel);
        Check("host da URL http", ActivityResolver.HostFromUrl("https://M.YouTube.com/watch?v=x") == "m.youtube.com");
        Check("host da URL non http → nil", ActivityResolver.HostFromUrl("chrome://settings") is null
              && ActivityResolver.HostFromUrl("about:blank") is null && ActivityResolver.HostFromUrl("file:///C:/x/a.html") is null);
        var custom = new ActivityResolver([new(RuleKind.Domain, "google.com", "Google"), new(RuleKind.Domain, "docs.google.com", "Google Docs")]);
        Check("dominio più specifico vince", custom.Activity(new Subject(Domain: "docs.google.com")) == "Google Docs"
              && custom.Activity(new Subject(Domain: "mail.google.com")) == "Google");

        Section("Windows: dominio dalla barra degli indirizzi");
        string? Bar(string s) => ActivityResolver.HostFromAddressBar(s);
        Check("Chrome/Edge senza schema: youtube.com/watch?v=x → youtube.com", Bar("youtube.com/watch?v=x") == "youtube.com");
        Check("m.youtube.com/… → m.youtube.com", Bar("m.youtube.com/shorts/abc") == "m.youtube.com");
        Check("con schema https", Bar("https://www.github.com/a/b") == "www.github.com");
        Check("Firefox mostra lo schema: http://example.org", Bar("http://example.org/") == "example.org");
        Check("localhost:3000 è un indirizzo", Bar("localhost:3000/x") == "localhost");
        Check("pagine interne → nessun dominio", Bar("chrome://settings") is null && Bar("edge://newtab/") is null && Bar("about:blank") is null);
        Check("file locali → nessun dominio", Bar("file:///C:/Users/x/a.html") is null && Bar(@"C:\Users\x\a.html") is null);
        Check("ricerca in corso di digitazione → nessun dominio", Bar("come cucinare la pasta") is null && Bar("pasta") is null);
        Check("barra vuota → nessun dominio", Bar("") is null && ActivityResolver.HostFromAddressBar(null) is null);
    }

    private static void Remainder()
    {
        Section("Resto maggiore");
        static LargestRemainder.Entry E(double w, string l) => new(w, l);
        var three = LargestRemainder.Apportion([E(100, "b"), E(100, "a"), E(100, "c")]);
        Check("tre voci uguali → 333/334/333 con somma 1000, extra alla prima in ordine alfabetico", three.SequenceEqual([333, 334, 333]), Join(three));
        var seven = LargestRemainder.Apportion(Enumerable.Range(0, 7).Select(i => E(1, $"v{i}")).ToList());
        Check("sette voci uguali → somma 1000", seven.Sum() == 1000 && seven.Count(x => x == 143) == 6, Join(seven));
        var many = LargestRemainder.Apportion(Enumerable.Range(0, 50).Select(i => E((i % 7 + 1) * 0.37, $"v{i}")).ToList());
        Check("50 voci piccole → somma 1000", many.Sum() == 1000, $"{many.Sum()}");
        var skewed = LargestRemainder.Apportion([E(997, "big"), E(1, "a"), E(1, "b"), E(1, "c")]);
        Check("voce dominante + tre piccole → somma 1000 e piccole ≥ 1", skewed.Sum() == 1000 && skewed.Skip(1).All(x => x >= 1), Join(skewed));
        var tie = LargestRemainder.Apportion([E(2, "z"), E(1, "a"), E(1, "b")]);
        Check("parità di resto: vince il peso maggiore", tie.SequenceEqual([500, 250, 250]), Join(tie));
        Check("totale zero → tutti zero", LargestRemainder.Apportion([E(0, "a"), E(0, "b")]).SequenceEqual([0, 0]));
        Check("lista vuota → vuota", LargestRemainder.Apportion([]).Length == 0);
        Check("una sola voce → 1000", LargestRemainder.Apportion([E(12.3, "solo")]).SequenceEqual([1000]));
        var a = LargestRemainder.Apportion(Enumerable.Range(0, 30).Select(i => E(i * 7 % 11 + 0.5, $"n{i}")).ToList());
        var b = LargestRemainder.Apportion(Enumerable.Range(0, 30).Select(i => E(i * 7 % 11 + 0.5, $"n{i}")).ToList());
        Check("deterministico", a.SequenceEqual(b) && a.Sum() == 1000);
    }

    private static void Formatter()
    {
        Section("Formatter");
        var row = new ReportRow("YouTube", 2 * 3600 + 35 * 60, 3600 + 50 * 60, 336, false);
        Check("riga esatta", ReportFormatter.Line(row) == "YouTube   33,6%  2h35  (di cui passivo 1h50)", $"«{ReportFormatter.Line(row)}»");
        var unused = new ReportRow(ReportBuilder.UnusedLabel, 3600 + 42 * 60, 0, 220, true);
        Check("riga Attivo senza utilizzo", ReportFormatter.Line(unused) == "Attivo senza utilizzo   22,0%  1h42", $"«{ReportFormatter.Line(unused)}»");
        var noPassive = new ReportRow("GitHub", 35 * 60, 59, 85, false);
        Check("passivo < 1 min → nessuna nota", ReportFormatter.Line(noPassive) == "GitHub   8,5%  35m", $"«{ReportFormatter.Line(noPassive)}»");
        Check("percentuali", ReportFormatter.Percent(1000) == "100,0%" && ReportFormatter.Percent(5) == "0,5%" && ReportFormatter.Percent(0) == "0,0%");
        Check("durate", ReportFormatter.Duration(2 * 3600 + 5 * 60) == "2h05" && ReportFormatter.Duration(35 * 60) == "35m"
              && ReportFormatter.Duration(59) == "<1m" && ReportFormatter.Duration(60) == "1m" && ReportFormatter.Duration(0) == "<1m"
              && ReportFormatter.Duration(10 * 3600) == "10h00" && ReportFormatter.Duration(3599) == "59m");
        var report = new Report(Today, [row, unused], row.Total + unused.Total, 3 * 3600 + 10 * 60);
        Check("riga totale", ReportFormatter.TotalLine(report) == "Totale 55,6% · Fuori sessione 3h10", $"«{ReportFormatter.TotalLine(report)}»");
        var full = new Report(Today, [new ReportRow("A", 100, 0, 1000, false)], 100, 0);
        Check("riga totale senza off", ReportFormatter.TotalLine(full) == "Totale 100,0%", $"«{ReportFormatter.TotalLine(full)}»");
        Check("sessione vuota", ReportFormatter.Text(new Report(Today, [], 0, 0)) == "Nessun dato per oggi");
        var text = ReportFormatter.Text(report);
        Check("testo completo su 3 righe", text.Split('\n').Length == 3 && text.EndsWith("Totale 55,6% · Fuori sessione 3h10", StringComparison.Ordinal));
    }

    private static void Builder()
    {
        Section("ReportBuilder");
        var builder = new ReportBuilder(new ActivityResolver(ActivityRule.Seed), Utc);
        var s = Epoch.Seconds(T0);
        var segments = new List<Segment>
        {
            new(Today, s, s + 1800, ActivityState.Active, "chrome.exe", domain: "www.youtube.com"),
            new(Today, s + 1800, s + 3000, ActivityState.Passive, "chrome.exe", domain: "www.youtube.com"),
            new(Today, s + 3000, s + 3600, ActivityState.Active, "explorer.exe", "Esplora risorse"),
            new(Today, s + 3600, s + 7200, ActivityState.Unused),
            new(Today, s + 7200, s + 7800, ActivityState.Off),
            new("1999-01-01", 0, 100, ActivityState.Active, appName: "Vecchio"),
        };
        var report = builder.Build(Today, segments);
        Check("3 righe (YouTube, Esplora risorse, Attivo senza utilizzo)", report.Rows.Count == 3, Join(report.Rows.Select(r => r.Name)));
        Check("ordinamento per durata, unused in fondo anche se più lungo",
              report.Rows.Select(r => r.Name).SequenceEqual(["YouTube", "Esplora risorse", ReportBuilder.UnusedLabel]), Join(report.Rows.Select(r => r.Name)));
        Check("YouTube = 3000 s di cui 1200 passivi", report.Rows[0].Total == 3000 && report.Rows[0].Passive == 1200);
        Check("sessione = 7200, off = 600", report.SessionTotal == 7200 && report.OffTotal == 600);
        Check("percentuali: 416,7 / 83,3 / 500,0 → 417/83/500", report.Rows.Select(r => r.PerMille).SequenceEqual([417, 83, 500]), Join(report.Rows.Select(r => r.PerMille)));
        Check("somma = 1000", report.Rows.Sum(r => r.PerMille) == 1000);
        Check("altro giorno ignorato", report.Rows.All(r => r.Name != "Vecchio"));

        var provisional = new List<ProvisionalSlice> { new(T0.AddSeconds(7800), T0.AddSeconds(7900), AppA, null) };
        var live = builder.Build(Today, segments, provisional);
        Check("coda provvisoria → attivo provvisorio su App A (100 s)", live.Rows.Any(r => r.Name == "App A" && r.Total == 100));
        Check("le regole si applicano al passato (dominio → attività)", live.Rows[0].Name == "YouTube");

        var custom = new ReportBuilder(new ActivityResolver([]), Utc).Build(Today, segments);
        Check("senza regole: fallback host senza www", custom.Rows[0].Name == "youtube.com");
        Check("report vuoto", builder.Build("2000-01-01", segments).IsEmpty);
    }

    private static void StoreAndCoalescing()
    {
        Section("Store e coalescenza");
        var store = Store.Temporary();
        var rules = store.Rules();
        Check($"regole seed al primo avvio ({ActivityRule.Seed.Count})", rules.Count == ActivityRule.Seed.Count, $"{rules.Count}");
        Check("seed contiene youtube.com → YouTube", rules.Any(r => r.Kind == RuleKind.Domain && r.Pattern == "youtube.com" && r.Activity == "YouTube"));

        var inserted = store.InsertRule(new ActivityRule(RuleKind.Domain, "example.test", "Esempio"));
        Check("insert regola assegna id", inserted.Id is not null);
        store.UpdateRule(inserted with { Activity = "Esempio 2" });
        Check("update regola", store.Rules().First(r => r.Id == inserted.Id).Activity == "Esempio 2");
        var duplicateFailed = false;
        try { store.InsertRule(new ActivityRule(RuleKind.Domain, "example.test", "Dup")); } catch (SqliteException) { duplicateFailed = true; }
        Check("UNIQUE(kind, pattern) respinge il duplicato", duplicateFailed);
        store.DeleteRule(inserted.Id!.Value);
        Check("delete regola", store.Rules().Count == ActivityRule.Seed.Count);

        // Coalescenza
        var coalescer = new SegmentCoalescer();
        var engine = new ClassificationEngine(coalescer, 180, Utc);
        var sim = new Simulation(engine, T0, foreground: AppA);
        sim.Run(30, true, AppA);
        Check("30 slice contigue stessa chiave → 1 segmento in memoria", coalescer.Pending.Count == 1 && Approx(coalescer.Pending[0].Duration, 30), $"{coalescer.Pending.Count}");
        coalescer.Flush(store);
        var rows = store.Segments(Today);
        Check("flush → 1 riga con id", rows.Count == 1 && rows[0].Id is not null && Approx(rows[0].Duration, 30));
        sim.Run(30, true, AppA);
        coalescer.Flush(store);
        rows = store.Segments(Today);
        Check("estensione dopo il flush → stessa riga allungata a 60 s", rows.Count == 1 && Approx(rows[0].Duration, 60), Join(rows.Select(r => r.Duration)));
        sim.Run(10, true, AppB);
        sim.Run(10, false, AppB, sessionOpen: false);
        coalescer.Flush(store);
        rows = store.Segments(Today);
        Check("cambio chiave → nuove righe (A, B, off)",
              rows.Select(r => r.State).SequenceEqual([ActivityState.Active, ActivityState.Active, ActivityState.Off]) && rows[1].AppId == "com.example.b",
              Join(rows.Select(r => $"{r.State}:{r.Duration}")));
        Check("righe contigue senza buchi", rows.Zip(rows.Skip(1)).All(p => Approx(p.First.End, p.Second.Start)));

        // Roundtrip
        var original = new Segment(Today, Epoch.Seconds(T0) + 5000, Epoch.Seconds(T0) + 5100, ActivityState.Passive,
                                   "chrome.exe", "Google Chrome", "youtube.com", "Video");
        store.Save(original);
        var fetched = store.Segments(Today).FirstOrDefault(r => r.Id == original.Id);
        Check("roundtrip segmento (tutti i campi)", fetched is not null && fetched.State == ActivityState.Passive && fetched.AppId == original.AppId
              && fetched.AppName == original.AppName && fetched.Domain == original.Domain && fetched.TitleHint == original.TitleHint
              && fetched.Start == original.Start && fetched.End == original.End);
        Check("days() elenca il giorno", store.Days().SequenceEqual([Today]));

        // Overlay: la coda in memoria sostituisce la propria riga persistita
        sim.Run(5, true, AppA);
        var dbRows = store.Segments(Today);
        var overlaid = coalescer.Overlay(store.Segments(Today));
        Check("overlay non duplica le righe persistite", overlaid.Count(r => r.Id is not null) == dbRows.Count);
        Check("overlay include la nuova coda non persistita", overlaid.Any(r => r.Id is null && r.AppId == "com.example.a"));

        // Persistenza su disco: riapertura
        var path = store.Path;
        store.Dispose();
        using (var reopened = new Store(path))
        {
            Check("riapertura: righe e regole persistono", reopened.Segments(Today).Count == dbRows.Count && reopened.Rules().Count == ActivityRule.Seed.Count);
            Check("schema come sul Mac: segments, activity_rules, grdb_migrations",
                  reopened.TableExists("segments") && reopened.TableExists("activity_rules") && reopened.TableExists("grdb_migrations"));
        }
        Cleanup(path);
    }

    public static void Cleanup(string dbPath)
    {
        try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch (Exception) { }
    }
}
