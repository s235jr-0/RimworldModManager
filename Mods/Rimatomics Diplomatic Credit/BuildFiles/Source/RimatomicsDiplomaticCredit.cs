using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimatomicsDiplomaticCredit
{
    public class DiplomaticCreditSettings : ModSettings
    {
        public const int DefaultReward = 10;

        public int goodwillReward = DefaultReward;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref goodwillReward, "goodwillReward", DefaultReward);
            goodwillReward = ClampReward(goodwillReward);
            base.ExposeData();
        }

        public static int ClampReward(int value)
        {
            return Math.Max(-100, Math.Min(100, value));
        }
    }

    public class DiplomaticCreditMod : Mod
    {
        public static DiplomaticCreditSettings Settings;
        private int lastSavedReward;

        public DiplomaticCreditMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DiplomaticCreditSettings>();
            Settings.goodwillReward = DiplomaticCreditSettings.ClampReward(Settings.goodwillReward);
            lastSavedReward = Settings.goodwillReward;

            Log.Message(
                "[Rimatomics Diplomatic Credit] Settings loaded. Current goodwill adjustment = " +
                FormatSigned(Settings.goodwillReward) + "."
            );
        }

        public override string SettingsCategory()
        {
            return "Rimatomics Diplomatic Credit";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings == null)
            {
                Settings = GetSettings<DiplomaticCreditSettings>();
                Settings.goodwillReward = DiplomaticCreditSettings.ClampReward(Settings.goodwillReward);
                lastSavedReward = Settings.goodwillReward;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label(
                "Goodwill adjustment when a Rimatomics strike destroys a hostile settlement: " +
                FormatSigned(Settings.goodwillReward)
            );
            listing.Gap(6f);

            float sliderValue = listing.Slider((float)Settings.goodwillReward, -100f, 100f);
            int newValue = DiplomaticCreditSettings.ClampReward((int)Math.Round(sliderValue));

            if (newValue != Settings.goodwillReward)
            {
                Settings.goodwillReward = newValue;

                // Save immediately so the value cannot be lost if the player leaves
                // the menu in an unusual way or another mod interferes with dialog closing.
                WriteSettings();

                Log.Message(
                    "[Rimatomics Diplomatic Credit] Setting changed and saved immediately: goodwill adjustment = " +
                    FormatSigned(Settings.goodwillReward) + "."
                );
            }

            listing.Gap(12f);
            listing.Label("Range: -100 to +100");
            listing.Label("Default: " + FormatSigned(DiplomaticCreditSettings.DefaultReward));
            listing.Gap(6f);
            listing.Label(
                "This is the addon's goodwill adjustment only. " +
                "Dubs Rimatomics' own nuclear diplomacy effects remain unchanged."
            );

            listing.End();
        }

        public override void WriteSettings()
        {
            if (Settings != null)
            {
                Settings.goodwillReward =
                    DiplomaticCreditSettings.ClampReward(Settings.goodwillReward);

                lastSavedReward = Settings.goodwillReward;
            }

            base.WriteSettings();
        }

        public static int CurrentGoodwillReward
        {
            get
            {
                if (Settings == null)
                    return DiplomaticCreditSettings.DefaultReward;

                return DiplomaticCreditSettings.ClampReward(Settings.goodwillReward);
            }
        }

        public static string FormatSigned(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }
    }

    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("s235jr.rimatomics.diplomaticcredit");
                harmony.PatchAll();
                Log.Message("[Rimatomics Diplomatic Credit] Harmony patches applied. Direct-strike detector v0.9.0 active. Settings range: -100 to +100.");
            }
            catch (Exception ex)
            {
                Log.Error("[Rimatomics Diplomatic Credit] Failed to initialize: " + ex);
            }
        }
    }

    internal sealed class DestroyState
    {
        public Faction targetFaction;
        public string settlementName;
        public List<Faction> mutualEnemies;
    }

    [HarmonyPatch(typeof(WorldObject), "Destroy")]
    internal static class Patch_WorldObject_Destroy
    {
        private static void Prefix(WorldObject __instance, out DestroyState __state)
        {
            __state = null;

            try
            {
                Settlement settlement = __instance as Settlement;
                if (settlement == null)
                    return;

                Faction targetFaction = settlement.Faction;
                if (targetFaction == null || targetFaction.IsPlayer)
                    return;

                // The reward is for destroying a HOSTILE settlement. This also
                // keeps quests/other mods removing friendly or neutral
                // settlements from triggering it.
                Faction player = Faction.OfPlayer;
                if (player == null || !targetFaction.HostileTo(player))
                    return;

                bool sawRimatomicsFrame;
                if (!IsDirectSettlementDestruction(out sawRimatomicsFrame))
                    return;

                Log.Message(
                    "[Rimatomics Diplomatic Credit] Direct settlement destruction detected. " +
                    "Rimatomics frame present in current call stack: " +
                    (sawRimatomicsFrame ? "yes" : "no") + "."
                );

                DestroyState state = new DestroyState();
                state.targetFaction = targetFaction;
                state.settlementName = settlement.Label;
                state.mutualEnemies = new List<Faction>();

                List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
                for (int i = 0; i < factions.Count; i++)
                {
                    Faction faction = factions[i];
                    if (faction == null)
                        continue;
                    if (faction == Faction.OfPlayer || faction == targetFaction)
                        continue;

                    if (faction.HostileTo(targetFaction))
                        state.mutualEnemies.Add(faction);
                }

                __state = state;

                Log.Message(
                    "[Rimatomics Diplomatic Credit] Rimatomics is destroying settlement '" +
                    state.settlementName + "' (" + targetFaction.Name + "). " +
                    state.mutualEnemies.Count + " hostile faction(s) eligible."
                );
            }
            catch (Exception ex)
            {
                Log.Error("[Rimatomics Diplomatic Credit] Error preparing goodwill adjustment: " + ex);
                __state = null;
            }
        }

        private static void Postfix(WorldObject __instance, DestroyState __state)
        {
            if (__state == null)
                return;

            try
            {
                if (!__instance.Destroyed)
                {
                    Log.Warning("[Rimatomics Diplomatic Credit] Settlement destruction did not complete; no goodwill adjustment applied.");
                    return;
                }

                Faction player = Faction.OfPlayer;
                if (player == null)
                    return;

                int goodwillReward = DiplomaticCreditMod.CurrentGoodwillReward;
                int affected = 0;

                Log.Message(
                    "[Rimatomics Diplomatic Credit] Strike is using configured goodwill adjustment = " +
                    DiplomaticCreditMod.FormatSigned(goodwillReward) + "."
                );

                // Our own history event, so the message and the diplomacy
                // history show this adjustment under its own name instead of
                // RimWorld's generic "destroyed base" entry. Null (the old
                // behavior) if the def is somehow missing.
                HistoryEventDef reason =
                    DefDatabase<HistoryEventDef>.GetNamedSilentFail("RDC_RimatomicsStrikeDestroyedBase");

                for (int i = 0; i < __state.mutualEnemies.Count; i++)
                {
                    Faction faction = __state.mutualEnemies[i];
                    if (faction == null)
                        continue;

                    if (faction.TryAffectGoodwillWith(
                        player,
                        goodwillReward,
                        true,
                        true,
                        reason,
                        null))
                    {
                        affected++;
                    }
                }

                Log.Message(
                    "[Rimatomics Diplomatic Credit] Applied " + DiplomaticCreditMod.FormatSigned(goodwillReward) +
                    " goodwill to " + affected + " faction(s) for destruction of '" +
                    __state.settlementName + "'. Rimatomics' own nuclear diplomacy remains unchanged."
                );
            }
            catch (Exception ex)
            {
                Log.Error("[Rimatomics Diplomatic Credit] Error applying goodwill adjustment: " + ex);
            }
        }

        private static bool IsDirectSettlementDestruction(out bool sawRimatomics)
        {
            sawRimatomics = false;

            try
            {
                StackTrace trace = new StackTrace();
                StackFrame[] frames = trace.GetFrames();

                // If stack inspection is unavailable, do not block the event.
                // WorldObject.Destroy on a Settlement is already a narrow hook.
                if (frames == null)
                    return true;

                for (int i = 0; i < frames.Length; i++)
                {
                    // When another mod has Harmony-patched a method (e.g.
                    // SettlementDefeatUtility.CheckDefeated), its frame is a
                    // generated replacement with no declaring type. Map it back
                    // to the original so the exclusions below still match.
                    MethodBase method = null;
                    try { method = Harmony.GetOriginalMethodFromStackframe(frames[i]); }
                    catch { }
                    if (method == null)
                        method = frames[i].GetMethod();
                    if (method == null)
                        continue;

                    Type type = method.DeclaringType;
                    if (type == null)
                        continue;

                    string fullName = type.FullName ?? "";
                    string ns = type.Namespace ?? "";
                    string assemblyName = "";

                    try
                    {
                        assemblyName = type.Assembly.GetName().Name ?? "";
                    }
                    catch
                    {
                    }

                    // Conventional map conquest already has RimWorld's own diplomacy
                    // handling. Never add our reward on that path.
                    if (fullName.IndexOf("SettlementDefeatUtility", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;

                    // Likewise, abandoning/removing a settlement is not a nuclear strike.
                    if (fullName.IndexOf("SettlementAbandonUtility", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;

                    if (assemblyName.Equals("Rimatomics", StringComparison.OrdinalIgnoreCase) ||
                        assemblyName.Equals("DubsRimatomics", StringComparison.OrdinalIgnoreCase) ||
                        ns.Equals("Rimatomics", StringComparison.OrdinalIgnoreCase) ||
                        ns.StartsWith("Rimatomics.", StringComparison.OrdinalIgnoreCase) ||
                        fullName.StartsWith("Rimatomics.", StringComparison.OrdinalIgnoreCase))
                    {
                        sawRimatomics = true;
                    }
                }

                // IMPORTANT:
                // Older builds required a Rimatomics stack frame here. Current Rimatomics
                // can remove the world settlement after the immediate Rimatomics call has
                // unwound, so that check caused genuine nuclear strikes to be missed.
                //
                // We instead accept any direct Settlement.Destroy path while excluding the
                // vanilla defeat/abandon paths above. This catches the delayed Rimatomics
                // world-map destruction reliably without double-rewarding normal conquest.
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "[Rimatomics Diplomatic Credit] Could not inspect settlement destruction call stack: " +
                    ex.Message + ". Allowing direct-destruction handling."
                );
                return true;
            }
        }
    }
}
