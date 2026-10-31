using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace GlamourSorter.FancyUI;

/// <summary>
/// Borderless Fancy UI window shell: a thin black buffer strip, a custom title bar with an accent
/// bar, a close button, a bottom-right resize grip, and a padded body region.
/// Derive from this instead of <see cref="Window"/> and implement <see cref="DrawBody"/>.
/// </summary>
public abstract class ShellWindow : Window
{
    private const float TopInsetPx = 2f;
    private const float HeaderHeightPx = 42f;

    private readonly string title;
    private bool dragging;

    protected ShellWindow(string id, string title, Vector2? minSize = null, Vector2? maxSize = null)
        : base(id, ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse)
    {
        this.title = title;
        // Do NOT leave Size pinned: Dalamud's WindowHost calls SetNextWindowSize(Size,
        // SizeCondition) every frame while Size is set, which makes the window unresizable.
        this.Size = new Vector2(760, 560);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = minSize ?? new Vector2(420, 300),
            MaximumSize = maxSize ?? new Vector2(1600, 1400),
        };
    }

    /// <summary>Draw the page content. The cursor is already inside the padded body child.</summary>
    protected abstract void DrawBody();

    /// <summary>
    /// Optional extras (version chip, status dots, …) drawn in the free span of the title bar
    /// between the title text and the close button. Coordinates are absolute screen space;
    /// draw with the window draw list. Added after the first adoption (Market Helper put a
    /// version label + one status dot per background runner here).
    /// </summary>
    protected virtual void DrawHeaderExtras(Vector2 freeMin, Vector2 freeMax)
    {
    }

    public override void PreDraw()
    {
        Theme.Push();
        Theme.PushVars();
    }

    public override void PostDraw()
    {
        Theme.PopVars();
        Theme.Pop();
    }

    public override void Draw()
    {
        var s = Theme.UiScale;
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();

        var topInset = TopInsetPx * s;
        var headerH = HeaderHeightPx * s;
        var headerTop = pos.Y + topInset;

        // Paint the whole window body so the top buffer is exactly WindowBg.
        dl.AddRectFilled(pos, pos + size, Theme.U32(Theme.WindowBg), Theme.RadiusCard);

        // Header plate: square top edge, sits below the hairline buffer.
        dl.AddRectFilled(new Vector2(pos.X, headerTop),
            new Vector2(pos.X + size.X, headerTop + headerH), Theme.U32(Theme.Surface));
        dl.AddLine(new Vector2(pos.X, headerTop + headerH),
            new Vector2(pos.X + size.X, headerTop + headerH), Theme.U32(Theme.Border), 1f * s);

        // Drag from the header.
        var mouse = ImGui.GetIO().MousePos;
        var inHeader = mouse.X >= pos.X && mouse.X <= pos.X + size.X
                    && mouse.Y >= headerTop && mouse.Y <= headerTop + headerH;
        if (inHeader && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            this.dragging = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            this.dragging = false;
        if (this.dragging)
            ImGui.SetWindowPos(pos + ImGui.GetIO().MouseDelta);

        // Accent bar + title.
        var titleY = headerTop + (headerH - ImGui.GetTextLineHeight()) * 0.5f;
        dl.AddRectFilled(new Vector2(pos.X + 14f * s, headerTop + headerH * 0.5f - 3f * s),
            new Vector2(pos.X + 20f * s, headerTop + headerH * 0.5f + 3f * s), Theme.U32(Theme.Accent), Theme.RadiusTag);
        dl.AddText(new Vector2(pos.X + 28f * s, titleY), Theme.U32(Theme.Text), this.title);

        // Extras between the title and the close button.
        DrawHeaderExtras(
            new Vector2(pos.X + 28f * s + ImGui.CalcTextSize(this.title).X + Theme.Gap, headerTop),
            new Vector2(pos.X + size.X - (26f * s) - 8f * s - Theme.Gap * 2f, headerTop + headerH));

        // Close button.
        var close = 26f;
        ImGui.SetCursorScreenPos(new Vector2(
            pos.X + size.X - (close * s) - 8f * s,
            headerTop + (headerH - close * s) * 0.5f));
        if (UiKit.IconButton("##fancy-close", FontAwesomeIcon.Times, "Close", false, close))
            this.IsOpen = false;

        // Body. Keep the left/right insets symmetric: an extra right gap stacked with the
        // scrollbar gutter made content lean left of centre (live screenshot, Market Helper).
        // The resize grip only occupies the bottom-right corner, which the bottom inset clears.
        ImGui.SetCursorScreenPos(new Vector2(pos.X + Theme.Pad, headerTop + headerH + Theme.Pad));
        var bodySize = new Vector2(size.X - Theme.Pad * 2f,
            size.Y - topInset - headerH - Theme.Pad * 2f - 8f * s);
        if (ImGui.BeginChild("##fancy-body", bodySize, false, ImGuiWindowFlags.None))
            this.DrawBody();
        ImGui.EndChild();

        this.DrawResizeGrip(pos, size);
    }

    /// <summary>Visible bottom-right grip; resizing works even when the native border does not.</summary>
    private void DrawResizeGrip(Vector2 pos, Vector2 size)
    {
        var s = Theme.UiScale;
        var box = 14f * s;
        var gp = new Vector2(pos.X + size.X - box - 2f * s, pos.Y + size.Y - box - 2f * s);

        ImGui.SetCursorScreenPos(gp);
        ImGui.InvisibleButton("##fancy-resize", new Vector2(box, box));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        var dl = ImGui.GetWindowDrawList();
        var col = active || hovered ? Theme.Accent : Theme.Muted;
        for (var i = 0; i < 3; i++)
        {
            var off = (4f + i * 4f) * s;
            dl.AddLine(new Vector2(gp.X + box - off, gp.Y + box),
                new Vector2(gp.X + box, gp.Y + box - off), Theme.U32(col), 1.5f * s);
        }

        if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var d = ImGui.GetIO().MouseDelta;
            var min = new Vector2(420f * s, 300f * s);
            ImGui.SetWindowSize(new Vector2(Math.Max(min.X, size.X + d.X), Math.Max(min.Y, size.Y + d.Y)));
        }
    }
}
