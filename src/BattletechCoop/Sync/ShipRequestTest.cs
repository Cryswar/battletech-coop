using System.Linq;
using BattleTech;
using BattletechCoop.Net;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // -coop-test-requests (host and guest): make the changes a player would (rename a 'Mech, refit
    // it, train and rename a pilot) through the same functions the screens call. The guest does it a
    // few seconds after the host's ship is showing, so its requests reach the host; the host does it
    // a few seconds after sending its ship, so its updates reach the guest (queued there while the
    // ship is still loading). They pick different 'Mechs and pilots (guest: first, host: last).
    // tools/two-copy-test.ps1 -ShipRequests then compares what each side logs.
    public static class ShipRequestTest
    {
        public static bool Enabled;
        private static bool done;

        public static void Update()
        {
            if (!Enabled || done) return;
            var session = CoopController.Instance?.Session;
            if (session == null) return;
            var host = session.Role == SessionRole.Host;
            var since = host ? ShipView.SentAt : ShipView.ShowingSince;
            if (since < 0 || Time.realtimeSinceStartup < since + (host ? 3f : 5f)) return;
            done = true;
            var sim = UnityGameInstance.BattleTechGame?.Simulation;
            if (sim == null) { Log.Error("SHIP test: no campaign showing"); return; }
            Run(sim, host);
        }

        private static void Run(SimGameState sim, bool host)
        {
            var mechs = sim.ActiveMechs.Values.Where(m => sim.GetWorkOrderEntryForMech(m) == null
                && m.Inventory.Any(c => c.ComponentDefType == ComponentType.Weapon && !c.IsFixed)).ToList();
            if (mechs.Count < 2) { Log.Error("SHIP test: need two 'Mechs to refit"); return; }
            var mech = host ? mechs.Last() : mechs.First();

            var newName = mech.Name + (host ? " HO" : " CO");
            ShipRequests.CaptureRename(mech, newName);
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
            bool CanTrain(Pilot p) => p.Gunnery < 10 && p.UnspentXP >= sim.GetLevelCost(p.Gunnery + 1);
            var trainable = sim.PilotRoster.Where(CanTrain).ToList();
            var pilot = host
                ? (trainable.Count > 1 ? trainable.Last() : sim.PilotRoster.Last())
                : (trainable.FirstOrDefault() ?? sim.PilotRoster.First());
            if (CanTrain(pilot))
            {
                var cost = sim.GetLevelCost(pilot.Gunnery + 1);
                var trained = ShipRequests.Train(sim, pilot, new[] { pilot.Gunnery + 1, pilot.Piloting, pilot.Guts, pilot.Tactics }, out var error);
                if (trained == null) { Log.Error("SHIP test: could not train: " + error); return; }
                trained.SpendExperience(0, "Advancement", (uint)cost);
                ShipRequests.CapturePilotSkills(pilot, trained);
                sim.UpgradePilot(trained);
                pilot = sim.PilotRoster.First(p => p.GUID == trained.GUID);
            }
            else Log.Info("SHIP test: this pilot cannot afford training; only renaming");
            var d = pilot.pilotDef.Description;
            d.SetCallsign(d.Callsign + (host ? "HO" : "CO"));
            d.SetName(d.Callsign);
            ShipRequests.CapturePilotNames(pilot);
            Log.Info($"SHIP test: {(host ? "host" : "guest")} pilot now " + ShipRequests.PilotSummary(pilot));
        }
    }
}
