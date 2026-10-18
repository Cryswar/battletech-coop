using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BattleTech;
using Harmony;

namespace BattletechCoop.Sync
{
    // Canonical text of the combat state that matters for lockstep. Both machines render it right
    // before each invocation runs; any difference is a desync. One line per actor so a diff points
    // straight at the unit and field that diverged. Positions are in centimetres and angles in
    // tenths of a degree, so float noise from rendering doesn't show up.
    //
    // A unit that is still carrying out an order (e.g. walking in the free-move phase, where the
    // next order may start before the last move finishes) renders as "<GUID> busy" and is skipped
    // when either side has it busy: its sequence runs on each machine's own clock, so mid-way
    // snapshots differ without the games diverging (the false desyncs of 2026-10-17). It is
    // compared at the next check where both sides have it at rest.
    public static class StateText
    {
        private static readonly ArmorLocation[] ArmorLocations =
        {
            ArmorLocation.Head, ArmorLocation.CenterTorso, ArmorLocation.CenterTorsoRear,
            ArmorLocation.LeftTorso, ArmorLocation.LeftTorsoRear, ArmorLocation.RightTorso, ArmorLocation.RightTorsoRear,
            ArmorLocation.LeftArm, ArmorLocation.RightArm, ArmorLocation.LeftLeg, ArmorLocation.RightLeg,
        };

        private static readonly ChassisLocations[] StructureLocations =
        {
            ChassisLocations.Head, ChassisLocations.CenterTorso, ChassisLocations.LeftTorso, ChassisLocations.RightTorso,
            ChassisLocations.LeftArm, ChassisLocations.RightArm, ChassisLocations.LeftLeg, ChassisLocations.RightLeg,
        };

        public static string Render(CombatGameState combat)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(2048);
            var td = combat.TurnDirector;
            sb.Append("round=").Append(td.CurrentRound).Append(" phase=").Append(td.CurrentPhase)
              .Append(" rng=").Append(combat.NetworkRandom.seed).Append('/').Append(combat.NetworkRandom.randomCalls).Append('\n');

            var busy = BusyActors(combat);
            foreach (var actor in combat.AllActors.OrderBy(a => a.GUID, StringComparer.Ordinal))
            {
                if (busy.Contains(actor.GUID))
                {
                    sb.Append(actor.GUID).Append(" busy\n");
                    continue;
                }
                var p = actor.CurrentPosition;
                sb.Append(actor.GUID)
                  .Append(" pos=").Append(Cm(p.x)).Append(',').Append(Cm(p.y)).Append(',').Append(Cm(p.z))
                  .Append(" yaw=").Append(Math.Round(actor.CurrentRotation.eulerAngles.y * 10).ToString(c))
                  .Append(" dead=").Append(actor.IsDead ? 1 : 0)
                  .Append(" act=").Append(actor.HasActivatedThisRound ? 1 : 0)
                  .Append(" evp=").Append(actor.EvasivePipsCurrent)
                  .Append(" ar=").Append(Math.Round(actor.SummaryArmorCurrent).ToString(c))
                  .Append(" st=").Append(Math.Round(actor.SummaryStructureCurrent).ToString(c));

                var mech = actor as Mech;
                if (mech != null)
                {
                    sb.Append(" heat=").Append(mech.CurrentHeat)
                      .Append(" stab=").Append(Math.Round(mech.CurrentStability).ToString(c))
                      .Append(" a=");
                    foreach (var loc in ArmorLocations)
                        sb.Append(Math.Round(mech.GetCurrentArmor(loc)).ToString(c)).Append(':');
                    sb.Append(" s=");
                    foreach (var loc in StructureLocations)
                        sb.Append(Math.Round(mech.GetCurrentStructure(loc)).ToString(c)).Append(':');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string Cm(float metres) =>
            Math.Round(metres * 100f).ToString(CultureInfo.InvariantCulture);

        // Units with an order sequence still running (normal or parallel stack, including children
        // of multi-sequences).
        private static HashSet<string> BusyActors(CombatGameState combat)
        {
            var result = new HashSet<string>();
            var stacks = combat.StackManager;
            Collect(Traverse.Create(stacks).Property("SequenceStack").GetValue() as IEnumerable, result, 0);
            Collect(Traverse.Create(stacks).Property("ParallelStack").GetValue() as IEnumerable, result, 0);
            return result;
        }

        private static void Collect(IEnumerable sequences, HashSet<string> result, int depth)
        {
            if (sequences == null || depth > 8) return;
            foreach (var sequence in sequences)
            {
                var order = sequence as OrderSequence;
                if (order != null)
                {
                    var actor = Traverse.Create(order).Property("OwningActor").GetValue<AbstractActor>();
                    if (actor != null) result.Add(actor.GUID);
                }
                if (sequence is MultiSequence)
                    Collect(Traverse.Create(sequence).Property("childSequences").GetValue() as IEnumerable, result, depth + 1);
            }
        }

        private static bool IsBusy(string line) => line.EndsWith(" busy", StringComparison.Ordinal);

        // Lines that differ, ignoring units busy on either side.
        public static List<KeyValuePair<string, string>> Differences(string host, string guest)
        {
            var a = host.Split('\n');
            var b = guest.Split('\n');
            var result = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length ? a[i] : "<missing>";
                var y = i < b.Length ? b[i] : "<missing>";
                if (x == y || IsBusy(x) || IsBusy(y)) continue;
                result.Add(new KeyValuePair<string, string>(x, y));
            }
            return result;
        }

        public static string Diff(List<KeyValuePair<string, string>> differences, int maxLines = 6)
        {
            var sb = new StringBuilder();
            foreach (var d in differences.Take(maxLines))
                sb.Append("  host : ").Append(d.Key).Append('\n').Append("  guest: ").Append(d.Value).Append('\n');
            return sb.ToString();
        }
    }
}
