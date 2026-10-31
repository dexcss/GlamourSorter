using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using CSInventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using LuminaCabinet = Lumina.Excel.Sheets.Cabinet;

namespace GlamourSorter;

// The 11 outfit slots, in MirageStoreSetItem column order.
// This order is load-bearing: StoreNewOutfit / StoreExistingOutfit expect the
// containers[] and slots[] arrays laid out in exactly this sequence.
public enum SetSlot
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
    Hands = 4,
    Legs = 5,
    Feet = 6,
    Earrings = 7,
    Necklace = 8,
    Bracelets = 9,
    Ring = 10,
}

public sealed class OutfitSet
{
    public uint SetItemId;                 // MirageStoreSetItem row id (also the outfit's Item row id)
    public string Name = "";
    // itemId that belongs in each of the 11 slots (0 = set doesn't use that slot)
    public readonly uint[] SlotItemIds = new uint[11];
}

// Where a physical piece currently lives in the player's bags
public readonly struct PieceLocation
{
    public readonly uint ItemId;
    public readonly CSInventoryType Container;
    public readonly ushort Slot;
    public readonly bool Hq;

    public PieceLocation(uint itemId, CSInventoryType container, ushort slot, bool hq)
    {
        ItemId = itemId;
        Container = container;
        Slot = slot;
        Hq = hq;
    }
}

public enum PlanKind
{
    CompleteExisting,   // set is already in the dresser as an outfit; add missing pieces
    CreateNew,          // set not stored at all; enough pieces held to make a new outfit
    SingleItem,         // one glamour item that belongs to no outfit set; stored loose
}

// Where this set should be deposited. Every set defaults to the dresser as an outfit glamour;
// fully Armoire-eligible sets can be routed to the Armoire via the opt-in executor toggle.
public enum StoreDestination
{
    Dresser,
    Armoire,
}

// One actionable deposit
public sealed class SetPlan
{
    public PlanKind Kind;
    public StoreDestination Destination;
    public OutfitSet Set = null!;
    public uint PrismBoxIndex;             // only meaningful for CompleteExisting
    // slot -> physical piece to deposit for that slot (only the slots we intend to store)
    public readonly Dictionary<SetSlot, PieceLocation> Pieces = new();
    // slots that are already filled in the dresser (CompleteExisting only) — informational
    public readonly List<SetSlot> AlreadyFilled = new();
    // piece itemIds in this plan whose appearance (model) is already in the dresser —
    // informational: storing them still works, the user just may prefer to sell them
    public readonly HashSet<uint> ModelDuplicateItemIds = new();
}

public sealed class SortResult
{
    public readonly List<SetPlan> Plans = new();
    // Pieces skipped because their item is already stored in the dresser — the dupe case.
    public readonly List<PieceLocation> AlreadyStoredDuplicates = new();
    // Pieces skipped because they are registered to a saved gear set (the game's own
    // gearset-mark check). Storing those would break the gear set.
    public readonly List<PieceLocation> GearsetPieces = new();
    public string? Error;
    // True when the error is a "the dresser isn't answering right now" condition — the store loop
    // retries those for a while instead of abandoning the rest of the run.
    public bool ErrorIsTransient;
}

public sealed unsafe class GlamourSortEngine
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    // itemId -> the set(s) it can belong to
    private Dictionary<uint, HashSet<uint>> _itemToSets = new();
    // setId (== outfit Item row id) -> OutfitSet
    private Dictionary<uint, OutfitSet> _sets = new();
    // base itemId -> Cabinet row id (Armoire eligibility)
    private Dictionary<uint, uint> _cabinetIdByItem = new();
    // every item the game flags as glamour-able (Item.IsGlamorous — the dresser's own filter);
    // used to find loose items that belong to no outfit set
    private HashSet<uint> _glamorousItemIds = new();

    private readonly CSInventoryType[] _playerContainers =
    {
        CSInventoryType.Inventory1, CSInventoryType.Inventory2,
        CSInventoryType.Inventory3, CSInventoryType.Inventory4,
        CSInventoryType.ArmoryMainHand, CSInventoryType.ArmoryOffHand,
        CSInventoryType.ArmoryHead, CSInventoryType.ArmoryBody,
        CSInventoryType.ArmoryHands, CSInventoryType.ArmoryLegs,
        CSInventoryType.ArmoryFeets, CSInventoryType.ArmoryEar,
        CSInventoryType.ArmoryNeck, CSInventoryType.ArmoryWrist,
        CSInventoryType.ArmoryRings,
    };

    public GlamourSortEngine(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    // Build set-membership maps from the MirageStoreSetItem sheet.
    // Mirrors CriticalCommonLib's builder: each row lists the 11 slot item ids.
    public void BuildLookups()
    {
        _itemToSets = new Dictionary<uint, HashSet<uint>>();
        _sets = new Dictionary<uint, OutfitSet>();

        BuildCabinetLookup();
        BuildGlamourLookup();

        try
        {
            var sheet = _data.GetExcelSheet<MirageStoreSetItem>();
            var itemSheet = _data.GetExcelSheet<Item>();
            if (sheet == null)
            {
                _log.Error("MirageStoreSetItem sheet unavailable.");
                return;
            }

            foreach (var row in sheet)
            {
                var slotIds = new uint[]
                {
                    row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
                    row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
                    row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
                };

                if (slotIds.All(id => id == 0))
                    continue;

                var set = new OutfitSet { SetItemId = row.RowId };
                for (var i = 0; i < 11; i++)
                    set.SlotItemIds[i] = slotIds[i];

                set.Name = ResolveSetName(itemSheet, row.RowId, slotIds);
                _sets[row.RowId] = set;

                foreach (var id in slotIds.Where(id => id != 0).Distinct())
                {
                    if (!_itemToSets.TryGetValue(id, out var s))
                    {
                        s = new HashSet<uint>();
                        _itemToSets[id] = s;
                    }
                    s.Add(row.RowId);
                }
            }

            _log.Info($"GlamourSorter: indexed {_sets.Count} outfit sets, {_itemToSets.Count} member items.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "GlamourSorter: failed to build set lookups. Plugin will load but sort will be empty.");
            _itemToSets = new Dictionary<uint, HashSet<uint>>();
            _sets = new Dictionary<uint, OutfitSet>();
        }
    }

    // The Cabinet sheet is the authoritative "can this go in the Armoire?" list. It maps
    // base item id -> Cabinet row id; StoreCabinetItem takes the row id, not the item id.
    private void BuildCabinetLookup()
    {
        _cabinetIdByItem = new Dictionary<uint, uint>();
        try
        {
            var sheet = _data.GetExcelSheet<LuminaCabinet>();
            if (sheet == null)
            {
                _log.Warning("GlamourSorter: Cabinet sheet unavailable; Armoire routing disabled.");
                return;
            }

            foreach (var row in sheet)
            {
                var itemId = row.Item.RowId;
                if (itemId != 0)
                    _cabinetIdByItem.TryAdd(itemId, row.RowId);
            }

            _log.Info($"GlamourSorter: indexed {_cabinetIdByItem.Count} Armoire-eligible items.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "GlamourSorter: failed to build Armoire item lookup.");
            _cabinetIdByItem = new Dictionary<uint, uint>();
        }
    }

    public bool IsArmoireEligible(uint itemId) => _cabinetIdByItem.ContainsKey(itemId);

    public bool TryGetCabinetId(uint itemId, out uint cabinetId)
        => _cabinetIdByItem.TryGetValue(itemId, out cabinetId);

    // Dresser eligibility is the game's own Item.IsGlamorous flag — the same filter the dresser's
    // store list applies. Indexed once so the bag walk stays cheap.
    private void BuildGlamourLookup()
    {
        _glamorousItemIds = new HashSet<uint>();
        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet == null)
            {
                _log.Warning("GlamourSorter: Item sheet unavailable; loose-item sorting disabled.");
                return;
            }

            foreach (var row in sheet)
            {
                if (row.IsGlamorous)
                    _glamorousItemIds.Add(row.RowId);
            }

            _log.Info($"GlamourSorter: indexed {_glamorousItemIds.Count} glamour-able items.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "GlamourSorter: failed to build glamour item lookup.");
            _glamorousItemIds = new HashSet<uint>();
        }
    }

    /// <summary>True when this item id is the item id of a stored outfit (a MirageStoreSetItem row).</summary>
    public bool IsOutfitItem(uint itemId) => _sets.ContainsKey(itemId);

    /// <summary>True when the dresser can store this item on its own.</summary>
    public bool IsDresserStorable(uint itemId) => _glamorousItemIds.Contains(itemId);

    // A set belongs in the Armoire only when every slot the set defines is Armoire-eligible.
    // Requiring the whole set (not just the pieces we happen to hold) prevents splitting a
    // mixed dungeon set between the Armoire and the dresser.
    private bool IsArmoireEligibleSet(OutfitSet set)
    {
        var any = false;
        for (var i = 0; i < set.SlotItemIds.Length; i++)
        {
            var itemId = set.SlotItemIds[i];
            if (itemId == 0) continue;
            any = true;
            if (!IsArmoireEligible(itemId)) return false;
        }
        return any;
    }

    private static string ResolveSetName(Lumina.Excel.ExcelSheet<Item>? itemSheet, uint setRowId, uint[] slotIds)
    {
        if (itemSheet == null) return $"Set {setRowId}";
        // The MirageStoreSetItem row id equals the "outfit" item id; prefer that name.
        if (itemSheet.TryGetRow(setRowId, out var outfitItem) && !outfitItem.Name.IsEmpty)
            return outfitItem.Name.ExtractText();
        // Fallback: name after the body piece, else first non-zero slot.
        var anchor = slotIds[(int)SetSlot.Body] != 0 ? slotIds[(int)SetSlot.Body]
                     : slotIds.First(id => id != 0);
        if (itemSheet.TryGetRow(anchor, out var it))
            return it.Name.ExtractText();
        return $"Set {setRowId}";
    }

    // Read the dresser via the open PrismBox agent. Requires the dresser window to be open.
    // Returns per-set the prism-box index that holds the stored outfit, the set of every
    // stored item id (expanding outfit glamours into their constituent pieces), and the set
    // of appearance keys (model) already present.
    private bool ReadDresser(
        out Dictionary<uint, (uint prismIndex, byte piecesAdded)> setToPrism,
        out HashSet<uint> storedItemIds,
        out HashSet<(ulong ModelMain, ulong ModelSub)> storedModels)
    {
        setToPrism = new Dictionary<uint, (uint, byte)>();
        storedItemIds = new HashSet<uint>();
        storedModels = new HashSet<(ulong, ulong)>();

        // Logout / zone transitions can leave the UI module temporarily unavailable; treat that as
        // "dresser not ready" instead of dereferencing a null pointer.
        var fw = Framework.Instance();
        if (fw == null || fw->UIModule == null) return false;
        var agents = fw->UIModule->GetAgentModule();
        if (agents == null) return false;
        var dresser = (AgentMiragePrismPrismBox*)agents->GetAgentByInternalId(AgentId.MiragePrismPrismBox);
        if (dresser == null || !dresser->IsAgentActive() || dresser->Data == null)
            return false;

        // PrismBoxItems is an expanded list (8000 entries): top-level dresser slots first,
        // then the constituent pieces of stored outfit glamours. Wait for the async item-name
        // load, otherwise dupe detection runs against a half-populated list.
        if (!dresser->Data->IsAsyncLoadComplete)
            return false;

        var itemSheet = _data.GetExcelSheet<Item>();

        for (var i = 0; i < 8000; i++)
        {
            var it = dresser->Data->PrismBoxItems[i];
            var itemId = it.ItemId;
            if (itemId == 0) continue;
            if (itemId >= 1_000_000) itemId -= 1_000_000; // HQ marker
            storedItemIds.Add(itemId);

            if (itemSheet != null && itemSheet.TryGetRow(itemId, out var storedRow))
            {
                // Accessories and similar have no model (ModelMain == 0) — the item-id check
                // above already covers them.
                if (storedRow.ModelMain != 0)
                    storedModels.Add((storedRow.ModelMain, storedRow.ModelSub));
            }

            if (it.NumOutfitPiecesAdded == 0)
                continue;

            // A stored outfit glamour's entry id is the outfit's Item row id, which equals the
            // MirageStoreSetItem row id, so record it as a completion target.
            //
            // The sheet definition must NOT be blanket-expanded into storedItemIds: the expanded
            // PrismBoxItems list already carries one entry per piece of every stored outfit (those
            // entries are what the dresser UI lists, and their Slot is >= 800), so the pieces are
            // already in storedItemIds above. Expanding the whole definition would also mark the
            // pieces a *partially* stored outfit is missing — and those are exactly the pieces the
            // completion feature has to store; marking them stored would report them as
            // "already stored, safe to sell" and leave CompleteExisting with nothing to plan.
            // Expansion is only correct when the outfit is complete (all its defined pieces are
            // inside it), so gate it on the outfit's own piece count.
            if (_sets.TryGetValue(itemId, out var outfitSet))
            {
                if (!setToPrism.ContainsKey(outfitSet.SetItemId))
                    setToPrism[outfitSet.SetItemId] = ((uint)i, it.NumOutfitPiecesAdded);

                var definedPieces = 0;
                foreach (var pieceId in outfitSet.SlotItemIds)
                    if (pieceId != 0) definedPieces++;

                if (it.NumOutfitPiecesAdded >= definedPieces)
                {
                    foreach (var pieceId in outfitSet.SlotItemIds)
                    {
                        if (pieceId != 0)
                            storedItemIds.Add(pieceId);
                    }
                }
            }
            else if (_itemToSets.TryGetValue(itemId, out var legacySets))
            {
                // Fallback for layouts where the entry id is a member item id instead.
                foreach (var setId in legacySets)
                {
                    if (!setToPrism.ContainsKey(setId))
                        setToPrism[setId] = ((uint)i, it.NumOutfitPiecesAdded);
                }
            }
        }

        return true;
    }

    // Every item id saved in a gear set. IsItemRegisteredToGearset is the game's own icon-mark check
    // and is preferred, but it was observed under-reporting right after a plugin load (one scan saw
    // 1 protected piece, the next saw 110), so the gearset entries themselves are read as a belt-
    // and-braces second source. Storing a piece that is in a gear set would pull it out of the set.
    private static HashSet<uint> BuildGearsetItemIds(RaptureGearsetModule* gsm)
    {
        var ids = new HashSet<uint>();
        if (gsm == null) return ids;

        for (var i = 0; i < 100; i++)
        {
            var entry = gsm->GetGearset(i);
            if (entry == null || (entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                continue;

            foreach (var item in entry->Items)
            {
                if (item.ItemId == 0) continue;
                ids.Add(item.ItemId % 1_000_000u);
            }
        }

        return ids;
    }

    // Scan the player's bags + armoury for glamour-able gear. Set pieces and loose single items
    // are separated: set pieces feed the outfit flows, loose items are stored one-by-one.
    // Pieces registered to a saved gear set are separated out first: storing one would pull it
    // out of the gear set (the game's own outfit window hides gear-set items for the same reason).
    private (Dictionary<uint, PieceLocation> Held, List<PieceLocation> Gearset, Dictionary<uint, PieceLocation> Singles)
        ScanHeldPieces()
    {
        var held = new Dictionary<uint, PieceLocation>();   // set-piece itemId -> first location
        var singles = new Dictionary<uint, PieceLocation>(); // non-set itemId -> first location
        var gearset = new List<PieceLocation>();
        var inv = InventoryManager.Instance();
        if (inv == null) return (held, gearset, singles);

        var gsm = RaptureGearsetModule.Instance();
        var gearsetItemIds = BuildGearsetItemIds(gsm);

        foreach (var container in _playerContainers)
        {
            var c = inv->GetInventoryContainer(container);
            if (c == null || !c->IsLoaded) continue;
            for (var s = 0; s < c->Size; s++)
            {
                var item = c->GetInventorySlot(s);
                if (item == null || item->ItemId == 0) continue;
                var itemId = item->ItemId;

                var isSetPiece = _itemToSets.ContainsKey(itemId);
                if (!isSetPiece && !IsDresserStorable(itemId)) continue;
                if (isSetPiece ? held.ContainsKey(itemId) : singles.ContainsKey(itemId))
                    continue; // one physical piece is enough per slot

                var hq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var location = new PieceLocation(itemId, container, (ushort)s, hq);

                // The same check the game uses to draw the gear-set mark on inventory icons, plus a
                // direct read of the saved gearsets as a second source (see BuildGearsetItemIds).
                if (gearsetItemIds.Contains(itemId)
                    || (gsm != null && gsm->IsItemRegisteredToGearset(item)))
                {
                    gearset.Add(location);
                    continue;
                }

                if (isSetPiece)
                    held[itemId] = location;
                else
                    singles[itemId] = location;
            }
        }

        _log.Info($"[GlamourSorter] Gearset check: {gearsetItemIds.Count} item id(s) in saved gearsets, "
                  + $"{gearset.Count} held piece(s) protected, {held.Count} set piece(s) + {singles.Count} loose item(s) sortable.");

        return (held, gearset, singles);
    }

    // Which set slot does this itemId occupy for a given set?
    private static SetSlot? SlotForItemInSet(OutfitSet set, uint itemId)
    {
        for (var i = 0; i < 11; i++)
            if (set.SlotItemIds[i] == itemId)
                return (SetSlot)i;
        return null;
    }

    public SortResult BuildPlan()
    {
        var result = new SortResult();

        if (_sets.Count == 0)
        {
            result.Error = "Set index not built. Reopen the plugin.";
            return result;
        }

        if (!ReadDresser(out var setToPrism, out var storedItemIds, out var storedModels))
        {
            result.Error = "Open your Glamour Dresser first (and wait a moment for it to load), then run the sort.";
            result.ErrorIsTransient = true;
            return result;
        }

        var (held, gearsetPieces, singles) = ScanHeldPieces();
        result.GearsetPieces.AddRange(gearsetPieces);
        _log.Info($"[GlamourSorter] Scan: dresser has {storedItemIds.Count} distinct stored item id(s) "
                  + $"and {setToPrism.Count} stored outfit(s); {held.Count} set piece(s) held, "
                  + $"{singles.Count} loose item(s), {gearsetPieces.Count} gear-set piece(s) left alone.");
        if (held.Count == 0 && singles.Count == 0)
            return result; // nothing to do; empty plan

        var itemSheet = _data.GetExcelSheet<Item>();

        // Group held pieces by candidate set.
        var setCandidates = new Dictionary<uint, List<PieceLocation>>();
        foreach (var kv in held)
        {
            var sets = _itemToSets[kv.Key];
            foreach (var setId in sets)
            {
                if (!setCandidates.TryGetValue(setId, out var list))
                {
                    list = new List<PieceLocation>();
                    setCandidates[setId] = list;
                }
                list.Add(kv.Value);
            }
        }

        foreach (var (setId, pieces) in setCandidates)
        {
            var set = _sets[setId];

            if (setToPrism.TryGetValue(setId, out var prism))
            {
                // Set already stored as an outfit glamour -> complete it.
                var plan = new SetPlan
                {
                    Kind = PlanKind.CompleteExisting,
                    Set = set,
                    PrismBoxIndex = prism.prismIndex,
                    // A set that is already an outfit glamour stays in the dresser; adding
                    // pieces to it must use the dresser flow, never the Armoire.
                    Destination = StoreDestination.Dresser,
                };

                foreach (var piece in pieces)
                {
                    var slot = SlotForItemInSet(set, piece.ItemId);
                    if (slot == null) continue;

                    // If the exact piece is already stored anywhere in the dresser (loose or
                    // inside the stored outfit), it's a true duplicate — leave it in the bag
                    // for the user to sell.
                    if (storedItemIds.Contains(piece.ItemId))
                    {
                        AddDuplicate(result, piece);
                        plan.AlreadyFilled.Add(slot.Value);
                        continue;
                    }
                    plan.Pieces[slot.Value] = piece;
                }

                if (plan.Pieces.Count > 0)
                {
                    MarkModelDuplicates(plan, itemSheet, storedModels);
                    result.Plans.Add(plan);
                }
            }
            else
            {
                // Set not stored at all -> candidate for a NEW outfit.
                // Partial sets ARE storable (you can store just the pieces you hold), so we
                // queue whatever pieces are present.
                var plan = new SetPlan
                {
                    Kind = PlanKind.CreateNew,
                    Set = set,
                    Destination = IsArmoireEligibleSet(set)
                        ? StoreDestination.Armoire
                        : StoreDestination.Dresser,
                };
                foreach (var piece in pieces)
                {
                    var slot = SlotForItemInSet(set, piece.ItemId);
                    if (slot == null) continue;
                    if (storedItemIds.Contains(piece.ItemId))
                    {
                        AddDuplicate(result, piece);
                        continue;
                    }
                    plan.Pieces.TryAdd(slot.Value, piece);
                }

                if (plan.Pieces.Count > 0)
                {
                    MarkModelDuplicates(plan, itemSheet, storedModels);
                    result.Plans.Add(plan);
                }
            }
        }

        // Stable, readable ordering: completions first, then new sets, alphabetical.
        result.Plans.Sort((a, b) =>
        {
            if (a.Kind != b.Kind)
                return a.Kind == PlanKind.CompleteExisting ? -1 : 1;
            return string.Compare(a.Set.Name, b.Set.Name, StringComparison.OrdinalIgnoreCase);
        });

        // Loose glamour items (belonging to no outfit set) are stored one at a time. A copy that is
        // already anywhere in the dresser — loose or inside an outfit — is the same duplicate case
        // as a set piece: report it, leave it in the bag.
        var singlePlans = new List<SetPlan>();
        foreach (var (itemId, piece) in singles)
        {
            if (storedItemIds.Contains(itemId))
            {
                AddDuplicate(result, piece);
                continue;
            }

            var plan = new SetPlan
            {
                Kind = PlanKind.SingleItem,
                Destination = StoreDestination.Dresser,
                Set = new OutfitSet { SetItemId = itemId, Name = ResolveItemName(itemSheet, itemId) },
            };
            // The slot is only used for logging/display on this path; the store window itself
            // decides which hand-in slot a lone item occupies.
            plan.Pieces[SetSlot.MainHand] = piece;
            MarkModelDuplicates(plan, itemSheet, storedModels);
            singlePlans.Add(plan);
        }

        singlePlans.Sort((a, b) => string.Compare(a.Set.Name, b.Set.Name, StringComparison.OrdinalIgnoreCase));
        result.Plans.AddRange(singlePlans);

        return result;
    }

    private static string ResolveItemName(Lumina.Excel.ExcelSheet<Item>? itemSheet, uint itemId)
    {
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row) && !row.Name.IsEmpty)
            return row.Name.ExtractText();
        return $"Item {itemId}";
    }

    // A piece can belong to more than one set; report each physical piece once.
    private static void AddDuplicate(SortResult result, PieceLocation piece)
    {
        foreach (var existing in result.AlreadyStoredDuplicates)
            if (existing.ItemId == piece.ItemId && existing.Container == piece.Container && existing.Slot == piece.Slot)
                return;
        result.AlreadyStoredDuplicates.Add(piece);
    }

    // Informational only: flag planned pieces whose appearance is already in the dresser via
    // another item. Storing them is still valid (and required to complete a set), but the
    // user may prefer to sell them instead.
    private static void MarkModelDuplicates(
        SetPlan plan,
        Lumina.Excel.ExcelSheet<Item>? itemSheet,
        HashSet<(ulong ModelMain, ulong ModelSub)> storedModels)
    {
        if (itemSheet == null || storedModels.Count == 0)
            return;

        foreach (var piece in plan.Pieces.Values)
        {
            if (plan.ModelDuplicateItemIds.Contains(piece.ItemId))
                continue;

            if (itemSheet.TryGetRow(piece.ItemId, out var row)
                && row.ModelMain != 0
                && storedModels.Contains((row.ModelMain, row.ModelSub)))
            {
                plan.ModelDuplicateItemIds.Add(piece.ItemId);
            }
        }
    }
}
