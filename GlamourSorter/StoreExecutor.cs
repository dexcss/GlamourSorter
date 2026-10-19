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
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using CSInventoryType = FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using CSCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using TaskManager = ECommons.Automation.NeoTaskManager.TaskManager;
using TaskManagerConfiguration = ECommons.Automation.NeoTaskManager.TaskManagerConfiguration;

namespace GlamourSorter;

public sealed class StoreOutcome
{
    public string SetName = "";
    public bool Success;
    public bool NeedsManual;
    public StoreDestination Destination;
    public string Detail = "";
}

// Drives the real game UI for normal outfit glamours, and the native Cabinet API for
// Armoire-eligible sets.
//
// Normal (non-Armoire) sets:
//   1. AgentMiragePrismPrismSetConvert.Open(...) anchored on a real inventory piece
//   2. wait for "MiragePrismPrismSetConvert"
//   3. TryHandOver() each owned slot
//   4. StoreAsGlamour() (component 27)
//   5. confirm "MiragePrismPrismSetConvertC" with callback event 0 (the same default-confirm
//      path YesAlready and DailyRoutines use). If the dialog is a normal Yes/No this deposits.
//   6. verify the dresser count grew
//
// Armoire-eligible sets:
//   1. load the Armoire's Cabinet data (the game's own request commands, no Armoire UI needed)
//   2. for each piece, call UIState.Cabinet.StoreCabinetItem(cabinetRowId)
//   3. verify IsItemInCabinet() for each piece
//
// This deliberately avoids the old "force the Store as Outfit Glamour checkbox bit" hack:
// writing AtkComponentButton.Flags made the box look ticked but skipped the addon's
// acknowledgement handler, so the client accepted the click and deposited nothing.
public sealed unsafe class StoreExecutor
{
    private const string AddonMain = "MiragePrismPrismSetConvert";
    private const string AddonConfirm = "MiragePrismPrismSetConvertC";

    // The game requests the Armoire cabinet list with these two commands. The glamour-plate
    // UI uses the same pair when it needs Armoire contents without opening the Cabinet addon.
    private const int CommandRequestCabinet = 423;
    private const int CommandFinishCabinetRequest = 427;

    private readonly IPluginLog _log;
    private readonly TaskManager _tm;
    private readonly GlamourSortEngine _engine;

    public bool Busy => _tm.IsBusy || _driving;
    public Action<StoreOutcome>? OnResult;

    // When true (default), sets whose entire definition is Armoire-eligible are stored in the
    // Armoire piece-by-piece. When false they go through the dresser outfit-glamour flow.
    public bool UseArmoireForEligibleSets = true;

    // Set ids we've already finished (stored or permanently failed) this run, so the
    // scan -> store-one -> rescan loop doesn't retry a set that can't progress.
    private readonly HashSet<uint> _doneSetIds = new();
    private bool _driving;
    private bool _abort;
    private uint _onlySetId;

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

    private static AgentMiragePrismPrismSetConvert* Agent =>
        (AgentMiragePrismPrismSetConvert*)Framework.Instance()->UIModule->GetAgentModule()
            ->GetAgentByInternalId(AgentId.MiragePrismPrismSetConvert);

    private static AgentMiragePrismPrismBox* Dresser =>
        (AgentMiragePrismPrismBox*)Framework.Instance()->UIModule->GetAgentModule()
            ->GetAgentByInternalId(AgentId.MiragePrismPrismBox);

    private static CSCabinet* Cabinet
    {
        get
        {
            var ui = UIState.Instance();
            return ui == null ? null : &ui->Cabinet;
        }
    }

    // Entry point. Instead of queuing every set from one stale scan (storing consumes items
    // and invalidates the rest of the plan), we drive ONE set at a time and RE-SCAN between
    // each, so every set is attempted from fresh inventory/dresser state.
    public void Start(bool useArmoireForEligibleSets)
    {
        if (Busy) Abort();

        UseArmoireForEligibleSets = useArmoireForEligibleSets;
        _doneSetIds.Clear();
        _onlySetId = 0;
        _abort = false;
        _driving = true;
        Svc.Framework.Update += Drive;
    }

    // Store just one specific set (still rescans fresh before doing it).
    public void StartSingle(uint setItemId, bool useArmoireForEligibleSets)
    {
        if (Busy) Abort();

        UseArmoireForEligibleSets = useArmoireForEligibleSets;
        _doneSetIds.Clear();
        _onlySetId = setItemId;
        _abort = false;
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

        var plan = _engine.BuildPlan();
        if (plan.Error != null)
        {
            _log.Info($"[GlamourSorter] Stopping: {plan.Error}");
            StopDriving();
            return;
        }

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
            _log.Info("[GlamourSorter] All sets processed.");
            StopDriving();
            return;
        }

        // Mark done up-front so that whether it stores or fails, we don't loop on it.
        _doneSetIds.Add(next.Set.SetItemId);
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

        if (plan.Destination == StoreDestination.Armoire && UseArmoireForEligibleSets)
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

    private void EnqueueDresserSet(SetPlan plan, StoreOutcome outcome)
    {
        outcome.Destination = StoreDestination.Dresser;

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

            // Pick any one physical piece from the plan to anchor the flow.
            var anchor = plan.Pieces.Values.FirstOrDefault();
            if (anchor.ItemId == 0)
            {
                outcome.Detail = "No source piece to open with.";
                return true;
            }

            // enableStoring: true = store flow (not preview).
            agent->Open(anchor.ItemId, anchor.Container, anchor.Slot, 0, 0, true);
            return true;
        }, $"Open store window: {plan.Set.Name}");

        _tm.EnqueueDelay(200);

        // 2. Wait for the main store addon to be ready.
        _tm.Enqueue(() => GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                          && m.IsAddonReady,
            $"Await window: {plan.Set.Name}");

        // 3. Fill only the window slots whose item we actually hold. The store window lists
        //    every slot the set defines, but for a partial set we own only some — trying to
        //    hand over a slot we can't fill either spams forever or picks a wrong entry, and
        //    the server then rejects with "you do not have the required item". So we match the
        //    window's per-slot ItemId against our owned pieces and only hand those over.
        var ownedItemIds = new HashSet<uint>(plan.Pieces.Values.Select(p => p.ItemId));
        var fillAttempts = new Dictionary<int, int>();
        _tm.Enqueue(() =>
        {
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                || !m.IsAddonReady)
                return false;

            var items = m.Items; // one entry per set slot, with ItemId + fill state
            int? target = null;
            for (var i = 0; i < items.Length; i++)
            {
                // InventoryType 9999 == unfilled. Only fill slots we own the item for.
                if (items[i].InventoryType == 9999 && ownedItemIds.Contains(items[i].ItemId))
                {
                    target = i;
                    break;
                }
            }

            if (target == null)
            {
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
                _log.Info($"[GlamourSorter] Handing over slot {target.Value} (item {items[target.Value].ItemId}, try {tries + 1}).");
                m.TryHandOver(target.Value);
            }
            return false;
        }, $"Fill slots: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 15000, AbortOnTimeout = false });

        _tm.EnqueueDelay(100);

        // 4. Verify prisms and click Store as Glamour.
        _tm.Enqueue(() =>
        {
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m)
                || !m.IsAddonReady)
                return false;

            _log.Info($"[GlamourSorter] Store step: prisms {m.GlamourPrismsHeld}/{m.GlamourPrismsRequired}, "
                      + $"slotsFilled={m.SlotsFilled.Count}, firstUnfilled={m.FirstUnfilledSlot?.ToString() ?? "none"}");

            if (m.GlamourPrismsHeld < m.GlamourPrismsRequired)
            {
                outcome.Detail = $"Not enough prisms ({m.GlamourPrismsHeld}/{m.GlamourPrismsRequired}).";
                m.Close();
                return true;
            }

            // Partial sets ARE storable. The game's own signal for "can store now" is the
            // StoreAsGlamour button being enabled — don't require every slot filled.
            var btn = m.StoreAsGlamourButton;
            if (btn == null || !btn->IsEnabled)
            {
                outcome.Detail = "Store button not enabled (nothing storable).";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                m.Close();
                return true;
            }

            _countBeforeStore = CountDresser();
            m.StoreAsGlamour();
            return true;
        }, $"Store: {plan.Set.Name}");

        _tm.EnqueueDelay(250);

        // 5. Confirm the outfit dialog.
        //    Plain Yes/No (normal sets): event 2, the proven Yes button from live testing.
        //    Checkbox dialog (mixed/Armoire-eligible outfits): event 0, the default-confirm
        //    callback YesAlready and DailyRoutines fire for "MiragePrismPrismSetConvertC".
        //    Event 0 runs the addon's acknowledgement handler; raw ReceiveEvent ids from the
        //    old probe do not.
        var confirmAttempts = 0;
        _tm.Enqueue(() =>
        {
            if (!GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvertC>(AddonConfirm, out var c)
                || !c.IsAddonReady)
            {
                var mainOpen = GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvert>(AddonMain, out var m2)
                               && m2.IsAddonReady;
                return !mainOpen;
            }

            var checkbox = c.StoreAsOutfitGlamourCheckBox;
            var hasCheckbox = checkbox != null && checkbox->AtkResNode->IsVisible();
            var yes = c.YesButton;
            var yesEnabled = yes != null && yes->IsEnabled;

            if (EzThrottler.Throttle($"gsort_confirm_{plan.Set.SetItemId}", 500))
            {
                confirmAttempts++;

                if (hasCheckbox)
                {
                    // Mixed set: the dialog has the "Store as Outfit Glamour" acknowledgement.
                    // Event 0 is the default-confirm callback YesAlready and DailyRoutines use
                    // for this exact addon; it runs the addon's acknowledgement path.
                    Callback.Fire(c.Base, true, 0);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: event 0 (checkbox dialog, "
                              + $"attempt {confirmAttempts}, yesEnabled={yesEnabled}).");
                }
                else if (yesEnabled)
                {
                    // Plain Yes/No dialog: event 2 is the proven Yes button for normal sets.
                    Callback.Fire(c.Base, true, 2);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: event 2 (plain Yes, "
                              + $"attempt {confirmAttempts}).");
                }
                else if (confirmAttempts >= 4)
                {
                    // Last resort for a normal dialog whose Yes button is still disabled.
                    Callback.Fire(c.Base, true, 0);
                    _log.Info($"[GlamourSorter] Confirm {plan.Set.Name}: event 0 fallback "
                              + $"(attempt {confirmAttempts}).");
                }
            }

            if (confirmAttempts >= 12)
            {
                outcome.NeedsManual = true;
                outcome.Detail = hasCheckbox
                    ? "Confirmation did not accept the outfit (Armoire checkbox). Left for manual review."
                    : "Confirmation did not accept. Left for manual review.";
                _log.Warning($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
                Callback.Fire(c.Base, true, 1); // No / cancel — leaves the items in bags
                return true;
            }

            return false;
        }, $"Confirm: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 12000, AbortOnTimeout = false });

        // 6. Wait for the confirm dialog to actually close, then verify the dresser grew.
        _tm.Enqueue(() =>
        {
            if (GenericHelpers.TryGetAddonMaster<AddonMaster.MiragePrismPrismSetConvertC>(AddonConfirm, out var c)
                && c.IsAddonReady)
                return false;
            return true;
        }, $"Await close: {plan.Set.Name}", new TaskManagerConfiguration { TimeLimitMS = 5000, AbortOnTimeout = false });

        _tm.EnqueueDelay(300);

        // 7. Verify the deposit really happened by comparing dresser counts.
        _tm.Enqueue(() =>
        {
            // If a prism/error message set Detail already, keep it (skip verification).
            if (string.IsNullOrEmpty(outcome.Detail))
            {
                var after = CountDresser();
                if (after > _countBeforeStore)
                {
                    outcome.Success = true;
                    outcome.Detail = plan.Kind == PlanKind.CompleteExisting
                        ? $"Completed set (dresser {_countBeforeStore}->{after})."
                        : $"Stored new outfit (dresser {_countBeforeStore}->{after}).";
                }
                else
                {
                    outcome.Success = false;
                    outcome.Detail = $"No deposit detected (dresser stayed {_countBeforeStore}).";
                }
            }
            _log.Info($"[GlamourSorter] {outcome.SetName}: {outcome.Detail}");
            OnResult?.Invoke(outcome);
            return true;
        }, $"Report: {plan.Set.Name}");
    }

    private int _countBeforeStore;

    private static int CountDresser()
    {
        var d = Dresser;
        if (d == null || d->Data == null) return -1;
        var n = 0;
        for (var i = 0; i < 800; i++)
            if (d->Data->PrismBoxItems[i].ItemId != 0)
                n++;
        return n;
    }
}
