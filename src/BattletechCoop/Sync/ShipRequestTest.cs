using System.Linq;
using BattleTech;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // -coop-test-requests (guest): a few seconds after the host's ship is showing, make the changes a
    // player would (rename a 'Mech, refit it, train and rename a pilot) through the same functions
    // the screens call, so the requests reach the host. tools/two-copy-test.ps1 -ShipRequests then
    // compares what each side logs.
    public static class ShipRequestTest
    {
        public static bool Enabled;
        private static bool done;

        public static void Update()
        {
            if (!Enabled || done || ShipView.ShowingSince < 0 || Time.realtimeSinceStartup < ShipView.ShowingSince + 5f) return;
            done = true;
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            if (sim == null) { Log.Error("SHIP test: no campaign showing"); return; }

            var mech = sim.ActiveMechs.Values.FirstOrDefault(m => sim.GetWorkOrderEntryForMech(m) == null
                && m.Inventory.Any(c => c.ComponentDefType == ComponentType.Weapon && !c.IsFixed));
            if (mech == null) { Log.Error("SHIP test: no 'Mech to refit"); return; }

            var newName = mech.Name + " CO";
            ShipRequests.GuestRename(mech, newName);
            mech.RenameMech(newName, null, null);

            // As MechLabPanel: a base order with one armor change and one weapon removed.
            var entry = new WorkOrderEntry_MechLab((WorkOrderType)100, "MechLab-BaseWorkOrder", "Modify 'Mech - " + mech.Description.Name,
                mech.GUID, 0, "Co-op test refit done");
            entry.SetMechDef(mech);
            var loadout = mech.GetLocationLoadoutDef(ChassisLocations.CenterTorso);
            var front = Mathf.Max(0, (int)loadout.AssignedArmor - 5);
            var rear = (int)loadout.AssignedRearArmor;
            entry.AddSubEntry(sim.CreateMechArmorModifyWorkOrder(mech.GUID, ChassisLocations.CenterTorso,
                (int)loadout.AssignedArmor - front, front, rear));
            var weapon = mech.Inventory.First(c => c.ComponentDefType == ComponentType.Weapon && !c.IsFixed);
            entry.AddSubEntry(sim.CreateComponentInstallWorkOrder(mech.GUID, weapon, ChassisLocations.None, weapon.MountedLocation));
            sim.MechLabQueue.Add(entry);
            sim.InitializeMechLabEntry(entry, 0); // the patch sends it
            sim.UpdateMechLabWorkQueue(false);

            // A pilot one Gunnery level up (if they can afford it), then renamed.
            var pilot = sim.PilotRoster.FirstOrDefault(p => p.Gunnery < 10 && p.UnspentXP >= sim.GetLevelCost(p.Gunnery + 1))
                        ?? sim.PilotRoster.First();
            if (pilot.Gunnery < 10 && pilot.UnspentXP >= sim.GetLevelCost(pilot.Gunnery + 1))
            {
                var trained = ShipRequests.Train(sim, pilot, new[] { pilot.Gunnery + 1, pilot.Piloting, pilot.Guts, pilot.Tactics }, out var error);
                if (trained == null) { Log.Error("SHIP test: could not train: " + error); return; }
                trained.SpendExperience(0, "Advancement", (uint)sim.GetLevelCost(pilot.Gunnery + 1));
                ShipRequests.GuestPilotSkills(pilot, trained);
                sim.UpgradePilot(trained);
                pilot = sim.PilotRoster.First(p => p.GUID == trained.GUID);
            }
            else Log.Info("SHIP test: no pilot can afford training; only renaming");
            var d = pilot.pilotDef.Description;
            d.SetCallsign(d.Callsign + "CO");
            d.SetName(d.Callsign);
            ShipRequests.GuestPilotNames(pilot);
            Log.Info("SHIP test: guest pilot now " + ShipRequests.PilotSummary(pilot));
        }
    }
}
