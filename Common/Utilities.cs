using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Common
{
    public static class Utilities
    {
        // ---------- UDP config ----------
        public const string Multicast = "239.0.0.222";
        public const int UdpPort = 9051;

        public static UdpClient CreateUdpSender() => new UdpClient();
        public static UdpClient CreateUdpReceiver()
        {
            var u = new UdpClient(UdpPort);
            u.JoinMulticastGroup(IPAddress.Parse(Multicast));
            return u;
        }

        public static void UdpSend(UdpClient udp, string msg)
        {
            var ep = new IPEndPoint(IPAddress.Parse(Multicast), UdpPort);
            var b = Encoding.UTF8.GetBytes(msg);
            udp.Send(b, b.Length, ep);
        }

        // ---------- UTF-8 stream helpers ----------
        public static StreamReader Utf8Reader(NetworkStream ns) =>
            new StreamReader(ns, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

        public static StreamWriter Utf8Writer(NetworkStream ns) =>
            new StreamWriter(ns, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

        // ---------- Cells & small parsers ----------
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

        // "MOVE h5"
        public static bool IsMoveCommand(string line, out string cell)
        {
            cell = "";
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (!line.StartsWith("move ", StringComparison.OrdinalIgnoreCase)) return false;
            var rest = line.Substring(5).Trim();
            return TryParseCell(rest, out cell);
        }

        // "attack h7" or "attack to h7"
        public static bool IsAttackCommand(string line, out string cell)
        {
            cell = "";
            if (string.IsNullOrWhiteSpace(line)) return false;
            var L = line.ToLowerInvariant();
            if (!L.StartsWith("attack")) return false;

            string rest = L.StartsWith("attack to ") ? line.Substring(10) : line.Substring(6);
            rest = rest.Trim();
            return TryParseCell(rest, out cell);
        }

        // "<trigger> h7" (e.g., "bang h7")
        public static bool IsTriggerAttack(string line, string trigger, out string cell)
        {
            cell = "";
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(trigger)) return false;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            if (!parts[0].Equals(trigger, StringComparison.OrdinalIgnoreCase)) return false;
            return TryParseCell(parts[1], out cell);
        }

        // ---------- Health bar ----------
        public static string HealthBar(int hp)
        {
            hp = Math.Clamp(hp, 0, 100);
            int filled = hp / 10;
            return "[" + new string('#', filled) + new string('-', 10 - filled) + $"] {hp}/100";
        }

        // ---------- Fighters (shared table) ----------
        public sealed class FighterMeta
        {
            public string Name = "";
            public string FighterEmoji = "";
            public string Trigger = "";
            public string AttackEmoji = "";
        }

        private static readonly Dictionary<string, FighterMeta> Fighters =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["spider"] = new() { Name="spider", FighterEmoji="🕷", Trigger="web",  AttackEmoji="🕸" },
                ["🕷"]     = new() { Name="spider", FighterEmoji="🕷", Trigger="web",  AttackEmoji="🕸" },

                ["robot"]  = new() { Name="robot",  FighterEmoji="🤖", Trigger="star", AttackEmoji="💫" },
                ["🤖"]     = new() { Name="robot",  FighterEmoji="🤖", Trigger="star", AttackEmoji="💫" },

                ["wizard"] = new() { Name="wizard", FighterEmoji="🧙", Trigger="ice",  AttackEmoji="❄️" },
                ["🧙"]     = new() { Name="wizard", FighterEmoji="🧙", Trigger="ice",  AttackEmoji="❄️" },

                ["ninja"]  = new() { Name="ninja",  FighterEmoji="🥷", Trigger="bang", AttackEmoji="💥" },
                ["🥷"]     = new() { Name="ninja",  FighterEmoji="🥷", Trigger="bang", AttackEmoji="💥" },

                ["dragon"] = new() { Name="dragon", FighterEmoji="🐉", Trigger="fire", AttackEmoji="🔥" },
                ["🐉"]     = new() { Name="dragon", FighterEmoji="🐉", Trigger="fire", AttackEmoji="🔥" },
            };

        public static bool TryGetFighter(string key, out FighterMeta meta) => Fighters.TryGetValue(key, out meta!);
        public static FighterMeta DefaultFighter => Fighters["ninja"];
    }
}
