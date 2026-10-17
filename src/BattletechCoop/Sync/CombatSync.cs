using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;
using HBS;

namespace BattletechCoop.Sync
{
    // Lockstep for one combat (see docs/DESIGN.md "M1 protocol"). The host runs the game normally
    // and streams every invocation, at the moment it executes, plus every AI "activation complete",
    // stamped with the turn-activation count. The guest never decides anything: it replays the
    // stream in order, one item at a time, only when its game is idle and at the same activation,
    // and compares a canonical state text before each invocation.
    public class CombatSync
    {
        public const ushort ChannelCombatStart = 10;
        public const ushort ChannelStream = 11;
        public const ushort ChannelGuestStatus = 12;
        public const ushort ChannelDesync = 13;
        public const ushort ChannelMenuReady = 14;

        // Guest: a combat the host started before this game reached its main menu.
        private static byte[] pendingStart;

        // Host: guests (by player index) that last reported being in their main menu.
        public static readonly HashSet<byte> GuestsInMenu = new HashSet<byte>();

        public const string Player1TeamGuid = "bf40fd39-ccf9-47c4-94a6-061809681140";

        private enum ItemKind : byte { Invocation = 1, AIComplete = 2 }

        private class Item
        {
            public uint Seq;
            public uint Activation;
            public ItemKind Kind;
            public string TypeName;
            public byte[] Data;
            public string State;
            public string TeamGuid;
        }

        public static CombatSync Current { get; private set; }

        public bool IsHost { get; }
        public bool Autoplay { get; }
        public uint CombatId { get; }
        public int Desyncs { get; private set; }
        public uint Activation { get; private set; }
        public uint LastSeq => seq;
        public int Pending => incoming.Count;
        public string Status => IsHost
            ? $"host: sent {seq}, guest applied {guestApplied}, guest desyncs {guestDesyncs}"
            : $"guest: applied {seq}, queued {incoming.Count}, desyncs {Desyncs}";

        private readonly CoopSession session;
        private readonly Queue<Item> incoming = new Queue<Item>();
        private readonly HashSet<string> streamedInvocations = new HashSet<string>();
        private readonly HashSet<string> reportedComplete = new HashSet<string>();
        private uint seq;
        private uint guestApplied;
        private int guestDesyncs;
        private Item applying;
        private bool missionOverLogged;
        private bool begunLogged;
        private float nextStatusTime;

        private CombatSync(CoopSession session, bool isHost, uint combatId, bool autoplay)
        {
            this.session = session;
            IsHost = isHost;
            CombatId = combatId;
            Autoplay = autoplay;
        }

        public static bool HostActive => Current != null && Current.IsHost;
        public static bool GuestActive => Current != null && !Current.IsHost;

        // ---- combat start ----

        // Host, from the LaunchContract prefix: send the contract to every guest before launching locally.
        public static void BeginAsHost(CoopSession session, Contract contract, bool autoplay)
        {
            var combatId = (uint)Environment.TickCount;
            var data = GameSerializer.SerializeContract(contract);
            session.SendGame(ChannelCombatStart, Build(w =>
            {
                w.Write(combatId);
                w.Write(autoplay);
                w.Write(data.Length);
                w.Write(data);
            }));
            Current = new CombatSync(session, true, combatId, autoplay);
            Log.Info($"SYNC host: combat {combatId} started, contract {data.Length} bytes sent, autoplay={autoplay}");
        }

        private static void BeginAsGuest(CoopSession session, byte[] payload)
        {
            if (!AutoSkirmish.MainMenuShowing)
            {
                if (UnityGameInstance.BattleTechGame?.Combat != null)
                {
                    Log.Error("SYNC guest: host started a combat but we are already in one; ignoring");
                    return;
                }
                pendingStart = payload;
                Log.Info("SYNC guest: host started a combat; launching it once our main menu is up");
                return;
            }
            pendingStart = null;
            var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            var combatId = r.ReadUInt32();
            var autoplay = r.ReadBoolean();
            var data = r.ReadBytes(r.ReadInt32());
            var contract = GameSerializer.DeserializeContract(data);
            Current = new CombatSync(session, false, combatId, autoplay);
            Log.Info($"SYNC guest: launching host's combat {combatId} ({data.Length} bytes), autoplay={autoplay}");

            ContractLauncher.LaunchFromMenu(contract);
        }

        // Guest, every frame while connected and not in a co-op combat: launch a deferred combat,
        // and tell the host whether we're in the main menu (auto-skirmish waits for that).
        private static float nextMenuReport;
        private static bool lastMenuReported;

        public static void UpdateIdleGuest(CoopSession session)
        {
            if (session.Role != SessionRole.Guest || session.State != SessionState.Active || Current != null) return;
            var inMenu = AutoSkirmish.MainMenuShowing;
            if (inMenu && pendingStart != null)
            {
                BeginAsGuest(session, pendingStart);
                return;
            }
            if (inMenu != lastMenuReported || UnityEngine.Time.realtimeSinceStartup >= nextMenuReport)
            {
                lastMenuReported = inMenu;
                nextMenuReport = UnityEngine.Time.realtimeSinceStartup + 2f;
                session.SendGame(ChannelMenuReady, Build(w => w.Write(inMenu)));
            }
        }

        public static void End(string reason)
        {
            if (Current == null) return;
            Log.Info($"SYNC end ({reason}): {Current.Status}");
            Current = null;
        }

        // ---- messages from the session ----

        public static void HandleGameMessage(CoopSession session, byte sender, ushort channel, byte[] payload)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                {
                    switch (channel)
                    {
                        case ChannelCombatStart:
                            if (session.Role == SessionRole.Guest) BeginAsGuest(session, payload);
                            break;
                        case ChannelMenuReady:
                            if (session.Role == SessionRole.Host)
                            {
                                if (r.ReadBoolean()) GuestsInMenu.Add(sender); else GuestsInMenu.Remove(sender);
                            }
                            break;
                        case ChannelStream:
                            if (Current != null && !Current.IsHost && r.ReadUInt32() == Current.CombatId) Current.Enqueue(r);
                            break;
                        case ChannelGuestStatus:
                            if (HostActive && r.ReadUInt32() == Current.CombatId)
                            {
                                Current.guestApplied = r.ReadUInt32();
                                Current.guestDesyncs = r.ReadInt32();
                            }
                            break;
                        case ChannelDesync:
                            if (HostActive && r.ReadUInt32() == Current.CombatId)
                                Log.Error($"SYNC host: guest {sender} reports DESYNC at seq {r.ReadUInt32()}:\n{r.ReadString()}");
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"SYNC: failed to handle channel {channel} message", e);
            }
        }

        // ---- host side ----

        // Prefix of every InvocationMessage.Invoke override: the invocation is executing now.
        public void OnInvoke(InvocationMessage invocation, CombatGameState combat)
        {
            if (IsHost) StreamInvocation(invocation, combat);
            else CheckInvocation(invocation, combat);
        }

        private void StreamInvocation(InvocationMessage invocation, CombatGameState combat)
        {
            if (!streamedInvocations.Add(invocation.InvocationID)) return; // base Invoke reached through an override
            var state = StateText.Render(combat);
            var data = GameSerializer.SerializeMessage(invocation);
            var item = new Item
            {
                Seq = ++seq, Activation = Activation, Kind = ItemKind.Invocation,
                TypeName = invocation.GetType().FullName, Data = data, State = state,
            };
            Send(item);
            if (Main.Settings.LogInvocations)
                Log.Info($"SYNC host: seq {item.Seq} act {item.Activation} {item.TypeName} id={invocation.InvocationID} ({data.Length} bytes)");
        }

        public void OnAITeamMaybeComplete(AITeam team)
        {
            if (!IsHost || !team.IsComplete || !reportedComplete.Add(team.GUID)) return;
            var item = new Item { Seq = ++seq, Activation = Activation, Kind = ItemKind.AIComplete, TeamGuid = team.GUID };
            Send(item);
            if (Main.Settings.LogInvocations)
                Log.Info($"SYNC host: seq {item.Seq} act {item.Activation} AIComplete {team.DisplayName}");
        }

        public void OnActivation()
        {
            Activation++;
            reportedComplete.Clear();
        }

        private void Send(Item item)
        {
            session.SendGame(ChannelStream, Build(w =>
            {
                w.Write(CombatId);
                w.Write(item.Seq);
                w.Write(item.Activation);
                w.Write((byte)item.Kind);
                if (item.Kind == ItemKind.Invocation)
                {
                    w.Write(item.TypeName);
                    w.Write(item.Data.Length);
                    w.Write(item.Data);
                    w.Write(item.State);
                }
                else
                {
                    w.Write(item.TeamGuid);
                }
            }));
        }

        // ---- guest side ----

        private void Enqueue(BinaryReader r)
        {
            var item = new Item { Seq = r.ReadUInt32(), Activation = r.ReadUInt32(), Kind = (ItemKind)r.ReadByte() };
            if (item.Kind == ItemKind.Invocation)
            {
                item.TypeName = r.ReadString();
                item.Data = r.ReadBytes(r.ReadInt32());
                item.State = r.ReadString();
            }
            else
            {
                item.TeamGuid = r.ReadString();
            }
            incoming.Enqueue(item);
        }

        // Called every frame by the controller.
        public void Update()
        {
            var combat = UnityGameInstance.BattleTechGame?.Combat;
            if (combat == null) return;

            if (!begunLogged && combat.TurnDirector != null && combat.TurnDirector.GameHasBegun)
            {
                begunLogged = true;
                Log.Info($"SYNC combat begun: {combat.AllActors.Count} actors; {Status}");
            }

            if (!missionOverLogged && combat.TurnDirector != null && combat.TurnDirector.IsMissionOver)
            {
                missionOverLogged = true;
                Log.Info($"SYNC mission over: {Status}");
            }

            if (IsHost) return;
            while (incoming.Count > 0 && CanApply(combat, incoming.Peek()))
                Apply(combat, incoming.Dequeue());

            if (UnityEngine.Time.realtimeSinceStartup >= nextStatusTime)
            {
                nextStatusTime = UnityEngine.Time.realtimeSinceStartup + 2f;
                session.SendGame(ChannelGuestStatus, Build(w => { w.Write(CombatId); w.Write(seq); w.Write(Desyncs); }));
            }
        }

        private bool CanApply(CombatGameState combat, Item item)
        {
            if (combat.TurnDirector == null || !combat.TurnDirector.GameHasBegun) return false;
            if (combat.StackManager.IsAnyOrderActive) return false;
            var queued = Traverse.Create(combat.InvocationHandler).Field("queuedInvocationMessages").GetValue<System.Collections.IList>();
            if (queued != null && queued.Count > 0) return false;
            if (item.Activation > Activation) return false;
            if (item.Activation < Activation)
            {
                ReportDesync(item.Seq, $"turn structure diverged: item is for activation {item.Activation}, guest is at {Activation}");
                // Apply anyway so the stream keeps moving; the state check will show the damage.
            }
            return true;
        }

        // Test of the desync detector itself (-coop-inject-desync <seq>): corrupt guest state on purpose.
        public static uint InjectDesyncAt;

        private void Apply(CombatGameState combat, Item item)
        {
            seq = item.Seq;
            if (InjectDesyncAt != 0 && item.Seq == InjectDesyncAt)
            {
                var victim = combat.AllActors.OfType<Mech>().Where(m => !m.IsDead).OrderBy(m => m.GUID, StringComparer.Ordinal).FirstOrDefault();
                if (victim != null)
                {
                    Traverse.Create(victim).Property("_heat").SetValue(victim.CurrentHeat + 7);
                    Log.Info($"SYNC guest: TEST - injected +7 heat on {victim.GUID} before seq {item.Seq}");
                }
            }
            if (item.Kind == ItemKind.AIComplete)
            {
                var team = combat.TurnDirector.TurnActors.OfType<AITeam>().FirstOrDefault(t => t.GUID == item.TeamGuid);
                if (team == null) { ReportDesync(item.Seq, "AIComplete for unknown team " + item.TeamGuid); return; }
                if (combat.TurnDirector.ActiveTurnActor != team)
                    Log.Error($"SYNC guest: seq {item.Seq} AIComplete for {team.DisplayName}, but active turn actor is {combat.TurnDirector.ActiveTurnActor}");
                Traverse.Create(team).Field("isComplete").SetValue(true);
                if (Main.Settings.LogInvocations) Log.Info($"SYNC guest: seq {item.Seq} act {item.Activation} AIComplete {team.DisplayName}");
                return;
            }

            MessageCenterMessage message;
            try
            {
                message = GameSerializer.DeserializeMessage(item.Data);
            }
            catch (Exception e)
            {
                ReportDesync(item.Seq, $"could not deserialize {item.TypeName}: {e.Message}");
                return;
            }
            var invocation = message as InvocationMessage;
            if (invocation == null) { ReportDesync(item.Seq, $"stream item is {message?.GetType().FullName ?? "null"}, expected {item.TypeName}"); return; }

            invocation.IsNetRouted = true;
            invocation.WasNetRouted = true;
            applying = item;
            combat.MessageCenter.PublishMessage(invocation);
            if (applying != null)
            {
                // GenericInvoke queued it instead of running it; the check runs when it executes.
                Log.Info($"SYNC guest: seq {item.Seq} queued by the game before executing");
            }
            if (Main.Settings.LogInvocations) Log.Info($"SYNC guest: seq {item.Seq} act {item.Activation} {item.TypeName} id={invocation.InvocationID}");
            if (seq % 10 == 0) Log.Info($"SYNC applied={seq} desyncs={Desyncs}");
        }

        private void CheckInvocation(InvocationMessage invocation, CombatGameState combat)
        {
            if (applying == null) return; // an Invoke not started by us (dropped earlier) or a base call
            var item = applying;
            applying = null;
            var state = StateText.Render(combat);
            if (state != item.State)
                ReportDesync(item.Seq, $"state differs before {item.TypeName} id={invocation.InvocationID}:\n{StateText.Diff(item.State, state)}");
        }

        private void ReportDesync(uint atSeq, string detail)
        {
            Desyncs++;
            Log.Error($"SYNC DESYNC at seq {atSeq}: {detail}");
            session.SendGame(ChannelDesync, Build(w => { w.Write(CombatId); w.Write(atSeq); w.Write(detail.Length > 4000 ? detail.Substring(0, 4000) : detail); }));
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
}
