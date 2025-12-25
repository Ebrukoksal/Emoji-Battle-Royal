using System.IO;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using Common;



class ThreadedTcpSrvr
{
    private readonly TcpListener listener;
    private volatile bool _running;

    public ThreadedTcpSrvr()
    {
        listener = new TcpListener(IPAddress.Parse("127.0.0.1"), 9050);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // prevent hard kill
            Stop();
        };

        listener.Start();
        _running = true;
        Console.WriteLine("Waiting for clients...");

        while (_running)
        {
            try
            {
                var client = listener.AcceptTcpClient(); // blocking
                var worker = new ConnectionThread(client);
                var thread = new Thread(worker.HandleConnection) { IsBackground = true };
                thread.Start();
            }
            catch (SocketException)
            {
                if (!_running) break; // listener.Stop() path
                throw;
            }
            catch (ObjectDisposedException) { break; }
        }

        Console.WriteLine("Server accept loop ended.");
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        try { listener.Stop(); } catch { }
        Console.WriteLine("Stopping server (Ctrl+C)...");
    }

    public static void Main() => _ = new ThreadedTcpSrvr();
}

class ConnectionThread
{
    private static int connections = 0;
    private static int nextId = 0;
    private static int udpErrs = 0;

    private static readonly object clientsLock = new object();
    private static readonly Dictionary<int, ClientInfo> clients = new Dictionary<int, ClientInfo>();

    private static readonly HashSet<int> Spectators = new HashSet<int>();
    private static readonly UdpClient udp = Utilities.CreateUdpSender();
    private static readonly IPEndPoint mcast =
    new IPEndPoint(IPAddress.Parse(Utilities.Multicast), Utilities.UdpPort);
    private static bool IsSpectator(int id)
    {
        lock (clientsLock) return Spectators.Contains(id);
    }



    // chosen fighter per player
    private static readonly Dictionary<int, Utilities.FighterMeta> FighterOfPlayer = new();

    // ---- turn/round state ----
    private static readonly object turnLock = new object();
    private static readonly List<int> turnOrder = new List<int>();
    private static int turnIndex = 0;
    private static int roundNo = 1;

    private static bool IsPlayersTurn(int id)
    {
        lock (turnLock)
            return turnOrder.Count > 0 && turnOrder[turnIndex] == id;
    }

    private static void AnnounceTurn_NoLock()
    {
        if (turnOrder.Count == 0) return;
        int current = turnOrder[turnIndex];
        string name = GameBoard.TryGetName(current, out var n) ? n : $"#{current}";
        UdpSend($"TURN {name}");
    }

    private static void AdvanceTurn()
    {
        lock (turnLock)
        {
            if (turnOrder.Count == 0) return;
            turnIndex = (turnIndex + 1) % turnOrder.Count;
            if (turnIndex == 0) roundNo++;
            UdpSend($"ROUND {roundNo}");
            AnnounceTurn_NoLock();
        }
    }

    private static void AddToTurnOrder(int id)
    {
        lock (turnLock)
        {
            turnOrder.Add(id);
            if (turnOrder.Count == 1)
            {
                turnIndex = 0;
                roundNo = 1;
                UdpSend($"ROUND {roundNo}");
                AnnounceTurn_NoLock();
            }
        }
    }

    private static void RemoveFromTurnOrder(int id)
    {
        lock (turnLock)
        {
            int idx = turnOrder.IndexOf(id);
            if (idx < 0) return;

            turnOrder.RemoveAt(idx);
            if (turnOrder.Count == 0) { turnIndex = 0; return; }

            if (idx < turnIndex) turnIndex--;        // keep pointer valid
            if (turnIndex >= turnOrder.Count) turnIndex = 0;
            AnnounceTurn_NoLock();
        }
    }

    private readonly TcpClient client;

    public ConnectionThread(TcpClient client) => this.client = client;

    public void HandleConnection()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        int clientId = -1;
        string clientName = "";

        try
        {
            using NetworkStream ns = client.GetStream();
            using var reader = Utilities.Utf8Reader(ns);
            using var writer = Utilities.Utf8Writer(ns);
            Interlocked.Increment(ref connections);
            Console.WriteLine("New client accepted: {0} active connections", connections);

            writer.WriteLine("SYS: Welcome to the battle arena.");

            string? line = reader.ReadLine();
            if (!TryParseJoin(line, out var parsedName))
            {
                writer.WriteLine("SYS: Invalid name. Please type your name");
                return;
            }
            clientName = parsedName!;

            clientId = Interlocked.Increment(ref nextId);
            lock (clientsLock)
            {
                clients[clientId] = new ClientInfo(clientId, clientName, writer);
            }

            WriteIntro(writer);  
            UdpSend($"{clientName} JOINED");

            // ---- Role selection: PLAY or SPECTATE ----
            writer.WriteLine("SYS: Type PLAY to enter the battle, or SPECTATE to watch.");
            var roleInput = reader.ReadLine()?.Trim() ?? "";
            bool spectate = roleInput.Equals("SPECTATE", StringComparison.OrdinalIgnoreCase);

            if (spectate)
            {
                lock (clientsLock) Spectators.Add(clientId);
                UdpSend($"{clientName} is watching the battle");

                // Spectator loop: chat / STATUS / QUIT only
                while (true)
                {
                    var lineSpec = reader.ReadLine();
                    if (lineSpec == null) break;

                    lineSpec = lineSpec.Trim();
                    if (lineSpec.Length == 0) continue;

                    if (lineSpec.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (lineSpec.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteLine("SYS: You are spectating. No position/HP.");
                        continue;
                    }

                    // allow spectator chat
                    string text = lineSpec;
                    if (IsTriggerWord(text)) text = ResolveTaunt(text);
                    Broadcast($"{clientName}: {text}");
                }

                // spectator leaves
                lock (clientsLock) { Spectators.Remove(clientId); clients.Remove(clientId); }
                UdpSend($"{clientName} LEFT");
                return; // do NOT continue into fighter flow
            }


            // ---- Fighter selection ----
            var fighterInput = reader.ReadLine()?.Trim() ?? "";
            var fighter = Utilities.TryGetFighter(fighterInput, out var meta) ? meta! : Utilities.DefaultFighter;
            FighterOfPlayer[clientId] = fighter;
            UdpSend($"{clientName} chose {fighter.FighterEmoji} ({fighter.Name})");
            writer.WriteLine($"SYS: Your attack word is '{fighter.Trigger}'. Example: {fighter.Trigger} h7");

            // ---- Spawn on board ----
            var spawn = GameBoard.AddPlayer(clientId, clientName);
            writer.WriteLine($"SYS: You spawned at {spawn}");
            UdpSend($"{clientName} spawned");
            UdpSend($"HP {clientName} 100");
            AddToTurnOrder(clientId);


            // -------- main loop --------
            var alive = true;
            while (alive)
            {
                line = reader.ReadLine();
                if (line == null) break; // client closed

                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    alive = false;
                    continue;
                }

                if (line.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    if (GameBoard.TryGetStatus(clientId, out var myCell, out var myHp))
                        writer.WriteLine($"SYS: You are at {myCell} | HP {myHp}");
                    else
                        writer.WriteLine("SYS: Status unavailable.");
                    continue;
                }

                if (IsSpectator(clientId))
                {
                    writer.WriteLine("SYS: You are spectating. Use chat or QUIT.");
                    continue;
                }

                // PASS / END: consume your turn without action
                if (line.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("END",  StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsPlayersTurn(clientId))
                    {
                        writer.WriteLine("SYS: Not your turn.");
                        continue;
                    }
                    UdpSend($"{clientName} passed");
                    AdvanceTurn();
                    continue;
                }


                // Trigger-based attack: "<trigger> h7"
                if (Utilities.IsTriggerAttack(line, FighterOfPlayer[clientId].Trigger, out var targetCellFromTrigger))
                {
                    if (!IsPlayersTurn(clientId))
                    {
                        writer.WriteLine("SYS: Not your turn.");
                        continue;
                    }
                    DoAttack(clientId, clientName, FighterOfPlayer[clientId], targetCellFromTrigger);
                    AdvanceTurn(); // consume turn
                    continue;
                }


                // Verbose attack: "attack h7" / "attack to h7"
                if (Utilities.IsAttackCommand(line, out var targetCell))
                {
                    if (!IsPlayersTurn(clientId))
                    {
                        writer.WriteLine("SYS: Not your turn.");
                        continue;
                    }
                    DoAttack(clientId, clientName, FighterOfPlayer[clientId], targetCell);
                    AdvanceTurn(); // consume turn
                    continue;
                }

                if (Utilities.IsMoveCommand(line, out var destCell))
                {
                    if (!IsPlayersTurn(clientId))
                    {
                        writer.WriteLine("SYS: Not your turn.");
                        continue;
                    }

                    if (GameBoard.TryMove(clientId, destCell, out var newCell, out var moveErr))
                    {
                        UdpSend($"{clientName} moved");  // no position leak
                        AdvanceTurn();                     // consume turn on success
                    }
                    else
                    {
                        writer.WriteLine($"SYS: {moveErr}");
                        // invalid move does NOT consume the turn
                    }
                    continue;
                }

                // chat + small taunts
                string text = line;
                if (IsTriggerWord(text)) text = ResolveTaunt(text);
                Broadcast($"{clientName}: {text}");
            }

            // normal leave
            GameBoard.RemovePlayer(clientId);
            FighterOfPlayer.Remove(clientId);
            RemoveFromTurnOrder(clientId);
            lock (clientsLock) { clients.Remove(clientId); }
            UdpSend($"{clientName} LEFT");

            var (aliveCount, last) = GameBoard.CountAlive();
            if (aliveCount == 1 && last != -1)
            {
                AnnounceWinner(last);
            }

        }
        catch (IOException ex)
        {
            Console.WriteLine($"[INFO] IO error for {(string.IsNullOrEmpty(clientName) ? "unknown" : clientName)}: {ex.Message}");
            if (clientId >= 0)
            {
                GameBoard.RemovePlayer(clientId);
                FighterOfPlayer.Remove(clientId);
                RemoveFromTurnOrder(clientId);
                lock (clientsLock) { clients.Remove(clientId); }
                if (!string.IsNullOrEmpty(clientName)) UdpSend($"{clientName} LEFT");
            }
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[INFO] Socket error for {(string.IsNullOrEmpty(clientName) ? "unknown" : clientName)}: {ex.SocketErrorCode} {ex.Message}");
            if (clientId >= 0)
            {
                GameBoard.RemovePlayer(clientId);
                FighterOfPlayer.Remove(clientId);
                RemoveFromTurnOrder(clientId);
                lock (clientsLock) { clients.Remove(clientId); }
                if (!string.IsNullOrEmpty(clientName)) UdpSend($"{clientName} LEFT");
            }
        }
        catch (ObjectDisposedException ex)
        {
            Console.WriteLine($"[INFO] Disposed connection for {(string.IsNullOrEmpty(clientName) ? "unknown" : clientName)}: {ex.Message}");
            if (clientId >= 0)
            {
                GameBoard.RemovePlayer(clientId);
                FighterOfPlayer.Remove(clientId);
                RemoveFromTurnOrder(clientId);
                lock (clientsLock) { clients.Remove(clientId); }
                if (!string.IsNullOrEmpty(clientName)) UdpSend($"{clientName} LEFT");
            }
        }
        finally
        {
            try { client.Close(); } catch { }
            Interlocked.Decrement(ref connections);
            Console.WriteLine("Client disconnected: {0} active connections", connections);
        }
    }

    // ===== helpers =====

    // Execute an attack and announce with emojis + names
    private static void DoAttack(int attackerId, string attackerName, Utilities.FighterMeta meta, string targetCell)
    {
        if (!GameBoard.TryAttack(attackerId, targetCell, out var targetId, out var newHp, out var killed))
        {
            SendTo(attackerId, $"SYS: Bad attack. Usage: {meta.Trigger} h7");
            return;
        }

        if (!targetId.HasValue)
        {
            UdpSend($"MISS {attackerName} {meta.AttackEmoji} {targetCell}");
            return;
        }

        string targetName = GameBoard.TryGetName(targetId.Value, out var tn) ? tn : $"#{targetId.Value}";
        UdpSend($"HIT {attackerName} {meta.AttackEmoji} {targetName} {targetCell}");
        UdpSend($"HP {targetName} {newHp}");
        if (killed)
        {
            UdpSend($"DEAD {targetName}");
            RemoveFromTurnOrder(targetId.Value);
            var (aliveCount, last) = GameBoard.CountAlive();
            if (aliveCount == 1 && last != -1)
            {
                AnnounceWinner(last);
            }
        }
    }

    private static void SendTo(int playerId, string line)
    {
        StreamWriter? w = null;
        lock (clientsLock)
            if (clients.TryGetValue(playerId, out var info)) w = info.Writer;
        try { w?.WriteLine(line); } catch { }
    }

    private static void UdpSend(string s)
    {
        try
        {
            var b = Encoding.UTF8.GetBytes(s);
            udp.Send(b, b.Length, mcast);
        }
            catch (ObjectDisposedException)
        {
            // shutting down ignored
        }
        catch (SocketException ex)
        {
            if (udpErrs++ < 3)
                Console.WriteLine($"[UDP] send error: {ex.SocketErrorCode} {ex.Message}");
            // after 3, stay silent
        }
        catch (Exception ex)
        {
            if (udpErrs++ < 3)
                Console.WriteLine($"[UDP] unexpected: {ex.Message}");
        }
    }
    private static void Broadcast(string line)
    {
        List<KeyValuePair<int, ClientInfo>> snapshot;
        lock (clientsLock)
            snapshot = new List<KeyValuePair<int, ClientInfo>>(clients);

        List<int> dead = new List<int>();
        foreach (var kv in snapshot)
        {
            try { kv.Value.Writer.WriteLine(line); }
            catch { dead.Add(kv.Key); }
        }
        if (dead.Count > 0)
        {
            lock (clientsLock)
                foreach (var cid in dead) clients.Remove(cid);
        }
    }

    // Name parsing (we also accept legacy "JOIN <name>" if someone sends it)
    private static bool TryParseJoin(string? line, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        var s = line.Trim();
        if (s.StartsWith("JOIN ", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(5).Trim();
        name = s;
        return name.Length > 0;
    }

    // small chat taunts 
    private static bool IsTriggerWord(string s)
        => s.Equals("rage", StringComparison.OrdinalIgnoreCase)
        || s.Equals("gg", StringComparison.OrdinalIgnoreCase)
        || s.Equals("ez", StringComparison.OrdinalIgnoreCase)
        || s.Equals("ninja", StringComparison.OrdinalIgnoreCase)
        || s.Equals("wp", StringComparison.OrdinalIgnoreCase)
        || s.Equals("lol", StringComparison.OrdinalIgnoreCase);

    private static string ResolveTaunt(string code) => code.ToLowerInvariant() switch
    {
        "rage"  => "(╯°□°）╯︵ ┻━┻",
        "gg"    => "✨ GG! ✨",
        "ez"    => "😏 ez",
        "ninja" => "🥷💨",
        "wp"    => "👏 wp",
        "lol"   => "😂 lol",
        _       => "💬"
    };

    private void CleanupAndClose()
    {
        try { client.Close(); } catch { }
    }

    private sealed class ClientInfo
    {
        public int Id;
        public string Name;
        public StreamWriter Writer;
        public ClientInfo(int id, string name, StreamWriter writer)
        { Id = id; Name = name; Writer = writer; }
    }

    private static void AnnounceWinner(int winnerId)
    {
        string winnerName = GameBoard.TryGetName(winnerId, out var wn) ? wn : $"#{winnerId}";
        string emblem = FighterOfPlayer.TryGetValue(winnerId, out var fm) ? fm.FighterEmoji : "👑";

        // multi-line fancy banner
        UdpSend("🏆===============================================🏆");
        UdpSend($"      {emblem}  {winnerName}  {emblem}");
        UdpSend("        is the last one standing!  🎉");
        UdpSend("🏆===============================================🏆");
    }
    private static void WriteIntro(StreamWriter w)
    {
        w.WriteLine("SYS: === Emoji Battle Royale ===");
        w.WriteLine("SYS: Goal: be the last one standing.");
        w.WriteLine("SYS: Turn-based: wait for 'TURN <name>' before acting.");
        w.WriteLine("SYS: Commands:");
        w.WriteLine("SYS:  • MOVE <cell>       e.g., MOVE h5");
        w.WriteLine("SYS:  • <attack> <cell>  after you pick a fighter (e.g., bang h7, fire c3)");
        w.WriteLine("SYS:  • attack <cell>    alternative attack command");
        w.WriteLine("SYS:  • STATUS           shows your cell + HP");
        w.WriteLine("SYS:  • PASS             skip your turn");
        w.WriteLine("SYS:  • QUIT             leave the game");
        w.WriteLine("SYS: Damage: each hit deals 10 HP. At 0 HP: DEAD.");
    }
}
