using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/**
 * Clone and Edit in tabs: the item, Appearance and, for a weapon, Gameplay. The fields, drafts, checks and Create are
 * the workflow's own.
 */
public sealed partial class ItemWorkflow
{
    private static readonly CreationChoice[] ReticleChoices = [new(0, "No Change"), new(1, "Base Holo Reticle")];
    private const string NeedsAuthored = "Needs a weapon made with New.";
    // The model preview beside the item's text, before scaling.
    private static readonly Vector2 PreviewSize = new(300, 210);
    /** Edited choice lists by socket, written at Create; a socket without one keeps what it offers. */
    private readonly Dictionary<int, List<InvestmentSocketChoice>> socketLists = [];
    private int addingSocket = -1, replacingChoice = -1;
    private bool openAddPlug;
    private int tab;
    private bool randomized;
    private string? noun;
    /** The kind in labels, such as Armor or Clan Banner. */
    private string Noun => noun ??= CultureInfo.InvariantCulture.TextInfo.ToTitleCase(CreationTypes.ItemNoun(category));
    private string[] Tabs => weapon ? ["Weapon", "Appearance", "Gameplay"] : [Noun, "Appearance"];
    /** Field text as the base loaded it, for Reset; taken again when the base changes. */
    private readonly Dictionary<CreationField, string> loadedText = [];
    private InvestmentItemDetail? loadedDetail;
    private string statSearch = "";
    /** The base the open document was last moved to, so the preview shows the item being cloned. */
    private uint? previewBase;
    private string BasePickerId => $"Choose base {CreationTypes.ItemNoun(category)}###base-item";
    private const string AddPlugId = "Add plug###add-plug";
    private static readonly CreationChoice[] DummyFilter = [new(1, "Hide Dummies")];
    private (InvestmentItem? Item, ItemStatGroup? Group) statGroup;
    /** What the equipped plugs add to each stat, keyed by the base and its equipped plugs. */
    private (string Key, Dictionary<byte, List<StatSource>> Plugs) plugStats = ("", []);
    private (InvestmentItem? Item, (string Damage, string Ammo) Names) fixedProfile;
    /** Damage type and ammo by weapon tag for the base picker's filters; empty until read. */
    private FrozenDictionary<uint, (int? Damage, int? Ammo)> weaponFacts = FrozenDictionary<uint, (int?, int?)>.Empty;
    private Task<FrozenDictionary<uint, (int? Damage, int? Ammo)>>? weaponFactsRead;
    private InvestmentCatalog? weaponFactsSource;

    public override void DrawLayout(StudioWorkspace workspace, Action<string, Action<string>> pickFile)
    {
        if (!ReferenceEquals(loadedDetail, detail))
        {
            loadedDetail = detail; loadedText.Clear();
            foreach (var field in Inputs) loadedText[field] = field.Text;
        }
        // Clone follows the chosen base with the open document, which the model preview shows.
        if (editing is null && baseItem.Tag is { } chosen && chosen != previewBase && !workspace.IsBusy)
        {
            previewBase = chosen;
            if (workspace.Document?.Entry.TagHash != chosen) workspace.Open(chosen);
        }
        float scale = CreationWidgets.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3) * scale);
        string[] tabs = Tabs;
        if (ImGui.BeginTabBar("##layout-tabs"))
        {
            for (int i = 0; i < tabs.Length; i++)
                if (ImGui.BeginTabItem(tabs[i])) { tab = i; ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.BeginChild("##layout-tab", Vector2.Zero);
        switch (tab)
        {
            case 0: DrawItemTab(pickFile); break;
            case 1: DrawAppearanceTab(pickFile); break;
            default: DrawGameplayTab(); break;
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    private bool Edited(CreationField field) => loadedText.TryGetValue(field, out string? text) && text != field.Text;

    private void Restore(CreationField field)
    {
        if (loadedText.TryGetValue(field, out string? text)) field.Text = text;
    }

    /** The base item and its text beside the profile tiles, then stats beside perks and sockets. */
    private void DrawItemTab(Action<string, Action<string>> pickFile)
    {
        float scale = CreationWidgets.Scale;
        if (ImGui.BeginTable("##identity", 2, ImGuiTableFlags.SizingFixedFit))
        {
            // The item and its model in one column, its text and values in the other.
            ImGui.TableSetupColumn("##base", ImGuiTableColumnFlags.WidthFixed, PreviewSize.X * scale);
            ImGui.TableSetupColumn("##text", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn();
            DrawBaseItem();
            DrawPreview();
            ImGui.TableNextColumn();
            DrawText();
            DrawProfile();
            ImGui.EndTable();
        }
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.BeginTable("##contents", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##stats", ImGuiTableColumnFlags.WidthStretch, .35f);
            ImGui.TableSetupColumn("##sockets", ImGuiTableColumnFlags.WidthStretch, .65f);
            ImGui.TableNextColumn();
            DrawStatRows();
            ImGui.TableNextColumn();
            DrawSocketRows(pickFile);
            ImGui.EndTable();
        }
        if (ImGui.TreeNodeEx("Technical", StudioUi.Section))
        {
            NamedField("Package ID", package, pickFile);
            if (sharedSets.Visible) NamedField("Shared Plug Sets", sharedSets, pickFile);
        }
    }

    /** The open item's model when it is the item on the form, else its icon. */
    private void DrawPreview()
    {
        var size = PreviewSize * CreationWidgets.Scale;
        if (source is not { } item || ItemPreview.Shared is not { } art) { ImGui.Dummy(size); return; }
        if (workspace.Document?.Entry.TagHash == item.Tag && art.DrawModel(workspace, size)) return;
        var at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        ImGui.GetWindowDrawList().AddRectFilled(at, at + size, ImGui.GetColorU32(ImGuiCol.FrameBg), 4 * CreationWidgets.Scale);
        float icon = 96 * CreationWidgets.Scale;
        var centre = at + size * .5f;
        art.Item(item)?.Draw(ImGui.GetWindowDrawList(), centre - new Vector2(icon * .5f), centre + new Vector2(icon * .5f));
    }

    /** The item as a card; Clone changes its base in the row picker's window, which filters weapons and armor. */
    private void DrawBaseItem()
    {
        StudioUi.Title(editing is null ? $"Base {Noun}" : Noun,
            editing is null ? KeptByCategory.GetValueOrDefault(category, "") : "");
        bool change = false;
        if (source is { } item && ItemPreview.Shared is { } preview)
            change = preview.Card(item, editing is null ? "Change Base" : "Open", CreationWidgets.Scale);
        else ImGui.TextDisabled("None selected");
        if (editing is not null) { if (change) workspace.Run(() => workspace.Select(source!.Tag)); return; }
        if (workspace.Investment is not { } investment || workspace.Catalog is not { } packages) return;
        if (change || source is null && ImGui.Button("Choose Base"))
        {
            if (weapon) ReadWeaponFacts(investment, packages);
            ImGui.OpenPopup(BasePickerId);
        }
        uint? tag = baseItem.Tag;
        string label = baseItem.TagLabel;
        if (weaponFactsRead is { IsCompleted: true } read)
        {
            if (read.IsCompletedSuccessfully) weaponFacts = read.Result;
            weaponFactsRead = null;
        }
        var facts = weaponFacts;
        // Display copies of weapons and armor stay out unless asked for.
        var dummies = new RowFacet("Hide Dummies", DummyFilter,
            row => row.Names.TryGetItem(row.Entry.TagHash, out var fact) && InvestmentItemKinds.IsDummy(fact) ? 0u : 1u,
            Default: 1, Checkbox: true);
        RowFacet[] facets = weapon && facts.Count != 0 ?
        [
            new("Damage", DamageTypes,
                row => facts.TryGetValue(row.Entry.TagHash, out var fact) && fact.Damage is { } value ? (uint)value : null),
            new("Ammo", AmmoTypes,
                row => facts.TryGetValue(row.Entry.TagHash, out var fact) && fact.Ammo is { } value ? (uint)value : null),
            dummies
        ] : category == InvestmentItemKinds.Armor ? [dummies] : [];
        if (CreationWidgets.RowPickerWindow(BasePickerId, ref tag, ref label, baseItem.Rows!(), baseItem.RowFilter,
            HashCode.Combine(workspace.AssetRevision, workspace.NameRevision, facts.Count), workspace.IsProjectTag,
            row => investment.FindItem(row) is { } item ? ItemPreview.Shared?.Item(item) : null, facets))
        { baseItem.Tag = tag; baseItem.TagLabel = label; }
    }

    /** Starts reading every weapon's damage type and ammo once per investment catalog, for the picker's filters. */
    private void ReadWeaponFacts(InvestmentCatalog investment, PackageCatalog packages)
    {
        if (weaponFactsRead is not null || weaponFacts.Count != 0 && ReferenceEquals(weaponFactsSource, investment)) return;
        weaponFactsSource = investment;
        bool damageTypes = InvestmentDamageType.Installed(investment);
        var weapons = baseItem.Rows!().Where(baseItem.RowFilter!).Select(row => investment.FindItem(row.Entry.TagHash))
            .OfType<InvestmentItem>().DistinctBy(item => item.Tag).ToArray();
        weaponFactsRead = Task.Run(() => weapons.ToFrozenDictionary(item => item.Tag,
            item => (damageTypes ? InvestmentDamageType.ReadWeapon(investment, packages, item) : null,
                InvestmentAmmoType.Read(packages, item))));
    }

    /** Name and flavor text, with the type line and lore in a closed Text Presentation. */
    private void DrawText()
    {
        string nameLabel = $"{Noun} Name";
        float label = Math.Max(110 * CreationWidgets.Scale, ImGui.CalcTextSize(nameLabel).X + ImGui.GetStyle().ItemSpacing.X);
        TextRow(nameLabel, name, label);
        TextRow("Flavor Text", description, label);
        if (!ImGui.TreeNodeEx("Text Presentation", StudioUi.Section)) return;
        TextRow("Item Type", weaponType, label);
        if (!loreTitle.Visible) return;
        TextRow("Lore Title", loreTitle, label);
        TextRow("Lore Subtitle", loreSubtitle, label);
        TextRow("Lore Text", loreText, label);
    }

    /** A text field with its label to the left; the field's hint shows on the label. */
    private static void TextRow(string label, CreationField field, float width)
    {
        if (!field.Visible) return;
        ImGui.PushID(label);
        float x = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        StudioUi.Tip(field.Hint);
        ImGui.SameLine();
        ImGui.SetCursorPosX(x + width);
        string text = field.Text;
        bool edited = field.Lines > 1
            ? ImGui.InputTextMultiline("##text", ref text, 8192,
                new Vector2(-1, Math.Min(field.Lines, 3) * ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2))
            : ImGui.InputTextWithHint("##text", field.Required ? "Required" : "", ref text, 512);
        if (edited) field.Text = text;
        ImGui.PopID();
    }

    /** Slot, a weapon's damage and ammo types, rarity, the exotic limit and Collections as value tiles. */
    private void DrawProfile()
    {
        var profile = FixedProfile();
        ImGui.Spacing();
        var tiles = new StudioUi.Tiles(4);
        var buckets = BucketChoices();
        // Weapons and armor show their slot by name; another bucket layout keeps the bucket list.
        var slots = buckets.Count != 0 && buckets.All(choice => choice.Value <= InvestmentItemKinds.LastArmorBucket)
            ? buckets.Select(choice => choice with { Label = InvestmentItemKinds.SlotName((byte)choice.Value) }).ToArray()
            : buckets;
        ChoiceTile(tiles, "Inventory Bucket", bucket, slots);
        // Damage and ammo stay in view; a weapon that cannot change them shows what it has.
        if (damageType.Visible) ChoiceTile(tiles, "Damage Type", damageType, DamageTypes);
        else if (weapon)
            tiles.Fixed("Damage Type", profile.Damage, damageWhy);
        if (ammoType.Visible) ChoiceTile(tiles, "Ammo Type", ammoType, AmmoTypes);
        else if (weapon) tiles.Fixed("Ammo Type", profile.Ammo, NeedsAuthored);
        ChoiceTile(tiles, "Rarity", tier, Rarities);
        ChoiceTile(tiles, "Exotic Equip Limit", exoticLimit, exoticLimit.Choices!());
        ChoiceTile(tiles, "Collections", collection, CollectionChoices());
    }

    /** A tile whose choices are buttons while they fit side by side, else a dropdown. */
    private void ChoiceTile(StudioUi.Tiles tiles, string label, CreationField field, IReadOnlyList<CreationChoice> choices)
    {
        if (!field.Visible || choices.Count == 0) return;
        if (tiles.Begin(label, field.Hint, Edited(field))) Restore(field);
        uint value = field.TryParse(out ulong parsed) ? (uint)parsed : uint.MaxValue;
        var style = ImGui.GetStyle();
        float each = (tiles.Width - style.ItemSpacing.X * .5f * (choices.Count - 1)) / choices.Count;
        bool buttons = choices.Count <= 5
            && choices.All(choice => ImGui.CalcTextSize(choice.Label).X + style.FramePadding.X * 2 <= each);
        bool changed = buttons ? CreationWidgets.Buttons("##buttons", ref value, choices, tiles.Width)
            : CreationWidgets.Choice("##choice", ref value, choices, tiles.Width / CreationWidgets.Scale);
        if (changed) field.Text = value.ToString(CultureInfo.InvariantCulture);
        tiles.End();
    }

    /** A weapon base's damage type and ammo names, read once per base for the tiles that cannot change them. */
    private (string Damage, string Ammo) FixedProfile()
    {
        if (ReferenceEquals(fixedProfile.Item, source)) return fixedProfile.Names;
        string damage = "", ammo = "";
        if (weapon && source is { } item && workspace.Investment is { } investment && workspace.Catalog is { } packages)
        {
            if (InvestmentDamageType.Installed(investment)
                && InvestmentDamageType.ReadWeapon(investment, packages, item) is { } value)
                damage = InvestmentDamageType.Names[value];
            if (InvestmentAmmoType.Read(packages, item) is { } kind) ammo = InvestmentAmmoType.Name(kind);
        }
        fixedProfile = (source, (damage, ammo));
        return fixedProfile.Names;
    }

    /** A text tile; a blank value keeps the weapon's own, which the hint text says. */
    private void TextTile(StudioUi.Tiles tiles, string label, CreationField field)
    {
        if (!field.Visible) return;
        if (tiles.Begin(label, field.Hint, Edited(field))) Restore(field);
        string text = field.Text;
        if (ImGui.InputTextWithHint("##value", "Base", ref text, 32)) field.Text = text;
        tiles.End();
    }

    /** A field under its own small name, for pickers and files that take the whole width. */
    private void NamedField(string label, CreationField field, Action<string, Action<string>> pickFile)
    {
        if (!field.Visible) return;
        ImGui.PushID(label);
        StudioUi.FieldName(label, field.Hint, false, ImGui.GetContentRegionAvail().X);
        CreationDialog.DrawField(workspace, field, pickFile, labelled: false);
        ImGui.PopID();
    }

    /**
     * Each stat as a row: the name against its bar, the value the game shows with the equipped plugs, then the raw value
     * saved to the item and its remove button. A stat only plugs give, and armor's empty bars, have no raw value.
     */
    private void DrawStatRows()
    {
        bool changed = statDrafts.Any(draft => draft.Source < 0 || draft.Remove
            || detail is not null && draft.Value != detail.Stats[draft.Source].Value);
        StudioUi.Title(weapon ? "Weapon Stats" : category == InvestmentItemKinds.Armor ? "Armor Stats" : "Stats", stats.Hint,
            changed);
        if (workspace.Investment is not { } investment || detail is null) { ImGui.TextDisabled(ChooseBase); return; }
        if (source is { } item && !ReferenceEquals(statGroup.Item, item) && workspace.Catalog is { } packages)
            statGroup = (item, ItemStatGroup.Read(packages, item));
        var group = statGroup.Group;
        // Read again only when a socket's default plug changes.
        ushort[] equipped = [.. detail.Sockets.Select(SocketDefault)];
        string key = $"{source?.Tag}:{string.Join(",", equipped)}";
        if (plugStats.Key != key && workspace.Catalog is { } catalog)
        {
            plugStats = (key, ItemStatGroup.PlugStats(investment, catalog, detail.Sockets.Select((socket, i) => (equipped[i], socket.Type))));
        }
        var plugs = plugStats.Plugs;
        int Added(byte index) => ItemStatGroup.Sum(plugs.GetValueOrDefault(index), false)
            + ItemStatGroup.Sum(plugs.GetValueOrDefault(index), true);
        byte[] extra = [.. plugs.Keys
            .Concat(category == InvestmentItemKinds.Armor && group is not null ? group.Stats.Keys.Where(group.Bar) : [])
            .Where(index => statDrafts.All(draft => draft.Index != index) && investment.StatName(index) != $"Stat {index}")
            .Distinct().OrderBy(index => group?.Order(index) ?? 0)];
        var style = ImGui.GetStyle();
        float scale = CreationWidgets.Scale, gap = style.ItemSpacing.X;
        string[] names = [.. statDrafts.Select(draft => investment.StatName(draft.Index))];
        string[] tags = [.. statDrafts.Select(StatTag)];
        float Label(int i) => ImGui.CalcTextSize(names[i]).X
            + (tags[i].Length == 0 ? 0 : StudioUi.BadgeWidth(tags[i]) + 4 * scale);
        var columns = StudioUi.StatColumns.Here(Enumerable.Range(0, names.Length).Select(Label)
            .Concat(extra.Select(index => ImGui.CalcTextSize(investment.StatName(index)).X)).DefaultIfEmpty(0).Max());
        // In the order the game lists them, then the stats it does not show.
        int[] order = [.. Enumerable.Range(0, statDrafts.Count).OrderBy(i => group?.Order(statDrafts[i].Index) ?? 0)];
        // Rows the base and its plugs hold at zero that the game does not show, such as Attack and Power, fold away.
        int[] zero = [.. order.Where(i => statDrafts[i].Source >= 0 && detail.Stats[statDrafts[i].Source].Value == 0
            && Added(statDrafts[i].Index) == 0
            && (group is null || group.Position(statDrafts[i].Index) == int.MaxValue))];
        void Compact(Action rows)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, 1 * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 1 * scale));
            rows();
            ImGui.PopStyleVar(2);
        }
        bool dropped = false;
        int sum = 0;
        Compact(() =>
        {
            dropped = order.Except(zero).Any(StatRow);
            if (!dropped) foreach (byte index in extra) PlugRow(index);
            // Armor sums its bars under them, as the game does.
            if (!dropped && category == InvestmentItemKinds.Armor)
                StudioUi.StatTotal(columns.Left + columns.Name, columns.Bar, sum);
        });
        if (!dropped && zero.Length != 0
            && ImGui.TreeNodeEx($"Zero Stats ({zero.Length})###zero-stats", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            Compact(() => zero.Any(StatRow));
            ImGui.TreePop();
        }
        if (ImGui.Button("+ Add Stat")) { statSearch = ""; ImGui.OpenPopup("add-stat"); }
        DrawAddStat(investment);

        InvestmentStatCurve? Curve(byte index) => detail.DisplayCurves.GetValueOrDefault(index);

        // The bar and the value the game shows with the equipped plugs.
        // Armor's stat roll and traits join the base value; other plugs read as the change.
        void Reading(byte index, int own, IReadOnlyList<StatSource>? sources, InvestmentStatCurve? curve, bool dim)
        {
            int Shown(int value) => value != 0 && curve is not null ? curve.Evaluate(value) : value;
            int start = own + ItemStatGroup.Sum(sources, false), total = start + ItemStatGroup.Sum(sources, true);
            int shown = Shown(total);
            // The group says which stats the game draws as bars; without one, values up to 100 do.
            bool barred = !dim && (group?.Bar(index) ?? shown is >= 0 and <= 100)
                && (total > 0 || group?.Bar(index) == true);
            StudioUi.StatReading(columns.Bar, columns.BarWidth, shown, Shown(start), group?.Full ?? 100, barred,
                ItemStatGroup.Breakdown(own, sources, total, shown), dim);
            if (barred) sum += shown;
        }

        // A stat only plugs give, or an empty armor bar.
        void PlugRow(byte index)
        {
            ImGui.PushID(-1 - index);
            string name = investment.StatName(index);
            ImGui.SetCursorPosX(columns.Left + Math.Max(0, columns.Name - ImGui.CalcTextSize(name).X));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StudioTheme.Muted, name);
            Reading(index, 0, plugs.GetValueOrDefault(index), Curve(index), false);
            ImGui.PopID();
        }

        // One row; true when it dropped a new row, which ends this frame's list.
        bool StatRow(int i)
        {
            var draft = statDrafts[i];
            ImGui.PushID(i);
            ImGui.SetCursorPosX(columns.Left + Math.Max(0, columns.Name - Label(i)));
            if (tags[i].Length != 0) { StudioUi.Badge(tags[i]); ImGui.SameLine(0, 4 * scale); }
            ImGui.AlignTextToFramePadding();
            var curve = draft.Source < 0 || !(detail.Stats[draft.Source].Conditional || detail.Stats[draft.Source].Computed)
                ? Curve(draft.Index) : null;
            bool hidden = group is not null && group.Position(draft.Index) == int.MaxValue;
            // Stats the game does not show read dim.
            if (draft.Remove || hidden) ImGui.TextDisabled(names[i]);
            else ImGui.TextColored(StudioTheme.Muted, names[i]);
            if (hidden && !draft.Remove) StudioUi.Tip("Not shown in game");
            Reading(draft.Index, draft.Value, draft.Remove ? null : plugs.GetValueOrDefault(draft.Index), curve, draft.Remove);
            ImGui.SameLine();
            ImGui.SetCursorPosX(columns.Raw);
            ImGui.SetNextItemWidth(columns.RawWidth);
            ImGui.BeginDisabled(draft.Remove);
            ImGui.DragInt("##raw", ref draft.Value);
            ImGui.EndDisabled();
            StudioUi.Tip("Zero uses the stat's level curve when one exists.");
            ImGui.SameLine();
            if (StudioUi.RowIconButton("remove", draft.Remove ? StudioIcon.Reset : StudioIcon.Remove,
                draft.Remove ? "Restore" : "Remove"))
            {
                if (draft.Source < 0) { statDrafts.RemoveAt(i); ImGui.PopID(); return true; }
                draft.Remove = !draft.Remove;
            }
            ImGui.PopID();
            return false;
        }
    }

    /** New for an added row, else what the base row says about its value. */
    private string StatTag(StatDraft draft) => draft.Source < 0 ? "New"
        : detail!.Stats[draft.Source].Computed ? "Computed" : detail.Stats[draft.Source].Conditional ? StudioUi.ConditionalBadge : "";

    /** A searchable stat list, the new row's raw value and Add. */
    private void DrawAddStat(InvestmentCatalog investment)
    {
        if (!ImGui.BeginPopup("add-stat")) return;
        var choices = StatChoices(investment);
        float scale = CreationWidgets.Scale;
        ImGui.SetNextItemWidth(320 * scale);
        ImGui.InputTextWithHint("##search", "Search", ref statSearch, 128);
        ImGui.BeginChild("##stats", new Vector2(320 * scale, 220 * scale), ImGuiChildFlags.Borders);
        foreach (var choice in choices.Where(choice => statSearch.Length == 0
            || choice.Label.Contains(statSearch, StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushID(unchecked((int)choice.Value));
            if (ImGui.Selectable(choice.Label, newStat == choice.Value)) newStat = choice.Value;
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.SetNextItemWidth(120 * scale);
        ImGui.InputInt("Raw", ref newStatValue, 0, 0);
        StudioUi.Tip("Zero uses the stat's level curve when one exists.");
        ImGui.SameLine();
        ImGui.BeginDisabled(choices.All(choice => choice.Value != newStat));
        if (ImGui.Button("Add"))
        {
            statDrafts.Add(new() { Index = (byte)newStat, Value = newStatValue });
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }

    private string ChooseBase => "Choose a base " + CreationTypes.ItemNoun(category);

    /**
     * One row per socket with what the item comes with, its plug picked from the socket's choices, then the sandbox perks
     * and the frame. Randomized shows each rolling socket's perk pool instead.
     */
    private void DrawSocketRows(Action<string, Action<string>> pickFile)
    {
        // New plugs come from Mods and plugs, new perks only from exotic New, which the heading and Add Perk say on hover.
        string hint = sockets.Visible
            ? "The first choice starts equipped. Right-click a choice to make it the default.\n" + sockets.Part!.NewBlocker
            : sockets.Hint;
        bool changed = plugDrafts.Count != 0 || socketLists.Count != 0 || PerksChanged;
        if (workspace.Investment is not { } investment || detail is null)
        { StudioUi.Title("Perks & Sockets", hint, changed); ImGui.TextDisabled(ChooseBase); return; }
        SocketTiles.Heading(hint, changed, sockets.Visible ? detail.Sockets : [], ref randomized);
        if (sockets.Visible)
        {
            if (detail.Sockets.Count == 0) ImGui.TextDisabled("No sockets");
            float label = detail.Sockets.Select(socket => ImGui.CalcTextSize(SocketTiles.Name(workspace, investment, socket)).X)
                .DefaultIfEmpty(0).Max();
            foreach (var socket in detail.Sockets)
            {
                ImGui.PushID(socket.Index);
                string name = SocketTiles.Name(workspace, investment, socket);
                if (randomized)
                {
                    // An edited socket's new choices join its pool at Create.
                    bool edited = socketLists.ContainsKey(socket.Index) || plugDrafts.ContainsKey(socket.Index);
                    if (socket.RandomizedPlugs.Count != 0)
                        SocketTiles.Row(investment, socket, name,
                            edited ? [.. socket.RandomizedPlugs.Concat(SocketPlugs(socket)).Distinct()] : socket.RandomizedPlugs,
                            label, false, _ => false, restorable: false, openable: false, pool: true);
                    ImGui.PopID();
                    continue;
                }
                if (SocketEditable(socket))
                {
                    var target = socket;
                    Apply(socket, SocketTiles.Row(investment, socket, name, SocketPlugs(socket), label, true,
                        from => CanMove(investment, from, target),
                        restorable: socketLists.ContainsKey(socket.Index) || plugDrafts.ContainsKey(socket.Index),
                        openable: false));
                    ImGui.PopID();
                    continue;
                }
                SocketTiles.Label(socket, name, label);
                uint value = plugDrafts.TryGetValue(socket.Index, out ushort chosen) ? chosen : socket.InitialPlug;
                if (PlugCombo(investment, SocketChoices(investment, socket), name, ref value))
                    SetSocketPlug(socket.Index, checked((ushort)value));
                ImGui.PopID();
            }
            DrawAddPlug(investment);
        }
        if (randomized) return;
        ImGui.Spacing();
        SocketTiles.SandboxHeading(perks.Hint, PerksChanged);
        for (int i = 0; i < perkDrafts.Count; i++)
        {
            var draft = perkDrafts[i];
            ImGui.PushID(i);
            float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight();
            ItemPreview.Draw(ItemPreview.Shared?.Perk(draft.Index), ImGui.GetFrameHeight());
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            string perk = PerkLabel(investment, draft.Index);
            if (draft.Remove) ImGui.TextDisabled(perk); else ImGui.TextUnformatted(perk);
            if (ImGui.IsItemHovered()) AssetTooltip.DrawPerk(draft.Index, perk);
            if (draft.Source < 0) { ImGui.SameLine(); StudioUi.Badge("New"); }
            else if (detail!.Perks[draft.Source].Conditional)
            { ImGui.SameLine(); StudioUi.Badge(StudioUi.ConditionalBadge, StudioUi.ConditionalTip); }
            ImGui.SameLine();
            ImGui.SetCursorPosX(right);
            if (StudioUi.RowIconButton("remove", draft.Remove ? StudioIcon.Reset : StudioIcon.Remove,
                draft.Remove ? "Restore" : "Remove"))
            {
                if (draft.Source < 0) { perkDrafts.RemoveAt(i); ImGui.PopID(); break; }
                draft.Remove = !draft.Remove;
            }
            ImGui.PopID();
        }
        if (ImGui.Button("+ Add Perk")) ImGui.OpenPopup("add-perk");
        StudioUi.Tip(perks.Part!.NewBlocker);
        if (ImGui.BeginPopup("add-perk"))
        {
            ImGui.BeginChild("##perk-picker", new Vector2(360, 230) * CreationWidgets.Scale);
            string pickedLabel = newPerkLabel;
            CreationWidgets.Picker("new-perk", ref newPerk, ref pickedLabel, search => SearchPerks(investment, search),
                HashCode.Combine(workspace.InvestmentRevision, workspace.NameRevision), 150);
            newPerkLabel = pickedLabel;
            ImGui.EndChild();
            ImGui.BeginDisabled(newPerk is null);
            if (ImGui.Button("Add") && newPerk is { } picked)
            {
                perkDrafts.Add(new() { Index = checked((ushort)picked) });
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.EndPopup();
        }
        if (frameName.Visible && ImGui.TreeNodeEx("Frame", StudioUi.Section))
        {
            float label = 110 * CreationWidgets.Scale;
            TextRow("Frame Name", frameName, label);
            TextRow("Frame Description", frameDescription, label);
            NamedField("Frame Icon", frameIcon, pickFile);
        }
    }

    /** True when the socket embeds a choice to copy and offers few enough choices to arrange. */
    private bool SocketEditable(InvestmentSocket socket) => SocketTiles.Arrangeable(Members(socket), socket);

    /** The plugs the socket embeds, so an edit has a member to copy. */
    private IReadOnlyList<ushort> Members(InvestmentSocket socket) =>
        SocketTiles.Members(workspace.Investment!, source!.Tag, detailBytes, socket);

    /** The socket's choices: its edited list, else what it offers now, its equipped plug first. */
    private List<ushort> SocketPlugs(InvestmentSocket socket) => socketLists.TryGetValue(socket.Index, out var plugs)
        ? [.. plugs.Select(choice => choice.Plug)]
        : SocketTiles.Offered(Members(socket), socket, SocketDefault(socket));

    private ushort SocketDefault(InvestmentSocket socket) =>
        plugDrafts.TryGetValue(socket.Index, out ushort plug) ? plug : socket.InitialPlug;

    /** An edit starts the socket's own list from what it offers now. */
    private List<InvestmentSocketChoice> EditPlugs(InvestmentSocket socket)
    {
        if (!socketLists.TryGetValue(socket.Index, out var plugs))
            socketLists[socket.Index] = plugs = [.. SocketPlugs(socket).Select(plug =>
                new InvestmentSocketChoice(plug, FromSocket: socket.Index))];
        return plugs;
    }

    private void SetDefault(InvestmentSocket socket, ushort plug)
    {
        if (plug == socket.InitialPlug) plugDrafts.Remove(socket.Index);
        else plugDrafts[socket.Index] = plug;
    }

    /** The first choice starts equipped. */
    private void KeepDefault(InvestmentSocket socket) => SetDefault(socket, SocketPlugs(socket)[0]);

    /** A plug moves within its socket, or to another socket whose type takes its category and lacks it. */
    private bool CanMove(InvestmentCatalog investment, (int Socket, ushort Plug) from, InvestmentSocket to) =>
        SocketEditable(to) && (from.Socket == to.Index || SocketPlugs(detail!.Sockets[from.Socket]).Count > 1
            && !SocketPlugs(to).Contains(from.Plug) && SocketTiles.Takes(workspace, investment, to.Type, from.Plug));

    private void Move((int Socket, ushort Plug) from, InvestmentSocket to, int before)
    {
        var source = detail!.Sockets[from.Socket];
        var plugs = EditPlugs(source);
        int at = plugs.FindIndex(choice => choice.Plug == from.Plug);
        var choice = plugs[at];
        plugs.RemoveAt(at);
        if (from.Socket == to.Index && at < before) before--;
        var target = EditPlugs(to);
        target.Insert(Math.Clamp(before, 0, target.Count), choice);
        KeepDefault(source);
        KeepDefault(to);
    }

    /** Applies what a socket row asks for to the drafts. */
    private void Apply(InvestmentSocket socket, SocketAction? action)
    {
        switch (action)
        {
            case SocketAction.Pick pick:
                addingSocket = socket.Index; replacingChoice = pick.Choice; openAddPlug = true;
                SocketTiles.Prepare(workspace);
                break;
            case SocketAction.Remove remove: EditPlugs(socket).RemoveAt(remove.Choice); KeepDefault(socket); break;
            case SocketAction.Move move: Move((move.FromSocket, move.Plug), socket, move.Before); break;
            case SocketAction.Restore: socketLists.Remove(socket.Index); plugDrafts.Remove(socket.Index); break;
        }
    }

    /** The plug picker for the socket being added to; Choose adds the plug last or replaces a choice. */
    private void DrawAddPlug(InvestmentCatalog investment)
    {
        if (openAddPlug) { ImGui.OpenPopup(AddPlugId); openAddPlug = false; }
        if (addingSocket < 0 || detail is null || addingSocket >= detail.Sockets.Count) return;
        var socket = detail.Sockets[addingSocket];
        if (source is not { } owner || PlugPicker.Pick(workspace, investment, AddPlugId, owner, socket,
                SocketTiles.Name(workspace, investment, socket), SocketPlugs(socket), replacingChoice) is not { } plug) return;
        var plugs = EditPlugs(socket);
        var choice = new InvestmentSocketChoice(plug, FromSocket: socket.Index);
        if (replacingChoice >= 0 && replacingChoice < plugs.Count) plugs[replacingChoice] = choice;
        else plugs.Add(choice);
        KeepDefault(socket);
    }

    /** A socket's plug with its icon and name in the button, the socket's name on hover, and each choice with its icon. */
    private static bool PlugCombo(InvestmentCatalog investment, IReadOnlyList<CreationChoice> choices, string socket,
        ref uint value)
    {
        uint current = value;
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, frame = ImGui.GetFrameHeight();
        ItemIcon? Icon(uint plug) => plug < investment.Items.Count ? ItemPreview.Shared?.Item(investment.Items[(int)plug]) : null;
        ImGui.SetNextItemWidth(width);
        bool open = ImGui.BeginCombo("##plug", "", ImGuiComboFlags.HeightLarge);
        ItemPreview.Label(draw, at, width - frame, Icon(current),
            choices.FirstOrDefault(choice => choice.Value == current)?.Label ?? investment.ItemName((int)current));
        if (!open)
        {
            if (current >= investment.Items.Count) StudioUi.Tip(socket);
            else if (ImGui.IsItemHovered()) AssetTooltip.DrawPlug(investment.Items[(int)current]);
            return false;
        }
        bool changed = false;
        foreach (var choice in choices)
        {
            ImGui.PushID(unchecked((int)choice.Value));
            ItemPreview.Draw(Icon(choice.Value), ImGui.GetTextLineHeight());
            ImGui.SameLine();
            if (ImGui.Selectable(choice.Label, choice.Value == current)) { value = choice.Value; changed = true; }
            if (choice.Value >= investment.Items.Count) StudioUi.Tip(choice.Detail);
            else if (ImGui.IsItemHovered()) AssetTooltip.DrawPlug(investment.Items[(int)choice.Value]);
            ImGui.PopID();
        }
        ImGui.EndCombo();
        return changed;
    }

    /** The inventory icon, then the HUD silhouette and sight reticle a project weapon can take. */
    private void DrawAppearanceTab(Action<string, Action<string>> pickFile)
    {
        StudioUi.Title("Inventory Icon", icon.Hint);
        CreationDialog.DrawField(workspace, icon, pickFile, labelled: false);
        if (perkIcon.Visible) NamedField("Sandbox Perk Icon", perkIcon, pickFile);
        if (!weapon) return;
        ImGui.Spacing();
        StudioUi.Title("HUD and Sight");
        if (!authoredWeapon) { ImGui.TextDisabled(NeedsAuthored); return; }
        NamedField("HUD Silhouette", hudIcon, pickFile);
        var tiles = new StudioUi.Tiles(1);
        ChoiceTile(tiles, "Sight Reticle", reticle, ReticleChoices);
        NamedField("Reticle Texture", reticleTexture, pickFile);
    }

    /** Fire rate, damage, magazine and family tiles; the raw translation tables sit in a closed Technical section. */
    private void DrawGameplayTab()
    {
        if (!authoredWeapon) { ImGui.TextDisabled(NeedsAuthored); return; }
        if (tuningError.Length != 0) ImGui.TextColored(StudioTheme.Warning, tuningError);
        var tiles = new StudioUi.Tiles(4);
        TextTile(tiles, "Fire Rate (RPM)", fireRate);
        TextTile(tiles, "Damage per Shot", damagePerShot);
        TextTile(tiles, "Magazine", magazineSize);
        ChoiceTile(tiles, "Weapon Family", family, FamilyChoices());
        if (!gameplay.Visible || !ImGui.TreeNodeEx("Technical", StudioUi.Section)) return;
        StudioUi.FieldName("Gameplay Values", gameplay.Hint, ValuesChanged, ImGui.GetContentRegionAvail().X);
        DrawGameplay();
    }
}
