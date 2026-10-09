# activity-tracker per Windows (e-track agent)

Agente Windows gemello di [activity-tracker per macOS](../activity-tracker/README.md): vive come icona nella tray,
misura come usi il PC, salva tutto in SQLite in locale, mostra il report del giorno e — solo durante una sessione di
lavoro aperta su equipe-track — gli manda i tratti aggregati. **Stesse regole, stesso contratto di misura, stesso
collegamento a equipe-track** del Mac. Per l'utente il prodotto si chiama **e-track agent** e si scarica come
`e-track-agent-windows-setup.exe`, e con questo nome compare ovunque lo veda (installer, tray, finestre, menu Start,
«App installate»); dentro (progetto, eseguibile, cartelle, dati, avvio automatico) resta `activity-tracker`.

```
YouTube   33,6%  2h35  (di cui passivo 1h50)
…
Attivo senza utilizzo   22,0%  1h42
Totale 100,0% · Fuori sessione 3h10
```

Contratto di misura, segnali e schema dati: [docs/DESIGN.md](docs/DESIGN.md). Prove a mano: [TEST_SCENARIOS.md](TEST_SCENARIOS.md).
Istruzioni per chi installa: [installer/LEGGIMI.txt](installer/LEGGIMI.txt) (copiato in `dist/`).

## Perché C# / .NET 10 con le API Win32

| Bisogno | Come lo copre lo stack | Alternative scartate |
|---|---|---|
| App in primo piano, inattività, stato sessione, spegnimento | P/Invoke diretto a `user32`/`kernel32`/`wtsapi32`/`powrprof`: `GetForegroundWindow`, `GetLastInputInfo`, `WM_WTSSESSION_CHANGE`, `GUID_CONSOLE_DISPLAY_STATE`, `WM_ENDSESSION`. Nessun wrapper, nessun permesso | Electron/Tauri: un livello in più per chiamare le stesse API, con un runtime web per una finestrella |
| Video in riproduzione | API WinRT `Windows.Media.Control` (sessioni multimediali) chiamata nativamente da .NET, più `CallNtPowerInformation` | Python: WinRT e COM possibili ma fragili da impacchettare; Rust: tutto fattibile ma interop COM/WinRT molto più verboso |
| Dominio della scheda del browser | UI Automation (COM, parte di Windows) letta da .NET senza dipendenze | Estensioni del browser: andrebbero installate e mantenute per ogni browser |
| Credenziali sicure | Gestione credenziali di Windows (`CredWriteW`/`CredReadW`, cifrate con DPAPI sul profilo) | File cifrati a mano |
| Icona nella tray, report, Impostazioni | WinForms: `NotifyIcon` e controlli nativi, nessun runtime grafico da distribuire | WPF/WinUI: più pesanti per due finestre di servizio |
| **Logica identica al Mac** | Il Core (motore, regole, report, storage, sync) è C# puro `net10.0`, **verificato su macOS/Linux** con gli stessi casi dei check Swift (274 verifiche, server equipe-track finto incluso) | — |
| Distribuzione | `dotnet publish` autosufficiente: **un solo .exe** col runtime dentro, niente da preinstallare; installer NSIS per utente, senza amministratore | MSI/WiX: richiede Windows per compilare; .NET Framework 4.8: preinstallato ma API vecchie e niente test sul Mac |

Il prezzo è la dimensione: il runtime .NET e la proiezione WinRT stanno dentro l'eseguibile (~54 MB, installer ~48 MB).

## Struttura

| Progetto | Contenuto |
|---|---|
| `src/ActivityTracker.Core` | Logica pura, `net10.0`, nessuna dipendenza da Windows: modello, `ClassificationEngine`, `SessionGate`, `SegmentCoalescer`, regole, report e formato, `Store` (SQLite, stesso schema del Mac), registro delle sessioni, costruzione dei tratti, client equipe-track, regola del video di Windows (`VideoOwnerPolicy`). Riscrittura riga per riga dei file Swift omonimi |
| `src/ActivityTracker.Windows` | L'app: tray, campionatore, lettori dei segnali, sync, credenziali, finestre |
| `tests/ActivityTracker.Checks` | Verifiche (come `ActivityTrackerChecks` sul Mac): stessi casi e stessi numeri, più le regole proprie di Windows e il giro HTTP vero contro un equipe-track finto su 127.0.0.1 |
| `installer/` | Script NSIS e LEGGIMI per l'utente |
| `scripts/` | `build.sh` (macOS/Linux), `build.ps1` (Windows), `setup-nsis.sh`, `make-icon.py` |

## Build, verifiche, installer

Serve il .NET SDK 10. Da macOS o Linux si compila tutto in cross-compilazione:

```sh
dotnet run --project tests/ActivityTracker.Checks   # verifiche: motore, regole, formato, storage, sync con server finto
dotnet build src/ActivityTracker.Windows -r win-x64 # build dell'app
scripts/build.sh                                    # verifiche + exe autosufficiente + dist/e-track-agent-windows-setup.exe
```

Su Windows: `powershell -ExecutionPolicy Bypass -File scripts\build.ps1` (con NSIS da `winget install NSIS.NSIS`).

`scripts/build.sh` usa `makensis` se c'è; altrimenti `scripts/setup-nsis.sh` lo procura in `.tools/`: scarica NSIS 3.13
(sorgenti e distribuzione Windows, verificati con gli SHA-1 pubblicati), compila il solo compilatore e usa stub e
plugin già compilati della distribuzione ufficiale. Niente sudo, niente mingw (Homebrew su macOS 13 compilerebbe
l'intera catena mingw-w64 dai sorgenti).

Risultato in `dist/`: `e-track-agent-windows-setup.exe` (l'installer di e-track agent, versione dentro le
proprietà del file), il suo `.sha256` e `LEGGIMI.txt`.

### Firma

Come l'app Mac (firmata ad-hoc), l'installer **non è firmato**. Al primo avvio Windows SmartScreen mostra
«Windows ha protetto il PC»: si supera con **«Ulteriori informazioni» → «Esegui comunque»** (dettagli e casi
particolari in [installer/LEGGIMI.txt](installer/LEGGIMI.txt)). L'eseguibile installato non porta il contrassegno di
download e non ripete l'avviso. Per eliminarlo serve un certificato di firma del codice (Authenticode).

## Installazione e permessi

L'installer è per utente, senza UAC: `%LOCALAPPDATA%\Programs\activity-tracker\activity-tracker.exe`, collegamento nel
menu Start, avvio all'accesso in `HKCU\…\Run`, voce in «App installate» con disinstallazione (che chiede se togliere
anche dati e credenziali). Aggiornare = rilanciare il nuovo installer: chiude la copia attiva con garbo
(`activity-tracker.exe --quit`: flush e ultimo invio) e la sostituisce.

Nessun permesso da concedere: su Windows app in primo piano, inattività, blocco, spegnimento dello schermo, sessioni
multimediali e barra degli indirizzi (UI Automation) sono leggibili da un utente normale. L'agente non chiede mai
l'amministratore.

## Dati

`%LOCALAPPDATA%\activity-tracker\`:
- `activity.sqlite` — segmenti grezzi, regole, registro delle sessioni, esiti d'invio. **Schema identico al Mac**
  (stesse tabelle, stesse colonne, migrazioni in `grdb_migrations`); su Windows `bundle_id` contiene il nome
  dell'eseguibile (`chrome.exe`). L'attività si calcola al momento del report: una regola modificata vale anche sul passato.
- `settings.json` — soglia, esclusioni video, primo avvio. Nessun segreto.
- `agent.log` — avvii, invii (`[Sync] <giorno>: N tratti scritti`), errori. Mai token, segreti, indirizzi o titoli.

## equipe-track

Identico al Mac ([docs/EQUIPE-TRACK.md](../activity-tracker/docs/EQUIPE-TRACK.md) dell'agente Mac):

- **Impostazioni → equipe-track**: URL del server, token dell'agente (`npm run agente:token -- "Nome"` in equipe-track),
  segreto del gate (`ACCESS_SECRET`). Tutti in **Gestione credenziali di Windows** (`it.equipe.activitytracker.equipe-track/…`),
  mai su file né nel codice. `http://` solo per `localhost`/`127.0.0.1` e solo con `AT_ALLOW_INSECURE_LOCALHOST=1`.
- **Quando**: si traccia solo se `GET /api/agente/sessione` (ogni 60 s, alla ripresa dalla sospensione e allo sblocco)
  dice sessione aperta e non in pausa. In pausa non si traccia. Fuori sessione app, dominio e titolo non arrivano
  nemmeno sul disco locale (il cancello `SessionGate` li toglie prima del database).
- **Cosa esce**: `POST /api/agente/segmenti` ogni 5 minuti, subito alla chiusura o alla pausa della sessione e all'uscita;
  la giornata intera, che sostituisce quella già inviata. Solo `(sessione, nome aggregato, stato, durata, inizio)` con
  i quattro stati `attivo`, `passivo`, `senza_utilizzo`, `fuori_sessione`; mai URL, titoli di finestra o eseguibili.
  I nomi passano dal filtro che rifiuta `/ ? @ ://` (lo stesso del server).
- **Spegnimento, riavvio, disconnessione** (`WM_ENDSESSION`): ultimo invio e `POST /api/agente/stop`.
- Intestazioni: `Authorization: Bearer <token>`, `Cookie: et_gate=<ACCESS_SECRET>`, `X-Agente-Versione: 0.3.0-windows`.

### Prova del giro vero in locale

1. equipe-track avviato (`npm run dev`) e raggiungibile dal PC Windows come `http://localhost:3000`: sullo stesso PC,
   oppure dal Mac con un tunnel (`ssh -R 3000:localhost:3000 utente@pc-windows`, o dal PC `ssh -L 3000:localhost:3000 mac`).
2. Chiudere l'agente installato (icona → Esci), poi in PowerShell:
   ```powershell
   $env:AT_ALLOW_INSECURE_LOCALHOST = "1"; $env:AT_STOP_ON_QUIT = "1"; $env:AT_DB_PATH = "$env:TEMP\at-prova.sqlite"
   & "$env:LOCALAPPDATA\Programs\activity-tracker\activity-tracker.exe"
   ```
3. Impostazioni → equipe-track: `http://localhost:3000`, il token, `ACCESS_SECRET` → «Salva nelle credenziali di Windows».
   Da qui i passi 5–9 della prova Mac sono identici; i log sono in `%LOCALAPPDATA%\activity-tracker\agent.log`.

## Variabili d'ambiente (prove)

- `AT_NO_LOGIN_ITEM=1`: non registra l'avvio all'accesso al primo avvio.
- `AT_NO_BROWSER_URL=1` (o `AT_NO_APPLE_EVENTS=1`, come sul Mac): non legge la barra degli indirizzi.
- `AT_DB_PATH=C:\percorso\file.sqlite`: usa un altro database.
- `AT_ALLOW_INSECURE_LOCALHOST=1`: ammette `http://127.0.0.1` / `http://localhost` (solo prove locali).
- `AT_STOP_ON_QUIT=1`: anche «Esci» chiude la sessione su equipe-track, come lo spegnimento.

## Il segnale video su Windows (unica differenza di piattaforma)

Sul Mac ogni asserzione «schermo acceso» dice quale processo l'ha presa. Su Windows l'elenco per processo
(`powercfg /requests`) richiede l'amministratore, e l'agente non lo è. La regola 5 (solo video, non audio) si
applica incrociando due segnali leggibili da utente (`VideoOwnerPolicy`, verificata nei check):

1. **sessioni multimediali in riproduzione** (le stesse dei controlli del volume): dicono *quale* app riproduce;
   contano solo app con una finestra visibile e non nella lista di esclusione (che contiene i lettori solo audio:
   Spotify, iTunes, Musica…);
2. **richiesta di sistema «schermo acceso»** (`ES_DISPLAY_REQUIRED`): dice *che* qualcuno tiene acceso lo schermo
   (lettori video, videochiamate) ma non chi; se nessuna sessione multimediale la spiega, il video va all'app in primo
   piano, a meno che giri un programma escluso che tiene acceso lo schermo senza video (PowerToys Awake, Caffeine…).

Conseguenza nota: musica riprodotta *in una scheda del browser* con la finestra visibile conta come passivo
(Windows non distingue una scheda che suona da una che mostra un video). Le app audio desktop sono escluse.

## Limiti noti

- Se l'agente va in crash si perdono al massimo gli ultimi N minuti non ancora confermati; il tempo ad agente spento
  non è registrato (come sul Mac).
- Uno spegnimento forzato (tasto di accensione tenuto premuto, mancanza di corrente) non chiama lo stop: la sessione
  resta aperta finché la chiude la persona o la mezzanotte UTC.
- La barra degli indirizzi si legge con UI Automation: Chrome attiva il suo albero di accessibilità quando un client
  UI Automation lo interroga (lieve costo di CPU). Se la lettura non riesce, il tempo va al browser e alle regole
  sul titolo (come per Firefox sul Mac). Pagine non web (`chrome://`, PDF locali) → nessun dominio, si usa l'app.
- Il tempo in cui le finestre dell'agente sono in primo piano (report, Impostazioni) va all'ultima app usata.
- Più monitor: «schermo spento» è lo stato della console (tutti gli schermi spenti).
