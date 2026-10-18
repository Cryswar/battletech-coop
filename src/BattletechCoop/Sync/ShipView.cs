using System;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.Save;
using BattleTech.Save.SaveGameStructure;
using BattletechCoop.Net;
using Harmony;
using HBS;

namespace BattletechCoop.Sync
{
    // Between missions the guest looks at the host's company: the host sends its campaign state as
    // an in-memory save (same bytes as a save file) and the guest loads it, like mission saves
    // (DESIGN.md "Ship view"). The guest's copy is never saved; anything changed there is local and
    // replaced by the next refresh, except requests the host accepts (refits, pilots: next).
    public static class ShipView
    {
        public const ushort ChannelShipSave = 19;
        public const ushort ChannelShipRefresh = 20;

        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);

        // Host
        private static bool dirty;
        private static bool wasInShip;
        private static DateTime lastSent = DateTime.MinValue;

        // Guest
        public static bool GuestViewing { get; private set; }
        public static string GuestCompany { get; private set; } = "";
        private static byte[] pendingLoad;
        private static object simBeforeLoad;
        private static bool awaitingLoaded;
        public static float ShowingSince { get; private set; } = -1;
        public static SimGameState UXReady; // set by SimGameAttachUXPatch

        public static void Reset()
        {
            dirty = false;
            wasInShip = false;
            pendingLoad = null;
            GuestViewing = false;
            ShowingSince = -1;
        }

        // Something the guest should see changed (or the guest asked).
        public static void MarkDirty(string why)
        {
            if (!dirty) Log.Info($"SHIP host: will send the ship ({why})");
            dirty = true;
        }

        private static bool InShip(GameInstance game) =>
            game != null && game.Simulation != null && game.Combat == null;

        public static void Update(CoopSession session)
        {
            if (session == null || session.State != SessionState.Active) return;
            if (session.Role == SessionRole.Host) UpdateHost(session);
            else UpdateGuest();
        }

        private static void UpdateHost(CoopSession session)
        {
            var game = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
            var inShip = InShip(game);
            if (inShip && !wasInShip) MarkDirty("host arrived on the ship");
            wasInShip = inShip;
            if (!dirty || !inShip || CombatSync.Current != null) return;
            if (session.Players.Count(p => p.Index != 0) == 0) return;
            if (DateTime.UtcNow - lastSent < MinInterval) return;
            if (!game.Simulation.CanSave(SaveReason.MANUAL, false)) return;

            dirty = false;
            lastSent = DateTime.UtcNow;
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var bytes = SaveTransfer.Serialize(new GameInstanceSave(game, SaveReason.MANUAL));
                var company = game.Simulation.CompanyName ?? "";
                session.SendGame(ChannelShipSave, Build(w => { w.Write(company); w.Write(bytes.Length); w.Write(bytes); }));
                Log.Info($"SHIP host: sent {Summary(game.Simulation)} ({bytes.Length} bytes, {started.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                Log.Error("SHIP host: could not send the ship", e);
            }
        }

        private static void UpdateGuest()
        {
            if (awaitingLoaded)
            {
                var current = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
                // The new SimGameState exists before the save is applied to it (default company,
                // funds 0) and before its screens are attached, so wait for the game's own
                // "UX attached" point on the loaded company.
                var sim = current?.Simulation;
                if (sim != null && !ReferenceEquals(sim, simBeforeLoad) && current.Combat == null
                    && ReferenceEquals(UXReady, sim) && sim.CompanyName == GuestCompany)
                {
                    awaitingLoaded = false;
                    ShowingSince = UnityEngine.Time.realtimeSinceStartup;
                    Log.Info("SHIP guest: now showing " + Summary(current.Simulation));
                }
            }
            if (pendingLoad == null) return;
            if (CombatSync.Current != null) { pendingLoad = null; return; } // a mission is on its way
            var game = SceneSingletonBehavior<UnityGameInstance>.HasInstance ? UnityGameInstance.BattleTechGame : null;
            if (game == null || game.Combat != null) return; // finish the battle / results first
            if (LazySingletonBehavior<LoadTransitioning>.Instance == null) return;

            var bytes = pendingLoad;
            pendingLoad = null;
            try
            {
                var save = SaveTransfer.Deserialize(bytes);
                CombatSync.GuestInHostGame = true;
                GuestViewing = true;
                ShowingSince = -1;
                simBeforeLoad = game.Simulation;
                awaitingLoaded = true;
                Log.Info($"SHIP guest: loading the host's ship '{GuestCompany}'");
                Traverse.Create(LazySingletonBehavior<LoadTransitioning>.Instance)
                    .Method("FileReadComplete", new object[] { true, save }).GetValue();
            }
            catch (Exception e)
            {
                Log.Error("SHIP guest: could not load the host's ship", e);
            }
        }

        public static void HandleGameMessage(CoopSession session, ushort channel, byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                if (channel == ChannelShipSave && session.Role == SessionRole.Guest)
                {
                    GuestCompany = r.ReadString();
                    pendingLoad = r.ReadBytes(r.ReadInt32());
                    Log.Info($"SHIP guest: received the host's ship '{GuestCompany}' ({pendingLoad.Length} bytes)");
                }
                else if (channel == ChannelShipRefresh && session.Role == SessionRole.Host)
                {
                    MarkDirty("guest asked for a refresh");
                }
            }
        }

        public static void RequestRefresh(CoopSession session)
        {
            session.SendGame(ChannelShipRefresh, new byte[0]);
            Log.Info("SHIP guest: asked the host for a refresh");
        }

        // Logged on both sides after a ship load, for comparing in tests.
        public static string Summary(SimGameState sim) =>
            sim == null ? "no campaign" : $"'{sim.CompanyName}' funds {sim.Funds} day {sim.DaysPassed} system {sim.CurSystem?.Name}";

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

    [HarmonyPatch(typeof(SimGameState), "_OnAttachUXComplete")]
    public static class SimGameAttachUXPatch
    {
        static void Postfix(SimGameState __instance) => ShipView.UXReady = __instance;
    }
}
