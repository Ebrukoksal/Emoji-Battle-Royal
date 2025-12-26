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
- TCP listener: `127.0.0.1:9050` (commands, chat, `STATUS`)
- UDP multicast sender: `239.0.0.222:9051`  
  Events: `SPAWNED`, `MOVED`, `HIT`, `MISS`, `HP`, `DEAD`, `ROUND`, `TURN`, `WINNER`, `PASSED`, `SPECTATE`
- `GameBoard`: thread-safe 8×8 (`a1..h8`) positions + HP + move/attack rules
- Turn system: `ROUND n` / `TURN <name>`

**Client**
- TCP: background reader + stdin writer
- UDP: multicast listener (port reuse; loopback enabled)
- On every connect: sends `name` → `role (PLAY|SPECTATE)` → `fighter` (if PLAY)

---

## Requirements / Dependencies

- .NET SDK **6.0+**
- Console that supports **UTF-8** (for emoji output)
- Local firewall/router allowing **UDP multicast** `239.0.0.222:9051`
- No external NuGet packages

---

### Project Structure
```text
CNP Project/
├─ README.md
├─ .gitignore 
├─ Common/
│  └─ Utilities.cs 
├─ Server/
│  ├─ Server.cs  
│  ├─ GameBoard.cs
│  └─ Server.csproj
└─ Client/
   └─ TcpClient.cs
```
---

## Setup

1) **Clone or copy** the project into a local folder (e.g., `Emoji Battle Royal`).
2) Ensure **.NET SDK 6.0+** is installed:
   ```bash
   dotnet --version
   ```
---

## How to Run
1) **Start the Server**
   
   In the powershell:
   ```bash
   cd Server
   dotnet run
   ```
  
   You should see: Waiting for clients...
   
3) **Start one or more Clients**
   
   Open a new terminal for each client:
   ```bash
   cd Client
   dotnet run TcpClient.cs
   ```
   
