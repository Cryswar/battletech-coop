using System;
using System.Linq;
using System.Reflection;
using BattleTech;
using BattletechCoop.Net;
using Harmony;
using UnityEngine;

namespace BattletechCoop.Sync
{
    // Hooks that feed CombatSync. Each one is a no-op unless a co-op combat is running.
    public static class SyncPatches
    {
        // InvocationMessage.Invoke is overridden by every concrete invocation type, so each
        // override gets the same prefix (patched by hand: Harmony attributes can't target "all
        // overrides"). This is the moment an invocation actually executes, after any queueing.
        public static void PatchInvokes(HarmonyInstance harmony)
        {
            var prefix = new HarmonyMethod(typeof(SyncPatches).GetMethod(nameof(InvokePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            var count = 0;
            foreach (var type in typeof(InvocationMessage).Assembly.GetTypes().Where(t => typeof(InvocationMessage).IsAssignableFrom(t)))
            {
                var invoke = type.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(CombatGameState) }, null);
                if (invoke == null || invoke.IsAbstract) continue;
                try
                {
                    harmony.Patch(invoke, prefix, null);
                    count++;
                }
                catch (Exception e)
                {
                    Log.Error($"Could not patch {type.Name}.Invoke", e);
                }
            }
            Log.Info($"Patched Invoke on {count} invocation types");
        }

        // The overrides name their CombatGameState parameter differently, so it isn't bound here;
        // the running combat is the same object.
        private static void InvokePrefix(InvocationMessage __instance)
        {
            try
            {
                var combat = UnityGameInstance.BattleTechGame?.Combat;
                if (combat != null) CombatSync.Current?.OnInvoke(__instance, combat);
            }
            catch (Exception e)
            {
                Log.Error("SYNC: invoke hook failed", e);
            }
        }
    }

    // Host: send the contract to the guests before launching it locally. Both overloads are
    // patched; CombatSync.Current guards against sending twice if one calls the other.
    [HarmonyPatch(typeof(GameInstance), "LaunchContract", new[] { typeof(Contract), typeof(string) })]
    public static class LaunchContractPatch
    {
        static void Prefix(Contract contract) => Run(contract);

        public static void Run(Contract contract)
        {
            var session = CoopController.Instance?.Session;
            if (session == null || session.Role != SessionRole.Host || session.State != SessionState.Active) return;
            if (session.Players.Count() < 2 || CombatSync.Current != null) return;
            if (contract?.ContractTypeValue == null || !contract.ContractTypeValue.IsSkirmish)
            {
                Log.Info("SYNC host: not a skirmish; co-op campaign contracts come in M2");
                return;
            }
            try
            {
                CombatSync.BeginAsHost(session, contract, CoopController.Instance.Autoplay);
            }
            catch (Exception e)
            {
                Log.Error("SYNC host: could not start co-op combat", e);
            }
        }
    }

    [HarmonyPatch(typeof(GameInstance), "LaunchContract", new[] { typeof(Contract) })]
    public static class LaunchContractSinglePatch
    {
        static void Prefix(Contract contract) => LaunchContractPatch.Run(contract);
    }

    [HarmonyPatch(typeof(TurnDirector), "_SendTurnActorActivateMessage")]
    public static class ActivationCountPatch
    {
        static void Prefix() => CombatSync.Current?.OnActivation();
    }

    // Only one machine decides for an AI team (CombatSync.ThinksHere): normally the host. Its moves
    // reach the other machine through the stream (host thinking) or as requests (guest thinking).
    [HarmonyPatch(typeof(AITeam), "think")]
    public static class AIThinkPatch
    {
        static bool Prefix(AITeam __instance)
        {
            var sync = CombatSync.Current;
            if (sync == null) return true;
            return sync.IsHost ? sync.ThinksHere(__instance) : sync.GuestMayThink(__instance);
        }

        static void Postfix(AITeam __instance)
        {
            var sync = CombatSync.Current;
            if (sync == null || !sync.ThinksHere(__instance)) return;
            if (sync.IsHost) sync.OnAITeamMaybeComplete(__instance);
            else sync.AfterGuestThink(__instance);
        }
    }

    [HarmonyPatch(typeof(AITeam), "TurnActorProcessActivation")]
    public static class AIActivationPatch
    {
        static void Postfix(AITeam __instance)
        {
            if (CombatSync.HostActive)
                CombatSync.Current.OnAITeamMaybeComplete(__instance);
            else if (CombatSync.GuestActive)
                Traverse.Create(__instance).Field("isComplete").SetValue(false); // completion comes from the host
        }
    }

    // Test mode: the AI plays the player's lance too (what HBS's DebugBridge.PlayerOneIsAIControlled does).
    [HarmonyPatch(typeof(EncounterLayerData), "CreatePlayerOneTeam")]
    public static class AutoplayTeamPatch
    {
        static void Postfix(EncounterLayerData __instance, ref Team __result)
        {
            if (CombatSync.Current == null || !CombatSync.Current.Autoplay) return;
            var combat = Traverse.Create(__instance).Property("Combat").GetValue<CombatGameState>();
            var color = (Color)AccessTools.Method(typeof(UnitSpawnPointGameLogic), "GetPlayerHostilityColor").Invoke(null, new object[] { CombatSync.Player1TeamGuid });
            var team = new AITeam("Player 1", color, CombatSync.Player1TeamGuid, true, combat, true, false);
            team.FactionValue = __result.FactionValue;
            __result = team;
            Log.Info("SYNC: autoplay - Player 1 is AI-controlled");
        }
    }

    // Test mode: press "Begin mission" as soon as the briefing enables it. (In normal play each
    // player presses it; the guest's stream waits until its own combat has begun.)
    [HarmonyPatch(typeof(BattleTech.UI.Briefing), "SetStartButtonActive")]
    public static class AutoplayBeginPatch
    {
        static void Postfix(BattleTech.UI.Briefing __instance, bool isActive)
        {
            if (!isActive || CombatSync.Current == null || !CombatSync.Current.Autoplay) return;
            Log.Info("SYNC: autoplay - pressing Begin mission");
            Traverse.Create(__instance).Method("BeginPlaying").GetValue();
        }
    }

    [HarmonyPatch(typeof(CombatGameState), "OnCombatGameDestroyed")]
    public static class CombatEndPatch
    {
        static void Postfix() => CombatSync.End("combat closed");
    }
}
