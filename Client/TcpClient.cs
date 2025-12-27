using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class SimpleTcpClient
{
    private static volatile bool serverAlive = false;

    public static void Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "host.docker.internal";
        int port = args.Length > 1 ? int.Parse(args[1]) : 9050;

        string ifaceArg = FindArgValue(args, "--iface");

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        Console.Write("Please enter your name to enter the battle\n");
        string? name = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(name)) name = "player";

        Console.Write("Type PLAY to fight or SPECTATE to watch: ");
        string role = Console.ReadLine()?.Trim() ?? "PLAY";
        if (!role.Equals("SPECTATE", StringComparison.OrdinalIgnoreCase)) role = "PLAY";

        string fighter = "ninja";
        if (role.Equals("PLAY", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Pick your fighter (emoji or word): 🕷 spider | 🤖 robot | 🧙 wizard | 🥷 ninja | 🐉 dragon");
            var f = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(f)) fighter = f.Trim();
        }

        Console.WriteLine($"[SYS] You are logged in as {name} | Role: {role}" +
            (role == "PLAY" ? $" | Fighter: {fighter}" : ""));

        bool exitRequested = false;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitRequested = true; };

        // =========================================================
        // UDP MULTICAST (DISABLED INSIDE DOCKER)
        // =========================================================

        bool runningInDocker =
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

        if (!runningInDocker)
        {
            var udpThread = new Thread(() =>
            {
                try
                {
                    var group = IPAddress.Parse("239.0.0.222");
                    int udpPort = 9051;

                    IPAddress localIface = ResolveLocalInterface(ifaceArg);

                    var u = new UdpClient();
                    u.ExclusiveAddressUse = false;
                    u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    u.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));

                    u.JoinMulticastGroup(group, localIface);
                    u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);

                    var any = new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        var data = u.Receive(ref any);
                        Console.WriteLine("[UDP] " + Encoding.UTF8.GetString(data));
                    }
                }
                catch
                {
                    // ignore on shutdown
                }
            })
            { IsBackground = true };

            udpThread.Start();
        }
        else
        {
            Console.WriteLine("[SYS] UDP multicast disabled (Docker environment)");
        }

        // =========================================================
        // TCP CONNECTION LOOP
        // =========================================================

        bool firstConnect = true;
        int retryCount = 0;
        const int maxRetries = 3;

        while (!exitRequested)
        {
            TcpClient? tcp = null;
            StreamReader? sr = null;
            StreamWriter? sw = null;
            bool connectedOk = false;

            try
            {
                Console.WriteLine($"[SYS] Connecting to {host}:{port} ...");
                tcp = new TcpClient(host, port);
                Console.WriteLine("[SYS] TCP connection established");

                var ns = tcp.GetStream();
                sr = new StreamReader(ns, Encoding.UTF8, false);
                sw = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };

                string? banner = sr.ReadLine();
                if (firstConnect && !string.IsNullOrEmpty(banner))
                    Console.WriteLine(banner);

                sw.WriteLine(name);
                sw.WriteLine(role);
                if (role.Equals("PLAY", StringComparison.OrdinalIgnoreCase))
                    sw.WriteLine(fighter);

                if (firstConnect)
                {
                    Console.WriteLine("[SYS] Connected. Type messages (QUIT to exit).");
                    firstConnect = false;
                }

                serverAlive = true;
                connectedOk = true;
                retryCount = 0;

                var readerThread = new Thread(() =>
                {
                    try
                    {
                        string? line;
                        while ((line = sr!.ReadLine()) != null)
                            Console.WriteLine(line);
                    }
                    catch { }
                    finally
                    {
                        serverAlive = false;
                        Console.WriteLine("[SYS] Disconnected from server.");
                    }
                })
                { IsBackground = true };
                readerThread.Start();

                while (!exitRequested && serverAlive)
                {
                    var input = Console.ReadLine();
                    if (input == null) break;

                    if (input.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        exitRequested = true;
                        break;
                    }

                    try
                    {
                        sw.WriteLine(input);
                    }
                    catch
                    {
                        serverAlive = false;
                        Console.WriteLine("[SYS] Send failed. Reconnecting...");
                        break;
                    }
                }
            }
            catch
            {
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

                for (int i = 3; i > 0 && !exitRequested; i--)
                {
                    Console.Write($"\r[SYS] Reconnecting in {i}s...");
                    Thread.Sleep(1000);
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("[SYS] Client exiting. Bye!");
    }

    // =========================================================
    // HELPERS
    // =========================================================

    private static string FindArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return string.Empty;
    }

    private static IPAddress ResolveLocalInterface(string ifaceArg)
    {
        if (!string.IsNullOrWhiteSpace(ifaceArg) && IPAddress.TryParse(ifaceArg, out var ip))
            return ip;

        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var addr in host.AddressList)
        {
            if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                return addr;
        }

        return IPAddress.Loopback;
    }
}
