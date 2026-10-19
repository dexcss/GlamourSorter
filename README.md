# Glamour Sorter

`/gsort` — sorts glamour-able gear from your inventory into the Glamour Dresser as outfit glamours.

- **Every set goes to the dresser** (including seasonal/Armoire-eligible gear) and becomes an outfit glamour.
- **The "Store as Outfit Glamour" checkbox dialog is automated** — see below for how.
- **Completes existing outfits.** If a set is already stored as an outfit glamour, missing pieces you
  hold are added to *that* set — no wasteful duplicate slot.
- **Protects your gear sets.** Pieces registered to a saved gear set are left in your bags (the same
  check the game uses for its gear-set icon mark) — they're never pulled into the dresser.
- **Leaves true duplicates alone.** Stored outfit glamours are expanded into their pieces and the
  dresser's full expanded item list is scanned, so a second copy of anything already stored is left
  in your bags to sell. Planned pieces that merely share an appearance with something stored get a
  "model already in dresser" hint so you can decide.
- **Preview first.** Nothing stores until you click. Scan shows every planned deposit; you confirm
  per-set or "Store All Below."
- Optional: send fully Armoire-eligible sets to the Armoire instead (piece-by-piece, no dresser slot).

## How it works

1. Reads the open Glamour Dresser (`AgentMiragePrismPrismBox->Data->PrismBoxItems`), noting which
   entries are outfit glamours (`NumOutfitPiecesAdded != 0`) and every stored item id.
2. Builds set membership from the `MirageStoreSetItem` sheet (11 slots, in column order: MainHand,
   OffHand, Head, Body, Hands, Legs, Feet, Earrings, Necklace, Bracelets, Ring).
3. Builds the Armoire eligibility map from the `Cabinet` sheet (base item id → Cabinet row id).
4. Scans inventory + armoury for pieces belonging to any known set, classifies each, and builds a plan.
5. On confirm, drives the game's own outfit-creation window:
   - `AgentMiragePrismPrismSetConvert.Open(...)` anchored on a real piece, hand over the owned slots,
     click `Store as Glamour` (component 27).
   - Confirm `MiragePrismPrismSetConvertC`:
     - plain Yes/No dialog → callback event `2` (proven Yes);
     - checkbox dialog → callback event `0` first (the default-confirm YesAlready and DailyRoutines
       use for this addon), then a full synthetic checkbox click until Yes enables, then event `2`.

## Why the checkbox works now

A real click on the "Store as Outfit Glamour" checkbox does **two** things, and the old attempts each
did only one:

- it toggles the checked bit (`AtkComponentButton.Flags` bit `0x40000`), **and**
- it delivers `AtkEventType.ButtonClick` (25) — the event AtkComponentButton/CheckBox emits on
  MouseUp/MouseClick — to the addon, whose handler enables Yes.

`SetChecked(true)` alone flips the box but skips the handler, so Yes stayed disabled. ECommons'
`ClickCheckBox` delivers the event but never toggles, so the box never flipped. The plugin now does
both — deliver the checkbox's registered ButtonClick event, then force the checked bit with
`SetChecked` if the handler did not flip it — and fires Yes (event `2`) the moment the game enables it.
As a first attempt it also fires callback `0`, the default-confirm path two major automation plugins
use for this exact dialog.

## Usage

1. Open your Glamour Dresser in-game.
2. `/gsort` → **Scan**.
3. Review the preview. Store per-set or all at once.

## Risk notes

- The dresser must stay open during a store; the plugin re-checks the agent is active before each step.
- Server rejects a deposit if you own a unique copy or lack inventory space — reported in the results
  log, nothing lost.
- If a confirmation still refuses after ~15s the set is cancelled (items stay in bags) and marked
  **MANUAL** in the results.
- First run: test on a known throwaway set to confirm behavior against your live client.

## Build

```
dotnet build GlamourSorter/GlamourSorter.csproj -c Release
```

The Dalamud SDK produces `GlamourSorter/bin/Release/GlamourSorter/latest.zip`.

## Release / version bump

Bump **all three** version locations to the same value, in one commit:

1. `GlamourSorter/GlamourSorter.csproj` — `<Version>` and `<AssemblyVersion>` and `<FileVersion>`
2. `GlamourSorter/GlamourSorter.json` — `AssemblyVersion`
3. `repo.json` — `AssemblyVersion` **and** `TestingAssemblyVersion`

Then (one command per line, PowerShell-safe):

```
git add -A
git commit -m "v1.2.0.0"
git tag v1.2.0.0
git push origin main
git push origin v1.2.0.0
```

The tag push triggers `.github/workflows/release.yml`, which builds and attaches
`GlamourSorter.zip` to the GitHub release.

## Add to the combined repo

In `C:\Users\brand\Desktop\DalamudPlugins`, `Build-CombinedRepo.ps1` includes
`https://raw.githubusercontent.com/dexcss/GlamourSorter/main/repo.json`, then:

```
powershell -ExecutionPolicy Bypass -File .\Build-CombinedRepo.ps1
git add -A
git commit -m "Add GlamourSorter"
git push origin main
```
