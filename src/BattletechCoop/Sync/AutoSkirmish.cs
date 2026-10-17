using System;
using System.Linq;
using BattleTech;
using BattleTech.Data;
using BattleTech.UI;
using HBS;

namespace BattletechCoop.Sync
{
    // Test mode (-coop-autoskirmish): build and launch the same skirmish the skirmish screen would
    // (SkirmishSettings_Beta.LaunchMap), from fixed choices, so every test run plays the same match.
    public static class AutoSkirmish
    {
        private const string Player2TeamGuid = "757173dd-b4e1-4bb5-9bee-d78e623cc867";

        private static readonly string[] Mechs =
        {
            "mechdef_atlas_AS7-D", "mechdef_centurion_CN9-A", "mechdef_hunchback_HBK-4G", "mechdef_jenner_JR7-D",
        };

        private static readonly string[] Pilots =
        {
            "pilot_d10_skirmisher", "pilot_d9_skirmisher", "pilot_d8_skirmisher", "pilot_d7_skirmisher",
        };

        // HasInstance first: touching UnityGameInstance before the scene creates it logs Unity errors.
        public static bool MainMenuShowing =>
            SceneSingletonBehavior<UnityGameInstance>.HasInstance
            && UnityGameInstance.BattleTechGame != null && UnityGameInstance.BattleTechGame.Combat == null
            && LazySingletonBehavior<UIManager>.Instance.GetFirstModule<MainMenu>() != null;

        // mapName: a Map_MDD.MapName to prefer; otherwise the first released multiplayer map by name.
        // The lance is built from loaded MechDef/PilotDef objects, like the skirmish screen does,
        // because that is what real players send (string IDs alone hid a guest spawn crash).
        public static void Launch(string mapName)
        {
            var dataManager = UnityGameInstance.BattleTechGame.DataManager;
            var request = dataManager.CreateLoadRequest(_ => Build(mapName), false);
            foreach (var id in Mechs.Distinct()) request.AddBlindLoadRequest(BattleTechResourceType.MechDef, id, false);
            foreach (var id in Pilots.Distinct()) request.AddBlindLoadRequest(BattleTechResourceType.PilotDef, id, false);
            request.ProcessRequests();
        }

        private static void Build(string mapName)
        {
            var maps = MetadataDatabase.Instance.GetReleasedMapsAndEncounters(false, true, true)
                .Where(m => m.Encounters.Any(e => e.ContractTypeValue.IsSkirmish))
                .OrderBy(m => m.Map.MapName, StringComparer.Ordinal)
                .ToList();
            if (maps.Count == 0) throw new InvalidOperationException("no skirmish maps found");
            var map = maps.FirstOrDefault(m => m.Map.MapName == mapName) ?? maps[0];
            var encounter = map.Encounters.First(e => e.ContractTypeValue.IsSkirmish);
            Log.Info($"SYNC autoskirmish: map {map.Map.MapName}, encounter {encounter.EncounterLayerGUID} ({encounter.ContractTypeValue.Name}); available: {string.Join(", ", maps.Select(m => m.Map.MapName))}");

            var game = UnityGameInstance.BattleTechGame;
            var contract = new Contract(map.Map.MapName, map.Map.MapPath, encounter.EncounterLayerGUID, encounter.ContractTypeValue,
                game, null, null, false, -1, 0, null);
            var lances = new LanceConfiguration();
            for (var i = 0; i < Mechs.Length; i++)
            {
                var mech = game.DataManager.MechDefs.Get(Mechs[i]);
                var pilot = game.DataManager.PilotDefs.Get(Pilots[i]);
                if (mech == null || pilot == null) throw new InvalidOperationException($"could not load {Mechs[i]} / {Pilots[i]}");
                lances.AddUnit(CombatSync.Player1TeamGuid, mech, pilot);
                lances.AddUnit(Player2TeamGuid, mech, pilot);
            }
            contract.SetLanceConfiguration(lances);
            // Without a mood the map load stalls after the biome skin. Same choice as the skirmish
            // screen's mood list (MapModule.UpdateMoodList): the biome's arena moods, else "Baseline".
            var tags = new HBS.Collections.TagSet();
            tags.Add("mood_mapTypeArena");
            var moods = MetadataDatabase.Instance.GetSupportedMoodsForBiome(map.Map.BiomeSkinEntry.BiomeSkin, tags);
            contract.mapMood = moods.Count > 0 ? moods.OrderBy(m => m.Name, StringComparer.Ordinal).First().Name : "Baseline";
            Log.Info($"SYNC autoskirmish: mood {contract.mapMood}");
            ContractLauncher.LaunchFromMenu(contract);
        }
    }
}
