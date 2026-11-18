using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Viewport;

/** The editor's dropdown, checkbox and section header, drawn one way everywhere. Each takes what ImGui's own takes. */
public static class StudioControls
{
    // The chevron and the checkbox are this share of the font height; the box's corners this share of a frame's.
    private const float ChevronSize = .8f, BoxSize = 1.05f, BoxRounding = .75f;
    // The check mark's three points in the box's unit square.
    private static readonly Vector2 CheckStart = new(.24f, .52f), CheckTurn = new(.43f, .7f), CheckEnd = new(.76f, .32f);

    /** The checkbox's box, in pixels at the current font size. */
    public static float BoxWidth => MathF.Round(ImGui.GetFontSize() * BoxSize);

    /** A dropdown with a chevron at the end of its frame in place of ImGui's arrow button. */
    public static bool BeginCombo(string label, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        // A combo with no preview is its arrow button alone, which stays ImGui's.
        if ((flags & ImGuiComboFlags.NoPreview) != 0) return ImGui.BeginCombo(label, preview, flags);
        // The frame's place and the window's draw list are read before the combo, which opens its popup's window.
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.CalcItemWidth(), height = ImGui.GetFrameHeight(), chevron = ImGui.GetFontSize() * ChevronSize;
        bool open = ImGui.BeginCombo(label, preview, flags | ImGuiComboFlags.NoArrowButton);
        ImGuiIcons.Draw(draw, StudioIcon.Expand, start + new Vector2(width - style.FramePadding.X - chevron, (height - chevron) / 2),
            chevron, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        return open;
    }

    /** A dropdown over a fixed list of choices. @return True when the choice changed. */
    public static bool Combo(string label, ref int current, string[] items, int count)
    {
        if (!BeginCombo(label, current >= 0 && current < count ? items[current] : "")) return false;
        bool changed = false;
        for (int index = 0; index < count; index++)
        {
            ImGui.PushID(index);
            if (ImGui.Selectable(items[index], index == current) && index != current) { current = index; changed = true; }
            ImGui.PopID();
        }
        ImGui.EndCombo();
        return changed;
    }

    /** A dropdown over choices that each end in a zero character, as ImGui's own takes them. */
    public static bool Combo(string label, ref int current, string items)
    {
        string[] choices = items.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return Combo(label, ref current, choices, choices.Length);
    }

    /**
     * A checkbox smaller than a frame: an outlined box that fills with the accent and a dark check when set. The row is
     * a frame high, so it lines up with the controls beside it.
     */
    public static bool Checkbox(string label, ref bool value)
    {
        var style = ImGui.GetStyle();
        // Text after "##" is the id only.
        int hidden = label.IndexOf("##", StringComparison.Ordinal);
        string shown = hidden < 0 ? label : label[..hidden];
        float box = BoxWidth, height = ImGui.GetFrameHeight();
        float text = shown.Length == 0 ? 0 : style.ItemInnerSpacing.X + ImGui.CalcTextSize(shown).X;
        var start = ImGui.GetCursorScreenPos();
        bool pressed = ImGui.InvisibleButton(label, new(box + text, height));
        if (pressed) value = !value;
        var draw = ImGui.GetWindowDrawList();
        var corner = start + new Vector2(0, MathF.Round((height - box) / 2));
        float rounding = style.FrameRounding * BoxRounding;
        if (value)
        {
            draw.AddRectFilled(corner, corner + new Vector2(box), ImGui.GetColorU32(ImGuiCol.CheckMark), rounding);
            uint mark = ImGui.GetColorU32(ImGuiCol.WindowBg);
            float stroke = MathF.Max(1.5f, box * .12f);
            draw.AddLine(corner + CheckStart * box, corner + CheckTurn * box, mark, stroke);
            draw.AddLine(corner + CheckTurn * box, corner + CheckEnd * box, mark, stroke);
        }
        else
        {
            bool hovered = ImGui.IsItemHovered();
            draw.AddRectFilled(corner, corner + new Vector2(box), ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg),
                rounding);
            draw.AddRect(corner, corner + new Vector2(box), ImGui.GetColorU32(hovered ? ImGuiCol.TextDisabled : ImGuiCol.Border),
                rounding);
        }
        if (shown.Length != 0)
            draw.AddText(start + new Vector2(box + style.ItemInnerSpacing.X, (height - ImGui.GetTextLineHeight()) / 2),
                ImGui.GetColorU32(ImGuiCol.Text), shown);
        return pressed;
    }

    /** A section header in the table header's quiet colour, so it does not read as a selected row. */
    public static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, ImGui.GetColorU32(ImGuiCol.FrameBgHovered));
        bool open = ImGui.CollapsingHeader(label, flags);
        ImGui.PopStyleColor(2);
        return open;
    }
}
