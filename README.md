# Glamour Sorter

`/gsort` — sorts glamour-able gear from your inventory into the Glamour Dresser **and Armoire**.

- **Normal sets** are stored as outfit glamours in the Glamour Dresser.
- **Armoire-eligible sets** (seasonal/event gear) are stored piece-by-piece in the **Armoire**.
  No dresser slot and no glamour prisms are used.
- **Completes existing outfits.** If a set is already stored as an outfit glamour, missing pieces you
  hold are added to *that* set — no wasteful duplicate slot.
- **Leaves true duplicates alone.** If the exact piece is already in the dresser, it's left in your
  bags so you can sell/expert-deliver it yourself.
- **Preview first.** Nothing stores until you click. Scan shows every planned deposit and the
  destination (Dresser or Armoire); you confirm per-set or "Store All Below."

## How it works

1. Reads the open Glamour Dresser (`AgentMiragePrismPrismBox->Data->PrismBoxItems`), noting which
   entries are outfit glamours (`NumOutfitPiecesAdded != 0`) and every stored item id.
2. Builds set membership from the `MirageStoreSetItem` sheet (11 slots, in column order: MainHand,
   OffHand, Head, Body, Hands, Legs, Feet, Earrings, Necklace, Bracelets, Ring).
3. Builds the Armoire eligibility map from the `Cabinet` sheet (base item id → Cabinet row id).
   A set is routed to the Armoire only when **every** slot it defines is Armoire-eligible.
4. Scans inventory + armoury for pieces belonging to any known set, classifies each, and builds a plan.
5. On confirm:
   - **Dresser:** opens the game's outfit-creation window, hands over the pieces you own, clicks
     `Store as Glamour`, and confirms the dialog with callback event `0` — the same default-confirm
     path YesAlready and DailyRoutines use. This handles the normal Yes/No dialog and the
     "Store as Outfit Glamour" acknowledgement for mixed sets.
   - **Armoire:** requests the Armoire's Cabinet data with the game's own commands (`423`/`427`),
     then calls `UIState.Cabinet.StoreCabinetItem(cabinetId)` for each piece and verifies with
     `IsItemInCabinet`.

## Usage

1. Open your Glamour Dresser in-game. (The Armoire data is requested automatically when needed; if it
   cannot load, open the Armoire once and retry.)
2. `/gsort` → **Scan**.
3. Review the preview. Store per-set or all at once.

## Why the old Armoire blocker is gone

The old build tried to force the `Store as Outfit Glamour` checkbox by writing the checked bit on the
component. That only changed the visual state; it skipped the addon's acknowledgement handler, so the
client accepted the click and deposited nothing. Seasonal gear is Armoire-eligible, so the correct
destination is the Armoire itself. The Armoire path above is the same native command the game uses for
"Store in Armoire", and it does not touch the outfit dialog at all.

## Risk notes

- The Armoire path requires the Cabinet data to be loaded. The plugin requests it automatically; if
  the server does not answer, the result is reported and the items stay in your bags.
- The dresser path still requires the Glamour Dresser to stay open during a store.
- Server rejects a deposit if you own a unique copy or lack inventory space — reported in the results
  log, nothing lost.
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
git commit -m "v1.1.0.1"
git tag v1.1.0.1
git push origin main
git push origin v1.1.0.1
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
