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

        public static bool MainMenuShowing =>
            UnityGameInstance.BattleTechGame != null && UnityGameInstance.BattleTechGame.Combat == null
            && LazySingletonBehavior<UIManager>.Instance.GetFirstModule<MainMenu>() != null;

        // mapName: a Map_MDD.MapName to prefer; otherwise the first released multiplayer map by name.
        public static void Launch(string mapName)
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
                lances.AddUnit(CombatSync.Player1TeamGuid, Mechs[i], Pilots[i], UnitType.Mech);
                lances.AddUnit(Player2TeamGuid, Mechs[i], Pilots[i], UnitType.Mech);
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
