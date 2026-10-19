using System.Numerics;
using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** Picker widgets shared by the dialog and by workflows that draw their own rows. */
public static class CreationWidgets
{
    private sealed class PickerState
    {
        public string Search = "", Searched = "\0";
        public int Revision = -1;
        public CreationChoice[] Results = [];
        public string Error = "";
    }

    private sealed class RowPickerState
    {
        public string Search = "", Searched = "\0";
        public int Revision = -1;
        public double Edited;
        /** The row array last filtered; a new array refilters. */
        public IReadOnlyList<BrowserAsset>? Rows;
        public BrowserAsset[] Results = [];
        public Task<BrowserAsset[]>? Work;
        public CancellationTokenSource? Cancel;
        public string Error = "";
        /** The type and rarity facets of the row array they were built from. */
        public IReadOnlyList<BrowserAsset>? FacetRows;
        public RowFacet[] Facets = [];
        /** True until the list has scrolled the chosen row into view. */
        public bool Reveal;
        /** Each facet's chosen value by name; a missing name is the facet's default. */
        public readonly Dictionary<string, uint> Chosen = [];
    }

    // A row icon before scaling; rows with icons are this tall.
    private const float IconRow = 40;
    // A facet with this value lets every row through.
    private const uint AnyValue = uint.MaxValue;
    private static readonly Dictionary<uint, (uint? Value, string Label)> WindowDrafts = [];

    /** A picker lists at most this many matches; searching narrows the rest. */
    private const int PickerRows = 200;
    /** Seconds a row search waits for typing to stop. */
    private const double SearchSettle = 0.2;
    private static readonly Dictionary<uint, PickerState> Pickers = [];
    private static readonly Dictionary<uint, RowPickerState> RowPickers = [];
    public static float Scale { get; set; } = 1;

    /** Forgets search text and cached results, so a new workflow starts clean. */
    public static void Reset()
    {
        Pickers.Clear();
        foreach (var state in RowPickers.Values) { state.Cancel?.Cancel(); state.Cancel?.Dispose(); }
        RowPickers.Clear();
        WindowDrafts.Clear();
    }

    /**
     * A searchable list of candidates.
     * @param id ImGui identity of this picker; state is kept per full ID stack.
     * @param revision Data revision; a change reruns the search.
     * @return True when the user chose a row.
     */
    public static bool Picker(string id, ref uint? value, ref string label, Func<string, IEnumerable<CreationChoice>> search,
        int revision, float height = 130)
    {
        // Keyed by the full ImGui ID stack, so pickers that share an id under different parents keep their own search.
        uint key = ImGui.GetID(id);
        if (!Pickers.TryGetValue(key, out var state)) Pickers.Add(key, state = new());
        bool changed = false;
        ImGui.PushID(id);
        ImGui.TextDisabled(value is null ? "None selected" : label);
        if (value is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) { value = null; label = ""; changed = true; }
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search", ref state.Search, 256);
        if (state.Search != state.Searched || state.Revision != revision)
        {
            state.Searched = state.Search; state.Revision = revision;
            state.Error = "";
            try { state.Results = search(state.Search.Trim()).Take(PickerRows).ToArray(); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { state.Results = []; state.Error = "Search failed: " + error.Message; }
        }
        ImGui.BeginChild("##rows", new Vector2(0, height * Scale), ImGuiChildFlags.Borders);
        foreach (var choice in state.Results)
        {
            ImGui.PushID(unchecked((int)choice.Value));
            if (ImGui.Selectable(choice.Label.Length == 0 ? $"{choice.Value:X8}" : choice.Label, value == choice.Value))
            { value = choice.Value; label = choice.Label; changed = true; }
            StudioUi.Tip(choice.Detail);
            ImGui.PopID();
        }
        if (state.Error.Length != 0)
        {
            ImGui.TextWrapped(state.Error);
            if (ImGui.SmallButton("Retry search")) state.Revision = -1;
        }
        else if (state.Results.Length == 0) ImGui.TextDisabled("No matches");
        else if (state.Results.Length == PickerRows) ImGui.TextDisabled("Search to see more");
        ImGui.EndChild();
        ImGui.PopID();
        return changed;
    }

    /**
     * A searchable list over a cached row array; a search or a large array filters on a worker.
     * @param filter Keeps the rows that fit; it must not read packages.
     * @param revision Data revision; a change refilters at once.
     * @param isProject True for a project row; called only for drawn rows. Other rows show their tag.
     * @param clearable False hides Clear, for a value that cannot be empty.
     * @param icon The icon a drawn row shows before its name, or null while it reads.
     * @param facets Dropdown filters beside the type and rarity ones the rows give; a facet with no choices hides.
     * @param reveal Scrolls the list to the chosen row once it is drawn.
     * @return True when the user chose a row.
     */
    public static unsafe bool RowPicker(string id, ref uint? value, ref string label, IReadOnlyList<BrowserAsset> rows,
        Func<BrowserAsset, bool>? filter, int revision, Func<uint, bool> isProject, float height = 130, bool clearable = true,
        Func<uint, ItemIcon?>? icon = null, IReadOnlyList<RowFacet>? facets = null, bool reveal = false)
    {
        uint key = ImGui.GetID(id);
        if (!RowPickers.TryGetValue(key, out var state)) RowPickers.Add(key, state = new());
        state.Reveal |= reveal;
        uint? revealed = value;
        bool changed = false;
        ImGui.PushID(id);
        ImGui.TextDisabled(value is null ? "None selected" : label);
        if (value is not null && clearable)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) { value = null; label = ""; changed = true; }
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search", ref state.Search, 256)) state.Edited = ImGui.GetTime();
        (filter, revision) = DrawFacets(state, rows, facets, filter, revision);
        TakeRows(state);
        if (state.Revision != revision || !ReferenceEquals(state.Rows, rows)
            || state.Search != state.Searched && ImGui.GetTime() - state.Edited > SearchSettle)
        {
            state.Searched = state.Search; state.Revision = revision; state.Rows = rows; state.Error = "";
            state.Cancel?.Cancel();
            state.Cancel?.Dispose();
            state.Cancel = new();
            (var inline, state.Work) = BrowserRowFilter.Start(rows, BrowserRowFilter.Words(state.Search), filter, state.Cancel.Token);
            if (inline is not null) state.Results = inline;
        }
        float line = ImGui.GetTextLineHeight();
        ImGui.BeginChild("##rows", new Vector2(0, height * Scale), ImGuiChildFlags.Borders);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        // Rows with icons take two lines, the name over the type, beside an icon of that height.
        float rowHeight = icon is null ? line : Math.Max(IconRow * Scale, line * 2 + 4 * Scale);
        float pitch = rowHeight + ImGui.GetStyle().ItemSpacing.Y;
        if (state.Reveal && state.Work is null)
        {
            state.Reveal = false;
            int at = Array.FindIndex(state.Results, row => row.Entry.TagHash == revealed);
            if (at >= 0) ImGui.SetScrollY(Math.Max(0, at * pitch - ImGui.GetWindowHeight() / 2 + pitch / 2));
        }
        clipper.Begin(state.Results.Length, pitch);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = state.Results[i];
                uint tag = row.Entry.TagHash;
                // The tag tells rows with the same name apart.
                string name = row.Name, detail = isProject(tag) ? "Project" : $"{tag:X8}";
                ImGui.PushID(i);
                var start = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X, detailWidth = ImGui.CalcTextSize(detail).X;
                if (ImGui.Selectable("##row", value == tag, ImGuiSelectableFlags.None, new(0, rowHeight)))
                { value = tag; label = name; changed = true; }
                if (ImGui.IsItemHovered()) AssetTooltip.Draw(row);
                var draw = ImGui.GetWindowDrawList();
                float indent = 0;
                // Rarity colours the name, as the asset browser colours it.
                uint color = ImGui.GetColorU32(row.Tier >= 0 ? StudioTheme.Rarity(row.Tier)
                    : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
                float top = 0;
                if (icon is not null)
                {
                    icon(tag)?.Draw(draw, start, start + new Vector2(rowHeight));
                    indent = rowHeight + 6 * Scale; top = (rowHeight - line * 2) / 2;
                }
                draw.PushClipRect(start, start + new Vector2(width - detailWidth - 8 * Scale, rowHeight), true);
                draw.AddText(start + new Vector2(indent, top), color, name);
                if (icon is not null)
                    draw.AddText(start + new Vector2(indent, top + line), ImGui.GetColorU32(StudioTheme.Muted),
                        row.Type.Length != 0 ? row.Type : row.Kind);
                draw.PopClipRect();
                draw.AddText(new(start.X + width - detailWidth, start.Y + (rowHeight - line) / 2),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), detail);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        if (state.Error.Length != 0) ImGui.TextWrapped(state.Error);
        else if (state.Work is not null) ImGui.TextDisabled("Searching");
        else if (state.Results.Length == 0) ImGui.TextDisabled("No matches");
        ImGui.EndChild();
        ImGui.PopID();
        return changed;
    }

    /**
     * One dropdown per facet with more than one choice: the rows' types and rarities, then the caller's, wrapping
     * at the edge.
     * @return The filter with the chosen values added, and a revision that changes with them.
     */
    private static (Func<BrowserAsset, bool>? Filter, int Revision) DrawFacets(RowPickerState state,
        IReadOnlyList<BrowserAsset> rows, IReadOnlyList<RowFacet>? extra, Func<BrowserAsset, bool>? filter, int revision)
    {
        if (!ReferenceEquals(state.FacetRows, rows))
        {
            state.FacetRows = rows; state.Chosen.Clear();
            string[] types = [.. rows.Select(row => row.Type).Where(type => type.Length != 0).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            var typeIndex = types.Select((type, index) => (type, index))
                .ToDictionary(pair => pair.type, pair => (uint)pair.index);
            int[] tiers = [.. rows.Select(row => row.Tier).Where(tier => tier >= 0).Distinct().Order()];
            state.Facets =
            [
                new("Type", [.. types.Select((type, index) => new CreationChoice((uint)index, type))],
                    row => typeIndex.TryGetValue(row.Type, out uint index) ? index : null),
                new("Rarity", [.. tiers.Select(tier => new CreationChoice((uint)tier, InvestmentItemKinds.TierName((byte)tier)))],
                    row => row.Tier >= 0 ? (uint)row.Tier : null)
            ];
        }
        // Type, the caller's dropdowns such as damage and ammo, rarity, then the checkboxes.
        var shown = state.Facets.Take(1).Concat(extra?.Where(facet => !facet.Checkbox) ?? []).Concat(state.Facets.Skip(1))
            .Concat(extra?.Where(facet => facet.Checkbox) ?? [])
            .Where(facet => facet.Choices.Count > (state.Facets.Contains(facet) ? 1 : 0)).ToArray();
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X, gap = ImGui.GetStyle().ItemSpacing.X;
        void Next(float width)
        {
            if (ImGui.GetItemRectMax().X + gap + width <= right) ImGui.SameLine();
        }
        var style = ImGui.GetStyle();
        for (int i = 0; i < shown.Length; i++)
        {
            var facet = shown[i];
            uint chosen = state.Chosen.GetValueOrDefault(facet.Name, facet.Default);
            if (facet.Checkbox)
            {
                if (i > 0) Next(ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(facet.Name).X);
                bool on = chosen != AnyValue;
                if (ImGui.Checkbox(facet.Name + "##facet", ref on))
                    state.Chosen[facet.Name] = on ? facet.Choices[0].Value : AnyValue;
                continue;
            }
            // As wide as its longest choice reads, with the arrow.
            float width = facet.Choices.Select(choice => choice.Label).Append("Any")
                .Max(label => ImGui.CalcTextSize($"{facet.Name}: {label}").X) + ImGui.GetFrameHeight() + style.FramePadding.X * 2;
            if (i > 0) Next(width);
            string current = facet.Choices.FirstOrDefault(choice => choice.Value == chosen)?.Label ?? "Any";
            ImGui.SetNextItemWidth(width);
            if (!ImGui.BeginCombo("##" + facet.Name, $"{facet.Name}: {current}")) continue;
            if (ImGui.Selectable("Any", chosen == AnyValue)) state.Chosen[facet.Name] = AnyValue;
            foreach (var choice in facet.Choices)
            {
                ImGui.PushID(unchecked((int)choice.Value));
                if (ImGui.Selectable(choice.Label, choice.Value == chosen)) state.Chosen[facet.Name] = choice.Value;
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if (shown.Length != 0)
        {
            Next(ImGui.CalcTextSize("Reset").X + ImGui.GetStyle().FramePadding.X * 2);
            ImGui.BeginDisabled(shown.All(facet => state.Chosen.GetValueOrDefault(facet.Name, facet.Default) == facet.Default));
            if (ImGui.SmallButton("Reset")) state.Chosen.Clear();
            ImGui.EndDisabled();
        }
        var active = shown.Select(facet => (Facet: facet, Value: state.Chosen.GetValueOrDefault(facet.Name, facet.Default)))
            .Where(pair => pair.Value != AnyValue).ToArray();
        if (active.Length == 0) return (filter, revision);
        int key = active.Aggregate(revision, (hash, pair) => HashCode.Combine(hash, pair.Facet.Name, pair.Value));
        return (row => (filter?.Invoke(row) ?? true) && active.All(pair => pair.Facet.Value(row) == pair.Value), key);
    }

    /**
     * The row picker in a window of its own with Choose and Cancel; ImGui.OpenPopup(id) opens it from the same ID
     * scope. The selection stays a draft until Choose.
     * @return True when Choose set the value.
     */
    public static bool RowPickerWindow(string id, ref uint? value, ref string label, IReadOnlyList<BrowserAsset> rows,
        Func<BrowserAsset, bool>? filter, int revision, Func<uint, bool> isProject, Func<uint, ItemIcon?>? icon = null,
        IReadOnlyList<RowFacet>? facets = null)
    {
        ImGui.SetNextWindowSize(new Vector2(760, 640) * Scale, ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.NoSavedSettings)) return false;
        uint key = ImGui.GetID("draft");
        if (ImGui.IsWindowAppearing() || !WindowDrafts.ContainsKey(key)) WindowDrafts[key] = (value, label);
        var (draft, draftLabel) = WindowDrafts[key];
        var style = ImGui.GetStyle();
        float footer = ImGui.GetFrameHeightWithSpacing() + style.ItemSpacing.Y;
        float list = ImGui.GetContentRegionAvail().Y - footer - ImGui.GetTextLineHeightWithSpacing()
            - ImGui.GetFrameHeightWithSpacing() * 2;
        RowPicker("rows", ref draft, ref draftLabel, rows, filter, revision, isProject, Math.Max(120 * Scale, list) / Scale,
            clearable: false, icon: icon, facets: facets, reveal: ImGui.IsWindowAppearing());
        WindowDrafts[key] = (draft, draftLabel);
        bool chosen = false;
        float button = 110 * Scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - button * 2 - style.ItemSpacing.X);
        if (ImGui.Button("Cancel", new(button, 0))) ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        ImGui.BeginDisabled(draft is null);
        if (StudioUi.Primary("Choose", new(button, 0)))
        { value = draft; label = draftLabel; chosen = true; ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled();
        ImGui.EndPopup();
        return chosen;
    }

    /** Adopts a finished worker filter; a cancelled one is dropped. */
    private static void TakeRows(RowPickerState state)
    {
        if (state.Work is not { IsCompleted: true } work) return;
        state.Work = null;
        try { state.Results = work.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { state.Results = []; state.Error = "Search failed: " + error.Message; }
    }

    /** A combo over fixed choices; returns true when the value changed. */
    public static bool Choice(string id, ref uint value, IReadOnlyList<CreationChoice> choices, float width = 260)
    {
        uint current = value;
        var selected = choices.FirstOrDefault(choice => choice.Value == current);
        bool changed = false;
        ImGui.SetNextItemWidth(width * Scale);
        if (ImGui.BeginCombo(id, selected?.Label ?? $"{value}"))
        {
            foreach (var choice in choices)
            {
                ImGui.PushID(unchecked((int)choice.Value));
                if (ImGui.Selectable(choice.Label, choice.Value == value)) { value = choice.Value; changed = true; }
                StudioUi.Tip(choice.Detail);
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    /** The field label; its hint shows on hover. */
    public static void Label(CreationField field)
    {
        ImGui.TextUnformatted(field.Required ? field.Label + " *" : field.Label);
        StudioUi.Tip(field.Hint);
    }

    /** Up to about five short choices side by side, the current one filled; returns true when the value changed. */
    public static bool Buttons(string id, ref uint value, IReadOnlyList<CreationChoice> choices, float width)
    {
        bool changed = false;
        float gap = ImGui.GetStyle().ItemSpacing.X * .5f;
        float each = (width - gap * (choices.Count - 1)) / Math.Max(1, choices.Count);
        ImGui.PushID(id);
        for (int i = 0; i < choices.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, gap);
            bool selected = choices[i].Value == value;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);
            if (ImGui.Button(choices[i].Label + "##" + i, new(each, 0)) && !selected) { value = choices[i].Value; changed = true; }
            if (selected) ImGui.PopStyleColor();
            StudioUi.Tip(choices[i].Detail);
        }
        ImGui.PopID();
        return changed;
    }

    /**
     * Import surfaces as a name and status table under a count line; the detail shows on hover.
     * @param skipped Surface that is read but not written; it gets a WIP tag.
     */
    public static void Surfaces(string id, IReadOnlyList<OriginalAssetSurface> surfaces, string skipped = "")
    {
        int ready = surfaces.Count(surface => surface.Present && surface.Supported && surface.Name != skipped);
        int unsupported = surfaces.Count(surface => surface.Present && !surface.Supported);
        ImGui.TextDisabled($"{ready} ready, {unsupported} unsupported");
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch, .4f);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch, .6f);
        foreach (var surface in surfaces)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(surface.Name);
            StudioUi.Tip(surface.Detail);
            ImGui.TableNextColumn();
            if (!surface.Present) ImGui.TextDisabled("Absent");
            else if (!surface.Supported) ImGui.TextColored(StudioTheme.Accent, "Unsupported");
            else if (skipped.Length != 0 && surface.Name == skipped) ImGui.TextDisabled("Skipped");
            else ImGui.TextUnformatted("Ready");
            StudioUi.Tip(surface.Detail);
            if (skipped.Length != 0 && surface.Name == skipped) StudioUi.Wip(StudioUi.NotShown);
        }
        ImGui.EndTable();
    }
}
