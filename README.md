# WinUsage 1.2

Piccola utility per Windows 11 che vive nella tray e misura da quanto tempo stai usando il computer
(standby e schermo bloccato esclusi), con storico in SQLite separato per ogni utente. Grafica in pixel art a tema subacqueo.

![Messaggio di soglia](docs/toast.png)

## Come si usa

| Azione | Effetto |
|---|---|
| **Tasto sinistro** sull'icona | Popup con il tempo di utilizzo dall'avvio e i totali di oggi (Sistema, Browser, Steam) |
| **Tasto destro** sull'icona | Menu: **Storico 7 giorni...** / **Storico 30 giorni...** / **Configurazione...** / **Manutenzione database...** / **Mostra messaggio di prova** / **Esci** |
| Passaggio del mouse | Tooltip "WinUsage - in uso da 1 ora e 30 minuti" |
| Soglia raggiunta | Messaggio in pixel art "Stai usando il computer da 2 ORE 30 MIN" (ogni N minuti, configurabile o disattivabile) |

### Messaggio di soglia in pixel art
Una finestrella animata (10 fotogrammi al secondo) in basso a destra: fondale a bande, elmo da palombaro con un
orologio nel vetro che dondola, bolle che salgono, un pesce che nuota, alghe che ondeggiano. Il testo è disegnato
con un font bitmap 5x7. Non ruba il focus, sparisce da sola dopo circa 11 secondi o con un clic. Se per qualche
motivo non si può vedere, resta comunque la normale notifica di Windows, mostrata insieme.
Tutta la grafica è originale: ispirata al tema (immersioni, oceano, ossigeno che finisce) senza usare personaggi
o elementi del gioco.

#### Schermo intero (giochi, Steam, Big Picture)
Il messaggio resta in primo piano sul monitor in cui stai giocando, senza mai prendere il focus (il gioco non
viene ridotto a icona né perde i comandi) e viene riportato in cima ogni mezzo secondo. Quando Windows segnala
uno schermo intero parte anche un breve jingle "8 bit". Funziona su giochi e Steam Big Picture in finestra senza
bordi (la modalità usata dalla maggior parte dei giochi attuali, comprese le "ottimizzazioni a schermo intero"
di Windows). Un gioco in schermo intero **esclusivo** (alcuni titoli DirectX 9 o più vecchi) impedisce a qualsiasi
altra finestra di apparire sopra: lì si sente il jingle, ma il messaggio si vede solo uscendo dal gioco. Disegnare
sopra un gioco richiederebbe di agganciarsi al gioco stesso, cosa che non facciamo (rischio con gli anti-cheat).
In *Configurazione* l'opzione si può disattivare: il messaggio viene allora rimandato a quando lo schermo intero finisce.

### Storico 7 e 30 giorni
Dal menu del tasto destro (o con i pulsanti *7 giorni* / *30 giorni* nella finestra): grafico a barre per giorno
(Sistema, Browser, Steam) e tabella con il totale del periodo, la media al giorno, la media nei soli giorni di
utilizzo e i valori di ogni giorno. Una linea tratteggiata indica la media del timer Sistema. Nella vista a 30 giorni
le barre sono più sottili, le date sono diradate e una linea leggera separa le settimane. I valori di oggi si
aggiornano ogni 5 secondi.

![Storico 7 giorni](docs/history.png)
![Storico 30 giorni](docs/history30.png)

### Manutenzione database
Dal menu del tasto destro, **Manutenzione database...**:
- **Informazioni**: percorso, dimensione, spazio recuperabile, giorni registrati (dal... al...), sessioni, versione di SQLite.
- **Verifica integrità** (`PRAGMA integrity_check`).
- **Compatta** (`VACUUM`): recupera lo spazio lasciato dai dati eliminati.
- **Backup...**: copia completa e coerente del database in un file `.db` a scelta (`VACUUM INTO`; con un SQLite più
  vecchio, checkpoint del WAL e copia del file).
- **Esporta CSV...**: totali di ogni giorno in hh:mm:ss e in secondi, separatore `;` e UTF-8, pronto per Excel.
- **Apri cartella**: apre Esplora file sul database.
- **Elimina i dati più vecchi di N giorni** e **Azzera tutto lo storico**, sempre con conferma. La configurazione
  non viene toccata; dopo l'azzeramento i totali di oggi ripartono da zero, il tempo "da avvio" resta.
- **Conservazione automatica**: elimina da sola i giorni più vecchi di N giorni (minimo 31, così lo storico a 30
  giorni resta completo; disattivata di default). Si applica al salvataggio (con conferma se elimina qualcosa),
  all'avvio e poi ogni 6 ore.

Prima di ogni operazione i secondi ancora in memoria vengono scritti nel database.

### Configurazione
- Avvio automatico con Windows (attivo di default)
- Notifica a intervalli regolari: attiva/disattiva e ogni quanti minuti (default 60)
- Timer Browser: attiva/disattiva e elenco dei processi considerati browser
- Timer Steam: attiva/disattiva

## Installare

`dist\WinUsage-Setup-1.2.0.exe`: installer per l'utente corrente (nessun permesso di amministratore).
Installa in `%LOCALAPPDATA%\Programs\WinUsage`, crea il collegamento nel menu Start e si registra in
*Impostazioni > App > App installate*. Un aggiornamento chiude da solo la versione in esecuzione (salvando i dati).
La disinstallazione rimuove app, collegamento e avvio automatico, e chiede se eliminare anche storico e
configurazione dell'utente (`%LOCALAPPDATA%\WinUsage`, per default no). Supporta `/S` per l'installazione silenziosa.

Essendo per-utente, ogni utente Windows che vuole la misura deve installare l'app (o avviarla dalla stessa
cartella): ognuno ha poi il proprio storico. Windows SmartScreen può mostrare un avviso perché l'installer non è
firmato digitalmente.

Per rigenerare l'installer: `build-installer.bat` (Windows, richiede NSIS) oppure `sh build-installer.sh` (Linux).

## Compilare

**Su Windows** (non serve installare nulla, usa il compilatore C# incluso in Windows):

    build.bat

**Su Linux/macOS** con Mono (`mono-mcs`, `libmono-system-windows-forms4.0-cil`):

    bash build-mono.sh

Risultato: `bin\WinUsage.exe` (circa 100 KB, richiede .NET Framework 4.8, già presente in Windows 11).
Copialo dove vuoi e avvialo: al primo avvio si registra per l'avvio automatico.
Se sposti l'exe, al lancio successivo l'avvio automatico viene riallineato al nuovo percorso.

## Come funziona

- **Timer "Sistema"**: parte all'avvio dell'app (che con l'avvio automatico coincide con l'accesso a Windows).
  Un tick al secondo; se tra due tick passano più di 5 secondi il PC era in standby/ibernazione
  (o è stato cambiato l'orologio) e quell'intervallo **non viene contato**. Sugli eventi di alimentazione
  Suspend/Resume i dati vengono salvati e il riferimento temporale azzerato.
- **Schermo bloccato**: con Win+L, il blocco automatico o il salvaschermo il conteggio si ferma e riprende allo
  sblocco. Oltre agli eventi di sessione di Windows, ogni secondo l'app controlla se il desktop che riceve l'input
  è quello della schermata di blocco, così il conteggio non resta acceso se un evento va perso. Se la soglia
  scatta in quel momento, il messaggio viene mostrato allo sblocco.
- **Cambio utente rapido**: quando la sessione viene disconnessa il conteggio si ferma, perché in quel momento
  il PC lo sta usando un altro utente.
- **Per utente**: l'app è per-utente. Database e configurazione sono in
  `%LOCALAPPDATA%\WinUsage\usage.db` (profilo di ciascun utente), l'avvio automatico è in `HKCU\...\Run`.
  Non servono privilegi di amministratore. Ogni utente che vuole la misura deve avviare l'app una volta.
- **SQLite**: usa `winsqlite3.dll`, la libreria SQLite già inclusa in Windows 10/11, quindi nessuna DLL o
  pacchetto da distribuire. Tabelle: `settings` (anche `retention_days`), `usage_daily(day, timer, seconds)`, `sessions(id, started_utc,
  last_seen_utc, active_seconds)`. Salvataggio ogni 30 secondi, prima dello standby e all'uscita/spegnimento.
- Log di eventuali errori: `%LOCALAPPDATA%\WinUsage\winusage.log`.

## I tre timer: è possibile? Sì

| Timer | Come viene misurato | Precisione / limiti |
|---|---|---|
| **Sistema** | Tempo con PC acceso e sessione attiva, standby e schermo bloccato esclusi | Conta dall'avvio dell'app. Se la avvii a mano dopo ore dall'accensione, parte da zero. Con "Avvio rapido" di Windows lo spegnimento è un'ibernazione del kernel, ma ogni accesso riavvia l'app, quindi la sessione riparte comunque da zero |
| **Browser** | Secondi in cui la finestra in **primo piano** appartiene a un processo browser (chrome, msedge, firefox, brave, opera, vivaldi... lista modificabile) | Non conta le schede in background, ma conta una pagina rimasta in primo piano mentre sei lontano dal PC (es. un video) |
| **Steam** | Secondi in cui Steam ha un **gioco in esecuzione** (valore `RunningAppID` nel registro dell'utente, verificato con la presenza di `steam.exe`) oppure il client Steam è in primo piano | Conta il gioco anche se la sua finestra non è in primo piano; non distingue quale gioco |

Tutti e tre sono salvati per giorno nel database. Browser e Steam non richiedono hook, driver o permessi speciali.

## Stato della verifica

- Compilato senza errori né avvisi con `mcs -langversion:5`, cioè con il sottoinsieme di C# del compilatore
  integrato in Windows.
- `tests/run-tests.sh`: 95 controlli sulla logica con tempo simulato (soglie, standby con e senza eventi, schermo bloccato,
  cambio utente, mezzanotte, persistenza, storico a 7 e 30 giorni, manutenzione: informazioni, verifica, pulizia,
  compattazione, backup, CSV, azzeramento, conservazione) su SQLite reale.
- Messaggio di soglia e grafico dello storico disegnati su PNG con Mono e controllati a occhio (`docs/`).
- L'installer si compila senza avvisi con NSIS 3.09.
- **Non ancora provato su un Windows reale**: tray, popup, finestre, animazione e posizione del toast,
  notifiche, eventi di alimentazione, `winsqlite3.dll`, registro e l'esecuzione dell'installer.
  Lo stesso vale per il comportamento a schermo intero (`SHQueryUserNotificationState`, `SetWindowPos`, audio).
  `bin/WinUsage.exe` incluso è stato compilato con Mono. Se qualcosa non va, controlla `winusage.log`.

## Struttura

    src/Core.cs          formattazione, impostazioni, accesso al database e manutenzione
    src/Art.cs           font pixel 5x7, scena animata del messaggio di soglia
    src/Sprites.cs       sprite (generato da tools/make_art.py)
    src/ToastForm.cs     finestra del messaggio di soglia
    src/ChipTune.cs      jingle 8 bit generato in memoria
    src/History.cs       dati e grafico dello storico
    src/HistoryForm.cs   finestra "Storico" (7 o 30 giorni)
    src/MaintenanceForm.cs finestra "Manutenzione database"
    src/Db.cs            wrapper P/Invoke su winsqlite3.dll
    src/UsageTracker.cs  motore di conteggio (standby, soglie, giorni)
    src/Probe.cs         finestra in primo piano, stato di Steam, avvio automatico
    src/TrayContext.cs   icona nella tray, menu, notifiche, eventi di sistema
    src/PopupForm.cs     popup del tasto sinistro
    src/SettingsForm.cs  finestra di configurazione
    tools/make_art.py    genera icona, sprite e immagini dell'installer (Pillow)
    installer/           script NSIS e immagini
    docs/                anteprime
    tests/               collaudo della logica (Mono); render-preview.sh disegna toast e grafico su PNG

## Sviluppo con l'AI
Questo progetto è stato sviluppato con l'assistenza di Claude (Anthropic): codice, test, script che genera la
pixel art e installer sono stati scritti insieme a Claude e provati dall'autore. I commit riportano il
trailer `Co-Authored-By`.

## Idee per dopo
Ripristino da un backup, installazione per tutti gli utenti, firma del codice.
