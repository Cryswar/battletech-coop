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

        // -coop-deep-state: also list every statistic (unit, pilot, components) per actor, so a
        // desync diff names the exact stat. Normally only a digest of them is compared.
        public static bool Deep;

        public static string Render(CombatGameState combat)
        {
            // Stat values are formatted with the current culture; players' PCs may differ.
            var thread = System.Threading.Thread.CurrentThread;
            var previous = thread.CurrentCulture;
            thread.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                return RenderInvariant(combat);
            }
            finally
            {
                thread.CurrentCulture = previous;
            }
        }

        private static string RenderInvariant(CombatGameState combat)
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
                // Status effects, modifiers, pilot state and weapon/ammo state all live in stat
                // collections; a desync after loading a save showed in attack results that the
                // fields above didn't explain (2026-10-17).
                var stats = ActorStats(actor);
                sb.Append(" stats=").Append(Fnv(string.Join("\n", stats.Select(x => x.Key + ":" + x.Value).ToArray())).ToString("x8"));
                sb.Append('\n');
                if (Deep)
                    foreach (var part in stats)
                        sb.Append(actor.GUID).Append(' ').Append(part.Key).Append(' ').Append(part.Value).Append('\n');
            }
            return sb.ToString();
        }

        // (label, "name=value;..." sorted by name) for the actor, its pilot and each component.
        private static List<KeyValuePair<string, string>> ActorStats(AbstractActor actor)
        {
            var result = new List<KeyValuePair<string, string>>();
            result.Add(new KeyValuePair<string, string>("S", StatsText(actor.StatCollection)));
            var pilot = actor.GetPilot();
            if (pilot != null) result.Add(new KeyValuePair<string, string>("P", StatsText(pilot.StatCollection)));
            // Components come back in a different order after a save is loaded, and ammo boxes have
            // no uid: label them by definition and mount location and sort, so order doesn't matter.
            var components = Traverse.Create(actor).Field("allComponents").GetValue() as IEnumerable;
            var parts = new List<KeyValuePair<string, string>>();
            if (components != null)
                foreach (var item in components)
                {
                    var component = item as MechComponent;
                    if (component != null)
                        parts.Add(new KeyValuePair<string, string>("C:" + component.defId + "@" + component.Location, StatsText(component.StatCollection)));
                }
            result.AddRange(parts.OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal));
            return result;
        }

        private static string StatsText(StatCollection collection)
        {
            if (collection == null) return "";
            var sb = new StringBuilder();
            foreach (var name in collection.Items.OrderBy(n => n, StringComparer.Ordinal))
            {
                var stat = collection.GetStatistic(name);
                sb.Append(name).Append('=').Append(stat?.CurrentValue?.ToString() ?? "null").Append(';');
            }
            return sb.ToString();
        }

        private static uint Fnv(string text)
        {
            var hash = 2166136261u;
            foreach (var ch in text) { hash ^= ch; hash *= 16777619u; }
            return hash;
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

        // Lines that differ, ignoring units busy on either side. Lines are matched by unit and section
        // (header, unit line, "S"/"P"/"C:<def>@<location>" stat lines), not by position: a unit
        // that is busy on one side has a single line there and many on the other.
        public static List<KeyValuePair<string, string>> Differences(string host, string guest)
        {
            var busy = new HashSet<string>();
            var a = Index(host, busy);
            var b = Index(guest, busy);
            var result = new List<KeyValuePair<string, string>>();
            foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                if (busy.Contains(key.Split(' ')[0])) continue;
                List<string> x, y;
                var hostText = a.TryGetValue(key, out x) ? string.Join(" | ", x.ToArray()) : "<missing>";
                var guestText = b.TryGetValue(key, out y) ? string.Join(" | ", y.ToArray()) : "<missing>";
                if (hostText != guestText) result.Add(new KeyValuePair<string, string>(hostText, guestText));
            }
            return result;
        }

        private static Dictionary<string, List<string>> Index(string text, HashSet<string> busy)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                var tokens = line.Split(' ');
                if (IsBusy(line)) { busy.Add(tokens[0]); continue; }
                var key = tokens[0];
                if (tokens.Length > 1 && (tokens[1] == "S" || tokens[1] == "P" || tokens[1].StartsWith("C:", StringComparison.Ordinal)))
                    key += " " + tokens[1];
                List<string> lines;
                if (!index.TryGetValue(key, out lines)) index[key] = lines = new List<string>();
                lines.Add(line);
            }
            foreach (var lines in index.Values) lines.Sort(StringComparer.Ordinal);
            return index;
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
