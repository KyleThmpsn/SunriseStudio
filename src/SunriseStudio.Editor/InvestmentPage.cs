using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/** Item and vendor pages share installed identities and the editor's delta transaction path. */
public sealed partial class InvestmentPage : IPreparedStudioPage
{
    private readonly Dictionary<ushort, ItemIcon> perkIcons = [];
    private IReadOnlyDictionary<ushort, InvestmentIcon>? preparedPerkIcons;
    private string iconError = "";
    private string editValue = "", plugSearch = "";
    private AssetProperty? editing;
    private IReadOnlyList<ushort>? plugChoices;
    private bool openEditor;
    private SunriseStudio.Packages.PackageCatalog? requestedCatalog;
    private long requestedOverlayRevision = -1, editingOverlayRevision = -1;
    private StudioDocument? editingDocument;
    private SunriseStudio.Projects.StudioProject? editingProject;
    private StudioDocument? requestedDocument;
    private InvestmentCatalog? requestedDetailCatalog;
    public string Id => "sunrise.investment";
    public string Title => "Investment";
    public void Prepare(StudioDocument? document, Dx11Renderer renderer) => Dispose();

    /** The window adopts detail textures between frames, before ImGui borrows their views. */
    public void PrepareDetail(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        InvalidateDrafts(workspace);
        var nextPerks = workspace.InvestmentDetailCurrent ? workspace.SelectedInvestmentPerkIcons : null;
        if (ReferenceEquals(nextPerks, preparedPerkIcons)) return;
        Dispose(); preparedPerkIcons = nextPerks;
        foreach (var (perk, image) in nextPerks ?? new Dictionary<ushort, InvestmentIcon>())
            try { perkIcons[perk] = ItemIcon.Upload(renderer, image); }
            catch (Exception error) when (error is not OutOfMemoryException) { iconError += $"Perk {perk}: {error.Message}\n"; }
    }

    public void Dispose()
    {
        iconError = "";
        foreach (var perkIcon in perkIcons.Values) perkIcon.Dispose();
        perkIcons.Clear(); preparedPerkIcons = null;
    }
    // The item and display index tables carry the item classes but are not items.
    public bool CanOpen(StudioDocument document) => document.Entry.TagHash is not (InvestmentCatalog.ItemIndexTag
        or InvestmentCatalog.DisplayIndexTag)
        && document.Entry.Reference is InvestmentCatalog.ItemClass or InvestmentCatalog.DisplayClass
            or InvestmentCatalog.VendorClass or 0x80807D84 or 0x808058EE;

    /** Detail and edits of the open item or vendor; the asset browser finds them. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Investment is not { } catalog)
        {
            if (workspace.Catalog is { } source && !workspace.IsBusy
                && (!ReferenceEquals(requestedCatalog, source) || requestedOverlayRevision != source.OverlayRevision))
            {
                requestedCatalog = source; requestedOverlayRevision = source.OverlayRevision;
                workspace.Run(workspace.LoadInvestment);
            }
            ImGui.BeginDisabled(workspace.IsBusy);
            if (ImGui.Button("Load investment")) workspace.Run(workspace.LoadInvestment);
            ImGui.EndDisabled();
            return;
        }
        // One request per document and catalog; a failed read waits for Retry.
        if (!workspace.InvestmentDetailCurrent && !workspace.IsBusy && workspace.Document is { } wanted
            && (!ReferenceEquals(requestedDocument, wanted) || !ReferenceEquals(requestedDetailCatalog, catalog)))
        {
            requestedDocument = wanted; requestedDetailCatalog = catalog;
            workspace.Run(workspace.LoadInvestmentDetail);
        }
        if (!workspace.InvestmentDetailCurrent && workspace.Document is not null)
        {
            if (workspace.IsBusy) ImGui.TextDisabled("Reading item");
            else
            {
                if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
                if (ImGui.SmallButton("Retry")) requestedDocument = null;
            }
        }
        else if (workspace.InvestmentDetailCurrent && workspace.SelectedInvestmentItem is { } item) DrawItem(context, catalog, item);
        else if (workspace.InvestmentDetailCurrent && workspace.SelectedInvestmentVendor is { } vendor) DrawVendor(context, catalog, vendor);
        else if (workspace.Document is { } document && catalog.FindItem(document.Entry.TagHash) is { } linked)
        { ImGui.TextUnformatted(linked.Name); if (ImGui.Button("Open item definition")) workspace.Select(linked.Tag); }
        else if (workspace.Document is { } shown && catalog.FindVendor(shown.Entry.TagHash) is { } seller)
        { ImGui.TextUnformatted(seller.Name); if (ImGui.Button("Open vendor")) workspace.Select(seller.Tag); }
        else ImGui.TextDisabled("Not an item or vendor");
        DrawEdit(context, catalog);
        DrawAddRow(context, catalog);
        DrawCostEditor(context, catalog);
    }

    /** Authored names win; the name index's owner label covers perks the game leaves unnamed. */
    private static string PerkLabel(StudioPageContext context, InvestmentCatalog catalog, int index) =>
        index < catalog.PerkCount && catalog.PerkDefinitions[index].Name.Length != 0 ? catalog.PerkDefinitions[index].Name
            : context.Workspace.Names?.PerkName((uint)index) ?? catalog.PerkName(index);

    /** One tree node per sale row, with its item, category and static costs. */
    private void DrawVendor(StudioPageContext context, InvestmentCatalog catalog, InvestmentVendorDetail vendor)
    {
        ImGui.TextUnformatted(vendor.Vendor.Name);
        StudioUi.Tip($"Vendor {vendor.Vendor.Index}\n{vendor.Vendor.Hash:X8}\n{vendor.Vendor.LabelSource}".TrimEnd());
        ImGui.TextDisabled($"{vendor.Sales.Count:N0} sale rows");
        foreach (var sale in vendor.Sales)
        {
            ImGui.PushID(sale.Index);
            if (ImGui.TreeNode($"{sale.Name}##sale{sale.Index}"))
            {
                DrawSaleActions(context, sale);
                Field(context, sale.ItemField); Field(context, sale.CategoryField);
                if (sale.ItemIndex < catalog.Items.Count && ImGui.SmallButton("Open item")) context.Workspace.Select(catalog.Items[sale.ItemIndex].Tag);
                foreach (var cost in sale.Costs)
                {
                    ImGui.PushID(cost.QuantityField.Offset);
                    DrawCostRow(context, sale, cost);
                    ImGui.PopID();
                }
                if (sale.Costs.Count == 0) ImGui.TextDisabled("No static costs");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        Report("Read errors", vendor.Diagnostics);
    }

    private void Field(StudioPageContext context, AssetProperty field, string? display = null, string tip = "")
    {
        ImGui.PushID(field.Offset); ImGui.TextUnformatted(field.Name); StudioUi.Tip(tip); ImGui.SameLine();
        if (field.Editable)
        { if (ImGui.SmallButton((display ?? field.Value) + "##value")) BeginEdit(context, field); }
        else ImGui.TextDisabled(display ?? field.Value);
        ImGui.PopID();
    }

    private void BeginEdit(StudioPageContext context, AssetProperty field)
    {
        editing = field; editValue = field.Value; plugChoices = null; plugSearch = ""; openEditor = true;
        editingDocument = context.Workspace.Document; editingProject = context.Workspace.Project;
        editingOverlayRevision = context.Workspace.Catalog?.OverlayRevision ?? -1;
    }

    /** Read-only sessions expose the same fields while project edits use explicit save actions. */
    private void DrawEdit(StudioPageContext context, InvestmentCatalog catalog)
    {
        if (openEditor) { ImGui.OpenPopup("Edit investment field"); openEditor = false; }
        if (!ImGui.BeginPopupModal("Edit investment field", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (!ReferenceEquals(editingDocument, context.Workspace.Document)
            || !ReferenceEquals(editingProject, context.Workspace.Project)
            || editingOverlayRevision != context.Workspace.Catalog?.OverlayRevision)
        { ClearEdit(); ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (editing is { } field)
        {
            ImGui.TextUnformatted(field.Name);
            var detail = context.Workspace.SelectedInvestmentItem;
            var stat = detail?.Stats.FirstOrDefault(row => row.Field.Offset == field.Offset);
            var curve = stat is not null && detail is not null ? DisplayCurve(detail, stat) : null;
            if (stat is not null) ImGui.TextDisabled("Base value");
            if (plugChoices is { Count: > 0 })
            {
                ImGui.SetNextItemWidth(350); ImGui.InputTextWithHint("##plugSearch", "Find allowed plug", ref plugSearch, 256);
                ImGui.BeginChild("allowed-plugs", new(350, 220), ImGuiChildFlags.Borders);
                foreach (ushort index in plugChoices.Where(index => plugSearch.Length == 0 || catalog.ItemName(index).Contains(plugSearch, StringComparison.OrdinalIgnoreCase)))
                    if (ImGui.Selectable(catalog.ItemName(index) + $"##{index}")) editValue = index.ToString(CultureInfo.InvariantCulture);
                ImGui.EndChild();
            }
            if (field.Offset == 186 && context.Workspace.SelectedInvestmentItem is not null)
            {
                ImGui.SetNextItemWidth(350);
                if (ImGui.BeginCombo("##investmentValue", byte.TryParse(editValue, out byte current) ? ItemKinds.TierName(current) : editValue))
                {
                    foreach (byte value in new byte[] { 0, 1, 2, 3, 4, 5, 255 })
                        if (ImGui.Selectable(ItemKinds.TierName(value))) editValue = value.ToString(CultureInfo.InvariantCulture);
                    ImGui.EndCombo();
                }
            }
            else { ImGui.SetNextItemWidth(350); ImGui.InputText("##investmentValue", ref editValue, 64); }
            if (curve is not null && int.TryParse(editValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int previewValue))
                ImGui.TextDisabled(previewValue == 0 ? "Uses item-level context" : $"Display {curve.Evaluate(previewValue)}");
            ImGui.BeginDisabled(!context.Workspace.CanEdit);
            if (ImGui.Button("Save delta") && SaveEditDraft(context.Workspace)) ImGui.CloseCurrentPopup();
            ImGui.EndDisabled(); ImGui.SameLine();
            if (ImGui.Button("Cancel")) { ImGui.CloseCurrentPopup(); ClearEdit(); }
            if (context.Workspace.Project is null) ImGui.TextDisabled("Create or open a project to edit");
        }
        ImGui.EndPopup();
    }

    private void ClearEdit()
    { editing = null; editingDocument = null; editingProject = null; plugChoices = null; openEditor = false; }

    /**
     * @param workspace Owns the selected document and project.
     * @return True after a successful save clears the draft.
     */
    private bool SaveEditDraft(StudioWorkspace workspace)
    {
        if (editing is not { } field) return false;
        workspace.Run(() => workspace.ApplyInvestmentEdit(field, editValue));
        if (workspace.Error.Length != 0) return false;
        ClearEdit();
        return true;
    }

    private static void Report(string title, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0 || !ImGui.CollapsingHeader($"{title} ({errors.Count})###{title}")) return;
        StudioUi.Facts(title, errors);
    }
    private static InvestmentStatCurve? DisplayCurve(InvestmentItemDetail detail, InvestmentStat stat) =>
        stat.Value != 0 && !stat.Computed && !stat.Conditional ? detail.DisplayCurves.GetValueOrDefault(stat.Index) : null;
}
