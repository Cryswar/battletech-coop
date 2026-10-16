using System;
using System.Collections.Generic;
using Steamworks;

namespace BattletechCoop.Net
{
    // Steam lobby + SteamNetworking P2P (relayed by Steam, no port forwarding). The host creates
    // a friends-only lobby; guests join it from a Steam invite and talk P2P to the lobby owner.
    // Peer ids are SteamID64s. Relies on the game's SteamManager pumping SteamAPI.RunCallbacks.
    public class SteamTransport : ITransport
    {
        public const string LobbyKey = "battletechcoop";
        private const int Channel = 0;
        // Reliable P2P sends are limited to ~1 MB; larger messages are split.
        private const int ChunkSize = 512 * 1024;
        private const byte ChunkMore = 0;
        private const byte ChunkLast = 1;

        private readonly bool isHost;
        private readonly Queue<TransportEvent> pending = new Queue<TransportEvent>();
        private readonly HashSet<ulong> connected = new HashSet<ulong>();
        private readonly Dictionary<ulong, List<byte[]>> partial = new Dictionary<ulong, List<byte[]>>();
        private readonly Callback<P2PSessionRequest_t> sessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> connectFail;
        private readonly Callback<LobbyChatUpdate_t> lobbyChatUpdate;
        private readonly Callback<LobbyEnter_t> lobbyEnter;
        private readonly CallResult<LobbyCreated_t> lobbyCreated;
        private CSteamID lobby = CSteamID.Nil;
        private CSteamID owner = CSteamID.Nil;
        private bool shutDown;

        public string Name { get; private set; }
        public CSteamID Lobby => lobby;
        public bool LobbyReady => lobby != CSteamID.Nil;

        private SteamTransport(bool isHost)
        {
            this.isHost = isHost;
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            connectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
            lobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        }

        public static SteamTransport Host(int maxPlayers)
        {
            var transport = new SteamTransport(true) { Name = "Steam host (creating lobby)" };
            transport.lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers));
            return transport;
        }

        public static SteamTransport Join(ulong lobbyId)
        {
            var transport = new SteamTransport(false) { Name = $"Steam guest -> lobby {lobbyId}" };
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            return transport;
        }

        public void InviteFriends()
        {
            if (LobbyReady) SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        public void Poll(List<TransportEvent> events)
        {
            uint size;
            while (!shutDown && SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                var buffer = new byte[size];
                uint read;
                CSteamID remote;
                if (!SteamNetworking.ReadP2PPacket(buffer, size, out read, out remote, Channel)) break;
                var peer = remote.m_SteamID;
                if (!connected.Contains(peer) || read == 0) continue;
                Reassemble(peer, buffer, (int)read);
            }
            while (pending.Count > 0)
                events.Add(pending.Dequeue());
        }

        public void Send(ulong peer, byte[] message)
        {
            if (shutDown || !connected.Contains(peer)) return;
            var id = new CSteamID(peer);
            var offset = 0;
            do
            {
                var length = Math.Min(ChunkSize, message.Length - offset);
                var packet = new byte[length + 1];
                packet[0] = offset + length >= message.Length ? ChunkLast : ChunkMore;
                Buffer.BlockCopy(message, offset, packet, 1, length);
                if (!SteamNetworking.SendP2PPacket(id, packet, (uint)packet.Length, EP2PSend.k_EP2PSendReliable, Channel))
                {
                    Drop(peer, "P2P send failed");
                    return;
                }
                offset += length;
            } while (offset < message.Length);
        }

        public void Disconnect(ulong peer)
        {
            if (!connected.Contains(peer)) return;
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
            Drop(peer, "disconnected locally");
        }

        public void Shutdown()
        {
            if (shutDown) return;
            foreach (var peer in new List<ulong>(connected))
                SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
            if (LobbyReady) SteamMatchmaking.LeaveLobby(lobby);
            shutDown = true;
            sessionRequest.Unregister();
            connectFail.Unregister();
            lobbyChatUpdate.Unregister();
            lobbyEnter.Unregister();
            lobbyCreated.Cancel();
        }

        private void Reassemble(ulong peer, byte[] packet, int length)
        {
            List<byte[]> chunks;
            if (!partial.TryGetValue(peer, out chunks))
                partial[peer] = chunks = new List<byte[]>();
            var body = new byte[length - 1];
            Buffer.BlockCopy(packet, 1, body, 0, body.Length);
            chunks.Add(body);
            if (packet[0] != ChunkLast) return;

            partial.Remove(peer);
            var total = 0;
            foreach (var chunk in chunks) total += chunk.Length;
            var message = new byte[total];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, message, offset, chunk.Length);
                offset += chunk.Length;
            }
            pending.Enqueue(TransportEvent.Received(peer, message));
        }

        private void Connect(ulong peer)
        {
            if (shutDown || !connected.Add(peer)) return;
            pending.Enqueue(TransportEvent.Connected(peer));
        }

        private void Drop(ulong peer, string reason)
        {
            if (!connected.Remove(peer)) return;
            partial.Remove(peer);
            pending.Enqueue(TransportEvent.Disconnected(peer, reason));
        }

        private bool IsLobbyMember(CSteamID user)
        {
            if (!LobbyReady) return false;
            var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (var i = 0; i < count; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == user) return true;
            return false;
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (shutDown) return;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                pending.Enqueue(TransportEvent.Disconnected(0, "could not create Steam lobby: " + (ioFailure ? "I/O failure" : result.m_eResult.ToString())));
                return;
            }
            lobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobby, LobbyKey, Protocol.ProtocolVersion.ToString());
            Name = $"Steam host (lobby {lobby.m_SteamID})";
        }

        private void OnLobbyEnter(LobbyEnter_t entered)
        {
            if (shutDown || isHost) return;
            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                pending.Enqueue(TransportEvent.Disconnected(0, "could not join Steam lobby: " + (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse));
                return;
            }
            lobby = new CSteamID(entered.m_ulSteamIDLobby);
            owner = SteamMatchmaking.GetLobbyOwner(lobby);
            // The first packet we send opens the P2P session; the host accepts it because we are in its lobby.
            Connect(owner.m_SteamID);
        }

        private void OnSessionRequest(P2PSessionRequest_t request)
        {
            if (shutDown) return;
            var remote = request.m_steamIDRemote;
            if (isHost ? IsLobbyMember(remote) : remote == owner)
            {
                SteamNetworking.AcceptP2PSessionWithUser(remote);
                Connect(remote.m_SteamID);
            }
        }

        private void OnConnectFail(P2PSessionConnectFail_t failure)
        {
            if (shutDown) return;
            Drop(failure.m_steamIDRemote.m_SteamID, "P2P connection failed (error " + failure.m_eP2PSessionError + ")");
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t update)
        {
            if (shutDown || update.m_ulSteamIDLobby != lobby.m_SteamID) return;
            const uint leftMask = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft
                | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
                | EChatMemberStateChange.k_EChatMemberStateChangeKicked
                | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((update.m_rgfChatMemberStateChange & leftMask) != 0)
                Drop(update.m_ulSteamIDUserChanged, "left the Steam lobby");
        }
    }
}
