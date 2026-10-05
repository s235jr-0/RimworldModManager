# Rimatomics Fuel Pool Bill Counter — status

**Current version:** v1.1.0
**Package ID:** `s235jr.rimatomics.fuelpoolbillcounter` · **Author:** s235JR

> **Package ID changed** (was `anon.…`). RimWorld sees it as a new mod: re-enable it in
> the mod list and set its settings again. Not yet built or tested in-game since the change.

## What it does
Makes uranium/MOX fuel rods stored in Rimatomics storage pools count toward
"Do until you have X" production bills.

- Patches `RecipeWorkerCounter.CountProducts`.
- Only touches `MakeFuelRods` → `FuelRods` and `MakeMoxFuelRods` → `FuelRodsMOX`.
- Count = `max(vanilla count, rods in valid storage + rods held inside pools)`. It never
  lowers another mod's count and doesn't double-count.
- Pool rods are read via `IThingHolder.GetDirectlyHeldThings()`. A reflection scan
  restricted to Rimatomics objects is kept as a fallback.
- Bills restricted to a specific stockpile (`GetIncludeSlotGroup() != null`) are left alone.
- Pool scanning is cached for 60 game ticks; caches drop maps that are no longer open.

## State
- **Compiles** against RimWorld 1.6 + Harmony 2.x (verified 2026-09-23).
- Names checked against the installed Rimatomics 1.6 files: recipes, products,
  `storagePool` → `Rimatomics.Building_storagePool` (an `IThingHolder` with `innerContainer`).
- **Not yet tested in-game.**

## Changes in v1.1.0 (from the 2026-09-23 code review)
- Build fix: `bill.includeFromZone` (gone since 1.5) → `bill.GetIncludeSlotGroup()`.
- Memory leak fixed: static caches keyed by `Map` are pruned against `Find.Maps`.
- Over-count fixed: spawned rods only count when `IsInValidStorage()`.
- Pool rods read through the public holder API instead of reflection.

## Known limitations
- If a bill is restricted to the storage pool's own storage group, the mod leaves it
  alone (it returns early for any restricted bill).

## Next steps
1. Build with `BUILD_TO_READY_FOLDER.bat`, then test in-game: put rods in a pool, set a
   "Do until you have X" bill, and check "Currently have". Also run `CHECK_AFTER_LOAD.bat`.
