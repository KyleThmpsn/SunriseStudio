using ImGuiNET;

namespace SunriseStudio.Viewport;

/** Frames with less vertical padding, for a menu whose rows should sit close. Pop ends what Push began. */
public static class CompactFrames
{
    // The share of the style's vertical frame padding a compact frame keeps.
    private const float Share = .4f;

    public static void Push()
    {
        var padding = ImGui.GetStyle().FramePadding;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, padding with { Y = padding.Y * Share });
    }

    public static void Pop() => ImGui.PopStyleVar();
}
