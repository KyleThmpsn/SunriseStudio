using ImGuiNET;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Back and Forward live on the toolbar. */
    private void DrawAssetHeader(StudioDocument document) => ImGui.TextUnformatted(document.Name);

    /**
     * The model the preview draws, above the preview: the entity owner, which related assets retain,
     * or the guardian wearing an armor item.
     */
    private void DrawPreviewChoice(StudioDocument document, float width)
    {
        if (document.Wearers.Count != 0)
        {
            var context = document.OpenContext;
            var current = document.Wearers.FirstOrDefault(value => value.Class.BodyTag == context?.EntityTag
                && value.Female == context.ArmorFemale) ?? document.Wearers[0];
            ImGui.SetNextItemWidth(width);
            if (!ImGui.BeginCombo("##armor-wearer", current.Name)) return;
            foreach (var wearer in document.Wearers)
                if (ImGui.Selectable(wearer.Name, wearer == current) && wearer != current)
                    workspace.Select(document.Entry.TagHash, context: new AssetOpenContext(EntityTag: wearer.Class.BodyTag)
                        { ArmorItemTag = document.Entry.TagHash, ArmorFemale = wearer.Female });
            ImGui.EndCombo();
            return;
        }
        if (document.Connections is not { EntityCandidates.Count: > 0 } connections) return;
        uint owner = connections.AnimationEntityTag != 0 ? connections.AnimationEntityTag
            : document.OpenContext?.EntityTag ?? 0;
        string label = owner == 0 ? "Choose entity owner" : workspace.DisplayName(owner);
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo("##animation-owner", label)) return;
        foreach (uint candidate in connections.EntityCandidates)
            if (ImGui.Selectable(workspace.DisplayName(candidate) + "##" + candidate, candidate == owner))
            {
                var nextContext = document.OpenContext is { WeaponItemTag: > 0 } weapon
                    ? weapon with { EntityTag = candidate } : new AssetOpenContext(EntityTag: candidate);
                workspace.Select(document.Entry.TagHash, context: nextContext);
                ShowAssetPage("sunrise.preview");
            }
        ImGui.EndCombo();
    }
}
