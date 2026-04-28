# WallEye — Come funziona una partita

WallEye è una modalità custom di CS2 in cui **uno o più giocatori sono scelti segretamente come cheater** e ricevono il wallhack. Il tuo obiettivo: giocare normalmente, osservare i comportamenti sospetti, e votare correttamente alla fine del match.

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

La classifica aggiornata è disponibile sulla **web leaderboard** del server.

---

## Consigli

- **Non accusare subito**: il cheater sa di essere osservato e può giocare in modo prudente per non farsi scoprire.
- **Osserva gli angoli pre-aimati**: posizionarsi su un angolo prima ancora di sentire passi è un segnale forte.
- **"No cheater" è una risposta valida**: a volte il cheater gioca talmente discretamente che è difficile distinguerlo. Votare "No cheater" correttamente vale quanto identificarlo.
- **Conferma sempre il voto**: il menu rimane aperto ma il voto è registrato solo dopo aver premuto *Confirm selection*.
