using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimatomicsFuelPoolBillCounter
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("s235jr.rimatomics.fuelpoolbillcounter");
                harmony.PatchAll();
                Log.Message("[Rimatomics Fuel Pool Bill Counter] Loaded. Fuel-rod target bills will include rods stored in Rimatomics storage pools.");
            }
            catch (Exception ex)
            {
                Log.Error("[Rimatomics Fuel Pool Bill Counter] Failed to apply Harmony patches: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(RecipeWorkerCounter), "CountProducts")]
    public static class RecipeWorkerCounter_CountProducts_Patch
    {
        private const int HeldCountCacheTicks = 60;

        private sealed class HeldCountCacheEntry
        {
            public int tick;
            public int count;
        }

        private static readonly Dictionary<Map, Dictionary<ThingDef, HeldCountCacheEntry>> HeldCountCache =
            new Dictionary<Map, Dictionary<ThingDef, HeldCountCacheEntry>>();

        private static readonly Dictionary<Map, Dictionary<ThingDef, int>> LastLoggedCorrectedCount =
            new Dictionary<Map, Dictionary<ThingDef, int>>();

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(RecipeWorkerCounter __instance, Bill_Production bill, ref int __result)
        {
            try
            {
                if (bill == null || bill.recipe == null || bill.recipe.products == null || bill.recipe.products.Count != 1)
                    return;

                ThingDef product = bill.recipe.products[0].thingDef;
                if (!IsSupportedFuelRecipe(bill.recipe, product))
                    return;

                // Respect an explicitly restricted stockpile-zone bill. The Rimatomics
                // storage pool cannot overlap normal zones, so changing such a bill's
                // count would violate the player's restriction.
                // (RimWorld 1.5+ replaced the old includeFromZone field.)
                if (bill.GetIncludeSlotGroup() != null)
                    return;

                Map map = bill.Map;
                if (map == null || product == null)
                    return;

                PruneClosedMaps();

                int vanillaCount = __result;
                int spawnedCount = CountSpawnedInStorage(map, product);
                int unspawnedPoolCount = GetCachedUnspawnedPoolCount(map, product);

                // Never reduce or replace a count another mod/vanilla already considers
                // valid. We only raise the result when the physical fuel-rod count proves
                // that the normal counter missed rods.
                int physicalCount = spawnedCount + unspawnedPoolCount;
                int correctedCount = Math.Max(vanillaCount, physicalCount);

                if (correctedCount != vanillaCount)
                {
                    __result = correctedCount;
                    LogCorrectionIfChanged(map, product, bill.recipe, vanillaCount, correctedCount, spawnedCount, unspawnedPoolCount);
                }
            }
            catch (Exception ex)
            {
                // A bill counter is called frequently. Failing open is safer than
                // breaking work assignment or the Bills tab.
                Log.ErrorOnce(
                    "[Rimatomics Fuel Pool Bill Counter] Error while correcting a fuel-rod bill count: " + ex,
                    178436921
                );
            }
        }

        private static bool IsSupportedFuelRecipe(RecipeDef recipe, ThingDef product)
        {
            if (recipe == null || product == null || recipe.defName == null || product.defName == null)
                return false;

            if (recipe.defName == "MakeFuelRods" && product.defName == "FuelRods")
                return true;

            if (recipe.defName == "MakeMoxFuelRods" && product.defName == "FuelRodsMOX")
                return true;

            return false;
        }

        // Only rods sitting in valid storage, like vanilla's resource count.
        // Counting every spawned rod (lying on the floor, just dropped by a
        // hauler) could make the bill think it's done early. If vanilla does
        // count such rods, Math.Max above keeps its higher number anyway.
        private static int CountSpawnedInStorage(Map map, ThingDef product)
        {
            List<Thing> things = map.listerThings.ThingsOfDef(product);
            if (things == null || things.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed || !thing.Spawned)
                    continue;

                if (!thing.IsInValidStorage())
                    continue;

                count += Math.Max(1, thing.stackCount);
            }

            return count;
        }

        // The caches are keyed by Map. Without pruning, every save loaded in
        // a session would keep its old maps (and everything on them) alive
        // until the game closes.
        // Only a handful of maps are ever open, so checking on every call is
        // cheap and allocates nothing unless a stale map is found.
        private static void PruneClosedMaps()
        {
            List<Map> open = Find.Maps;
            PruneClosedMaps(HeldCountCache, open);
            PruneClosedMaps(LastLoggedCorrectedCount, open);
        }

        private static void PruneClosedMaps<T>(Dictionary<Map, T> cache, List<Map> open)
        {
            if (cache.Count == 0)
                return;

            List<Map> stale = null;
            foreach (Map m in cache.Keys)
            {
                if (open != null && open.Contains(m))
                    continue;

                if (stale == null)
                    stale = new List<Map>();
                stale.Add(m);
            }

            if (stale == null)
                return;

            for (int i = 0; i < stale.Count; i++)
                cache.Remove(stale[i]);
        }

        private static int GetCachedUnspawnedPoolCount(Map map, ThingDef product)
        {
            int currentTick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;

            Dictionary<ThingDef, HeldCountCacheEntry> perDef;
            if (!HeldCountCache.TryGetValue(map, out perDef))
            {
                perDef = new Dictionary<ThingDef, HeldCountCacheEntry>();
                HeldCountCache[map] = perDef;
            }

            HeldCountCacheEntry entry;
            if (perDef.TryGetValue(product, out entry))
            {
                int age = currentTick - entry.tick;
                if (age >= 0 && age <= HeldCountCacheTicks)
                    return entry.count;
            }

            int count = CountUnspawnedFuelInsidePools(map, product);

            if (entry == null)
            {
                entry = new HeldCountCacheEntry();
                perDef[product] = entry;
            }

            entry.tick = currentTick;
            entry.count = count;
            return count;
        }

        private static int CountUnspawnedFuelInsidePools(Map map, ThingDef product)
        {
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            if (buildings == null || buildings.Count == 0)
                return 0;

            HashSet<object> visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
            HashSet<Thing> countedFuel = new HashSet<Thing>();
            int total = 0;

            for (int i = 0; i < buildings.Count; i++)
            {
                Building building = buildings[i];
                if (!IsRimatomicsStoragePool(building))
                    continue;

                // Rimatomics' Building_storagePool is an IThingHolder that keeps
                // its rods in innerContainer (checked against Rimatomics 1.6),
                // so read it through RimWorld's public holder API.
                IThingHolder holder = building as IThingHolder;
                ThingOwner held = holder == null ? null : holder.GetDirectlyHeldThings();

                if (held != null)
                {
                    for (int j = 0; j < held.Count; j++)
                    {
                        Thing thing = held[j];
                        if (thing != null &&
                            thing.def == product &&
                            !thing.Spawned &&
                            !thing.Destroyed &&
                            countedFuel.Add(thing))
                        {
                            total += Math.Max(1, thing.stackCount);
                        }
                    }

                    continue;
                }

                // Fallback for a future Rimatomics that stops exposing a holder.
                total += ScanPoolObject(building, product, visitedObjects, countedFuel, 0);
            }

            return total;
        }

        private static bool IsRimatomicsStoragePool(Building building)
        {
            if (building == null || building.Destroyed)
                return false;

            Type type = building.GetType();
            if (type != null && type.FullName == "Rimatomics.Building_storagePool")
                return true;

            return building.def != null && building.def.defName == "storagePool";
        }

        private static int ScanPoolObject(
            object obj,
            ThingDef product,
            HashSet<object> visitedObjects,
            HashSet<Thing> countedFuel,
            int depth)
        {
            if (obj == null || depth > 6)
                return 0;

            Type objectType = obj.GetType();

            if (objectType.IsPrimitive || objectType.IsEnum || objectType.IsValueType || obj is string)
                return 0;

            if (!visitedObjects.Add(obj))
                return 0;

            Thing asThing = obj as Thing;
            if (asThing != null)
            {
                if (asThing.def == product)
                {
                    // Spawned rods are already counted by CountSpawned(). Only add
                    // truly held/unspawned rods here, and only once.
                    if (!asThing.Spawned && !asThing.Destroyed && countedFuel.Add(asThing))
                        return Math.Max(1, asThing.stackCount);

                    return 0;
                }

                // Do not walk through arbitrary Things: that could lead into pawns,
                // maps, reactors, or other holders. The storage pool itself is the
                // only non-fuel Thing whose private fields we intentionally inspect.
                Building possiblePool = asThing as Building;
                if (!IsRimatomicsStoragePool(possiblePool))
                    return 0;
            }

            int count = 0;

            IDictionary dictionary = obj as IDictionary;
            if (dictionary != null)
            {
                foreach (object value in dictionary.Values)
                    count += ScanPoolObject(value, product, visitedObjects, countedFuel, depth + 1);

                return count;
            }

            IEnumerable enumerable = obj as IEnumerable;
            if (enumerable != null && !(obj is Thing))
            {
                try
                {
                    foreach (object item in enumerable)
                        count += ScanPoolObject(item, product, visitedObjects, countedFuel, depth + 1);
                }
                catch
                {
                    // Some game collections can invalidate during inspection. The
                    // spawned-item path still works, so simply skip this container.
                }

                return count;
            }

            string ns = objectType.Namespace ?? "";
            bool isPool = IsRimatomicsStoragePool(asThing as Building);

            // Only recurse through Rimatomics' own helper objects (plus the pool
            // itself). This prevents reflection from wandering into the Map, game
            // managers, factions, pawns, etc.
            if (!isPool && !ns.StartsWith("Rimatomics", StringComparison.Ordinal))
                return 0;

            Type current = objectType;
            while (current != null)
            {
                string currentNs = current.Namespace ?? "";
                if (current != objectType && !currentNs.StartsWith("Rimatomics", StringComparison.Ordinal))
                    break;

                FieldInfo[] fields;
                try
                {
                    fields = current.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly
                    );
                }
                catch
                {
                    break;
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];

                    if (field.IsStatic)
                        continue;

                    object value;
                    try
                    {
                        value = field.GetValue(obj);
                    }
                    catch
                    {
                        continue;
                    }

                    count += ScanPoolObject(value, product, visitedObjects, countedFuel, depth + 1);
                }

                current = current.BaseType;
            }

            return count;
        }

        private static void LogCorrectionIfChanged(
            Map map,
            ThingDef product,
            RecipeDef recipe,
            int vanillaCount,
            int correctedCount,
            int spawnedCount,
            int unspawnedPoolCount)
        {
            Dictionary<ThingDef, int> perDef;
            if (!LastLoggedCorrectedCount.TryGetValue(map, out perDef))
            {
                perDef = new Dictionary<ThingDef, int>();
                LastLoggedCorrectedCount[map] = perDef;
            }

            int previous;
            if (perDef.TryGetValue(product, out previous) && previous == correctedCount)
                return;

            perDef[product] = correctedCount;

            Log.Message(
                "[Rimatomics Fuel Pool Bill Counter] " +
                recipe.defName +
                ": corrected product count " +
                vanillaCount +
                " -> " +
                correctedCount +
                " (spawned=" +
                spawnedCount +
                ", pool-held=" +
                unspawnedPoolCount +
                ")."
            );
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            private ReferenceEqualityComparer()
            {
            }

            public new bool Equals(object x, object y)
            {
                return Object.ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
