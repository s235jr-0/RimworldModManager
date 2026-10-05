Rimatomics Fuel Pool Bill Counter v1.1.0
===============================================

v1.1.0 CHANGES
--------------
- Now compiles against RimWorld 1.6 (v1.0.0 used a field that 1.5+ renamed
  and never built).
- Reads rods in Storage Pools through RimWorld's standard container API
  (the pool keeps them in an inner container); the reflection scan is only
  a fallback now.
- Floor rods only count when they are in valid storage, so loose or
  just-dropped rods can no longer end a bill early.
- Its small caches no longer keep old maps in memory after loading a
  different save.

AUTHOR
------
s235JR

WHAT IT FIXES
-------------
Dubs Rimatomics fuel rods placed in a Storage Pool can be missed by
RimWorld's normal "Do until you have X" product counter.

This mod corrects the target-count result for exactly these two Rimatomics
recipes/products:

    MakeFuelRods     -> FuelRods
    MakeMoxFuelRods  -> FuelRodsMOX

It does not alter:
- storage behavior
- hauling
- radiation
- reactor loading
- fuel burnup
- recipes or material costs

HOW THE FIX WORKS
-----------------
For the two fuel-rod recipes only, the mod leaves vanilla/other-mod counts
alone unless it can prove that more rods physically exist.

Corrected count:
    max(normal count, rods in storage + rods held inside Storage Pools)

That means it never deliberately lowers another mod's count and does not
blindly add the same spawned rod twice.

Pool-held rods are read from the pool's container. If a future Rimatomics
stops exposing that container, a guarded reflection fallback takes over.
Reflection is restricted to the Storage Pool and Rimatomics-owned helper
objects so it does not walk unrelated game objects.

Bills explicitly restricted to a stockpile zone are left untouched.

PERFORMANCE
-----------
The patch only runs special logic for the two Rimatomics fuel-rod recipes.
Spawned rods are obtained from RimWorld's indexed ListerThings query.
The fallback scan for internally-held pool rods is cached for 60 game ticks.

BUILD
-----
Run:
    BUILD_TO_READY_FOLDER.bat

The finished mod will be created at:
    READY_TO_DROP\Rimatomics Fuel Pool Bill Counter

Nothing is installed automatically.

QUICK TEST
----------
After enabling the mod and loading your save:

1. Open the Rimatomics machining table.
2. Open Make Uranium Fuel rods or Make MOX fuel rods.
3. Set the bill to "Do until you have X".
4. Look at "Currently have".

The displayed count should already include rods in Storage Pools.
You do not need to manufacture another rod to test the fix.

You can also run:
    CHECK_AFTER_LOAD.bat

That only reads Player.log and shows this mod's diagnostic lines.

WORKSHOP METADATA
-----------------
Name:
    Rimatomics Fuel Pool Bill Counter

Author:
    s235JR

Package ID:
    s235jr.rimatomics.fuelpoolbillcounter

Requires:
    Harmony
    Dubs Rimatomics

PRIVACY
-------
The package contains no personal name, username, machine name, save-game
name, local log, or user-specific filesystem path.
