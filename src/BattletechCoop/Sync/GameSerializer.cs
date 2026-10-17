using System;
using BattleTech;
using BattleTech.Network.Models.Messages;
using BattleTech.Serialization;

namespace BattletechCoop.Sync
{
    // The game's own network serialization, called with the same arguments as
    // BattleTech.Network.NetworkMessageResolver (target All = 15, mask HAS_ANY = 2), so anything
    // multiplayer can send (invocations inside a NetMessageCenterMessage, contracts inside a
    // GameContractNetworkMessage) round-trips the same way here.
    public static class GameSerializer
    {
        private const SerializationTarget Target = (SerializationTarget)15;
        private const TargetMaskOperation Mask = (TargetMaskOperation)2;

        public static byte[] SerializeMessage(MessageCenterMessage message)
        {
            var envelope = new NetMessageCenterMessage();
            envelope.Message = message;
            return Serializer.Serialize(envelope, typeof(NetMessageCenterMessage), Target, Mask);
        }

        public static MessageCenterMessage DeserializeMessage(byte[] data)
        {
            var envelope = (NetMessageCenterMessage)Serializer.Deserialize(data, typeof(NetMessageCenterMessage), Target, Mask);
            return envelope?.Message;
        }

        public static byte[] SerializeContract(Contract contract)
        {
            var envelope = new GameContractNetworkMessage();
            envelope.Contract = contract;
            return Serializer.Serialize(envelope, typeof(GameContractNetworkMessage), Target, Mask);
        }

        public static Contract DeserializeContract(byte[] data)
        {
            var envelope = (GameContractNetworkMessage)Serializer.Deserialize(data, typeof(GameContractNetworkMessage), Target, Mask);
            if (envelope?.Contract == null) throw new InvalidOperationException("contract missing from combat start message");
            return envelope.Contract;
        }
    }
}
