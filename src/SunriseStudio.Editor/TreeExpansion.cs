using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Expand All and Collapse All for a page's folding rows. The button gives the rows new ids, so every row starts as the
 * button set; until it is pressed, each row keeps its own default.
 */
public sealed class TreeExpansion
{
    private bool? open;
    private int generation;

    /** The width the button takes after a control on its line, gap included. */
    public static float Width => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;

    /** The button, on the current line. */
    public void Button()
    {
        bool expand = open != true;
        if (StudioUi.IconButton("open-all", expand ? StudioIcon.ExpandAll : StudioIcon.CollapseAll,
            expand ? "Expand All" : "Collapse All"))
        { open = expand; generation++; }
    }

    /** Starts the rows' id scope; End closes it. */
    public void Begin() => ImGui.PushID(generation);
    public void End() => ImGui.PopID();

    /** A row's flags: open after Expand All, closed after Collapse All, else as openByDefault says. */
    public ImGuiTreeNodeFlags Flags(ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None, bool openByDefault = false) =>
        open ?? openByDefault ? flags | ImGuiTreeNodeFlags.DefaultOpen : flags;
}
