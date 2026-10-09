# activity-tracker per Windows — Design

Il design di riferimento è quello del Mac ([activity-tracker/docs/DESIGN.md](../../activity-tracker/docs/DESIGN.md) e
[EQUIPE-TRACK.md](../../activity-tracker/docs/EQUIPE-TRACK.md)). Qui c'è solo ciò che cambia passando a Windows:
i segnali e come sono letti. **Tutto il resto è identico e condiviso nel Core** (riscrittura riga per riga dei file
Swift, verificata con gli stessi casi dei check Swift).

## Cosa è identico (Core, `net10.0`)

| Pezzo | File Mac | File Windows |
|---|---|---|
| Motore: soglia N, retroattività, schermo spento = soglia scattata, buchi > 5 s = off, mezzanotte | `ClassificationEngine.swift` | `ClassificationEngine.cs` |
| Cancello del contenuto fuori sessione («Non tracciato») | `SessionGate.swift` | `SessionGate.cs` |
| Coalescenza e flush ogni 10 s | `SegmentCoalescer.swift`, `Sampler.swift` | `SegmentCoalescer.cs`, `Sampler.cs` |
| Regole (domain › title › app › ripiego), seed | `ActivityResolver.swift`, `ActivityRule.swift` | `ActivityResolver.cs`, `ActivityRule.cs` |
| Report, resto maggiore su 1000, formato italiano | `ReportBuilder.swift`, `LargestRemainder.swift`, `ReportFormatter.swift` | idem `.cs` |
| SQLite: tabelle, colonne, migrazioni (`grdb_migrations`) | `Store.swift` (GRDB) | `Store.cs` (Microsoft.Data.Sqlite) |
| Registro delle sessioni, finestre di contenuto, troncamenti | `TrackingLedger.swift`, `TrackingSync.swift` | idem `.cs` |
| Tratti, hash, filtro privacy dei nomi, 422 con indice | `SyncPayloadBuilder.swift` | `SyncPayloadBuilder.cs` (+ `PrivacyFilter` = `motivoNomeRifiutato`) |
| Client: 3 rotte, Bearer, cookie `et_gate`, 401 rotta/gate | `SyncClient.swift` | `SyncClient.cs` |
| Servizio di sync: 60 s / 5 min / ripresa / sblocco / spegnimento (8 s) | `SyncService.swift` | `Sync/SyncService.cs` |

Le uniche costanti diverse: le regole «app» del seed usano l'eseguibile (`zoom.exe`, `ms-teams.exe`, `teams.exe`)
invece del bundle id; FaceTime non c'è. La versione dichiarata a equipe-track è `0.3.0-windows`.

## Segnali: Mac → Windows

| Segnale | Mac | Windows | Permesso |
|---|---|---|---|
| App in primo piano | `NSWorkspace.frontmostApplication` | `GetForegroundWindow` → pid → `QueryFullProcessImageNameW`; nome dalla descrizione del file. App UWP: processo figlio di `ApplicationFrameHost`. Le finestre dell'agente non contano | nessuno |
| Secondi dall'ultimo input | `CGEventSource.secondsSinceLastEventType` | `GetLastInputInfo` (tastiera, mouse, touch, penna della sessione) | nessuno |
| Video | `IOPMCopyAssertionsByProcess` (display-sleep) | sessioni multimediali (`Windows.Media.Control`) + `CallNtPowerInformation(SystemExecutionState)` & `ES_DISPLAY_REQUIRED`, regola in `VideoOwnerPolicy` | nessuno |
| Sospensione / ripresa | `willSleep` / `didWake` | `WM_POWERBROADCAST` `PBT_APMSUSPEND` / `PBT_APMRESUME*` | nessuno |
| Schermo spento / acceso | `screensDidSleep` / `screensDidWake` | `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)`: 0 spento, 1 acceso, 2 attenuato (= acceso) | nessuno |
| Blocco / sblocco | `com.apple.screenIsLocked` | `WTSRegisterSessionNotification` → `WTS_SESSION_LOCK/UNLOCK`; stato iniziale da `WTSSessionInfoEx` | nessuno |
| Salvaschermo | `com.apple.screensaver.didstart` | `SystemParametersInfo(SPI_GETSCREENSAVERRUNNING)` a ogni tick | nessuno |
| Cambio utente | `sessionDidResignActive` | `WTS_CONSOLE/REMOTE_(DIS)CONNECT` + `WTSConnectState == WTSActive` | nessuno |
| Spegnimento / logout | `willPowerOff` | `WM_QUERYENDSESSION` / `WM_ENDSESSION` (con `ShutdownBlockReasonCreate` per l'invio finale) | nessuno |
| Dominio della scheda | Apple Events | UI Automation sulla barra degli indirizzi (Chrome, Edge, Brave, Opera, Vivaldi, Arc, Firefox), su un thread proprio, cache 2,5 s; solo l'host (`HostFromAddressBar`) | nessuno |
| Titolo finestra (ripiego) | Accessibilità (AX) | `GetWindowTextW` | nessuno |
| Credenziali | Portachiavi | Gestione credenziali (`CredWriteW`, generiche, per utente) | nessuno |
| Avvio al login | `SMAppService` / LaunchAgent | `HKCU\…\Run` (stato «disattivato da Windows» letto da `StartupApproved\Run`) | nessuno |

**Sessione aperta** = sveglio ∧ schermo acceso ∧ sbloccato ∧ niente salvaschermo ∧ sessione utente attiva: stessa
definizione del Mac, stesso effetto sul motore (`SessionWillClose` chiude subito la coda e persiste).

### Regola del video (`VideoOwnerPolicy`)

1. Candidati: sessioni multimediali in stato *Playing*, di app con almeno una finestra visibile e non ridotta a icona,
   non nella lista di esclusione (confronto senza maiuscole e senza `.exe`), mai l'agente stesso.
2. Se ci sono candidati: vince quello in primo piano, altrimenti il primo per nome (deterministico, come la regola 5
   del Mac). Il soggetto è la finestra principale di quell'app; per un browser il dominio della sua scheda attiva, o
   l'ultimo dominio noto.
3. Se non ci sono: se qualcuno tiene acceso lo schermo (`ES_DISPLAY_REQUIRED`), nessun programma escluso è in
   esecuzione e l'app in primo piano non è esclusa → video dell'app in primo piano (videochiamate desktop, lettori
   che usano `SetThreadExecutionState`).
4. Altrimenti nessun video.

Esclusioni predefinite: lettori solo audio (Spotify, AppleMusic, iTunes, Music.UI, foobar2000, AIMP, MusicBee, Deezer,
TIDAL, Amazon Music, winamp, MediaMonkey) e programmi che tengono acceso lo schermo senza video (PowerToys.Awake,
caffeine, DontSleep, Insomnia, NoSleep), più l'agente. Modificabili nelle Impostazioni.

## Thread

Come sul Mac tutto lo stato vive su un thread solo, quello dell'interfaccia: campionatore (timer 1 s), motore,
coalescer, cancello, servizio di sync (le continuazioni `await` tornano lì, come `@MainActor`). Fuori da quel thread
girano solo le letture lente, che pubblicano l'ultimo valore: UI Automation (thread MTA, ~0,7 s), sessioni
multimediali e processi (ogni 2 s / 5 s). Lo spegnimento blocca il thread dell'interfaccia al più 8 s, con il lavoro di
rete su un altro thread.

## Interfaccia

- Icona nella tray: barre verdi quando traccia, grigie quando no; suggerimento con lo stato. Clic sinistro: report;
  clic destro: menu (report, Impostazioni, Invia ora, Esci).
- Report: finestrella senza bordi accanto all'icona, si chiude perdendo il fuoco, si aggiorna ogni 5 s; segue il tema
  chiaro/scuro delle app di Windows; cifre in colonna (Segoe UI ha cifre a larghezza fissa).
- Impostazioni: le stesse sezioni del Mac (Misura, Avvio, equipe-track, Regole attività, Esclusioni video, Dati) più
  «Segnali (diagnostica)» per le prove a mano.
