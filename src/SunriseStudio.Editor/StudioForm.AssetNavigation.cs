using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Back and Forward live on the toolbar; a document with no view picks its entity owner here. */
    private void DrawAssetHeader(StudioDocument document)
    {
        ImGui.TextUnformatted(document.Name);
        if (document.Scene is null && document.Texture is null) DrawPreviewChoice(document, -1);
    }

    /**
     * The model the preview draws, above the preview: the entity owner, which related assets retain, an item's art
     * arrangement or one entity of it, or the guardian wearing an armor item.
     */
    private void DrawPreviewChoice(StudioDocument document, float width)
    {
        if (document.Wearers.Count != 0)
        {
            var context = document.OpenContext;
            var current = document.Wearers.FirstOrDefault(value => value.Class.BodyTag == context?.EntityTag
                && value.Female == context.ArmorFemale) ?? document.Wearers[0];
            // The item's model entity, named like the weapon owners, with the guardian it is worn by.
            string Label(ArmorWearer wearer) => $"{workspace.DisplayName(wearer.EntityTag)} - {wearer.Name}";
            ImGui.SetNextItemWidth(width);
            if (!ImGui.BeginCombo("##armor-wearer", Label(current))) return;
            foreach (var wearer in document.Wearers)
                if (ImGui.Selectable(Label(wearer), wearer == current) && wearer != current)
                    workspace.Select(document.Entry.TagHash, context: new AssetOpenContext(EntityTag: wearer.Class.BodyTag)
                        { ItemModelTag = document.Entry.TagHash, ArmorFemale = wearer.Female });
            ImGui.EndCombo();
            return;
        }
        if (document.Connections is not { EntityCandidates.Count: > 0 } connections
            || connections.EntityCandidates is [var only] && only == document.Entry.TagHash) return;
        uint owner = connections.AnimationEntityTag != 0 ? connections.AnimationEntityTag
            : document.OpenContext?.EntityTag ?? 0;
        // An item model lists its whole art arrangement first, as entity 0: its row in the arrangement table.
        bool art = document.ArtArrangement >= 0;
        string Name(uint entity) => entity != 0 ? workspace.DisplayName(entity)
            : $"{workspace.DisplayName(GuardianAppearance.ArrangementTable)} / Row {document.ArtArrangement}";
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo("##animation-owner", owner != 0 || art ? Name(owner) : "Choose entity owner")) return;
        foreach (uint candidate in art ? connections.EntityCandidates.Prepend(0u) : connections.EntityCandidates)
            if (ImGui.Selectable(Name(candidate) + "##" + candidate, candidate == owner))
            {
                var nextContext = document.OpenContext is { } held && (held.WeaponItemTag > 0 || held.ItemModelTag > 0)
                    ? held with { EntityTag = candidate } : new AssetOpenContext(EntityTag: candidate);
                workspace.Select(document.Entry.TagHash, context: nextContext);
                ShowAssetPage("sunrise.preview");
            }
        ImGui.EndCombo();
    }
}
