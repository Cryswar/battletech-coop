using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;

namespace BattletechCoop.Sync
{
    // Level B ship co-op (DESIGN.md "Ship requests"): the guest's ship is a throwaway copy of the
    // host's, so its own game applies a refit or pilot change at once (the guest sees it), and the
    // same change goes to the host as a request. The host rebuilds it with its own objects through
    // the game's own functions (work orders, UpgradePilot), so costs, refit time and parts from
    // storage work as if the host had done it, then replies applied/refused.
    //
    // Refits are sent as their work orders (install/remove, armor, structure, component repair),
    // not as objects: parts the guest took from storage get new IDs on the guest, and the host's
    // game finds those parts in its own storage by part type.
    public static class ShipRequests
    {
        public const ushort ChannelRequest = 21;
        public const ushort ChannelReply = 22;

        private const byte KindRefit = 1, KindRename = 2, KindPilotSkills = 3, KindPilotNames = 4;
        private const byte EntryBase = 0, EntryInstall = 1, EntryArmor = 2, EntryStructure = 3, EntryRepair = 4;

        public static string Notice { get; private set; } = "";
        public static float NoticeUntil { get; private set; }

        // Guest: requests are sent only while showing the host's ship.
        private static CoopSession GuestSession()
        {
            var session = CoopController.Instance?.Session;
            if (session == null || session.Role != SessionRole.Guest || session.State != SessionState.Active) return null;
            if (!ShipView.GuestViewing || CombatSync.Current != null) return null;
            var game = UnityGameInstance.BattleTechGame;
            return game?.Simulation != null && game.Combat == null ? session : null;
        }

        private static void Send(CoopSession session, byte kind, string what, Action<BinaryWriter> body)
        {
            session.SendGame(ChannelRequest, Build(w => { w.Write(kind); w.Write(what); body(w); }));
            Log.Info("SHIP guest: asked the host to " + what);
        }

        // ---- Guest: capture ----

        public static void GuestRename(MechDef mech, string newName)
        {
            var session = GuestSession();
            if (session == null || mech == null || newName == null || mech.Name == newName) return;
            Send(session, KindRename, $"rename '{mech.Name}' to '{newName}'", w => { w.Write(mech.GUID); w.Write(newName); });
        }

        public static void GuestRefit(SimGameState sim, WorkOrderEntry_MechLab entry)
        {
            var session = GuestSession();
            if (session == null || entry == null) return;
            var mech = sim.GetMechByID(entry.MechID);
            var name = mech?.Name ?? entry.MechID;
            try
            {
                var body = Build(w => { w.Write(entry.MechID); WriteEntry(w, entry, mech); });
                Send(session, KindRefit, $"refit '{name}' ({CountTasks(entry)} tasks, {entry.GetCBillCostForIncompleteTasks()} C-Bills)",
                    w => { w.Write(body.Length); w.Write(body); });
            }
            catch (NotSupportedException e)
            {
                ShowNotice($"Not sent to the host: {e.Message}");
                Log.Info("SHIP guest: refit not sent: " + e.Message);
            }
        }

        public static void GuestPilotSkills(Pilot before, Pilot after)
        {
            var session = GuestSession();
            if (session == null || before == null || after == null) return;
            var xp = after.SpentXP - before.SpentXP;
            Send(session, KindPilotSkills,
                $"train '{after.Callsign}' to G{after.Gunnery} P{after.Piloting} U{after.Guts} T{after.Tactics} ({xp} XP)",
                w =>
                {
                    w.Write(after.GUID);
                    w.Write(after.Gunnery); w.Write(after.Piloting); w.Write(after.Guts); w.Write(after.Tactics);
                    w.Write(xp);
                });
        }

        public static void GuestPilotNames(Pilot pilot)
        {
            var session = GuestSession();
            if (session == null || pilot == null) return;
            var d = pilot.pilotDef.Description;
            Send(session, KindPilotNames, $"rename pilot to '{d.Callsign}' ({d.FirstName} {d.LastName})", w =>
            {
                w.Write(pilot.GUID);
                w.Write(d.FirstName ?? ""); w.Write(d.LastName ?? ""); w.Write(d.Callsign ?? "");
                w.Write((int)d.Gender);
                w.Write(pilot.pilotDef.Voice ?? "");
            });
        }

        private static void WriteEntry(BinaryWriter w, WorkOrderEntry entry, MechDef mech)
        {
            switch (entry)
            {
                case WorkOrderEntry_InstallComponent e:
                    w.Write(EntryInstall);
                    w.Write(e.MechComponentID); w.Write((int)e.ComponentType); w.Write(e.ComponentSimGameUID ?? "");
                    w.Write((int)e.DamageLevel); w.Write((int)e.DesiredLocation); w.Write((int)e.PreviousLocation);
                    w.Write(e.HardpointSlot);
                    break;
                case WorkOrderEntry_ModifyMechArmor e:
                    w.Write(EntryArmor);
                    w.Write((int)e.Location); w.Write(e.DesiredFrontArmor); w.Write(e.DesiredRearArmor);
                    break;
                case WorkOrderEntry_RepairMechStructure e:
                    w.Write(EntryStructure);
                    w.Write((int)e.Location); w.Write(e.StructureAmount);
                    break;
                case WorkOrderEntry_RepairComponent e:
                    w.Write(EntryRepair);
                    w.Write(e.MechComponentID); w.Write((int)e.ComponentType); w.Write(e.ComponentSimGameUID ?? "");
                    w.Write((int)e.DamageLevel);
                    w.Write(mech?.Inventory?.Any(c => c.SimGameUID == e.ComponentSimGameUID) == true);
                    break;
                case WorkOrderEntry_MechLab e when e.GetType() == typeof(WorkOrderEntry_MechLab):
                    w.Write(EntryBase);
                    w.Write((int)e.Type); w.Write(e.ID ?? ""); w.Write(e.Description ?? ""); w.Write(e.ToastDescription ?? "");
                    break;
                default:
                    throw new NotSupportedException($"{entry.GetType().Name} work orders are not supported in co-op yet");
            }
            var subs = entry.SubEntries ?? new List<WorkOrderEntry>();
            w.Write(subs.Count);
            foreach (var sub in subs) WriteEntry(w, sub, mech);
        }

        public static int CountTasks(WorkOrderEntry e) => 1 + (e.SubEntries?.Sum(CountTasks) ?? 0) - (e.GetType() == typeof(WorkOrderEntry_MechLab) ? 1 : 0);

        // ---- Messages ----

        public static void HandleGameMessage(CoopSession session, byte sender, ushort channel, byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                if (channel == ChannelReply && session.Role == SessionRole.Guest)
                {
                    var ok = r.ReadBoolean();
                    var text = r.ReadString();
                    Log.Info($"SHIP guest: host {(ok ? "applied" : "refused")}: {text}");
                    ShowNotice(ok ? "Host applied: " + text : "Host refused: " + text);
                }
                else if (channel == ChannelRequest && session.Role == SessionRole.Host)
                {
                    var kind = r.ReadByte();
                    var what = r.ReadString();
                    var player = session.Players.FirstOrDefault(p => p.Index == sender)?.Name ?? $"player {sender}";
                    string result;
                    bool ok;
                    try
                    {
                        result = Apply(kind, r);
                        ok = result == null;
                    }
                    catch (Exception e)
                    {
                        Log.Error($"SHIP host: {player}'s request failed ({what})", e);
                        result = "the host's game could not apply it (see the host's log)";
                        ok = false;
                    }
                    Log.Info($"SHIP host: {(ok ? "applied" : "refused")} {player}'s request to {what}{(ok ? "" : ": " + result)}");
                    ShowNotice(ok ? $"{player}: {what}" : $"Refused {player}'s request to {what}: {result}");
                    session.SendGame(ChannelReply, Build(w => { w.Write(ok); w.Write(ok ? what : $"{what}: {result}"); }), sender);
                }
            }
        }

        private static void ShowNotice(string text)
        {
            Notice = text;
            NoticeUntil = UnityEngine.Time.realtimeSinceStartup + 8f;
        }

        // ---- Host: apply. Returns null when applied, else why not. ----

        private static string Apply(byte kind, BinaryReader r)
        {
            var game = UnityGameInstance.BattleTechGame;
            var sim = game?.Simulation;
            if (sim == null || game.Combat != null || CombatSync.Current != null) return "the host is not on the ship";
            switch (kind)
            {
                case KindRename: return ApplyRename(sim, r.ReadString(), r.ReadString());
                case KindRefit: return ApplyRefit(sim, r.ReadBytes(r.ReadInt32()));
                case KindPilotSkills: return ApplyPilotSkills(sim, r);
                case KindPilotNames: return ApplyPilotNames(sim, r);
                default: return $"unknown request {kind} (are both players on the same mod version?)";
            }
        }

        private static string ApplyRename(SimGameState sim, string mechId, string newName)
        {
            var mech = sim.GetMechByID(mechId);
            if (mech == null) return "the host has no such 'Mech";
            mech.RenameMech(newName, null, null);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info($"SHIP host: 'Mech {mech.GUID} is now '{mech.Name}'");
            return null;
        }

        private static string ApplyRefit(SimGameState sim, byte[] body)
        {
            using (var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8))
            {
                var mechId = r.ReadString();
                var mech = sim.GetMechByID(mechId);
                if (mech == null) return "the host has no such 'Mech";
                if (sim.GetWorkOrderEntryForMech(mech) != null) return $"'{mech.Name}' already has a refit in progress on the host";
                var entry = ReadEntry(r, sim, mech) as WorkOrderEntry_MechLab;
                if (entry == null) return "unexpected work order";
                var cost = entry.GetCBillCostForIncompleteTasks();
                if (cost > sim.Funds) return $"needs {cost} C-Bills, the company has {sim.Funds}";

                // As MechBayPanel.OnMechLabComplete, without the UI.
                if (!sim.MechLabQueue.Contains(entry)) sim.MechLabQueue.Add(entry);
                var funds = sim.Funds;
                sim.InitializeMechLabEntry(entry, 0);
                sim.UpdateMechLabWorkQueue(false);
                sim.RoomManager?.RefreshTimeline(false);
                Log.Info($"SHIP host: queued refit '{mech.Name}' ({CountTasks(entry)} tasks, {cost} C-Bills), funds {funds} -> {sim.Funds}");
                return null;
            }
        }

        private static WorkOrderEntry ReadEntry(BinaryReader r, SimGameState sim, MechDef mech)
        {
            WorkOrderEntry entry;
            var kind = r.ReadByte();
            switch (kind)
            {
                case EntryBase:
                {
                    var type = (WorkOrderType)r.ReadInt32();
                    var id = r.ReadString();
                    var description = r.ReadString();
                    var toast = r.ReadString();
                    var e = new WorkOrderEntry_MechLab(type, id, description, mech.GUID, 0, toast);
                    e.SetMechDef(mech);
                    entry = e;
                    break;
                }
                case EntryInstall:
                {
                    var defId = r.ReadString();
                    var type = (ComponentType)r.ReadInt32();
                    var uid = r.ReadString();
                    var damage = (ComponentDamageLevel)r.ReadInt32();
                    var desired = (ChassisLocations)r.ReadInt32();
                    var previous = (ChassisLocations)r.ReadInt32();
                    var hardpoint = r.ReadInt32();
                    var component = FindOrMakeComponent(sim, mech, defId, type, uid, damage, previous, hardpoint);
                    entry = sim.CreateComponentInstallWorkOrder(mech.GUID, component, desired, previous);
                    break;
                }
                case EntryArmor:
                {
                    var location = (ChassisLocations)r.ReadInt32();
                    var front = r.ReadInt32();
                    var rear = r.ReadInt32();
                    // As MechLabLocationWidget.SetArmor: the change from the 'Mech's current armor.
                    var loadout = mech.GetLocationLoadoutDef(location);
                    var diff = (int)Math.Abs(front - loadout.AssignedArmor) + (int)Math.Abs(rear - loadout.AssignedRearArmor);
                    entry = sim.CreateMechArmorModifyWorkOrder(mech.GUID, location, diff, front, rear);
                    break;
                }
                case EntryStructure:
                {
                    var location = (ChassisLocations)r.ReadInt32();
                    var amount = r.ReadInt32();
                    entry = sim.CreateMechRepairWorkOrder(mech.GUID, location, amount);
                    break;
                }
                case EntryRepair:
                {
                    var defId = r.ReadString();
                    var type = (ComponentType)r.ReadInt32();
                    var uid = r.ReadString();
                    var damage = (ComponentDamageLevel)r.ReadInt32();
                    var onMech = r.ReadBoolean();
                    var component = FindOrMakeComponent(sim, mech, defId, type, uid, damage, ChassisLocations.None, -1);
                    entry = sim.CreateComponentRepairWorkOrder(component, onMech);
                    break;
                }
                default:
                    throw new InvalidDataException($"unknown work order kind {kind}");
            }
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++) entry.AddSubEntry(ReadEntry(r, sim, mech));
            return entry;
        }

        // A part on the host's 'Mech by its ID, else the same part type at that location (the guest
        // may have installed it in its preview, giving it a new ID), else a new reference that the
        // host's game then takes from storage by type.
        private static MechComponentRef FindOrMakeComponent(SimGameState sim, MechDef mech, string defId, ComponentType type,
            string uid, ComponentDamageLevel damage, ChassisLocations location, int hardpoint)
        {
            var inventory = mech.Inventory ?? new MechComponentRef[0];
            var found = inventory.FirstOrDefault(c => !string.IsNullOrEmpty(uid) && c.SimGameUID == uid)
                        ?? (location == ChassisLocations.None ? null
                            : inventory.FirstOrDefault(c => c.ComponentDefID == defId && c.MountedLocation == location && c.DamageLevel == damage));
            if (found != null) return found;
            var component = new MechComponentRef(defId, sim.GenerateSimGameUID(), type, location, hardpoint, damage, false);
            component.DataManager = sim.DataManager;
            component.RefreshComponentDef();
            if (component.Def == null) throw new InvalidDataException($"the host has no part '{defId}' (different mods?)");
            return component;
        }

        private static Pilot FindPilot(SimGameState sim, string guid) =>
            sim.PilotRoster.FirstOrDefault(p => p.GUID == guid) ?? (sim.Commander?.GUID == guid ? sim.Commander : null);

        private static string ApplyPilotSkills(SimGameState sim, BinaryReader r)
        {
            var pilot = FindPilot(sim, r.ReadString());
            var target = new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
            var xp = r.ReadInt32();
            if (pilot == null) return "the host has no such pilot";
            if (xp > pilot.UnspentXP) return $"needs {xp} XP, the pilot has {pilot.UnspentXP}";

            var p = Train(sim, pilot, target, out var error);
            if (p == null) return error;
            if (xp > 0) p.SpendExperience(0, "Advancement", (uint)xp);
            sim.UpgradePilot(p);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info("SHIP host: pilot now " + PilotSummary(FindPilot(sim, pilot.GUID)));
            return null;
        }

        public static string PilotSummary(Pilot p) => p == null ? "missing" :
            $"'{p.Callsign}' ({p.pilotDef.Description.FirstName} {p.pilotDef.Description.LastName}) G{p.Gunnery} P{p.Piloting} U{p.Guts} T{p.Tactics} XP {p.UnspentXP}/{p.SpentXP} abilities {string.Join(",", p.pilotDef.abilityDefNames.OrderBy(a => a))}";

        // As SGBarracksAdvancementPanel.SetTempPilotSkill, one level at a time (the confirm then
        // spends the XP and calls UpgradePilot). Gunnery, Piloting, Guts, Tactics.
        public static Pilot Train(SimGameState sim, Pilot pilot, int[] target, out string error)
        {
            error = null;
            var skills = new[] { "Gunnery", "Piloting", "Guts", "Tactics" };
            var current = new[] { pilot.Gunnery, pilot.Piloting, pilot.Guts, pilot.Tactics };
            var p = pilot;
            for (var s = 0; s < skills.Length; s++)
            {
                if (target[s] < current[s]) { error = $"{skills[s]} is already {current[s]} on the host"; return null; }
                for (var level = current[s]; level < target[s]; level++)
                {
                    var def = p.ToPilotDef(true);
                    def.DataManager = sim.DataManager;
                    def.ForceRefreshAbilityDefs();
                    foreach (var ability in sim.AbilityTree[skills[s]][level])
                        if (sim.CanPilotTakeAbility(def, ability, false)) def.abilityDefNames.Add(ability.Description.Id);
                    def.ForceRefreshAbilityDefs();
                    var next = new Pilot(def, p.GUID, true);
                    next.pilotDef.DataManager = p.pilotDef.DataManager;
                    next.ModifyPilotStat_Barracks(0, "Advancement", skills[s], (uint)(level + 1));
                    p = next;
                }
            }
            return p;
        }

        private static string ApplyPilotNames(SimGameState sim, BinaryReader r)
        {
            var pilot = FindPilot(sim, r.ReadString());
            var first = r.ReadString();
            var last = r.ReadString();
            var callsign = r.ReadString();
            var gender = r.ReadInt32();
            var voice = r.ReadString();
            if (pilot == null) return "the host has no such pilot";
            // As SGBarracksMWCustomizationPopup.Save (portraits are not sent yet).
            var d = pilot.pilotDef.Description;
            d.SetFirstName(first);
            d.SetLastName(last);
            d.SetCallsign(callsign);
            d.SetName(callsign);
            d.SetGender((Gender)gender);
            if (voice.Length > 0) pilot.pilotDef.SetVoice(voice);
            sim.RoomManager?.RefreshTimeline(false);
            Log.Info("SHIP host: pilot now " + PilotSummary(pilot));
            return null;
        }

        private static byte[] Build(Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                body(w);
                w.Flush();
                return stream.ToArray();
            }
        }
    }

    // ---- Guest capture patches ----

    // MechLab confirm: the rename happens here (also without work orders).
    [HarmonyPatch(typeof(MechBayPanel), "OnMechLabComplete")]
    public static class MechLabCompletePatch
    {
        static void Prefix(MechBayPanel __instance, string newMechNickname)
        {
            var mech = Traverse.Create(__instance).Field("selectedMech").GetValue<MechBayMechUnitElement>()?.MechDef;
            ShipRequests.GuestRename(mech, newMechNickname);
        }
    }

    // Every refit or repair the player orders (MechLab, repair, repair all) starts here.
    [HarmonyPatch(typeof(SimGameState), "InitializeMechLabEntry")]
    public static class InitializeMechLabEntryPatch
    {
        static void Prefix(SimGameState __instance, WorkOrderEntry_MechLab entry) => ShipRequests.GuestRefit(__instance, entry);
    }

    [HarmonyPatch(typeof(SGBarracksMWDetailPanel), "OnPilotConfirmAccepted")]
    public static class PilotConfirmPatch
    {
        static void Prefix(SGBarracksMWDetailPanel __instance)
        {
            var t = Traverse.Create(__instance);
            ShipRequests.GuestPilotSkills(t.Field("curPilot").GetValue<Pilot>(), t.Field("tempPilot").GetValue<Pilot>());
        }
    }

    // The customization popup is in the global namespace.
    [HarmonyPatch]
    public static class PilotCustomizationPatch
    {
        static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("SGBarracksMWCustomizationPopup"), "Save");

        static void Postfix(object __instance) =>
            ShipRequests.GuestPilotNames(Traverse.Create(__instance).Property("pilot").GetValue<Pilot>());
    }
}
