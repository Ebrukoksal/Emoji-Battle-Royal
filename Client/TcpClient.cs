using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Net;

class SimpleTcpClient
{
    private static volatile bool serverAlive = false;
    public static void Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "127.0.0.1";
        int port = args.Length > 1 ? int.Parse(args[1]) : 9050;

        Console.InputEncoding  = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;


        Console.Write("Please enter your name to enter the battle\n");
        string name = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(name)) name = "player";

        Console.Write("Type PLAY to fight or SPECTATE to watch: ");
        string role = Console.ReadLine()?.Trim() ?? "PLAY";
        if (!role.Equals("SPECTATE", StringComparison.OrdinalIgnoreCase)) role = "PLAY";

        // Ask fighter only if PLAY
        string fighter = "ninja";
        if (role.Equals("PLAY", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Pick your fighter (emoji or word): 🕷 spider | 🤖 robot | 🧙 wizard | 🥷 ninja | 🐉 dragon");
            var f = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(f)) fighter = f.Trim();
        }

        Console.WriteLine($"[SYS] You are logged in as {name} | Role: {role}" +
        (role == "PLAY" ? $" | Fighter: {fighter}" : ""));


        var exitRequested = false;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitRequested = true; };
        
        var udpThread = new Thread(new ThreadStart(() =>
        {
            try
            {
                var group = IPAddress.Parse("239.0.0.222");
                int port  = 9051;

                // 1) Create socket that allows multiple listeners on the same port
                var u = new UdpClient();
                u.ExclusiveAddressUse = false; 

                // 2) Allow address/port reuse and bind BEFORE JoinMulticastGroup
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                // 3) Join the multicast group (optionally on a specific local interface)
                u.JoinMulticastGroup(group);

                // 4) Ensure to get looped-back packets when sender & receivers are same host
                u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                var any = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    var data = u.Receive(ref any);
                    Console.WriteLine("[UDP] " + Encoding.UTF8.GetString(data));
                }
            }
            catch { /* exit on shutdown */ }
        }));
        udpThread.IsBackground = true;
        udpThread.Start();

        bool firstConnect = true;
        int retryCount = 0;
        const int maxRetries = 3;
        while (!exitRequested)
        {
            TcpClient tcp = null;
            StreamReader sr = null;
            StreamWriter sw = null;
            bool connectedOk = false; 

            try
            {
                // connect
                tcp = new TcpClient(host, port);
                var ns = tcp.GetStream();
                sr = new StreamReader(ns, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                sw = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };

                // always consume the server banner, show it only on first connect
                string? banner = sr.ReadLine(); // consume
                if (firstConnect && !string.IsNullOrEmpty(banner))
                    Console.WriteLine(banner);

                // send handshake (always in this order)
                sw.WriteLine(name);          // identity
                sw.WriteLine(role);          // "PLAY" or "SPECTATE"
                if (role.Equals("PLAY", StringComparison.OrdinalIgnoreCase))
                    sw.WriteLine(fighter);   // only if PLAY

                if (firstConnect)
                {
                    Console.WriteLine($"[SYS] You are logged in as {name} | Role: {role}" +
                        (role == "PLAY" ? $" | Fighter: {fighter}" : ""));
                    Console.WriteLine("[SYS] Connected. Type messages (QUIT to exit).");
                }
                firstConnect = false;

                serverAlive = true;
                connectedOk = true;          // handshake completed
                retryCount = 0;              // reset consecutive failures

                // background reader – marks serverAlive=false on disconnect
                var readerThread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                            Console.WriteLine(line);
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        serverAlive = false;
                        Console.WriteLine("[SYS] Disconnected from server.");
                    }
                })
                { IsBackground = true };
                readerThread.Start();

                // keyboard -> server
                while (!exitRequested && serverAlive)
                {
                    var input = Console.ReadLine();
                    if (input == null) break; // stdin closed
                    if (input.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        exitRequested = true;  // user wants to exit client
                        break;
                    }

                    try
                    {
                        sw.WriteLine(input);   // throws if server went away
                    }
                    catch
                    {
                        serverAlive = false;   // trigger reconnect
                        Console.WriteLine("[SYS] Send failed. Will try to reconnect...");
                        break;
                    }
                }
            }
            catch
            {
                // connection failed before handshake
                Console.WriteLine($"[SYS] Unable to connect (attempt {retryCount + 1}/{maxRetries}).");
            }
            finally
            {
                try { sw?.Dispose(); } catch { }
                try { sr?.Dispose(); } catch { }
                try { tcp?.Close(); } catch { }
            }

            if (exitRequested) break;

            if (!connectedOk || !serverAlive)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    Console.WriteLine("[SYS] Max retries reached. Exiting.");
                    break;
                }

                // wait 3 seconds before trying again
                for (int i = 3; i > 0 && !exitRequested; i--)
                {
                    Console.Write($"\r[SYS] Reconnecting in {i}s...   ");
                    Thread.Sleep(1000);
                }
                Console.WriteLine("\r[SYS] Reconnecting now...        ");
            }
        }


        Console.WriteLine("[SYS] Client exiting. Bye!");
    }


}
