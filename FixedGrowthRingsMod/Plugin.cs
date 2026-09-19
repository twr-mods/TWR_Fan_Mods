using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FixedGrowthRingsMod;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.twrmods.fixedgrowthrework";
    public const string Name = "Fixed Growth Rework";
    public const string Version = "1.0.0";
    internal static ManualLogSource Log;
    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("[FixedGrowthRework] Plugin loaded.");
        try
        {
            GrowthSystem.ValidateGameShape();
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("[FixedGrowthRework] Fixed-growth level-up patch applied successfully.");
            Log.LogInfo("[FixedGrowthRework] Unit accumulator save/load patches applied successfully.");
        }
        catch (Exception ex)
        {
            Log.LogError("[FixedGrowthRework] ERROR applying patches:\n" + ex);
        }
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();
}

internal enum GrowthStat { Vitality, Strength, Skill, Agility, Endurance, Defense }

internal sealed class StatResult
{
    internal GrowthStat Stat;
    internal int Before, After, Starting, BaseGrowth, EquipmentBonus, EffectiveGrowth;
    internal int ProgressBefore, RawProgress, AccumulatorGain, ProgressAfter;
    internal int EligibleLevels, PromotionGains, CatchupGain, FinalGain;
    internal float Expected, Difference;
    internal string Sources;
}

internal static class GrowthSystem
{
    private const int DefaultProgress = 50;
    private const string KeyPrefix = Plugin.Guid + ".v1.";
    private static readonly GrowthStat[] Stats = (GrowthStat[])Enum.GetValues(typeof(GrowthStat));
    private static readonly Dictionary<string, Dictionary<GrowthStat, int>> Progress = new();
    private static readonly FieldInfo LevelField = Field("level");
    private static readonly FieldInfo CloseableField = Field("isCloseable");
    private static readonly FieldInfo CallbackField = Field("afterLevelUpClosed");
    private static readonly FieldInfo[] ValueFields =
    {
        Field("vitality"), Field("strength"), Field("skill"),
        Field("agility"), Field("endurance"), Field("defense")
    };

    private static FieldInfo Field(string name) => AccessTools.Field(typeof(LevelUpUIController), name)
        ?? throw new MissingFieldException(typeof(LevelUpUIController).FullName, name);

    internal static void ValidateGameShape()
    {
        if (AccessTools.Method(typeof(LevelUpUIController), "ProcessLevelUp", new[] { typeof(PlayerControlledUnit), typeof(Action) }) == null)
            throw new MissingMethodException("LevelUpUIController.ProcessLevelUp(PlayerControlledUnit, Action)");
    }

    internal static bool Prefix(LevelUpUIController controller, PlayerControlledUnit unit, Action callback)
    {
        if (!SaveController.IsFixedGrowthsEnabled()) return true; // Random growth remains completely vanilla.
        try
        {
            LoadProgress(unit, false);
            controller.StartCoroutine(ProcessFixed(controller, unit, callback));
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[FixedGrowthRework] ERROR starting fixed level-up for {Describe(unit)}; using vanilla fallback:\n{ex}");
            return true;
        }
    }

    private static IEnumerator ProcessFixed(LevelUpUIController controller, PlayerControlledUnit unit, Action callback)
    {
        yield return new WaitForSeconds(0.5f);
        object levelText = LevelField.GetValue(controller);
        SetProperty(levelText, "text", unit.level.ToString());
        SetProperty(levelText, "color", ColorConstants.TEXT_GREEN);
        PlayAttributeSound();

        int[] promos = PromotionGains(unit);
        List<StatResult> results = new(6);
        LogBegin(unit);
        for (int i = 0; i < Stats.Length; i++)
        {
            StatResult result = Calculate(unit, Stats[i], promos[i]);
            results.Add(result);
            Progress[unit.uniqueUnitId][result.Stat] = result.ProgressAfter;
            SaveOne(unit, result.Stat, result.ProgressAfter);
            LogStat(result);
        }

        foreach (StatResult result in results)
        {
            if (result.FinalGain == 0) continue;
            yield return new WaitForSeconds(0.35f);
            Apply(unit, result.Stat, result.FinalGain);
            SetProperty(ValueFields[(int)result.Stat].GetValue(controller), "text", Current(unit, result.Stat).ToString());
            LevelUpStatRow row = Row(controller, result.Stat);
            object plusText = AccessTools.Field(typeof(LevelUpStatRow), "plusText").GetValue(row);
            SetProperty(plusText, "text", "+" + result.FinalGain);
            row.AnimateStatIncrease();
        }

        LogSummary(unit, results);
        yield return new WaitForSeconds(1f);
        CallbackField.SetValue(controller, callback);
        CloseableField.SetValue(controller, true);
        Plugin.Log.LogInfo("============================================================\n" +
            $"[FixedGrowthRework] LEVEL-UP END\nUnit: {unit.UNIT_NAME_PROP}\nLevel: {unit.level}\n" +
            "============================================================");
    }

    private static StatResult Calculate(PlayerControlledUnit unit, GrowthStat stat, int promo)
    {
        int before = Current(unit, stat);
        int baseGrowth = BaseGrowth(unit, stat);
        int effective = EffectiveGrowth(unit, stat);
        int prior = Progress[unit.uniqueUnitId][stat];
        int raw = prior + effective;
        int accumulatorGain = Math.Max(0, raw / 100);
        int remainder = raw % 100;

        int adjustedLevel = unit.level;
        if (adjustedLevel > 10 && unit.D_LEVEL < 10) adjustedLevel--;
        if (adjustedLevel > 20 && unit.D_LEVEL < 20) adjustedLevel--;
        int eligible = adjustedLevel - unit.D_LEVEL;
        float expected = Starting(unit, stat) + promo + eligible * baseGrowth / 100f;
        float difference = expected - before;
        int catchup = Utilities.ApproximatelyEquals(difference, 0.5f, 0.00001f) || difference > 0.5f ? 1 : 0;
        int gain = Math.Max(accumulatorGain, catchup);
        return new StatResult
        {
            Stat = stat, Before = before, After = before + gain, Starting = Starting(unit, stat),
            BaseGrowth = baseGrowth, EquipmentBonus = effective - baseGrowth, EffectiveGrowth = effective,
            ProgressBefore = prior, RawProgress = raw, AccumulatorGain = accumulatorGain, ProgressAfter = remainder,
            EligibleLevels = eligible, PromotionGains = promo, Expected = expected, Difference = difference,
            CatchupGain = catchup, FinalGain = gain, Sources = BonusSources(unit, stat)
        };
    }

    private static void Apply(PlayerControlledUnit u, GrowthStat s, int gain)
    {
        switch (s)
        {
            case GrowthStat.Vitality: u.vitalityAttr += gain; u.currentVitalityAttr += gain; break;
            case GrowthStat.Strength: u._strengthAttr += gain; break;
            case GrowthStat.Skill: u._skillAttr += gain; break;
            case GrowthStat.Agility: u._agilityAttr += gain; break;
            case GrowthStat.Endurance: u._enduranceAttr += gain; break;
            case GrowthStat.Defense: u._defenseAttr += gain; break;
        }
    }

    private static LevelUpStatRow Row(LevelUpUIController c, GrowthStat s) => s switch
    {
        GrowthStat.Vitality => c.vitLevelUpRow, GrowthStat.Strength => c.strLevelUpRow,
        GrowthStat.Skill => c.sklLevelUpRow, GrowthStat.Agility => c.aglLevelUpRow,
        GrowthStat.Endurance => c.endLevelUpRow, _ => c.defLevelUpRow
    };

    private static void SetProperty(object target, string name, object value)
    {
        if (target == null) throw new NullReferenceException("Cannot set " + name + " on a null UI object.");
        PropertyInfo property = AccessTools.Property(target.GetType(), name)
            ?? throw new MissingMemberException(target.GetType().FullName, name);
        property.SetValue(target, value, null);
    }

    private static int[] PromotionGains(PlayerControlledUnit u)
    {
        int[] result = new int[6];
        if (u.advancedClass != null) Add(result, u.advancedClass.GetStatsOnPromotion());
        if (u.exaltedClass != null) Add(result, u.exaltedClass.GetStatsOnPromotion());
        return result;
    }

    private static void Add(int[] target, int[] source) { for (int i = 0; i < 6; i++) target[i] += source[i]; }

    private static int Current(Unit u, GrowthStat s) => s switch
    {
        GrowthStat.Vitality => u.vitalityAttr, GrowthStat.Strength => u.strengthAttr,
        GrowthStat.Skill => u.skillAttr, GrowthStat.Agility => u.agilityAttr,
        GrowthStat.Endurance => u.enduranceAttr, _ => u.defenseAttr
    };

    private static int Starting(Unit u, GrowthStat s) => s switch
    {
        GrowthStat.Vitality => u.D_VITALITY, GrowthStat.Strength => u.D_STRENGTH,
        GrowthStat.Skill => u.D_SKILL, GrowthStat.Agility => u.D_AGILITY,
        GrowthStat.Endurance => u.D_ENDURANCE, _ => u.D_DEFENSE
    };

    private static int BaseGrowth(Unit u, GrowthStat s) => s switch
    {
        GrowthStat.Vitality => u._vitalityGrowthRate, GrowthStat.Strength => u._strengthGrowthRate,
        GrowthStat.Skill => u._skillGrowthRate, GrowthStat.Agility => u._agilityGrowthRate,
        GrowthStat.Endurance => u._enduranceGrowthRate, _ => u._defenseGrowthRate
    };

    private static int EffectiveGrowth(Unit u, GrowthStat s) => s switch
    {
        GrowthStat.Vitality => u.vitalityGrowthRate, GrowthStat.Strength => u.strengthGrowthRate,
        GrowthStat.Skill => u.skillGrowthRate, GrowthStat.Agility => u.agilityGrowthRate,
        GrowthStat.Endurance => u.enduranceGrowthRate, _ => u.defenseGrowthRate
    };

    private static string BonusSources(Unit u, GrowthStat s)
    {
        List<string> sources = new();
        bool ring = s switch
        {
            GrowthStat.Vitality => u.HasItem<VitalityRing>(), GrowthStat.Strength => u.HasItem<StrengthRing>(),
            GrowthStat.Skill => u.HasItem<SkillRing>(), GrowthStat.Agility => u.HasItem<AgilityRing>(),
            GrowthStat.Endurance => u.HasItem<EnduranceRing>(), _ => u.HasItem<DefenseRing>()
        };
        if (ring) sources.Add(s + " Ring = +" + (s == GrowthStat.Vitality ? 30 : 20));
        if (u.HasItem<AhrimansGrimoire>()) sources.Add("Ahriman's Grimoire = +10");
        return sources.Count == 0 ? "none" : string.Join(", ", sources);
    }

    private static string Key(Unit u, GrowthStat s) => KeyPrefix + u.uniqueUnitId + "." + s;
    private static string Describe(Unit u) => u == null ? "<null>" : $"{u.UNIT_NAME_PROP} ({u.uniqueUnitId})";

    private static void LoadProgress(Unit unit, bool force)
    {
        if (unit is not PlayerControlledUnit || string.IsNullOrEmpty(unit.uniqueUnitId)) return;
        if (!force && Progress.ContainsKey(unit.uniqueUnitId)) return;
        Dictionary<GrowthStat, int> values = new();
        foreach (GrowthStat stat in Stats)
        {
            string key = Key(unit, stat);
            try
            {
                bool exists = SaveController.KeyExists(key);
                int value = exists ? SaveController.Load(key, DefaultProgress) : DefaultProgress;
                values[stat] = value;
                Plugin.Log.LogInfo(exists
                    ? $"[FixedGrowthRework] Loaded {stat} progress for {Describe(unit)}: {value} (key: {key})"
                    : $"[FixedGrowthRework] No saved {stat} progress for {Describe(unit)}; initializing to {DefaultProgress} (key: {key})");
            }
            catch (Exception ex)
            {
                values[stat] = DefaultProgress;
                Plugin.Log.LogError($"[FixedGrowthRework] ERROR loading {stat} for {Describe(unit)} (key: {key}); using 50:\n{ex}");
            }
        }
        Progress[unit.uniqueUnitId] = values;
    }

    private static void SaveOne(Unit unit, GrowthStat stat, int value)
    {
        string key = Key(unit, stat);
        try
        {
            SaveController.Save(key, value);
            Plugin.Log.LogInfo($"[FixedGrowthRework] Saved {stat} progress for {Describe(unit)}: {value} (key: {key})");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[FixedGrowthRework] ERROR saving {stat} for {Describe(unit)} (key: {key}, value: {value}):\n{ex}");
        }
    }

    internal static void UnitLoaded(Unit u) => LoadProgress(u, true);
    internal static void UnitSaved(Unit u)
    {
        if (u is not PlayerControlledUnit || string.IsNullOrEmpty(u.uniqueUnitId)) return;
        LoadProgress(u, false);
        foreach (GrowthStat s in Stats) SaveOne(u, s, Progress[u.uniqueUnitId][s]);
    }

    private static void LogBegin(PlayerControlledUnit u)
    {
        bool promotion = (u.level == 10 && u.advancedClass == null) || (u.level == 20 && u.exaltedClass == null);
        Plugin.Log.LogInfo("============================================================\n" +
            $"[FixedGrowthRework] LEVEL-UP BEGIN\nUnit: {u.UNIT_NAME_PROP}\nUniqueUnitId: {u.uniqueUnitId}\n" +
            $"Old Level: {u.level - 1}\nNew Level: {u.level}\nFixed Growth: true\nPromotion Level: {promotion}\n" +
            "============================================================");
    }

    private static void LogStat(StatResult r)
    {
        Plugin.Log.LogInfo($"[FixedGrowthRework] STAT: {r.Stat}\n" +
            $"  CurrentStat              = {r.Before}\n  StartingStat             = {r.Starting}\n" +
            $"  BaseGrowth               = {r.BaseGrowth} percent\n  EquipmentGrowthBonus     = {r.EquipmentBonus}\n" +
            $"  EffectiveGrowthThisLevel = {r.EffectiveGrowth}\n  GrowthBonusSources       = {r.Sources}\n\n" +
            $"  ProgressBefore           = {r.ProgressBefore}\n  RawProgressAfterGrowth   = {r.RawProgress}\n" +
            $"  AccumulatorGain          = {r.AccumulatorGain}\n  ProgressAfterModulo      = {r.ProgressAfter}\n\n" +
            $"  EligibleGrowthLevels     = {r.EligibleLevels}\n  PromotionGainTotal       = {r.PromotionGains}\n" +
            $"  BaseCurveExpectedStat    = {r.Expected:F4}\n  CatchupDifference        = {r.Difference:F4}\n" +
            "  CatchupComparison        = ApproximatelyEquals(difference, 0.5, 0.00001) OR difference > 0.5\n" +
            $"  CatchupGain              = {r.CatchupGain}\n\n  FinalGain                = {r.FinalGain}\n" +
            $"  GainReason               = {Reason(r)}\n  StatBefore               = {r.Before}\n" +
            $"  StatAfter                = {r.After}\n  SavedProgress            = {r.ProgressAfter}");
    }

    private static string Reason(StatResult r)
    {
        if (r.AccumulatorGain > 0 && r.CatchupGain > 0) return $"Accumulator + Catchup overlap -> max = {r.FinalGain}";
        if (r.AccumulatorGain > 0) return "Accumulator";
        if (r.CatchupGain > 0) return "Catchup";
        return "none";
    }

    private static void LogSummary(Unit u, List<StatResult> results)
    {
        string message = $"[FixedGrowthRework] LEVEL-UP RESULT ({Describe(u)})";
        foreach (StatResult r in results)
            message += $"\n  {r.Stat,-10}: +{r.FinalGain} | progress {r.ProgressBefore} -> {r.ProgressAfter} [{Reason(r)}]";
        Plugin.Log.LogInfo(message);
    }

    private static void PlayAttributeSound()
    {
        try
        {
            Type type = AccessTools.TypeByName("DarkTonic.MasterAudio.MasterAudio");
            MethodInfo method = null;
            foreach (MethodInfo candidate in type?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
                if (candidate.Name == "PlaySoundAndForget" && candidate.GetParameters().Length == 6) { method = candidate; break; }
            if (method == null) Plugin.Log.LogWarning("[FixedGrowthRework] Could not locate initial level-up audio method.");
            else method.Invoke(null, new object[] { "AttributeIncrease", 1f, null, 0f, null, null });
        }
        catch (Exception ex) { Plugin.Log.LogWarning("[FixedGrowthRework] Could not play initial level-up sound: " + ex); }
    }
}

[HarmonyPatch(typeof(LevelUpUIController), nameof(LevelUpUIController.ProcessLevelUp))]
internal static class ProcessLevelUpPatch
{
    private static bool Prefix(LevelUpUIController __instance, PlayerControlledUnit unit, Action callbackAfterFinished)
        => GrowthSystem.Prefix(__instance, unit, callbackAfterFinished);
}

[HarmonyPatch(typeof(Unit), nameof(Unit.Load))]
internal static class UnitLoadPatch
{
    private static void Postfix(Unit __instance) => GrowthSystem.UnitLoaded(__instance);
}

[HarmonyPatch(typeof(Unit), nameof(Unit.Save))]
internal static class UnitSavePatch
{
    private static void Postfix(Unit __instance) => GrowthSystem.UnitSaved(__instance);
}
