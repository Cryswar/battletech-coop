using System.Linq;
using BattleTech;
using BattletechCoop.Net;
using Harmony;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // -coop-test-mission (host, with -coop-test-shipview and -coop-autoplay): once the ship has
    // gone to the guest, launch the first ordinary contract of the current system the way the
    // lance screen's Accept does (SimGameState.OnContractReady with the last used lance), so a
    // Campaign/Career mission starts for both: briefing save, shared Begin, intro and opening
    // dialogue. tools/two-copy-test.ps1 -MissionStart compares what both sides show.
    public static class MissionTest
    {
        public static bool Enabled;
        private static bool done;

        public static void Update()
        {
            if (!Enabled || done) return;
            var session = CoopController.Instance?.Session;
            if (session == null || session.Role != SessionRole.Host || ShipView.SentAt < 0) return;
            if (Time.realtimeSinceStartup < ShipView.SentAt + 35f) return; // the guest loads the ship first
            var game = UnityGameInstance.BattleTechGame;
            if (game?.Simulation == null || game.Combat != null || CombatSync.Current != null) return;
            done = true;
            try { Launch(game.Simulation); }
            catch (System.Exception e) { Log.Error("MISSION test: could not launch a contract", e); }
        }

        private static void Launch(SimGameState sim)
        {
            var contracts = sim.CurSystem.SystemContracts
                .Where(c => !c.IsFlashpointContract && !c.IsStoryContract && !c.IsPriorityContract && c.CanLanceConfigure)
                .ToList();
            if (contracts.Count == 0) { Log.Error("MISSION test: no ordinary contract in this system"); return; }
            var contract = contracts[0];
            sim.SetSelectedContract(contract, false, false);

            var lance = Traverse.Create(sim).Method("GetLastLance").GetValue<LanceConfiguration>();
            if (lance == null || !lance.LanceValid)
            {
                lance = new LanceConfiguration();
                var pilots = new[] { sim.Commander }.Concat(sim.PilotRoster).Where(p => p != null).ToList();
                var mechs = sim.ActiveMechs.OrderBy(kv => kv.Key).Select(kv => kv.Value).Take(4).ToList();
                for (var i = 0; i < mechs.Count && i < pilots.Count; i++)
                    lance.AddUnit(CombatSync.Player1TeamGuid, mechs[i], pilots[i].pilotDef);
            }
            // The lance screen fills the contract's own lance; OnContractReady copies it only for
            // contracts that disable lance configuration.
            contract.Lances.AddUnits(lance.GetLanceUnits(CombatSync.Player1TeamGuid));
            Log.Info($"MISSION test: launching '{contract.Name}' ({contract.ContractTypeValue?.Name}) with {lance.GetLanceUnits(CombatSync.Player1TeamGuid).Length} units, contract lance valid {contract.Lances.LanceValid}");
            Traverse.Create(sim).Method("OnContractReady", lance).GetValue();
        }
    }
}
