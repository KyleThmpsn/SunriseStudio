using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** An item's card, and the icon and name a plug or perk control shows. */
public static partial class StudioUi
{
    // The card before scaling: its icon's side, the margin over and under the icon, and where its text starts.
    private const float CardIcon = 52, CardMargin = 1, CardTextTop = 4, CardLineGap = 2;
    // The card's name at this share of the body size.
    private const float CardNameSize = 1.1f;
    // A card is this much lighter than the panel in each colour, so it reads as one thing.
    private const float CardLift = 14 / 255f;
    // An icon inside a frame-high control keeps this margin, as a share of the frame's height.
    private const float LabelIconInset = 1 / 12f;

    /**
     * An item as a card across the width: its icon, its name with its hash, its type line, and an action at the
     * bottom right. A right click copies the hash.
     * @param icon The item's icon, or null while it reads or when it has none.
     * @return True when the action was clicked.
     */
    public static bool ItemCard(InvestmentItem item, GpuTexture? icon, string action)
    {
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float scale = CreationWidgets.Scale, margin = CardMargin * scale, top = CardTextTop * scale;
        float width = ImGui.GetContentRegionAvail().X, side = CardIcon * scale, height = side + margin * 2;
        var panel = style.Colors[(int)ImGuiCol.WindowBg];
        draw.AddRectFilled(at, at + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(panel.X + CardLift, panel.Y + CardLift, panel.Z + CardLift, 1)));
        if (icon is not null)
            InvestmentIconCache.DrawFitted(draw, icon, at + new Vector2(0, margin), at + new Vector2(side, side + margin));
        float left = side + style.ItemSpacing.X, title = ImGui.GetFontSize() * CardNameSize;
        string hash = item.Hash.ToString("X8", CultureInfo.InvariantCulture);
        float hashWidth = ImGui.CalcTextSize(hash).X, right = width - style.FramePadding.X;
        draw.AddText(at + new Vector2(right - hashWidth, top), ImGui.GetColorU32(ImGuiCol.TextDisabled), hash);
        draw.PushClipRect(at, at + new Vector2(right - hashWidth - style.ItemSpacing.X, height), true);
        draw.AddText(ImGui.GetFont(), title, at + new Vector2(left, top), ImGui.GetColorU32(StudioTheme.Rarity(item.Tier)),
            item.Name);
        draw.PopClipRect();
        draw.AddText(at + new Vector2(left, top + title + CardLineGap * scale), ImGui.GetColorU32(ImGuiCol.Text), item.TypeName);
        float button = ImGui.CalcTextSize(action).X + style.FramePadding.X * 2;
        ImGui.InvisibleButton("##card", new(Math.Max(1, right - button - style.ItemSpacing.X), height));
        Tip($"{item.Name}\nItem {item.Index}");
        if (ImGui.BeginPopupContextItem("##card-menu"))
        {
            if (ImGui.MenuItem("Copy Hash")) ImGui.SetClipboardText(hash);
            ImGui.EndPopup();
        }
        // The action is inset like the hash above it, from the right and from the bottom.
        ImGui.SetCursorScreenPos(at + new Vector2(right - button, height - ImGui.GetFrameHeight() - top));
        bool clicked = ImGui.Button(action);
        ImGui.SetCursorScreenPos(at + new Vector2(0, height + style.ItemSpacing.Y));
        ImGui.Dummy(new(width, 0));
        return clicked;
    }

    /**
     * An icon and a name inside a frame-high control at a screen position, as a plug button shows its plug.
     * @param draw The list of the window that holds the control, which a combo's open popup does not change.
     */
    public static void IconLabel(ImDrawListPtr draw, Vector2 at, float width, GpuTexture? icon, string text)
    {
        var style = ImGui.GetStyle();
        float frame = ImGui.GetFrameHeight(), inset = frame * LabelIconInset, size = frame - inset * 2;
        if (icon is not null) InvestmentIconCache.DrawFitted(draw, icon, at + new Vector2(inset), at + new Vector2(inset + size));
        float left = inset + size + style.FramePadding.X;
        draw.PushClipRect(at, at + new Vector2(width, frame), true);
        draw.AddText(at + new Vector2(left, style.FramePadding.Y), ImGui.GetColorU32(ImGuiCol.Text), text);
        draw.PopClipRect();
    }

    /** An icon as a square ImGui item, which stays empty while the icon reads or when there is none. */
    public static void Icon(GpuTexture? icon, float side)
    {
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new(side));
        if (icon is not null) InvestmentIconCache.DrawFitted(ImGui.GetWindowDrawList(), icon, at, at + new Vector2(side));
    }
}
