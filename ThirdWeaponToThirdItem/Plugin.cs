using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ThirdWeaponToThirdItem;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.adminpiland.thosewhorule.thirdweapontothirditem";
    public const string PluginName = "Third Weapon To Third Item";
    public const string PluginVersion = "1.0.2";

    internal static ManualLogSource Log;
    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        try
        {
            InventoryChanges.ValidateGameShape();
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("[ThirdWeaponToThirdItem] Ready: two weapon slots and three item slots are active.");
        }
        catch (Exception exception)
        {
            _harmony?.UnpatchSelf();
            Log.LogError("[ThirdWeaponToThirdItem] Failed to install required patches:\n" + exception);
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}

internal static class InventoryChanges
{
    internal const int WeaponLimit = 2;
    internal const int ItemLimit = 3;

    private static readonly FieldInfo ItemsField =
        AccessTools.Field(typeof(Inventory), "items");

    private static readonly FieldInfo WeaponsField =
        AccessTools.Field(typeof(Inventory), "weapons");

    [ThreadStatic]
    private static int _unitLoadDepth;

    internal static bool IsUnitLoading => _unitLoadDepth > 0;

    internal static void ValidateGameShape()
    {
        if (ItemsField == null || WeaponsField == null)
        {
            throw new MissingFieldException("Inventory.items or Inventory.weapons was not found.");
        }

        RequireMethod(typeof(Inventory), nameof(Inventory.AddItem), typeof(Item));
        RequireMethod(typeof(Inventory), nameof(Inventory.RemoveItem), typeof(Item));
        RequireMethod(typeof(Unit), nameof(Unit.Load), typeof(bool));
        RequireMethod(typeof(UnitInfoUIController), nameof(UnitInfoUIController.BuildUI), typeof(Unit), typeof(bool));
    }

    private static void RequireMethod(Type type, string name, params Type[] parameters)
    {
        if (AccessTools.Method(type, name, parameters) == null)
        {
            throw new MissingMethodException(type.FullName, name);
        }
    }

    internal static Item[] GetItems(Inventory inventory)
    {
        return (Item[])ItemsField.GetValue(inventory);
    }

    internal static Weapon[] GetWeapons(Inventory inventory)
    {
        return (Weapon[])WeaponsField.GetValue(inventory);
    }

    internal static void ExpandItemArray(Inventory inventory)
    {
        Item[] oldItems = GetItems(inventory);
        if (oldItems != null && oldItems.Length >= ItemLimit)
        {
            return;
        }

        var newItems = new Item[ItemLimit];
        if (oldItems != null)
        {
            Array.Copy(oldItems, newItems, Math.Min(oldItems.Length, newItems.Length));
        }

        ItemsField.SetValue(inventory, newItems);
    }

    internal static void AddItem(Inventory inventory, Item item)
    {
        ExpandItemArray(inventory);
        Item[] items = GetItems(inventory);
        for (int index = 0; index < ItemLimit; index++)
        {
            if (items[index] == null)
            {
                items[index] = item;
                return;
            }
        }
    }

    internal static void RemoveItem(Inventory inventory, Item item)
    {
        if (item == null)
        {
            return;
        }

        ExpandItemArray(inventory);
        Item[] items = GetItems(inventory);
        for (int index = 0; index < ItemLimit; index++)
        {
            if (!ReferenceEquals(items[index], item))
            {
                continue;
            }

            for (int shift = index; shift < ItemLimit - 1; shift++)
            {
                items[shift] = items[shift + 1];
            }

            items[ItemLimit - 1] = null;
            return;
        }
    }

    internal static void CopyRowImageSprites(Transform sourceRoot, Transform targetRoot)
    {
        if (sourceRoot == null || targetRoot == null)
        {
            return;
        }

        var sourceImages = new Dictionary<string, Image>();
        foreach (Image image in sourceRoot.GetComponentsInChildren<Image>(true))
        {
            sourceImages[RelativePath(sourceRoot, image.transform)] = image;
        }

        foreach (Image targetImage in targetRoot.GetComponentsInChildren<Image>(true))
        {
            string path = RelativePath(targetRoot, targetImage.transform);
            if (sourceImages.TryGetValue(path, out Image sourceImage))
            {
                targetImage.sprite = sourceImage.sprite;
            }
        }
    }

    internal static void SwapRowLayout(Transform first, Transform second)
    {
        if (first == null || second == null)
        {
            return;
        }

        int firstSiblingIndex = first.GetSiblingIndex();
        int secondSiblingIndex = second.GetSiblingIndex();

        var firstRect = first as RectTransform;
        var secondRect = second as RectTransform;
        if (firstRect != null && secondRect != null)
        {
            Vector2 firstAnchorMin = firstRect.anchorMin;
            Vector2 firstAnchorMax = firstRect.anchorMax;
            Vector2 firstAnchoredPosition = firstRect.anchoredPosition;
            Vector2 firstPivot = firstRect.pivot;
            Vector2 firstSizeDelta = firstRect.sizeDelta;

            firstRect.anchorMin = secondRect.anchorMin;
            firstRect.anchorMax = secondRect.anchorMax;
            firstRect.anchoredPosition = secondRect.anchoredPosition;
            firstRect.pivot = secondRect.pivot;
            firstRect.sizeDelta = secondRect.sizeDelta;

            secondRect.anchorMin = firstAnchorMin;
            secondRect.anchorMax = firstAnchorMax;
            secondRect.anchoredPosition = firstAnchoredPosition;
            secondRect.pivot = firstPivot;
            secondRect.sizeDelta = firstSizeDelta;
        }
        else
        {
            Vector3 firstLocalPosition = first.localPosition;
            first.localPosition = second.localPosition;
            second.localPosition = firstLocalPosition;
        }

        if (first.parent == second.parent)
        {
            first.SetSiblingIndex(secondSiblingIndex);
            second.SetSiblingIndex(firstSiblingIndex);
        }
    }

    private static string RelativePath(Transform root, Transform current)
    {
        string path = "";
        while (current != null && current != root)
        {
            path = string.IsNullOrEmpty(path) ? current.name : current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    internal static void BeginUnitLoad()
    {
        _unitLoadDepth++;
    }

    internal static void EndUnitLoad()
    {
        _unitLoadDepth = Math.Max(0, _unitLoadDepth - 1);
    }

    internal static void FinishUnitLoad(Unit unit, bool loadingMidBattle)
    {
        try
        {
            LoadThirdItem(unit, loadingMidBattle);
            MigrateThirdWeapon(unit);
            unit.RefreshAbilityList();
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                "[ThirdWeaponToThirdItem] Post-load inventory conversion failed for " +
                Describe(unit) + ":\n" + exception);
        }
    }

    internal static void RouteOverflowWeapon(Unit unit, Weapon weapon)
    {
        if (weapon == null)
        {
            return;
        }

        Storage.Instance.Add(weapon);
        Plugin.Log.LogInfo(
            "[ThirdWeaponToThirdItem] Routed overflow weapon " + weapon.GetItemName() +
            " from " + Describe(unit) + " to storage.");
    }

    private static void LoadThirdItem(Unit unit, bool loadingMidBattle)
    {
        Inventory inventory = unit.GetInventory();
        ExpandItemArray(inventory);
        Item[] items = GetItems(inventory);
        if (items[2] != null)
        {
            return;
        }

        string unitKey = GetUnitSaveKey(unit);
        string itemName = SaveController.Load(unitKey + "Item2", "");
        if (string.IsNullOrEmpty(itemName))
        {
            return;
        }

        Item item = Item.GetItemByName<Item>(itemName);
        item.Load(unitKey, 2, loadingMidBattle);
        items[2] = item;

        // Unit.Load has already run its all-abilities load pass by the time this postfix runs.
        // Reproduce that pass for the newly introduced slot in addition to Item.Load above.
        foreach (Ability ability in item.GetAbilities())
        {
            ability.Load(unitKey, loadingMidBattle);
        }

        Plugin.Log.LogDebug(
            "[ThirdWeaponToThirdItem] Loaded Item2 (" + item.GetItemName() + ") for " + Describe(unit) + ".");
    }

    private static void MigrateThirdWeapon(Unit unit)
    {
        // Enemy and allied AI loadouts are not player-managed inventory. In particular, never
        // turn an enemy's legacy equipment into free player storage during a mid-battle load.
        if (!(unit is PlayerControlledUnit))
        {
            return;
        }

        Inventory inventory = unit.GetInventory();
        Weapon[] weapons = GetWeapons(inventory);
        if (weapons == null || weapons.Length < 3 || weapons[2] == null)
        {
            return;
        }

        Weapon overflow = weapons[2];
        bool wasEquipped = unit.GetEquippedWeaponSlot() == 2;
        if (wasEquipped)
        {
            unit.UnequipWeapon(2);
        }

        weapons[2] = null;
        if (wasEquipped && weapons[0] != null)
        {
            unit.EquipWeapon(0);
        }

        Storage.Instance.Add(overflow);

        // Persist both halves immediately. This makes the migration genuinely one-time even
        // if the same save is loaded again before the next ordinary game save.
        Storage.Instance.Save();
        string unitKey = GetUnitSaveKey(unit);
        SaveController.Delete(unitKey + "Weapon2");
        SaveController.Delete(unitKey + "WeaponQuality2");
        SaveController.Save(unitKey + "rightHandItemSlot", unit.GetEquippedWeaponSlot());

        Plugin.Log.LogInfo(
            "[ThirdWeaponToThirdItem] Migrated " + overflow.GetItemName() + " from Weapon2 on " +
            Describe(unit) + " to storage.");
    }

    private static string GetUnitSaveKey(Unit unit)
    {
        string key = unit.uniqueUnitId;
        if (unit is AIControlledUnit)
        {
            key += "-Ch" + ChapterController.chapterIndex;
        }

        return key;
    }

    private static string Describe(Unit unit)
    {
        return unit == null ? "<null>" : unit.uniqueUnitId + " (" + unit.GetType().Name + ")";
    }
}

[HarmonyPatch(typeof(Inventory), MethodType.Constructor)]
internal static class InventoryConstructorPatch
{
    private static void Postfix(Inventory __instance)
    {
        InventoryChanges.ExpandItemArray(__instance);
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.CanEquipWeapon))]
internal static class CanEquipWeaponPatch
{
    private static bool Prefix(Inventory __instance, ref bool __result)
    {
        __result = __instance.GetWeaponCount() < InventoryChanges.WeaponLimit;
        return false;
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.CanEquipItem))]
internal static class CanEquipItemPatch
{
    private static bool Prefix(Inventory __instance, ref bool __result)
    {
        __result = __instance.GetItemCount() < InventoryChanges.ItemLimit;
        return false;
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem))]
internal static class AddItemPatch
{
    private static bool Prefix(Inventory __instance, Item item)
    {
        InventoryChanges.AddItem(__instance, item);
        return false;
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem))]
internal static class RemoveItemPatch
{
    private static bool Prefix(Inventory __instance, Item item)
    {
        InventoryChanges.RemoveItem(__instance, item);
        return false;
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.GetTotalWeight))]
internal static class TotalWeightPatch
{
    private static void Postfix(Inventory __instance, ref int __result)
    {
        Item[] items = InventoryChanges.GetItems(__instance);
        if (items != null && items.Length > 2 && items[2] != null)
        {
            __result += items[2].Weight;
        }
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.AddWeapon))]
internal static class UnitAddWeaponPatch
{
    private static bool Prefix(Unit __instance, Weapon weapon)
    {
        if (!(__instance is PlayerControlledUnit) ||
            InventoryChanges.IsUnitLoading ||
            __instance.GetInventory().GetWeaponCount() < InventoryChanges.WeaponLimit)
        {
            return true;
        }

        InventoryChanges.RouteOverflowWeapon(__instance, weapon);
        return false;
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.Load), new[] { typeof(bool) })]
internal static class UnitLoadPatch
{
    private static void Prefix()
    {
        InventoryChanges.BeginUnitLoad();
    }

    private static void Postfix(Unit __instance, bool loadingMidBattle)
    {
        InventoryChanges.FinishUnitLoad(__instance, loadingMidBattle);
    }

    private static Exception Finalizer(Exception __exception)
    {
        InventoryChanges.EndUnitLoad();
        return __exception;
    }
}

[HarmonyPatch(typeof(UnitInventoryUIController), nameof(UnitInventoryUIController.BuildUI))]
internal static class InventoryUiPatch
{
    private static readonly HashSet<int> SwappedControllers = new HashSet<int>();

    private static void Prefix(UnitInventoryUIController __instance)
    {
        if (__instance.itemRows == null || __instance.weaponRows == null)
        {
            return;
        }

        if (__instance.weaponRows.Length < 3 || __instance.itemRows.Length < 2)
        {
            throw new InvalidOperationException("Unexpected inventory UI row layout.");
        }

        ItemInventoryRow convertedRow = __instance.weaponRows[2];
        InventoryChanges.CopyRowImageSprites(__instance.itemRows[0].transform, convertedRow.transform);

        if (__instance.itemRows.Length == 2)
        {
            __instance.itemRows = new[]
            {
                __instance.itemRows[0],
                __instance.itemRows[1],
                convertedRow
            };
        }

        if (__instance.shieldRow != null && SwappedControllers.Add(__instance.GetInstanceID()))
        {
            InventoryChanges.SwapRowLayout(convertedRow.transform, __instance.shieldRow.transform);
        }
    }
}

[HarmonyPatch(typeof(UnitInfoUIController), "Awake")]
internal static class UnitInfoUiAwakePatch
{
    private static void Postfix(UnitInfoUIController __instance)
    {
        Transform inventory = __instance.transform.Find("UnitInfoPanel/BottomBanner/Inventory");
        if (inventory == null)
        {
            Plugin.Log.LogWarning("[ThirdWeaponToThirdItem] Unit-info inventory hierarchy was not found.");
            return;
        }

        InventoryChanges.CopyRowImageSprites(
            inventory.Find("ItemRow"),
            inventory.Find("WeaponRow2"));

        InventoryChanges.SwapRowLayout(
            inventory.Find("WeaponRow2"),
            inventory.Find("ShieldRow"));
    }
}

[HarmonyPatch(typeof(UnitInfoUIController), nameof(UnitInfoUIController.BuildUI))]
internal static class UnitInfoUiPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo getWeapon = AccessTools.Method(typeof(Unit), nameof(Unit.GetWeapon), new[] { typeof(int) });
        MethodInfo getThirdItem = AccessTools.Method(typeof(UnitInfoUiPatch), nameof(GetThirdItem));
        int replacements = 0;

        for (int index = 1; index < codes.Count; index++)
        {
            if (!LoadsIntegerTwo(codes[index - 1]) || !codes[index].Calls(getWeapon))
            {
                continue;
            }

            // GetWeapon is an instance method taking (Unit, int) on the evaluation stack;
            // GetThirdItem is static and takes only Unit, so discard the old slot argument.
            codes[index - 1].opcode = OpCodes.Nop;
            codes[index - 1].operand = null;
            codes[index].opcode = OpCodes.Call;
            codes[index].operand = getThirdItem;
            replacements++;
        }

        if (replacements != 1)
        {
            throw new InvalidOperationException(
                "Expected one UnitInfo Weapon2 display call, found " + replacements + ".");
        }

        return codes;
    }

    private static Item GetThirdItem(Unit unit)
    {
        return unit.GetItem(2);
    }

    private static bool LoadsIntegerTwo(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldc_I4_2 ||
               (instruction.opcode == OpCodes.Ldc_I4 && Equals(instruction.operand, 2)) ||
               (instruction.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.operand) == 2);
    }
}

[HarmonyPatch(typeof(ChapterController), nameof(ChapterController.AddDroppableItem), new[] { typeof(string), typeof(Item) })]
internal static class DroppableItemPatch
{
    private static bool Prefix(string unitName, Item item)
    {
        if (!ChapterController.enemyUnits.ContainsKey(unitName))
        {
            return false;
        }

        Unit unit = ChapterController.enemyUnits[unitName];
        item.isDroppable = true;
        for (int index = 0; index < InventoryChanges.ItemLimit; index++)
        {
            Item existing = unit.GetItem(index);
            if (existing != null && existing.GetType() == item.GetType())
            {
                unit.SwapItem(item, index);
                unit.SetDroppableItems(true);
                return false;
            }
        }

        unit.AddItem(item);
        unit.SetDroppableItems(true);
        return false;
    }
}
