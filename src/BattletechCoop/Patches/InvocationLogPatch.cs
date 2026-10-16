using System.Reflection;
using BattleTech;
using Harmony;

namespace BattletechCoop.Patches
{
    // M0: log every invocation that reaches the single execution choke point, so we can
    // confirm in a live game that all player and AI actions pass through here.
    [HarmonyPatch]
    public static class InvocationLogPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(InvocationHandler), "GenericInvoke");

        static void Prefix(MessageCenterMessage message)
        {
            if (!Main.Settings.LogInvocations) return;
            var invocation = message as InvocationMessage;
            Log.Info(invocation == null
                ? $"GenericInvoke {message?.GetType().Name}"
                : $"GenericInvoke {invocation.GetType().Name} id={invocation.InvocationID} seed={invocation.RandomSeed} netRouted={message.IsNetRouted}/{message.WasNetRouted}");
        }
    }
}
