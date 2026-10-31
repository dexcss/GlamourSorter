using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace GlamourSorter.FancyUI;

/// <summary>
/// Design tokens for a "fancy" Dalamud UI, purple and deliberately blocky: near-black window,
/// violet surfaces, one purple accent, small corner radii, squared toggles and segments.
///
/// These are defaults, not laws. Rebrand by changing this file only:
///   - change the palette below (the accent drives headers, selection, focus),
///   - raise <see cref="Roundness"/> for a softer look (2f ≈ pill-shaped controls).
/// Every metric is multiplied by <see cref="UiScale"/> so the UI follows the game's UI scale.
/// </summary>
public static class Theme
{
    // ---- palette: purple default ----------------------------------------
    // Text/Muted/Border were lifted after the first real adoption (Market Helper, 2026-10-30):
    // the original #E9E4FA / #9A8FBE values blended into the near-black window on a live
    // screenshot and the user asked for readability. Contrast first; the look stays purple.
    public static readonly Vector4 WindowBg   = new(0.027f, 0.024f, 0.043f, 0.96f); // #07060B
    public static readonly Vector4 Surface    = new(0.110f, 0.090f, 0.188f, 1.00f); // #1C1730
    public static readonly Vector4 SurfaceAlt = new(0.153f, 0.125f, 0.251f, 1.00f); // #272040
    public static readonly Vector4 Border     = new(0.365f, 0.290f, 0.560f, 0.75f); // lifted #4B3A7A

    public static readonly Vector4 Accent     = new(0.608f, 0.420f, 1.000f, 1.00f); // #9B6BFF violet
    public static readonly Vector4 AccentSoft = new(0.608f, 0.420f, 1.000f, 0.30f);
    public static readonly Vector4 Text       = new(0.965f, 0.949f, 1.000f, 1.00f); // near-white violet
    public static readonly Vector4 Muted      = new(0.760f, 0.710f, 0.900f, 1.00f); // lifted #9A8FBE

    public static readonly Vector4 Good       = new(0.369f, 0.878f, 0.784f, 1.00f); // #5EE0C8 teal
    public static readonly Vector4 GoodSoft   = new(0.369f, 0.878f, 0.784f, 0.14f);
    public static readonly Vector4 Warn       = new(1.000f, 0.420f, 0.839f, 1.00f); // #FF6BD6 magenta
    public static readonly Vector4 WarnSoft   = new(1.000f, 0.420f, 0.839f, 0.14f);
    public static readonly Vector4 Danger     = new(1.000f, 0.361f, 0.424f, 1.00f); // #FF5C6C

    // ---- shape ----------------------------------------------------------
    /// <summary>
    /// Corner-radius multiplier. 1 = blocky default (4px cards, 3px controls).
    /// 2 = softer, 3+ = pill-shaped controls. Nothing else needs changing.
    /// </summary>
    public static float Roundness = 1f;

    public static float UiScale => ImGui.GetIO().FontGlobalScale;
    public static float RadiusCard => 4f * Roundness * UiScale;
    public static float RadiusControl => 3f * Roundness * UiScale;
    public static float RadiusTag => 2f * Roundness * UiScale;
    public static float Pad => 12f * UiScale;
    public static float Gap => 8f * UiScale;

    public static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    /// <summary>
    /// Colours pushed for the whole window (pair with <see cref="Pop"/>). ChildBg rides along so
    /// every BeginChild panel reads as a Surface card with zero per-call-site work, and FrameBg is
    /// the brighter hover-step value: on the original SurfaceAlt, slider tracks and input fields
    /// were near-invisible and a slider grab floated on nothing (live screenshot, Market Helper).
    /// </summary>
    private static readonly (ImGuiCol Col, Vector4 Value)[] Styles =
    {
        (ImGuiCol.WindowBg,              WindowBg),
        (ImGuiCol.PopupBg,               Surface),
        (ImGuiCol.Border,                Border),
        (ImGuiCol.FrameBg,               new Vector4(0.196f, 0.161f, 0.310f, 1f)),
        (ImGuiCol.FrameBgHovered,        new Vector4(0.227f, 0.188f, 0.353f, 1f)),
        (ImGuiCol.FrameBgActive,         new Vector4(0.260f, 0.220f, 0.400f, 1f)),
        (ImGuiCol.ChildBg,               Surface),
        (ImGuiCol.TitleBg,               Surface),
        (ImGuiCol.TitleBgActive,         SurfaceAlt),
        (ImGuiCol.CheckMark,             Accent),
        (ImGuiCol.SliderGrab,            Accent),
        (ImGuiCol.SliderGrabActive,      Accent),
        (ImGuiCol.Button,                SurfaceAlt),
        (ImGuiCol.ButtonHovered,         new Vector4(0.278f, 0.231f, 0.435f, 1f)),
        (ImGuiCol.ButtonActive,          Accent),
        (ImGuiCol.Header,                AccentSoft),
        (ImGuiCol.HeaderHovered,         new Vector4(0.608f, 0.420f, 1.000f, 0.45f)),
        (ImGuiCol.HeaderActive,          AccentSoft),
        (ImGuiCol.Separator,             Border),
        (ImGuiCol.ScrollbarBg,           WindowBg),
        (ImGuiCol.ScrollbarGrab,         Border),
        (ImGuiCol.ScrollbarGrabHovered,  Accent),
        (ImGuiCol.ScrollbarGrabActive,   Accent),
        (ImGuiCol.Tab,                   Surface),
        (ImGuiCol.TabHovered,            AccentSoft),
        (ImGuiCol.Text,                  Text),
        (ImGuiCol.TextDisabled,          Muted),
        (ImGuiCol.TextSelectedBg,        AccentSoft),
        (ImGuiCol.PlotLines,             Accent),
        (ImGuiCol.PlotHistogram,         Accent),
    };

    public static void Push()
    {
        foreach (var (col, value) in Styles)
            ImGui.PushStyleColor(col, value);
    }

    public static void Pop() => ImGui.PopStyleColor(Styles.Length);

    /// <summary>
    /// Blocky geometry for the stock widgets that remain (text fields, sliders, combos, tables).
    /// Pair with <see cref="PopVars"/>; call after <see cref="Push"/> in PreDraw, pop before Pop in
    /// PostDraw (reverse order). Without these the leftover stock widgets render with ImGui's
    /// default pill-ish rounding and cramped padding and break the blocky look.
    /// </summary>
    public static void PushVars()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, RadiusControl);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, RadiusCard);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Gap * 0.8f, Gap * 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Gap * 0.9f, Gap * 0.7f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(Gap * 0.6f, Gap * 0.4f));
    }

    public static void PopVars() => ImGui.PopStyleVar(8);
}
