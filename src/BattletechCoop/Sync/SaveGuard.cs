using BattleTech;
using BattleTech.Save;
using BattleTech.Save.Core;
using BattleTech.Save.SaveGameStructure;
using BattleTech.Serialization;
using BattletechCoop.Net;
using Harmony;

namespace BattletechCoop.Sync
{
    // Saves are Steam Cloud files (userdata/<id>/637090/remote/C0/SGS1). A co-op guest runs the
    // host's campaign, so any save it made would land in the guest's own slots; test copies on the
    // owner's PC share the owner's slots. Both never save (see DESIGN.md, M2).
    public static class SaveGuard
    {
        public static bool NoSavesFlag; // -coop-no-saves

        public static bool Blocked
        {
            get
            {
                if (NoSavesFlag) return true;
                var session = CoopController.Instance?.Session;
                return session != null && session.IsOpen && session.Role == SessionRole.Guest;
            }
        }

        public static bool Allow(string what)
        {
            if (!Blocked) return true;
            Log.Info($"SYNC: blocked a save ({what}); {(NoSavesFlag ? "-coop-no-saves" : "co-op guest")}");
            return false;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "TriggerSaveNow")]
    public static class TriggerSaveNowGuard
    {
        static bool Prefix(SaveReason reason) => SaveGuard.Allow("TriggerSaveNow " + reason);
    }

    [HarmonyPatch(typeof(SaveGameStructure), "Save")]
    public static class SaveGameStructureGuard
    {
        static bool Prefix(SaveReason saveReason) => SaveGuard.Allow("Save " + saveReason);
    }

    [HarmonyPatch(typeof(SaveGameStructure), "SaveCampaignResult")]
    public static class SaveCampaignResultGuard
    {
        static bool Prefix() => SaveGuard.Allow("SaveCampaignResult");
    }

    // The bytes of a real save file, built in memory: SaveBlock<GameInstanceSave> with target
    // SaveGame is what SaveGameStructure.Save hands to SaveSystem.CloudSaveBlock (compressed).
    public static class SaveTransfer
    {
        public static byte[] Serialize(GameInstanceSave save) =>
            new SaveBlock<GameInstanceSave>(SerializationTarget.SaveGame).ToBlockBytes(save);

        public static GameInstanceSave Deserialize(byte[] bytes) =>
            new SaveBlock<GameInstanceSave>(SerializationTarget.SaveGame).GetSaveData(bytes);
    }
}
