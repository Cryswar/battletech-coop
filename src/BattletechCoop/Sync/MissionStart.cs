using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BattleTech;
using BattleTech.UI;
using BattletechCoop.Net;
using Harmony;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // The start of a co-op mission (DESIGN.md "Mission start"): both players watch the same
    // opening. The combat save is taken at the host's briefing and both sides launch the mission
    // fresh from it (CombatSync.OnBriefingReady), so the camera intro, the dropship and the
    // opening dialogue play on both. Here:
    // - "Begin mission" is shared: whoever presses it starts the mission for everyone, once every
    //   player's briefing is loaded.
    // - Conversation lines (InterruptDialogSequence) advance together: either player's Continue
    //   goes through the host, which advances its own copy and tells the guests to do the same.
    //   Side chatter (AsyncDialogSequence) runs on its own timers, which match once both began
    //   together.
    public static class MissionStart
    {
        public const ushort ChannelReady = 24;        // guest -> host: my briefing is up
        public const ushort ChannelBeginRequest = 25; // guest -> host: I pressed Begin
        public const ushort ChannelBegin = 26;        // host -> guests: begin now
        public const ushort ChannelDialogRequest = 27; // guest -> host: I pressed Continue/skip
        public const ushort ChannelDialog = 28;       // host -> guests: advance/skip this line

        public static bool Handles(ushort channel) => channel >= ChannelReady && channel <= ChannelDialog;

        private const byte Advance = 1, Skip = 2;

        private class Convo
        {
            public InterruptDialogSequence Sequence;
            public int Line;
            public float ShownAt;
            public bool Requested;
        }

        private class RemoteStep
        {
            public byte Kind;
            public string Guid;
            public int Line;
            public float ReceivedAt;
        }

        private static CombatSync forSync;
        private static Briefing briefing;
        private static bool localReady;
        private static bool begun;
        private static bool beginPending;
        private static bool applyingRemote;
        private static readonly HashSet<byte> readyGuests = new HashSet<byte>();
        private static readonly Dictionary<string, Convo> convos = new Dictionary<string, Convo>();
        private static readonly List<RemoteStep> remoteSteps = new List<RemoteStep>();

        public static string Notice { get; private set; } = "";
        public static float NoticeUntil { get; private set; }

        private static void ShowNotice(string text, float seconds = 6f)
        {
            Notice = text;
            NoticeUntil = Time.realtimeSinceStartup + seconds;
            Log.Info("MISSION notice: " + text);
        }

        private static CoopSession Session => CoopController.Instance?.Session;

        private static CombatSync Sync()
        {
            var sync = CombatSync.Current;
            if (!ReferenceEquals(sync, forSync))
            {
                forSync = sync;
                briefing = null;
                localReady = begun = beginPending = false;
                readyGuests.Clear();
                convos.Clear();
                remoteSteps.Clear();
            }
            return sync;
        }

        // ---- Begin mission ----

        // Briefing.SetStartButtonActive(true): this side's mission is loaded.
        public static void OnBriefingReady(Briefing instance)
        {
            var sync = Sync();
            if (sync == null) return;
            briefing = instance;
            // Host: the guest's save is taken from this briefing first (CombatSync.Update); this side
            // counts as ready once it is sent (Update below).
            if (sync.OnBriefingReady()) return;
            if (!sync.IsHost && sync.ViaSave && !sync.SaveLoadStarted) return;
            MarkReady(sync);
        }

        private static void MarkReady(CombatSync sync)
        {
            localReady = true;
            Log.Info("MISSION briefing ready");
            if (sync.IsHost)
            {
                if (beginPending && AllGuestsReady()) PressBegin();
                return;
            }
            Session?.SendGame(ChannelReady, new byte[0]);
            if (beginPending) BeginHere();
            // Test mode: the guest presses Begin, so the request path runs too.
            else if (sync.Autoplay) PressBegin();
        }

        private static bool AllGuestsReady()
        {
            var session = Session;
            return session != null && session.Players.Where(p => p.Index != 0).All(p => readyGuests.Contains(p.Index));
        }

        private static void PressBegin() => Traverse.Create(briefing).Method("BeginPlaying").GetValue();

        // Briefing.BeginPlaying prefix: false holds the press until everyone can begin together.
        public static bool AllowBegin()
        {
            var sync = Sync();
            if (sync == null) return true;
            if (applyingRemote) { sync.OnBeginPlaying(); return true; }
            if (sync.IsHost)
            {
                if (!localReady) return false;
                if (!AllGuestsReady())
                {
                    beginPending = true;
                    ShowNotice("Waiting for the other players to finish loading the mission...", 30f);
                    return false;
                }
                beginPending = false;
                begun = true;
                Session?.SendGame(ChannelBegin, new byte[0]);
                Log.Info("MISSION begin (host)");
                sync.OnBeginPlaying();
                return true;
            }
            if (begun) return false;
            Session?.SendGame(ChannelBeginRequest, new byte[0]);
            ShowNotice("Starting the mission for everyone...", 10f);
            return false;
        }

        private static void BeginHere()
        {
            beginPending = false;
            begun = true;
            applyingRemote = true;
            try { PressBegin(); }
            finally { applyingRemote = false; }
            Log.Info("MISSION begin (from the host)");
        }

        // ---- Conversations ----

        // InterruptDialogSequence.Play: a line is on screen.
        public static void OnLineShown(InterruptDialogSequence sequence, DialogueContent line)
        {
            if (Sync() == null || line == null) return;
            var guid = Traverse.Create(sequence).Field("dialogueGuid").GetValue<string>() ?? "";
            convos[guid] = new Convo { Sequence = sequence, Line = line.conversationIdx, ShownAt = Time.realtimeSinceStartup };
            Log.Info($"MISSION dialog {guid} line {line.conversationIdx}: {Short(line.words)}");
        }

        public static void OnChatterShown(AsyncDialogSequence sequence, DialogueContent line)
        {
            if (Sync() == null || line == null) return;
            var guid = Traverse.Create(sequence).Field("dialogueGuid").GetValue<string>() ?? "";
            Log.Info($"MISSION chatter {guid} line {line.conversationIdx}: {Short(line.words)}");
        }

        private static string Short(string text)
        {
            text = (text ?? "").Replace('\n', ' ');
            return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
        }

        // InterruptDialogSequence.AfterDialogHide (Continue) or UserRequestHide (skip) prefix.
        // false: the step waits for the host.
        public static bool AllowStep(InterruptDialogSequence sequence, byte kind)
        {
            var sync = Sync();
            if (sync == null || applyingRemote) return true;
            var guid = Traverse.Create(sequence).Field("dialogueGuid").GetValue<string>() ?? "";
            if (!convos.TryGetValue(guid, out var convo)) return true;
            if (sync.IsHost)
            {
                Session?.SendGame(ChannelDialog, Build(w => { w.Write(kind); w.Write(guid); w.Write(convo.Line); }));
                Log.Info($"MISSION host {(kind == Skip ? "skips" : "advances")} {guid} line {convo.Line}");
                return true;
            }
            if (!convo.Requested)
            {
                convo.Requested = true;
                Session?.SendGame(ChannelDialogRequest, Build(w => { w.Write(kind); w.Write(guid); w.Write(convo.Line); }));
                Log.Info($"MISSION guest asks to {(kind == Skip ? "skip" : "advance")} {guid} line {convo.Line}");
            }
            return false;
        }

        private static void Step(Convo convo, byte kind) =>
            Traverse.Create(convo.Sequence).Method(kind == Skip ? "UserRequestHide" : "AfterDialogHide").GetValue();

        // ---- Messages ----

        public static void HandleGameMessage(CoopSession session, byte sender, ushort channel, byte[] payload)
        {
            var sync = Sync();
            if (sync == null) return;
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                switch (channel)
                {
                    case ChannelReady when sync.IsHost:
                        readyGuests.Add(sender);
                        Log.Info($"MISSION player {sender}'s briefing is ready");
                        if (beginPending && localReady && AllGuestsReady()) PressBegin();
                        break;
                    case ChannelBeginRequest when sync.IsHost:
                        Log.Info($"MISSION player {sender} pressed Begin");
                        if (begun) break;
                        if (localReady) PressBegin();
                        else beginPending = true;
                        break;
                    case ChannelBegin when !sync.IsHost:
                        if (begun) break;
                        if (localReady) BeginHere();
                        else beginPending = true;
                        break;
                    case ChannelDialogRequest when sync.IsHost:
                    {
                        var kind = r.ReadByte();
                        var guid = r.ReadString();
                        var line = r.ReadInt32();
                        // Only if the host shows that line still; otherwise someone was faster.
                        if (convos.TryGetValue(guid, out var convo) && convo.Line == line) Step(convo, kind);
                        break;
                    }
                    case ChannelDialog when !sync.IsHost:
                        remoteSteps.Add(new RemoteStep { Kind = r.ReadByte(), Guid = r.ReadString(), Line = r.ReadInt32(), ReceivedAt = Time.realtimeSinceStartup });
                        break;
                }
            }
        }

        // Every frame while connected.
        public static void Update()
        {
            var sync = Sync();
            if (sync == null) return;
            if (sync.IsHost && !localReady && briefing != null && sync.ViaSave && sync.SaveSent) MarkReady(sync);
            // Guest: the host's steps, in order, once this side shows the same line.
            while (!sync.IsHost && remoteSteps.Count > 0)
            {
                var step = remoteSteps[0];
                convos.TryGetValue(step.Guid, out var convo);
                if (convo == null || convo.Line < step.Line)
                {
                    // Not there yet (the conversation starts a moment later here). Give up after a while.
                    if (Time.realtimeSinceStartup - step.ReceivedAt < 60f) break;
                    Log.Info($"MISSION guest: dropped the host's step for {step.Guid} line {step.Line} (never shown here)");
                }
                else if (convo.Line == step.Line)
                {
                    applyingRemote = true;
                    try { Step(convo, step.Kind); }
                    catch (Exception e) { Log.Error("MISSION guest: could not follow the host's dialogue step", e); }
                    finally { applyingRemote = false; }
                    Log.Info($"MISSION guest follows the host: {(step.Kind == Skip ? "skip" : "advance")} {step.Guid} line {step.Line}");
                }
                remoteSteps.RemoveAt(0);
            }
            // Test mode: press Continue after a moment, even lines on the host, odd ones on the guest.
            if (sync.Autoplay)
                foreach (var convo in convos.Values.ToList())
                    if (!convo.Requested && Time.realtimeSinceStartup > convo.ShownAt + 1.5f && sync.IsHost == (convo.Line % 2 == 0))
                    {
                        convo.Requested = sync.IsHost; // the guest's AllowStep marks its own request
                        convo.ShownAt = float.MaxValue;
                        Log.Info($"MISSION test: pressing Continue on line {convo.Line}");
                        Step(convo, Advance);
                    }
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

    // ---- Patches ----

    [HarmonyPatch(typeof(Briefing), "SetStartButtonActive")]
    public static class BriefingReadyPatch
    {
        static void Postfix(Briefing __instance, bool isActive)
        {
            if (isActive) MissionStart.OnBriefingReady(__instance);
        }
    }

    [HarmonyPatch(typeof(Briefing), "BeginPlaying")]
    public static class SharedBeginPatch
    {
        static bool Prefix() => MissionStart.AllowBegin();
    }

    [HarmonyPatch(typeof(InterruptDialogSequence), "Play")]
    public static class DialogLinePatch
    {
        static void Prefix(InterruptDialogSequence __instance, DialogueContent msg) => MissionStart.OnLineShown(__instance, msg);
    }

    [HarmonyPatch(typeof(AsyncDialogSequence), "Play")]
    public static class ChatterLinePatch
    {
        static void Prefix(AsyncDialogSequence __instance, DialogueContent msg) => MissionStart.OnChatterShown(__instance, msg);
    }

    [HarmonyPatch(typeof(InterruptDialogSequence), "AfterDialogHide")]
    public static class DialogContinuePatch
    {
        // AfterDialogHide returns a SimGameConversationManager.ConversationState (0 when continuing).
        static bool Prefix(InterruptDialogSequence __instance, ref SimGameConversationManager.ConversationState __result)
        {
            if (MissionStart.AllowStep(__instance, 1)) return true;
            __result = default(SimGameConversationManager.ConversationState);
            return false;
        }
    }

    [HarmonyPatch(typeof(InterruptDialogSequence), "UserRequestHide")]
    public static class DialogSkipPatch
    {
        static bool Prefix(InterruptDialogSequence __instance) => MissionStart.AllowStep(__instance, 2);
    }

    // Co-op restart saves (CombatSync.RestartSaves) go straight to loading, without the game's
    // "mission save out of date, restart?" popup.
    [HarmonyPatch(typeof(BattleTech.Save.LoadTransitioning), "CheckAndHandleCombatRestart")]
    public static class CombatRestartPopupPatch
    {
        static bool Prefix(BattleTech.Save.LoadTransitioning __instance, BattleTech.Save.GameInstanceSave save)
        {
            if (!CombatSync.RestartSaves.Remove(save)) return true;
            Log.Info("SYNC: loading the co-op save as a fresh start of the mission");
            Traverse.Create(__instance).Method("ProcessLoadedFile", save).GetValue();
            return false;
        }
    }

    // Markers for tests: the intro is over and the opening dialogue is done.
    [HarmonyPatch(typeof(TurnDirector), "ShowMissionStartDialogue")]
    public static class MissionStartDialoguePatch
    {
        static void Prefix() { if (CombatSync.Current != null) Log.Info("MISSION intro done; opening dialogue starts"); }
    }

    [HarmonyPatch(typeof(TurnDirector), "OnDialogueComplete")]
    public static class MissionStartDialogueDonePatch
    {
        static void Prefix(TurnDirector __instance, ref bool __state) =>
            __state = Traverse.Create(__instance).Field("hasMissionStartDialogCompleted").GetValue<bool>();

        static void Postfix(TurnDirector __instance, bool __state)
        {
            if (CombatSync.Current == null || __state) return;
            if (Traverse.Create(__instance).Field("hasMissionStartDialogCompleted").GetValue<bool>())
                Log.Info("MISSION opening dialogue complete");
        }
    }
}

namespace BattletechCoop.Sync
{
    // Diagnostics for the mission start (logged only in a co-op combat).
    [HarmonyPatch]
    public static class MissionStartDiagnostics
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Briefing), "Init");
            yield return AccessTools.Method(typeof(Briefing), "LevelLoaded");
            yield return AccessTools.Method(typeof(Briefing), "InitializeContract");
            yield return AccessTools.Method(typeof(Briefing), "InitializeContractComplete");
            yield return AccessTools.Method(typeof(Briefing), "SetStartButtonActive");
            yield return AccessTools.Method(typeof(GameInstance), "CreateCombatFromSave");
            yield return AccessTools.Method(typeof(BattleTech.Save.LoadTransitioning), "ProcessLoadedFile");
        }

        static void Prefix(System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            if (CombatSync.Current == null) return;
            var args = string.Join(", ", (__args ?? new object[0]).Select(a => a is bool || a is int ? a.ToString() : a?.GetType().Name ?? "null"));
            Log.Info($"MISSION diag: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args})");
        }
    }
}
