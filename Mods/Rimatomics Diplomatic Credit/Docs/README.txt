Rimatomics Diplomatic Credit v0.9.0
====================================

AUTHOR
------
s235JR

WHAT IT DOES
------------
Adds configurable diplomatic credit when a hostile settlement is destroyed by a
Dubs Rimatomics nuclear strike.

Goodwill adjustment:
    -100 to +100

Default:
    +10

Dubs Rimatomics' own nuclear diplomacy effects remain unchanged.

IN-GAME SETTINGS
----------------
Options -> Mod settings -> Rimatomics Diplomatic Credit

The setting is saved immediately when changed.

BUILD
-----
Run:
    BUILD_TO_READY_FOLDER.bat

The finished mod is created inside:
    READY_TO_DROP\Rimatomics Diplomatic Credit

Nothing is installed automatically.

WORKSHOP
--------
The folder created inside READY_TO_DROP is the mod folder to use for a local
install or Workshop upload after compiling.

Public metadata:
    Name: Rimatomics Diplomatic Credit
    Author: s235JR
    Package ID: s235jr.rimatomics.diplomaticcredit

PRIVACY
-------
This package contains no personal name, username, machine name, local log,
save-game name, or user-specific filesystem path.

CHANGE FROM EARLIER TEST BUILDS
-------------------------------
The public package ID changed from the earlier test identifier to:
    s235jr.rimatomics.diplomaticcredit

RimWorld will therefore treat this as the finalized mod identity. Remove the
older test copy before enabling this one to avoid duplicate versions.


V0.9.0 - OWN HISTORY ENTRY + SAFER TRIGGER
------------------------------------------
- The goodwill message and faction history now name this mod's adjustment
  ("Rimatomics strike destroyed a hostile base") instead of passing no
  reason. Before, the history showed RimWorld's generic "Destroyed base +10"
  even when the addon applied +30.
  New file: 1.6\Defs\HistoryEventDefs\RDC_HistoryEvents.xml
- Only settlements hostile to the player trigger the reward, so quests or
  other mods removing friendly/neutral settlements no longer do.
- The normal-conquest exclusion still works when another mod has
  Harmony-patched RimWorld's conquest code (no double reward).

NOT YET TESTED IN-GAME. After a strike, check the blue message and the
faction's diplomacy history for the new entry name.


V0.8.1 - DIRECT STRIKE DETECTION FIX
------------------------------------
The settings system was working correctly, but the old nuclear-strike detector
could miss a real Rimatomics strike when Rimatomics destroyed the world
settlement after its own immediate call stack had already unwound.

That meant:
    - the configured value could load correctly (for example +30)
    - but the goodwill code never ran for the strike

v0.8.1 fixes this by detecting direct Settlement destruction and excluding the
normal RimWorld SettlementDefeatUtility and SettlementAbandonUtility paths.

This keeps normal map conquest from receiving a duplicate reward while allowing
delayed Rimatomics world-map destruction to trigger reliably.

Diagnostic log lines now include:
    Direct settlement destruction detected...
    Strike is using configured goodwill adjustment = +XX.
    Applied +XX goodwill...
