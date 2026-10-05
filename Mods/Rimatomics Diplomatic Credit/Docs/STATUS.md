# Rimatomics Diplomatic Credit — status

**Current version:** v0.9.0
**Package ID:** `s235jr.rimatomics.diplomaticcredit` · **Author:** s235JR

> **Package ID changed** (was `anon.…`). RimWorld sees it as a new mod: re-enable it in
> the mod list and set its settings again. Not yet built or tested in-game since the change.

## What it does
Adds a configurable goodwill change (-100 to +100, default +10) when a hostile
settlement is destroyed by a Rimatomics nuclear strike. Rimatomics' own diplomacy
effects are left unchanged.

## State
- **v0.8.1 works in-game** (tested in-game: a configured +30 was applied as +30).
- **v0.9.0 compiles** against RimWorld 1.6 + Harmony 2.x (verified 2026-09-23) but is
  **not yet tested in-game**.

## Changes in v0.9.0
- **Own history reason:** new `HistoryEventDef` `RDC_RimatomicsStrikeDestroyedBase`
  (`1.6/Defs/HistoryEventDefs/RDC_HistoryEvents.xml`) is passed to `TryAffectGoodwillWith`
  instead of `null`. It's looked up with `GetNamedSilentFail`, so a missing def falls back
  to the old behavior. Aimed at the "Destroyed base +10" history/popup mismatch.
- **Hostile targets only:** `targetFaction.HostileTo(player)` is required. This narrows
  false triggers from quests and other mods removing settlements.
- **Harmony-aware exclusion:** stack frames are resolved with
  `Harmony.GetOriginalMethodFromStackframe`, so the `SettlementDefeatUtility` /
  `SettlementAbandonUtility` exclusion still matches when another mod patched them.
- `DefaultReward` constant replaces the repeated `10`.

## To check in-game (v0.9.0)
1. The strike still applies the configured value (log: "Applied +XX goodwill...").
2. The blue message and the diplomacy history show the new reason name. If the
   "Destroyed base +10" entry *also* still appears, that one comes from Rimatomics or
   vanilla, not this addon. It would then be a separate +10 on top, which is worth knowing.
3. Normal (non-nuclear) conquest of a base gives no extra addon reward.

## Possible future improvement
Trigger only on confirmed Rimatomics strikes. Rimatomics 1.6 has
`Rimatomics.WorldObject_ICBMfission.Arrived` (also `NukemRico`, `MakeFallout`) and a
separate `WorldObject_Fallout.Arrived`. Recording struck tiles there and rewarding only
settlements on those tiles would remove all non-nuke triggers. Not done yet: the
destruction is delayed (likely via fallout), so this needs in-game testing to get right.

## History (short)
- Early versions patched `WorldObject.Destroy` and required a live Rimatomics call-stack
  frame, which missed delayed world-map destruction. That's why it never triggered.
- v0.7 made the settings slider save immediately and log the real loaded value.
- v0.8 changed the author and package ID (settings had to be re-set).
- v0.8.1 detects direct settlement destruction, excluding `SettlementDefeatUtility`
  and `SettlementAbandonUtility` to avoid double rewards on normal conquest.
