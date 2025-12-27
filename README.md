# Emoji Battle Royale — C# (TCP + UDP)

A turn-based, text-mode battle royale. Players join as 🕷/🤖/🧙/🥷/🐉, move on a hidden 8×8 grid, and attack using word triggers (e.g., `bang h7`). **TCP** handles chat/commands; **UDP multicast** broadcasts public game events for spectators and players.

---

## Features

- Turn-based combat with global rounds and per-player turns  
- Hidden positions (no coordinates leaked in public broadcasts)  
- Five fighters with themed triggers & emojis:  
  - 🕷 *spider* → `web` → 🕸  
  - 🤖 *robot* → `star` → 💫  
  - 🧙 *wizard* → `ice` → ❄️  
  - 🥷 *ninja* → `bang` → 💥  
  - 🐉 *dragon* → `fire` → 🔥  
- Chat via TCP; public events via UDP multicast  
- Spectator mode  
- Auto-reconnect client that resends **name / role / fighter**  
- Fancy winner banner over UDP

---

## Architecture

**Server**
- TCP listener: `0.0.0.0:9050` (commands, chat, `STATUS`)
- UDP multicast sender: `239.0.0.222:9051`  
  Events: `SPAWNED`, `MOVED`, `HIT`, `MISS`, `HP`, `DEAD`, `ROUND`, `TURN`, `WINNER`, `PASSED`, `SPECTATE`
- `GameBoard`: thread-safe 8×8 (`a1..h8`) positions + HP + move/attack rules
- Turn system: `ROUND n` / `TURN <name>`

**Client**
- TCP: background reader + stdin writer
- UDP: multicast listener (ReuseAddress enabled)
- On every connect: sends `name` → `role (PLAY|SPECTATE)` → `fighter` (if PLAY)

---

## Requirements / Dependencies

- .NET SDK **8.0+**
- Docker Desktop
- Console that supports **UTF-8** (for emoji output)
- Local firewall/router allowing **UDP multicast** `239.0.0.222:9051`
- No external NuGet packages


---

### Project Structure
```text
CNP Project/
├─ README.md
├─ Dockerfile.server
├─ Dockerfile.client
├─ .gitignore
├─ Common/
│  └─ Utilities.cs
├─ Server/
│  ├─ Server.cs
│  ├─ GameBoard.cs
│  └─ Server.csproj
└─ Client/
   ├─ Client.cs
   └─ Client.csproj

```
---

## Setup

1) **Clone or copy** the project into a local folder (e.g., `Emoji Battle Royal`).
2) Ensure **.NET SDK 8.0+** is installed:
   ```bash
   dotnet --version
   ```
---

## How to Run (Docker – Recommended)
1) **Build Docker Images(once)**
  From the project root:
   ```bash
   docker build -t cnp-server -f Dockerfile.server .
   docker build -t cnp-client -f Dockerfile.client .
   ```
2) **Start the Server**
   
   In the powershell:
   ```bash
   docker run -p 9050:9050 cnp-server
   ```
  
   You should see: Waiting for clients...
   
3) **Start one or more Clients**
   
   Open a new terminal for each client:
   ```bash
   docker run -it cnp-client
   ```

## How to Run (Local)

1) **Start the Server**
   
   In the powershell:
   ```bash
   cd Server
   dotnet run

   ```
  
   You should see: Waiting for clients...
   
2) **Start one or more Clients**
   
   Open a new terminal for each client:
   ```bash
   cd Client
   dotnet run

   ```

## How to Test Main Features

1) **Join & Role**
  - Start the server, then start 2+ clients.
  - Enter a name; choose `PLAY` for at least two clients (others may `SPECTATE`).
2) **Fighter Selection**
  - When asked, choose one: 🕷 spider (web), 🤖 robot (star), 🧙 wizard (ice), 🥷 ninja   (bang), 🐉 dragon (fire).
3) **Turn System**
  - Watch **UDP** messages `ROUND n` and `TURN <name>`.
  - Only the named player can act; others can chat or wait.
4) **Movement**
  - On your turn, type `MOVE <cell>`.
  - Other players see `<name> MOVED` over UDP.
5) **Attacks**
  - On your turn, attack with your trigger word (e.g. `bang h7` for ninja)
  - If someone is on that cell: expect `HIT <attacker> <emoji> <target>` and `HP <target> <value>`.
  - If empty: `MISS <attacker> <emoji>`.
6) **Status**
  - Type `STATUS` to see your current cell and HP.
7) **Pass**
  - Type `PASS` to skip your turn.
8) **Death & Win**
  - Each hit deals 10HP. At `0 HP` you will see `DEAD <name>`.
  - When only one player remains, a winner banner will shown.
9) **Spectator Mode**
  - Start a client as `SPECTATE`. You will see all UDP events and can chat, but can't move or attack.

## Known Issues / Limitations
  - UDP multicast may be blocked by local firewall/router. TCP gameplay still works; UDP events won’t appear until multicast is allowed.
  - Console emojis require UTF-8 support. If you see boxes/garbled icons, ensure the console encoding is UTF-8.
  - Server restarts: the server does not persist board state (HP/position). On reconnect, players re-enter with random spawn.
  - Same-host UDP: Multiple clients on one machine must enable socket port reuse. If only the first client sees UDP, verify ReuseAddress + Bind happen before JoinMulticastGroup.

## Credits
  - **Implementation**: Student project for Computer Network Programming lecture.
  - **Patterns**: Threaded TCP server/client patterns inspired by `C# Network Programming
 by Richard Blum (2003)` textbook approaches.
  - **AI Assistance**: Built with help from ChatGPT (GPT-5 Thinking).
