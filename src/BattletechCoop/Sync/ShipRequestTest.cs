using System.Linq;
using BattleTech;
using BattletechCoop.Net;
using Harmony;
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
        private static bool barracksOpened;

        public static void Update()
        {
            if (!Enabled || done) return;
            var session = CoopController.Instance?.Session;
            if (session == null) return;
            var host = session.Role == SessionRole.Host;
            var since = host ? ShipView.SentAt : ShipView.ShowingSince;
            // The host opens the Barracks first, so the guest's pilot changes must redraw it.
            if (host && !barracksOpened && since >= 0 && Time.realtimeSinceStartup >= since + 1f)
            {
                barracksOpened = true;
                var rooms = UnityGameInstance.BattleTechGame?.Simulation?.RoomManager;
                if (rooms != null)
                {
                    // As SimGameState.MemorialWallShortcut, with the personnel menu (2) instead.
                    rooms.SetQueuedUIActivationID((DropshipMenuType)2, (DropshipLocation)0, false);
                    rooms.ForceShipRoomChangeOfRoom((DropshipLocation)4);
                    Log.Info("SHIP test: host opened the Barracks");
                }
            }
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

            try { RunCompany(sim, host, mechs); }
            catch (System.Exception e) { Log.Error("SHIP test: company changes failed", e); }
            ShipRequests.LogDigest("after its own test changes");
        }

        // 0.0.12: hiring, Store, storing a 'Mech, dismissing, cancelling a work order, heraldry. The
        // guest dismisses the pilot the host hired and cancels the host's refit, which only works
        // if both copies gave them the same IDs.
        private static void RunCompany(SimGameState sim, bool host, System.Collections.Generic.List<MechDef> mechs)
        {
            var who = host ? "host" : "guest";
            var system = sim.CurSystem;

            if (host && mechs.Count >= 3)
            {
                var store = mechs[1];
                var slot = sim.ActiveMechs.First(kv => kv.Value == store).Key;
                sim.UnreadyMech(slot, store);
                Log.Info($"SHIP test: host stored '{store.Name}'");
            }

            var forHire = system.AvailablePilots.ToList();
            if (forHire.Count >= 2)
            {
                var def = host ? forHire.Last() : forHire.First();
                system.HirePilot(def);
                var hired = sim.PilotRoster.FirstOrDefault(p => p.pilotDef.Description.Id == def.Description.Id);
                Log.Info($"SHIP test: {who} hired '{def.Description.Callsign}' as {hired?.GUID}");
                if (host && hired != null)
                {
                    hired.pilotDef.Description.SetCallsign(hired.Callsign + "HIRE");
                    hired.pilotDef.Description.SetName(hired.Callsign);
                    ShipRequests.CapturePilotNames(hired);
                }
            }
            else Log.Info("SHIP test: fewer than two pilots for hire; not hiring");

            var shop = system.SystemShop;
            var items = shop?.ActiveInventory?.Where(i => i.Type == ShopItemType.Weapon).ToList();
            if (items != null && items.Count > 0)
            {
                var item = host ? items.Last() : items.First();
                var bought = shop.Purchase(item.ID, item.IsInfinite ? Shop.PurchaseType.Normal : Shop.PurchaseType.Special, item.Type);
                Log.Info($"SHIP test: {who} bought {item.ID}: {bought}");
                if (bought)
                {
                    var sell = new ShopDefItem();
                    sell.ID = item.ID;
                    sell.Type = item.Type;
                    sell.SellCost = 1000;
                    Log.Info($"SHIP test: {who} sold {item.ID}: {shop.SellInventoryItem(sell)}");
                }
            }
            else Log.Info("SHIP test: no weapons in the system store");

            if (host) return;
            var hostHired = sim.PilotRoster.FirstOrDefault(p => p.Callsign.EndsWith("HIRE"));
            if (hostHired != null)
            {
                Log.Info($"SHIP test: guest dismisses the host's hire '{hostHired.Callsign}' {hostHired.GUID}");
                sim.DismissPilot(hostHired);
            }
            else Log.Info("SHIP test: no host hire to dismiss");

            // Ready a stored 'Mech (the host stored one above), then cancel that order: readying
            // makes the new 'Mech's ID, and the cancel finds the order by it on the host.
            var stored = sim.CompanyStats.Items.FirstOrDefault(n => n.StartsWith("Item.MechDef.")
                && sim.CompanyStats.GetValue<int>(n) > 0);
            var freeSlot = Enumerable.Range(0, 18).Where(i => !sim.ActiveMechs.ContainsKey(i) && !sim.ReadyingMechs.ContainsKey(i))
                .DefaultIfEmpty(-1).First();
            if (stored != null && freeSlot >= 0)
            {
                var chassis = stored.Substring("Item.MechDef.".Length);
                sim.ReadyMech(freeSlot, chassis);
                var order = sim.MechLabQueue.OfType<WorkOrderEntry_ReadyMech>().LastOrDefault();
                Log.Info($"SHIP test: guest readies {chassis} in bay {freeSlot} as {order?.MechID}");
                if (order != null) sim.CancelWorkOrder(order, true);
            }
            else Log.Info($"SHIP test: nothing to ready (stored {stored}, free bay {freeSlot})");

            var heraldry = sim.Player1sMercUnitHeraldryDef.Copy();
            heraldry.Description.SetName(heraldry.Description.Name + " CO");
            var room = Traverse.Create(sim.RoomManager).Field("CptQuartersRoom").GetValue();
            Traverse.Create(room).Method("OnHeraldryClosed", heraldry).GetValue();
            Log.Info($"SHIP test: guest renamed the company to '{sim.CompanyName}'");
        }
    }
}
