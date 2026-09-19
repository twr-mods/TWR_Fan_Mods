using System;
using BepInEx;

namespace IronSwordTwoRefs;

[BepInPlugin(
    MyPluginInfo.PLUGIN_GUID,
    MyPluginInfo.PLUGIN_NAME,
    MyPluginInfo.PLUGIN_VERSION
)]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        try
        {
            EnableIronSwordSecondRefinement();
        }
        catch (Exception exception)
        {
            Logger.LogError(
                $"Failed to modify Iron Sword: {exception}"
            );
        }
    }

    private void EnableIronSwordSecondRefinement()
    {
        if (!WeaponConstants.WeaponData.TryGetValue(
                "IronSword",
                out var ironSword))
        {
            Logger.LogError(
                "Could not find IronSword in WeaponConstants.WeaponData."
            );

            return;
        }

        bool wasAlreadyEnabled = ironSword.Rest.Item1;

        WeaponConstants.WeaponData["IronSword"] =
            new ValueTuple<
                int,
                int,
                int,
                int,
                int,
                WeaponExperience.WeaponRank,
                bool,
                ValueTuple<bool>
            >(
                ironSword.Item1,
                ironSword.Item2,
                ironSword.Item3,
                ironSword.Item4,
                ironSword.Item5,
                ironSword.Item6,
                ironSword.Item7,
                new ValueTuple<bool>(true)
            );

        if (wasAlreadyEnabled)
        {
            Logger.LogInfo(
                "Iron Sword's second refinement slot was already enabled."
            );
        }
        else
        {
            Logger.LogInfo(
                "Enabled Iron Sword's second refinement slot."
            );
        }
    }
}