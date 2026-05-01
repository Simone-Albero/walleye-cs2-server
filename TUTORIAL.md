# WallEye — Come funziona una partita

WallEye è una modalità custom di CS2 in cui **uno o più giocatori sono scelti segretamente come cheater** e ricevono il wallhack. Il tuo obiettivo: giocare normalmente, osservare i comportamenti sospetti, e votare correttamente alla fine del match.

---

## Connettersi al server

### Server CS2
Apri CS2, vai nella console (`~`) e digita:

```
connect 57.131.24.95:27015
```

Oppure usa **Play → Find Game → Community Server Browser** e cerca "WallEye".

### Leaderboard
La classifica aggiornata è disponibile all'indirizzo:

```
http://57.131.24.95:8080
```

---

## Comandi in chat

In qualsiasi momento puoi digitare comandi in chat usando il prefisso `!`:

| Comando | Cosa fa |
|---|---|
| `!9` | Chiude il menu attivo |
| `!1`, `!2`, … | Seleziona la voce corrispondente del menu |

I menu si possono navigare anche con i **tasti numerici** sulla tastiera senza dover aprire la chat.

---

## Fasi di una partita

### 1. Attesa giocatori
Il server attende che si connettano tutti i giocatori necessari.  
In chat vedrai il contatore aggiornarsi in tempo reale:

```
[WallEye] Players connected: 7/10
```

Non appena il numero è raggiunto, parte il warmup automaticamente.

---

### 2. Warmup
Fase di riscaldamento prima del match vero e proprio.

- Durata: ~40 secondi
- I cheat sono **disabilitati per tutti** durante il warmup
- Usa questo tempo per scaldarti e prepararti

---

### 3. Live match
Il match parte. In questo momento il cheater viene scelto e **riceve una notifica privata** con un popup che gli comunica il suo ruolo. Da questo momento ha il **wallhack attivo**.

- Si giocano i round normalmente
- Il cheater vede i nemici attraverso i muri tramite un contorno luminoso (glow)
- Gli altri giocatori non hanno informazioni aggiuntive
- Osserva attentamente i comportamenti sospetti: pre-aim su angoli, reazioni prima di sentire i passi, posizionamento inspiegabile

> Il cheater è scelto in modo rotante: nel tempo tutti i giocatori saranno cheater almeno una volta.

---

### 4. Votazione
Alla fine dell'ultimo round si apre la fase di votazione. Comparirà un menu con la lista dei **giocatori avversari** tra cui scegliere.

- Hai **60 secondi** per votare
- Puoi selezionare uno o più sospettati, oppure **"No cheater"** se pensi che nessuno abbia barato
- Premi **"Confirm selection"** per inviare il voto — senza conferma il voto non viene registrato
- Un countdown nel centro schermo mostra il tempo rimanente

> Se sei tu il cheater, il menu potrebbe non apparire o mostrare un messaggio diverso.

---

### 5. Nuovo match
Chiusa la votazione, dopo pochi secondi parte un nuovo match con gli stessi giocatori connessi.

---

## Punteggio

| Evento | Punti |
|---|---|
| Partecipazione al match | **+5** |
| Report corretto (cheater identificato) | **+30** |
| Report errato | **−20** |
| "No cheater" corretto | **+30** |
| Kill | **+3** |
| Assist | **+2** |
| Morte | **−2** |

La classifica aggiornata è disponibile sulla **leaderboard** all'indirizzo `http://57.131.24.95:8080`.

---

## Consigli

- **Non accusare subito**: il cheater sa di essere osservato e può giocare in modo prudente per non farsi scoprire.
- **Osserva gli angoli pre-aimati**: posizionarsi su un angolo prima ancora di sentire passi è un segnale forte.
- **"No cheater" è una risposta valida**: a volte il cheater gioca talmente discretamente che è difficile distinguerlo. Votare "No cheater" correttamente vale quanto identificarlo.
- **Conferma sempre il voto**: il menu rimane aperto ma il voto è registrato solo dopo aver premuto *Confirm selection*.

---

## Comandi admin (in-game)

I comandi admin sono disponibili solo per i SteamID64 configurati in `config.json` con `dev.enabled: true`. Si digitano in chat con il prefisso `!` (o `/`).

### Informazioni

| Comando | Cosa fa |
|---|---|
| `!help` | Elenca tutti i comandi admin disponibili |
| `!status` | Mostra la fase corrente, i giocatori connessi, i cheater attivi e i parametri principali |
| `!players` | Lista dei giocatori connessi con SteamID64 e squadra (T/CT) |

### Controllo delle fasi

| Comando | Cosa fa |
|---|---|
| `!phase` | Apre un popup per saltare a una fase specifica: Attesa → Warmup → Match live → Votazione → Nuovo ciclo |

Il popup si chiude con `!9`.

### ESP / Wallhack

| Comando | Cosa fa |
|---|---|
| `!xray` | Apre un popup per attivare/disattivare l'ESP su tutti i giocatori o su un singolo |
| `!cheater <nome>` | Assegna il ruolo cheater (ESP attivo) a un giocatore specifico, in qualsiasi fase |

### Votazione

| Comando | Cosa fa |
|---|---|
| `!reports` | Apre manualmente il menu di votazione per tutti i giocatori |

---

### Parametri modificabili al volo con `!set`

> Le modifiche con `!set` sono **temporanee** (solo in memoria) e si perdono al riavvio. Per renderle permanenti usa il pannello admin web o modifica `config.json`.

| Chiave | Tipo | Descrizione | Esempio |
|---|---|---|---|
| `required_players` | numero | Giocatori richiesti per avviare il ciclo | `!set required_players 5` |
| `max_rounds` | numero | Numero di round per match | `!set max_rounds 15` |
| `warmup_duration` | secondi | Durata del warmup | `!set warmup_duration 30` |
| `report_duration` | secondi | Tempo per votare nella fase di report | `!set report_duration 30` |
| `cheaters_count` | numero | Quanti cheater selezionare per match | `!set cheaters_count 2` |
| `cheater_selection` | testo | `global` = N tra tutti, `per_team` = N per squadra | `!set cheater_selection global` |
| `report_scope` | testo | `enemy_team` = solo avversari, `all` = tutti | `!set report_scope all` |
| `restart_delay` | secondi | Pausa prima del riavvio automatico del ciclo | `!set restart_delay 0` |
| `skip_player_check` | true/false | Ignora il conteggio giocatori e parte subito | `!set skip_player_check true` |
| `points_participation` | numero | Punti per partecipazione | `!set points_participation 10` |
| `points_correct_report` | numero | Punti per report corretto | `!set points_correct_report 50` |
| `points_wrong_report` | numero | Penalità per report errato (usa valore negativo) | `!set points_wrong_report -10` |

### Altre utility

| Comando | Cosa fa |
|---|---|
| `!reload` | Ricarica tutti i parametri da `config.json` su disco |
| `!map <mappa>` | Cambia mappa immediatamente (es. `!map de_inferno`) |
