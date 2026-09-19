using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace NoMainThree;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.adminpiland.thosewhorule.nomainthree";
    public const string PluginName = "No Main Three";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Log;

    private Harmony _harmony;
    private int _patchFailures;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo("[NoMainThree] Loading patches...");

        _harmony = new Harmony(PluginGuid);

        InstallMajorPatch(
            "Main-three deselection",
            AccessTools.Method(typeof(UnitSelectionRow), nameof(UnitSelectionRow.SelectUnit)),
            prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.SelectUnitPrefix)));

        InstallMajorPatch(
            "IsMainCharacter",
            AccessTools.Method(typeof(UnitSelectionRow), nameof(UnitSelectionRow.IsMainCharacter)),
            prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.IsMainCharacterPrefix)));

        InstallMajorPatch(
            "Battle preparation priority",
            AccessTools.Method(
                typeof(BattlePreparationUIController),
                nameof(BattlePreparationUIController.GetUnitSortedScore),
                new[] { typeof(Unit) }),
            transpiler: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.SortPriorityTranspiler)));

        InstallPlayerDefeatPatches();
        InstallUndeployedCharacterCompatibilityPatches();

        if (_patchFailures == 0)
        {
            Log.LogInfo("[NoMainThree] Ready.");
        }
        else
        {
            Log.LogError(
                "[NoMainThree] Patch installation completed with " + _patchFailures +
                " failed major patch group(s); the plugin is not fully active.");
        }
    }

    private void InstallPlayerDefeatPatches()
    {
        try
        {
            PatchRequired(
                AccessTools.Method(typeof(PlayerUnitDeath), nameof(PlayerUnitDeath.HasTriggered)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.PlayerUnitDeathHasTriggeredPrefix)));

            PatchRequired(
                AccessTools.Method(typeof(PlayerUnitDeath), nameof(PlayerUnitDeath.GetDescription)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.PlayerUnitDeathGetDescriptionPrefix)));

            // ChapterController.Update normally stops checking objectives when playerUnits is
            // empty unless the list literally contains AllPlayerUnitsDead. PlayerUnitDeath is
            // redirected to that condition above, so this guard must recognize it too or the
            // final unit's removal would prevent the normal game-over path from running.
            PatchRequired(
                AccessTools.Method(typeof(ChapterController), "Update"),
                transpiler: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.ChapterUpdateTranspiler)));

            Log.LogInfo("[NoMainThree] Player defeat-condition patches installed.");
        }
        catch (Exception exception)
        {
            _patchFailures++;
            Log.LogError("[NoMainThree] Player defeat-condition patches FAILED. " + exception);
        }
    }

    private void InstallUndeployedCharacterCompatibilityPatches()
    {
        try
        {
            PatchRequired(
                AccessTools.Method(typeof(Chapter3Controller), "InstantiateBattle"),
                transpiler: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.Chapter3BattleTranspiler)));

            PatchRequired(
                AccessTools.Method(typeof(Chapter4Controller), "InstantiateBattle"),
                transpiler: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.UnitObjectiveLookupTranspiler)));

            PatchRequired(
                AccessTools.Method(typeof(ChapterController), nameof(ChapterController.AddOptionalObjective)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.AddOptionalObjectivePrefix)));

            PatchRequired(
                AccessTools.Method(typeof(MarcusFirstKillEvent), nameof(MarcusFirstKillEvent.ConditionsMet)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.MarcusEventConditionsPrefix)));

            PatchRequired(
                AccessTools.Method(typeof(SlykerFirstBloodEvent), nameof(SlykerFirstBloodEvent.ConditionsMet)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.SlykerEventConditionsPrefix)));

            PatchRequired(
                AccessTools.Method(typeof(SlykerOverkillEvent), nameof(SlykerOverkillEvent.ConditionsMet)),
                prefix: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.SlykerEventConditionsPrefix)));

            foreach (Type eventType in new[]
                     {
                         typeof(Chapter8Reinforcements),
                         typeof(Chapter16ReinforcementEventWave2),
                         typeof(Chapter19ReinforcementEventWave1),
                         typeof(SlykerOverkillEvent)
                     })
            {
                MethodInfo executeEvent = AccessTools.Method(eventType, "ExecuteEvent");
                PatchRequired(
                    AccessTools.EnumeratorMoveNext(executeEvent),
                    transpiler: AccessTools.Method(typeof(PatchMethods), nameof(PatchMethods.EventCameraTranspiler)));
            }

            Log.LogInfo("[NoMainThree] Undeployed-character compatibility patches installed.");
        }
        catch (Exception exception)
        {
            _patchFailures++;
            Log.LogError("[NoMainThree] Undeployed-character compatibility patches FAILED. " + exception);
        }
    }

    private void InstallMajorPatch(string name, MethodBase original, MethodInfo prefix = null, MethodInfo transpiler = null)
    {
        try
        {
            PatchRequired(original, prefix, transpiler);
            Log.LogInfo("[NoMainThree] " + name + " patch installed.");
        }
        catch (Exception exception)
        {
            _patchFailures++;
            Log.LogError("[NoMainThree] " + name + " patch FAILED. " + exception);
        }
    }

    private void PatchRequired(MethodBase original, MethodInfo prefix = null, MethodInfo transpiler = null)
    {
        if (original == null)
        {
            throw new MissingMethodException("Required game method was not found.");
        }

        if (prefix == null && transpiler == null)
        {
            throw new MissingMethodException("Required patch method was not found.");
        }

        _harmony.Patch(
            original,
            prefix == null ? null : new HarmonyMethod(prefix),
            transpiler: transpiler == null ? null : new HarmonyMethod(transpiler));
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}

internal static class PatchMethods
{
    private static readonly FieldInfo PlayerUnitsField =
        AccessTools.Field(typeof(ChapterController), nameof(ChapterController.playerUnits));

    private static readonly FieldInfo DefeatObjectiveUnitField =
        AccessTools.Field(typeof(DefeatXUnitsObjective), "unit");

    private static readonly FieldInfo DoNotDefeatObjectiveUnitField =
        AccessTools.Field(typeof(DoNotDefeatXUnitsObjective), "unit");

    private static readonly Type[] PriorityTypes =
    {
        typeof(Slyker),
        typeof(Marcus),
        typeof(Illyana),
        typeof(August),
        typeof(Crawford),
        typeof(Ophelia),
        typeof(Rorick),
        typeof(Russel)
    };

    private static readonly int[] PriorityWeights = { 50, 45, 40, 135, 130, 125, 135, 130 };

    internal static bool SelectUnitPrefix(
        UnitSelectionRow __instance,
        Unit ___unit,
        Action ___onClickCallback)
    {
        if (!__instance.selected ||
            !(___unit is Slyker || ___unit is Illyana || ___unit is Marcus))
        {
            return true;
        }

        // Vanilla refuses this exact case before reaching its ordinary deselection branch.
        // Perform only that branch here; all selection, ordinary deselection, capacity, and
        // unrelated error handling continues through the original method.
        BattlePreparationUIController.DisableUnit(___unit);
        __instance.SetUnselected();
        ___onClickCallback?.Invoke();
        return false;
    }

    internal static bool IsMainCharacterPrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    internal static IEnumerable<CodeInstruction> SortPriorityTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int replacements = 0;

        for (int typeIndex = 0; typeIndex < PriorityTypes.Length; typeIndex++)
        {
            Type priorityType = PriorityTypes[typeIndex];
            int expectedWeight = PriorityWeights[typeIndex];
            int typeTestIndex = codes.FindIndex(instruction =>
                instruction.opcode == OpCodes.Isinst && Equals(instruction.operand, priorityType));

            if (typeTestIndex < 0)
            {
                continue;
            }

            // Each named branch has the semantic form: isinst Type; ...; ldc weight; sub.
            // Limit the scan to this branch so the deployed -100 and final -level operations
            // cannot be mistaken for one of the eight requested priority adjustments.
            int scanEnd = Math.Min(typeTestIndex + 12, codes.Count - 1);
            for (int index = typeTestIndex + 1; index <= scanEnd; index++)
            {
                if (codes[index].opcode != OpCodes.Sub || index == 0)
                {
                    continue;
                }

                if (!TryGetLoadedInt(codes[index - 1], out int loadedWeight) || loadedWeight != expectedWeight)
                {
                    break;
                }

                codes[index - 1].opcode = OpCodes.Ldc_I4_0;
                codes[index - 1].operand = null;
                replacements++;
                break;
            }
        }

        if (replacements == PriorityTypes.Length)
        {
            Plugin.Log.LogInfo("[NoMainThree] Removed 8 battle-preparation priority adjustments.");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[NoMainThree] Expected 8 battle-preparation priority adjustments, but removed " +
                replacements + ".");
            throw new InvalidOperationException("Battle-preparation priority IL pattern count did not match.");
        }

        return codes;
    }

    internal static bool PlayerUnitDeathHasTriggeredPrefix(ref bool __result)
    {
        // Reuse the developers' condition verbatim. Individual unit death/removal/save code is
        // deliberately not patched, including all permadeath and Brutal-mode behavior.
        __result = new AllPlayerUnitsDead().HasTriggered();
        return false;
    }

    internal static bool PlayerUnitDeathGetDescriptionPrefix(ref string __result)
    {
        __result = new AllPlayerUnitsDead().GetDescription();
        return false;
    }

    internal static IEnumerable<CodeInstruction> ChapterUpdateTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo replacement = AccessTools.Method(
            typeof(PatchMethods),
            nameof(ContainsAllPlayerUnitsDeadSemantics));
        int replacements = 0;

        for (int index = 0; index < codes.Count - 1; index++)
        {
            if (!IsEnumerableGenericCall(codes[index], nameof(Enumerable.OfType), typeof(AllPlayerUnitsDead)) ||
                !IsEnumerableGenericCall(codes[index + 1], nameof(Enumerable.Any), typeof(AllPlayerUnitsDead)))
            {
                continue;
            }

            // The List<DefeatCondition> already on the stack is consumed by our predicate.
            // The following Any call becomes unnecessary; the resulting bool feeds the same
            // vanilla branch that permits objective checks with an empty player dictionary.
            codes[index].opcode = OpCodes.Call;
            codes[index].operand = replacement;
            codes[index + 1].opcode = OpCodes.Nop;
            codes[index + 1].operand = null;
            replacements++;
        }

        if (replacements == 1)
        {
            Plugin.Log.LogInfo("[NoMainThree] Updated the empty-player-roster defeat guard.");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[NoMainThree] Expected 1 empty-player-roster defeat guard, but updated " +
                replacements + ".");
            throw new InvalidOperationException("Empty-player-roster defeat guard IL pattern count did not match.");
        }

        return codes;
    }

    internal static IEnumerable<CodeInstruction> Chapter3BattleTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(UnitObjectiveLookupTranspiler(instructions));
        MethodInfo setRotation30 = AccessTools.Method(typeof(Unit), "SetRotation30");
        MethodInfo setRotation330 = AccessTools.Method(typeof(Unit), "SetRotation330");
        MethodInfo safeRotation30 = AccessTools.Method(typeof(PatchMethods), nameof(TrySetRotation30));
        MethodInfo safeRotation330 = AccessTools.Method(typeof(PatchMethods), nameof(TrySetRotation330));
        int replacements = 0;

        for (int index = 4; index < codes.Count - 1; index++)
        {
            if (!IsPlayerUnitDictionaryLookup(codes, index))
            {
                continue;
            }

            MethodInfo replacement;
            if (codes[index + 1].Calls(setRotation30))
            {
                replacement = safeRotation30;
            }
            else if (codes[index + 1].Calls(setRotation330))
            {
                replacement = safeRotation330;
            }
            else
            {
                continue;
            }

            // Preserve the dictionary and key on the stack, then let the helper perform a
            // conditional lookup and the same rotation only when that unit was deployed.
            codes[index].opcode = OpCodes.Nop;
            codes[index].operand = null;
            codes[index + 1].opcode = OpCodes.Call;
            codes[index + 1].operand = replacement;
            replacements++;
        }

        if (replacements == 8)
        {
            Plugin.Log.LogInfo("[NoMainThree] Made 8 Chapter 3 starting-unit rotations deployment-safe.");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[NoMainThree] Expected 8 Chapter 3 starting-unit rotations, but updated " +
                replacements + ".");
            throw new InvalidOperationException("Chapter 3 starting-unit rotation IL pattern count did not match.");
        }

        return codes;
    }

    internal static IEnumerable<CodeInstruction> UnitObjectiveLookupTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo safeLookup = AccessTools.Method(typeof(PatchMethods), nameof(GetUnitOrNull));
        int replacements = 0;

        for (int index = 4; index < codes.Count - 3; index++)
        {
            if (!IsPlayerUnitDictionaryLookup(codes, index) ||
                codes[index + 3].opcode != OpCodes.Newobj ||
                !(codes[index + 3].operand is ConstructorInfo constructor) ||
                (constructor.DeclaringType != typeof(DefeatXUnitsObjective) &&
                 constructor.DeclaringType != typeof(DoNotDefeatXUnitsObjective)))
            {
                continue;
            }

            // These constructors tolerate null until the objective is displayed. The
            // AddOptionalObjective prefix below omits exactly these unavailable objectives.
            codes[index].opcode = OpCodes.Call;
            codes[index].operand = safeLookup;
            replacements++;
        }

        if (replacements == 1)
        {
            Plugin.Log.LogInfo("[NoMainThree] Made a protagonist-specific optional objective deployment-safe.");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[NoMainThree] Expected 1 protagonist-specific optional objective lookup in this method, but updated " +
                replacements + ".");
            throw new InvalidOperationException("Unit-specific optional-objective IL pattern count did not match.");
        }

        return codes;
    }

    internal static bool AddOptionalObjectivePrefix(OptionalObjective objective)
    {
        if (objective is DefeatXUnitsObjective && DefeatObjectiveUnitField.GetValue(objective) == null)
        {
            Plugin.Log.LogInfo("[NoMainThree] Skipped an unavailable Marcus-specific optional objective.");
            return false;
        }

        if (objective is DoNotDefeatXUnitsObjective && DoNotDefeatObjectiveUnitField.GetValue(objective) == null)
        {
            Plugin.Log.LogInfo("[NoMainThree] Skipped an unavailable Illyana-specific optional objective.");
            return false;
        }

        return true;
    }

    internal static bool MarcusEventConditionsPrefix(ref bool __result)
    {
        return RequireDeployedUnit(Marcus.UNIT_NAME + "0", ref __result);
    }

    internal static bool SlykerEventConditionsPrefix(ref bool __result)
    {
        return RequireDeployedUnit(Slyker.UNIT_NAME + "0", ref __result);
    }

    internal static IEnumerable<CodeInstruction> EventCameraTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo getTransform = AccessTools.PropertyGetter(typeof(UnityEngine.Component), "transform");
        MethodInfo getPosition = AccessTools.PropertyGetter(typeof(UnityEngine.Transform), "position");
        MethodInfo moveCamera = AccessTools.Method(typeof(CameraController), nameof(CameraController.MoveCamera));
        MethodInfo safeMoveCamera = AccessTools.Method(typeof(PatchMethods), nameof(MoveCameraToUnitIfPresent));
        int replacements = 0;

        for (int index = 4; index < codes.Count - 3; index++)
        {
            if (!IsPlayerUnitDictionaryLookup(codes, index) ||
                !codes[index + 1].Calls(getTransform) ||
                !codes[index + 2].Calls(getPosition) ||
                !codes[index + 3].Calls(moveCamera))
            {
                continue;
            }

            codes[index].opcode = OpCodes.Nop;
            codes[index].operand = null;
            codes[index + 1].opcode = OpCodes.Nop;
            codes[index + 1].operand = null;
            codes[index + 2].opcode = OpCodes.Nop;
            codes[index + 2].operand = null;
            codes[index + 3].opcode = OpCodes.Call;
            codes[index + 3].operand = safeMoveCamera;
            replacements++;
        }

        if (replacements == 1)
        {
            Plugin.Log.LogInfo(
                "[NoMainThree] Made protagonist camera lookup deployment-safe in " +
                __originalMethod.DeclaringType?.FullName + ".");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[NoMainThree] Expected 1 protagonist camera lookup in " +
                __originalMethod.DeclaringType?.FullName + ", but updated " + replacements + ".");
            throw new InvalidOperationException("Protagonist camera IL pattern count did not match.");
        }

        return codes;
    }

    internal static Unit GetUnitOrNull(Dictionary<string, Unit> units, string unitId)
    {
        return units != null && units.TryGetValue(unitId, out Unit unit) ? unit : null;
    }

    internal static void TrySetRotation30(Dictionary<string, Unit> units, string unitId)
    {
        GetUnitOrNull(units, unitId)?.SetRotation30();
    }

    internal static void TrySetRotation330(Dictionary<string, Unit> units, string unitId)
    {
        GetUnitOrNull(units, unitId)?.SetRotation330();
    }

    internal static void MoveCameraToUnitIfPresent(Dictionary<string, Unit> units, string unitId)
    {
        Unit unit = GetUnitOrNull(units, unitId);
        if (unit != null)
        {
            CameraController.MoveCamera(unit.transform.position);
        }
        else if (units != null)
        {
            foreach (Unit fallback in units.Values)
            {
                if (fallback != null && fallback.currentVitalityAttr > 0)
                {
                    CameraController.MoveCamera(fallback.transform.position);
                    break;
                }
            }
        }
    }

    internal static bool ContainsAllPlayerUnitsDeadSemantics(List<DefeatCondition> conditions)
    {
        if (conditions == null)
        {
            return false;
        }

        foreach (DefeatCondition condition in conditions)
        {
            if (condition is AllPlayerUnitsDead || condition is PlayerUnitDeath)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequireDeployedUnit(string unitId, ref bool result)
    {
        if (ChapterController.playerUnits != null && ChapterController.playerUnits.ContainsKey(unitId))
        {
            return true;
        }

        result = false;
        return false;
    }

    private static bool IsPlayerUnitDictionaryLookup(List<CodeInstruction> codes, int lookupIndex)
    {
        if (lookupIndex < 4 || !codes[lookupIndex - 4].LoadsField(PlayerUnitsField) ||
            !(codes[lookupIndex].operand is MethodInfo method) || method.Name != "get_Item" ||
            !method.DeclaringType.IsGenericType)
        {
            return false;
        }

        Type[] arguments = method.DeclaringType.GetGenericArguments();
        return arguments.Length == 2 && arguments[0] == typeof(string) && arguments[1] == typeof(Unit);
    }

    private static bool IsEnumerableGenericCall(CodeInstruction instruction, string name, Type genericType)
    {
        if (instruction.opcode != OpCodes.Call || !(instruction.operand is MethodInfo method) ||
            method.DeclaringType != typeof(Enumerable) || method.Name != name || !method.IsGenericMethod)
        {
            return false;
        }

        Type[] arguments = method.GetGenericArguments();
        return arguments.Length == 1 && arguments[0] == genericType;
    }

    private static bool TryGetLoadedInt(CodeInstruction instruction, out int value)
    {
        if (instruction.opcode == OpCodes.Ldc_I4)
        {
            value = (int)instruction.operand;
            return true;
        }

        if (instruction.opcode == OpCodes.Ldc_I4_S)
        {
            value = Convert.ToInt32(instruction.operand);
            return true;
        }

        if (instruction.opcode == OpCodes.Ldc_I4_0)
        {
            value = 0;
            return true;
        }

        value = 0;
        return false;
    }
}
