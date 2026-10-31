using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GlamourSorter.FancyUI;

namespace GlamourSorter;

/// <summary>
/// The plugin window: a Fancy UI shell (custom title bar, drag anywhere in the header, bottom-right
/// resize grip) around a small page of custom-drawn cards. Presentation only — scanning and storing
/// live in <see cref="GlamourSortEngine"/> and <see cref="StoreExecutor"/>.
/// </summary>
public sealed class MainWindow : ShellWindow
{
    private readonly GlamourSortEngine _engine;
    private readonly StoreExecutor _executor;

    private SortResult? _plan;
    private readonly List<StoreOutcome> _log = new();
    private string _status = "Open your Glamour Dresser, then click Scan.";
    private bool _wasBusy;
    private int _okCount;
    private int _manualCount;
    private int _failCount;

    public MainWindow(GlamourSortEngine engine, StoreExecutor executor)
        : base("GlamourSorter##Main", "Glamour Sorter", new Vector2(440f, 380f), new Vector2(1200f, 1200f))
    {
        _engine = engine;
        _executor = executor;
        _executor.OnResult = o =>
        {
            _log.Add(o);
            if (o.NeedsManual) _manualCount++;
            else if (o.Success) _okCount++;
            else _failCount++;
            _status = $"Storing… {_okCount} stored, {_manualCount} need attention, {_failCount} failed.";
        };

        Size = new Vector2(520f, 620f);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // ---- title bar: version text + a live state dot ------------------------------------------

    protected override void DrawHeaderExtras(Vector2 freeMin, Vector2 freeMax)
    {
        var s = Theme.UiScale;
        var dl = ImGui.GetWindowDrawList();
        var y = (freeMin.Y + freeMax.Y) * 0.5f;
        var x = freeMin.X + Theme.Gap;

        var ver = typeof(GlamourSortEngine).Assembly.GetName().Version;
        var verText = ver == null ? string.Empty : "v" + ver.ToString(3);
        var ts = ImGui.CalcTextSize(verText);
        dl.AddText(new Vector2(x, y - ts.Y * 0.5f), Theme.U32(Theme.Muted), verText);
        x += ts.X + Theme.Gap * 1.5f;

        var busy = _executor.Busy;
        var dot = busy ? Theme.Warn : _log.Count > 0 ? Theme.Good : Theme.Muted;
        dl.AddCircleFilled(new Vector2(x + 4f * s, y), 4f * s, Theme.U32(dot));
    }

    // ---- page --------------------------------------------------------------------------------

    protected override void DrawBody()
    {
        DrawStatusCard();
        DrawActions();
        DrawOptions();
        DrawResults();
        DrawPlan();

        // Publish the final tally once the run winds down (the card is drawn above, so the tally
        // shows from the next frame — same as before the reskin).
        var busy = _executor.Busy;
        if (_wasBusy && !busy && _log.Count > 0 && _status.StartsWith("Storing", StringComparison.Ordinal))
            _status = $"Finished: {_okCount} stored, {_manualCount} need attention, {_failCount} failed.";
        _wasBusy = busy;
    }

    private void DrawStatusCard()
    {
        var s = Theme.UiScale;
        var busy = _executor.Busy;
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var wrapW = w - Theme.Pad * 2f;
        var textH = ImGui.CalcTextSize(_status, false, wrapW).Y;
        var chipH = ImGui.GetTextLineHeight() + 6f * s;
        var h = Theme.Pad * 2f + chipH + 6f * s + textH;

        UiKit.Card(ImGui.GetWindowDrawList(), p, p + new Vector2(w, h), null, busy ? 0.10f : 0f);

        var (label, color) = busy
            ? ("STORING", Theme.Warn)
            : _log.Count > 0 ? ("FINISHED", Theme.Good) : ("IDLE", Theme.Muted);
        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad));
        UiKit.Chip(label, color);

        ImGui.SetCursorScreenPos(p + new Vector2(Theme.Pad, Theme.Pad + chipH + 6f * s));
        // Wrap-pos is window-local, so derive it from the cursor, not from the screen-space card
        // rect (screen-space values silently disable wrapping).
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - Theme.Pad);
        ImGui.TextColored(Theme.Text, _status);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(p + new Vector2(0f, h + Theme.Gap));
    }

    private void DrawActions()
    {
        var s = Theme.UiScale;
        var busy = _executor.Busy;
        var canStoreAll = _plan is { Error: null } && _plan.Plans.Count > 0;

        if (busy) ImGui.BeginDisabled();
        if (UiKit.GradientButton("##scan", "Scan", Theme.SurfaceAlt, Theme.Accent, 38f))
            DoScan();

        if (!canStoreAll) ImGui.BeginDisabled();
        if (UiKit.GradientButton("##storeall", "Store All Below", Theme.Accent, Theme.Good, 38f))
        {
            ResetCounters();
            _log.Clear();
            // The preview is a snapshot from before the first deposit; drop it so a stale per-set
            // button cannot be clicked mid- or post-run.
            _plan = null;
            _executor.Start(_executor.UseArmoireForEligibleSets);
            _status = "Storing… it re-scans between each set.";
        }
        if (!canStoreAll) ImGui.EndDisabled();

        if (busy)
        {
            if (UiKit.ChipButton("##abort", "Abort", false, ImGui.GetContentRegionAvail().X))
            {
                _executor.Abort();
                _status = "Aborted.";
            }
        }
        if (busy) ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0f, Theme.Gap));
    }

    private void DoScan()
    {
        ResetCounters();
        _log.Clear();
        _plan = _engine.BuildPlan();
        if (_plan.Error != null)
        {
            _status = _plan.Error;
            return;
        }

        var armoireRouted = 0;
        foreach (var p in _plan.Plans)
            if (p.Destination == StoreDestination.Armoire) armoireRouted++;

        _status = $"{_plan.Plans.Count} set(s) ready"
                  + (_executor.UseArmoireForEligibleSets && armoireRouted > 0 ? $" ({armoireRouted} → Armoire)" : "")
                  + $", {_plan.AlreadyStoredDuplicates.Count} duplicate(s), "
                  + $"{_plan.GearsetPieces.Count} gear-set piece(s) left alone.";
    }

    private void DrawOptions()
    {
        var s = Theme.UiScale;
        UiKit.Section(FontAwesomeIcon.Cog, "options");

        var busy = _executor.Busy;
        if (busy) ImGui.BeginDisabled();
        var armoire = _executor.UseArmoireForEligibleSets;
        var now = UiKit.Toggle("##armoire-toggle", armoire);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off (default): every set is stored in the dresser as an outfit glamour — the seasonal checkbox dialog is automated.\n"
                + "On: seasonal/event sets go to the Armoire piece-by-piece instead (no dresser slot or prisms used).");
        if (now != armoire)
            _executor.UseArmoireForEligibleSets = now;

        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, "Send Armoire-eligible sets to the Armoire instead");
        if (busy) ImGui.EndDisabled();
        ImGui.Dummy(new Vector2(0f, 4f * s));
    }

    private void DrawResults()
    {
        if (_log.Count == 0) return;

        UiKit.Section(FontAwesomeIcon.ListUl, "results");
        foreach (var o in _log)
        {
            var (tag, color) = o.NeedsManual
                ? ("MANUAL", Theme.Warn)
                : o.Success ? ("OK", Theme.Good) : ("SKIP", Theme.Danger);
            UiKit.Chip(tag, color);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextWrapped($"{o.SetName} — {o.Detail}");
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawPlan()
    {
        if (_plan is not { Error: null }) return;
        var plan = _plan;

        if (plan.Plans.Count == 0 && plan.AlreadyStoredDuplicates.Count == 0 && plan.GearsetPieces.Count == 0)
        {
            UiKit.Section(FontAwesomeIcon.Tasks, "preview");
            ImGui.TextColored(Theme.Muted, "Nothing to sort. No set pieces in inventory match known outfits.");
            return;
        }

        if (plan.Plans.Count > 0)
        {
            UiKit.Section(FontAwesomeIcon.Tasks, "preview");
            var busy = _executor.Busy;
            foreach (var p in plan.Plans)
                DrawPlanCard(p, busy);
        }

        if (plan.AlreadyStoredDuplicates.Count > 0)
        {
            UiKit.Section(FontAwesomeIcon.Recycle, "already stored — safe to sell");
            ImGui.TextColored(Theme.Warn,
                $"{plan.AlreadyStoredDuplicates.Count} piece(s) are already in the dresser and were left in your bags:");
            foreach (var d in plan.AlreadyStoredDuplicates)
                ImGui.BulletText($"item {d.ItemId} in {d.Container} slot {d.Slot}");
        }

        if (plan.GearsetPieces.Count > 0)
        {
            UiKit.Section(FontAwesomeIcon.ShieldAlt, "registered to a saved gear set");
            ImGui.TextColored(Theme.Warn,
                $"{plan.GearsetPieces.Count} piece(s) were left alone because a saved gear set uses them:");
            foreach (var d in plan.GearsetPieces)
                ImGui.BulletText($"item {d.ItemId} in {d.Container} slot {d.Slot}");
        }
    }

    private void DrawPlanCard(SetPlan p, bool busy)
    {
        var s = Theme.UiScale;
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var destination = p.Destination == StoreDestination.Armoire && _executor.UseArmoireForEligibleSets
            ? "Armoire"
            : "Dresser";
        var kind = p.Kind == PlanKind.CompleteExisting ? "COMPLETE" : "NEW";
        var kindColor = p.Kind == PlanKind.CompleteExisting ? Theme.Good : Theme.Accent;

        var extraLines = (p.AlreadyFilled.Count > 0 ? 1 : 0) + (p.ModelDuplicateItemIds.Count > 0 ? 1 : 0);
        var rows = 1 + p.Pieces.Count + extraLines;
        var h = Theme.Pad * 2f + rows * lineH + Theme.Gap + 24f * s;

        var top = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        UiKit.Card(ImGui.GetWindowDrawList(), top, top + new Vector2(w, h));

        ImGui.SetCursorScreenPos(top + new Vector2(Theme.Pad, Theme.Pad));
        UiKit.Chip($"{kind} → {destination}", kindColor);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Text, p.Set.Name);

        foreach (var (slot, piece) in p.Pieces)
        {
            ImGui.TextColored(Theme.Muted, $"{slot}: item {piece.ItemId} in {piece.Container} slot {piece.Slot}");
            if (p.ModelDuplicateItemIds.Contains(piece.ItemId))
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Warn, "(model already in dresser)");
            }
        }

        if (p.AlreadyFilled.Count > 0)
            ImGui.TextColored(Theme.Muted, $"Already in dresser: {string.Join(", ", p.AlreadyFilled)}");
        if (p.ModelDuplicateItemIds.Count > 0)
            ImGui.TextColored(Theme.Muted, "Amber piece(s) share an appearance with something already stored.");

        if (busy) ImGui.BeginDisabled();
        if (UiKit.ChipButton($"##store_{p.Set.SetItemId}", "Store this set", false, 150f * s))
        {
            ResetCounters();
            _plan = null;
            _executor.StartSingle(p.Set.SetItemId, _executor.UseArmoireForEligibleSets);
            _status = "Storing…";
        }
        if (busy) ImGui.EndDisabled();

        ImGui.SetCursorScreenPos(top + new Vector2(0f, h + Theme.Gap));
    }

    private void ResetCounters()
    {
        _okCount = 0;
        _manualCount = 0;
        _failCount = 0;
    }
}
