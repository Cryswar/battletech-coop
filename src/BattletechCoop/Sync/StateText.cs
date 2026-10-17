using System;
using System.Globalization;
using System.Linq;
using System.Text;
using BattleTech;

namespace BattletechCoop.Sync
{
    // Canonical text of the combat state that matters for lockstep. Both machines render it right
    // before each invocation runs; any difference is a desync. One line per actor so a diff points
    // straight at the unit and field that diverged. Positions are in centimetres and angles in
    // tenths of a degree, so float noise from rendering doesn't show up.
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

            foreach (var actor in combat.AllActors.OrderBy(a => a.GUID, StringComparer.Ordinal))
            {
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

        // First few lines that differ, for the desync log.
        public static string Diff(string expected, string actual, int maxLines = 6)
        {
            var a = expected.Split('\n');
            var b = actual.Split('\n');
            var sb = new StringBuilder();
            var shown = 0;
            for (var i = 0; i < Math.Max(a.Length, b.Length) && shown < maxLines; i++)
            {
                var x = i < a.Length ? a[i] : "<missing>";
                var y = i < b.Length ? b[i] : "<missing>";
                if (x == y) continue;
                sb.Append("  host : ").Append(x).Append('\n').Append("  guest: ").Append(y).Append('\n');
                shown++;
            }
            return sb.ToString();
        }
    }
}
