using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;
using ItemKinds = SunriseStudio.Packages.InvestmentItemKinds;

namespace SunriseStudio.Editor;

/**
 * An item reads as its card and its fields in name and value pairs, then stats beside perks and sockets. Values open
 * the edit popup.
 */
public sealed partial class InvestmentPage
{
    private (InvestmentItemDetail? Detail, ItemStatGroup? Group, Dictionary<byte, List<StatSource>> Plugs) statGroup =
        (null, null, []);
    /** The raw stat value being dragged, by field offset; the project takes it when the drag ends. */
    private (int Offset, int Value)? rawDrag;
    private (InvestmentItemDetail? Detail, string? Damage, string? Ammo) profile;
    private const string AddPlugId = "Add plug###item-add-plug";
    private (int Socket, int Choice)? addingPlug;
    // A choice of this sets a dropdown socket's plug from the plugs it allows.
    private const int AllowedPlug = -2;
    // Item definition fields the card names: maximum stack +180, inventory bucket +184, tier +186 and instanced +187.
    private const int StackField = 180, BucketField = 184, TierField = 186, InstancedField = 187;
    // Narrower than this, stats sit above sockets instead of beside them.
    private const float SideBySideWidth = 760;
    private bool openAddPlug, randomized;

    /** A weapon's damage type and ammo names, read once per detail. */
    private (string? Damage, string? Ammo) Profile(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentItemDetail detail)
    {
        if (ReferenceEquals(profile.Detail, detail)) return (profile.Damage, profile.Ammo);
        string? damage = null, ammo = null;
        if (context.Workspace.Catalog is { } packages)
        {
            if (InvestmentDamageType.Installed(catalog)
                && InvestmentDamageType.ReadWeapon(catalog, packages, detail.Item) is { } type)
                damage = InvestmentDamageType.Names[type];
            if (InvestmentAmmoType.Read(packages, detail.Item) is { } kind) ammo = InvestmentAmmoType.Name(kind);
        }
        profile = (detail, damage, ammo);
        return (damage, ammo);
    }

    /** The plugs the socket embeds in the open item, so an edit has a member to copy. */
    private static IReadOnlyList<ushort> Members(StudioPageContext context, InvestmentCatalog catalog,
        InvestmentItemDetail detail, InvestmentSocket socket) =>
        context.Workspace.Document is { } document ? SocketTiles.Members(catalog, detail.Item.Tag, document.Bytes, socket) : [];

    /**
     * Applies what a socket row asks for as one project edit; a move between sockets writes both.
     * @param write Writes the edited lists; the socket's choices unless a pool's writer is given.
     */
    private void Apply(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail, InvestmentSocket socket,
        List<ushort> plugs, SocketAction? action, Action<IReadOnlyList<(int Socket, IReadOnlyList<ushort> Plugs)>>? write = null)
    {
        var workspace = context.Workspace;
        // A plug moved into another socket keeps its member from the socket it came from.
        InvestmentSocketChoice Choice(int list, ushort plug, SocketAction.Move? move) =>
            move is not null && list != move.FromSocket && plug == move.Plug
                ? new(plug, FromSocket: move.FromSocket) : new(plug);
        void Write(IReadOnlyList<(int Socket, IReadOnlyList<ushort> Plugs)> lists, SocketAction.Move? move = null) =>
            workspace.Run(() =>
            {
                if (write is not null) write(lists);
                else workspace.SetInvestmentSockets([.. lists.Select(list => (list.Socket,
                    (IReadOnlyList<InvestmentSocketChoice>)[.. list.Plugs.Select(plug => Choice(list.Socket, plug, move))]))]);
            });
        switch (action)
        {
            case SocketAction.Pick pick:
                addingPlug = (socket.Index, pick.Choice); openAddPlug = true;
                SocketTiles.Prepare(workspace);
                break;
            case SocketAction.Remove remove:
                List<ushort> left = [.. plugs];
                left.RemoveAt(remove.Choice);
                Write([(socket.Index, left)]);
                break;
            case SocketAction.Move move when move.FromSocket == socket.Index:
                List<ushort> moved = [.. plugs];
                int at = moved.IndexOf(move.Plug), before = move.Before;
                moved.RemoveAt(at);
                if (at < before) before--;
                moved.Insert(Math.Clamp(before, 0, moved.Count), move.Plug);
                Write([(socket.Index, moved)]);
                break;
            case SocketAction.Move move:
                var from = detail.Sockets[move.FromSocket];
                List<ushort> source = [.. SocketTiles.Offered(Members(context, catalog, detail, from), from, from.InitialPlug)];
                source.Remove(move.Plug);
                List<ushort> target = [.. plugs];
                target.Insert(Math.Clamp(move.Before, 0, target.Count), move.Plug);
                Write([(from.Index, source), (socket.Index, target)], move);
                break;
            case SocketAction.Open open: workspace.Select(catalog.Items[open.Plug].Tag); break;
        }
    }

    /** The plug picker for the socket being added to; Choose adds the plug last or replaces a choice, as one edit. */
    private void DrawAddPlug(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        if (openAddPlug) { ImGui.OpenPopup(AddPlugId); openAddPlug = false; }
        else if (!ImGui.IsPopupOpen(AddPlugId)) addingPlug = null;
        if (addingPlug is not { } adding || adding.Socket >= detail.Sockets.Count) return;
        var socket = detail.Sockets[adding.Socket];
        string name = SocketTiles.Name(context.Workspace, catalog, socket);
        if (adding.Choice == AllowedPlug)
        {
            ushort[] current = socket.InitialPlug < catalog.Items.Count ? [socket.InitialPlug] : [];
            if (PlugPicker.Pick(context.Workspace, catalog, AddPlugId, detail.Item, socket, name, current, current.Length - 1,
                    socket.AllowedPlugs) is { } allowed)
                context.Workspace.Run(() => context.Workspace.ApplyInvestmentEdit(socket.PlugField,
                    allowed.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        // The Randomized view adds to the socket's pool; the other to what the item comes with.
        List<ushort> plugs = randomized ? [.. socket.RandomizedPlugs]
            : SocketTiles.Offered(Members(context, catalog, detail, socket), socket, socket.InitialPlug);
        if (PlugPicker.Pick(context.Workspace, catalog, AddPlugId, detail.Item, socket, name, plugs, adding.Choice)
            is not { } plug) return;
        if (adding.Choice >= 0 && adding.Choice < plugs.Count) plugs[adding.Choice] = plug;
        else plugs.Add(plug);
        context.Workspace.Run(() =>
        {
            if (randomized) context.Workspace.SetInvestmentPool(socket.Index, plugs);
            else context.Workspace.SetInvestmentSockets([(socket.Index, [.. plugs.Select(value => new InvestmentSocketChoice(value))])]);
        });
    }

    /** Icon, name, fields, stats, sockets and perks of one item; values open the edit popup. */
    private void DrawItem(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        var item = detail.Item;
        var workspace = context.Workspace;
        bool weapon = item.Category == InvestmentCategory.Weapons;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4) * CreationWidgets.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6, 3) * CreationWidgets.Scale);
        // The item's card: its icon, name, hash and type, with its display record as its action.
        if (item.DisplayTag != 0 && ItemPreview.Shared is { } preview)
        {
            if (preview.Card(item, "Display Properties", CreationWidgets.Scale)) workspace.Select(item.DisplayTag);
        }
        else
        {
            ImGui.TextColored(StudioTheme.Rarity(item.Tier), item.Name);
            StudioUi.Tip($"Item {item.Index}\n{item.Hash:X8}");
            ImGui.TextColored(StudioTheme.Muted, $"{item.Rarity}  {item.TypeName}");
        }
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        ImGui.Spacing();
        var pairs = new StudioUi.Pairs();
        var (damage, ammo) = weapon ? Profile(context, catalog, detail) : (null, null);
        // Slot, damage, ammo and rarity first, then the other fields.
        int[] order = [BucketField, TierField, StackField, InstancedField];
        foreach (var field in detail.Fields.OrderBy(field =>
            Array.IndexOf(order, field.Offset) is var at and >= 0 ? at : order.Length))
        {
            // Damage and ammo read from the weapon; Clone and Edit change them.
            if (weapon && field.Offset == TierField)
            {
                pairs.Fixed("Damage Type", damage, "Clone or Edit changes it.");
                pairs.Fixed("Ammo Type", ammo, "Clone or Edit changes it.");
            }
            var (label, value) = field.Offset switch
            {
                StackField => ("Maximum Stack", field.Value),
                BucketField => ("Inventory Bucket", catalog.BucketName(item.Bucket) is { Length: > 0 } bucket ? bucket : field.Value),
                TierField => ("Rarity", item.Rarity),
                InstancedField => ("Instanced", field.Value == "0" ? "No" : "Yes"),
                _ => (field.Name, field.Value)
            };
            if (pairs.Value(label, value, field.Editable, field.Offset == BucketField ? $"Inventory bucket {field.Value}" : ""))
                BeginEdit(context, field);
        }
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.GetContentRegionAvail().X >= SideBySideWidth * CreationWidgets.Scale
            && ImGui.BeginTable("##item-contents", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##stats", ImGuiTableColumnFlags.WidthStretch, .45f);
            ImGui.TableSetupColumn("##sockets", ImGuiTableColumnFlags.WidthStretch, .55f);
            ImGui.TableNextColumn();
            DrawStats(context, catalog, detail);
            ImGui.TableNextColumn();
            DrawSockets(context, catalog, detail);
            ImGui.EndTable();
        }
        else
        {
            DrawStats(context, catalog, detail);
            ImGui.Spacing();
            DrawSockets(context, catalog, detail);
        }
        if (workspace.Names?.TryGetItem(item.Tag, out var facts) == true && (facts.HasObjectives
            || ItemKinds.Classify(facts).Category is ItemKinds.Subclasses or ItemKinds.Quests or ItemKinds.Bounties))
        {
            ImGui.TextDisabled("Objectives not shown");
            StudioUi.Tip("Objectives and talent trees are not read yet.");
        }
        Report("Read errors", detail.Diagnostics);
        string error = iconError.Length != 0 ? iconError : workspace.InvestmentIconError;
        Report("Icon errors", error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ImGui.PopStyleVar(2);
    }

    /**
     * Stats as rows: the name against its bar, the value the game shows with the equipped plugs, then the item's raw value
     * and remove. A stat only plugs give has no raw value, and armor lists every bar of its group, an empty roll at zero.
     */
    private void DrawStats(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        StudioUi.Title(detail.Item.Category switch
        {
            InvestmentCategory.Weapons => "Weapon Stats", InvestmentCategory.Armor => "Armor Stats", _ => "Stats"
        }, "Each row reads the value the game shows with the equipped plugs, then the raw value saved to the item.");
        if (!ReferenceEquals(statGroup.Detail, detail) && context.Workspace.Catalog is { } packages)
        {
            statGroup = (detail, ItemStatGroup.Read(packages, detail.Item),
                ItemStatGroup.PlugStats(catalog, packages, detail.Sockets.Select(socket => (socket.InitialPlug, socket.Type))));
        }
        var (_, group, plugs) = statGroup;
        bool armor = detail.Item.Category == InvestmentCategory.Armor;
        var extra = plugs.Keys.Concat(armor && group is not null ? group.Stats.Keys.Where(group.Bar) : [])
            .Where(index => detail.Stats.All(stat => stat.Index != index) && catalog.StatName(index) != $"Stat {index}")
            .Distinct();
        // In the order the game lists them, then the stats it does not show.
        StatLine Line(byte index, string name, InvestmentStat? own) =>
            new(index, name, own, plugs.GetValueOrDefault(index) ?? []);
        var lines = detail.Stats.Select(stat => Line(stat.Index, stat.Name, stat))
            .Concat(extra.Select(index => Line(index, catalog.StatName(index), null)))
            .OrderBy(line => group?.Order(line.Index) ?? 0).ToArray();
        var style = ImGui.GetStyle();
        float scale = CreationWidgets.Scale, gap = style.ItemSpacing.X;
        float Label(StatLine line) => ImGui.CalcTextSize(line.Name).X
            + (line.Tag.Length == 0 ? 0 : StudioUi.BadgeWidth(line.Tag) + 4 * scale);
        var columns = StudioUi.StatColumns.Here(lines.Select(Label).DefaultIfEmpty(0).Max());
        // Zero rows the game does not show, such as Attack and Power, fold away.
        bool Folded(StatLine line) => line.Total == 0 && (group is null || group.Position(line.Index) == int.MaxValue);
        var zero = lines.Where(Folded).ToArray();
        // The sum is of the bars the rows draw.
        void Rows(IEnumerable<StatLine> shown, bool total)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, 1 * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 1 * scale));
            int sum = shown.Sum(line => StatRow(context, detail, line, columns, Label(line)));
            // Armor sums its bars under them, as the game does.
            if (total) StudioUi.StatTotal(columns.Left + columns.Name, columns.Bar, sum);
            ImGui.PopStyleVar(2);
        }
        Rows(lines.Where(line => !Folded(line)), armor);
        if (zero.Length != 0 && ImGui.TreeNodeEx($"Zero Stats ({zero.Length})###zero-stats", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            Rows(zero, false);
            ImGui.TreePop();
        }
        ImGui.BeginDisabled(!context.Workspace.CanEdit);
        if (ImGui.Button("+ Add Stat")) BeginRow(context, false);
        ImGui.EndDisabled();
    }

    /** A stat row: the item's own row, none for a stat only its equipped plugs give, what its other plugs add and its mods'. */
    private readonly record struct StatLine(byte Index, string Name, InvestmentStat? Own, IReadOnlyList<StatSource> Plugs)
    {
        public int Base => ItemStatGroup.Sum(Plugs, false);
        public int Total => (Own?.Value ?? 0) + Base + ItemStatGroup.Sum(Plugs, true);
        public string Tag => Own is { Computed: true } ? "Computed" : Own is { Conditional: true } ? StudioUi.ConditionalBadge : "";
    }

    /** One stat row; returns the bar's value, or zero when the row draws no bar. */
    private int StatRow(StudioPageContext context, InvestmentItemDetail detail, StatLine line, StudioUi.StatColumns columns,
        float label)
    {
        ImGui.PushID(line.Own?.Field.Offset ?? -1 - line.Index);
        ImGui.SetCursorPosX(columns.Left + Math.Max(0, columns.Name - label));
        if (line.Tag.Length != 0) { StudioUi.Badge(line.Tag); ImGui.SameLine(0, 4 * CreationWidgets.Scale); }
        ImGui.AlignTextToFramePadding();
        // The curve maps raw values to what the game shows; zero and computed or conditional rows stay raw.
        var curve = detail.DisplayCurves.GetValueOrDefault(line.Index);
        int Shown(int raw) => curve is not null && raw != 0 && line.Own is not ({ Computed: true } or { Conditional: true })
            ? curve.Evaluate(raw) : raw;
        // A raw value being dragged reads live.
        int own = line.Own is { } row && rawDrag is { } drag && drag.Offset == row.Field.Offset ? drag.Value
            : line.Own?.Value ?? 0;
        int total = line.Total - (line.Own?.Value ?? 0) + own, displayed = Shown(total);
        // Stats the game does not show read dim.
        if (statGroup.Group is { } shown && shown.Position(line.Index) == int.MaxValue)
        { ImGui.TextDisabled(line.Name); StudioUi.Tip("Not shown in game"); }
        else ImGui.TextColored(StudioTheme.Muted, line.Name);
        // The group says which stats the game draws as bars; without one, values up to 100 do.
        var group = statGroup.Group;
        bool bar = (group?.Bar(line.Index) ?? displayed is >= 0 and <= 100) && (total > 0 || group?.Bar(line.Index) == true);
        StudioUi.StatReading(columns.Bar, columns.BarWidth, displayed, Shown(own + line.Base), group?.Full ?? 100, bar,
            ItemStatGroup.Breakdown(own, line.Plugs, total, displayed));
        if (line.Own is { } stat)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(columns.Raw);
            ImGui.SetNextItemWidth(columns.RawWidth);
            int raw = own;
            ImGui.BeginDisabled(!context.Workspace.CanEdit);
            if (ImGui.DragInt("##raw", ref raw)) rawDrag = (stat.Field.Offset, raw);
            ImGui.EndDisabled();
            StudioUi.Tip("Raw value. Zero uses the stat's level curve when one exists.");
            if (ImGui.IsItemDeactivated() && rawDrag is { } done && done.Offset == stat.Field.Offset)
            {
                rawDrag = null;
                if (done.Value != stat.Value)
                    context.Workspace.Run(() => context.Workspace.ApplyInvestmentEdit(stat.Field,
                        done.Value.ToString(CultureInfo.InvariantCulture)));
            }
            ImGui.SameLine();
            if (StudioUi.RowIconButton("remove", StudioIcon.Remove, "Remove stat", context.Workspace.CanEdit))
                context.Workspace.Run(() => context.Workspace.RemoveInvestmentStat(stat));
        }
        ImGui.PopID();
        return bar ? displayed : 0;
    }

    /**
     * One row per socket with what the item comes with, which edits at once or opens the allowed plugs, then the sandbox
     * perks. Randomized shows each rolling socket's perk pool instead.
     */
    private void DrawSockets(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        var workspace = context.Workspace;
        SocketTiles.Heading("The first choice starts equipped. Right-click a choice to make it the default.", false,
            detail.Sockets, ref randomized);
        if (detail.Sockets.Count == 0) ImGui.TextDisabled("No sockets");
        float label = detail.Sockets.Select(socket => ImGui.CalcTextSize(SocketTiles.Name(workspace, catalog, socket)).X)
            .DefaultIfEmpty(0).Max();
        foreach (var socket in detail.Sockets)
        {
            ImGui.PushID(socket.Index);
            string name = SocketTiles.Name(workspace, catalog, socket);
            if (randomized)
            {
                if (socket.RandomizedPlugs.Count != 0)
                    Apply(context, catalog, detail, socket, [.. socket.RandomizedPlugs], SocketTiles.Row(catalog, socket, name,
                        socket.RandomizedPlugs, label, workspace.CanEdit, from => from.Socket == socket.Index, restorable: false,
                        openable: true, pool: true,
                        note: $"Plug Set {socket.RandomizedSet.Index} · {socket.RandomizedSet.Hash:X8}"),
                        lists => workspace.SetInvestmentPool(lists[0].Socket, lists[0].Plugs));
                ImGui.PopID();
                continue;
            }
            // A socket of few choices shows them as tiles that edit at once; a longer one keeps the allowed plug popup.
            var members = Members(context, catalog, detail, socket);
            if (SocketTiles.Arrangeable(members, socket))
            {
                var plugs = SocketTiles.Offered(members, socket, socket.InitialPlug);
                var target = socket;
                bool Accepts((int Socket, ushort Plug) from)
                {
                    if (from.Socket == target.Index) return true;
                    var source = detail.Sockets[from.Socket];
                    var sourceMembers = Members(context, catalog, detail, source);
                    return SocketTiles.Arrangeable(sourceMembers, source)
                        && SocketTiles.Offered(sourceMembers, source, source.InitialPlug).Count > 1
                        && !plugs.Contains(from.Plug) && SocketTiles.Takes(workspace, catalog, target.Type, from.Plug);
                }
                Apply(context, catalog, detail, socket, plugs,
                    SocketTiles.Row(catalog, socket, name, plugs, label, workspace.CanEdit, Accepts, restorable: false,
                        openable: true));
                ImGui.PopID();
                continue;
            }
            SocketTiles.Label(socket, name, label);
            bool open = socket.InitialPlug < catalog.Items.Count;
            float button = ImGui.GetContentRegionAvail().X;
            var at = ImGui.GetCursorScreenPos();
            if (ImGui.Button("##plug", new(button, 0)))
            {
                // An editable socket picks its plug in the plug picker; a read-only one lists what it allows.
                if (workspace.CanEdit) { addingPlug = (socket.Index, AllowedPlug); openAddPlug = true; }
                else { BeginEdit(context, socket.PlugField); plugChoices = socket.AllowedPlugs; }
            }
            ItemPreview.Label(ImGui.GetWindowDrawList(), at, button,
                open ? ItemPreview.Shared?.Item(catalog.Items[socket.InitialPlug]) : null, socket.PlugName);
            if (!open) StudioUi.Tip(name);
            else if (ImGui.IsItemHovered()) AssetTooltip.DrawPlug(catalog.Items[socket.InitialPlug]);
            if (ImGui.BeginPopupContextItem("##plug-menu"))
            {
                if (open && ImGui.MenuItem("Open Plug")) workspace.Select(catalog.Items[socket.InitialPlug].Tag);
                if (ImGui.MenuItem("Edit Raw Value")) { BeginEdit(context, socket.PlugField); plugChoices = socket.AllowedPlugs; }
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        DrawAddPlug(context, catalog, detail);
        if (randomized) return;
        ImGui.Spacing();
        SocketTiles.SandboxHeading("Click a perk to replace it, right-click to open its behavior.");
        float frame = ImGui.GetFrameHeight(), gap = ImGui.GetStyle().ItemSpacing.X;
        foreach (var perk in detail.Perks)
        {
            ImGui.PushID(perk.Field.Offset);
            string perkName = PerkLabel(context, catalog, perk.Index);
            float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - frame;
            float badge = perk.Conditional ? StudioUi.BadgeWidth(StudioUi.ConditionalBadge) + gap : 0;
            float width = right - gap - badge - ImGui.GetCursorPosX();
            var at = ImGui.GetCursorScreenPos();
            if (ImGui.Button("##perk", new(width, 0)))
            {
                if (workspace.CanEdit) BeginReplace(context, perk.Field);
                else BeginEdit(context, perk.Field);
            }
            ItemPreview.Label(ImGui.GetWindowDrawList(), at, width, perkIcons.GetValueOrDefault(perk.Index), perkName);
            if (ImGui.IsItemHovered()) AssetTooltip.DrawPerk(perk.Index, perkName);
            if (ImGui.BeginPopupContextItem("##perk-menu"))
            {
                if (ImGui.MenuItem("Replace Perk", "", false, workspace.CanEdit)) BeginReplace(context, perk.Field);
                if (ImGui.MenuItem("Open Behavior"))
                    workspace.Run(() =>
                        workspace.Select(InvestmentSandboxPerkAuthoring.Read(workspace.Catalog!, perk.Index).GameplayTag));
                if (ImGui.MenuItem("Remove", "", false, workspace.CanEdit))
                    workspace.Run(() => workspace.RemoveInvestmentPerk(perk));
                ImGui.EndPopup();
            }
            if (perk.Conditional) { ImGui.SameLine(); StudioUi.Badge(StudioUi.ConditionalBadge, StudioUi.ConditionalTip); }
            ImGui.SameLine();
            ImGui.SetCursorPosX(right);
            if (StudioUi.RowIconButton("remove", StudioIcon.Remove, "Remove perk", workspace.CanEdit))
                workspace.Run(() => workspace.RemoveInvestmentPerk(perk));
            ImGui.PopID();
        }
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (ImGui.Button("+ Add Perk")) BeginRow(context, true);
        ImGui.EndDisabled();
    }
}
