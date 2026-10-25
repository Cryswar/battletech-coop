using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattleTech;
using BattleTech.Framework;
using Harmony;

namespace BattletechCoop.Sync
{
    // A combat restart reloads the contract's mission definition (Contract.ResetStateForRestart)
    // and the launch rolls its "any 'Mech matching these tags" lances and units again, so the
    // guest's restart got different enemies than the host's launch (2026-10-25: 2 enemy 'Mechs on
    // the host, 3 on the guest). The host sends what its launch picked; the guest writes those
    // picks into the reloaded definition as explicit IDs before its launch rolls anything.
    public static class LancePins
    {
        private static readonly string[] Teams =
            { "employerTeam", "employersAllyTeam", "targetTeam", "targetsAllyTeam", "neutralToAllTeam", "hostileToAllTeam", "player2Team" };

        public class Pin
        {
            public string Team;
            public int Lance;
            public int Unit; // -1: the lance itself
            public string LanceDefId, UnitDefId, PilotDefId;
            public int UnitType;
        }

        // Guest: picks for the restart that is about to run.
        public static List<Pin> Pending;

        public static List<Pin> Collect(Contract contract)
        {
            var pins = new List<Pin>();
            var o = contract?.Override;
            if (o == null) return pins;
            foreach (var teamName in Teams)
            {
                var team = Traverse.Create(o).Field(teamName).GetValue<TeamOverride>();
                if (team?.lanceOverrideList == null) continue;
                for (var l = 0; l < team.lanceOverrideList.Count; l++)
                {
                    var lance = team.lanceOverrideList[l];
                    pins.Add(new Pin { Team = teamName, Lance = l, Unit = -1, LanceDefId = lance.selectedLanceDefId ?? "" });
                    for (var u = 0; u < (lance.unitSpawnPointOverrideList?.Count ?? 0); u++)
                    {
                        var unit = lance.unitSpawnPointOverrideList[u];
                        pins.Add(new Pin
                        {
                            Team = teamName, Lance = l, Unit = u,
                            UnitDefId = unit.selectedUnitDefId ?? "", UnitType = (int)unit.selectedUnitType, PilotDefId = unit.selectedPilotDefId ?? "",
                        });
                    }
                }
            }
            return pins;
        }

        public static void Write(BinaryWriter w, List<Pin> pins)
        {
            w.Write(pins.Count);
            foreach (var p in pins)
            {
                w.Write(p.Team); w.Write(p.Lance); w.Write(p.Unit);
                w.Write(p.LanceDefId ?? ""); w.Write(p.UnitDefId ?? ""); w.Write(p.UnitType); w.Write(p.PilotDefId ?? "");
            }
        }

        public static List<Pin> Read(BinaryReader r)
        {
            var pins = new List<Pin>();
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
                pins.Add(new Pin
                {
                    Team = r.ReadString(), Lance = r.ReadInt32(), Unit = r.ReadInt32(),
                    LanceDefId = r.ReadString(), UnitDefId = r.ReadString(), UnitType = r.ReadInt32(), PilotDefId = r.ReadString(),
                });
            return pins;
        }

        public static string Describe(List<Pin> pins) =>
            string.Join("; ", pins.Where(p => p.Unit >= 0 && p.UnitDefId.Length > 0).Select(p => $"{p.Team}[{p.Lance}.{p.Unit}]={p.UnitDefId}/{p.PilotDefId}"));

        public static void Apply(ContractOverride o, List<Pin> pins)
        {
            var applied = 0;
            foreach (var p in pins)
            {
                var team = Traverse.Create(o).Field(p.Team).GetValue<TeamOverride>();
                if (team?.lanceOverrideList == null || p.Lance >= team.lanceOverrideList.Count) continue;
                var lance = team.lanceOverrideList[p.Lance];
                if (p.Unit < 0)
                {
                    if (p.LanceDefId.Length > 0) { lance.lanceDefId = p.LanceDefId; applied++; }
                    continue;
                }
                if (lance.unitSpawnPointOverrideList == null || p.Unit >= lance.unitSpawnPointOverrideList.Count) continue;
                var unit = lance.unitSpawnPointOverrideList[p.Unit];
                if (p.UnitDefId.Length > 0)
                {
                    unit.unitDefId = p.UnitDefId;
                    unit.unitType = (UnitType)p.UnitType;
                    applied++;
                }
                if (p.PilotDefId.Length > 0) unit.pilotDefId = p.PilotDefId;
            }
            Log.Info($"SYNC guest: pinned {applied} lance and unit picks from the host's launch");
        }
    }

    [HarmonyPatch(typeof(Contract), "ResetStateForRestart")]
    public static class RestartPinsPatch
    {
        static void Postfix(Contract __instance)
        {
            if (LancePins.Pending == null || __instance.Override == null) return;
            LancePins.Apply(__instance.Override, LancePins.Pending);
            LancePins.Pending = null;
        }
    }
}
