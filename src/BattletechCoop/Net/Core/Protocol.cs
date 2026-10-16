using System.IO;
using System.Text;

namespace BattletechCoop.Net
{
    // Wire format: one byte MessageType, then the body written with BinaryWriter (little-endian,
    // length-prefixed UTF-8 strings). Bump ProtocolVersion on any incompatible change.
    public static class Protocol
    {
        public const ushort ProtocolVersion = 1;
    }

    public enum MessageType : byte
    {
        Hello = 1,        // guest -> host: ushort protocol, string modVersion, string manifest, string name
        Welcome = 2,      // host -> guest: byte playerIndex, string hostName
        Reject = 3,       // host -> guest: string reason
        Ping = 4,         // either way: long senderTimestamp
        Pong = 5,         // reply: long senderTimestamp (echoed)
        Bye = 6,          // either way: string reason
        PlayerJoined = 7, // host -> guests: byte playerIndex, string name
        PlayerLeft = 8,   // host -> guests: byte playerIndex, string reason
        Game = 100,       // either way: ushort channel, int length, byte[] payload
    }

    public static class MessageWriter
    {
        public static byte[] Build(MessageType type, System.Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write((byte)type);
                body?.Invoke(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static BinaryReader Open(byte[] message, out MessageType type)
        {
            var reader = new BinaryReader(new MemoryStream(message), Encoding.UTF8);
            type = (MessageType)reader.ReadByte();
            return reader;
        }
    }
}
