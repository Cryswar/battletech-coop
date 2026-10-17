using System;
using System.Linq;
using BattleTech;
using Harmony;

namespace BattletechCoop.Sync
{
    // Extra logging for co-op combats, aimed at the first human-play desync (2026-10-17, see
    // DESIGN.md): a replayed move left no trace on the guest. Cancelled order sequences are the
    // leading suspect (StackManager.StopCancelableSequences cancels them silently).
    public static class SyncDiagnostics
    {
        // The unit an invocation acts for, for log lines. Invocation types name it differently.
        public static string ActorOf(InvocationMessage invocation)
        {
            foreach (var name in new[] { "ActorGUID", "SourceGUID", "MechGUID", "AttackerGUID", "TargetGUID" })
            {
                var property = AccessTools.Property(invocation.GetType(), name);
                var guid = property?.GetValue(invocation, null) as string;
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
            return "?";
        }
    }

    [HarmonyPatch(typeof(OrderSequence), "OnCanceled")]
    public static class OrderCanceledPatch
    {
        static void Prefix(OrderSequence __instance)
        {
            if (CombatSync.Current == null) return;
            var actor = Traverse.Create(__instance).Property("OwningActor").GetValue<AbstractActor>();
            var stack = Environment.StackTrace.Split('\n').Skip(3).Take(8).Select(l => l.Trim());
            Log.Info($"SYNC diag: {__instance.GetType().Name} for {actor?.GUID ?? "?"} CANCELED at seq {CombatSync.Current.LastSeq}\n    " + string.Join("\n    ", stack));
        }
    }
}
