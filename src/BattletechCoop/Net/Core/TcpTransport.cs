using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BattletechCoop.Net
{
    // Length-prefixed messages over TCP. Used for development (two game copies on one PC),
    // LAN play and the headless test harness. Internet play uses SteamTransport.
    public class TcpTransport : ITransport
    {
        public const int MaxMessageSize = 64 * 1024 * 1024;

        private readonly object gate = new object();
        private readonly Queue<TransportEvent> pending = new Queue<TransportEvent>();
        private readonly Dictionary<ulong, Connection> connections = new Dictionary<ulong, Connection>();
        private TcpListener listener;
        private ulong nextPeer = 1;
        private volatile bool shutDown;

        public string Name { get; private set; }

        public int ListenPort => listener == null ? 0 : ((IPEndPoint)listener.LocalEndpoint).Port;

        // Host side. Port 0 picks a free port (see ListenPort).
        public static TcpTransport Listen(IPAddress address, int port)
        {
            var transport = new TcpTransport();
            transport.listener = new TcpListener(address, port);
            transport.listener.Start();
            transport.Name = $"TCP host {address}:{transport.ListenPort}";
            var thread = new Thread(transport.AcceptLoop) { IsBackground = true, Name = "BattletechCoop TCP accept" };
            thread.Start();
            return transport;
        }

        // Guest side. Connects in the background; Poll reports Connected or Disconnected.
        public static TcpTransport Connect(string host, int port)
        {
            var transport = new TcpTransport { Name = $"TCP guest -> {host}:{port}" };
            var thread = new Thread(() => transport.ConnectWorker(host, port)) { IsBackground = true, Name = "BattletechCoop TCP connect" };
            thread.Start();
            return transport;
        }

        public void Poll(List<TransportEvent> events)
        {
            lock (gate)
            {
                while (pending.Count > 0)
                    events.Add(pending.Dequeue());
            }
        }

        public void Send(ulong peer, byte[] message)
        {
            Connection connection;
            lock (gate)
            {
                if (!connections.TryGetValue(peer, out connection)) return;
            }
            try
            {
                lock (connection.WriteLock)
                {
                    var header = BitConverter.GetBytes(message.Length);
                    connection.Stream.Write(header, 0, 4);
                    connection.Stream.Write(message, 0, message.Length);
                }
            }
            catch (Exception e)
            {
                Close(connection, "send failed: " + e.Message);
            }
        }

        public void Disconnect(ulong peer)
        {
            Connection connection;
            lock (gate)
            {
                if (!connections.TryGetValue(peer, out connection)) return;
            }
            Close(connection, "disconnected locally");
        }

        public void Shutdown()
        {
            shutDown = true;
            try { listener?.Stop(); } catch (Exception) { }
            List<Connection> all;
            lock (gate) all = new List<Connection>(connections.Values);
            foreach (var connection in all)
                Close(connection, "transport shut down");
        }

        private void AcceptLoop()
        {
            while (!shutDown)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                Start(client);
            }
        }

        private void ConnectWorker(string host, int port)
        {
            var client = new TcpClient();
            try
            {
                client.Connect(host, port);
            }
            catch (Exception e)
            {
                client.Close();
                lock (gate) pending.Enqueue(TransportEvent.Disconnected(0, "connect failed: " + e.Message));
                return;
            }
            Start(client);
        }

        private void Start(TcpClient client)
        {
            client.NoDelay = true;
            var connection = new Connection { Client = client, Stream = client.GetStream() };
            lock (gate)
            {
                if (shutDown) { client.Close(); return; }
                connection.Peer = nextPeer++;
                connections[connection.Peer] = connection;
                pending.Enqueue(TransportEvent.Connected(connection.Peer));
            }
            var thread = new Thread(() => ReadLoop(connection)) { IsBackground = true, Name = "BattletechCoop TCP read " + connection.Peer };
            thread.Start();
        }

        private void ReadLoop(Connection connection)
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    ReadExactly(connection.Stream, header, 4);
                    var length = BitConverter.ToInt32(header, 0);
                    if (length < 0 || length > MaxMessageSize)
                        throw new InvalidDataException("bad message length " + length);
                    var message = new byte[length];
                    ReadExactly(connection.Stream, message, length);
                    lock (gate)
                    {
                        if (connection.Closed) return;
                        pending.Enqueue(TransportEvent.Received(connection.Peer, message));
                    }
                }
            }
            catch (Exception e)
            {
                Close(connection, e is EndOfStreamException ? "remote closed" : e.Message);
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private void Close(Connection connection, string reason)
        {
            lock (gate)
            {
                if (connection.Closed) return;
                connection.Closed = true;
                connections.Remove(connection.Peer);
                pending.Enqueue(TransportEvent.Disconnected(connection.Peer, reason));
            }
            try { connection.Client.Close(); } catch (Exception) { }
        }

        private class Connection
        {
            public ulong Peer;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public bool Closed;
        }
    }
}
