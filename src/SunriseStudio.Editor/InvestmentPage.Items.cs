using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * An item reads as its card and its fields in name and value pairs, then stats beside perks and sockets. Values open
 * the edit popup.
 */
public sealed partial class InvestmentPage
{
    /** What the open item's equipped plugs add to each stat, read once per detail. */
    private (InvestmentItemDetail? Detail, Dictionary<byte, List<StatSource>> Plugs) plugStats = (null, []);
    /** The raw stat value being dragged, by field offset; the project takes it when the drag ends. */
    private (int Offset, int Value)? rawDrag;
    private (InvestmentItemDetail? Detail, string? Damage, string? Ammo) profile;
    private const string AddPlugId = "Add plug###item-add-plug";
    private (int Socket, int Choice)? addingPlug;
    // A choice of this sets a dropdown socket's plug from the plugs it allows.
    private const int AllowedPlug = -2;
    // Narrower than this, before scaling, stats sit above sockets instead of beside them.
    private const float SideBySideWidth = 760;
    // The share of a side by side line the stats take; sockets take the rest.
    private const float StatsShare = .45f;
    // Layout before scaling: the item spacing and frame padding of the item's rows, the row gap and frame padding of its
    // stat rows, and the gap after a stat's badge.
    private static readonly Vector2 RowSpacing = new(8, 4), RowPadding = new(6, 3);
    private const float StatRowGap = 1, BadgeGap = 4;
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
        var icons = InvestmentIconCache.Of(context);
        string name = SocketTiles.Name(context.Workspace, catalog, socket);
        if (adding.Choice == AllowedPlug)
        {
            ushort[] current = socket.InitialPlug < catalog.Items.Count ? [socket.InitialPlug] : [];
            if (PlugPicker.Pick(context.Workspace, catalog, icons, AddPlugId, detail.Item, socket, name, current,
                    current.Length - 1, socket.AllowedPlugs) is { } allowed)
                context.Workspace.Run(() => context.Workspace.ApplyInvestmentEdit(socket.PlugField,
                    allowed.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        // The Randomized view adds to the socket's pool; the other to what the item comes with.
        List<ushort> plugs = randomized ? [.. socket.RandomizedPlugs]
            : SocketTiles.Offered(Members(context, catalog, detail, socket), socket, socket.InitialPlug);
        if (PlugPicker.Pick(context.Workspace, catalog, icons, AddPlugId, detail.Item, socket, name, plugs, adding.Choice)
            is not { } plug) return;
        if (adding.Choice >= 0 && adding.Choice < plugs.Count) plugs[adding.Choice] = plug;
        else plugs.Add(plug);
        context.Workspace.Run(() =>
        {
            if (randomized) context.Workspace.SetInvestmentPool(socket.Index, plugs);
            else context.Workspace.SetInvestmentSockets(
                [(socket.Index, [.. plugs.Select(value => new InvestmentSocketChoice(value))])]);
        });
    }

    /** Icon, name, fields, stats, sockets and perks of one item; values open the edit popup. */
    private void DrawItem(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        var item = detail.Item;
        var workspace = context.Workspace;
        bool weapon = item.Category == InvestmentCategory.Weapons;
        float scale = CreationWidgets.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, RowSpacing * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, RowPadding * scale);
        // The item's card: its icon, name, hash and type, with its display record as its action.
        if (item.DisplayTag != 0)
        {
            if (StudioUi.ItemCard(item, icon, "Display Properties")) workspace.Select(item.DisplayTag);
        }
        else
        {
            ImGui.TextColored(StudioTheme.Rarity(item.Tier), item.Name);
            StudioUi.Tip($"Item {item.Index}\n{item.Hash:X8}");
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{item.Rarity}  {item.TypeName}");
        StudioLiveLink.Draw(context, StudioLiveLink.ItemTarget(item));
        DrawHeaderNotice(context, detail);
        if (item.Description.Length != 0) ImGui.TextWrapped(item.Description);
        DrawPursuitRow(context, detail);
        ImGui.Spacing();
        DrawObjectives(context, catalog, detail);
        var pairs = new StudioUi.Pairs();
        var (damage, ammo) = weapon ? Profile(context, catalog, detail) : (null, null);
        // Slot, damage, ammo and rarity first, then the other fields.
        int[] order = [ItemFields.Bucket, ItemFields.Tier, ItemFields.MaximumStack, ItemFields.Instanced];
        foreach (var field in detail.Fields.OrderBy(field =>
            Array.IndexOf(order, field.Offset) is var at and >= 0 ? at : order.Length))
        {
            // Damage and ammo read from the weapon; Clone and Edit change them.
            if (weapon && field.Offset == ItemFields.Tier)
            {
                pairs.Fixed("Damage Type", damage, "Clone or Edit changes it.");
                pairs.Fixed("Ammo Type", ammo, "Clone or Edit changes it.");
            }
            string value = field.Value, hint = "";
            if (field.Offset == ItemFields.Bucket)
            {
                hint = $"Inventory bucket {field.Value}";
                if (catalog.BucketName(item.Bucket) is { Length: > 0 } bucket) value = bucket;
            }
            else if (field.Offset == ItemFields.Tier) value = item.Rarity;
            else if (field.Offset == ItemFields.Instanced) value = field.Value == "0" ? "No" : "Yes";
            if (pairs.Value(field.Name, value, field.Editable, hint)) BeginEdit(context, field);
        }
        DrawBucket(context, item);
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.GetContentRegionAvail().X >= SideBySideWidth * scale
            && ImGui.BeginTable("##item-contents", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##stats", ImGuiTableColumnFlags.WidthStretch, StatsShare);
            ImGui.TableSetupColumn("##sockets", ImGuiTableColumnFlags.WidthStretch, 1 - StatsShare);
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
        Report("Read errors", detail.Diagnostics);
        string error = iconError.Length != 0 ? iconError : workspace.InvestmentIconError;
        Report("Icon errors", error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ImGui.PopStyleVar(2);
    }

    /** Each stat as a row: name, bar, the value the game shows with the equipped plugs, then the raw value and Remove. */
    private void DrawStats(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        StudioUi.Title(detail.Item.Category switch
        {
            InvestmentCategory.Weapons => "Weapon Stats", InvestmentCategory.Armor => "Armor Stats", _ => "Stats"
        }, "Each row reads the value the game shows with the equipped plugs, then the raw value saved to the item.");
        if (!ReferenceEquals(plugStats.Detail, detail) && context.Workspace.Catalog is { } packages)
            plugStats = (detail,
                StatSources.Read(catalog, packages, detail.Sockets.Select(socket => (socket.InitialPlug, socket.Type))));
        var plugs = plugStats.Plugs;
        var group = StatSources.Group(detail);
        bool armor = detail.Item.Category == InvestmentCategory.Armor;
        var extra = plugs.Keys.Concat(armor && group is not null ? group.Bars() : [])
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
            + (line.Tag.Length == 0 ? 0 : StudioUi.BadgeWidth(line.Tag) + BadgeGap * scale);
        var columns = StudioUi.StatColumns.Here(lines.Select(Label).DefaultIfEmpty(0).Max());
        // Zero rows the game does not show, such as Attack and Power, fold away.
        bool Folded(StatLine line) => line.Total == 0 && (group is null || !group.Shows(line.Index));
        var zero = lines.Where(Folded).ToArray();
        // The sum is of the bars the rows draw.
        void Rows(IEnumerable<StatLine> shown, bool total)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, StatRowGap * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, StatRowGap * scale));
            int sum = shown.Sum(line => StatRow(context, group, line, columns, Label(line)));
            // Armor sums its bars under them, as the game does.
            if (total) StudioUi.StatTotal(columns.Left + columns.Name, columns.Bar, sum);
            ImGui.PopStyleVar(2);
        }
        Rows(lines.Where(line => !Folded(line)), armor);
        if (zero.Length != 0 && ImGui.TreeNodeEx($"Zero stats ({zero.Length})###zero-stats", ImGuiTreeNodeFlags.SpanAvailWidth))
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
        public int Base => StatSources.Sum(Plugs, false);
        public int Total => (Own?.Value ?? 0) + Base + StatSources.Sum(Plugs, true);
        public string Tag => Own is { Computed: true } ? "Computed"
            : Own is { Conditional: true } ? StudioUi.ConditionalBadge : "";
    }

    /**
     * One stat row.
     * @param group The item's display group, or null when it lists no stats.
     * @return The bar's value, or zero when the row draws no bar.
     */
    private int StatRow(StudioPageContext context, InvestmentStatGroup? group, StatLine line, StudioUi.StatColumns columns,
        float label)
    {
        ImGui.PushID(line.Own?.Field.Offset ?? -1 - line.Index);
        ImGui.SetCursorPosX(columns.Left + Math.Max(0, columns.Name - label));
        if (line.Tag.Length != 0) { StudioUi.Badge(line.Tag); ImGui.SameLine(0, BadgeGap * CreationWidgets.Scale); }
        ImGui.AlignTextToFramePadding();
        // The group's row maps raw values to what the game shows; zero and computed or conditional rows stay raw.
        var listed = group?.Row(line.Index);
        int Shown(int raw) => listed is { } stat && raw != 0 && line.Own is not ({ Computed: true } or { Conditional: true })
            ? stat.Shown(raw) : raw;
        // A raw value being dragged reads live.
        int own = line.Own is { } row && rawDrag is { } drag && drag.Offset == row.Field.Offset ? drag.Value
            : line.Own?.Value ?? 0;
        int total = line.Total - (line.Own?.Value ?? 0) + own, displayed = Shown(total);
        // Stats the game does not show read dim.
        if (group is not null && listed is null) { ImGui.TextDisabled(line.Name); StudioUi.Tip("Not shown in game"); }
        else ImGui.TextColored(StudioTheme.Muted, line.Name);
        // The group says which stats the game draws as bars; without one, values up to a full bar do.
        int full = StatSources.Full(group);
        bool bar = (group?.Bar(line.Index) ?? displayed >= 0 && displayed <= full)
            && (total > 0 || group?.Bar(line.Index) == true);
        StudioUi.StatReading(columns.Bar, columns.BarWidth, displayed, Shown(own + line.Base), full, bar,
            StatSources.Breakdown(own, line.Plugs, total, displayed));
        if (line.Own is { } saved)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(columns.Raw);
            ImGui.SetNextItemWidth(columns.RawWidth);
            int raw = own;
            ImGui.BeginDisabled(!context.Workspace.CanEdit);
            if (ImGui.DragInt("##raw", ref raw)) rawDrag = (saved.Field.Offset, raw);
            ImGui.EndDisabled();
            StudioUi.Tip("Raw value. Zero uses the stat's level curve when one exists.");
            if (ImGui.IsItemDeactivated() && rawDrag is { } done && done.Offset == saved.Field.Offset)
            {
                rawDrag = null;
                if (done.Value != saved.Value)
                    context.Workspace.Run(() => context.Workspace.ApplyInvestmentEdit(saved.Field,
                        done.Value.ToString(CultureInfo.InvariantCulture)));
            }
            ImGui.SameLine();
            if (StudioUi.RowIconButton("remove", StudioIcon.Remove, "Remove stat", context.Workspace.CanEdit))
                context.Workspace.Run(() => context.Workspace.RemoveInvestmentStat(saved));
        }
        ImGui.PopID();
        return bar ? displayed : 0;
    }

    /** Opens the field popup on a socket's plug with the socket's allowed plugs to pick from. */
    private void BeginPlugEdit(StudioPageContext context, InvestmentSocket socket)
    {
        BeginEdit(context, socket.PlugField);
        plugOptions = PlugOptions(socket);
        plugPicker.Chosen = socket.InitialPlug;
        plugPicker.Reset();
    }

    /**
     * One row per socket with what the item comes with, which edits at once or opens the allowed plugs, then the sandbox
     * perks. Randomized shows each rolling socket's perk pool instead.
     */
    private void DrawSockets(StudioPageContext context, InvestmentCatalog catalog, InvestmentItemDetail detail)
    {
        var workspace = context.Workspace;
        var icons = InvestmentIconCache.Of(context);
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
                    Apply(context, catalog, detail, socket, [.. socket.RandomizedPlugs], SocketTiles.Row(catalog, icons, socket,
                        name, socket.RandomizedPlugs, label, workspace.CanEdit, from => from.Socket == socket.Index,
                        restorable: false, openable: true, pool: true,
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
                    SocketTiles.Row(catalog, icons, socket, name, plugs, label, workspace.CanEdit, Accepts, restorable: false,
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
                else BeginPlugEdit(context, socket);
            }
            StudioUi.IconLabel(ImGui.GetWindowDrawList(), at, button, open ? icons.Get(catalog, socket.InitialPlug) : null,
                socket.PlugName);
            if (!open) StudioUi.Tip(name);
            else if (ImGui.IsItemHovered())
            {
                var plug = catalog.Items[socket.InitialPlug];
                AssetTooltip.Draw(plug.Tag, plug.Name, plug.TypeName);
            }
            if (ImGui.BeginPopupContextItem("##plug-menu"))
            {
                if (open && ImGui.MenuItem("Open Plug")) workspace.Select(catalog.Items[socket.InitialPlug].Tag);
                if (ImGui.MenuItem("Edit Raw Value")) BeginPlugEdit(context, socket);
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
            StudioUi.IconLabel(ImGui.GetWindowDrawList(), at, width, perkIcons.GetValueOrDefault(perk.Index), perkName);
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
