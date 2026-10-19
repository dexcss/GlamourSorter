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

        var agents = Framework.Instance()->UIModule->GetAgentModule();
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
            // MirageStoreSetItem row id. Expand it: every constituent piece counts as stored
            // (a second loose copy in the bags is a sellable duplicate, not a missing piece),
            // and the set becomes a completion target.
            if (_sets.TryGetValue(itemId, out var outfitSet))
            {
                if (!setToPrism.ContainsKey(outfitSet.SetItemId))
                    setToPrism[outfitSet.SetItemId] = ((uint)i, it.NumOutfitPiecesAdded);

                foreach (var pieceId in outfitSet.SlotItemIds)
                {
                    if (pieceId != 0)
                        storedItemIds.Add(pieceId);
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

    // Scan the player's bags + armoury for glamour-able pieces that belong to any known set.
    // Pieces registered to a saved gear set are separated out: storing one would pull it out
    // of the gear set (the game's own outfit window hides gear-set items for the same reason).
    private (Dictionary<uint, PieceLocation> Held, List<PieceLocation> Gearset) ScanHeldPieces()
    {
        var held = new Dictionary<uint, PieceLocation>();  // itemId -> first location found
        var gearset = new List<PieceLocation>();
        var inv = InventoryManager.Instance();
        if (inv == null) return (held, gearset);

        var gsm = RaptureGearsetModule.Instance();

        foreach (var container in _playerContainers)
        {
            var c = inv->GetInventoryContainer(container);
            if (c == null || !c->IsLoaded) continue;
            for (var s = 0; s < c->Size; s++)
            {
                var item = c->GetInventorySlot(s);
                if (item == null || item->ItemId == 0) continue;
                var itemId = item->ItemId;
                if (!_itemToSets.ContainsKey(itemId)) continue;
                if (held.ContainsKey(itemId)) continue; // one physical piece is enough per slot

                var hq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var location = new PieceLocation(itemId, container, (ushort)s, hq);

                // The same check the game uses to draw the gear-set mark on inventory icons.
                if (gsm != null && gsm->IsItemRegisteredToGearset(item))
                {
                    gearset.Add(location);
                    continue;
                }

                held[itemId] = location;
            }
        }

        return (held, gearset);
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
            return result;
        }

        var (held, gearsetPieces) = ScanHeldPieces();
        result.GearsetPieces.AddRange(gearsetPieces);
        if (held.Count == 0)
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
                        result.AlreadyStoredDuplicates.Add(piece);
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
                        result.AlreadyStoredDuplicates.Add(piece);
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

        return result;
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
