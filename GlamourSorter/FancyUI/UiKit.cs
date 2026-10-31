using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace GlamourSorter.FancyUI;

/// <summary>
/// The Fancy UI widget kit — blocky edition. Everything is custom-drawn with the window draw
/// list, so it looks nothing like stock ImGui: rectangular toggles, segmented blocks, square
/// gradient CTAs. Each widget consumes layout space via InvisibleButton/Dummy, so it composes
/// with normal ImGui layout.
/// </summary>
public static class UiKit
{
    private static readonly Dictionary<uint, float> Anim = new();

    // ---------------------------------------------------------------- cards

    /// <summary>Rounded card. Call before drawing its content; does not advance the cursor.</summary>
    public static void Card(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4? fill = null, float glow = 0f)
    {
        var r = Theme.RadiusCard;
        if (glow > 0f)
        {
            for (var i = 1; i <= 3; i++)
            {
                var c = new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, glow / i);
                dl.AddRectFilled(min - new Vector2(i, i), max + new Vector2(i, i), Theme.U32(c), r + i * Theme.UiScale);
            }
        }

        dl.AddRectFilled(min, max, Theme.U32(fill ?? Theme.Surface), r);
        dl.AddRect(min, max, Theme.U32(Theme.Border), r, ImDrawFlags.None, 1f * Theme.UiScale);
    }

    /// <summary>A card that behaves like a button (hover highlight + click).</summary>
    public static bool CardButton(string id, Vector2 size, Action<Vector2, Vector2>? draw = null)
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();

        Card(dl, p, p + size, hovered ? Theme.SurfaceAlt : Theme.Surface);
        draw?.Invoke(p, p + size);
        return ImGui.IsItemClicked();
    }

    // ------------------------------------------------------------- headings

    /// <summary>
    /// Small uppercase section label with a FontAwesome glyph, and a hairline rule underneath —
    /// a short accent tick at the left, then a faint line across the content width — so sections
    /// read as separate blocks. Added after the first adoption: without the rule, screenshot
    /// review showed everything running together on a long page.
    /// </summary>
    public static void Section(FontAwesomeIcon icon, string text)
    {
        var s = Theme.UiScale;
        ImGui.Dummy(new Vector2(0, 4f * s));

        // Capture the row's left edge and width before the items shift the cursor.
        var rowX = ImGui.GetCursorScreenPos().X;
        var rowW = ImGui.GetContentRegionAvail().X;

        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Theme.Accent, icon.ToIconString());
        ImGui.PopFont();

        ImGui.SameLine(0, 6f * s);
        ImGui.TextColored(Theme.Muted, text.ToUpperInvariant());

        var textMax = ImGui.GetItemRectMax();
        var lineY = textMax.Y + 5f * s;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(rowX, lineY), new Vector2(rowX + 18f * s, lineY + 1.5f * s),
            Theme.U32(new Vector4(Theme.Accent.X, Theme.Accent.Y, Theme.Accent.Z, 0.85f)), 1f * s);
        dl.AddRectFilled(new Vector2(rowX + 22f * s, lineY), new Vector2(rowX + rowW, lineY + 1f * s),
            Theme.U32(new Vector4(Theme.Border.X, Theme.Border.Y, Theme.Border.Z, 0.55f)));

        ImGui.Dummy(new Vector2(0, 7f * s));
    }

    // -------------------------------------------------------------- toggle

    /// <summary>Rectangular slide switch (blocky default). Returns the new value.</summary>
    public static bool Toggle(string id, bool value)
    {
        var s = Theme.UiScale;
        var size = new Vector2(40f * s, 22f * s);
        var p = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
            value = !value;

        var key = ImGui.GetID(id);
        var target = value ? 1f : 0f;
        var cur = Anim.TryGetValue(key, out var v) ? v : target;
        cur = Lerp(cur, target, ImGui.GetIO().DeltaTime * 16f);
        Anim[key] = cur;

        var dl = ImGui.GetWindowDrawList();
        var max = p + size;
        var track = Vector4.Lerp(Theme.SurfaceAlt, Theme.Accent, cur);
        dl.AddRectFilled(p, max, Theme.U32(track), Theme.RadiusControl);

        var knobW = size.Y - 4f * s;
        var knobX = Lerp(p.X + 2f * s, max.X - 2f * s - knobW, cur);
        var knob = hovered ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.88f, 0.85f, 0.94f, 1f);
        dl.AddRectFilled(new Vector2(knobX, p.Y + 2f * s),
            new Vector2(knobX + knobW, max.Y - 2f * s), Theme.U32(knob), Theme.RadiusTag);

        return value;
    }

    // ----------------------------------------------------------- segmented

    /// <summary>Rectangular segmented control. Returns the selected index.</summary>
    public static int Segmented(string id, string[] labels, int value, float width = 0f)
    {
        var s = Theme.UiScale;
        var h = 30f * s;
        if (width <= 0f)
            width = ImGui.GetContentRegionAvail().X;

        var p = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(width, h));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var n = Math.Max(1, labels.Length);
        var segW = width / n;
        var dl = ImGui.GetWindowDrawList();
        var max = p + new Vector2(width, h);

        dl.AddRectFilled(p, max, Theme.U32(Theme.SurfaceAlt), Theme.RadiusControl);

        var sel = Math.Clamp(value, 0, n - 1);
        var hlMin = new Vector2(p.X + segW * sel + 2f * s, p.Y + 2f * s);
        var hlMax = new Vector2(p.X + segW * (sel + 1) - 2f * s, max.Y - 2f * s);
        dl.AddRectFilled(hlMin, hlMax, Theme.U32(Theme.Surface), Theme.RadiusTag);
        dl.AddRect(hlMin, hlMax, Theme.U32(Theme.Border), Theme.RadiusTag, ImDrawFlags.None, 1f * s);

        for (var i = 0; i < n; i++)
        {
            var label = labels[i];
            var ts = ImGui.CalcTextSize(label);
            var tp = new Vector2(p.X + segW * i + (segW - ts.X) * 0.5f, p.Y + (h - ts.Y) * 0.5f);
            dl.AddText(tp, Theme.U32(i == sel ? Theme.Text : Theme.Muted), label);
        }

        if (clicked && hovered)
            value = Math.Clamp((int)((ImGui.GetIO().MousePos.X - p.X) / segW), 0, n - 1);

        return value;
    }

    // ---------------------------------------------------------- icon button

    /// <summary>Square icon button (FontAwesome glyph).</summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string tooltip = "", bool active = false, float size = 36f)
    {
        var s = Theme.UiScale;
        var box = new Vector2(size * s, size * s);
        var p = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, box);
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var dl = ImGui.GetWindowDrawList();
        Card(dl, p, p + box, active ? Theme.Accent : hovered ? Theme.SurfaceAlt : Theme.Surface);

        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var ts = ImGui.CalcTextSize(glyph);
        var col = active ? Theme.WindowBg : hovered ? Theme.Text : Theme.Muted;
        dl.AddText(p + (box - ts) * 0.5f, Theme.U32(col), glyph);
        ImGui.PopFont();

        if (tooltip.Length > 0 && hovered)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    // ------------------------------------------------------ gradient button

    /// <summary>
    /// Full-width call-to-action button with a left-to-right gradient. Square corners by default
    /// (blocky); raise <see cref="Theme.Roundness"/> for softer geometry.
    /// </summary>
    public static bool GradientButton(string id, string label, Vector4 left, Vector4 right, float height = 40f)
    {
        var s = Theme.UiScale;
        var h = height * s;
        var w = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        var max = p + new Vector2(w, h);

        ImGui.InvisibleButton(id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var a = hovered ? Brighten(left) : left;
        var b = hovered ? Brighten(right) : right;

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilledMultiColor(p, max, Theme.U32(a), Theme.U32(b), Theme.U32(b), Theme.U32(a));
        dl.AddRect(p, max, Theme.U32(Theme.Border), Theme.RadiusControl, ImDrawFlags.None, 1f * s);

        var ts = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(p.X + (w - ts.X) * 0.5f, p.Y + (h - ts.Y) * 0.5f),
            Theme.U32(new Vector4(1f, 1f, 1f, 1f)), label);

        return clicked;
    }

    // ---------------------------------------------------------------- chip

    /// <summary>Small rectangular tag (status, version, label).</summary>
    public static void Chip(string text, Vector4 color)
    {
        var s = Theme.UiScale;
        var ts = ImGui.CalcTextSize(text);
        var padX = 8f * s;
        var h = ts.Y + 6f * s;
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ts.X + padX * 2f, h);

        var dl = ImGui.GetWindowDrawList();
        var bg = new Vector4(color.X, color.Y, color.Z, 0.18f);
        dl.AddRectFilled(p, p + size, Theme.U32(bg), Theme.RadiusTag);
        dl.AddRect(p, p + size, Theme.U32(color), Theme.RadiusTag, ImDrawFlags.None, 1f * s);
        dl.AddText(p + new Vector2(padX, 3f * s), Theme.U32(color), text);

        ImGui.Dummy(size);
    }

    // ----------------------------------------------------------------- chip

    /// <summary>Compact rectangular button used for row/grid controls (also a toggle chip).</summary>
    public static bool ChipButton(string id, string label, bool active = false, float width = 0f, string tooltip = "")
    {
        var s = Theme.UiScale;
        var tsz = ImGui.CalcTextSize(label);
        if (width <= 0f)
            width = tsz.X + 20f * s;

        var h = 24f * s;
        var p = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(width, h));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var dl = ImGui.GetWindowDrawList();
        var max = p + new Vector2(width, h);
        var bg = active ? Theme.Accent : hovered ? Theme.SurfaceAlt : Theme.Surface;
        dl.AddRectFilled(p, max, Theme.U32(bg), Theme.RadiusControl);
        dl.AddRect(p, max, Theme.U32(active ? Theme.Accent : Theme.Border), Theme.RadiusControl, ImDrawFlags.None, 1f * s);

        var col = active ? Theme.WindowBg : hovered ? Theme.Text : Theme.Muted;
        dl.AddText(new Vector2(p.X + (width - tsz.X) * 0.5f, p.Y + (h - tsz.Y) * 0.5f), Theme.U32(col), label);

        if (tooltip.Length > 0 && hovered)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    /// <summary>Chip that flips a boolean: returns the new value.</summary>
    public static bool ToggleChip(string id, string label, bool value, float width = 0f, string tooltip = "")
    {
        if (ChipButton(id, label, value, width, tooltip))
            value = !value;
        return value;
    }

    // ------------------------------------------------------------- helpers

    private static float Lerp(float a, float b, float t) => a + (b - a) * MathF.Min(1f, t);

    private static Vector4 Brighten(Vector4 c) => new(
        MathF.Min(1f, c.X * 1.12f),
        MathF.Min(1f, c.Y * 1.12f),
        MathF.Min(1f, c.Z * 1.12f),
        c.W);
}
