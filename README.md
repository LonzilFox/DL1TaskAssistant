# DL1 Task Assistant

Windows x64 helper for **Dying Light 1 / Steam 1.55**. The tool is implemented as a standalone C# WinForms program and only enables game-memory writes when the loaded `gamedll_x64_rwdi.dll` matches the expected SHA-256:

```text
76F20D27C45C0B807D2F099022AD0ABC42CA90A8ADE7F761641518AF346D26A0
```

Current packaged release: **v0.1.2**.

## Run

1. Back up the game save first.
2. Start Dying Light and enter a save.
3. Run `StartAssistant.bat`.
4. To compile an EXE only, run `BuildAssistant.bat`.

The build script uses 64-bit Windows PowerShell 5.1 and the .NET Framework CodeDOM C# compiler. No Cheat Engine installation is required at runtime.

## Included features

- Bounty list, tracking and completion path based on the live bounty progress objects and the game's own evaluation queue.
- Main/side/repeatable/challenge task catalog browsing.
- Full-time stealth hook.
- Jump-height multiplier.
- Challenge timer and mission timer hooks.
- Weapon insertion using the verified native item-name ABI.
- Backpack/inventory viewer and editor for amount, quality, durability, repair count and selected weapon base damage.
- Collectible progress and completion for the categories whose collected IDs are verified.
- Simplified-Chinese names extracted/mapped from the supplied game data; internal IDs remain visible for diagnostics.
- CraftPlan/blueprint catalog.
- Hellraid coins/tools/bounties and related helper functions.
- Built-in diagnostic sampling for unresolved runtime behavior.

## Known incomplete / not fully verified

The repository is being frozen at the current state. The following items should **not** be treated as fully finished:

- **Run-speed multiplier:** both current hook sites can report a hit, but actual player displacement was still reported unchanged in game. The feature remains experimental/non-working in the last user test.
- **Orange/legendary weapon quality:** weapon insertion itself is confirmed to put the weapon in the backpack. Automatic conversion of the newly created instance to orange quality is not fully verified; the inventory editor can be used to change quality manually.
- **Inventory persistence:** live inventory editing is implemented, but persistence of every edited field across save/reload has not been exhaustively verified. Base-definition damage is process-wide for that weapon type and is restored on exit where possible.
- **Missing Persons / Strange Rocks:** the save used for development already has all 9 Missing Persons and all 15 Strange Rocks. Their dedicated persistent progress container/IDs were never conclusively mapped, so these remain read-only/unknown rather than guessed bulk writes.
- **Blueprint learned-state / unlock-all:** the tool can enumerate CraftPlan definitions and contains guarded native-award experiments, but the game's dedicated persistent “learned blueprint” container/ABI was not conclusively identified. `?` means unknown, not missing. The CT “all blueprint/easy craft” path bypasses crafting requirements and is not proof of permanent recipe ownership.
- **Main/side/repeatable/challenge quest completion:** these catalogs are intentionally read-only because the development save had those quest lines completed and a safe generic write ABI was not verified.
- **Bounty coverage:** the completion path is based on verified live progress/evaluation behavior, but not every special/DLC objective type has been exhaustively tested.
- **UI scaling:** responsive layout fixes are present, but all Windows DPI/font/window-size combinations have not been exhaustively validated.

## Repository layout

```text
DL1TaskAssistant/
├─ Source/                 C# source code
├─ Data/                   Runtime catalogs/localization data
├─ BuildAssistant.bat      Compile to DL1TaskAssistant.exe
├─ StartAssistant.bat      Launch through 64-bit PowerShell
├─ StartAssistant.ps1      Runtime compiler/launcher
├─ README.md
└─ .gitignore
```

Reverse-engineering notes, uploaded CT files, game DLL/EXE files, PAK files, generated diagnostics and old version research documents are intentionally excluded from the GitHub package. The runtime data distilled from those sources remains under `Data/`.

## Safety / compatibility

This project is tied to the exact Steam 1.55 DLL hash above. Unsupported builds are rejected rather than written with guessed offsets. Runtime hooks are restored on disconnect/exit where implemented. Back up saves before using inventory, collectible or task write functions.
