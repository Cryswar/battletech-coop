using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BattletechCoop.Net;

// Run: dotnet run --project tests/NetTests
// Each test drives real TCP sockets on 127.0.0.1 and pumps the sessions the way the game's
// Update loop does. Exit code = number of failed tests.
static class Program
{
    const string Version = "0.0.1.0";
    const string Manifest = "ABCDEF0123456789 (3 files)";

    static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("guest joins and both sides see the roster", GuestJoins),
            ("ping measures round-trip time", PingMeasuresRtt),
            ("game messages flow both ways", GameMessagesBothWays),
            ("4 MB game message arrives intact", LargeMessage),
            ("mismatched mod list is rejected", ManifestMismatchRejected),
            ("mismatched mod version is rejected", VersionMismatchRejected),
            ("two guests get distinct indices and see each other", TwoGuests),
            ("session full rejects extra guest", SessionFull),
            ("guest leaving is reported to host and other guests", GuestLeaves),
            ("host leaving closes guest session", HostLeaves),
            ("connecting to nothing fails cleanly", ConnectFails),
            ("silent peer is dropped after handshake timeout", HandshakeTimeout),
        };

        var failed = 0;
        foreach (var test in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                test.Body();
                Console.WriteLine($"PASS  {test.Name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"FAIL  {test.Name}: {e.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        return failed;
    }

    // ---- helpers ----

    static CoopSession Host(out int port, string manifest = Manifest)
    {
        var transport = TcpTransport.Listen(IPAddress.Loopback, 0);
        port = transport.ListenPort;
        return CoopSession.Host(transport, "HostPlayer", Version, manifest);
    }

    static CoopSession Guest(int port, string name, string manifest = Manifest, string version = Version) =>
        CoopSession.Join(TcpTransport.Connect("127.0.0.1", port), name, version, manifest);

    static void PumpUntil(Func<bool> condition, IEnumerable<CoopSession> sessions, int timeoutMs = 5000, string what = "condition")
    {
        var all = sessions.ToList();
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception("timed out waiting for " + what);
            foreach (var s in all) s.Update();
            Thread.Sleep(5);
        }
    }

    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    static void CloseAll(params CoopSession[] sessions)
    {
        foreach (var s in sessions) s.Close("test done");
    }

    // ---- tests ----

    static void GuestJoins()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Alice");
        PumpUntil(() => guest.State == SessionState.Active && host.Players.Count() == 2, new[] { host, guest }, what: "handshake");
        Check(guest.LocalIndex == 1, "guest index " + guest.LocalIndex);
        Check(guest.Players.Select(p => p.Name).SequenceEqual(new[] { "HostPlayer", "Alice" }), "guest roster " + string.Join(",", guest.Players.Select(p => p.Name)));
        Check(host.Players.Select(p => p.Name).SequenceEqual(new[] { "HostPlayer", "Alice" }), "host roster");
        CloseAll(guest, host);
    }

    static void PingMeasuresRtt()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Alice");
        PumpUntil(() => host.Players.Any(p => p.Index == 1 && p.RttMs >= 0) && guest.Players.Any(p => p.Index == 0 && p.RttMs >= 0),
            new[] { host, guest }, timeoutMs: 4000, what: "RTT on both sides");
        var rtt = host.Players.First(p => p.Index == 1).RttMs;
        Check(rtt < 500, "localhost RTT too high: " + rtt);
        CloseAll(guest, host);
    }

    static void GameMessagesBothWays()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Alice");
        var atHost = new List<string>();
        var atGuest = new List<string>();
        host.GameMessage += (from, ch, p) => atHost.Add($"{from}/{ch}/{Encoding.UTF8.GetString(p)}");
        guest.GameMessage += (from, ch, p) => atGuest.Add($"{from}/{ch}/{Encoding.UTF8.GetString(p)}");
        PumpUntil(() => guest.State == SessionState.Active, new[] { host, guest });
        guest.SendGame(7, Encoding.UTF8.GetBytes("order 1"));
        guest.SendGame(7, Encoding.UTF8.GetBytes("order 2"));
        host.SendGame(9, Encoding.UTF8.GetBytes("broadcast"));
        PumpUntil(() => atHost.Count == 2 && atGuest.Count == 1, new[] { host, guest }, what: "messages");
        Check(atHost.SequenceEqual(new[] { "1/7/order 1", "1/7/order 2" }), "host got " + string.Join(";", atHost));
        Check(atGuest.Single() == "0/9/broadcast", "guest got " + atGuest.Single());
        CloseAll(guest, host);
    }

    static void LargeMessage()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Alice");
        byte[] received = null;
        host.GameMessage += (from, ch, p) => received = p;
        PumpUntil(() => guest.State == SessionState.Active, new[] { host, guest });
        var payload = new byte[4 * 1024 * 1024];
        new Random(1234).NextBytes(payload);
        guest.SendGame(3, payload);
        PumpUntil(() => received != null, new[] { host, guest }, what: "large message");
        Check(received.SequenceEqual(payload), "payload corrupted");
        CloseAll(guest, host);
    }

    static void ManifestMismatchRejected()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Mallory", manifest: "DIFFERENT (9 files)");
        PumpUntil(() => guest.State == SessionState.Rejected, new[] { host, guest }, what: "rejection");
        Check(guest.CloseReason.Contains("mod list"), "reason: " + guest.CloseReason);
        Check(host.Players.Count() == 1, "host roster should only contain the host");
        CloseAll(host);
    }

    static void VersionMismatchRejected()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Bob", version: "0.0.0.9");
        PumpUntil(() => guest.State == SessionState.Rejected, new[] { host, guest }, what: "rejection");
        Check(guest.CloseReason.Contains("version"), "reason: " + guest.CloseReason);
        CloseAll(host);
    }

    static void TwoGuests()
    {
        var host = Host(out var port);
        var a = Guest(port, "Alice");
        PumpUntil(() => a.State == SessionState.Active, new[] { host, a });
        var b = Guest(port, "Bob");
        PumpUntil(() => b.State == SessionState.Active && a.Players.Count() == 3, new[] { host, a, b }, what: "second guest");
        Check(a.LocalIndex == 1 && b.LocalIndex == 2, $"indices {a.LocalIndex},{b.LocalIndex}");
        Check(b.Players.Select(p => p.Name).SequenceEqual(new[] { "HostPlayer", "Alice", "Bob" }), "Bob's roster " + string.Join(",", b.Players.Select(p => p.Name)));
        Check(a.Players.Select(p => p.Name).SequenceEqual(new[] { "HostPlayer", "Alice", "Bob" }), "Alice's roster");
        CloseAll(a, b, host);
    }

    static void SessionFull()
    {
        var host = Host(out var port);
        host.MaxPlayers = 2;
        var a = Guest(port, "Alice");
        PumpUntil(() => a.State == SessionState.Active, new[] { host, a });
        var b = Guest(port, "Bob");
        PumpUntil(() => b.State == SessionState.Rejected, new[] { host, a, b }, what: "full rejection");
        Check(b.CloseReason.Contains("full"), "reason: " + b.CloseReason);
        CloseAll(a, host);
    }

    static void GuestLeaves()
    {
        var host = Host(out var port);
        var a = Guest(port, "Alice");
        var b = Guest(port, "Bob");
        PumpUntil(() => a.State == SessionState.Active && b.State == SessionState.Active && a.Players.Count() == 3 && b.Players.Count() == 3,
            new[] { host, a, b }, what: "both guests");
        string hostSaw = null;
        host.PlayerLeft += (p, r) => hostSaw = p.Name;
        b.Close("quit");
        PumpUntil(() => hostSaw == "Bob" && a.Players.Count() == 2, new[] { host, a }, what: "leave propagation");
        Check(!a.Players.Any(p => p.Name == "Bob"), "Alice still lists Bob");
        CloseAll(a, host);
    }

    static void HostLeaves()
    {
        var host = Host(out var port);
        var guest = Guest(port, "Alice");
        PumpUntil(() => guest.State == SessionState.Active, new[] { host, guest });
        host.Close("host quit");
        PumpUntil(() => guest.State == SessionState.Closed, new[] { guest }, what: "guest close");
        Check(guest.CloseReason.Contains("host"), "reason: " + guest.CloseReason);
    }

    static void ConnectFails()
    {
        // Grab a free port, then close it so nothing is listening.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var guest = Guest(port, "Alice");
        PumpUntil(() => guest.State == SessionState.Closed, new[] { guest }, timeoutMs: 10000, what: "connect failure");
        Check(guest.CloseReason.Contains("connect failed"), "reason: " + guest.CloseReason);
    }

    static void HandshakeTimeout()
    {
        var host = Host(out var port);
        host.HandshakeTimeout = TimeSpan.FromMilliseconds(300);
        var raw = new System.Net.Sockets.TcpClient();
        raw.Connect(IPAddress.Loopback, port); // connects but never sends Hello
        var stream = raw.GetStream();
        var dropped = false;
        var sw = Stopwatch.StartNew();
        while (!dropped && sw.ElapsedMilliseconds < 3000)
        {
            host.Update();
            Thread.Sleep(10);
            if (raw.Client.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && raw.Available == 0) dropped = true;
        }
        Check(dropped, "host did not drop the silent connection");
        raw.Close();
        CloseAll(host);
    }
}
