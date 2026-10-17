using System;
using System.Collections;
using System.Collections.Generic;
using BattleTech;
using BattleTech.Data;
using Harmony;

namespace BattletechCoop.Sync
{
    // Guest: make the host's lance usable before launching its contract.
    //
    // Lance units built from MechDef/PilotDef objects (what the skirmish screen does) arrive
    // deserialized without their chassis, component and ability links, and spawning them crashes
    // (MechDef.GetChassisLocationDef, then AmmunitionBox..ctor; seen 2026-10-16). Each unit also
    // carries its defs as JSON (SpawnableUnit.StoreJSON). This rebuilds every such unit from that
    // JSON against this machine's DataManager and loads its dependencies, doing what
    // Contract.RequestDependencies does for networked lances, then marks it initialized so the
    // normal single-player load uses it as-is. (Forcing the game's own multiplayer branch instead
    // stalled the load.)
    public static class LanceLoader
    {
        private const uint Weight = 1000;

        public static void PrepareGuestLance(Contract contract, Action done)
        {
            var dataManager = UnityGameInstance.BattleTechGame.DataManager;
            var units = JsonUnits(contract);
            if (units.Count == 0)
            {
                Log.Info("SYNC guest: lance is by ID only; the game loads it normally");
                done();
                return;
            }

            var request = new DataManager.InjectedDependencyLoadRequest(dataManager);
            foreach (var unit in units)
            {
                unit.FromJSON();
                Link(unit, dataManager);
                GatherIfMissing(unit, dataManager, request);
            }

            Action finish = () =>
            {
                var initialized = AccessTools.Field(typeof(SpawnableUnit), "<Initialized>k__BackingField");
                foreach (var unit in units)
                {
                    // Refresh again now that dependencies are loaded, including the chassis itself:
                    // MechDef.Refresh only re-points Chassis; ChassisDef.Refresh links movement,
                    // pathing and hardpoint data (missing MovementCaps crashed Mech.InitStats).
                    Link(unit, dataManager);
                    RefreshChassis(unit.Unit, dataManager);
                    RefreshChassis(unit.VUnit, dataManager);
                    Link(unit, dataManager);
                    initialized.SetValue(unit, true);
                }
                Log.Info($"SYNC guest: {units.Count} lance units rebuilt from the host's data");
                done();
            };

            var pending = request.DependencyCount();
            if (pending == 0)
            {
                finish();
                return;
            }
            Log.Info($"SYNC guest: loading {pending} dependencies for the host's lance");
            request.RegisterLoadCompleteCallback(finish);
            dataManager.InjectDependencyLoader(request, Weight);
        }

        private static List<SpawnableUnit> JsonUnits(Contract contract)
        {
            var result = new List<SpawnableUnit>();
            var lances = Traverse.Create(contract.Lances).Field("lanceConfigurations").GetValue<IDictionary>();
            if (lances == null) return result;
            var unitJson = AccessTools.Field(typeof(SpawnableUnit), "UnitJson");
            foreach (IEnumerable list in lances.Values)
                foreach (SpawnableUnit unit in list)
                    if (unit != null && !string.IsNullOrEmpty(unitJson.GetValue(unit) as string))
                        result.Add(unit);
            return result;
        }

        private static void Link(SpawnableUnit unit, DataManager dataManager)
        {
            if (unit.Unit != null) { unit.Unit.DataManager = dataManager; unit.Unit.Refresh(); }
            if (unit.VUnit != null) { unit.VUnit.DataManager = dataManager; unit.VUnit.Refresh(); }
            if (unit.TUnit != null) { unit.TUnit.DataManager = dataManager; unit.TUnit.Refresh(); }
            if (unit.Pilot != null) { unit.Pilot.DataManager = dataManager; unit.Pilot.ForceRefreshAbilityDefs(); }
        }

        // def.Chassis (ChassisDef / VehicleChassisDef): give it the DataManager and Refresh it.
        private static void RefreshChassis(object def, DataManager dataManager)
        {
            if (def == null) return;
            var chassis = Traverse.Create(def).Property("Chassis").GetValue();
            if (chassis == null) return;
            var field = Traverse.Create(chassis).Field("dataManager");
            if (field.FieldExists() && field.GetValue() == null) field.SetValue(dataManager);
            Traverse.Create(chassis).Method("Refresh").GetValue();
        }

        private static void GatherIfMissing(SpawnableUnit unit, DataManager dataManager, DataManager.InjectedDependencyLoadRequest request)
        {
            if (unit.Unit != null && !unit.Unit.DependenciesLoaded(Weight)) unit.Unit.GatherDependencies(dataManager, request, Weight);
            if (unit.VUnit != null && !unit.VUnit.DependenciesLoaded(Weight)) unit.VUnit.GatherDependencies(dataManager, request, Weight);
            if (unit.TUnit != null && !unit.TUnit.DependenciesLoaded(Weight)) unit.TUnit.GatherDependencies(dataManager, request, Weight);
            if (unit.Pilot != null && !unit.Pilot.DependenciesLoaded(Weight)) unit.Pilot.GatherDependencies(dataManager, request, Weight);
        }
    }
}
