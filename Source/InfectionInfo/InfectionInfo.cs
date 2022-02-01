#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using RimWorld;
using Verse;
using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace CF_InfectionInfo
{
    public class Patcher: Mod
    {
        public static Settings Settings = new();

        public Patcher(ModContentPack pack): base(pack)
        {
            Settings = GetSettings<Settings>();
            DoPatching();
        }
        public override string SettingsCategory()
        {
            return "Infection Info";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new();
            listingStandard.Begin(inRect);
            listingStandard.CheckboxLabeled("Use current room for infection chance", ref Settings.UseCurrentRoomForInfection, "Base game uses room cleanliness at tend time to adjust infection chance. This option makes the game to consider the current room instead.");
            listingStandard.End();
            base.DoSettingsWindowContents(inRect);
        }

        public void DoPatching()
        {
            var harmony = new Harmony("com.colinfang.InfectionInfo");
            harmony.PatchAll();
        }
    }

    public class Settings: ModSettings
    {
        public bool UseCurrentRoomForInfection;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref UseCurrentRoomForInfection, "UseCurrentRoomForInfection", false);
            base.ExposeData();
        }
    }


    public static class InfectionUtililty
    {
        public class InfecterData
        {
            public HediffComp_Infecter Infecter;
            public float BaseInfectionChance;
            public float InfectionChanceFactorFromRoom = 1f;
            public float InfectionChanceFactorFromTendQuality = 1f;
            public float InfectionChanceFactorFromSeverity = 1f;
            public string? InfectionSymbol;

            public InfecterData(HediffComp_Infecter infecter)
            {
                float baseInfectionChance = infecter.Props.infectionChance;
                if (infecter.Pawn.RaceProps.Animal)
                {
                    baseInfectionChance *= 0.1f;
                }

                // Log.Message($"Init InfecterData {infecter.Pawn} {infecter.parent}");

                Infecter = infecter;
                BaseInfectionChance = baseInfectionChance;
                Update();
            }

            public int Age => Infecter.parent.ageTicks;
            public int TicksUntilDanger => HealthTuning.InfectionDelayRange.min - Age;
            public int TicksUntilSafe => HealthTuning.InfectionDelayRange.max - Age;
            public bool IsGrace => HealthTuning.InfectionDelayRange.min > Age;

            public bool IsDanger => (HealthTuning.InfectionDelayRange.min <= Age) && (HealthTuning.InfectionDelayRange.max >= Age);
            public float TotalInfectionChance => BaseInfectionChance * InfectionChanceFactorFromRoom * InfectionChanceFactorFromTendQuality * InfectionChanceFactorFromSeverity;

            public override string ToString()
            {
                var hediff = Infecter.parent;
                return $"InfecterData({Infecter.Pawn}:{hediff}:{Infecter}:{InfectionSymbol}:{TicksUntilDanger}:{TotalInfectionChance:F3}:{InfectionChanceFactorFromRoom:F3}:{InfectionChanceFactorFromTendQuality:F3}:{InfectionChanceFactorFromSeverity:F3})";
            }

            public void Update()
            {
                var hediff = Infecter.parent;
                InfectionChanceFactorFromSeverity = InfectionChanceFactorFromSeverityCurve.Evaluate(hediff.Severity);
                InfectionChanceFactorFromRoom = Patcher.Settings.UseCurrentRoomForInfection ? GetInfectionChanceFactorFromCurrentRoom(Infecter.Pawn.GetRoom()) : (float)F_infectionChanceFactorFromTendRoom.GetValue(Infecter);

                var tendDuration = hediff.TryGetComp<HediffComp_TendDuration>();
                if (tendDuration?.IsTended ?? false)
                {
                    InfectionChanceFactorFromTendQuality = InfectionChanceFactorFromTendQualityCurve.Evaluate(tendDuration.tendQuality);
                }

                //if (Infecter.Pawn.IsColonist)
                //{
                //    Log.Message($"Updated {Patcher.Settings.UseCurrentRoomForInfection} {this}");
                //}
                InfectionSymbol = GetInfectionSymbol();
            }

            public string? GetInfectionSymbol()
            {
                // Cached in InfectionSymbol via Tick
                var suffix = "☣️";
                if (IsGrace)
                {
                    return suffix;
                }
                else if (IsDanger)
                {
                    if (TotalInfectionChance < 0.1)
                    {
                        return suffix.Colorize(Color.yellow);
                    }
                    else
                    {
                        return suffix.Colorize(Color.red);
                    }
                }
                return null;
            }
        }

        public static bool IsSafe(int age) => age > HealthTuning.InfectionDelayRange.max;

        public static Dictionary<HediffWithComps, InfecterData> InfectionDataDict = new();
        public static int AlreadyMadeInfectionValue = (int)typeof(HediffComp_Infecter).GetField("AlreadyMadeInfectionValue", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        public static FieldInfo F_ticksUntilInfect = typeof(HediffComp_Infecter).GetField("ticksUntilInfect", BindingFlags.NonPublic | BindingFlags.Instance);
        public static FieldInfo F_infectionChanceFactorFromTendRoom = typeof(HediffComp_Infecter).GetField("infectionChanceFactorFromTendRoom", BindingFlags.NonPublic | BindingFlags.Instance);
        public static SimpleCurve InfectionChanceFactorFromTendQualityCurve = (SimpleCurve)typeof(HediffComp_Infecter).GetField("InfectionChanceFactorFromTendQualityCurve", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        public static SimpleCurve InfectionChanceFactorFromSeverityCurve = (SimpleCurve)typeof(HediffComp_Infecter).GetField("InfectionChanceFactorFromSeverityCurve", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

        [DebugAction("InfectionInfo", null, false, false, actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void LogInfectionDataDict()
        {
            foreach (var kv in InfectionDataDict)
            {
                Log.Message(kv.Value.ToString());
            }
        }

        // If room is null, fall back to roomless (note it is still possible to be considered roomless even if not null.)
        public static float GetInfectionChanceFactorFromCurrentRoom(Room? room) => room?.GetStat(RoomStatDefOf.InfectionChanceFactor) ?? RoomStatDefOf.InfectionChanceFactor.roomlessScore;

        public static bool CanInfectCheckUponRegister(HediffWithComps parent)
        {
            // Follow HediffComp_Infecter.CompPostPostAdd logic
            // Skip rng test
            if (parent.IsPermanent())
            {
                return false;
            }
            if (parent.Part.def.IsSolid(parent.Part, parent.pawn.health.hediffSet.hediffs))
            {
                return false;
            }
            if (parent.pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(parent.Part))
            {
                return false;
            }
            return true;
        }

        public static bool CanInfectCheckFrequently(HediffComp_Infecter infecter)
        {
            // Need to revisit frequenctly to check if conditions are met
            // Otherwise need to patch a lot of funtions for callback
            var hediff = infecter.parent;
            if (infecter.Pawn.Dead)
            {
                return false;
            }
            if (IsSafe(hediff.ageTicks))
            {
                return false;
            }
            if ((int)F_ticksUntilInfect.GetValue(infecter) == AlreadyMadeInfectionValue)
            {
                return false;
            }
            return true;
        }


        public static void GcAndUpdate()
        {
            // Log.Message("InfectionUtililty GcAndUpdate");

            List<HediffWithComps> toRemove = new();
            foreach (var kv in InfectionDataDict)
            {
                var hediff = kv.Key;
                var data = kv.Value;
                if (!CanInfectCheckFrequently(data.Infecter))
                {
                    toRemove.Add(hediff);
                    // Log.Message($"Remove {data}");
                    continue;
                }
                data.Update();
            }
            foreach (var hediff in toRemove)
            {
                InfectionDataDict.Remove(hediff);
            }
        }

        public static InfecterData? CheckAndRegister(HediffComp_Infecter infecter)
        {
            var hediff = infecter.parent;
            if (!CanInfectCheckFrequently(infecter) || !CanInfectCheckUponRegister(hediff))
            {
                return null;
            }
            InfecterData data = new(infecter);
            // Not sure if there is duplicate, so not using Add
            InfectionDataDict[hediff] = data;
            return data;
        }

    }



    [HarmonyPatch(typeof(HediffStatsUtility))]
    [HarmonyPatch(nameof(HediffStatsUtility.SpecialDisplayStats))]
    public class PatchHediffStatsUtilitySpecialDisplayStats
    {
        public static IEnumerable<StatDrawEntry> BuildExtraEntries(HediffWithComps hediff)
        {
            if (InfectionUtililty.InfectionDataDict.TryGetValue(hediff, out var data))
            {
                string placeholder = "Tell me what is this";
                yield return new StatDrawEntry(StatCategoryDefOf.Basics, "Infection chance", data.TotalInfectionChance.ToStringByStyle(ToStringStyle.PercentZero), placeholder, 4040);
                yield return new StatDrawEntry(StatCategoryDefOf.Basics, "    Base value", data.BaseInfectionChance.ToStringByStyle(ToStringStyle.PercentZero), placeholder, 4040);

                if (data.InfectionChanceFactorFromRoom != 1)
                {
                    yield return new StatDrawEntry(StatCategoryDefOf.Basics, Patcher.Settings.UseCurrentRoomForInfection ? "    From current room" : "    From tend room", $"x{data.InfectionChanceFactorFromRoom.ToStringByStyle(ToStringStyle.PercentZero)}", placeholder, 4040);
                }
                if (data.InfectionChanceFactorFromTendQuality != 1)
                {
                    yield return new StatDrawEntry(StatCategoryDefOf.Basics, "    From tend quality", $"x{data.InfectionChanceFactorFromTendQuality.ToStringByStyle(ToStringStyle.PercentZero)}", placeholder, 4040);
                }

                yield return new StatDrawEntry(StatCategoryDefOf.Basics, "    From severity", $"x{data.InfectionChanceFactorFromSeverity.ToStringByStyle(ToStringStyle.PercentZero)}", placeholder, 4040);
                if (data.IsGrace)
                {
                    yield return new StatDrawEntry(StatCategoryDefOf.Basics, "    Danger in", $"{(float)data.TicksUntilDanger / GenDate.TicksPerHour:F1} Hour", placeholder, 4040);
                }
                else if (data.IsDanger)
                {
                    yield return new StatDrawEntry(StatCategoryDefOf.Basics, "    Safe in", $"{(float)data.TicksUntilSafe / GenDate.TicksPerHour:F1} Hour", placeholder, 4040);
                }
            }
        }

        public static void Postfix(ref IEnumerable<StatDrawEntry> __result, Hediff instance)
        {
            if (instance is HediffWithComps hediff)
            {
                __result = __result.Concat(BuildExtraEntries(hediff));
            }
        }
    }

    [HarmonyPatch(typeof(HediffComp_Infecter))]
    [HarmonyPatch(nameof(HediffComp_Infecter.CompPostPostAdd))]
    public class PatchHediff_HediffComp_InfecterCompPostPostAdd
    {
        public static void Postfix(HediffComp_Infecter __instance) => InfectionUtililty.CheckAndRegister(__instance);
    }


    [HarmonyPatch(typeof(HediffComp_Infecter))]
    [HarmonyPatch(nameof(HediffComp_Infecter.CompExposeData))]
    public class PatchHediff_HediffComp_InfecterExposeData
    {
        public static void Postfix(HediffComp_Infecter __instance)
        {
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // HediffComp_TendDuration was loaded by now, during LoadSaveMode.LoadingVars
                // However, `HediffComp_TendDuration.IsTend` is false as it needs `ProgramState.Playing`
                // And `Pawn.GetRoom` is null
                // So after the game is loaded & paused, the immediate stats are wrong until a further update is called when the game resumes.
                // Is there a better place to init this?
                InfectionUtililty.CheckAndRegister(__instance);
            }
        }
    }


    [HarmonyPatch(typeof(Hediff_Injury))]
    [HarmonyPatch(nameof(Hediff_Injury.LabelBase), MethodType.Getter)]
    public class PatchHediff_InjuryLabelBase
    {
        public static void Postfix(ref string __result, Hediff_Injury __instance)
        {
            if (InfectionUtililty.InfectionDataDict.TryGetValue(__instance, out var data))
            {
                if (data.InfectionSymbol is not null)
                {
                    __result += " " + data.InfectionSymbol;
                }
            }
        }
    }


    [HarmonyPatch(typeof(PostLoadIniter))]
    [HarmonyPatch(nameof(PostLoadIniter.DoAllPostLoadInits))]
    public class PatchHediff_PostLoadIniterDoAllPostLoadInits
    {
        public static void Prefix()
        {
            // Is there a better place to reset?
            InfectionUtililty.InfectionDataDict.Clear();
            Log.Message("InfectionInfo utililty resets");
        }
    }

    [HarmonyPatch(typeof(HealthCardUtility))]
    [HarmonyPatch(nameof(HealthCardUtility.DrawHediffListing))]
    public class PatchHealthCardUtilityDrawHediffListing
    {
        public static void Postfix()
        {
            // Trigger only if Health panel is active.
            if (GenTicks.TicksGame % 60 == 0)
            {
                InfectionUtililty.GcAndUpdate();
            }
        }
    }

    [HarmonyPatch(typeof(InspectTabBase))]
    [HarmonyPatch(nameof(InspectTabBase.OnOpen))]
    public class PatchOnOpen
    {
        public static void Postfix(InspectTabBase __instance)
        {
            // Update just before the tab is drawn so the data is latest.
            if (__instance is ITab_Pawn_Health)
            {
                // Log.Message("InfectionInfo utility OnOpen");
                InfectionUtililty.GcAndUpdate();
            }
        }
    }

    [HarmonyPatch(typeof(HediffWithComps))]
    [HarmonyPatch(nameof(HediffWithComps.PostRemoved))]
    public class PatchHediffWithCompsPostRemoved
    {
        public static void Postfix(HediffWithComps __instance)
        {
            // Log.Message($"Remove hediff {__instance.pawn}:{__instance}");
            InfectionUtililty.InfectionDataDict.Remove(__instance);
        }
    }

    [HarmonyPatch(typeof(HediffComp_Infecter))]
    [HarmonyPatch("CheckMakeInfection")]
    public class PatchHediffComp_InfecterCheckMakeInfection
    {
        public static void Prefix(HediffComp_Infecter __instance)
        {
            if (Patcher.Settings.UseCurrentRoomForInfection)
            {
                InfectionUtililty.F_infectionChanceFactorFromTendRoom.SetValue(__instance, InfectionUtililty.GetInfectionChanceFactorFromCurrentRoom(__instance.Pawn.GetRoom()));
            }
        }
    }


}