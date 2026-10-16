using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BattletechCoop.Net
{
    public enum SessionRole { Host, Guest }

    public enum SessionState { Connecting, Handshaking, Active, Rejected, Closed }

    public class PlayerInfo
    {
        public byte Index;      // 0 = host
        public string Name;
        public ulong Peer;      // transport peer id (host side only; 0 for the host itself)
        public double RttMs = -1;
    }

    // Handshake, roster, keepalive and game-message routing on top of an ITransport.
    // The host is player 0 and assigns indices to guests. Guests only talk to the host;
    // the host relays roster changes. Call Update() once per frame from the main thread.
    public class CoopSession
    {
        public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
        public TimeSpan Timeout = TimeSpan.FromSeconds(20);
        public TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
        public int MaxPlayers = 4;

        public SessionRole Role { get; }
        public SessionState State { get; private set; }
        public string CloseReason { get; private set; }
        public byte LocalIndex { get; private set; }
        public string LocalName { get; }
        public ITransport Transport { get; }

        public IEnumerable<PlayerInfo> Players => players.Values.OrderBy(p => p.Index);

        public event Action<PlayerInfo> PlayerJoined;
        public event Action<PlayerInfo, string> PlayerLeft;
        public event Action<string> Closed;
        // (sender player index, channel, payload)
        public event Action<byte, ushort, byte[]> GameMessage;

        private readonly string modVersion;
        private readonly string manifest;
        private readonly Action<string> log;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<byte, PlayerInfo> players = new Dictionary<byte, PlayerInfo>();
        private readonly Dictionary<ulong, PeerState> peers = new Dictionary<ulong, PeerState>();
        private readonly List<TransportEvent> events = new List<TransportEvent>();
        private ulong hostPeer;
        private bool hostPeerKnown;

        private CoopSession(SessionRole role, ITransport transport, string localName, string modVersion, string manifest, Action<string> log)
        {
            Role = role;
            Transport = transport;
            LocalName = localName;
            this.modVersion = modVersion;
            this.manifest = manifest;
            this.log = log ?? (_ => { });
        }

        public static CoopSession Host(ITransport transport, string localName, string modVersion, string manifest, Action<string> log = null)
        {
            var session = new CoopSession(SessionRole.Host, transport, localName, modVersion, manifest, log)
            {
                State = SessionState.Active,
                LocalIndex = 0,
            };
            session.players[0] = new PlayerInfo { Index = 0, Name = localName, RttMs = 0 };
            session.log($"Hosting via {transport.Name} as '{localName}'");
            return session;
        }

        public static CoopSession Join(ITransport transport, string localName, string modVersion, string manifest, Action<string> log = null)
        {
            var session = new CoopSession(SessionRole.Guest, transport, localName, modVersion, manifest, log)
            {
                State = SessionState.Connecting,
            };
            session.log($"Joining via {transport.Name} as '{localName}'");
            return session;
        }

        public bool IsOpen => State != SessionState.Closed && State != SessionState.Rejected;

        public void Update()
        {
            if (!IsOpen) return;
            events.Clear();
            Transport.Poll(events);
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case TransportEventKind.Connected: OnConnected(e.Peer); break;
                    case TransportEventKind.Data: OnData(e.Peer, e.Data); break;
                    case TransportEventKind.Disconnected: OnDisconnected(e.Peer, e.Reason); break;
                }
                if (!IsOpen) return;
            }
            Tick();
        }

        // Host: channel message to one guest (by index) or all guests (target = null).
        // Guest: always goes to the host; target is ignored.
        public void SendGame(ushort channel, byte[] payload, byte? target = null)
        {
            var message = MessageWriter.Build(MessageType.Game, w =>
            {
                w.Write(channel);
                w.Write(payload.Length);
                w.Write(payload);
            });
            if (Role == SessionRole.Guest)
            {
                if (State == SessionState.Active) Transport.Send(hostPeer, message);
                return;
            }
            foreach (var player in players.Values)
            {
                if (player.Index == 0 || (target.HasValue && target.Value != player.Index)) continue;
                Transport.Send(player.Peer, message);
            }
        }

        public void Close(string reason)
        {
            if (!IsOpen) return;
            var bye = MessageWriter.Build(MessageType.Bye, w => w.Write(reason));
            foreach (var peer in peers.Keys.ToList())
                Transport.Send(peer, bye);
            Finish(SessionState.Closed, reason);
        }

        private void Finish(SessionState state, string reason)
        {
            State = state;
            CloseReason = reason;
            Transport.Shutdown();
            log($"Session ended ({state}): {reason}");
            Closed?.Invoke(reason);
        }

        private void OnConnected(ulong peer)
        {
            var now = clock.Elapsed;
            peers[peer] = new PeerState { Peer = peer, ConnectedAt = now, LastHeard = now };
            if (Role == SessionRole.Guest)
            {
                hostPeer = peer;
                hostPeerKnown = true;
                State = SessionState.Handshaking;
                Transport.Send(peer, MessageWriter.Build(MessageType.Hello, w =>
                {
                    w.Write(Protocol.ProtocolVersion);
                    w.Write(modVersion);
                    w.Write(manifest);
                    w.Write(LocalName);
                }));
                log("Connected to host; sent Hello");
            }
            else
            {
                log($"Peer {peer} connected; waiting for Hello");
            }
        }

        private void OnDisconnected(ulong peer, string reason)
        {
            if (Role == SessionRole.Guest)
            {
                if (!hostPeerKnown || peer == hostPeer)
                    Finish(State == SessionState.Rejected ? SessionState.Rejected : SessionState.Closed, "lost connection to host: " + reason);
                return;
            }
            if (peer == 0)
            {
                // Peer 0 is never a real peer: transports use it to report that hosting itself failed.
                Finish(SessionState.Closed, reason);
                return;
            }
            peers.Remove(peer);
            var player = players.Values.FirstOrDefault(p => p.Index != 0 && p.Peer == peer);
            if (player == null) return;
            RemovePlayer(player, reason);
        }

        private void RemovePlayer(PlayerInfo player, string reason)
        {
            players.Remove(player.Index);
            log($"Player {player.Index} '{player.Name}' left: {reason}");
            var left = MessageWriter.Build(MessageType.PlayerLeft, w => { w.Write(player.Index); w.Write(reason ?? ""); });
            foreach (var other in players.Values)
                if (other.Index != 0) Transport.Send(other.Peer, left);
            PlayerLeft?.Invoke(player, reason);
        }

        private void OnData(ulong peer, byte[] data)
        {
            PeerState state;
            if (!peers.TryGetValue(peer, out state)) return;
            state.LastHeard = clock.Elapsed;

            MessageType type;
            BinaryReader reader;
            try
            {
                reader = MessageWriter.Open(data, out type);
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                switch (type)
                {
                    case MessageType.Ping:
                        var stamp = reader.ReadInt64();
                        Transport.Send(peer, MessageWriter.Build(MessageType.Pong, w => w.Write(stamp)));
                        break;
                    case MessageType.Pong:
                        OnPong(peer, reader.ReadInt64());
                        break;
                    case MessageType.Bye:
                        var reason = reader.ReadString();
                        if (Role == SessionRole.Guest) Finish(SessionState.Closed, "host left: " + reason);
                        else Transport.Disconnect(peer);
                        break;
                    default:
                        if (Role == SessionRole.Host) OnHostMessage(state, type, reader);
                        else OnGuestMessage(type, reader);
                        break;
                }
            }
            catch (Exception e) when (e is EndOfStreamException || e is IOException)
            {
                log($"Malformed {type} from peer {peer}: {e.Message}");
                if (Role == SessionRole.Host) Transport.Disconnect(peer);
            }
        }

        private void OnHostMessage(PeerState peer, MessageType type, BinaryReader reader)
        {
            var player = players.Values.FirstOrDefault(p => p.Index != 0 && p.Peer == peer.Peer);
            if (type == MessageType.Hello)
            {
                if (player != null) return;
                var protocol = reader.ReadUInt16();
                var theirVersion = reader.ReadString();
                var theirManifest = reader.ReadString();
                var name = reader.ReadString();
                var rejection = Validate(protocol, theirVersion, theirManifest);
                if (rejection != null)
                {
                    log($"Rejecting '{name}' (peer {peer.Peer}): {rejection}");
                    Transport.Send(peer.Peer, MessageWriter.Build(MessageType.Reject, w => w.Write(rejection)));
                    Transport.Disconnect(peer.Peer);
                    return;
                }
                AddGuest(peer.Peer, name);
                return;
            }
            if (player == null) return; // nothing but Hello before the handshake
            if (type == MessageType.Game)
            {
                var channel = reader.ReadUInt16();
                var payload = reader.ReadBytes(reader.ReadInt32());
                GameMessage?.Invoke(player.Index, channel, payload);
            }
        }

        private string Validate(ushort protocol, string theirVersion, string theirManifest)
        {
            if (protocol != Protocol.ProtocolVersion)
                return $"protocol mismatch (host {Protocol.ProtocolVersion}, you {protocol})";
            if (theirVersion != modVersion)
                return $"BattletechCoop version mismatch (host {modVersion}, you {theirVersion})";
            if (theirManifest != manifest)
                return "mod list differs from the host's. Everyone needs the exact same mods and versions";
            if (players.Count >= MaxPlayers)
                return $"session is full ({MaxPlayers} players)";
            return null;
        }

        private void AddGuest(ulong peer, string name)
        {
            byte index = 1;
            while (players.ContainsKey(index)) index++;
            var player = new PlayerInfo { Index = index, Name = name, Peer = peer };

            Transport.Send(peer, MessageWriter.Build(MessageType.Welcome, w => { w.Write(index); w.Write(LocalName); }));
            foreach (var existing in players.Values)
            {
                if (existing.Index == 0) continue;
                Transport.Send(peer, MessageWriter.Build(MessageType.PlayerJoined, w => { w.Write(existing.Index); w.Write(existing.Name); }));
                Transport.Send(existing.Peer, MessageWriter.Build(MessageType.PlayerJoined, w => { w.Write(index); w.Write(name); }));
            }
            players[index] = player;
            log($"Player {index} '{name}' joined (peer {peer})");
            PlayerJoined?.Invoke(player);
        }

        private void OnGuestMessage(MessageType type, BinaryReader reader)
        {
            switch (type)
            {
                case MessageType.Welcome:
                    LocalIndex = reader.ReadByte();
                    var hostName = reader.ReadString();
                    players[0] = new PlayerInfo { Index = 0, Name = hostName, Peer = hostPeer };
                    players[LocalIndex] = new PlayerInfo { Index = LocalIndex, Name = LocalName, RttMs = 0 };
                    State = SessionState.Active;
                    log($"Welcomed by host '{hostName}' as player {LocalIndex}");
                    PlayerJoined?.Invoke(players[0]);
                    break;
                case MessageType.Reject:
                    var reason = reader.ReadString();
                    Finish(SessionState.Rejected, "rejected by host: " + reason);
                    break;
                case MessageType.PlayerJoined:
                    var joined = new PlayerInfo { Index = reader.ReadByte(), Name = reader.ReadString() };
                    players[joined.Index] = joined;
                    PlayerJoined?.Invoke(joined);
                    break;
                case MessageType.PlayerLeft:
                    var index = reader.ReadByte();
                    var why = reader.ReadString();
                    PlayerInfo left;
                    if (players.TryGetValue(index, out left))
                    {
                        players.Remove(index);
                        PlayerLeft?.Invoke(left, why);
                    }
                    break;
                case MessageType.Game:
                    if (State != SessionState.Active) return;
                    var channel = reader.ReadUInt16();
                    var payload = reader.ReadBytes(reader.ReadInt32());
                    GameMessage?.Invoke(0, channel, payload);
                    break;
            }
        }

        private void OnPong(ulong peer, long stamp)
        {
            var rtt = (clock.ElapsedTicks - stamp) * 1000.0 / Stopwatch.Frequency;
            var player = Role == SessionRole.Guest
                ? (players.ContainsKey(0) ? players[0] : null)
                : players.Values.FirstOrDefault(p => p.Index != 0 && p.Peer == peer);
            if (player == null) return;
            player.RttMs = player.RttMs < 0 ? rtt : player.RttMs * 0.8 + rtt * 0.2;
        }

        private void Tick()
        {
            var now = clock.Elapsed;
            foreach (var state in peers.Values.ToList())
            {
                var joined = Role == SessionRole.Guest || players.Values.Any(p => p.Index != 0 && p.Peer == state.Peer);
                if (!joined && now - state.ConnectedAt > HandshakeTimeout)
                {
                    log($"Peer {state.Peer} sent no Hello in time");
                    Transport.Disconnect(state.Peer);
                    continue;
                }
                if (now - state.LastHeard > Timeout)
                {
                    log($"Peer {state.Peer} timed out");
                    if (Role == SessionRole.Guest) { Finish(SessionState.Closed, "host timed out"); return; }
                    Transport.Disconnect(state.Peer);
                    continue;
                }
                if (now - state.LastPing >= PingInterval)
                {
                    state.LastPing = now;
                    var stamp = clock.ElapsedTicks;
                    Transport.Send(state.Peer, MessageWriter.Build(MessageType.Ping, w => w.Write(stamp)));
                }
            }
        }

        private class PeerState
        {
            public ulong Peer;
            public TimeSpan ConnectedAt;
            public TimeSpan LastHeard;
            public TimeSpan LastPing;
        }
    }
}
