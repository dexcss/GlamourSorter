using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace GlamourSorter;

/// <summary>
/// Item names, icons and category labels for the dresser UI, with per-id caching because the
/// window redraws every frame and a sheet lookup per row per frame is wasteful.
/// </summary>
public static class ItemDisplay
{
    private static readonly Dictionary<uint, string> NameCache = new();
    private static readonly Dictionary<uint, ushort> IconCache = new();
    private static readonly Dictionary<uint, string> CategoryCache = new();

    public static string Name(uint itemId)
    {
        if (NameCache.TryGetValue(itemId, out var cached))
            return cached;

        var name = $"Item {itemId}";
        try
        {
            var row = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (row != null)
            {
                var text = row.Value.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(text))
                    name = text;
            }
        }
        catch
        {
            // Keep the numeric fallback.
        }

        NameCache[itemId] = name;
        return name;
    }

    /// <summary>ItemUICategory name ("Rings", "Body", …), or an empty string when unknown.</summary>
    public static string Category(uint itemId)
    {
        if (CategoryCache.TryGetValue(itemId, out var cached))
            return cached;

        var category = string.Empty;
        try
        {
            var row = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (row != null)
            {
                var categoryId = row.Value.ItemUICategory.RowId;
                var categoryRow = Svc.Data.GetExcelSheet<ItemUICategory>()?.GetRowOrDefault(categoryId);
                if (categoryRow != null)
                    category = categoryRow.Value.Name.ExtractText();
            }
        }
        catch
        {
            // Empty fallback.
        }

        CategoryCache[itemId] = category;
        return category;
    }

    public static ushort IconId(uint itemId)
    {
        if (IconCache.TryGetValue(itemId, out var cached))
            return cached;

        ushort icon = 0;
        try
        {
            var row = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (row != null)
                icon = row.Value.Icon;
        }
        catch
        {
            icon = 0;
        }

        IconCache[itemId] = icon;
        return icon;
    }

    /// <summary>Draws the item icon (blank spacer of the same size if it cannot be loaded).</summary>
    public static void Icon(uint itemId, float size)
    {
        var icon = IconId(itemId);
        if (icon != 0)
        {
            try
            {
                var tex = Svc.Texture.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(size, size));
                return;
            }
            catch
            {
                // Fall through to the spacer so layout stays aligned.
            }
        }

        ImGui.Dummy(new Vector2(size, size));
    }
}
