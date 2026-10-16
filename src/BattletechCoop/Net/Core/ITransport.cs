using System.Collections.Generic;

namespace BattletechCoop.Net
{
    public enum TransportEventKind
    {
        Connected,
        Data,
        Disconnected,
    }

    public struct TransportEvent
    {
        public TransportEventKind Kind;
        public ulong Peer;
        public byte[] Data;
        public string Reason;

        public static TransportEvent Connected(ulong peer) =>
            new TransportEvent { Kind = TransportEventKind.Connected, Peer = peer };

        public static TransportEvent Received(ulong peer, byte[] data) =>
            new TransportEvent { Kind = TransportEventKind.Data, Peer = peer, Data = data };

        public static TransportEvent Disconnected(ulong peer, string reason) =>
            new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = peer, Reason = reason };
    }

    // Reliable, ordered, message-oriented link to one or more peers. Implementations may use
    // background threads, but Poll/Send/Disconnect/Shutdown are only called from the main thread.
    // A guest's transport has exactly one peer: the host. Peer id 0 is reserved: a Disconnected
    // event for peer 0 means the transport itself failed (could not connect or could not host).
    public interface ITransport
    {
        string Name { get; }

        // Moves all pending events into `events`. Each peer yields Connected once, then any number
        // of Data events, then at most one Disconnected.
        void Poll(List<TransportEvent> events);

        void Send(ulong peer, byte[] message);

        void Disconnect(ulong peer);

        void Shutdown();
    }
}
