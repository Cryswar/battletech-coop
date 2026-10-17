using System.Reflection;
using BattleTech;
using BattletechCoop.Sync;
using Harmony;

namespace BattletechCoop.Patches
{
    // GenericInvoke is the single entry point for every invocation (player HUD, AI, and replayed
    // stream items); it also sees every other MessageCenter message. Confirmed in game on
    // 2026-10-16: player and AI actions both pass through here (see docs/DESIGN.md).
    [HarmonyPatch]
    public static class InvocationLogPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(InvocationHandler), "GenericInvoke");

        static bool Prefix(MessageCenterMessage message)
        {
            var invocation = message as InvocationMessage;
            if (invocation == null) return true;

            // A guest only executes invocations replayed from the host's stream (WasNetRouted).
            // Anything it creates itself (HUD orders, auto stand-up) runs on the host instead.
            if (CombatSync.GuestActive && !message.WasNetRouted)
            {
                Log.Info($"SYNC guest: dropped local {invocation.GetType().Name}; the host's stream drives combat");
                return false;
            }

            if (Main.Settings.LogInvocations && CombatSync.Current == null)
                Log.Info($"GenericInvoke {invocation.GetType().Name} id={invocation.InvocationID} seed={invocation.RandomSeed} netRouted={message.IsNetRouted}/{message.WasNetRouted}");
            return true;
        }
    }
}
