using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

public sealed partial class InvestmentPage
{
    /**
     * An item's display record: the text and icon the item shows. Each value names the field and string it reads;
     * Edit on the creation bar changes them for a project item.
     */
    private static void DrawDisplay(StudioWorkspace workspace, InvestmentItem item, byte[] display)
    {
        if (ItemPreview.Shared is { } preview)
        {
            if (preview.Card(item, "Item Definition", CreationWidgets.Scale)) workspace.Select(item.Tag);
        }
        else
        {
            ImGui.TextUnformatted(item.Name);
            if (ImGui.Button("Item Definition")) workspace.Select(item.Tag);
        }
        if (display.Length < InvestmentItemClone.DisplayLength) { ImGui.TextDisabled("Display record is truncated"); return; }
        StudioUi.Title("Display Properties");
        if (!ImGui.BeginTable("display", 2, ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
        Text("Name", item.Name, InvestmentItemClone.DisplayName);
        Text("Type", item.TypeName, InvestmentItemClone.DisplayType);
        Text("Description", item.Description, InvestmentItemClone.DisplayFlavor);
        ushort icon = InvestmentItemClone.ReadDisplayIcon(display);
        Row("Icon", icon == ushort.MaxValue ? "None" : $"Row {icon}",
            $"Display +{InvestmentItemClone.DisplayIcon}: u16 row of the shared icon table; 0xFFFF is none.");
        ImGui.EndTable();

        void Text(string name, string value, int offset)
        {
            var (bank, hash) = InvestmentItemClone.ReadDisplayString(display, offset);
            Row(name, value.Length != 0 ? value : "None", $"Display +{offset}: string bank {bank}, hash {hash:X8}");
        }
        static void Row(string name, string value, string source)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(StudioTheme.Muted, name);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(value);
            StudioUi.Tip(source);
        }
    }
}
