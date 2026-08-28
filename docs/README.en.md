# Sephiria Backpack Organizer

<p align="center"><font size="6"><b>🔥 F8 auto-sort, middle-click custom priority!!! 🔥</b></font></p>

🌏 **Language / 语言**：[English](README.en.md) · [中文](README.md) · [한국어](README.ko.md)

![Downloads](https://img.shields.io/github/downloads/Infinite-Heaven/SephiriaBackpackOrganizer/total?label=Downloads&color=2f6fad)
![往期下载量合计](https://img.shields.io/badge/%E5%BE%80%E6%9C%9F%E4%B8%8B%E8%BD%BD%E9%87%8F%E5%90%88%E8%AE%A1-1762-2f6fad)

A BepInEx plugin for *Sephiria* (Steam AppID 2436940). Press **F8** to auto-arrange your inventory so all the synergy mechanics trigger at once: tablet coverage, charm position conditions, planet clusters, harmony crystals, dedication badges, the Kelsardanni Key cycle rows, compass original-target binding, white-paper combo filling and the glowing hourglass.

- Version: v2.5.1
- Bug fix: manually prioritized artifacts are no longer incorrectly rolled back when the native game score drops; P1/P2/P3 now contribute 6000/5800/5600 per level, with matching penalties for negative levels
- Runtime: BepInEx 6 (Unity Mono) / Unity 6000.3.21f1 / Mirror multiplayer
- Works for solo, host and multiplayer clients

## Screenshots

<p align="center">
  <img src="../screenshots/before.png" width="49%" alt="Before"/>
  <img src="../screenshots/after.png" width="49%" alt="After"/>
</p>

Press **F8** to arrange your bag: before on the left, after on the right (install instructions below).

## Features

- **One-key arrangement**: press F8 to re-arrange the bag; a "done" toast appears when finished
- **Manual priority**: middle-click an artifact to toggle priority; the latest selection is P1, followed by P2/P3, with a small transparent marker at the lower-left of its icon
- **Background search + frame-sliced apply**: host and clients search in the background, then apply swaps/rotations in validated per-frame batches with safe rollback
- **Fully offline scoring model**: simulates the game's real bonus formulas on a copy of your bag — any layout gets a score, and the search optimizes that score
- **Multi-round simulated annealing**: 4 independent search rounds (different random seeds) per press, taking the global best; a full 34-slot bag takes about 200–300 ms
- **Smart initial layout**: tablets → restricted charms → cyclic/fixed-row charms → planet module → harmony crystals → dedication badge → hourglass → planet clustering → compass original-target binding → white-paper combo filling → remaining charms → burdens into the worst cells
- **Supported mechanics** (all mirror the game's real formulas):
  - Tablet effect grids (+level / ×multiplier / disable / ignore-criteria cells) with rotation
  - Charm position conditions (top / bottom / sides / inside / outlined / both sides empty / both sides charm / all neighbors full / near a magic book)
  - Ignore-criteria cells first (Ice Lock style)
  - Planet telescope clusters (8 surrounding cells; excludable items such as Galaxy Sheet Music)
  - Harmony crystal: damage scales with the level-sum of the 8 surrounding cells
  - Dedication badge: strengthens all companion items on the same row
  - **Kelsardanni Key**: counts the existing STURDY / EMBER / GLACIER / MAGITECH synergies and picks the most numerous one; rows 1/2/3/4 map to the four types and repeat every 4 rows, so STURDY picks the best cell among rows 1/5/9
  - **Compass original-target binding**: a compass that already points at a damage item before sorting keeps following the same item instance and stays right below it; stacked compasses move as one vertical chain; only unpaired compasses auto-seek a target
  - **Golden-needle target gets maxed first**: the artifact locked by the north-pointing golden needle is forced to top priority and receives levels first, regardless of rarity
  - **White-paper combo filling**: counts each combo's current count vs. its max tier, prefers the largest combo that is not full yet, and places the white paper between two items of that combo — e.g. auto-fills STURDY 9/10 → 10/10
  - **Glowing hourglass: automatically placed left of the magic book with the longest cooldown** (the magic book on its right gains +30/60/100% cooldown recovery)
  - **Multipurpose Belt support**: when the belt is active, its effect stacks once per artifact in the top row — the sorter packs as many artifacts as possible into the top row
  - **Low-level-value charms**: Shadow Eye and Lizard Plate Armor gain almost nothing from levels — their level score is discounted so they only need to stay active (level ≥ 0) without chasing high levels; the Fault Detection Probe is simply demoted to normal priority (level 4)
  - **Minimum-level targets**: Galaxy Sheet Music is forced to top priority and guaranteed to reach effective level 2
  - Negative items like Mind Burden are forced into the worst (negative-level) cells
  - Mystic ×2 cells (unlocked at 2 / 5 mystic items)
  - Configurable fixed-row items
  - Weapon-related charms (activated only when the current weapon type matches)
  - Enchant levels and a priority system (Legend/Bond > Rare > Advanced > Common)
- **Multiplayer client support**: without server authority, the plugin computes the optimal layout locally, then applies it through the game's own network ops (Swap / DoClickAction) step by step — never clears the bag
- **Safety**: waits 3 s for the bag to initialize after entering a session, verifies a snapshot before sorting, applies the result once and never retries, to avoid item loss

## Installation

### Option 1: Full package (recommended, easiest)

1. Open **Releases** (top right of this page) and download the latest full package (file name like `SephiriaBackpackOrganizer-v2.5.1.zip`)
2. Unzip it — you will get a `BepInEx` folder, `winhttp.dll` and other files
3. Copy everything into your game folder: in Steam, right-click *Sephiria* → Manage → Browse local files, and paste over it
4. Launch the game from Steam, then press **F8** in-game to sort your bag

> The full package already bundles the BepInEx 6 framework and the plugin itself — nothing else to install; re-installing is safe, just overwrite.

### Uninstall

Delete `游戏目录/BepInEx/plugins/SephiriaBackpackOrganizer.dll`; to remove the framework completely as well, also delete the whole `BepInEx` folder plus `winhttp.dll` and `doorstop_config.ini` (only if you have not installed other BepInEx plugins).

## Usage

- Press **F8** in game to sort your bag; a "done" toast appears when finished
- Middle-click an artifact in the bag to toggle manual priority; later selections have higher priority, and middle-clicking it again cancels the selection
- All settings live in `游戏目录/BepInEx/config/com.sephiria.backpack-organizer.cfg` — restart the game after editing

## Configuration

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| General | Hotkey | F8 | Hotkey that triggers sorting |
| General | SortMode | Enhanced | Vanilla (built-in) / Enhanced |
| General | SessionStableDelay | 3 s | Wait after entering a session until the bag is ready (prevents item loss) |
| Enhanced | SearchRounds | 4 | Independent search rounds per sort |
| Enhanced | Iterations | 3000 | Simulated-annealing iterations |
| Enhanced | Temperature | 800 | Initial temperature (higher = easier to escape local optima) |
| Priority | Enable | true | Priority system (Legend=1 … Common=4) |
| Priority | CompassTargetForcedHigh | true | The artifact locked by the golden needle gets top priority regardless of rarity |
| Priority | LowLevelValueItems | Shadow Eye / Lizard Plate Armor | Charms whose level score is discounted (only need to be active, no need for high level) |
| Priority | ForcedPriorityItems | Berut's Scythe=3 / Blizzard Hammer=2 / Fault Detection Probe=4 | Force a specific priority (key:priority, overrides the rarity mapping) |
| Priority | MinLevelItems | Galaxy Sheet Music=2 | Charms that must reach a minimum effective level first |
| Synergy | PlanetBonus | 40000 | Bonus per planet clustered around the telescope |
| Synergy | HarmonyLevelBonus | 2000 | Bonus per charm level around a harmony crystal |
| Synergy | DedicationCompanionBonus | 3000 | Bonus per companion on the dedication badge's row |
| Synergy | HourglassBonus | 6000 | Bonus per second of the magic book's CD on the hourglass's right |
| Synergy | WhitePaperComboBonus | 5000 | Scoring weight for filling the largest not-yet-full combo with a white paper |
| Synergy | CompassBonus | 12000 | Reward for a compass keeping its original target (unpaired ones auto-pair); 0 does not disable original-target binding |
| Synergy | BeltItems | Multipurpose Belt | Belt-style charm keys; when active, the sorter packs the top row with artifacts |
| Synergy | BeltRowBonus | 2500 | Bonus per artifact in the top row while a belt is active |
| Burden | NegativeCellPenalty | 20000 | Penalty when a burden is not on a negative cell |
| Mystic | Enable | true | Mystic ×2 cells |

## How it works (short)

1. **Recognize**: classify bag items (tablets / charms / burdens / misc) and identify each mechanic's items (by type or a configured key)
2. **Score**: simulate all the game's bonus formulas offline and score any layout (level × priority weight, enabled/disabled, planet clusters, harmony crystals, dedication badges, Kelsardanni Key cycle rows, compass original-target binding, white-paper combos, hourglass–magic book, burden penalty, fixed-row constraints, …)
3. **Search**: smart initial layout + multi-start multi-round simulated annealing (directed moves + random exploration), take the global best layout
4. **Apply**: host writes the whole layout back at once; multiplayer clients translate it into swap/rotate sequences executed step by step

## License

[MIT](../LICENSE). This plugin does not modify any game files; for learning and personal use. Use at your own risk.
