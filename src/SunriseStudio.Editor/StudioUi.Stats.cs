using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;

namespace SunriseStudio.Editor;

/** Stat bars, the values beside them and a Total row, as the item tooltip draws them. */
public static partial class StudioUi
{
    /** What plugs add to a stat and take from it. */
    private static readonly Vector4 Added = new(.47f, .78f, .5f, 1), Taken = new(.9f, .45f, .4f, 1);
    // The raw value box is this wide and a bar never narrower than this, before scaling.
    private const float RawBox = 64, ShortestBar = 40;

    /** Where a stat row's parts start, in window coordinates: the name, the bar, then the raw value box. */
    public readonly record struct StatColumns(float Left, float Name, float Bar, float BarWidth, float Raw, float RawWidth)
    {
        /** Columns across the rest of the line from the cursor; the name column fits the widest name, up to 40% of it. */
        public static StatColumns Here(float widest)
        {
            float scale = CreationWidgets.Scale, gap = ImGui.GetStyle().ItemSpacing.X, width = ImGui.GetContentRegionAvail().X;
            float left = ImGui.GetCursorPosX(), name = Math.Min(width * .4f, widest);
            float reading = ImGui.CalcTextSize("0000").X, raw = RawBox * scale;
            float bar = Math.Max(ShortestBar * scale, width - name - reading - raw - ImGui.GetFrameHeight() - gap * 4);
            float barX = left + name + gap;
            return new(left, name, barX, bar, barX + bar + gap + reading + gap, raw);
        }
    }

    /**
     * A stat bar as the item tooltip draws it: a near-white fill on a faint white track; the maximum fills it.
     * @param start The base value, when plugs change it.
     */
    public static void StatBar(float width, int value, int maximum = 100, int? start = null)
    {
        var at = ImGui.GetCursorScreenPos();
        float line = ImGui.GetFrameHeight(), height = 10 * CreationWidgets.Scale;
        ImGui.Dummy(new(width, line));
        var min = new Vector2(at.X, at.Y + (line - height) * .5f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(new Vector4(1, 1, 1, .14f)));
        float Fill(int amount) => width * Math.Clamp(amount / (float)Math.Max(1, maximum), 0, 1);
        // With a start, the base fills white and what plugs add or take reads past or over its end.
        float filled = Fill(value), from = Fill(start ?? value), white = Math.Min(filled, from);
        void Span(float left, float right, Vector4 color)
        {
            if (right > left)
                draw.AddRectFilled(min + new Vector2(left, 0), min + new Vector2(right, height), ImGui.GetColorU32(color));
        }
        Span(0, white, new Vector4(.93f, .93f, .93f, 1));
        Span(from, filled, Added);
        Span(filled, from, Taken);
    }

    /**
     * A stat's bar, when it has one, then the value the game shows at this x. What plugs change reads green or red on
     * the bar, or after a number with no bar.
     * @param start The value before the plugs that change it.
     * @param tip What the value is made of.
     */
    public static void StatReading(float x, float width, int value, int start, int maximum, bool bar, string tip,
        bool dim = false)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(x);
        if (bar)
        {
            StatBar(width, value, maximum, value == start ? null : start);
            ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);
        }
        ImGui.AlignTextToFramePadding();
        // A value plugs raise reads green and one they lower reads red.
        var colors = ImGui.GetStyle().Colors;
        ImGui.TextColored(dim ? colors[(int)ImGuiCol.TextDisabled] : value > start ? Added : value < start ? Taken
            : colors[(int)ImGuiCol.Text], value.ToString(CultureInfo.InvariantCulture));
        Tip(tip);
    }

    /** A Total row under stat bars: its name ending where the stat names end, the sum where the bars start. */
    public static void StatTotal(float nameEnd, float x, int total)
    {
        ImGui.SetCursorPosX(nameEnd - ImGui.CalcTextSize("Total").X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(StudioTheme.Muted, "Total");
        ImGui.SameLine();
        ImGui.SetCursorPosX(x);
        ImGui.TextUnformatted(total.ToString(CultureInfo.InvariantCulture));
    }
}
