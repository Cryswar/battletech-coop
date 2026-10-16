using System.Reflection;
using BattleTech;
using Harmony;

namespace BattletechCoop.Patches
{
    // M0: log every invocation that reaches the single execution choke point. Confirmed in game
    // on 2026-10-16: both player and AI actions pass through here (see docs/DESIGN.md).
    [HarmonyPatch]
    public static class InvocationLogPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(InvocationHandler), "GenericInvoke");

        static void Prefix(MessageCenterMessage message)
        {
            // GenericInvoke sees every MessageCenter message; only invocations matter for sync.
            if (!Main.Settings.LogInvocations || !(message is InvocationMessage invocation)) return;
            Log.Info($"GenericInvoke {invocation.GetType().Name} id={invocation.InvocationID} seed={invocation.RandomSeed} netRouted={message.IsNetRouted}/{message.WasNetRouted}");
        }
    }
}
