# Those Who Rule Fan Mods

A collection of quality-of-life, gameplay, tutorial, and story mods for **Those Who Rule**, along with a simple Windows mod loader for players who would rather not install and manage BepInEx manually.

This release includes:

* **Those Who Rule Mod Loader**
* **Double Iron Sword Refinements**
* **Deselect Main Characters**
* **Alternative Fixed Growth Rings**
* **Jeycii's Fan Final Battle Sound-Offs**
* **Jeycii's Fan Epilogues**
* **Third Weapon Slot to Item Slot**
* **All Promotions Possible**

---

## Installation

### Recommended: Those Who Rule Mod Loader

The **Those Who Rule Mod Loader** is the easiest way to install and manage these mods on Windows.

1. Download and unzip the Mod Loader anywhere you like.
2. Run `Those Who Rule Mod Loader.exe`.
3. Let the loader install the correct version of BepInEx into the game directory.
4. Place downloaded mod `.dll` files into the loader's mods folder.
5. Check or uncheck mods in the loader to install or remove them from the game.

The Mod Loader is currently **Windows only**. Sorry, pals.

### Manual Installation

All gameplay and story mods can also be installed manually.

1. Install **BepInEx 5** in the `ThoseWhoRule/build` game directory.
2. Place the desired mod `.dll` files into the BepInEx `plugins` folder.
3. Launch the game normally.

Unless otherwise noted, the individual mods require either:

* **BepInEx 5**, or
* the **Those Who Rule Mod Loader**

---

# Included Mods

## Those Who Rule Mod Loader

A simple graphical mod loader intended to make **using** Those Who Rule mods easier without requiring players to understand the underlying BepInEx setup.

### Features

* Installs the correct version of BepInEx in the correct game directory.
* Detects compatible `.dll` files placed in its mods folder.
* Allows individual mods to be enabled or disabled with a checkbox.
* Copies enabled mods into the game and retracts disabled mods automatically.

### Requirements

None.

The loader handles the BepInEx installation itself.

---

## Double Iron Sword Refinements

**Tutorial Mod**

Increases early-game customization by allowing the basic **Iron Sword** to use two refinement slots.

This was originally featured as part of the *Those Who Rule* Wiki modding tutorial by **Sigismund's Wrath**. Thanks for the help in getting started.

### Features

* Increases the Iron Sword's refinement slots to two.
* Provides a simple practical example of modifying weapons and other game items.
* Source/plugin files are included as a reference for aspiring modders.

---

## Deselect Main Characters

One of the more frequently requested challenge-run features is the ability to play without relying on the game's three main characters.

This mod allows **Slyker, Marcus, and Illyana** to be deselected while also accounting for associated defeat conditions and quest behavior.

### Features

* Allows Slyker, Marcus, and Illyana to be deselected during battle preparation.
* Removes the special deployment protection normally applied to the main three.
* Changes chapter defeat conditions so that defeat occurs when **all player units are dead**, rather than when a protected main character falls.
* Includes additional handling for quests and events involving the main characters.

Mod idea from **Aven555**

---

## Alternative Fixed Growth Rings

> **Save Compatibility Warning**
>
> This mod is strongly recommended for **fresh saves or saves around chapter 3** because it adds persistent stat-growth information to the save structure.
>
> It will attempt to initialize correctly on later saves, but perfect results cannot be guaranteed.

In the base game's **Fixed Growth** mode, stat increases are determined by comparing a character's current stat against an expected value.

Growth rings increase that expected value while equipped. This creates an unintuitive side effect: a character may receive better level-ups while wearing a ring, but after removing it can then receive worse-than-normal level-ups until their stats fall back in line with the original expected curve.

This mod changes how growth-ring bonuses are handled.

Instead of temporarily changing the character's expected stat curve, ring bonuses contribute to a persistent per-stat growth accumulator. While a ring is equipped, growth accumulates more quickly. When the ring is removed, growth simply returns to its normal rate.

As a result, using a growth ring for several levels remains a permanent positive rather than creating weaker corrective levels afterward.

### Late-Joining Character Handling

Some later-game characters appear to intentionally join below their expected statistical curve so that the base game's fixed-growth system gives them unusually strong early level-ups.

To preserve this behavior, the mod retains the game's normal catch-up system while preventing growth rings from modifying the underlying expected-stat calculation.

This allows:

* intentionally under-statted characters to retain their intended catch-up growth;
* normal characters to follow predictable fixed growth;
* growth-ring bonuses to remain beneficial regardless of when the ring is equipped or removed.

### Features

* Tracks persistent growth progress independently for every character stat.
* Adds normal growth and equipment growth bonuses to the accumulator each level.
* Preserves unused growth progress between levels.
* Prevents growth rings from temporarily distorting the base expected-stat curve.
* Retains the game's intended catch-up behavior for characters who join below their expected stats.

In testing, early-game characters without growth rings follow the same practical growth behavior as the base game.

---

## Jeycii's Fan Final Battle Sound-Offs

Adds additional character moments to the final battlefield by giving surviving characters their own sound-offs during the concluding battle.

The goal is to give the full cast a little more presence and emotional resolution as the story approaches its ending.

### Features

* Adds additional dialogue from surviving characters during the final battle.
* Expands the presence of secondary characters during the game's climax.
* Includes source/plugin files demonstrating how to modify existing Pixel Crushers conversations in *Those Who Rule*.

The dialogue scripts were written by **Jeycii**.

---

## Jeycii's Fan Epilogues

A full in-game implementation of **Jeycii's fan-made epilogues**, originally shared with the official *Those Who Rule* Discord community.

The mod adds postgame endings for the game's cast, including both **single-character** and **paired** epilogues.

Only characters who survive through the final battle receive their applicable endings.

The epilogue sequence plays near the end of the game before the closing narration.

### Features

* Adds fan-written epilogues for the playable cast.
* Supports both single-character and paired endings.
* Determines applicable endings based on surviving characters.
* Integrates the new sequence directly into both ending routes.
* Includes source/plugin files demonstrating how to create and insert entirely new Pixel Crushers conversations into the game.

The epilogue scripts were written by **Jeycii**.

---

## Third Weapon Slot to Item Slot

Converts the character's **third weapon slot into an additional item slot**, giving units more flexibility for carrying consumables and utility items at the cost of one weapon slot.

> **Save Compatibility Warning**
>
> This mod changes how inventory slots are interpreted and can potentially cause serious save or inventory problems if you repeatedly switch between playing **with the mod enabled and disabled** on the same save.
>
> The mod includes error-checking intended to prevent accidental item loss where possible, but it cannot guarantee that every inventory state will remain safe across repeated enable/disable cycles. Back up your save before first using the mod, and avoid toggling it on and off once you have begun using the converted slot.
>
> Use on important saves at your own risk.

### Features

* Converts the third weapon slot into an additional item slot.
* Allows characters to carry more consumables or utility items.
* Includes safeguards intended to detect problematic inventory states and reduce the risk of accidental item loss.
* Preserves the normal behavior of the remaining weapon and item slots.

Mod idea from **Delong**.

---

## All Promotions Possible

Removes normal promotion restrictions and allows **any unit to promote into any available class** at the appropriate level.

### Features

* At **level 10**, any unit can promote into any of the **14 Advanced Classes**.
* At **level 20**, any unit can promote into any of the **14 Exalted Classes**.
* Opens up substantially more freedom for unusual builds, challenge runs, and character experimentation.

---

# For Modders

Several of these projects intentionally include their source/plugin file

---

# Disclaimer

These mods and tools are unofficial fan-made software and are provided **“as is” without warranty of any kind**, express or implied. Use them entirely at your own risk. Modifying game files or save data may cause crashes, corrupted saves, unexpected game behavior, incompatibilities with future game updates or other mods, loss of progress, or other unintended effects. Users are strongly encouraged to back up important save files before installing or using any mod. The author makes no guarantee of compatibility, continued support, error-free operation, or preservation of user data and, to the fullest extent permitted by applicable law, assumes no responsibility or liability for any damage, data loss, software issues, hardware issues, lost progress, or other consequences arising from the installation, use, misuse, modification, or removal of these mods or tools. These projects are not affiliated with, endorsed by, or officially supported by the developers or publishers of *Those Who Rule*.
---

# For Modders

Several of these projects intentionally include their source/plugin file
