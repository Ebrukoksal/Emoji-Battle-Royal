using System;
using System.Collections.Generic;

public static class GameBoard
{
    // --- Types --------------------------------------------------------------
    internal sealed class Player
    {
        public int Id;
        public string Name = "";
        public string Cell = "a1";
        public int HP = 100;
    }

    // --- State --------------------------------------------------------------
    private static readonly object _lock = new object();
    private static readonly Dictionary<int, Player> _players = new Dictionary<int, Player>();
    // board maps "a1".."h8" -> playerId
    private static readonly Dictionary<string, int> _board =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly Random _rng = new Random();

    // --- Public API ---------------------------------------------------------

    /// <summary>Add a player on the first free cell (a1..h8). Returns the spawn cell.</summary>
    public static string AddPlayer(int id, string name)
    {
        lock (_lock)
        {
            string cell = RandomFreeCell_NoLock();
            var p = new Player { Id = id, Name = name, Cell = cell, HP = 100 };
            _players[id] = p;
            _board[cell] = id;
            return cell;
        }
    }

    /// <summary>Remove player and free its board cell. Returns last cell (or empty).</summary>
    public static string RemovePlayer(int id)
    {
        lock (_lock)
        {
            if (_players.TryGetValue(id, out var p))
            {
                _players.Remove(id);
                _board.Remove(p.Cell);
                return p.Cell;
            }
            return "";
        }
    }

    /// <summary>Try to move the player to a destination cell. On success returns true and the new cell.</summary>
    public static bool TryMove(int id, string destCell, out string newCell, out string error)
    {
        newCell = "";
        error = "";

        if (!TryParseCell(destCell, out destCell))
        {
            error = "Bad cell.";
            return false;
        }

        lock (_lock)
        {
            if (!_players.TryGetValue(id, out var p))
            {
                error = "No such player.";
                return false;
            }

            if (p.Cell.Equals(destCell, StringComparison.OrdinalIgnoreCase))
            {
                newCell = p.Cell; // no-op move
                return true;
            }

            if (_board.ContainsKey(destCell))
            {
                error = $"Cell {destCell} is occupied.";
                return false;
            }

            // perform move
            _board.Remove(p.Cell);
            p.Cell = destCell;
            _board[destCell] = id;
            newCell = destCell;
            return true;
        }
    }

    /// <summary>
    /// Try to attack a cell. If a player is there: HP-=10, returns hit=true with targetId/newHp and killed flag.
    /// If empty: hit=false.
    /// </summary>
    public static bool TryAttack(int attackerId, string targetCell, out int? targetId, out int newHp, out bool killed)
    {
        targetId = null;
        newHp = 0;
        killed = false;

        if (!TryParseCell(targetCell, out targetCell))
            return false; // invalid command shape -> caller may treat as miss or bad input

        lock (_lock)
        {
            if (_board.TryGetValue(targetCell, out int tid) && _players.TryGetValue(tid, out var t))
            {
                targetId = tid;
                t.HP = Math.Max(0, t.HP - 10);
                newHp = t.HP;
                if (t.HP == 0)
                {
                    killed = true;
                    _board.Remove(t.Cell); // dead is removed from board (kept in _players for name lookup if desired)
                }
                return true; // valid attack command processed; there was a target
            }
        }

        // valid cell but empty -> miss
        return true;
    }

    /// <summary>Return (aliveCount, lastAliveId). lastAliveId=-1 if none.</summary>
    public static (int alive, int lastId) CountAlive()
    {
        lock (_lock)
        {
            int alive = 0, last = -1;
            foreach (var p in _players.Values)
            {
                if (p.HP > 0) { alive++; last = p.Id; }
            }
            return (alive, last);
        }
    }

    /// <summary>Lookup player name by id (for pretty printing on server).</summary>
    public static bool TryGetName(int id, out string name)
    {
        lock (_lock)
        {
            if (_players.TryGetValue(id, out var p)) { name = p.Name; return true; }
        }
        name = "";
        return false;
    }

    // --- Cell helpers -------------------------------------------------------

    /// <summary>Validate input like "a1".."h8" (case-insensitive). Returns normalized "a1".</summary>
    public static bool TryParseCell(string s, out string cell)
    {
        cell = "";
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        char file = char.ToLowerInvariant(s[0]);
        if (file < 'a' || file > 'h') return false;

        if (!int.TryParse(s.Substring(1), out int rank)) return false;
        if (rank < 1 || rank > 8) return false;

        cell = $"{file}{rank}";
        return true;
    }

    private static string RandomFreeCell_NoLock()
    {
        var free = new List<string>(64);
        for (char f = 'a'; f <= 'h'; f++)
            for (int r = 1; r <= 8; r++)
            {
                string c = $"{f}{r}";
                if (!_board.ContainsKey(c)) free.Add(c);
            }
        if (free.Count == 0) return "a1";
        return free[_rng.Next(free.Count)];
    }

    public static bool TryGetStatus(int id, out string cell, out int hp)
    {
        lock (_lock)
        {
            if (_players.TryGetValue(id, out var p))
            {
                cell = p.Cell;
                hp   = p.HP;
                return true;
            }
        }
        cell = "";
        hp   = 0;
        return false;
    }

}
