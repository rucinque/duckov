# StatBuffMod

This mod registers a hidden Totem item that grants flat stat bonuses (+50 Max Health, +2.4 Body Armor, +2.4 Head Armor) to the player once equipped.

## How it works
- Uses the dynamic item registration API (`ItemStatsSystem.ItemAssetsCollection.AddDynamicEntry`) to inject a `Totem_StatBuff` prefab at runtime. *(Source: S1)*
- Applies three modifiers with `DisplayNameKey` values `Stat_MaxHealth`, `Stat_BodyArmor`, and `Stat_HeadArmor` to match the base-game schema for flat character stat bonuses. *(Sources: S2, S4, S3 respectively)*
- All modifiers use `Type = 0` / `Target = 2` to represent flat character attribute bonuses (contrasting with `Type = 100` percent modifiers). *(Sources: S2–S5)*

### Verification checklist
1. Equip `Totem_StatBuff` in the Totem slot and open the character panel: Max Health should display **+50**. *(S2)*
2. Inspect the armor values on the character or item detail view: both Body Armor and Head Armor gain **+2.4**. *(S3, S4)*

## Building
1. Open `StatBuffMod.csproj` with the .NET SDK (target framework `netstandard2.1`). *(S1)*
2. Restore references from `Duckov_Data/Managed/` (TeamSoda.*, ItemStatsSystem.dll, UnityEngine* assemblies). *(S1)*
3. Build to produce `StatBuffMod.dll` and copy it into this folder as `StatBuffMod.dll`.

## Deployment
1. Place the entire `StatBuffMod` folder into `.../Escape From Duckov/Duckov_Data/Mods/`.
2. Ensure `StatBuffMod.dll`, `info.ini`, and `preview.png` are present.
3. Launch the game and enable **Stat Buff (+50 HP, +2.4 Body/Head Armor)** from the Mods toggle on the main menu.

## Preview image
Include a 256x256 `preview.png` in this directory (a simple flat-colored square is acceptable if you do not have bespoke art).

---
Place the generated `StatBuffMod` folder in `.../Escape From Duckov/Duckov_Data/Mods/` and enable it from the game's **Mods** menu.
