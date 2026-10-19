using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;

namespace SunriseStudio.Editor;

/** Value tiles, name and value pairs, and the small names that head a value. */
public static partial class StudioUi
{
    // Value tiles: at least 150 wide, at most 240, four to a line, 12 apart.
    private const float TileMinimum = 150, TileMaximum = 240, TileGap = 12;
    private const int TilesPerLine = 4;
    // Names and labels at this share of the body size.
    private const float NameSize = .86f;
    // Name and value pairs sit this far apart on a line.
    private const float PairGap = 16;

    /**
     * Value tiles: a small name over each control, three or four to a line and fewer in a narrow pane.
     * @param count Tiles on the line; fewer than four widen each up to the maximum.
     */
    public sealed class Tiles
    {
        private readonly float right;
        private bool any;
        public float Width { get; }

        public Tiles(int count)
        {
            float line = ImGui.GetContentRegionAvail().X, gap = TileGap * CreationWidgets.Scale;
            int fit = Math.Clamp((int)((line + gap) / (TileMinimum * CreationWidgets.Scale + gap)), 1,
                Math.Clamp(count, 1, TilesPerLine));
            Width = Math.Min(TileMaximum * CreationWidgets.Scale, (line - gap * (fit - 1)) / fit);
            right = ImGui.GetCursorScreenPos().X + line;
        }

        /**
         * Starts a tile, beside the last one when it fits, and sets the next item's width; End closes it.
         * @param changed Shows Reset beside the name.
         * @return True when Reset was clicked.
         */
        public bool Begin(string name, string hint = "", bool changed = false)
        {
            float gap = TileGap * CreationWidgets.Scale;
            if (any && ImGui.GetItemRectMax().X + gap + Width <= right + 1) ImGui.SameLine(0, gap);
            any = true;
            ImGui.PushID(name);
            ImGui.BeginGroup();
            bool reset = FieldName(name, hint, changed, Width);
            ImGui.SetNextItemWidth(Width);
            return reset;
        }

        public void End() { ImGui.EndGroup(); ImGui.PopID(); }

        /** A value this item cannot change here, with why on hover; an empty value draws nothing. */
        public void Fixed(string name, string? value, string why)
        {
            if (string.IsNullOrEmpty(value)) return;
            Begin(name, why);
            ImGui.BeginDisabled();
            ImGui.Button(value + "##fixed", new(Width, 0));
            ImGui.EndDisabled();
            Tip(why);
            End();
        }
    }


    /** Name and value pairs on shared lines, each beside the last when it fits; an editable value takes a field's box. */
    public sealed class Pairs
    {
        private readonly float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        private bool any;

        /** @return True when an editable value was clicked. */
        public bool Value(string name, string value, bool editable, string hint = "")
        {
            var style = ImGui.GetStyle();
            float gap = PairGap * CreationWidgets.Scale, width = ImGui.CalcTextSize(name).X + style.ItemInnerSpacing.X
                + ImGui.CalcTextSize(value).X + style.FramePadding.X * 2;
            if (any && ImGui.GetItemRectMax().X + gap + width <= right) ImGui.SameLine(0, gap);
            any = true;
            ImGui.PushID(name);
            ImGui.BeginGroup();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Muted, name);
            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            bool clicked = editable && FieldButton(value + "##value");
            if (!editable) ImGui.TextUnformatted(value);
            ImGui.EndGroup();
            Tip(hint);
            ImGui.PopID();
            return clicked;
        }

        /** A value this item cannot change here, with why on hover; an empty value draws nothing. */
        public void Fixed(string name, string? value, string why)
        {
            if (!string.IsNullOrEmpty(value)) Value(name, value, false, why);
        }
    }

    /** A value that opens its editor, in an input field's box so it reads as editable. */
    private static bool FieldButton(string label, Vector2 size = default)
    {
        var colors = ImGui.GetStyle().Colors;
        ImGui.PushStyleColor(ImGuiCol.Button, colors[(int)ImGuiCol.FrameBg]);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colors[(int)ImGuiCol.FrameBgHovered]);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, colors[(int)ImGuiCol.FrameBgActive]);
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    /**
     * A value's name, small: secondary while the value is its original, the text colour with Reset at the right once not.
     * @return True when Reset was clicked.
     */
    public static bool FieldName(string name, string hint, bool changed, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float size = ImGui.GetFontSize() * NameSize, height = ImGui.GetTextLineHeight(), reserve = 0;
        bool reset = false;
        if (changed)
        {
            float text = ImGui.CalcTextSize("Reset").X * NameSize;
            reserve = text + 6 * CreationWidgets.Scale;
            ImGui.SetCursorScreenPos(new(at.X + width - text, at.Y));
            reset = ImGui.InvisibleButton("##reset", new(text, height));
            Tip("Restore the original value");
            draw.AddText(ImGui.GetFont(), size, new(at.X + width - text, at.Y + (height - size) * .5f),
                ImGui.GetColorU32(ImGui.IsItemHovered() ? StudioTheme.Accent : StudioTheme.Muted), "Reset");
            ImGui.SetCursorScreenPos(at);
        }
        ImGui.Dummy(new(Math.Max(1, width - reserve), height));
        Tip(hint.Length == 0 ? "" : $"{name}\n{hint}");
        draw.PushClipRect(at, new(at.X + width - reserve, at.Y + height), true);
        draw.AddText(ImGui.GetFont(), size, new(at.X, at.Y + (height - size) * .5f),
            ImGui.GetColorU32(changed ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : StudioTheme.Muted), name);
        draw.PopClipRect();
        return reset;
    }
}
