using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using GlamourSorter.FancyUI;
using LuminaItem = Lumina.Excel.Sheets.Item;
using TaskManager = ECommons.Automation.NeoTaskManager.TaskManager;
using TaskManagerConfiguration = ECommons.Automation.NeoTaskManager.TaskManagerConfiguration;

namespace GlamourSorter;

/// <summary>
/// Withdraws loose glamour items out of the Glamour Dresser and back into the player's bags, so the
/// user can clear duplicates (or everything) and re-sort from scratch. This is the native Mirage
/// API rather than UI automation: the dresser contents are read from
/// <c>MirageManager.PrismBoxItemIds</c> and each withdrawal is a paced
/// <c>RestorePrismBoxItem</c> call whose slot is polled until the server response clears it.
/// </summary>
public sealed unsafe class DresserExtractor
{
    // Pace between the server sends; the game tolerates a call per ~200 ms and the response to the
    // previous slot is awaited before the next send anyway.
    private const int WithdrawPaceMs = 400;
    // A successful RestorePrismBoxItem zeroes the slot once the server responds; the dresser also
    // briefly reloads its list afterwards, so allow both without waiting forever.
    private const int ClearTimeoutMs = 3000;
    private const int ReloadTimeoutMs = 5000;
    private const int ConfirmSeconds = 5;
    private const int DresserSlots = 800;

    private enum Armed
    {
        None,
        Duplicates,
        AllSingles,
        Category,
        Consolidate,
    }

    private sealed class DresserStack
    {
        public uint ItemId;
        public string Name = "";
        public string Category = "";
        public readonly List<int> Slots = new();
        // Cached per rebuild so the per-frame row draw allocates nothing.
        public string CountText = "";
        public string KeepLabel = "";
        public string WithdrawId = "";
        public string KeepId = "";
    }

    private sealed class CategoryChip
    {
        public string Name = "";
        public string Id = "";
    }

    private readonly IPluginLog _log;
    private readonly GlamourSortEngine _engine;
    private readonly TaskManager _tm;

    private readonly List<DresserStack> _stacks = new();
    private readonly Dictionary<uint, DresserStack> _byItem = new();
    // Set pieces the dresser holds as individual items; withdrawing them lets Store All pack each
    // set into a single outfit glamour and free slots.
    private readonly List<LooseSetStorage> _looseSetSets = new();
    private readonly List<int> _looseSetSlots = new();
    // Slot -> base item id from the last snapshot, so a withdraw can prove it is still aiming at
    // the item it planned to withdraw (the dresser can re-order its list between snapshots).
    private readonly Dictionary<int, uint> _slotItem = new();
    private readonly List<CategoryChip> _categories = new();
    private readonly HashSet<string> _categorySeen = new(StringComparer.Ordinal);

    private bool _snapshotDirty = true;
    private bool _loaded;
    // 0 = every category, otherwise 1 + index into _categories.
    private int _filterIndex;

    private int _entryCount;
    private int _outfitCount;
    private int _looseCount;
    private int _duplicateStacks;
    private int _duplicateExcess;
    private string _readySummary = "";

    // Last action result, kept visible across the automatic snapshot rebuilds; the running progress
    // line and the "dresser not loaded" hint take precedence over it.
    private string _result = "";
    private string _progress = "";

    // Withdraw run state, driven by the task manager and shared with the panel.
    private readonly List<int> _runSlots = new();
    private readonly List<uint> _runExpected = new();
    private bool _running;
    private bool _awaitingClear;
    private uint _awaitExpected;
    private int _sendRefusals;
    private int _runIndex;
    private int _runDone;
    private int _runTotal;
    private DateTime _lastSendAt;
    private DateTime _awaitStart;
    private DateTime _unloadedSince;

    private Armed _armed;
    private DateTime _armUntil;
    private string _armedConfirm = "";
    private bool _armedThisFrame;

    public DresserExtractor(IPluginLog log, GlamourSortEngine engine)
    {
        _log = log;
        _engine = engine;
        _tm = new TaskManager(new TaskManagerConfiguration
        {
            AbortOnTimeout = false,
            TimeLimitMS = 3_600_000,
            ShowError = false,
        });
    }

    /// <summary>True while a withdraw run is in progress.</summary>
    public bool Busy => _running;

    /// <summary>
    /// Set when the last/current run was started from the "withdraw set pieces" action; the main
    /// window then follows it with a store run that packs the pieces into outfits.
    /// </summary>
    public bool ConsolidationRequested { get; set; }

    /// <summary>The outfit set whose pieces the current consolidation batch withdrew.</summary>
    public uint ConsolidationSetId => _consolidationSetId;

    private uint _consolidationSetId;

    /// <summary>Raised after a run actually changed the dresser, so the main window can re-scan.</summary>
    public Action? OnChanged;

    public void Abort()
    {
        if (!_running) return;

        ConsolidationRequested = false;
        _tm.Abort();
        FinishRun($"Withdraw cancelled after {_runDone} of {_runTotal} item(s).");
    }

    /// <summary>Re-read the dresser contents now.</summary>
    public void Refresh()
    {
        Disarm();
        _result = "";
        Rebuild();
    }

    // -------------------------------------------------------------------------------------------
    // Dresser snapshot
    // -------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        _snapshotDirty = false;

        var previousFilter = SelectedCategoryName;

        _stacks.Clear();
        _byItem.Clear();
        _slotItem.Clear();
        _categories.Clear();
        _categorySeen.Clear();
        _entryCount = 0;
        _outfitCount = 0;
        _looseCount = 0;
        _duplicateStacks = 0;
        _duplicateExcess = 0;
        _loaded = false;

        var mgr = MirageManager.Instance();
        if (mgr != null)
        {
            _loaded = mgr->PrismBoxLoaded;
            if (_loaded)
            {
                var ids = mgr->PrismBoxItemIds;
                var count = Math.Min(ids.Length, DresserSlots);
                for (var i = 0; i < count; i++)
                {
                    var raw = ids[i];
                    if (raw == 0) continue;
                    _entryCount++;

                    var itemId = raw % 1_000_000u; // strip the HQ marker
                    if (itemId == 0) continue;

                    // A stored outfit glamour occupies one slot under its outfit item id
                    // (== MirageStoreSetItem row id); those are out of scope for this version.
                    if (_engine.IsOutfitItem(itemId))
                    {
                        _outfitCount++;
                        continue;
                    }

                    _looseCount++;
                    if (!_byItem.TryGetValue(itemId, out var stack))
                    {
                        stack = new DresserStack
                        {
                            ItemId = itemId,
                            Name = ItemDisplay.Name(itemId),
                            Category = ItemDisplay.Category(itemId),
                        };
                        stack.WithdrawId = "##extract_all_" + itemId.ToString();
                        stack.KeepId = "##extract_keep_" + itemId.ToString();
                        _byItem[itemId] = stack;
                        _stacks.Add(stack);
                    }
                    stack.Slots.Add(i);
                    _slotItem[i] = itemId;
                }
            }
        }

        _stacks.Sort(CompareStacks);
        foreach (var stack in _stacks)
        {
            stack.CountText = $"x{stack.Slots.Count}";
            stack.KeepLabel = $"keep 1, withdraw {stack.Slots.Count - 1}";
            if (stack.Slots.Count > 1)
            {
                _duplicateStacks++;
                _duplicateExcess += stack.Slots.Count - 1;
            }

            if (stack.Category.Length > 0 && _categorySeen.Add(stack.Category))
                _categories.Add(new CategoryChip { Name = stack.Category, Id = "##extract_cat_" + stack.Category });
        }

        _categories.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        _filterIndex = 0;
        if (previousFilter != null)
        {
            for (var i = 0; i < _categories.Count; i++)
            {
                if (!string.Equals(_categories[i].Name, previousFilter, StringComparison.Ordinal)) continue;
                _filterIndex = i + 1;
                break;
            }
        }

        _readySummary = _loaded
            ? $"{_looseCount} loose item(s) in {_stacks.Count} stack(s); {_outfitCount} outfit glamour(s) are left alone."
            : "";

        RebuildLooseSetCandidates();
    }

    private void RebuildLooseSetCandidates()
    {
        _looseSetSets.Clear();
        _looseSetSlots.Clear();
        if (!_loaded)
            return;

        var candidates = _engine.FindLooseSetPieces();
        if (candidates.Count == 0)
            return;

        _looseSetSets.AddRange(candidates);
        var slots = new HashSet<int>();
        foreach (var candidate in candidates)
            foreach (var slot in candidate.Slots)
                slots.Add(slot);
        _looseSetSlots.AddRange(slots);
        _looseSetSlots.Sort();
    }

    private static int CompareStacks(DresserStack a, DresserStack b)
    {
        var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : a.ItemId.CompareTo(b.ItemId);
    }

    private string? SelectedCategoryName
    {
        get
        {
            if (_filterIndex <= 0 || _filterIndex > _categories.Count) return null;
            return _categories[_filterIndex - 1].Name;
        }
    }

    private static int EmptyBagSlots()
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : (int)inv->GetEmptySlotsInBag();
    }

    // -------------------------------------------------------------------------------------------
    // Withdraw run
    // -------------------------------------------------------------------------------------------

    private bool StartRun(List<int> slots)
    {
        if (_running) return false;

        if (slots.Count == 0)
        {
            SetResult("Nothing to withdraw.");
            return false;
        }

        var mgr = MirageManager.Instance();
        if (mgr == null || !mgr->PrismBoxLoaded)
        {
            SetResult("Dresser data is not loaded. Open the Glamour Dresser once, then try again.");
            _snapshotDirty = true;
            return false;
        }

        var empty = EmptyBagSlots();
        if (empty <= 0)
        {
            SetResult("Your bags are full — free at least one slot before withdrawing.");
            return false;
        }
        if (slots.Count > empty)
        {
            SetResult($"Not enough free bag slots: {slots.Count} item(s) to withdraw, {empty} free.");
            return false;
        }

        Disarm();
        _runSlots.Clear();
        _runSlots.AddRange(slots);
        _runExpected.Clear();
        foreach (var slot in _runSlots)
            _runExpected.Add(_slotItem.TryGetValue(slot, out var expectedId) ? expectedId : 0u);
        _runIndex = 0;
        _runDone = 0;
        _runTotal = _runSlots.Count;
        _awaitingClear = false;
        _sendRefusals = 0;
        _lastSendAt = DateTime.MinValue;
        _awaitStart = DateTime.MinValue;
        _unloadedSince = DateTime.MinValue;
        _result = "";
        _progress = $"Withdrawing {_runDone} of {_runTotal}…";
        _running = true;

        _log.Info($"[GlamourSorter] Dresser extractor: withdrawing {_runTotal} item(s).");
        _tm.Enqueue(ProcessRun, "Withdraw dresser items", new TaskManagerConfiguration
        {
            TimeLimitMS = 3_600_000,
            AbortOnTimeout = false,
            ShowError = false,
        });
        return true;
    }

    // One task drives the whole run so the queue stays bounded: send, wait for the server response
    // to clear that slot, pace, next. Returns true only when the run is over.
    private bool ProcessRun()
    {
        if (_runIndex >= _runSlots.Count)
        {
            FinishRun(_runDone == _runTotal
                ? $"Withdrew {_runDone} item(s) back to your bags. Re-scan for an updated plan."
                : $"Withdrew {_runDone} of {_runTotal} item(s).");
            return true;
        }

        var mgr = MirageManager.Instance();
        if (mgr == null)
        {
            FinishRun($"Stopped after {_runDone} of {_runTotal}: the game's dresser data is unavailable.");
            return true;
        }

        // The game reloads the dresser list around a withdrawal; wait for that instead of treating
        // a temporarily empty array as "slot cleared".
        if (!mgr->PrismBoxLoaded)
        {
            if (_unloadedSince == DateTime.MinValue)
                _unloadedSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _unloadedSince).TotalMilliseconds > ReloadTimeoutMs)
            {
                FinishRun($"Stopped after {_runDone} of {_runTotal}: the dresser data did not reload.");
                return true;
            }
            return false;
        }
        _unloadedSince = DateTime.MinValue;

        var slot = _runSlots[_runIndex];
        var ids = mgr->PrismBoxItemIds;

        if (_awaitingClear)
        {
            var rawNow = ids[slot];
            // Done when the slot is empty, or no longer holds the item we withdrew (the dresser
            // re-ordered its list around the response).
            if (rawNow == 0 || rawNow % 1_000_000u != _awaitExpected)
            {
                _awaitingClear = false;
                _runDone++;
                _sendRefusals = 0;
                // Pace from the clear, not the send: a fast clear used to let the next send fire
                // while the client was still settling the previous restore.
                _lastSendAt = DateTime.UtcNow;
                _snapshotDirty = true;
                _progress = $"Withdrawing {_runDone} of {_runTotal}…";
                return false;
            }

            if ((DateTime.UtcNow - _awaitStart).TotalMilliseconds > ClearTimeoutMs)
            {
                FinishRun($"Stopped after {_runDone} of {_runTotal}: the dresser did not confirm the last withdraw.");
                return true;
            }
            return false;
        }

        // The snapshot can be a frame stale, so skip slots that are already gone.
        var rawHere = ids[slot];
        if (rawHere == 0)
        {
            _runIndex++;
            return false;
        }

        // Safety: never withdraw something that was not planned. If the dresser re-ordered since
        // the snapshot, stop and let the user run again against a fresh list.
        var expected = _runExpected[_runIndex];
        if (rawHere % 1_000_000u != expected)
        {
            FinishRun($"Stopped after {_runDone} of {_runTotal}: the dresser order changed — run the withdraw again.");
            return true;
        }

        if ((DateTime.UtcNow - _lastSendAt).TotalMilliseconds < WithdrawPaceMs)
            return false;

        _lastSendAt = DateTime.UtcNow;
        if (!mgr->RestorePrismBoxItem((uint)slot))
        {
            _sendRefusals++;
            var itemName = ItemDisplay.Name(expected);
            var unique = IsUniqueItem(expected);
            _log.Info($"[GlamourSorter] Dresser extractor: withdraw refused for {itemName} ({expected}), "
                      + $"unique={unique}, attempt={_sendRefusals}, freeBagSlots={EmptyBagSlots()}.");

            // A unique item cannot be withdrawn while you already hold a copy of it. That is per
            // item, not a run-wide failure: skip the rest of this item's copies and keep going.
            if (unique && _sendRefusals >= 2)
            {
                SkipRemainingCopies(expected);
                _sendRefusals = 0;
                _lastSendAt = DateTime.UtcNow;
                return false;
            }

            // Transient refusals (the previous restore still settling) are retried; a repeat
            // failure stops the run with the item named.
            if (_sendRefusals >= 5)
            {
                FinishRun($"Stopped after {_runDone} of {_runTotal}: {itemName} was refused 5 times — bags full, or you already hold a copy.");
                return true;
            }

            _lastSendAt = DateTime.UtcNow; // pace the retry
            return false;
        }

        _sendRefusals = 0;
        _awaitingClear = true;
        _awaitExpected = expected;
        _awaitStart = _lastSendAt;
        return false;
    }

    private void FinishRun(string message)
    {
        var changed = _runDone > 0;
        if (!changed)
            ConsolidationRequested = false; // nothing moved, so there is nothing to pack into outfits

        _running = false;
        _awaitingClear = false;
        _unloadedSince = DateTime.MinValue;
        _sendRefusals = 0;
        _lastSendAt = DateTime.MinValue;
        _runSlots.Clear();
        Disarm();
        SetResult(message);

        if (!changed) return;

        _snapshotDirty = true;
        OnChanged?.Invoke();
    }

    private void SetResult(string message)
    {
        _result = message;
        _log.Info($"[GlamourSorter] Dresser extractor: {message}");
    }

    private List<int> BuildSlots(Armed action)
    {
        if (action == Armed.Consolidate)
            return new List<int>(_looseSetSlots);

        var slots = new List<int>();
        var category = SelectedCategoryName;

        foreach (var stack in _stacks)
        {
            if (action == Armed.Category && !string.Equals(stack.Category, category, StringComparison.Ordinal))
                continue;

            var first = action == Armed.Duplicates ? 1 : 0;
            for (var i = first; i < stack.Slots.Count; i++)
                slots.Add(stack.Slots[i]);
        }

        return slots;
    }

    private void Execute(Armed action)
    {
        if (action == Armed.None) return;

        if (action == Armed.Consolidate)
        {
            TryStartConsolidationBatch();
            return;
        }

        var started = StartRun(BuildSlots(action));
        ConsolidationRequested = started && false;
        _consolidationSetId = 0;
    }

    /// <summary>
    /// Starts one consolidation batch: withdraws the loose pieces of the next candidate set that
    /// fits in the free bag slots (pieces have to pass through the bags before being packed into an
    /// outfit). The main window chains this with a store run, one set at a time, so a full dresser
    /// with hundreds of loose set pieces can be packed without needing that much bag space at once.
    /// </summary>
    public bool TryStartConsolidationBatch()
    {
        if (_running) return false;

        _snapshotDirty = true;
        Rebuild();

        if (!_loaded)
        {
            SetResult("Dresser data is not loaded. Open the Glamour Dresser once, then try again.");
            return false;
        }

        if (_looseSetSets.Count == 0)
        {
            SetResult("Nothing to consolidate — no outfit set is stored as individual pieces.");
            return false;
        }

        var free = EmptyBagSlots();
        var budget = free - 2;
        if (budget <= 0)
        {
            SetResult($"Not enough free bag slots to move a set's pieces through your bags ({free} free).");
            return false;
        }

        foreach (var set in _looseSetSets)
        {
            if (set.Slots.Count > budget)
                continue;

            var started = StartRun(new List<int>(set.Slots));
            ConsolidationRequested = started;
            _consolidationSetId = started ? set.SetItemId : 0;
            if (started)
                _log.Info($"[GlamourSorter] Dresser extractor: consolidating {set.SetName} "
                          + $"({set.Slots.Count} loose piece(s)).");
            return started;
        }

        var largest = 0;
        foreach (var set in _looseSetSets)
            if (set.Slots.Count > largest) largest = set.Slots.Count;

        SetResult($"No set fits in the {budget} free bag slot(s) — free some bag space first "
                  + $"(the largest loose set has {largest} piece(s)).");
        return false;
    }

    // -------------------------------------------------------------------------------------------
    // Panel
    // -------------------------------------------------------------------------------------------

    public void Draw()
    {
        var mgr = MirageManager.Instance();
        var loadedNow = mgr != null && mgr->PrismBoxLoaded;

        _armedThisFrame = false;
        var loadChanged = loadedNow != _loaded;
        if ((loadChanged || _snapshotDirty) && !(_running && !loadedNow))
            Rebuild();

        if (_armed != Armed.None && DateTime.UtcNow > _armUntil)
            Disarm();

        UiKit.Section(FontAwesomeIcon.Broom, "extractor");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(Theme.Muted,
            "Withdraws loose glamour items from the dresser back into your bags so you can clear space or re-sort them. "
            + "Outfit glamours are left alone.");
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0f, Theme.Gap));

        DrawStatusCard();
        DrawToolbar();
        DrawConsolidationBlock();

        if (_loaded && _stacks.Count > 0)
        {
            DrawRunButtons();
            DrawFilterRow();
            UiKit.Section(FontAwesomeIcon.BoxOpen, "loose items in the dresser");
            DrawStackList();
        }

        // Any click that was not the arming click cancels a pending confirmation.
        if (_armed != Armed.None && !_armedThisFrame && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            Disarm();
    }

    private void DrawConsolidationBlock()
    {
        UiKit.Section(FontAwesomeIcon.LayerGroup, "set pieces stored loose");
        if (_looseSetSlots.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "No outfit set is stored as individual pieces — nothing to pack.");
            ImGui.Dummy(new Vector2(0f, Theme.Gap));
            return;
        }

        var alreadyOutfits = 0;
        foreach (var set in _looseSetSets)
            if (set.AlreadyStoredAsOutfit) alreadyOutfits++;
        var freed = Math.Max(0, _looseSetSlots.Count - (_looseSetSets.Count - alreadyOutfits));

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(Theme.Muted,
            $"{_looseSetSlots.Count} piece(s) from {_looseSetSets.Count} outfit set(s) are stored individually. "
            + $"Withdrawing them lets Store All pack each set into one outfit glamour — about {freed} dresser slot(s) freed.");
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0f, Theme.Gap));

        var can = !_running && _looseSetSlots.Count > 0;
        if (!can) ImGui.BeginDisabled();
        var label = _armed == Armed.Consolidate ? _armedConfirm : "Withdraw set pieces";
        if (UiKit.GradientButton("##extract_consolidate", label, Theme.SurfaceAlt, Theme.Accent, 34f))
            OnPrimaryButton(Armed.Consolidate, _looseSetSlots.Count);
        if (!can) ImGui.EndDisabled();
        ImGui.Dummy(new Vector2(0f, Theme.Gap));
    }

    private void DrawToolbar()
    {
        var busy = _running;
        if (busy) ImGui.BeginDisabled();
        if (UiKit.ChipButton("##extract_refresh", "Refresh", false, 0f, "Re-read the dresser contents now."))
            Refresh();
        if (busy) ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0f, Theme.Gap));
    }

    private string StatusLine()
    {
        if (_running) return _progress;
        if (!_loaded)
            return "Dresser data is not loaded. Open the Glamour Dresser once this session (it reloads after a zone change), then the list appears.";
        if (_result.Length > 0) return _result;
        if (_entryCount == 0) return "The dresser is empty — nothing to withdraw.";
        return _readySummary;
    }

    private void DrawStatusCard()
    {
        var s = Theme.UiScale;
        var status = StatusLine();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var wrapW = w - Theme.Pad * 2f;
        var textH = ImGui.CalcTextSize(status, false, wrapW).Y;
        var chipH = ImGui.GetTextLineHeight() + 6f * s;
        var statsH = ImGui.GetTextLineHeight();
        var h = Theme.Pad * 2f + chipH + 6f * s + textH + 4f * s + statsH;

        UiKit.Card(ImGui.GetWindowDrawList(), p, p + new Vector2(w, h), null, _running ? 0.10f : 0f);

        var (label, color) = _running
            ? ("WITHDRAWING", Theme.Warn)
            : !_loaded ? ("DRESSER NOT LOADED", Theme.Muted)
            : _entryCount == 0 ? ("EMPTY", Theme.Muted)
            : _duplicateExcess > 0 ? ("READY", Theme.Good)
            : ("READY", Theme.Accent);

        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad));
        UiKit.Chip(label, color);

        var textY = Theme.Pad + chipH + 6f * s;
        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, textY));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - Theme.Pad);
        ImGui.TextColored(Theme.Text, status);
        ImGui.PopTextWrapPos();

        var stats = $"Entries {_entryCount} · loose {_looseCount} · duplicate stacks {_duplicateStacks} ({_duplicateExcess} extra) · free bag slots {EmptyBagSlots()}";
        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, textY + textH + 4f * s));
        ImGui.TextColored(Theme.Muted, stats);

        ImGui.SetCursorScreenPos(p + new Vector2(0f, h + Theme.Gap));
    }

    private void Arm(Armed action, int count)
    {
        _armed = action;
        _armedThisFrame = true;
        _armUntil = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
        _armedConfirm = $"Click again to confirm — {count} item(s)";
    }

    private void Disarm()
    {
        _armed = Armed.None;
        _armedConfirm = "";
        _armUntil = DateTime.MinValue;
    }

    private void OnPrimaryButton(Armed action, int count)
    {
        if (_armed == action)
        {
            Disarm();
            Execute(action);
            return;
        }
        Arm(action, count);
    }

    private void DrawRunButtons()
    {
        var busy = _running;
        var duplicates = _duplicateExcess;
        var singles = _looseCount;

        var canDuplicates = duplicates > 0 && !busy;
        if (!canDuplicates) ImGui.BeginDisabled();
        var duplicatesLabel = _armed == Armed.Duplicates ? _armedConfirm : "Withdraw duplicates";
        if (UiKit.GradientButton("##extract_dupes", duplicatesLabel, Theme.Accent, Theme.Warn, 36f))
            OnPrimaryButton(Armed.Duplicates, duplicates);
        if (!canDuplicates) ImGui.EndDisabled();

        var canSingles = singles > 0 && !busy;
        if (!canSingles) ImGui.BeginDisabled();
        var singlesLabel = _armed == Armed.AllSingles ? _armedConfirm : "Withdraw all singles";
        if (UiKit.GradientButton("##extract_singles", singlesLabel, Theme.Accent, Theme.Good, 36f))
            OnPrimaryButton(Armed.AllSingles, singles);
        if (!canSingles) ImGui.EndDisabled();

        if (_armed != Armed.None)
        {
            ImGui.TextColored(Theme.Warn, "Confirm with a second click; anything else cancels (5 s).");
        }
        ImGui.Dummy(new Vector2(0f, Theme.Gap));
    }

    private void DrawFilterRow()
    {
        var s = Theme.UiScale;
        var start = ImGui.GetCursorScreenPos();
        var maxX = start.X + ImGui.GetContentRegionAvail().X;
        var lineH = 24f * s;
        var gap = 4f * s;
        var x = start.X;
        var y = start.Y;

        var allW = ImGui.CalcTextSize("All").X + 20f * s;
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        if (UiKit.ChipButton("##extract_cat_all", "All", _filterIndex == 0, allW, "Show every loose item."))
        {
            Disarm();
            _filterIndex = 0;
        }
        x += allW + gap;

        for (var i = 0; i < _categories.Count; i++)
        {
            var category = _categories[i];
            var chipW = ImGui.CalcTextSize(category.Name).X + 20f * s;
            if (x + chipW > maxX && x > start.X)
            {
                x = start.X;
                y += lineH + gap;
            }
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (UiKit.ChipButton(category.Id, category.Name, _filterIndex == i + 1, chipW, "Only this category."))
            {
                Disarm();
                _filterIndex = i + 1;
            }
            x += chipW + gap;
        }

        var bottom = y + lineH + Theme.Gap;
        var categoryName = SelectedCategoryName;
        if (categoryName != null)
        {
            var copies = 0;
            foreach (var stack in _stacks)
            {
                if (string.Equals(stack.Category, categoryName, StringComparison.Ordinal))
                    copies += stack.Slots.Count;
            }

            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
            var label = _armed == Armed.Category ? _armedConfirm : $"Withdraw all {categoryName} ({copies})";
            if (UiKit.GradientButton("##extract_category", label, Theme.SurfaceAlt, Theme.Warn, 32f))
                OnPrimaryButton(Armed.Category, copies);
            bottom = ImGui.GetItemRectMax().Y + Theme.Gap;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
    }

    private void DrawStackList()
    {
        var category = SelectedCategoryName;
        var drawn = 0;
        foreach (var stack in _stacks)
        {
            if (category != null && !string.Equals(stack.Category, category, StringComparison.Ordinal)) continue;
            DrawStackRow(stack);
            drawn++;
        }

        if (drawn == 0)
            ImGui.TextColored(Theme.Muted, category == null ? "No loose items in the dresser." : $"No loose {category} items in the dresser.");
    }

    private void DrawStackRow(DresserStack stack)
    {
        var s = Theme.UiScale;
        var dl = ImGui.GetWindowDrawList();
        var rowTop = ImGui.GetCursorScreenPos();
        var rowW = ImGui.GetContentRegionAvail().X;
        var pad = Theme.Pad * 0.75f;
        var iconSize = 28f * s;
        var lineH = ImGui.GetTextLineHeight();
        var chipH = lineH + 6f * s;
        var buttonH = 24f * s;

        const string withdrawAllText = "Withdraw all";
        var withdrawAllW = ImGui.CalcTextSize(withdrawAllText).X + 20f * s;
        var isDuplicate = stack.Slots.Count > 1;
        var keepW = isDuplicate ? ImGui.CalcTextSize(stack.KeepLabel).X + 20f * s : 0f;
        var countW = ImGui.CalcTextSize(stack.CountText).X + 16f * s;
        var categoryW = stack.Category.Length > 0 ? ImGui.CalcTextSize(stack.Category).X + 16f * s : 0f;
        var nameW = ImGui.CalcTextSize(stack.Name).X;
        var buttonsW = withdrawAllW + (isDuplicate ? 4f * s + keepW : 0f);

        // Wide windows fit the whole stack on one line; narrow ones put the buttons on a second line
        // so the item name stays readable.
        var oneLine = pad * 2f + iconSize + 8f * s + nameW + 8f * s + countW + 4f * s + categoryW + 8f * s + buttonsW <= rowW;
        var rowH = (oneLine ? 36f : 62f) * s;
        var rowMax = rowTop + new Vector2(rowW, rowH);
        var hovered = ImGui.IsMouseHoveringRect(rowTop, rowMax);
        UiKit.Card(dl, rowTop, rowMax, hovered ? Theme.SurfaceAlt : (Vector4?)null);

        var firstLineH = oneLine ? rowH : 34f * s;
        var iconX = rowTop.X + pad;
        var nameX = iconX + iconSize + 8f * s;
        var textY = rowTop.Y + (firstLineH - lineH) * 0.5f;
        var chipY = rowTop.Y + (firstLineH - chipH) * 0.5f;
        var buttonY = oneLine
            ? rowTop.Y + (rowH - buttonH) * 0.5f
            : rowTop.Y + firstLineH + (rowH - firstLineH - buttonH) * 0.5f;
        var buttonX = rowMax.X - pad - buttonsW;

        ImGui.SetCursorScreenPos(new Vector2(iconX, rowTop.Y + (firstLineH - iconSize) * 0.5f));
        ItemDisplay.Icon(stack.ItemId, iconSize);

        float countX, categoryX, nameMaxX;
        if (oneLine)
        {
            categoryX = buttonX - 8f * s - categoryW;
            countX = categoryX - 4f * s - countW;
            nameMaxX = countX - 8f * s;
        }
        else
        {
            categoryX = rowMax.X - pad - categoryW;
            countX = categoryX - 4f * s - countW;
            nameMaxX = countX - 8f * s;
        }

        if (nameMaxX > nameX)
        {
            dl.PushClipRect(new Vector2(nameX, rowTop.Y), new Vector2(nameMaxX, rowMax.Y), true);
            dl.AddText(new Vector2(nameX, textY), Theme.U32(Theme.Text), stack.Name);
            dl.PopClipRect();
        }

        ImGui.SetCursorScreenPos(new Vector2(countX, chipY));
        UiKit.Chip(stack.CountText, Theme.Accent);
        if (categoryW > 0f)
        {
            ImGui.SetCursorScreenPos(new Vector2(categoryX, chipY));
            UiKit.Chip(stack.Category, Theme.Muted);
        }

        var busy = _running;
        if (busy) ImGui.BeginDisabled();
        ImGui.SetCursorScreenPos(new Vector2(buttonX, buttonY));
        if (UiKit.ChipButton(stack.WithdrawId, withdrawAllText, false, withdrawAllW, "Withdraw every copy of this item."))
            WithdrawStack(stack, keepFirst: false);
        if (isDuplicate)
        {
            ImGui.SetCursorScreenPos(new Vector2(buttonX + withdrawAllW + 4f * s, buttonY));
            if (UiKit.ChipButton(stack.KeepId, stack.KeepLabel, false, keepW, "Keep one copy in the dresser and withdraw the rest."))
                WithdrawStack(stack, keepFirst: true);
        }
        if (busy) ImGui.EndDisabled();

        if (hovered && nameMaxX < nameX + nameW)
            ImGui.SetTooltip(stack.Name);

        ImGui.SetCursorScreenPos(rowTop + new Vector2(0f, rowH + 4f * s));
    }

    private static bool IsUniqueItem(uint itemId)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId);
            return row?.IsUnique ?? false;
        }
        catch
        {
            return false;
        }
    }

    // Drop the remaining planned copies of one item id — used when the game refuses them because
    // the item is unique and the player now holds one.
    private void SkipRemainingCopies(uint itemId)
    {
        var skipped = 0;
        for (var i = _runSlots.Count - 1; i >= _runIndex; i--)
        {
            if (_runExpected[i] != itemId)
                continue;
            _runSlots.RemoveAt(i);
            _runExpected.RemoveAt(i);
            skipped++;
        }

        if (skipped == 0)
            return;

        _log.Info($"[GlamourSorter] Dresser extractor: skipped {skipped} more cop{(skipped == 1 ? "y" : "ies")} of {ItemDisplay.Name(itemId)} (unique item — you already hold one).");
        _runTotal = Math.Max(_runDone, _runTotal - skipped);
        _progress = $"Withdrawing {_runDone} of {_runTotal}…";
    }

    private void WithdrawStack(DresserStack stack, bool keepFirst)
    {
        Disarm();
        var slots = new List<int>(stack.Slots.Count);
        for (var i = keepFirst ? 1 : 0; i < stack.Slots.Count; i++)
            slots.Add(stack.Slots[i]);
        StartRun(slots);
    }
}
