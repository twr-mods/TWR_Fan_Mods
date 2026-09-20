using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AllPromotionsPossible;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    private Harmony _harmony;

    private static readonly Type[] AdvancedClasses =
    {
        typeof(AcolyteClass),
        typeof(BladeClass),
        typeof(CrusaderClass),
        typeof(HeroClass),
        typeof(HunterClass),
        typeof(KnightClass),
        typeof(LancerClass),
        typeof(LongbowmanClass),
        typeof(PriestClass),
        typeof(RaiderClass),
        typeof(RogueClass),
        typeof(ScoutClass),
        typeof(SentinelClass),
        typeof(WarriorClass)
    };

    private static readonly Type[] ExaltedClasses =
    {
        typeof(AssassinClass),
        typeof(BerzerkerClass),
        typeof(BlademasterClass),
        typeof(CenturionClass),
        typeof(ChampionClass),
        typeof(CommandoClass),
        typeof(EnlightenedClass),
        typeof(LionheartClass),
        typeof(MarshalClass),
        typeof(PaladinClass),
        typeof(RangerClass),
        typeof(SaintClass),
        typeof(SniperClass),
        typeof(WarlordClass)
    };

    private static readonly Type[] BaseClasses =
    {
        typeof(SkirmisherClass),
        typeof(FighterClass),
        typeof(SoldierClass),
        typeof(DefenderClass),
        typeof(ArcherClass),
        typeof(SpiritualistClass)
    };

    private void Awake()
    {
        Logger = base.Logger;

        try
        {
            ExpandPromotionLists();
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();
            Logger.LogInfo(
                "All Promotions Possible loaded: every base class now has all " +
                "14 advanced and all 14 exalted promotion options.");
        }
        catch (Exception exception)
        {
            Logger.LogError("All Promotions Possible failed to initialize:");
            Logger.LogError(exception);
        }
    }

    private static void ExpandPromotionLists()
    {
        foreach (Type baseClass in BaseClasses)
        {
            ValueTuple<int[], Type[], Type[], WeaponConstants.WeaponType[]> data =
                ClassConstants.ClassData[baseClass];

            ClassConstants.ClassData[baseClass] =
                new ValueTuple<int[], Type[], Type[], WeaponConstants.WeaponType[]>(
                    data.Item1,
                    AdvancedClasses,
                    ExaltedClasses,
                    data.Item4);
        }
    }
}

internal static class PromotionCarousel
{
    internal static void Arrange(
        List<Unit> renderedUnits,
        int currentIndex,
        float[] xPositions)
    {
        int count = renderedUnits.Count;
        if (count == 0)
        {
            return;
        }

        int previousIndex = (currentIndex - 1 + count) % count;
        int nextIndex = (currentIndex + 1) % count;

        for (int i = 0; i < count; i++)
        {
            Unit preview = renderedUnits[i];
            bool visible = i == currentIndex || i == previousIndex || i == nextIndex;
            preview.gameObject.SetActive(visible);

            if (!visible)
            {
                continue;
            }

            float x = xPositions[0];
            if (i == nextIndex)
            {
                x = xPositions[1];
            }
            else if (i == previousIndex)
            {
                x = xPositions[2];
            }

            preview.transform.localPosition = new Vector3(x, -1f, 3f);
        }
    }
}

[HarmonyPatch(typeof(UnitPromotionUIController), "RenderUnitAsClasses")]
internal static class RenderUnitAsClassesPatch
{
    private static bool Prefix(
        Unit unit,
        List<Unit> ___renderedPromotionUnits,
        float[] ___xPositions)
    {
        Type[] options = unit.GetPromotionOptions();
        GameObject prefab = UnitConstants.GetUnitTuple(unit).Item2;

        foreach (Type option in options)
        {
            UnitClass unitClass = (UnitClass)Activator.CreateInstance(option);
            Unit preview = ChapterController.InstantiateUnitForUI(
                prefab,
                new Vector3(___xPositions[0], -1f, 3f),
                Quaternion.Euler(0f, 190f, 0f),
                CameraController.promotionCamera.gameObject);

            preview.gender = unit.gender;
            preview.ChangeClass(unitClass);
            preview.transform.SetParent(CameraController.promotionCamera.transform);
            ___renderedPromotionUnits.Add(preview);
        }

        PromotionCarousel.Arrange(___renderedPromotionUnits, 0, ___xPositions);
        CameraController.SwitchToPromotionCamera();
        return false;
    }
}

[HarmonyPatch(typeof(UnitPromotionUIController), nameof(UnitPromotionUIController.NextClassPromotion))]
internal static class NextClassPromotionPatch
{
    private static bool Prefix(
        UnitPromotionUIController __instance,
        ConfirmationPopupController ___confirmationPopup,
        bool ___disableNavigation,
        int ___currentIndex,
        Unit ___unit,
        Action ___callbackAfterFinished)
    {
        ___confirmationPopup.Disable();
        if (!___disableNavigation)
        {
            int count = ___unit.GetPromotionOptions().Length;
            int nextIndex = (___currentIndex + 1) % count;
            __instance.BuildUI(___unit, nextIndex, true, ___callbackAfterFinished);
        }

        return false;
    }
}

[HarmonyPatch(typeof(UnitPromotionUIController), nameof(UnitPromotionUIController.PreviousClassPromotion))]
internal static class PreviousClassPromotionPatch
{
    private static bool Prefix(
        UnitPromotionUIController __instance,
        ConfirmationPopupController ___confirmationPopup,
        bool ___disableNavigation,
        int ___currentIndex,
        Unit ___unit,
        Action ___callbackAfterFinished)
    {
        ___confirmationPopup.Disable();
        if (!___disableNavigation)
        {
            int count = ___unit.GetPromotionOptions().Length;
            int previousIndex = (___currentIndex - 1 + count) % count;
            __instance.BuildUI(___unit, previousIndex, true, ___callbackAfterFinished);
        }

        return false;
    }
}

[HarmonyPatch(typeof(UnitPromotionUIController), nameof(UnitPromotionUIController.ShiftUnits))]
internal static class ShiftUnitsPatch
{
    private static bool Prefix()
    {
        // BuildUI still calls this method when the index changes. The postfix below
        // places the three visible previews immediately, so the original three-item
        // animation must not move all fourteen instantiated previews.
        return false;
    }
}

[HarmonyPatch(typeof(UnitPromotionUIController), nameof(UnitPromotionUIController.BuildUI))]
internal static class BuildUIPatch
{
    private static void Postfix(
        int index,
        List<Unit> ___renderedPromotionUnits,
        float[] ___xPositions)
    {
        PromotionCarousel.Arrange(___renderedPromotionUnits, index, ___xPositions);
    }
}
