using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GlamourSorter;

public sealed class MainWindow : Window
{
    private readonly GlamourSortEngine _engine;
    private readonly StoreExecutor _executor;

    private SortResult? _plan;
    private readonly List<StoreOutcome> _log = new();
    private string _status = "Open your Glamour Dresser, then click Scan.";

    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Yellow = new(0.90f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 Red = new(0.90f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);

    public MainWindow(GlamourSortEngine engine, StoreExecutor executor)
        : base("Glamour Sorter##gsort")
    {
        _engine = engine;
        _executor = executor;
        _executor.OnResult = o => _log.Add(o);
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextWrapped(_status);
        ImGui.Separator();

        var busy = _executor.Busy;

        {
            var armoire = _executor.UseArmoireForEligibleSets;
            if (ImGui.Checkbox("Send Armoire-eligible sets to the Armoire instead", ref armoire))
                _executor.UseArmoireForEligibleSets = armoire;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off (default): every set is stored in the dresser as an outfit\n"
                    + "glamour — the seasonal checkbox dialog is automated.\n"
                    + "On: seasonal/event sets go to the Armoire piece-by-piece instead\n"
                    + "(no dresser slot or prisms used).");
        }

        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Scan"))
        {
            _log.Clear();
            _plan = _engine.BuildPlan();
            _status = _plan.Error
                      ?? $"{_plan.Plans.Count} set(s) ready to store, "
                         + $"{_plan.AlreadyStoredDuplicates.Count} duplicate piece(s) skipped.";
        }

        ImGui.SameLine();
        var canStoreAll = _plan is { Error: null } && _plan.Plans.Count > 0;
        if (!canStoreAll) ImGui.BeginDisabled();
        if (ImGui.Button("Store All Below"))
        {
            _log.Clear();
            _executor.Start(_executor.UseArmoireForEligibleSets);
            _status = "Storing… watch the dresser. It re-scans between each set.";
        }
        if (!canStoreAll) ImGui.EndDisabled();
        if (busy) ImGui.EndDisabled();

        if (busy)
        {
            ImGui.SameLine();
            if (ImGui.Button("Abort"))
            {
                _executor.Abort();
                _status = "Aborted.";
            }
        }

        ImGui.Separator();

        if (_plan is { Error: null })
            DrawPlan(_plan, busy);

        if (_log.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Results");
            foreach (var o in _log)
            {
                var tag = o.Success ? "OK" : o.NeedsManual ? "MANUAL" : "SKIP";
                var color = o.Success ? Green : o.NeedsManual ? Yellow : Red;
                ImGui.TextColored(color, tag);
                ImGui.SameLine();
                ImGui.TextWrapped($"{o.SetName} — {o.Detail}");
            }
        }
    }

    private void DrawPlan(SortResult plan, bool busy)
    {
        if (plan.Plans.Count == 0 && plan.AlreadyStoredDuplicates.Count == 0)
        {
            ImGui.TextColored(Grey, "Nothing to sort. No set pieces in inventory match known outfits.");
            return;
        }

        foreach (var p in plan.Plans)
        {
            var destination = p.Destination == StoreDestination.Armoire && _executor.UseArmoireForEligibleSets
                ? "Armoire"
                : "Dresser";
            var header = p.Kind == PlanKind.CompleteExisting
                ? $"[Complete → {destination}] {p.Set.Name}  (+{p.Pieces.Count})"
                : $"[New → {destination}]  {p.Set.Name}  ({p.Pieces.Count})";

            if (ImGui.CollapsingHeader($"{header}##{p.Set.SetItemId}_{(int)p.Kind}"))
            {
                foreach (var (slot, piece) in p.Pieces)
                    ImGui.BulletText($"{slot}: item {piece.ItemId} from {piece.Container} slot {piece.Slot}");

                if (p.AlreadyFilled.Count > 0)
                    ImGui.TextColored(Grey, $"Already in dresser: {string.Join(", ", p.AlreadyFilled)}");

                if (busy) ImGui.BeginDisabled();
                if (ImGui.Button($"Store this set##do_{p.Set.SetItemId}_{(int)p.Kind}"))
                    _executor.StartSingle(p.Set.SetItemId, _executor.UseArmoireForEligibleSets);
                if (busy) ImGui.EndDisabled();
            }
        }

        if (plan.AlreadyStoredDuplicates.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(Yellow,
                $"{plan.AlreadyStoredDuplicates.Count} piece(s) already stored (safe to sell — left in your bags):");
            foreach (var d in plan.AlreadyStoredDuplicates)
                ImGui.BulletText($"item {d.ItemId} in {d.Container} slot {d.Slot}");
        }
    }
}
