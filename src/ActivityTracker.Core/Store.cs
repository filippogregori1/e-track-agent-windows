using Microsoft.Data.Sqlite;

namespace ActivityTracker.Core;

/// <summary>
/// Riga della tabella <c>sync_days</c>: ultimo esito dell'invio di una giornata.
/// <c>Rejected</c> falso: <c>PayloadHash</c> è l'istantanea inviata con successo in <c>SyncedAt</c>; vero: il server
/// l'ha rifiutata (<c>Message</c>) e la giornata non si ritenta finché l'hash non cambia.
/// </summary>
public sealed record SyncDayRecord(string Day, string PayloadHash, double SyncedAt, bool Rejected = false, string? Message = null)
{
    public DateTimeOffset SyncedAtDate => Epoch.FromSeconds(SyncedAt);
}

/// <summary>
/// Persistenza SQLite (WAL, migrazioni registrate in <c>grdb_migrations</c> come fa GRDB sul Mac). Lo schema è
/// identico a quello dell'agente Mac, così le stesse query funzionano su entrambi; su Windows la colonna
/// <c>bundle_id</c> contiene il nome dell'eseguibile. Thread-safe: ogni operazione prende il lucchetto della connessione.
/// </summary>
public sealed class Store : ISegmentWriter, IDisposable
{
    public string Path { get; }
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public static readonly string[] MigrationIds = ["v1_schema", "v1_seed_rules", "v2_sync_days", "v3_tracking_ledger"];

    /// <summary>Apre (o crea) il database al percorso dato, creando la cartella se manca. <paramref name="migratingUpTo"/>
    /// (solo verifiche) si ferma a quella migrazione inclusa, come un database creato da una versione precedente.</summary>
    public Store(string path, string? migratingUpTo = null)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode = WAL");
        Exec("PRAGMA busy_timeout = 5000");
        Migrate(migratingUpTo);
    }

    /// <summary>Database nella cartella dati dell'app.</summary>
    public static Store Default() => new(AppIdentity.DatabasePath());

    /// <summary>Database temporaneo (verifiche).</summary>
    public static Store Temporary()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "activity-tracker-checks-" + Guid.NewGuid().ToString("N"));
        return new Store(System.IO.Path.Combine(dir, AppIdentity.DatabaseFileName));
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }

    // Migrazioni

    private void Migrate(string? upTo)
    {
        Exec("CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY)");
        var applied = AppliedMigrations().ToHashSet();
        foreach (var id in MigrationIds)
        {
            if (!applied.Contains(id))
            {
                lock (_lock)
                {
                    using var tx = _db.BeginTransaction();
                    foreach (var (sql, args) in MigrationSteps(id)) Exec(sql, tx, args);
                    Exec("INSERT INTO grdb_migrations(identifier) VALUES ($id)", tx, ("$id", id));
                    tx.Commit();
                }
            }
            if (id == upTo) break;
        }
    }

    private static IEnumerable<(string Sql, (string, object?)[] Args)> MigrationSteps(string id)
    {
        switch (id)
        {
            case "v1_schema":
                yield return ("""
                    CREATE TABLE segments (
                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                      day TEXT NOT NULL,
                      start DOUBLE NOT NULL,
                      "end" DOUBLE NOT NULL,
                      state TEXT NOT NULL,
                      bundle_id TEXT,
                      app_name TEXT,
                      domain TEXT,
                      title_hint TEXT)
                    """, []);
                yield return ("CREATE INDEX segments_day ON segments(day)", []);
                yield return ("""
                    CREATE TABLE activity_rules (
                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                      kind TEXT NOT NULL,
                      pattern TEXT NOT NULL,
                      activity TEXT NOT NULL,
                      UNIQUE(kind, pattern))
                    """, []);
                break;
            case "v1_seed_rules":
                foreach (var r in ActivityRule.Seed)
                    yield return ("INSERT OR IGNORE INTO activity_rules(kind, pattern, activity) VALUES ($k, $p, $a)",
                                  [("$k", r.Kind.Raw()), ("$p", r.Pattern), ("$a", r.Activity)]);
                break;
            case "v2_sync_days":
                // Ultimo esito dell'invio per giorno (hash dell'istantanea inviata o rifiutata).
                yield return ("""
                    CREATE TABLE sync_days (
                      day TEXT PRIMARY KEY,
                      payload_hash TEXT NOT NULL,
                      synced_at DOUBLE NOT NULL,
                      rejected BOOLEAN NOT NULL DEFAULT 0,
                      message TEXT)
                    """, []);
                break;
            case "v3_tracking_ledger":
                // Registro delle letture di GET /api/agente/sessione (TrackingLedger).
                yield return ("""
                    CREATE TABLE tracking_ledger (
                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                      kind TEXT NOT NULL,
                      session_id TEXT,
                      giorno TEXT,
                      start DOUBLE NOT NULL,
                      "end" DOUBLE NOT NULL)
                    """, []);
                break;
        }
    }

    /// <summary>Identificatori delle migrazioni già applicate a questo database, in ordine.</summary>
    public List<string> AppliedMigrations() =>
        Query("SELECT identifier FROM grdb_migrations ORDER BY identifier", r => r.GetString(0));

    public bool TableExists(string name) =>
        Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name = $n", r => r.GetString(0), ("$n", name)).Count > 0;

    // Segmenti

    public void Save(Segment segment) => Save([segment]);

    public void Save(IList<Segment> segments)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var s in segments) SaveOne(s, tx);
            tx.Commit();
        }
    }

    /// <summary>Aggiorna la riga con lo stesso id, o la inserisce (come <c>save</c> di GRDB).</summary>
    private void SaveOne(Segment s, SqliteTransaction tx)
    {
        (string, object?)[] args =
        [
            ("$day", s.Day), ("$start", s.Start), ("$end", s.End), ("$state", s.State.Raw()),
            ("$app", s.AppId), ("$name", s.AppName), ("$domain", s.Domain), ("$title", s.TitleHint),
        ];
        if (s.Id is { } id)
        {
            var n = Exec("""
                UPDATE segments SET day = $day, start = $start, "end" = $end, state = $state, bundle_id = $app,
                  app_name = $name, domain = $domain, title_hint = $title WHERE id = $id
                """, tx, [.. args, ("$id", id)]);
            if (n > 0) return;
            Exec("""
                INSERT INTO segments(id, day, start, "end", state, bundle_id, app_name, domain, title_hint)
                VALUES ($id, $day, $start, $end, $state, $app, $name, $domain, $title)
                """, tx, [.. args, ("$id", id)]);
            return;
        }
        Exec("""
            INSERT INTO segments(day, start, "end", state, bundle_id, app_name, domain, title_hint)
            VALUES ($day, $start, $end, $state, $app, $name, $domain, $title)
            """, tx, args);
        s.Id = Query("SELECT last_insert_rowid()", r => r.GetInt64(0), tx)[0];
    }

    private const string SegmentColumns = "id, day, start, \"end\", state, bundle_id, app_name, domain, title_hint";

    private static Segment ReadSegment(SqliteDataReader r) =>
        new(r.GetString(1), r.GetDouble(2), r.GetDouble(3), ActivityStates.Parse(r.GetString(4)),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(0));

    public List<Segment> Segments(string day) =>
        Query($"SELECT {SegmentColumns} FROM segments WHERE day = $d ORDER BY start", ReadSegment, ("$d", day));

    /// <summary>Segmenti che si sovrappongono a <c>[from, to)</c>, in ordine di inizio.</summary>
    public List<Segment> Segments(DateTimeOffset from, DateTimeOffset to) =>
        Query($"SELECT {SegmentColumns} FROM segments WHERE \"end\" > $f AND start < $t ORDER BY start", ReadSegment,
              ("$f", Epoch.Seconds(from)), ("$t", Epoch.Seconds(to)));

    public List<Segment> AllSegments() => Query($"SELECT {SegmentColumns} FROM segments ORDER BY start", ReadSegment);

    public List<string> Days() => Query("SELECT DISTINCT day FROM segments ORDER BY day", r => r.GetString(0));

    // Regole

    public List<ActivityRule> Rules() =>
        Query("SELECT id, kind, pattern, activity FROM activity_rules ORDER BY kind, pattern",
              r => new ActivityRule(RuleKinds.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt64(0)));

    /// <summary>Inserisce una regola; un duplicato (stesso tipo e pattern) lancia <see cref="SqliteException"/>.</summary>
    public ActivityRule InsertRule(ActivityRule rule)
    {
        lock (_lock)
        {
            Exec("INSERT INTO activity_rules(kind, pattern, activity) VALUES ($k, $p, $a)", null,
                 ("$k", rule.Kind.Raw()), ("$p", rule.Pattern), ("$a", rule.Activity));
            return rule with { Id = Query("SELECT last_insert_rowid()", r => r.GetInt64(0))[0] };
        }
    }

    public void UpdateRule(ActivityRule rule) =>
        Exec("UPDATE activity_rules SET kind = $k, pattern = $p, activity = $a WHERE id = $id", null,
             ("$k", rule.Kind.Raw()), ("$p", rule.Pattern), ("$a", rule.Activity), ("$id", rule.Id));

    public void DeleteRule(long id) => Exec("DELETE FROM activity_rules WHERE id = $id", null, ("$id", id));

    // Esiti d'invio (sync_days)

    private static SyncDayRecord ReadSyncDay(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4));

    public List<SyncDayRecord> SyncDays() =>
        Query("SELECT day, payload_hash, synced_at, rejected, message FROM sync_days ORDER BY day", ReadSyncDay);

    public SyncDayRecord? SyncDay(string day) =>
        Query("SELECT day, payload_hash, synced_at, rejected, message FROM sync_days WHERE day = $d", ReadSyncDay, ("$d", day)).FirstOrDefault();

    /// <summary>Inserisce o sostituisce l'esito di un giorno.</summary>
    public void SaveSyncDay(SyncDayRecord r) =>
        Exec("INSERT OR REPLACE INTO sync_days(day, payload_hash, synced_at, rejected, message) VALUES ($d, $h, $s, $r, $m)", null,
             ("$d", r.Day), ("$h", r.PayloadHash), ("$s", r.SyncedAt), ("$r", r.Rejected ? 1 : 0), ("$m", r.Message));

    public void SaveSyncDays(IEnumerable<SyncDayRecord> records)
    {
        foreach (var r in records) SaveSyncDay(r);
    }

    /// <summary>Dimentica tutti gli esiti (credenziali nuove o scollegate: si riparte da zero).</summary>
    public void ClearSyncDays() => Exec("DELETE FROM sync_days");

    /// <summary>Esiti d'invio di giornate ormai fuori dal registro.</summary>
    public void DeleteSyncDays(string before) => Exec("DELETE FROM sync_days WHERE day < $d", null, ("$d", before));

    /// <summary>Istante dell'ultimo invio riuscito (il più recente tra i giorni non rifiutati).</summary>
    public DateTimeOffset? LastSuccessfulSyncAt()
    {
        var v = Query("SELECT MAX(synced_at) FROM sync_days WHERE rejected = 0", r => r.IsDBNull(0) ? (double?)null : r.GetDouble(0));
        return v.Count > 0 && v[0] is { } s ? Epoch.FromSeconds(s) : null;
    }

    // Registro delle sessioni (equipe-track)

    public List<LedgerInterval> LedgerIntervals() =>
        Query("SELECT kind, session_id, giorno, start, \"end\" FROM tracking_ledger ORDER BY start",
              r => new LedgerInterval(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                                      r.GetDouble(3), r.GetDouble(4)));

    /// <summary>Sostituisce l'intero registro (poche centinaia di righe al massimo, già potate).</summary>
    public void ReplaceLedger(IEnumerable<LedgerInterval> intervals)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM tracking_ledger", tx);
            foreach (var iv in intervals)
                Exec("INSERT INTO tracking_ledger(kind, session_id, giorno, start, \"end\") VALUES ($k, $s, $g, $a, $b)", tx,
                     ("$k", iv.Kind), ("$s", iv.SessionId), ("$g", iv.Giorno), ("$a", iv.Start), ("$b", iv.End));
            tx.Commit();
        }
    }

    // Accesso grezzo

    private int Exec(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args) =>
        Query(sql, read, null, args);

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(read(r));
            return list;
        }
    }
}
