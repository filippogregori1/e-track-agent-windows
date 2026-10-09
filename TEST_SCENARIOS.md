# Scenari di verifica manuale — agente Windows

Le stesse dieci prove dell'agente Mac ([activity-tracker/TEST_SCENARIOS.md](../activity-tracker/TEST_SCENARIOS.md)),
con le azioni e gli strumenti di Windows. Gli **esiti attesi sono identici**: stesso contratto di misura, stessa soglia
**N = 3 minuti** (default). Ognuna dice cosa preparare, cosa fare e cosa deve comparire nel report (clic sinistro
sull'icona nella tray).

## Prima di iniziare

1. Installa con `dist\e-track-agent-windows-setup.exe` (SmartScreen: «Ulteriori informazioni» → «Esegui comunque»).
   L'app parte dalla pagina finale dell'installazione e compare come icona vicino all'orologio (se non la vedi:
   freccia **^** delle icone nascoste).
2. **Collega equipe-track e apri una sessione** (Impostazioni → equipe-track). Senza sessione aperta l'agente misura
   lo stesso, ma nel report locale le app compaiono come «Non tracciato» (regola della privacy, identica al Mac):
   per vedere i nomi delle attività serve una sessione aperta e non in pausa.
3. Impostazioni di Windows → Sistema → Alimentazione: **Spegni lo schermo dopo 20 minuti** (alimentazione da rete e,
   sui portatili, anche a batteria) e salvaschermo disattivato o oltre i 20 minuti. Fa eccezione lo scenario 4.
   Su un portatile tieni l'alimentatore collegato.
4. Prima di ogni prova annota i valori del report (o fai uno screenshot con Win+Maiusc+S): l'esito atteso è sempre una
   **differenza** rispetto a quei valori. Tolleranza ±1 minuto: i tick sono al secondo, ma il cronometro è a mano.
5. Per vedere cosa vede l'agente: **Impostazioni → Segnali (diagnostica)** (si aggiorna ogni 2 s: stato della sessione,
   richiesta «schermo acceso», sessioni multimediali in riproduzione, a chi va il video).
   Per il dettaglio grezzo (con [sqlite3](https://sqlite.org/download.html) o DB Browser for SQLite):
   ```powershell
   sqlite3 "$env:LOCALAPPDATA\activity-tracker\activity.sqlite" `
     "select state, bundle_id, domain, datetime(start,'unixepoch','localtime'), round(end-start) from segments order by id desc limit 20"
   ```
   (`bundle_id` su Windows contiene il nome dell'eseguibile, es. `chrome.exe`.) Per le richieste di alimentazione per
   processo, l'equivalente di `pmset -g assertions`: `powercfg /requests` in un terminale **come amministratore**.

Valgono in **ogni** scenario:
- la riga «Totale» mostra sempre **100,0%**;
- il tempo «Fuori sessione» non entra mai nel 100%;
- un secondo non compare mai in due voci.

---

## 1. Video guardato senza toccare nulla

**Preparazione:** Chrome o Edge con un video YouTube lungo (≥ 20 min), a schermo intero o finestra in primo piano.
**Azione:** avvia il video, poi non toccare mouse, touchpad né tastiera per **15 minuti**. Dopo, muovi il mouse.
**Atteso:**
- la voce **YouTube** cresce di 15 min e **«di cui passivo»** cresce di 15 min (anche i primi 3 minuti:
  la retroattività fa partire il passivo dall'ultimo input);
- «Attivo senza utilizzo» non cambia;
- la voce non si chiama «Google Chrome»/«Microsoft Edge»: è **YouTube**;
- in Segnali, durante il video: «Video attribuito a: Google Chrome (sessione multimediale)».

## 2. Lettura PDF ferma

**Preparazione:** apri un PDF in Adobe Acrobat Reader (o nel lettore PDF predefinito), finestra in primo piano.
**Azione:** leggi senza toccare nulla per **10 minuti**, poi muovi il mouse.
**Atteso:**
- **Attivo senza utilizzo** cresce di 10 min;
- il lettore PDF non cresce (i primi 3 minuti, mostrati come lettore PDF finché la soglia non scatta,
  vengono spostati in «Attivo senza utilizzo» quando scatta);
- nessun «di cui passivo»: un PDF non tiene acceso lo schermo (in Segnali: «Video attribuito a: nessuno»).

> È il comportamento voluto dal contratto: senza input per oltre N minuti e senza video, la lettura
> ferma non si distingue dall'assenza. Se vuoi che conti come uso, alza N nelle Impostazioni.

## 3. 15 minuti lontano dalla scrivania, schermo bloccato

**Preparazione:** qualsiasi app in primo piano.
**Azione:** blocca lo schermo (**Win+L**), allontanati **15 minuti**, torna e sblocca.
**Atteso:**
- **Fuori sessione** cresce di 15 min;
- nessuna voce del 100% cresce durante l'assenza (né l'app, né «Attivo senza utilizzo»);
- le percentuali delle altre voci non cambiano.

## 4. 15 minuti lontano senza bloccare (lo schermo si spegne da solo)

**Preparazione:** imposta temporaneamente **Spegni lo schermo dopo 10 minuti**. Nessun video in riproduzione.
Blocco note in primo piano.
**Azione:** smetti di toccare il PC e allontanati per **15 minuti**; torna e riaccendi lo schermo.
Poi rimetti lo spegnimento a 20 minuti.
**Atteso:**
- **Attivo senza utilizzo** cresce di 10 min (dall'ultimo input allo spegnimento dello schermo, retroattivo);
- **Fuori sessione** cresce di 5 min (schermo spento);
- **Blocco note** non cresce.

> Se al ritorno Windows chiede il PIN perché la sessione si è bloccata, anche il tempo bloccato è fuori sessione:
> il risultato non cambia. Se il PC va in sospensione prima dei 15 minuti, idem (sospensione = fuori sessione).

## 5. Audio in sottofondo

**Preparazione:** avvia una playlist in **Spotify** (app desktop). In primo piano tieni Blocco note.
**Azione:** non toccare nulla per **10 minuti**.
**Atteso:**
- **Attivo senza utilizzo** cresce di 10 min;
- **né Spotify né Blocco note crescono**, e non compare nessun «di cui passivo»;
- in Segnali: Spotify compare fra le sessioni multimediali, ma «Video attribuito a: nessuno» (Spotify è nella lista di
  esclusione dei lettori solo audio).

**Variante (programmi che tengono acceso lo schermo senza video):** con **PowerToys Awake** attivo su «Mantieni lo
schermo acceso» (o Caffeine), ripeti la prova per 5 minuti → stesso esito (sono nella lista di esclusione).

> Differenza nota rispetto al Mac (vedi README, «Il segnale video su Windows»): la musica riprodotta **in una scheda
> del browser** (YouTube Music, Spotify Web) con la finestra visibile conta come passivo, perché senza amministratore
> Windows non dice se una scheda mostra un video o suona soltanto. Con l'app desktop di Spotify l'esito è quello atteso.

## 6. Cambio scheda nel browser e aggregazione per attività

**Preparazione:** in Chrome (o Edge) apri tre schede: `youtube.com`, `m.youtube.com`, `github.com`.
**Azione:** usando mouse e tastiera (scorri, clicca), resta **2 min** su youtube.com, **2 min** su m.youtube.com,
**2 min** su github.com, cambiando scheda con **Ctrl+1 / Ctrl+2 / Ctrl+3**.
**Atteso:**
- **YouTube** cresce di 4 min (le due varianti del dominio sono **una** voce), senza passivo;
- **GitHub** cresce di 2 min;
- la voce del browser («Google Chrome», «Microsoft Edge») **non** cresce;
- nelle Impostazioni → Regole attività, aggiungi una regola Dominio `github.com` → `Lavoro`: alla riapertura del
  report la voce GitHub di oggi diventa **Lavoro** con la stessa durata (le regole valgono anche sul passato).

## 7. Pausa breve sotto la soglia

**Preparazione:** un documento aperto in un editor qualsiasi (Blocco note, Word).
**Azione:** scrivi per 2 min, fermati **2 minuti** senza toccare nulla, poi riprendi a scrivere per 1 min.
**Atteso:**
- l'editor cresce di **5 min**, tutti attivi;
- «Attivo senza utilizzo» **non** cresce (2 min < N).

## 8. Riclassificazione visibile allo scattare della soglia

**Preparazione:** Blocco note in primo piano, scrivi qualcosa.
**Azione:** apri il report con un clic sull'icona (la finestrella resta aperta finché non clicchi altrove), poi non
toccare più nulla. Guarda il report a **2 min 30 s** e a **3 min 15 s** dall'ultimo input.
**Atteso:**
- a 2:30 Blocco note include i minuti appena passati come uso attivo (provvisorio) e lo stato dice
  «Ora: attivo su Blocco note · fermo da 2m»;
- a 3:15 quei minuti **sono spariti da Blocco note** e compaiono in **Attivo senza utilizzo** (~3 min);
- il totale resta 100,0% in entrambi i momenti;
- il report aperto non sposta tempo su «e-track agent»: conta l'ultima app in primo piano.

> La finestrella si aggiorna da sola ogni 5 s mentre è aperta: non serve toccare nulla per vedere il cambio.

## 9. Videochiamata senza toccare nulla

**Preparazione:** una chiamata Google Meet nel browser (o Teams/Zoom) con video attivo; va bene una chiamata di prova
da solo.
**Azione:** resta in chiamata **10 minuti** senza toccare nulla.
**Atteso:**
- **Google Meet** (o **Teams**/**Zoom**) cresce di 10 min con **«di cui passivo» 10 min**;
- «Attivo senza utilizzo» non cresce;
- in Segnali: «Video attribuito a: …» con «sessione multimediale» oppure «schermo tenuto acceso».

**Variante (video non in primo piano):** avvia un video YouTube in una finestra di Chrome visibile, porta in primo
piano Blocco note senza coprire del tutto il video e non toccare nulla per 5 min → il passivo va a **YouTube** (chi
tiene acceso lo schermo), non a Blocco note.

## 10. Sospensione e ripresa

**Preparazione:** qualsiasi app in primo piano.
**Azione:** Start → Arresta → **Sospendi** (o chiudi il coperchio del portatile). Aspetta **10 minuti**, riprendi e
sblocca.
**Atteso:**
- **Fuori sessione** cresce di 10 min;
- nessuna voce del 100% cresce durante la sospensione;
- alla ripresa l'agente riprende a contare da solo (usa il PC 1 min: l'app in primo piano cresce di 1 min).

---

## Avvio all'accesso (controllo finale)

Riavvia il PC (o esci e rientra nell'account): l'icona dell'agente ricompare nella tray senza aprirla a mano, e il
report di oggi conserva i dati di prima del riavvio. Il tempo a PC spento non compare da nessuna parte.
Su equipe-track la sessione risulta chiusa all'ora dello spegnimento (`POST /api/agente/stop`).

## Esito

| # | Scenario | Passa? | Note |
|---|---|---|---|
| 1 | Video senza toccare nulla | | |
| 2 | Lettura PDF ferma | | |
| 3 | Lontano, schermo bloccato | | |
| 4 | Lontano, schermo che si spegne | | |
| 5 | Audio in sottofondo | | |
| 6 | Cambio scheda e aggregazione | | |
| 7 | Pausa sotto soglia | | |
| 8 | Riclassificazione visibile | | |
| 9 | Videochiamata | | |
| 10 | Sospensione e ripresa | | |
| — | Avvio all'accesso | | |
