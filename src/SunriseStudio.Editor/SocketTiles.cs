using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** What a socket row asks for; the caller applies it to its drafts or as a project edit. */
public abstract record SocketAction
{
    /** Opens the plug picker to replace a choice, or with Choice -1 to add one. */
    public sealed record Pick(int Choice) : SocketAction;
    public sealed record Remove(int Choice) : SocketAction;
    /** Puts a plug from this socket or another before a choice; before the first makes it the default. */
    public sealed record Move(int FromSocket, ushort Plug, int Before) : SocketAction;
    public sealed record Restore : SocketAction;
    public sealed record Open(ushort Plug) : SocketAction;
}

/**
 * Socket rows of choice tiles that drag, reorder and remove; the first choice starts equipped. Plug categories and
 * socket type whitelists, which say where a plug may go, are read once per catalog.
 */
public static class SocketTiles
{
    // A socket of up to this many choices shows tiles; a longer one, such as shaders, keeps its dropdown.
    public const int Limit = 40;
    // Tiles aimed at 168 wide, at most three to a line and never under 96, before scaling.
    private const float ChoiceWidth = 168, ChoiceMinimum = 96;
    private const int ChoiceColumns = 3;
    private const string Payload = "socket-plug";
    private static (int Socket, ushort Plug)? dragged;
    private static (PackageCatalog? Catalog, long Revision) source;
    private static readonly Dictionary<ushort, uint?> Categories = [];
    private static readonly Dictionary<ushort, bool> Masterworks = [];
    private static readonly Dictionary<ushort, IReadOnlySet<uint>> Whitelists = [];
    private static readonly Dictionary<ushort, string> CategoryNames = [], TypeLines = [];
    private static readonly ConditionalWeakTable<InvestmentSocket, string> Names = [];
    /** Plug categories by item tag for the picker's filter; read once on the thread pool. */
    private static FrozenDictionary<uint, uint> rowCategories = FrozenDictionary<uint, uint>.Empty;
    private static Task<FrozenDictionary<uint, uint>>? reading;

    /** What was read belongs to one catalog and project overlay; another, or a project edit, starts over. */
    private static void Follow(StudioWorkspace workspace)
    {
        var current = (workspace.Catalog, workspace.Catalog?.OverlayRevision ?? -1);
        if (ReferenceEquals(source.Catalog, current.Catalog) && source.Revision == current.Item2) return;
        source = current;
        Categories.Clear(); Masterworks.Clear(); Whitelists.Clear(); CategoryNames.Clear(); TypeLines.Clear(); Names.Clear();
        // A read still running for the old overlay is never adopted.
        rowCategories = FrozenDictionary<uint, uint>.Empty; reading = null;
    }

    /** The plug category hash of a plug, or null when it has no plug block. */
    public static uint? Category(StudioWorkspace workspace, InvestmentCatalog investment, ushort plug)
    {
        Follow(workspace);
        if (Categories.TryGetValue(plug, out var category)) return category;
        try
        {
            category = plug < investment.Items.Count
                ? InvestmentCatalog.ReadPlugCategory(workspace.Catalog!.Read(investment.Items[plug].Tag)) : null;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { category = null; }
        return Categories[plug] = category;
    }

    /** The plug categories a socket type takes. */
    public static IReadOnlySet<uint> Whitelist(StudioWorkspace workspace, ushort type)
    {
        Follow(workspace);
        if (Whitelists.TryGetValue(type, out var list)) return list;
        try
        {
            list = InvestmentSocketTypes.Whitelist(workspace.Catalog!.Read(InvestmentSocketTypes.GameplayTable), type)
                .ToHashSet();
        }
        catch (InvalidDataException) { list = new HashSet<uint>(); }
        return Whitelists[type] = list;
    }

    /**
     * A socket's name: Masterwork for a masterwork socket type, else the type line most of the plugs its type takes share,
     * such as Scope, Trait or Shader, else Bungie's name for its socket category, else its position. Until every plug's
     * category has read, the socket's own plugs stand in for the ones its type takes, and the name is not kept.
     */
    public static string Name(StudioWorkspace workspace, InvestmentCatalog investment, InvestmentSocket socket)
    {
        if (Names.TryGetValue(socket, out var name)) return name;
        Prepare(workspace);
        bool read = Adopt().Count != 0;
        if (read && !TypeLines.ContainsKey(socket.Type))
            TypeLines[socket.Type] = Commonest(investment, Takers(workspace, investment, socket.Type)) ?? "";
        name = MasterworkType(workspace, socket.Type) ? "Masterwork"
            : (read ? TypeLines[socket.Type] : Commonest(investment, socket.AllowedPlugs.Append(socket.InitialPlug))) is
                { Length: > 0 } line ? line : CategoryName(workspace, investment, socket.Type);
        if (name.Length == 0) name = $"Socket {socket.Index + 1}";
        if (read) Names.AddOrUpdate(socket, name);
        return name;
    }

    private static bool MasterworkType(StudioWorkspace workspace, ushort type)
    {
        Follow(workspace);
        if (Masterworks.TryGetValue(type, out bool masterwork)) return masterwork;
        try { masterwork = InvestmentSocketTypes.Masterwork(workspace.Catalog!.Read(InvestmentSocketTypes.GameplayTable), type); }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { masterwork = false; }
        return Masterworks[type] = masterwork;
    }

    /** The type line most of these plugs share; null when none has one. */
    private static string? Commonest(InvestmentCatalog investment, IEnumerable<ushort> plugs) => plugs
        .Where(plug => plug < investment.Items.Count).Select(plug => investment.Items[plug].TypeName.Trim())
        .Where(type => type.Length != 0).GroupBy(type => type).OrderByDescending(group => group.Count())
        .Select(group => group.Key).FirstOrDefault();

    /** Every plug item whose category the socket type's whitelist names. */
    internal static IEnumerable<ushort> Takers(StudioWorkspace workspace, InvestmentCatalog investment, ushort type)
    {
        var whitelist = Whitelist(workspace, type);
        return rowCategories.Where(pair => whitelist.Contains(pair.Value))
            .Select(pair => investment.FindItem(pair.Key)).OfType<InvestmentItem>().Select(item => (ushort)item.Index);
    }

    /** The socket type's category name in title case, as the game writes it in capitals; empty when it has none. */
    private static string CategoryName(StudioWorkspace workspace, InvestmentCatalog investment, ushort type)
    {
        Follow(workspace);
        if (CategoryNames.TryGetValue(type, out var name)) return name;
        try
        {
            name = InvestmentSocketTypes.CategoryIndex(workspace.Catalog!.Read(InvestmentSocketTypes.GameplayTable), type)
                is { } category ? investment.SocketCategoryName(category) : "";
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { name = ""; }
        return CategoryNames[type] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
    }

    /** True when a socket of this type takes the plug. */
    public static bool Takes(StudioWorkspace workspace, InvestmentCatalog investment, ushort type, ushort plug) =>
        Category(workspace, investment, plug) is { } category && Whitelist(workspace, type).Contains(category);

    private static (byte[]? Bytes, Dictionary<int, IReadOnlyList<ushort>> Rows) members = (null, []);

    /** The plugs a socket embeds in its stored order, read once per definition bytes; empty when it embeds none. */
    public static IReadOnlyList<ushort> Members(InvestmentCatalog investment, uint tag, byte[] bytes, InvestmentSocket socket)
    {
        if (!ReferenceEquals(members.Bytes, bytes)) members = (bytes, []);
        if (members.Rows.TryGetValue(socket.Index, out var plugs)) return plugs;
        try { plugs = [.. investment.SocketPlugTemplates(tag, bytes, socket).Where(plug => plug < investment.Items.Count)]; }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { plugs = []; }
        return members.Rows[socket.Index] = plugs;
    }

    /** The socket's name right-aligned in a column of this width, its number and type on hover; the row goes on after it. */
    public static void Label(InvestmentSocket socket, string name, float width)
    {
        float x = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(x + width - ImGui.CalcTextSize(name).X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(StudioTheme.Muted, name);
        StudioUi.Tip($"Socket {socket.Index + 1}  /  Type {socket.Type}");
        ImGui.SameLine();
        ImGui.SetCursorPosX(x + width + ImGui.GetStyle().ItemSpacing.X);
    }

    /**
     * What the item comes with: the equipped plug, its embedded members in their stored order, then its reusable plug
     * set. The randomized set's pool is not among them. A socket that starts empty stores ushort.MaxValue, which is no plug.
     */
    public static List<ushort> Offered(IReadOnlyList<ushort> members, InvestmentSocket socket, ushort equipped) =>
        [.. members.Prepend(equipped).Where(plug => plug != ushort.MaxValue).Concat(socket.ReusablePlugs).Distinct()];

    /** True when the socket embeds a choice to copy and offers few enough choices to arrange. */
    public static bool Arrangeable(IReadOnlyList<ushort> members, InvestmentSocket socket) =>
        members.Count != 0 && socket.ReusablePlugs.Count <= Limit;

    private static readonly CreationChoice[] Pools =
    [
        new(0, "Reusable", "What the item comes with"),
        new(1, "Randomized", "The perk pool its rolls pick from. A new choice joins it.")
    ];

    /** The Perks & Sockets heading; when a socket rolls, a switch between what the item comes with and its perk pools. */
    public static void Heading(string hint, bool changed, IReadOnlyList<InvestmentSocket> sockets, ref bool randomized)
    {
        StudioUi.Title("Perks & Sockets", hint, changed);
        if (sockets.All(socket => socket.RandomizedPlugs.Count == 0)) { randomized = false; return; }
        var style = ImGui.GetStyle();
        float width = Pools.Max(pool => ImGui.CalcTextSize(pool.Label).X + style.FramePadding.X * 2) * Pools.Length
            + style.ItemSpacing.X * .5f;
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - width));
        uint view = randomized ? 1u : 0u;
        if (CreationWidgets.Buttons("##pools", ref view, Pools, width)) randomized = view == 1;
    }

    /**
     * Draws one socket row.
     * @param label The width of the name column, so every row's tiles line up.
     * @param accepts Whether this socket takes a dragged plug, from another socket or its own.
     * @param restorable Offers Restore Socket in a choice's menu.
     * @param openable Offers Open Plug in a choice's menu.
     * @param note Text on the name's line, over the choices, such as a pool's plug set.
     * @param pool Draws a randomized pool in roll order, which has no default choice.
     * @return What the row asks for, once.
     */
    public static unsafe SocketAction? Row(InvestmentCatalog investment, InvestmentSocket socket, string name,
        IReadOnlyList<ushort> plugs, float label, bool editable, Func<(int Socket, ushort Plug), bool> accepts, bool restorable,
        bool openable, bool pool = false, string note = "")
    {
        var style = ImGui.GetStyle();
        float scale = CreationWidgets.Scale, frame = ImGui.GetFrameHeight(), gap = style.ItemSpacing.X;
        Label(socket, name, label);
        // The add action keeps a fixed column, so every row's choices line up.
        float add = ImGui.CalcTextSize("+ Add Choice").X + style.FramePadding.X * 2, start = ImGui.GetCursorPosX();
        if (note.Length != 0)
        {
            ImGui.TextDisabled(note);
            ImGui.SetCursorPosX(start);
        }
        float area = ImGui.GetContentRegionAvail().X - add - gap;
        int columns = Math.Clamp((int)(area / (ChoiceWidth * scale)), 1, Math.Min(ChoiceColumns, Math.Max(1, plugs.Count)));
        float width = MathF.Floor(Math.Max(ChoiceMinimum * scale, (area - gap * (columns - 1)) / columns));
        float grip = frame * .5f;
        SocketAction? action = null;
        var draw = ImGui.GetWindowDrawList();
        ImGui.BeginGroup();
        for (int i = 0; i < plugs.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, gap);
            ImGui.PushID(i);
            ushort plug = plugs[i];
            string text = investment.ItemName(plug);
            var at = ImGui.GetCursorScreenPos();
            // One continuous tile; its button holds the grip, icon and name and drags, and removal sits at its edge.
            draw.AddRectFilled(at, at + new Vector2(width, frame), ImGui.GetColorU32(ImGuiCol.FrameBg), style.FrameRounding);
            bool removable = editable && (pool ? plugs.Count > 1 : i > 0);
            // Only a row that can change draws grips, so a read-only one does not look draggable.
            float button = width - (removable ? frame : 0), face = editable ? grip : 0;
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            if (ImGui.Button("##choice", new(button, frame)) && editable) action = new SocketAction.Pick(i);
            ImGui.PopStyleColor();
            bool held = editable && (ImGui.IsItemHovered() || ImGui.IsItemActive());
            if (editable && ImGui.BeginDragDropSource())
            {
                dragged = (socket.Index, plug);
                ImGui.SetDragDropPayload(Payload, IntPtr.Zero, 0);
                ImGui.TextUnformatted(text);
                ImGui.EndDragDropSource();
            }
            if (editable) DrawGrip(draw, at, grip, frame, ImGui.GetColorU32(held ? ImGuiCol.Text : ImGuiCol.TextDisabled));
            ItemPreview.Label(draw, at + new Vector2(face, 0), button - face, ItemPreview.Shared?.Item(investment.Items[plug]),
                text);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                AssetTooltip.DrawPlug(investment.Items[plug]);
            if (ImGui.BeginPopupContextItem("##choice-menu"))
            {
                if (!pool && ImGui.MenuItem("Make Default", "", false, editable && i > 0))
                    action = new SocketAction.Move(socket.Index, plug, 0);
                if (ImGui.MenuItem("Remove", "", false, editable && plugs.Count > 1)) action = new SocketAction.Remove(i);
                if (openable && ImGui.MenuItem("Open Plug")) action = new SocketAction.Open(plug);
                if (restorable)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Restore Socket")) action = new SocketAction.Restore();
                }
                ImGui.EndPopup();
            }
            if (removable)
            {
                ImGui.SameLine(0, 0);
                if (StudioUi.RowIconButton("remove", StudioIcon.Remove, "Remove this choice"))
                    action = new SocketAction.Remove(i);
            }
            // A drop on a tile lands the plug in its place.
            ImGui.SetCursorScreenPos(at);
            ImGui.Dummy(new(width, frame));
            if (editable && ImGui.BeginDragDropTarget())
            {
                if (dragged is { } from && accepts(from) && ImGui.AcceptDragDropPayload(Payload).NativePtr != null)
                    action = new SocketAction.Move(from.Socket, from.Plug, i);
                ImGui.EndDragDropTarget();
            }
            ImGui.PopID();
        }
        ImGui.EndGroup();
        var end = ImGui.GetItemRectMax();
        ImGui.SameLine();
        ImGui.SetCursorPosX(start + area + gap);
        ImGui.BeginDisabled(!editable);
        if (ImGui.Button("+ Add Choice")) action = new SocketAction.Pick(-1);
        ImGui.EndDisabled();
        StudioUi.Tip($"Add a plug to {name}");
        float bottom = Math.Max(end.Y, ImGui.GetItemRectMax().Y) + style.ItemSpacing.Y;
        ImGui.SetCursorScreenPos(new(ImGui.GetCursorScreenPos().X, bottom));
        ImGui.Dummy(Vector2.Zero);
        return action;
    }

    /** The sandbox perks' heading: perks the item carries itself, beside the ones its sockets' plugs give. */
    public static void SandboxHeading(string hint = "", bool changed = false)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(changed ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : StudioTheme.Muted, "Sandbox Perks");
        StudioUi.Tip(string.Join("\n", new[] { "Perks the item carries itself, active whatever its sockets hold.", hint }
            .Where(text => text.Length != 0)));
    }

    /** Six dots in two columns, the grip a choice drags by. */
    private static void DrawGrip(ImDrawListPtr draw, Vector2 at, float width, float height, uint color)
    {
        float radius = Math.Max(1, height * .055f), step = height * .2f;
        var middle = at + new Vector2(width * .5f, height * .5f);
        for (int row = -1; row <= 1; row++)
            for (int column = 0; column < 2; column++)
                draw.AddCircleFilled(middle + new Vector2((column - .5f) * step, row * step), radius, color);
    }

    /** Takes the plug categories once their read finishes; empty until then. */
    internal static FrozenDictionary<uint, uint> Adopt()
    {
        if (reading is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) rowCategories = done.Result;
            reading = null;
        }
        return rowCategories;
    }

    /** Starts reading every plug and shader row's category on the thread pool, for the picker and socket names. */
    public static void Prepare(StudioWorkspace workspace)
    {
        Follow(workspace);
        if (rowCategories.Count != 0 || reading is not null || workspace.Catalog is not { } packages
            || workspace.Investment is not { } investment) return;
        var rows = workspace.CategoryRowsOf(InvestmentItemKinds.Mods)
            .Concat(workspace.CategoryRowsOf(InvestmentItemKinds.Shaders)).ToArray();
        reading = Task.Run(() =>
        {
            var map = new Dictionary<uint, uint>();
            foreach (var row in rows)
                try
                {
                    if (investment.FindItem(row.Entry.TagHash) is { } item
                        && InvestmentCatalog.ReadPlugCategory(packages.Read(item.Tag)) is { } category)
                        map[row.Entry.TagHash] = category;
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException) { }
            return map.ToFrozenDictionary();
        });
    }
}
