using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using CSInventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using CSCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TaskManager = ECommons.Automation.NeoTaskManager.TaskManager;
using TaskManagerConfiguration = ECommons.Automation.NeoTaskManager.TaskManagerConfiguration;

namespace GlamourSorter;

public sealed class StoreOutcome
{
    public string SetName = "";
    public bool Success;
    // Nothing was stored because the outfit already contains every planned piece — not a failure,
    // but not a store either, so it must not count as "stored".
    public bool AlreadyPresent;
    public bool NeedsManual;
    public StoreDestination Destination;
    public string Detail = "";
}

// Drives the real game UI for normal outfit glamours, and the native Cabinet API for
// Armoire-eligible sets when the user opts into Armoire routing.
//
// Dresser flow (default for every set):
//   1. AgentMiragePrismPrismSetConvert.Open(...) anchored on a real inventory piece
//   2. wait for "MiragePrismPrismSetConvert"
//   3. TryHandOver() each owned slot
//   4. StoreAsGlamour() (component 27)
//   5. confirm "MiragePrismPrismSetConvertC":
//        - plain Yes/No: callback event 2 (proven Yes)
//        - "Store as Outfit Glamour" checkbox: event 0 first (the default-confirm YesAlready
//          and DailyRoutines use), then a full synthetic checkbox click (deliver the box's
//          registered ButtonClick event + force the checked bit) until Yes enables, then
//          event 2 deposits
//   6. verify the dresser count grew
//
// Armoire flow (opt-in via UseArmoireForEligibleSets):
//   1. load the Armoire's Cabinet data (the game's own request commands, no Armoire UI needed)
//   2. for each piece, call UIState.Cabinet.StoreCabinetItem(cabinetRowId)
//   3. verify IsItemInCabinet() for each piece
public sealed unsafe class StoreExecutor
{
    private const string AddonMain = "MiragePrismPrismSetConvert";
    private const string AddonConfirm = "MiragePrismPrismSetConvertC";

    // The game requests the Armoire cabinet list with these two commands. The glamour-plate
    // UI uses the same pair when it needs Armoire contents without opening the Cabinet addon.
    private const int CommandRequestCabinet = 423;
    private const int CommandFinishCabinetRequest = 427;

    // "Store item into the prism box" — exactly what the Glamour Creation window's Yes button
    // sends (params: inventory type, inventory slot). No window automation needed for loose items.
    private const int CommandStorePrismBoxItem = 2351;
    // Glamour Prism item id; every stored piece costs one.
    private const uint GlamourPrismItemId = 21800;
    // The dresser holds 800 slots; a full dresser rejects every store that would need a new slot.
    private const int DresserCapacity = 800;

    private readonly IPluginLog _log;
    private readonly TaskManager _tm;
    private readonly GlamourSortEngine _engine;

    public bool Busy => _tm.IsBusy || _driving;
    public Action<StoreOutcome>? OnResult;

    // When false (default), every set — including seasonal/Armoire-eligible gear — is stored
    // in the dresser as an outfit glamour (the checkbox dialog is automated). When true,
    // fully Armoire-eligible sets are stored in the Armoire piece-by-piece instead.
    public bool UseArmoireForEligibleSets = false;

    // Set ids we've already finished (stored or permanently failed) this run, so the
    // scan -> store-one -> rescan loop doesn't retry a set that can't progress.
    private readonly HashSet<uint> _doneSetIds = new();
    private bool _driving;
    private bool _abort;
    private uint _onlySetId;
    // Snapshot of the Armoire toggle for this run: flipping the checkbox mid-run must not re-route
    // sets that were planned for the dresser.
    private bool _useArmoireForRun;
    // When the dresser momentarily stops answering (async reload right after a deposit).
    private DateTime _notReadySince = DateTime.MinValue;
    // Store-loop scan pacing, so a retry cannot rebuild the plan (dresser + bag walk) every frame.
    private DateTime _nextScanAt = DateTime.MinValue;
    // True once a one-off "Store this set" has actually been handed to the task manager.
    private bool _singleStarted;

    public StoreExecutor(IPluginLog log, GlamourSortEngine engine)
    {
        _log = log;
        _engine = engine;

        _tm = new TaskManager(new TaskManagerConfiguration
        {
            AbortOnTimeout = true,
            TimeLimitMS = 15000,
            ShowError = false,
        });
    }

    // Logout / zone transitions can leave the UI module temporarily unavailable. Guard the walk
    // instead of dereferencing a null pointer from a framework-update callback.
    private static AgentMiragePrismPrismSetConvert* Agent
    {
        get
        {
            var fw = Framework.Instance();
            if (fw == null || fw->UIModule == null) return null;
            var agents = fw->UIModule->GetAgentModule();
            return agents == null
                ? null
                : (AgentMiragePrismPrismSetConvert*)agents->GetAgentByInternalId(AgentId.MiragePrismPrismSetConvert);
        }
    }

    private static AgentMiragePrismPrismBox* Dresser
    {
        get
        {
            var fw = Framework.Instance();
            if (fw == null || fw->UIModule == null) return null;
            var agents = fw->UIModule->GetAgentModule();
            return agents == null
                ? null
                : (AgentMiragePrismPrismBox*)agents->GetAgentByInternalId(AgentId.MiragePrismPrismBox);
        }
    }

    private static CSCabinet* Cabinet
    {
        get
        {
            var ui = UIState.Instance();
            return ui == null ? null : &ui->Cabinet;
        }
    }

    // The dresser answers "An outfit glamour matching this gear is available. Add to pre-existing
    // outfit glamour?" through the generic SelectYesno addon — sometimes before the store window
    // opens, sometimes after the store click. Yes is callback 0 (the same call YesAlready uses for
    // SelectYesno). Without answering it the flow stalls with the prompt on screen.
    private bool TryAnswerDresserPrompt()
    {
        var addon = Svc.GameGui.GetAddonByName("SelectYesno", 1);
        if (addon.Address == IntPtr.Zero)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (unit == null || !unit->IsReady || !unit->IsVisible)
            return false;

        if (!EzThrottler.Throttle("gsort_selectyesno", 750))
            return true;

        Callback.Fire(unit, true, 0);
        _log.Info("[GlamourSorter] Answered the dresser's Yes/No prompt with Yes (add to pre-existing outfit glamour).");
        return true;
    }

    // Between sets, a store window left open (e.g. after a give-up path) would block the next one.
    private bool TryCloseLeftoverStoreWindow()
    {
        if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
            || !m.IsAddonReady)
            return false;

        m.Close();
        _log.Info("[GlamourSorter] Closed a leftover store window.");
        return true;
    }

    // Entry point. Instead of queuing every set from one stale scan (storing consumes items
    // and invalidates the rest of the plan), we drive ONE set at a time and RE-SCAN between
    // each, so every set is attempted from fresh inventory/dresser state.
    public void Start(bool useArmoireForEligibleSets)
    {
        if (Busy) Abort();

        UseArmoireForEligibleSets = useArmoireForEligibleSets;
        _useArmoireForRun = useArmoireForEligibleSets;
        _doneSetIds.Clear();
        _onlySetId = 0;
        _abort = false;
        _notReadySince = DateTime.MinValue;
        _driving = true;
        Svc.Framework.Update += Drive;
    }

    // Store just one specific set (still rescans fresh before doing it).
    public void StartSingle(uint setItemId, bool useArmoireForEligibleSets)
    {
        if (Busy) Abort();

        UseArmoireForEligibleSets = useArmoireForEligibleSets;
        _useArmoireForRun = useArmoireForEligibleSets;
        _doneSetIds.Clear();
        _onlySetId = setItemId;
        _singleStarted = false;
        _abort = false;
        _notReadySince = DateTime.MinValue;
        _driving = true;
        Svc.Framework.Update += Drive;
    }

    public void Abort()
    {
        _abort = true;
        _tm.Abort();
        _driving = false;
        Svc.Framework.Update -= Drive;
    }

    // Called each frame while driving. When the task manager is idle, scan fresh and enqueue
    // the next not-yet-done set. When no such set remains, we're finished.
    private void Drive(IFramework _)
    {
        if (_abort) { StopDriving(); return; }
        if (_tm.IsBusy) return; // a set is mid-store; wait

        // A one-off "Store this set" is done the moment its deposit finishes; do not scan again
        // and re-queue the same set (a partial outfit would otherwise be stored twice).
        if (_onlySetId != 0 && _singleStarted)
        {
            StopDriving();
            return;
        }

        // At most two plan builds a second: the dresser-not-ready retry path would otherwise walk
        // the 8000-entry dresser list and the bags on every frame for up to 15 seconds.
        if (DateTime.UtcNow < _nextScanAt) return;
        _nextScanAt = DateTime.UtcNow.AddMilliseconds(500);

        // A prompt or store window left open by a previous set would block the next window.
        if (TryAnswerDresserPrompt())
            return;
        if (TryCloseLeftoverStoreWindow())
            return;

        var plan = _engine.BuildPlan();
        if (plan.Error != null)
        {
            // Right after a deposit the dresser can briefly stop answering while its item list
            // reloads. Retry for a while; only give up if it stays unavailable.
            if (_notReadySince == DateTime.MinValue)
                _notReadySince = DateTime.UtcNow;

            if (plan.ErrorIsTransient && (DateTime.UtcNow - _notReadySince).TotalMilliseconds < 15000)
                return;

            _log.Info($"[GlamourSorter] Stopping: {plan.Error}");
            StopDriving();
            return;
        }
        _notReadySince = DateTime.MinValue;

        // Find the next storable set we haven't already finished this run.
        SetPlan? next = null;
        foreach (var p in plan.Plans)
        {
            if (_onlySetId != 0 && p.Set.SetItemId != _onlySetId) continue;
            if (_doneSetIds.Contains(p.Set.SetItemId)) continue;
            next = p;
            break;
        }

        if (next == null)
        {
            // A one-off "Store this set" whose plan no longer exists (already stored, or its
            // pieces are gone) must report something, or the window sits on "Storing…" forever.
            if (_onlySetId != 0 && !_singleStarted)
            {
                var missing = new StoreOutcome
                {
                    SetName = "Selected set",
                    Detail = "Not found in a fresh scan — it may already be stored, or no set pieces are in your bags.",
                };
                _log.Info($"[GlamourSorter] {missing.Detail}");
                OnResult?.Invoke(missing);
            }
            else if (_onlySetId == 0 && _doneSetIds.Count == 0)
            {
                // Start() with nothing storable: report it, so the window does not sit on
                // "Storing…" with an empty results list.
                var nothing = new StoreOutcome
                {
                    SetName = "Nothing to store",
                    Detail = "No set pieces were found in a fresh scan — open the Glamour Dresser and scan again.",
                };
                _log.Info($"[GlamourSorter] {nothing.Detail}");
                OnResult?.Invoke(nothing);
            }
            _log.Info("[GlamourSorter] All sets processed.");
            StopDriving();
            return;
        }

        // A full dresser refuses anything that needs a new slot; say so once instead of failing every
        // remaining item with "no deposit detected". Completing an existing outfit needs no slot.
        if (next.Kind != PlanKind.CompleteExisting && CountDresser() >= DresserCapacity)
        {
            var full = new StoreOutcome
            {
                SetName = "Glamour Dresser full",
                Detail = $"The Glamour Dresser is full ({DresserCapacity}/{DresserCapacity}). "
                         + "Withdraw something on the Withdraw tab (or consolidate loose set pieces), then store again.",
            };
            _log.Info($"[GlamourSorter] {full.Detail}");
            OnResult?.Invoke(full);
            StopDriving();
            return;
        }

        // Mark done up-front so that whether it stores or fails, we don't loop on it.
        _doneSetIds.Add(next.Set.SetItemId);
        if (_onlySetId != 0)
            _singleStarted = true;
        EnqueueSet(next);
    }

    private void StopDriving()
    {
        _driving = false;
        Svc.Framework.Update -= Drive;
    }

    private void EnqueueSet(SetPlan plan)
    {
        var outcome = new StoreOutcome { SetName = plan.Set.Name };

        if (plan.Kind == PlanKind.SingleItem)
            EnqueueSingleItemStore(plan, outcome);
        else if (plan.Destination == StoreDestination.Armoire && _useArmoireForRun)
            EnqueueArmoireSet(plan, outcome);
        else
            EnqueueDresserSet(plan, outcome);
    }

    // ---------------------------------------------------------------------------------
    // Armoire path
    // ---------------------------------------------------------------------------------

    private void EnqueueArmoireSet(SetPlan plan, StoreOutcome outcome)
    {
        outcome.Destination = StoreDestination.Armoire;

        var alreadyBefore = 0;

        // Wait for the Armoire's item list. The glamour dresser does not always keep it
        // loaded, so ask the game for it using the same commands the plate UI uses.
        _tm.Enqueue(() =>
        {
            var cabinet = Cabinet;
            if (cabinet != null && cabinet->IsCabinetLoaded())
                return true;

            RequestCabinetLoad();
            return false;
        }, $"Load Armoire data: {plan.Set.Name}", new TaskManagerConfiguration
        {
            TimeLimitMS = 8000,
            AbortOnTimeout = false,
        });

        _tm.EnqueueDelay(250);

        // Capture how many of this set's pieces were already in the Armoire before we start,
        // so the final report can distinguish "newly stored" from "already there".
        _tm.Enqueue(() =>
        {
            if (Cabinet == null || !Cabinet->IsCabinetLoaded())
            {
                outcome.Detail = "Armoire data unavailable — open the Armoire once, then retry.";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                return true;
            }

            alreadyBefore = CountPlanItemsInCabinet(plan);
            _log.Info($"[GlamourSorter] Armoire plan for {plan.Set.Name}: "
                      + $"{plan.Pieces.Count} piece(s), {alreadyBefore} already stored.");
            return true;
        }, $"Armoire precheck: {plan.Set.Name}");

        foreach (var piece in plan.Pieces.Values.OrderBy(p => p.ItemId))
        {
            if (!_engine.TryGetCabinetId(piece.ItemId, out var cabinetId))
            {
                _log.Warning($"[GlamourSorter] {plan.Set.Name}: item {piece.ItemId} has no Cabinet row; skipping.");
                continue;
            }

            var itemId = piece.ItemId;
            var pieceRef = piece;
            var attempted = false;
            var accepted = false;

            _tm.Enqueue(() =>
            {
                if (attempted) return true;
                attempted = true;

                var cabinet = Cabinet;
                if (cabinet == null || !cabinet->IsCabinetLoaded())
                    return true;

                if (cabinet->IsItemInCabinet(cabinetId))
                {
                    accepted = true;
                    _log.Info($"[GlamourSorter] {plan.Set.Name}: item {itemId} already in Armoire.");
                    return true;
                }

                accepted = cabinet->StoreCabinetItem(cabinetId);
                _log.Info($"[GlamourSorter] StoreCabinetItem {plan.Set.Name}: item={itemId} "
                          + $"cabinet={cabinetId} accepted={accepted}");
                return true;
            }, $"Armoire store: {plan.Set.Name}", new TaskManagerConfiguration
            {
                TimeLimitMS = 5000,
                AbortOnTimeout = false,
            });

            _tm.Enqueue(() =>
            {
                if (!accepted) return true;
                var cabinet = Cabinet;
                if (cabinet == null || !cabinet->IsCabinetLoaded()) return true;
                // Cabinet membership updates on the server response; the inventory slot
                // clearing is a second, independent confirmation. Either is enough.
                return cabinet->IsItemInCabinet(cabinetId) || !IsPieceStillHeld(pieceRef);
            }, $"Armoire wait: {plan.Set.Name}", new TaskManagerConfiguration
            {
                TimeLimitMS = 3000,
                AbortOnTimeout = false,
            });

            _tm.EnqueueDelay(100);
        }

        // Report and verify by cabinet membership, not by the return value of the command.
        _tm.Enqueue(() =>
        {
            if (string.IsNullOrEmpty(outcome.Detail))
            {
                var now = CountPlanItemsStored(plan);
                var total = plan.Pieces.Count;
                var newlyStored = Math.Max(0, now - alreadyBefore);

                if (now == total)
                {
                    outcome.Success = true;
                    outcome.Detail = total == 0
                        ? "Nothing to store."
                        : newlyStored > 0
                            ? $"Stored {newlyStored} new piece(s) in the Armoire"
                              + (alreadyBefore > 0 ? $" ({alreadyBefore} already there)." : ".")
                            : $"All {total} piece(s) already in the Armoire.";
                }
                else if (now > 0)
                {
                    outcome.Success = true;
                    outcome.NeedsManual = true;
                    outcome.Detail = $"Stored {newlyStored} new piece(s); "
                                     + $"{total - now} still in bags (see log).";
                }
                else
                {
                    outcome.Success = false;
                    outcome.Detail = "No Armoire deposits detected.";
                }
            }

            _log.Info($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
            OnResult?.Invoke(outcome);
            return true;
        }, $"Report: {plan.Set.Name}");
    }

    private static void RequestCabinetLoad()
    {
        var cabinet = Cabinet;
        if (cabinet == null || cabinet->State != CSCabinet.CabinetState.Invalid)
            return;

        cabinet->State = CSCabinet.CabinetState.Requested;
        GameMain.ExecuteCommand(CommandRequestCabinet);
        GameMain.ExecuteCommand(CommandFinishCabinetRequest);
        Svc.Log.Info("[GlamourSorter] Requested Armoire cabinet data.");
    }

    private int CountPlanItemsInCabinet(SetPlan plan)
    {
        var cabinet = Cabinet;
        if (cabinet == null || !cabinet->IsCabinetLoaded())
            return 0;

        var count = 0;
        foreach (var piece in plan.Pieces.Values)
        {
            if (_engine.TryGetCabinetId(piece.ItemId, out var cabinetId)
                && cabinet->IsItemInCabinet(cabinetId))
                count++;
        }
        return count;
    }

    // Count pieces that are either in the Cabinet or no longer occupy the inventory slot we
    // scanned. The latter covers the frame(s) before the Cabinet bit array refreshes.
    private int CountPlanItemsStored(SetPlan plan)
    {
        var cabinet = Cabinet;
        var count = 0;
        foreach (var piece in plan.Pieces.Values)
        {
            var inCabinet = cabinet != null && cabinet->IsCabinetLoaded()
                            && _engine.TryGetCabinetId(piece.ItemId, out var cabinetId)
                            && cabinet->IsItemInCabinet(cabinetId);
            if (inCabinet || !IsPieceStillHeld(piece))
                count++;
        }
        return count;
    }

    private static bool IsPieceStillHeld(PieceLocation piece)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return true;

        var container = inv->GetInventoryContainer(piece.Container);
        if (container == null || !container->IsLoaded) return true;

        var item = container->GetInventorySlot(piece.Slot);
        return item != null && item->ItemId == piece.ItemId;
    }

    // ---------------------------------------------------------------------------------
    // Dresser path
    // ---------------------------------------------------------------------------------

    // Loose glamour items (no outfit set) are stored with the game's own prism-box request command,
    // which is exactly what the Glamour Creation window's "Yes" sends. That window is a different
    // addon (MiragePrismPrismBoxCrystallize) and the outfit-store window never opens for these items,
    // so there is nothing to automate: send the command and verify the dresser grew.
    private void EnqueueSingleItemStore(SetPlan plan, StoreOutcome outcome)
    {
        outcome.Destination = StoreDestination.Dresser;

        if (!plan.Pieces.TryGetValue(SetSlot.MainHand, out var piece) || piece.ItemId == 0)
        {
            outcome.Detail = "No item to store.";
            _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
            OnResult?.Invoke(outcome);
            return;
        }

        void Report()
        {
            _log.Info($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
            OnResult?.Invoke(outcome);
        }

        // Guard rails, then the store request itself.
        _tm.Enqueue(() =>
        {
            var mgr = MirageManager.Instance();
            if (mgr == null || !mgr->PrismBoxLoaded)
            {
                outcome.Detail = "Dresser data is not loaded. Open the Glamour Dresser, wait a moment, then retry.";
                Report();
                return true;
            }

            if (!IsPieceStillHeld(piece))
            {
                outcome.Detail = "The item is no longer in the bag slot that was scanned.";
                Report();
                return true;
            }

            if (CountDresser() >= DresserCapacity)
            {
                outcome.Detail = $"The Glamour Dresser is full ({DresserCapacity}/{DresserCapacity}). Free a slot first.";
                Report();
                return true;
            }

            var prisms = CountGlamourPrisms();
            if (prisms < 1)
            {
                outcome.Detail = $"Not enough glamour prisms ({prisms}/1).";
                Report();
                return true;
            }

            _countBeforeStore = CountDresser();
            _log.Info($"[GlamourSorter] Store item {plan.Set.Name}: dresser entries before={_countBeforeStore}, "
                      + $"prisms={prisms}, {piece.Container} slot {piece.Slot}.");
            GameMain.ExecuteCommand(CommandStorePrismBoxItem, (int)piece.Container, piece.Slot);
            return true;
        }, $"Store item: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false });

        // The server response lands a moment later; accept either a new dresser entry or the item
        // leaving the scanned bag slot.
        var verifyStart = DateTime.MinValue;
        _tm.Enqueue(() =>
        {
            if (string.IsNullOrEmpty(outcome.Detail))
            {
                var after = CountDresser();
                var consumed = !IsPieceStillHeld(piece);
                if ((_countBeforeStore >= 0 && after > _countBeforeStore) || consumed)
                {
                    outcome.Success = true;
                    outcome.Detail = $"Stored item (dresser {_countBeforeStore}->{after}).";
                }
                else
                {
                    if (verifyStart == DateTime.MinValue)
                        verifyStart = DateTime.UtcNow;
                    if ((DateTime.UtcNow - verifyStart).TotalMilliseconds < 4000)
                        return false;

                    outcome.Success = false;
                    outcome.Detail = after < 0
                        ? "Couldn't read the dresser to verify the deposit (did the dresser close?)."
                        : $"No deposit detected (dresser stayed {_countBeforeStore}).";
                }
            }

            Report();
            return true;
        }, $"Report: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false });
    }

    private static int CountGlamourPrisms()
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : (int)inv->GetInventoryItemCount(GlamourPrismItemId);
    }

    private void EnqueueDresserSet(SetPlan plan, StoreOutcome outcome)
    {
        outcome.Destination = StoreDestination.Dresser;

        // The outfit window only accepts hand-ins of one quality ("Only items of the same quality may
        // be selected"), and a mixed selection makes the game silently refuse the store. Anchor on
        // the quality we hold most of and hand in only pieces of that quality.
        var nqPieces = plan.Pieces.Values.Where(piece => !piece.Hq).ToList();
        var hqPieces = plan.Pieces.Values.Where(piece => piece.Hq).ToList();
        var anchorPiece = nqPieces.Count > 0 && nqPieces.Count >= hqPieces.Count
            ? nqPieces[0]
            : hqPieces.Count > 0 ? hqPieces[0] : plan.Pieces.Values.FirstOrDefault();
        var anchorHq = anchorPiece.Hq;
        var matchingQualityItemIds = new HashSet<uint>(
            plan.Pieces.Values.Where(piece => piece.Hq == anchorHq).Select(piece => piece.ItemId));

        // 1. Open the store window for this set.
        //    Open() must be anchored on a REAL inventory piece belonging to the set — its
        //    itemId + container + slot. Passing the MirageStoreSetItem row id with no source
        //    opens the generic picker ("no glamour-ready items"), which is not what we want.
        _tm.Enqueue(() =>
        {
            var agent = Agent;
            if (agent == null) return false;
            var dresser = Dresser;
            if (dresser == null || !dresser->IsAgentActive())
            {
                outcome.Detail = "Dresser closed.";
                return true;
            }

            // Pick a physical piece of the anchored quality to open the flow.
            var anchor = anchorPiece;
            if (anchor.ItemId == 0)
            {
                outcome.Detail = "No source piece to open with.";
                return true;
            }

            // enableStoring: true = store flow (not preview).
            agent->Open(anchor.ItemId, anchor.Container, anchor.Slot, 0, 0, true);
            return true;
        }, $"Open store window: {plan.Set.Name}",
        new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false });

        _tm.EnqueueDelay(200);

        // 2. Wait for the main store addon to be ready. A task that inherits the manager's default
        //    configuration clears the whole queue when it times out — which would discard the
        //    report task and make the set vanish from the results without a word.
        var openWaitStart = DateTime.MinValue;
        _tm.Enqueue(() =>
            {
                if (!string.IsNullOrEmpty(outcome.Detail))
                    return true;
                if (TryAnswerDresserPrompt())
                    return false; // "add to pre-existing outfit glamour?" — answer, then wait for the window
                if (GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                    && m.IsAddonReady)
                    return true;

                if (openWaitStart == DateTime.MinValue)
                    openWaitStart = DateTime.UtcNow;
                if ((DateTime.UtcNow - openWaitStart).TotalMilliseconds > 10000)
                {
                    outcome.Detail = "The store window did not open (left for manual review).";
                    _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                    return true;
                }
                return false;
            },
            $"Await window: {plan.Set.Name}",
            new TaskManagerConfiguration { TimeLimitMS = 15000, AbortOnTimeout = false });

        // 3. Fill only the window slots whose item we actually hold. The store window lists
        //    every slot the set defines, but for a partial set we own only some — trying to
        //    hand over a slot we can't fill either spams forever or picks a wrong entry, and
        //    the server then rejects with "you do not have the required item". So we match the
        //    window's per-slot ItemId against our owned pieces and only hand those over.
        var ownedItemIds = new HashSet<uint>(plan.Pieces.Values.Select(p => p.ItemId));
        var fillAttempts = new Dictionary<int, int>();
        // Pieces this run has already handed over; their slots turn Filled (3) as a result, and they
        // must not be mistaken for pieces that were inside the outfit before we started.
        var handedPieces = new HashSet<uint>();
        _tm.Enqueue(() =>
        {
            if (!string.IsNullOrEmpty(outcome.Detail))
                return true; // a previous step already decided this set cannot proceed
            if (TryAnswerDresserPrompt())
                return false;
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                || !m.IsAddonReady)
                return false;

            var items = m.Items; // one entry per set slot, with ItemId + fill state
            if (items.Length == 0)
                return false; // the window is still populating its hand-in list

            int? target = null;
            for (var i = 0; i < items.Length; i++)
            {
                // InventoryType 9999 == unfilled. Flag: 0 = missing, 2 = unfilled, 3 = filled,
                // 6 = the piece is already inside the stored outfit — handing a second copy
                // into that slot would just waste it, so never target those.
                var flag = (uint)items[i].Flag;
                // Only Unfilled (2) slots can take a hand-in. Filled (3) means the outfit already
                // contains that piece; AlreadyInOutfit (6) is the same from the other side; Missing (0)
                // cannot be handed at all. Targeting 3 was handing a piece into a slot that already had
                // it, which the game then silently refused.
                if (items[i].InventoryType == 9999
                    && flag == 2
                    && ownedItemIds.Contains(items[i].ItemId)
                    && matchingQualityItemIds.Contains(items[i].ItemId))
                {
                    target = i;
                    break;
                }
            }

            if (target == null)
            {
                var qualitySkipped = 0;
                foreach (var piece in plan.Pieces.Values)
                    if (!handedPieces.Contains(piece.ItemId) && !matchingQualityItemIds.Contains(piece.ItemId))
                        qualitySkipped++;
                if (qualitySkipped > 0)
                    _log.Info($"[GlamourSorter] {plan.Set.Name}: skipped {qualitySkipped} piece(s) of a different "
                              + $"quality (outfit quality {(anchorHq ? "HQ" : "NQ")}).");

                // Distinguish "everything we own is already inside the outfit" from "we handed in
                // everything we could" — the first needs no store attempt at all.
                var planned = 0;
                var alreadyInside = 0;
                foreach (var piece in plan.Pieces.Values)
                {
                    planned++;
                    if (handedPieces.Contains(piece.ItemId))
                        continue; // we just filled that slot ourselves
                    for (var i = 0; i < items.Length; i++)
                    {
                        if (items[i].ItemId != piece.ItemId)
                            continue;
                        var flag = (uint)items[i].Flag;
                        if (flag == 3 || flag == 6)
                            alreadyInside++;
                        break;
                    }
                }

                if (planned > 0 && alreadyInside == planned)
                {
                    // Everything we planned is already inside the stored outfit — there is nothing to
                    // deposit, and reporting it as an "ok" (not a failure) matches what the user sees.
                    outcome.AlreadyPresent = true;
                    outcome.Detail = "Already in the stored outfit — nothing to add.";
                    _log.Info($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                    return true;
                }

                _log.Info($"[GlamourSorter] All owned slots filled for {plan.Set.Name} "
                          + $"({m.SlotsFilled.Count}/{items.Length} slots).");
                return true;
            }

            // Give up on a slot that won't fill (e.g. the item was consumed by a prior set)
            // instead of spamming handover forever.
            fillAttempts.TryGetValue(target.Value, out var tries);
            if (tries >= 8)
            {
                outcome.Detail = $"Couldn't fill slot {target.Value} (item {items[target.Value].ItemId} "
                                 + "unavailable — likely used by another set).";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                m.Close();
                return true;
            }

            if (EzThrottler.Throttle($"gsort_fill_{plan.Set.SetItemId}_{target.Value}", 300))
            {
                fillAttempts[target.Value] = tries + 1;
                handedPieces.Add(items[target.Value].ItemId);
                _log.Info($"[GlamourSorter] Handing over slot {target.Value} (item {items[target.Value].ItemId}, try {tries + 1}).");
                m.TryHandOver(target.Value);
            }
            return false;
        }, $"Fill slots: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 15000, AbortOnTimeout = false });

        _tm.EnqueueDelay(100);

        // 4. Verify prisms and click Store as Glamour.
        _tm.Enqueue(() =>
        {
            if (!string.IsNullOrEmpty(outcome.Detail))
                return true; // fill step gave up (slot unavailable) — nothing to store
            if (TryAnswerDresserPrompt())
                return false;
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                || !m.IsAddonReady)
                return false;

            var storeAgent = Agent;
            _log.Info($"[GlamourSorter] Store step: prisms {m.GlamourPrismsHeld}/{m.GlamourPrismsRequired}, "
                      + $"slotsFilled={m.SlotsFilled.Count}, firstUnfilled={m.FirstUnfilledSlot?.ToString() ?? "none"}"
                      + (storeAgent != null && storeAgent->Data != null
                          ? $", mode={(storeAgent->Data->StoreInExistingOutfit ? "existing" : "new")}, "
                            + $"prismBoxIndex={storeAgent->Data->PrismBoxIndex}"
                          : ""));

            // The game decides "add to pre-existing outfit" from the anchor piece, and pieces can
            // belong to more than one set. If the outfit it picked is not this plan's outfit, storing
            // the hand-in into it would be a cross-set mismatch the server rejects — skip instead.
            if (storeAgent != null && storeAgent->Data != null && storeAgent->Data->StoreInExistingOutfit)
            {
                var boxIndex = storeAgent->Data->PrismBoxIndex;
                var mirage = MirageManager.Instance();
                if (mirage != null && mirage->PrismBoxLoaded
                    && boxIndex < (uint)mirage->PrismBoxItemIds.Length)
                {
                    var outfitItemId = mirage->PrismBoxItemIds[(int)boxIndex] % 1_000_000u;
                    if (outfitItemId != plan.Set.SetItemId)
                    {
                        outcome.Detail = "This piece also belongs to a different stored outfit — left for manual review.";
                        _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail} "
                                     + $"(opened outfit {outfitItemId}, plan set {plan.Set.SetItemId}).");
                        m.Close();
                        return true;
                    }
                }
            }

            if (m.GlamourPrismsHeld < m.GlamourPrismsRequired)
            {
                outcome.Detail = $"Not enough prisms ({m.GlamourPrismsHeld}/{m.GlamourPrismsRequired}).";
                m.Close();
                return true;
            }

            // Partial sets ARE storable. The game's own signal for "can store now" is the
            // StoreAsGlamour button being enabled — don't require every slot filled.
            // Click the button through the addon struct itself (what YesAlready does); the ECommons
            // helper resolves a component id that differs in the "Outfit Glamour Creation" window
            // variant, which made the click a no-op there.
            var addonPtr = Svc.GameGui.GetAddonByName(AddonMain, 1);
            var setConvert = addonPtr.Address == IntPtr.Zero
                ? null
                : (AddonMiragePrismPrismSetConvert*)addonPtr.Address;
            var btn = setConvert == null ? null : setConvert->StoreAsGlamourButton;
            if (btn == null || !btn->IsEnabled)
            {
                outcome.Detail = "Store button not enabled (nothing storable).";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                // Diagnostic detail: which hand-in slots the window actually offered.
                for (var i = 0; i < m.Items.Length; i++)
                {
                    _log.Info($"[GlamourSorter]   hand-in slot {i}: item {m.Items[i].ItemId} "
                              + $"inventoryType={m.Items[i].InventoryType} flag={m.Items[i].Flag}");
                }
                m.Close();
                return true;
            }

            if (storeAgent != null && storeAgent->Data != null)
            {
                var data = storeAgent->Data;
                _log.Info($"[GlamourSorter] SetConvert state={data->State}, itemSets={data->ItemSetCount}, "
                          + $"numItemsInSet={data->NumItemsInSet}, handInCount={data->HandInItemCount}, "
                          + $"handInValid={data->HandInItemValidCount}, "
                          + $"storeExisting={data->StoreInExistingOutfit}.");
                for (var i = 0; i < m.Items.Length && i < 11; i++)
                {
                    _log.Info($"[GlamourSorter]   slot {i}: item {m.Items[i].ItemId} "
                              + $"invType={m.Items[i].InventoryType} flag={m.Items[i].Flag}");
                }
            }

            _countBeforeStore = CountDresser();
            _log.Info($"[GlamourSorter] Store {plan.Set.Name}: dresser entries before={_countBeforeStore}.");
            m.StoreAsGlamour();
            _log.Info($"[GlamourSorter] Store {plan.Set.Name}: clicked Store as Glamour.");
            return true;
        }, $"Store: {plan.Set.Name}",
        new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false });

        _tm.EnqueueDelay(250);

        // 5. Confirm the outfit dialog. Every layer below is ordered cheapest-first; each one
        //    stops as soon as the deposit happens.
        //      a) Plain Yes/No (normal sets): event 2 — the proven Yes callback (deposit
        //         confirmed live once the dialog accepts).
        //      b) Checkbox dialog ("Store as Outfit Glamour", shown for Armoire-eligible gear):
        //         event 0 first — the default-confirm callback YesAlready and DailyRoutines fire
        //         for this exact addon. The old attempts never tried it.
        //      c) If the dialog is still open, replicate a real checkbox click. A real click does
        //         TWO things the old attempts each did only half of:
        //           - toggles the checked bit (AtkComponentCheckBox's own handler), and
        //           - delivers ButtonClick (AtkEventType 25, "sent on MouseUp and MouseClick") to
        //             the addon, whose handler enables Yes.
        //         SetChecked alone flips the box but never enables Yes; delivering the event alone
        //         flips nothing. Doing both — event first, then SetChecked + event again if the
        //         box did not flip — covers whichever half owns the toggle.
        //      d) When Yes is finally enabled, event 2 deposits (proven live with a real
        //         checkbox tick + automated Yes).
        var confirmStarted = DateTime.MinValue;
        var confirmWaitStart = DateTime.MinValue;
        var storeCallbackFired = false;
        var confirmAttempts = 0;
        var zeroFired = false;
        var checkboxClicks = 0;
        _tm.Enqueue(() =>
        {
            if (!string.IsNullOrEmpty(outcome.Detail))
                return true; // nothing was stored, so there is no dialog to answer
            if (TryAnswerDresserPrompt())
                return false; // answered the pre-existing-outfit prompt; the store window follows
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvertC>(AddonConfirm, out var c)
                || !c.IsAddonReady)
            {
                var mainOpen = GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m2)
                               && m2.IsAddonReady;
                if (!mainOpen)
                    return true;

                // Watchdog: a store window that never produces its confirm dialog (the game rejected
                // the hand-in silently) must not swallow the rest of the run.
                if (confirmWaitStart == DateTime.MinValue)
                    confirmWaitStart = DateTime.UtcNow;

                // Fallback: the component click may not have started the store; DresserArmoire fires
                // callback 18 ("Store as Glamour") on this addon instead. Try it once after 2 s.
                if (!storeCallbackFired
                    && (DateTime.UtcNow - confirmWaitStart).TotalMilliseconds > 2000)
                {
                    storeCallbackFired = true;
                    Callback.Fire(m2.Base, true, 18);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: no confirmation opened; fired "
                              + "Store as Glamour callback 18 as a fallback.");
                }
                if ((DateTime.UtcNow - confirmWaitStart).TotalMilliseconds > 10000)
                {
                    outcome.Detail = "The store window never produced a confirmation (left for manual review).";
                    _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                    TryCloseLeftoverStoreWindow();
                    return true;
                }
                return false;
            }

            var checkbox = c.StoreAsOutfitGlamourCheckBox;
            var hasCheckbox = checkbox != null && checkbox->AtkResNode != null && checkbox->AtkResNode->IsVisible();
            var yes = c.YesButton;
            var yesEnabled = yes != null && yes->IsEnabled;
            if (confirmStarted == DateTime.MinValue)
                confirmStarted = DateTime.UtcNow;
            var elapsedMs = (DateTime.UtcNow - confirmStarted).TotalMilliseconds;

            if (hasCheckbox)
            {
                // (b) click the checkbox FIRST. Firing the default-confirm callback (event 0) before
                // ticking the box closed the dialog without depositing on several sets — exactly the
                // "checkbox was never selected" symptom. Ticking the box and delivering its ButtonClick
                // is what a real player does; Yes then enables and event 2 deposits.
                if (!yesEnabled && checkboxClicks < 20
                    && EzThrottler.Throttle($"gsort_cb_{plan.Set.SetItemId}", 250))
                {
                    checkboxClicks++;
                    var delivered = DeliverCheckboxClick(checkbox, c.Base);
                    if (!checkbox->IsChecked)
                        checkbox->SetChecked(true);
                    var yesNow = c.YesButton != null && c.YesButton->IsEnabled;
                    _log.Info($"[GlamourSorter] Checkbox click {plan.Set.Name}: #{checkboxClicks} "
                              + $"eventDelivered={delivered}, checked={checkbox->IsChecked}, "
                              + $"yesEnabled={yesEnabled}->{yesNow}.");
                }
                // (c) fallback for dialogs whose Yes never enables from the checkbox: the old
                // default-confirm callback, only after the checkbox path has had its chance.
                else if (!yesEnabled && !zeroFired && elapsedMs >= 6000
                         && EzThrottler.Throttle($"gsort_zero_{plan.Set.SetItemId}", 800))
                {
                    zeroFired = true;
                    confirmAttempts++;
                    Callback.Fire(c.Base, true, 0);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: event 0 fallback (checkbox dialog, "
                              + $"checked={checkbox->IsChecked}, yesEnabled={yesEnabled}).");
                }
            }
            // Deposit confirmation, staged cheapest-first each time the game reports a usable Yes:
            //   1) click the real Yes button (what a player does),
            //   2) fall back to callback event 2 (the proven Yes callback),
            //   3) fall back to callback event 0 (default confirm).
            if (yesEnabled && EzThrottler.Throttle($"gsort_yes_{plan.Set.SetItemId}", 700))
            {
                confirmAttempts++;
                if (confirmAttempts == 1)
                {
                    c.Yes();
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: clicked the Yes button "
                              + $"(attempt 1, checkbox={hasCheckbox}).");
                }
                else if (confirmAttempts == 2)
                {
                    Callback.Fire(c.Base, true, 2);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: fired event 2 (attempt 2).");
                }
                else if (confirmAttempts == 3)
                {
                    Callback.Fire(c.Base, true, 0);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: fired event 0 (attempt 3).");
                }
            }

            if (elapsedMs >= 15000 || confirmAttempts >= 12)
            {
                outcome.NeedsManual = true;
                outcome.Detail = hasCheckbox
                    ? "Confirmation did not accept the outfit (Store as Outfit Glamour checkbox). Left for manual review."
                    : "Confirmation did not accept. Left for manual review.";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                Callback.Fire(c.Base, true, 1); // No / cancel — leaves the items in bags
                return true;
            }

            return false;
        }, $"Confirm: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 18000, AbortOnTimeout = false });

        // 6. Wait for the confirm dialog to actually close, then verify the dresser grew.
        _tm.Enqueue(() =>
        {
            if (GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvertC>(AddonConfirm, out var c)
                && c.IsAddonReady)
                return false;
            return true;
        }, $"Await close: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false });

        _tm.EnqueueDelay(300);

        // 7. Verify the deposit really happened without depending on a single signal. The dresser
        //    count only updates once the server response lands, so poll briefly instead of declaring
        //    failure after one read. Two independent success signals:
        //      - the dresser's expanded entry list grew (a new outfit and/or newly added pieces), or
        //      - every planned piece left the bag slot it was scanned in (the deposit consumed it).
        //    The second is what makes completing an existing outfit verifiable: that deposit adds
        //    pieces to an entry that already exists and may not change the top-level slot count.
        var verifyStart = DateTime.MinValue;
        _tm.Enqueue(() =>
        {
            // If a prism/error message set Detail already, keep it (skip verification).
            if (string.IsNullOrEmpty(outcome.Detail))
            {
                var after = CountDresser();
                var consumed = plan.Pieces.Count > 0
                               && plan.Pieces.Values.All(piece => !IsPieceStillHeld(piece));
                var grew = _countBeforeStore >= 0 && after > _countBeforeStore;

                if (grew || consumed)
                {
                    outcome.Success = true;
                    outcome.Detail = plan.Kind switch
                    {
                        PlanKind.CompleteExisting => $"Completed set (dresser {_countBeforeStore}->{after}).",
                        PlanKind.SingleItem => $"Stored item (dresser {_countBeforeStore}->{after}).",
                        _ => $"Stored new outfit (dresser {_countBeforeStore}->{after}).",
                    };
                }
                else
                {
                    if (verifyStart == DateTime.MinValue)
                        verifyStart = DateTime.UtcNow;

                    // The count updates when the server response lands, and the dresser data can be
                    // briefly unreadable while it reloads — give both a few seconds, but never poll
                    // forever (a timed-out task is discarded, which would swallow the result).
                    if ((DateTime.UtcNow - verifyStart).TotalMilliseconds < 4000)
                        return false;

                    outcome.Success = false;
                    outcome.Detail = after < 0
                        ? "Couldn't read the dresser to verify the deposit (did the dresser close?)."
                        : $"No deposit detected (dresser stayed {_countBeforeStore}, pieces still in bags).";
                }
            }
            _log.Info($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
            OnResult?.Invoke(outcome);
            return true;
        }, $"Report: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 10000, AbortOnTimeout = false });
    }

    private int _countBeforeStore;

    // The dresser's PrismBoxItems is an expanded list of 8000 entries (top-level slots first, then
    // one entry per piece of each stored outfit glamour). Counting only the first 800 entries used to
    // be "the whole dresser", but once the expanded list holds 800+ entries that count saturates —
    // every later store then looked like "No deposit detected" even though the deposit had landed.
    // Data->ItemCount is the game's own count of valid entries in the expanded list.
    private static int CountDresser()
    {
        // MirageManager keeps its own copy of the dresser list, so verification still works when the
        // dresser *window*'s agent data is momentarily unreadable (the old code returned -1 forever
        // if the window had been closed, which reported a false "couldn't read the dresser").
        var mgr = MirageManager.Instance();
        if (mgr == null || !mgr->PrismBoxLoaded)
            return -1;

        var ids = mgr->PrismBoxItemIds;
        var n = 0;
        for (var i = 0; i < ids.Length; i++)
            if (ids[i] != 0)
                n++;
        return n;
    }

    // Deliver the checkbox's registered ButtonClick event to the addon — the notification half
    // of a real click (AtkEventType.ButtonClick = 25 is what AtkComponentButton/CheckBox emits
    // on MouseUp/MouseClick). A real click also toggles the checked bit inside the component;
    // when a synthetic delivery does not flip it, the caller forces it with SetChecked. Both
    // halves together are what make the addon's Yes-enable handler accept the box — which is
    // why SetChecked alone or the event alone each failed in the old attempts.
    private static bool DeliverCheckboxClick(AtkComponentCheckBox* checkbox, AtkUnitBase* addon)
    {
        if (checkbox == null || checkbox->OwnerNode == null)
            return false;

        var res = &checkbox->OwnerNode->AtkResNode;
        var evt = res->AtkEventManager.Event;
        while (evt != null && evt->State.EventType != AtkEventType.ButtonClick)
            evt = evt->NextEvent;
        if (evt == null)
            return false;

        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
        return true;
    }
}
